using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using APISwitch.Dialogs;
using APISwitch.Models;
using APISwitch.Services;

namespace APISwitch;

public partial class MainWindow : Window
{
    List<Profile> _profiles = new();
    List<ClaudeProvider> _claudeProviders = new();
    List<ClaudeProvider> _desktopProviders = new();
    List<CodexProvider> _codexProviders = new();
    List<PiAccount> _piAccounts = new();
    List<OpenCodeProvider> _openCodeProviders = new();

    public MainWindow()
    {
        InitializeComponent();
        var ver = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        if (ver != null && AppVersionText != null)
        {
            AppVersionText.Text = $"v{ver.Major}.{ver.Minor}.{ver.Build}";
        }
        Loaded += (_, _) => RefreshAll();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var wa = SystemParameters.WorkArea;
        if (Width > wa.Width) Width = wa.Width;
        if (Height > wa.Height) Height = wa.Height;
        if (Left < wa.Left) Left = wa.Left;
        if (Top < wa.Top) Top = wa.Top;
        if (Left + Width > wa.Right) Left = wa.Right - Width;
        if (Top + Height > wa.Bottom) Top = wa.Bottom - Height;
    }

    void RefreshAll()
    {
        RefreshAntigravity();
        RefreshCodex();
        RefreshClaude();
        RefreshDesktop();
        RefreshOpencode();
        RefreshPi();
    }

    void OnTabChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        switch (Tabs!.SelectedIndex)
        {
            case 0: RefreshAntigravity(); break;
            case 1: RefreshCodex(); break;
            case 2: RefreshClaude(); break;
            case 3: RefreshDesktop(); break;
            case 4: RefreshOpencode(); break;
            case 5: RefreshPi(); break;
        }
    }

    // ---------- Antigravity ----------

    void RefreshAntigravity()
    {
        var db = AgPaths.FindStateDb();
        DbPathText.Text = db ?? "未找到 Antigravity 数据目录（请先安装并至少启动一次 Antigravity）";

        string? email = null, plan = null, state = null;

        // 1. Try detecting active email from ~/.gemini/oauth_creds.json
        if (File.Exists(AgToolsService.GeminiCredsPath))
        {
            try
            {
                var credsText = File.ReadAllText(AgToolsService.GeminiCredsPath);
                using var doc = JsonDocument.Parse(credsText);
                if (doc.RootElement.TryGetProperty("id_token", out var idElem))
                {
                    var idToken = idElem.GetString();
                    if (!string.IsNullOrEmpty(idToken))
                    {
                        var parts = idToken.Split('.');
                        if (parts.Length >= 2)
                        {
                            var payload = parts[1].Replace('-', '+').Replace('_', '/');
                            switch (payload.Length % 4) { case 2: payload += "=="; break; case 3: payload += "="; break; }
                            using var pDoc = JsonDocument.Parse(Convert.FromBase64String(payload));
                            if (pDoc.RootElement.TryGetProperty("email", out var emElem))
                            {
                                email = emElem.GetString();
                                state = "signedIn";
                            }
                        }
                    }
                }
            }
            catch { }
        }

        // 2. Fallback to state.vscdb
        if (db != null)
        {
            try
            {
                var auth = AgDb.ReadAuth(db);
                auth.TryGetValue(AgState.KeyUserStatus, out var us);
                auth.TryGetValue(AgState.KeyOauthToken, out var ot);
                var dbEmail = AgState.ExtractEmail(us);
                if (string.IsNullOrEmpty(email))
                {
                    email = dbEmail;
                    state = AgState.ExtractAuthState(ot, email);
                }
                plan = AgState.ExtractPlan(us);
            }
            catch (Exception ex)
            {
                if (email == null) DbPathText.Text = db + "  （读取失败: " + ex.Message + "）";
            }
        }

        CurrentAccountText.Text = email ?? "未登录 / 未检测到";
        StateChip.Text = state switch
        {
            "signedIn" => "已登录",
            "signedOut" => "已登出",
            null => "状态未知",
            _ => state,
        };
        PlanChip.Text = string.IsNullOrEmpty(plan) ? "—" : plan;

        var running = AgProcess.IsRunning();
        IdeChip.Text = running ? "IDE 运行中" : "IDE 未运行";
        IdeChip.Foreground = running ? System.Windows.Media.Brushes.LightGreen : null;

        _profiles = ProfileStore.Load();

        // If no profiles loaded yet and Antigravity Tools is available, auto import
        if (_profiles.Count == 0 && AgToolsService.IsInstalled())
        {
            try
            {
                _ = Task.Run(async () =>
                {
                    var imported = await AgToolsService.ImportAccountsAsync();
                    if (imported.Count > 0)
                    {
                        Dispatcher.Invoke(() => RefreshAntigravity());
                    }
                });
            }
            catch { }
        }

        foreach (var p in _profiles) p.IsCurrent = email != null && email.Equals(p.Email, StringComparison.OrdinalIgnoreCase);
        ProfileList.ItemsSource = null;
        ProfileList.ItemsSource = _profiles;
        AgEmpty.Visibility = _profiles.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    async void OnImportFromAgTools(object sender, RoutedEventArgs e)
    {
        SetBusy("正在从 Antigravity Tools 导入账号及配额…");
        try
        {
            var imported = await AgToolsService.ImportAccountsAsync();
            ClearBusy();
            RefreshAntigravity();
            if (imported.Count > 0)
                ShowToast($"已成功导入 {imported.Count} 个账号与最新配额");
            else
                ShowToast("未在 Antigravity Tools 中找到可用账号", isError: true);
        }
        catch (Exception ex)
        {
            ClearBusy();
            ShowToast("导入异常: " + ex.Message, isError: true);
        }
    }

    async void OnNewAccountLogin(object sender, RoutedEventArgs e)
    {
        SetBusy("正在打开浏览器进行 Google 授权…");
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            var profile = await AgAuthFlow.StartGoogleLoginAsync(cts.Token);
            ClearBusy();
            RefreshAntigravity();
            ShowToast($"Google 账号 {profile.Email} 授权成功并已存档！");
        }
        catch (OperationCanceledException)
        {
            ClearBusy();
            ShowToast("已取消登录或授权超时", isError: true);
        }
        catch (Exception ex)
        {
            ClearBusy();
            ShowToast("登录失败: " + ex.Message, isError: true);
        }
    }

    async void OnCardActivateAntigravity(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not Profile target) return;
        SetBusy($"正在对 {target.Email} 执行官方握手激活…");
        try
        {
            var (ok, latencyMs, msg) = await AgQuotaService.ActivateProfileAsync(target);
            ClearBusy();
            RefreshAntigravity();
            if (ok)
            {
                ShowToast($"⚡ {target.Email} 握手激活成功 (响应 {latencyMs}ms)");
            }
            else
            {
                ShowToast($"激活失败: {msg}", isError: true);
            }
        }
        catch (Exception ex)
        {
            ClearBusy();
            ShowToast("激活异常: " + ex.Message, isError: true);
        }
    }

    async void OnBatchActivateAntigravity(object sender, RoutedEventArgs e)
    {
        if (_profiles.Count == 0)
        {
            ShowToast("当前没有账号存档可供激活", isError: true);
            return;
        }

        var count = _profiles.Count;
        int successCount = 0;
        int failCount = 0;

        for (int i = 0; i < _profiles.Count; i++)
        {
            var p = _profiles[i];
            SetBusy($"正在批量激活 ({i + 1}/{count}): {p.Email}…");

            try
            {
                var (ok, latencyMs, msg) = await AgQuotaService.ActivateProfileAsync(p);
                if (ok) successCount++;
                else failCount++;
            }
            catch (Exception ex)
            {
                failCount++;
                p.IsActivated = false;
                p.ActivationError = ex.Message;
                ProfileStore.Save(p);
            }

            RefreshAntigravity();

            if (i < _profiles.Count - 1)
            {
                var delay = Random.Shared.Next(1500, 3000);
                SetBusy($"已完成 ({i + 1}/{count})，安全冷却中 {delay / 1000.0:F1}s…");
                await Task.Delay(delay);
            }
        }

        ClearBusy();
        RefreshAntigravity();
        ShowToast($"⚡ 批量激活完成！成功 {successCount} 个，失败 {failCount} 个");
    }

    async void OnCardRefreshQuota(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not Profile target) return;
        SetBusy($"正在刷新 {target.Email} 的实时配额…");
        try
        {
            var ok = await AgQuotaService.RefreshQuotaAsync(target);
            ClearBusy();
            RefreshAntigravity();
            if (ok)
            {
                if (target.Has5hQuota)
                    ShowToast($"已更新 {target.Email} 的 5H/周配额数据");
                else
                    ShowToast($"已更新 {target.Email} 的周配额数据 (无5H限制)");
            }
            else
            {
                ShowToast($"获取配额失败，请确认网络连接正常", isError: true);
            }
        }
        catch (Exception ex)
        {
            ClearBusy();
            ShowToast("配额刷新失败: " + ex.Message, isError: true);
        }
    }

    void OnRowSwitchAntigravity(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is Profile p) _ = SwitchToProfile(p);
    }

    async Task SwitchToProfile(Profile target)
    {
        SetBusy($"正在切换到 {target.Email}…");
        try
        {
            var stopped = await Task.Run(AgProcess.StopIdeAsync);
            if (!stopped)
            {
                ClearBusy();
                ShowToast("无法自动关闭 Antigravity，请手动退出后重试", isError: true);
                return;
            }

            // 1. Sync credentials to system (oauth_creds.json, Credential Manager, Antigravity Tools)
            await AgToolsService.ApplySystemCredentialsAsync(target);

            // 2. Write state.vscdb if available
            var db = AgPaths.FindStateDb();
            if (db != null && target.Values != null && target.Values.Count > 0)
            {
                await Task.Run(() => AgDb.WriteAuth(db, target.Values));
            }
        }
        catch (Exception ex)
        {
            ClearBusy();
            ShowToast("切换失败：" + ex.Message, isError: true);
            return;
        }

        if (AutoRestartCheck.IsChecked == true)
        {
            try { AgProcess.StartIde(); }
            catch (Exception ex) { ClearBusy(); ShowToast("已切换凭据，但重启 IDE 失败：" + ex.Message, isError: true); return; }
        }

        ClearBusy();
        RefreshAntigravity();
        ShowToast($"已切换到 {target.Email}" + (AutoRestartCheck.IsChecked == true ? "（已重启 IDE）" : ""));
    }

    void OnCardDeleteAntigravity(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not Profile target) return;
        if (!Confirm($"删除账号存档 {target.Email}？\n（仅删除本地存档，不影响 Google 账号本身）")) return;
        try { ProfileStore.Delete(target); }
        catch (Exception ex) { Warn("删除失败：" + ex.Message); return; }
        RefreshAntigravity();
    }

    void OnRefresh(object sender, RoutedEventArgs e) => RefreshAntigravity();

    void OnLaunchIde(object sender, RoutedEventArgs e)
    {
        try { AgProcess.StartIde(); }
        catch (Exception ex) { Warn("启动失败：" + ex.Message); }
    }

    void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        Process.Start(new ProcessStartInfo(AgPaths.ProfilesDir) { UseShellExecute = true });
    }

    // ---------- Shared row types ----------

    public class ClaudeRow
    {
        public ClaudeProvider P { get; init; } = null!;
        public bool IsCurrent { get; init; }
        public string Name => P.Name;
        public string Initial => Name.Length > 0 ? Name.Substring(0, 1).ToUpperInvariant() : "?";
        public string BaseUrl => P.IsOfficial ? "官方登录（无自定义端点）" : (string.IsNullOrEmpty(P.BaseUrl) ? "—" : P.BaseUrl!);
        public string Model => string.IsNullOrEmpty(P.Model) ? "—" : P.Model!;
        public string SubText => P.IsOfficial ? BaseUrl : string.Join("   ·   ",
            new[] { BaseUrl, string.IsNullOrEmpty(P.Model) ? null : "模型 " + P.Model }.Where(s => s != null));

        public string Status => IsCurrent ? "● 当前" : "";
    }

    public class CodexRow
    {
        public CodexProvider P { get; init; } = null!;
        public bool IsCurrent { get; init; }
        public string Name => P.Name;
        public string Initial => Name.Length > 0 ? Name.Substring(0, 1).ToUpperInvariant() : "?";
        public string BaseUrl => P.IsOfficial ? "官方登录（无自定义端点）" : (string.IsNullOrEmpty(P.BaseUrl) ? "—" : P.BaseUrl!);
        public string WireApi => P.IsOfficial ? "—" : P.WireApi;
        public string SubText => P.IsOfficial ? "官方登录（无自定义端点）" : string.Join("   ·   ",
            new[]
            {
                string.IsNullOrEmpty(P.BaseUrl) ? null : P.BaseUrl,
                P.WireApi,
                string.IsNullOrWhiteSpace(P.BearerToken) ? "auth.json" : "bearer",
            }.Where(s => s != null));
    }

    // ---------- Claude CLI ----------

    void RefreshClaude()
    {
        _claudeProviders = CliStore.LoadClaude();
        var current = ClaudeCli.CurrentBaseUrl();

        var rows = _claudeProviders.Select(p => new ClaudeRow
        {
            P = p,
            IsCurrent = p.IsOfficial
                ? string.IsNullOrEmpty(current)
                : !string.IsNullOrEmpty(current) && current == p.BaseUrl,
        }).ToList();

        ClaudeList.ItemsSource = null;
        ClaudeList.ItemsSource = rows;

        var active = rows.FirstOrDefault(r => r.IsCurrent);
        ClaudeCurrentText.Text = active != null
            ? active.Name + (active.P.IsOfficial ? "" : "  (" + active.P.BaseUrl + ")")
            : (string.IsNullOrEmpty(current) ? "官方（无自定义端点）" : "未收录的端点: " + current);

        ClaudeEmpty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    void OnAddClaude(object sender, RoutedEventArgs e) => EditClaude(null);

    void OnRowApplyClaude(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ClaudeRow row) ApplyClaudeRow(row);
    }

    void OnCardEditClaude(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ClaudeRow row) EditClaude(row.P);
    }

    void OnCardDeleteClaude(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ClaudeRow row) return;
        if (!Confirm($"删除供应商「{row.Name}」？（仅删除本地存档）")) return;
        _claudeProviders.RemoveAll(x => x.Name == row.P.Name);
        CliStore.SaveClaude(_claudeProviders);
        RefreshClaude();
    }

    void OnCardCopyClaude(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ClaudeRow row) CopyClaudeProvider(row.P, CopyTarget.ClaudeCli);
    }

    void ApplyClaudeRow(ClaudeRow row)
    {
        try { ClaudeCli.Apply(row.P); }
        catch (Exception ex) { ShowToast("应用失败：" + ex.Message, isError: true); return; }
        RefreshClaude();
        ShowToast($"Claude CLI 已切换到「{row.Name}」");
    }

    void EditClaude(ClaudeProvider? existing)
    {
        var dlg = new ProviderDialog(ProviderDialogMode.Claude, existing) { Owner = this };
        if (dlg.ShowDialog() != true || dlg.ResultClaude == null) return;
        var p = dlg.ResultClaude;
        var idx = existing != null ? _claudeProviders.IndexOf(existing) : _claudeProviders.FindIndex(x => x.Name == p.Name);
        if (idx >= 0) _claudeProviders[idx] = p;
        else _claudeProviders.Add(p);
        CliStore.SaveClaude(_claudeProviders);
        RefreshClaude();
        ShowToast($"已保存供应商「{p.Name}」");
    }

    void OnRefreshClaude(object sender, RoutedEventArgs e) => RefreshClaude();

    void OnOpenClaudeSettings(object sender, RoutedEventArgs e) => OpenFile(ClaudeCli.SettingsPath);

    // ---------- Claude Desktop ----------

    void RefreshDesktop()
    {
        _desktopProviders = CliStore.LoadClaudeDesktop();
        var current = ClaudeDesktopCli.CurrentGatewayUrl();

        var rows = _desktopProviders.Select(p => new ClaudeRow
        {
            P = p,
            IsCurrent = p.IsOfficial
                ? string.IsNullOrEmpty(current)
                : !string.IsNullOrEmpty(current) && current == p.BaseUrl,
        }).ToList();

        DesktopList.ItemsSource = null;
        DesktopList.ItemsSource = rows;

        var active = rows.FirstOrDefault(r => r.IsCurrent);
        DesktopCurrentText.Text = active != null
            ? active.Name + (active.P.IsOfficial ? "" : "  (" + active.P.BaseUrl + ")")
            : (string.IsNullOrEmpty(current) ? "官方（无自定义网关）" : "未收录的网关: " + current);

        DesktopEmpty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    void OnAddDesktop(object sender, RoutedEventArgs e) => EditDesktop(null);

    void OnRowApplyDesktop(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ClaudeRow row) ApplyDesktopRow(row);
    }

    void OnCardEditDesktop(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ClaudeRow row) EditDesktop(row.P);
    }

    void OnCardDeleteDesktop(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ClaudeRow row) return;
        if (!Confirm($"删除供应商「{row.Name}」？（仅删除本地存档）")) return;
        _desktopProviders.RemoveAll(x => x.Name == row.P.Name);
        CliStore.SaveClaudeDesktop(_desktopProviders);
        RefreshDesktop();
    }

    void OnCardCopyDesktop(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ClaudeRow row) CopyClaudeProvider(row.P, CopyTarget.ClaudeDesktop);
    }

    void ApplyDesktopRow(ClaudeRow row)
    {
        try { ClaudeDesktopCli.Apply(row.P); }
        catch (Exception ex) { ShowToast("应用失败：" + ex.Message, isError: true); return; }
        RefreshDesktop();
        ShowToast($"Claude 客户端已切换到「{row.Name}」（需重启生效）");
    }

    void EditDesktop(ClaudeProvider? existing)
    {
        var dlg = new ProviderDialog(ProviderDialogMode.ClaudeDesktop, existing) { Owner = this };
        if (dlg.ShowDialog() != true || dlg.ResultClaude == null) return;
        var p = dlg.ResultClaude;
        var idx = existing != null ? _desktopProviders.IndexOf(existing) : _desktopProviders.FindIndex(x => x.Name == p.Name);
        if (idx >= 0) _desktopProviders[idx] = p;
        else _desktopProviders.Add(p);
        CliStore.SaveClaudeDesktop(_desktopProviders);
        RefreshDesktop();
        ShowToast($"已保存供应商「{p.Name}」");
    }

    void OnRefreshDesktop(object sender, RoutedEventArgs e) => RefreshDesktop();

    void OnOpenDesktopConfig(object sender, RoutedEventArgs e) => OpenFile(ClaudeDesktopCli.ConfigPath);

    // ---------- Codex ----------

    void RefreshCodex()
    {
        _codexProviders = CliStore.LoadCodex();
        var current = CodexCli.CurrentProviderId();

        var rows = _codexProviders.Select(p => new CodexRow
        {
            P = p,
            IsCurrent = p.IsOfficial
                ? string.IsNullOrEmpty(current)
                : current == p.Id,
        }).ToList();

        CodexList.ItemsSource = null;
        CodexList.ItemsSource = rows;

        var active = rows.FirstOrDefault(r => r.IsCurrent);
        CodexCurrentText.Text = active != null
            ? active.Name + (active.P.IsOfficial ? "" : "  (" + active.P.BaseUrl + ")")
            : (string.IsNullOrEmpty(current) ? "官方（无自定义端点）" : "未收录的供应商: " + current);

        CodexEmpty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    void OnAddCodex(object sender, RoutedEventArgs e) => EditCodex(null);

    void OnRowApplyCodex(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is CodexRow row) ApplyCodexRow(row);
    }

    void OnCardEditCodex(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is CodexRow row) EditCodex(row.P);
    }

    void OnCardDeleteCodex(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not CodexRow row) return;
        if (!Confirm($"删除供应商「{row.Name}」？（仅删除本地存档）")) return;
        _codexProviders.RemoveAll(x => x.Name == row.P.Name);
        CliStore.SaveCodex(_codexProviders);
        RefreshCodex();
    }

    void OnCardCopyCodex(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is CodexRow row) CopyCodexProvider(row.P);
    }

    void ApplyCodexRow(CodexRow row)
    {
        try { CodexCli.Apply(row.P); }
        catch (Exception ex) { ShowToast("应用失败：" + ex.Message, isError: true); return; }
        RefreshCodex();
        ShowToast($"Codex 已切换到「{row.Name}」");
    }

    void EditCodex(CodexProvider? existing)
    {
        var dlg = new ProviderDialog(ProviderDialogMode.Codex, existing) { Owner = this };
        if (dlg.ShowDialog() != true || dlg.ResultCodex == null) return;
        var p = dlg.ResultCodex;
        var idx = existing != null ? _codexProviders.IndexOf(existing) : _codexProviders.FindIndex(x => x.Name == p.Name || x.Id == p.Id);
        if (idx >= 0) _codexProviders[idx] = p;
        else _codexProviders.Add(p);
        CliStore.SaveCodex(_codexProviders);
        RefreshCodex();
        ShowToast($"已保存供应商「{p.Name}」");
    }

    void OnRefreshCodex(object sender, RoutedEventArgs e) => RefreshCodex();

    void OnOpenCodexConfig(object sender, RoutedEventArgs e) => OpenFile(CodexCli.ConfigPath);

    // ---------- OpenCode ----------

    public class OpenCodeRow
    {
        public OpenCodeProvider P { get; init; } = null!;
        public bool IsCurrent { get; init; }
        public string Name => string.IsNullOrEmpty(P.Name) ? P.Id : P.Name;
        public string Initial =>
            (string.IsNullOrEmpty(P.Name) ? P.Id : P.Name).Substring(0, 1).ToUpperInvariant();
        public string SubText => string.Join("   ·   ",
            new string?[]
            {
                string.IsNullOrEmpty(P.BaseUrl) ? null : P.BaseUrl,
                P.Npm,
                ModelsSummary(),
            }.Where(s => !string.IsNullOrEmpty(s)));

        string ModelsSummary()
        {
            try
            {
                if (string.IsNullOrWhiteSpace(P.ModelsJson)) return "未配置模型";
                if (JsonNode.Parse(P.ModelsJson) is not JsonObject models) return "未配置模型";
                var count = models.Count;
                return count == 0 ? "未配置模型" : count + " 个模型";
            }
            catch
            {
                return "未配置模型";
            }
        }
    }

    void RefreshOpencode()
    {
        _openCodeProviders = OpenCodeCli.LoadProviders();
        var current = OpenCodeCli.CurrentModel();

        var rows = _openCodeProviders.Select(p => new OpenCodeRow
        {
            P = p,
            IsCurrent = !string.IsNullOrEmpty(current)
                && (current == p.Id || current.StartsWith(p.Id + "/", StringComparison.Ordinal)),
        }).ToList();

        OcList.ItemsSource = null;
        OcList.ItemsSource = rows;
        OcCurrentText.Text = string.IsNullOrEmpty(current)
            ? "未设置默认模型"
            : current;
        OcEmpty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    void OnAddOpencode(object sender, RoutedEventArgs e) => EditOpencode(null);

    void OnRowApplyOc(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is OpenCodeRow row) ApplyOpencodeRow(row);
    }

    void OnCardEditOc(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is OpenCodeRow row) EditOpencode(row.P);
    }

    void OnCardDeleteOc(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not OpenCodeRow row) return;
        if (!Confirm($"从 opencode.json 删除供应商「{row.P.Id}」？（仅此文件，不影响 api key 本体）")) return;
        try
        {
            OpenCodeCli.DeleteProvider(row.P.Id);
            if (OpenCodeCli.CurrentModel()?.StartsWith(row.P.Id + "/") == true)
            {
                // cancel by resetting the default model: leaving it alone would result in an uneffective state, directly remove the model key
                OpenCodeCli.ClearDefaultModel();
            }
        }
        catch (Exception ex) { Warn("删除失败：" + ex.Message); return; }
        RefreshOpencode();
    }

    void OnCardCopyOc(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not OpenCodeRow row) return;
        var dlg = new CopyTargetsDialog(row.Name, CopyTarget.OpenCode) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        var done = new List<string>();
        foreach (var t in dlg.Targets)
        {
            switch (t)
            {
                case CopyTarget.ClaudeCli:
                    UpsertClaude(ProviderConvert.ToClaude(row.P));
                    done.Add("Claude CLI");
                    break;
                case CopyTarget.ClaudeDesktop:
                    UpsertDesktop(ProviderConvert.ToClaude(row.P));
                    done.Add("Claude 客户端");
                    break;
                case CopyTarget.Codex:
                    UpsertCodex(ProviderConvert.ToCodex(row.P));
                    done.Add("Codex");
                    break;
            }
        }
        RefreshClaude(); RefreshDesktop(); RefreshCodex();
        ShowToast($"已复制「{row.P.Id}」到: {string.Join("、", done)}");
    }

    void EditOpencode(OpenCodeProvider? existing)
    {
        var dlg = new ProviderDialog(ProviderDialogMode.OpenCode, existing) { Owner = this };
        if (dlg.ShowDialog() != true || dlg.ResultOpenCode == null) return;
        var p = dlg.ResultOpenCode;
        try
        {
            if (existing != null && existing.Id != p.Id)
                OpenCodeCli.DeleteProvider(existing.Id);
            OpenCodeCli.SaveProvider(p);
            RefreshOpencode();
            ShowToast($"已保存供应商「{p.Id}」");
        }
        catch (Exception ex) { ShowToast("保存失败：" + ex.Message, isError: true); }
    }

    void ApplyOpencodeRow(OpenCodeRow row)
    {
        string? firstModel = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(row.P.ModelsJson) &&
                JsonNode.Parse(row.P.ModelsJson) is JsonObject models)
                firstModel = models.Select(kv => kv.Key).FirstOrDefault();
        }
        catch { }

        try
        {
            OpenCodeCli.SaveProvider(row.P, setDefaultModel: firstModel != null, defaultModelId: firstModel);
        }
        catch (Exception ex) { ShowToast("应用失败：" + ex.Message, isError: true); return; }
        RefreshOpencode();
        ShowToast(firstModel != null
            ? $"OpenCode 默认模型已设为 {row.P.Id}/{firstModel}"
            : $"已保存供应商「{row.P.Id}」");
    }

    void OnRefreshOpencode(object sender, RoutedEventArgs e) => RefreshOpencode();

    void OnOpenOcConfig(object sender, RoutedEventArgs e) => OpenFile(OpenCodeCli.ConfigPath);

    // ---------- Pi ----------

    void RefreshPi()
    {
        _piAccounts = CliStore.LoadPi();
        if (!PiCli.IsInstalled)
        {
            PiCurrentText.Text = "未安装（~/.pi/agent 不存在）";
            PiList.ItemsSource = null;
            PiEmpty.Visibility = Visibility.Collapsed;
            return;
        }
        var (prov, model) = PiCli.CurrentDefaults();
        PiCurrentText.Text = (prov ?? "未设置") + (string.IsNullOrEmpty(model) ? "" : " = " + model);

        foreach (var a in _piAccounts) a.IsCurrent = PiCli.MatchesCurrent(a);
        PiList.ItemsSource = null;
        PiList.ItemsSource = _piAccounts.OrderByDescending(a => a.CapturedAtUtc).ToList();
        PiEmpty.Visibility = _piAccounts.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    void OnCapturePi(object sender, RoutedEventArgs e)
    {
        if (!PiCli.IsInstalled) { ShowToast("未检测到 Pi（~/.pi/agent 不存在）", isError: true); return; }
        var (prov, _) = PiCli.CurrentDefaults();
        var name = $"pi-{prov ?? "default"}-{DateTime.Now:yyyyMMdd-HHmmss}";
        try
        {
            var account = PiCli.Capture(name);
            var idx = _piAccounts.FindIndex(a => a.Name == name);
            if (idx >= 0) _piAccounts[idx] = account; else _piAccounts.Add(account);
            CliStore.SavePi(_piAccounts);
        }
        catch (Exception ex) { ShowToast("抓取失败：" + ex.Message, isError: true); return; }
        RefreshPi();
        ShowToast($"已抓取快照 {name}");
    }

    void OnRowApplyPi(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not PiAccount acc) return;
        try { PiCli.Restore(acc); }
        catch (Exception ex) { ShowToast("还原失败：" + ex.Message, isError: true); return; }
        RefreshPi();
        ShowToast($"Pi 已还原到「{acc.Name}」");
    }

    void OnCardDeletePi(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not PiAccount acc) return;
        if (!Confirm($"删除快照「{acc.Name}」？（不影响 Pi 当前凭据）")) return;
        _piAccounts.RemoveAll(x => x.Name == acc.Name);
        CliStore.SavePi(_piAccounts);
        RefreshPi();
    }

    void OnRefreshPi(object sender, RoutedEventArgs e) => RefreshPi();

    void OnOpenPiDir(object sender, RoutedEventArgs e)
    {
        var dir = Path.GetDirectoryName(PiCli.AuthPath);
        if (dir == null || !Directory.Exists(dir)) { ShowToast("未找到 Pi 目录", isError: true); return; }
        Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
    }

    // ---------- Cross-copy ----------

    void CopyClaudeProvider(ClaudeProvider src, CopyTarget source)
    {
        var dlg = new CopyTargetsDialog(src.Name, source) { Owner = this };
        if (dlg.ShowDialog() != true) return;

        var done = new List<string>();
        foreach (var t in dlg.Targets)
        {
            switch (t)
            {
                case CopyTarget.ClaudeCli:
                    UpsertClaude(ProviderConvert.Clone(src));
                    done.Add("Claude CLI");
                    break;
                case CopyTarget.ClaudeDesktop:
                    UpsertDesktop(ProviderConvert.Clone(src));
                    done.Add("Claude 客户端");
                    break;
                case CopyTarget.Codex:
                    UpsertCodex(ProviderConvert.ToCodex(src));
                    done.Add("Codex");
                    break;
            }
        }
        RefreshClaude(); RefreshDesktop(); RefreshCodex();
        ShowToast($"已复制「{src.Name}」到: {string.Join("、", done)}");
    }

    void CopyCodexProvider(CodexProvider src)
    {
        var dlg = new CopyTargetsDialog(src.Name, CopyTarget.Codex) { Owner = this };
        if (dlg.ShowDialog() != true) return;

        var done = new List<string>();
        foreach (var t in dlg.Targets)
        {
            var c = ProviderConvert.ToClaude(src);
            switch (t)
            {
                case CopyTarget.ClaudeCli:
                    UpsertClaude(c);
                    done.Add("Claude CLI");
                    break;
                case CopyTarget.ClaudeDesktop:
                    UpsertDesktop(c);
                    done.Add("Claude 客户端");
                    break;
            }
        }
        RefreshClaude(); RefreshDesktop(); RefreshCodex();
        ShowToast($"已复制「{src.Name}」到: {string.Join("、", done)}");
    }

    void UpsertClaude(ClaudeProvider p)
    {
        var i = _claudeProviders.FindIndex(x => x.Name == p.Name);
        if (i >= 0) _claudeProviders[i] = p; else _claudeProviders.Add(p);
        CliStore.SaveClaude(_claudeProviders);
    }

    void UpsertDesktop(ClaudeProvider p)
    {
        var i = _desktopProviders.FindIndex(x => x.Name == p.Name);
        if (i >= 0) _desktopProviders[i] = p; else _desktopProviders.Add(p);
        CliStore.SaveClaudeDesktop(_desktopProviders);
    }

    void UpsertCodex(CodexProvider p)
    {
        var i = _codexProviders.FindIndex(x => x.Name == p.Name || x.Id == p.Id);
        if (i >= 0) _codexProviders[i] = p; else _codexProviders.Add(p);
        CliStore.SaveCodex(_codexProviders);
    }

    // ---------- cc-switch import ----------

    void OnImportCc(object sender, RoutedEventArgs e)
    {
        if (!CcSwitchImport.IsAvailable)
        {
            ShowToast("未找到 cc-switch 数据库（~/.cc-switch/cc-switch.db）", isError: true);
            return;
        }
        var dlg = new ImportDialog { Owner = this };
        try
        {
            if (dlg.ShowDialog() == true)
            {
                RefreshClaude(); RefreshDesktop(); RefreshCodex();
                ShowToast("已从 cc-switch 导入供应商");
            }
        }
        catch (Exception ex) { ShowToast("读取 cc-switch 数据失败：" + ex.Message, isError: true); return; }
        RefreshClaude(); RefreshDesktop(); RefreshCodex();
    }

    // ---------- Window Controls & Toast ----------

    void OnMinWindow(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    void OnMaxWindow(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    void OnCloseWindow(object sender, RoutedEventArgs e) => Close();

    System.Windows.Threading.DispatcherTimer? _toastTimer;

    void ShowToast(string msg, bool isError = false)
    {
        Dispatcher.Invoke(() =>
        {
            _toastTimer?.Stop();
            ToastText.Text = msg;
            ToastIcon.Data = (System.Windows.Media.Geometry)FindResource(isError ? "IconClose" : "IconCheck");
            ToastIcon.Fill = (System.Windows.Media.Brush)FindResource(isError ? "DangerBrush" : "GreenBrush");
            ToastBanner.BorderBrush = (System.Windows.Media.Brush)FindResource(isError ? "DangerBrush" : "CardBorderBrush");

            ToastBanner.Visibility = Visibility.Visible;
            var anim = new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180));
            ToastBanner.BeginAnimation(OpacityProperty, anim);

            _toastTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2.6) };
            _toastTimer.Tick += (_, _) =>
            {
                _toastTimer.Stop();
                var fadeOut = new System.Windows.Media.Animation.DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(200));
                fadeOut.Completed += (_, _) => ToastBanner.Visibility = Visibility.Collapsed;
                ToastBanner.BeginAnimation(OpacityProperty, fadeOut);
            };
            _toastTimer.Start();
        });
    }

    // ---------- Common ----------

    static void OpenFile(string path)
    {
        if (!File.Exists(path)) { Warn("文件不存在：" + path); return; }
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    void SetBusy(string text)
    {
        BusyText.Text = text;
        BusyOverlay.Visibility = Visibility.Visible;
        IsEnabled = false;
    }

    void ClearBusy()
    {
        BusyOverlay.Visibility = Visibility.Collapsed;
        IsEnabled = true;
    }

    static bool Confirm(string msg) =>
        MessageBox.Show(msg, "APISwitch", MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK;

    static void Warn(string msg) =>
        MessageBox.Show(msg, "APISwitch", MessageBoxButton.OK, MessageBoxImage.Warning);

    static void Info(string msg) =>
        MessageBox.Show(msg, "APISwitch", MessageBoxButton.OK, MessageBoxImage.Information);
}


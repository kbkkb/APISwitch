using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using APISwitch.Dialogs;
using APISwitch.Models;
using APISwitch.Services;

namespace APISwitch;

public partial class MainWindow : Window
{
    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;

    List<Profile> _profiles = new();
    List<ClaudeProvider> _claudeProviders = new();
    List<ClaudeProvider> _desktopProviders = new();
    List<CodexProvider> _codexProviders = new();
    List<PiAccount> _piAccounts = new();
    List<OpenCodeProvider> _openCodeProviders = new();
    List<PiProvider> _piProviders = new();
    UpdateInfo? _latestUpdate;

    System.Windows.Threading.DispatcherTimer? _agQuotaTimer;
    bool _isRefreshingAgQuotas = false;

    System.Windows.Forms.NotifyIcon? _notifyIcon;
    bool _isExiting = false;
    ProviderDialog? _activeProviderDialog;

    public MainWindow()
    {
        InitializeComponent();
        var ver = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        if (ver != null && AppVersionText != null)
        {
            AppVersionText.Text = $"v{ver.Major}.{ver.Minor}.{ver.Build}";
        }

        Activated += OnWindowActivated;
        Deactivated += OnWindowDeactivated;
        StateChanged += OnWindowStateChanged;

        Loaded += (_, _) =>
        {
            LocalProxyServer.StateChanged += () => Dispatcher.Invoke(UpdateRouterUI);
            RefreshAll();
            UpdateRouterUI();
            _ = CheckUpdateSilentAsync();
            InitAgQuotaTimer();
            InitTrayIcon();
        };
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

        try
        {
            var handle = new WindowInteropHelper(this).Handle;
            int cornerPreference = DWMWCP_ROUND;
            DwmSetWindowAttribute(handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref cornerPreference, sizeof(int));
        }
        catch
        {
            // 兼容不支持 DWMWA_WINDOW_CORNER_PREFERENCE 的低版本系统
        }
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

    void InitAgQuotaTimer()
    {
        _agQuotaTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(60)
        };
        _agQuotaTimer.Tick += (_, _) =>
        {
            if (IsActive && WindowState != WindowState.Minimized && Tabs?.SelectedIndex == 0)
            {
                _ = RefreshAllAgQuotasAsync(silent: true);
            }
        };
        if (IsActive && WindowState != WindowState.Minimized)
        {
            _agQuotaTimer.Start();
            _ = RefreshAllAgQuotasAsync(silent: true);
        }
    }

    void OnWindowActivated(object? sender, EventArgs e)
    {
        if (WindowState != WindowState.Minimized)
        {
            OnForegroundEntered();
        }
    }

    void OnWindowDeactivated(object? sender, EventArgs e)
    {
        _agQuotaTimer?.Stop();
    }

    void OnWindowStateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized)
        {
            MinimizeToTray();
        }
        else if (IsActive)
        {
            OnForegroundEntered();
        }
    }

    void OnForegroundEntered()
    {
        _agQuotaTimer?.Stop();
        _agQuotaTimer?.Start();

        if (Tabs?.SelectedIndex == 0)
        {
            _ = RefreshAllAgQuotasAsync(silent: true);
        }
    }

    void OnTabChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        switch (Tabs!.SelectedIndex)
        {
            case 0:
                RefreshAntigravity();
                _ = RefreshAllAgQuotasAsync(silent: true);
                break;
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

    async void OnCardActivateQuota(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not Profile target) return;
        SetBusy($"正在向 Google 发送激活消息并刷新 {target.Email} 的限额…");
        try
        {
            var (ok, latencyMs, msg) = await AgQuotaService.ActivateAccountQuotaAsync(target);
            ClearBusy();
            RefreshAntigravity();
            if (ok)
            {
                ShowToast($"⚡ {target.Email} 账号限额已激活并刷新 (延迟 {latencyMs}ms)");
            }
            else
            {
                ShowToast($"激活限额失败: {msg}", isError: true);
            }
        }
        catch (Exception ex)
        {
            ClearBusy();
            ShowToast("激活限额异常: " + ex.Message, isError: true);
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
            SetBusy($"正在批量发送消息激活限额 ({i + 1}/{count}): {p.Email}…");

            try
            {
                var (ok, latencyMs, msg) = await AgQuotaService.ActivateAccountQuotaAsync(p);
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
        ShowToast($"⚡ 批量限额激活完成！成功 {successCount} 个，失败 {failCount} 个");
    }

    async void OnCardRefreshQuota(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not Profile target) return;
        try
        {
            var ok = await AgQuotaService.RefreshQuotaAsync(target);
            Dispatcher.Invoke(() => ProfileList.Items.Refresh());
            if (ok)
            {
                if (target.IsProTier)
                    ShowToast($"已同时刷新 {target.Email} 的 Gemini 与 GPT/Claude 配额");
                else
                    ShowToast($"已刷新 {target.Email} 的 Gemini 周配额 (免费版仅限Gemini周额度)");
            }
            else
            {
                ShowToast($"获取 {target.Email} 配额失败，请确认网络连接正常", isError: true);
            }
        }
        catch (Exception ex)
        {
            ShowToast("配额刷新失败: " + ex.Message, isError: true);
        }
    }

    void OnRowSwitchAntigravity(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is Profile p) _ = SwitchToProfile(p);
    }

    async Task SwitchToProfile(Profile target)
    {
        SetBusy($"正在切换到账号 {target.Email}…");
        try
        {
            var stopped = await Task.Run(AgProcess.StopIdeAsync);
            if (!stopped)
            {
                ClearBusy();
                ShowToast("无法自动关闭 Antigravity，请手动退出后重试", isError: true);
                return;
            }

            // 1. Sync credentials to system (oauth_creds.json, Credential Manager, Antigravity Tools, storage.json)
            await AgToolsService.ApplySystemCredentialsAsync(target);

            // 2. Inject or restore full authentication session in state.vscdb
            var db = AgPaths.FindStateDb();
            if (db != null)
            {
                var authValues = AgAuthHelper.BuildAuthValues(target);
                if (authValues.Count > 0)
                {
                    target.Values[AgState.KeyOauthToken] = authValues[AgState.KeyOauthToken];
                    target.Values[AgState.KeyUserStatus] = authValues[AgState.KeyUserStatus];
                    ProfileStore.Save(target);
                    await Task.Run(() => AgDb.WriteAuth(db, target.Values));
                }
                else if (target.Values != null && target.Values.Count > 0)
                {
                    await Task.Run(() => AgDb.WriteAuth(db, target.Values));
                }
                else
                {
                    await Task.Run(() => AgDb.ClearAuth(db));
                }
            }

            // 3. Trigger cloud handshake in background to bind cloud companion project
            _ = Task.Run(async () =>
            {
                try { await AgQuotaService.ActivateProfileAsync(target); }
                catch { }
            });
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
            catch (Exception ex) { ClearBusy(); ShowToast("已激活凭据，但重启 IDE 失败：" + ex.Message, isError: true); return; }
        }

        ClearBusy();
        RefreshAntigravity();
        ShowToast($"已成功切换到账号 {target.Email}" + (AutoRestartCheck.IsChecked == true ? "（已重启 IDE）" : ""));
    }

    void OnCardDeleteAntigravity(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not Profile target) return;
        if (!Confirm($"删除账号存档 {target.Email}？\n（仅删除本地存档，不影响 Google 账号本身）")) return;
        try { ProfileStore.Delete(target); }
        catch (Exception ex) { Warn("删除失败：" + ex.Message); return; }
        RefreshAntigravity();
    }

    async Task RefreshAllAgQuotasAsync(bool silent)
    {
        if (_isRefreshingAgQuotas) return;
        _isRefreshingAgQuotas = true;

        try
        {
            RefreshAntigravity();

            if (_profiles.Count == 0)
            {
                if (!silent) ShowToast("当前没有账号存档可供刷新配额");
                return;
            }

            var targets = _profiles.ToList();
            using var semaphore = new SemaphoreSlim(4);

            var tasks = targets.Select(async p =>
            {
                await semaphore.WaitAsync();
                try
                {
                    await AgQuotaService.RefreshQuotaAsync(p);
                    Dispatcher.Invoke(() =>
                    {
                        ProfileList.Items.Refresh();
                    });
                }
                catch
                {
                    // 单账号异常不中断整体批量刷新
                }
                finally
                {
                    semaphore.Release();
                }
            });

            await Task.WhenAll(tasks);

            Dispatcher.Invoke(() =>
            {
                ProfileList.Items.Refresh();
                if (!silent)
                {
                    ShowToast($"已刷新全部 {_profiles.Count} 个账号的最新配额");
                }
            });
        }
        finally
        {
            _isRefreshingAgQuotas = false;
        }
    }

    async void OnRefresh(object sender, RoutedEventArgs e) => await RefreshAllAgQuotasAsync(silent: false);

    void OnLaunchIde(object sender, RoutedEventArgs e)
    {
        try { AgProcess.StartIde(); }
        catch (Exception ex) { Warn("启动失败：" + ex.Message); }
    }

    void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        Process.Start(new ProcessStartInfo(AgPaths.ProfilesDir) { UseShellExecute = true });
    }

    // ---------- Card Drag and Drop Reordering ----------

    System.Windows.Point _dragStartPoint;
    object? _draggedItem;
    System.Windows.Controls.ListBox? _dragSourceListBox;

    static T? FindVisualParent<T>(DependencyObject? child) where T : DependencyObject
    {
        while (child != null)
        {
            if (child is T parent) return parent;
            child = System.Windows.Media.VisualTreeHelper.GetParent(child);
        }
        return null;
    }

    void OnCardListPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not System.Windows.Controls.ListBox lb) return;

        // If clicked on any button or interactive element, don't start dragging
        if (FindVisualParent<System.Windows.Controls.Primitives.ButtonBase>(e.OriginalSource as DependencyObject) != null)
        {
            _draggedItem = null;
            _dragSourceListBox = null;
            return;
        }

        var item = FindVisualParent<ListBoxItem>(e.OriginalSource as DependencyObject);
        if (item != null && item.DataContext != null)
        {
            _dragStartPoint = e.GetPosition(lb);
            _draggedItem = item.DataContext;
            _dragSourceListBox = lb;
        }
    }

    void OnCardListPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _draggedItem = null;
        _dragSourceListBox = null;
    }

    void OnCardListPreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _draggedItem == null || _dragSourceListBox != sender)
            return;

        var lb = sender as System.Windows.Controls.ListBox;
        if (lb == null) return;

        System.Windows.Point currentPoint = e.GetPosition(lb);
        Vector diff = _dragStartPoint - currentPoint;

        if (Math.Abs(diff.X) > SystemParameters.MinimumHorizontalDragDistance ||
            Math.Abs(diff.Y) > SystemParameters.MinimumVerticalDragDistance)
        {
            var data = _draggedItem;
            var srcLb = _dragSourceListBox;
            try
            {
                System.Windows.DragDrop.DoDragDrop(srcLb, data, System.Windows.DragDropEffects.Move);
            }
            catch { }
            finally
            {
                _draggedItem = null;
                _dragSourceListBox = null;
            }
        }
    }

    void OnCardListDragOver(object sender, System.Windows.DragEventArgs e)
    {
        if (sender is System.Windows.Controls.ListBox lb && _dragSourceListBox == lb && _draggedItem != null)
        {
            e.Effects = System.Windows.DragDropEffects.Move;

            // Auto-scroll when dragging near boundaries
            if (FindVisualParent<ScrollViewer>(lb) is ScrollViewer sv)
            {
                var pos = e.GetPosition(sv);
                if (pos.Y < 30) sv.ScrollToVerticalOffset(sv.VerticalOffset - 10);
                else if (pos.Y > sv.ActualHeight - 30) sv.ScrollToVerticalOffset(sv.VerticalOffset + 10);
            }
        }
        else
        {
            e.Effects = System.Windows.DragDropEffects.None;
        }
        e.Handled = true;
    }

    void OnCardListDrop(object sender, System.Windows.DragEventArgs e)
    {
        if (sender is not System.Windows.Controls.ListBox lb || _dragSourceListBox != lb || _draggedItem == null) return;

        var targetItem = FindVisualParent<ListBoxItem>(e.OriginalSource as DependencyObject);
        object? targetData = targetItem?.DataContext;

        if (lb == CodexList && _draggedItem is CodexRow srcCodex)
        {
            int oldIndex = _codexProviders.FindIndex(x => x.Name == srcCodex.P.Name && x.Id == srcCodex.P.Id);
            int newIndex = targetData is CodexRow dstCodex
                ? _codexProviders.FindIndex(x => x.Name == dstCodex.P.Name && x.Id == dstCodex.P.Id)
                : _codexProviders.Count - 1;

            if (oldIndex >= 0 && newIndex >= 0 && oldIndex != newIndex)
            {
                var item = _codexProviders[oldIndex];
                _codexProviders.RemoveAt(oldIndex);
                _codexProviders.Insert(newIndex, item);
                CliStore.SaveCodex(_codexProviders);
                RefreshCodex();
                ShowToast($"已将「{srcCodex.Name}」移动至第 {newIndex + 1} 位");
            }
        }
        else if (lb == ClaudeList && _draggedItem is ClaudeRow srcClaude)
        {
            int oldIndex = _claudeProviders.FindIndex(x => x.Name == srcClaude.P.Name);
            int newIndex = targetData is ClaudeRow dstClaude
                ? _claudeProviders.FindIndex(x => x.Name == dstClaude.P.Name)
                : _claudeProviders.Count - 1;

            if (oldIndex >= 0 && newIndex >= 0 && oldIndex != newIndex)
            {
                var item = _claudeProviders[oldIndex];
                _claudeProviders.RemoveAt(oldIndex);
                _claudeProviders.Insert(newIndex, item);
                CliStore.SaveClaude(_claudeProviders);
                RefreshClaude();
                ShowToast($"已将「{srcClaude.Name}」移动至第 {newIndex + 1} 位");
            }
        }
        else if (lb == DesktopList && _draggedItem is ClaudeRow srcDesk)
        {
            int oldIndex = _desktopProviders.FindIndex(x => x.Name == srcDesk.P.Name);
            int newIndex = targetData is ClaudeRow dstDesk
                ? _desktopProviders.FindIndex(x => x.Name == dstDesk.P.Name)
                : _desktopProviders.Count - 1;

            if (oldIndex >= 0 && newIndex >= 0 && oldIndex != newIndex)
            {
                var item = _desktopProviders[oldIndex];
                _desktopProviders.RemoveAt(oldIndex);
                _desktopProviders.Insert(newIndex, item);
                CliStore.SaveClaudeDesktop(_desktopProviders);
                RefreshDesktop();
                ShowToast($"已将「{srcDesk.Name}」移动至第 {newIndex + 1} 位");
            }
        }

        _draggedItem = null;
        _dragSourceListBox = null;
        e.Handled = true;
    }

    // ---------- Shared row types ----------

    public class ClaudeRow
    {
        public ClaudeProvider P { get; init; } = null!;
        public bool IsCurrent { get; init; }
        public bool IsDesktop { get; init; }
        public string Name => P.Name;
        public string Initial => Name.Length > 0 ? Name.Substring(0, 1).ToUpperInvariant() : "?";
        public string BaseUrl => P.IsOfficial ? "官方登录（无自定义端点）" : (string.IsNullOrEmpty(P.BaseUrl) ? "—" : P.BaseUrl!);
        public string Model => string.IsNullOrEmpty(P.Model) ? "—" : P.Model!;

        public bool RequiresRouter => !P.IsOfficial && (
            string.Equals(P.WireApi, "chat", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(P.WireApi, "responses", StringComparison.OrdinalIgnoreCase) ||
            (IsDesktop && string.Equals(P.AccessMode, "mapping", StringComparison.OrdinalIgnoreCase)) ||
            (!IsDesktop && P.ModelMappings != null && P.ModelMappings.Any(m => m.Supports1m)) ||
            (P.ExtraOptions != null && P.ExtraOptions.TryGetValue("require_proxy", out var rp) && bool.TryParse(rp, out var b) && b));

        public Visibility RouterBadgeVisibility => RequiresRouter ? Visibility.Visible : Visibility.Collapsed;

        public bool IsRouterActive => IsDesktop ? LocalProxyServer.IsClaudeDesktopEnabled : LocalProxyServer.IsClaudeCliEnabled;

        public string RouterBadgeText => IsRouterActive ? "⚡ 需开启路由" : "⚠️ 需开启路由 (未开启)";

        public string RouterBadgeTooltip => IsRouterActive
            ? (string.Equals(P.WireApi, "chat", StringComparison.OrdinalIgnoreCase)
                ? "此供应商上游通信协议为 Chat Completions，必须通过本地路由进行协议转译（当前本地路由已就绪）"
                : (string.Equals(P.WireApi, "responses", StringComparison.OrdinalIgnoreCase)
                    ? "此供应商上游通信协议为 OpenAI Responses，必须通过本地路由进行协议转译（当前本地路由已就绪）"
                    : (IsDesktop && string.Equals(P.AccessMode, "mapping", StringComparison.OrdinalIgnoreCase)
                        ? "Claude Desktop 采用模型映射机制，必须通过本地路由重写模型请求（当前本地路由已就绪）"
                        : "此供应商配置了 1M 长上下文，已通过本地路由自动剥离 [1M] 转发上游（当前本地路由已就绪）")))
            : (string.Equals(P.WireApi, "chat", StringComparison.OrdinalIgnoreCase)
                ? "此供应商上游通信协议为 Chat Completions，必须开启本地路由进行协议转译才能正常使用（当前本地路由未开启）"
                : (string.Equals(P.WireApi, "responses", StringComparison.OrdinalIgnoreCase)
                    ? "此供应商上游通信协议为 OpenAI Responses，必须开启本地路由进行协议转译才能正常使用（当前本地路由未开启）"
                    : (IsDesktop && string.Equals(P.AccessMode, "mapping", StringComparison.OrdinalIgnoreCase)
                        ? "Claude Desktop 采用模型映射机制，必须开启本地路由才能将标准模型重写映射到目标模型（当前本地路由未开启）"
                        : "此供应商配置了 1M 长上下文，建议开启本地路由以自动剥离 [1M] 并转发上游；当前未开启本地路由，将以纯净模型名直连上游")));

        public System.Windows.Media.Brush RouterBadgeBackground => IsRouterActive
            ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xEE, 0xF2, 0xFF))
            : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFE, 0xF3, 0xC7));

        public System.Windows.Media.Brush RouterBadgeBorderBrush => IsRouterActive
            ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xC7, 0xD2, 0xFE))
            : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFD, 0xE6, 0x8A));

        public System.Windows.Media.Brush RouterBadgeForeground => IsRouterActive
            ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4F, 0x46, 0xE5))
            : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xD9, 0x77, 0x06));

        public string SubText
        {
            get
            {
                if (P.IsOfficial) return "官方登录（无自定义端点）";

                var parts = new List<string?> { string.IsNullOrEmpty(P.BaseUrl) ? "—" : P.BaseUrl };

                if (string.Equals(P.WireApi, "chat", StringComparison.OrdinalIgnoreCase))
                {
                    parts.Add("Chat 格式 (需路由)");
                }
                else if (string.Equals(P.WireApi, "responses", StringComparison.OrdinalIgnoreCase))
                {
                    parts.Add("Responses 格式 (需路由)");
                }

                if (P.ModelMappings != null && P.ModelMappings.Count > 0)
                {
                    var mapParts = P.ModelMappings
                        .Where(m => !string.IsNullOrWhiteSpace(m.Model))
                        .Select(m => $"{m.Role}: {m.Model}{(m.Supports1m ? "[1M]" : "")}");
                    var mapStr = string.Join(" | ", mapParts);
                    if (!string.IsNullOrEmpty(mapStr))
                    {
                        parts.Add(mapStr);
                    }
                    else if (!string.IsNullOrEmpty(P.Model))
                    {
                        parts.Add("模型 " + P.Model);
                    }
                }
                else if (!string.IsNullOrEmpty(P.Model))
                {
                    parts.Add("模型 " + P.Model);
                }

                return string.Join("   ·   ", parts.Where(s => !string.IsNullOrWhiteSpace(s)));
            }
        }

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

        public bool RequiresRouter => !P.IsOfficial && (
            string.Equals(P.WireApi, "chat", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(P.WireApi, "anthropic", StringComparison.OrdinalIgnoreCase) ||
            (P.ExtraOptions != null && P.ExtraOptions.TryGetValue("require_proxy", out var rp) && bool.TryParse(rp, out var b) && b));

        public Visibility RouterBadgeVisibility => RequiresRouter ? Visibility.Visible : Visibility.Collapsed;

        public bool IsRouterActive => LocalProxyServer.IsCodexEnabled;

        public string RouterBadgeText => IsRouterActive ? "⚡ 需开启路由" : "⚠️ 需开启路由 (未开启)";

        public string RouterBadgeTooltip => IsRouterActive
            ? "此供应商上游通信协议为 " + (P.WireApi == "chat" ? "Chat Completions" : (P.WireApi == "anthropic" ? "Anthropic Messages" : P.WireApi)) + "，必须通过 APISwitch 本地路由进行协议转译（当前本地路由已就绪）"
            : "此供应商上游通信协议为 " + (P.WireApi == "chat" ? "Chat Completions" : (P.WireApi == "anthropic" ? "Anthropic Messages" : P.WireApi)) + "，必须开启 Codex 本地路由才能正常通信（当前本地路由未开启）";

        public System.Windows.Media.Brush RouterBadgeBackground => IsRouterActive
            ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xEE, 0xF2, 0xFF))
            : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFE, 0xF3, 0xC7));

        public System.Windows.Media.Brush RouterBadgeBorderBrush => IsRouterActive
            ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xC7, 0xD2, 0xFE))
            : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFD, 0xE6, 0x8A));

        public System.Windows.Media.Brush RouterBadgeForeground => IsRouterActive
            ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4F, 0x46, 0xE5))
            : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xD9, 0x77, 0x06));

        public string SubText
        {
            get
            {
                if (P.IsOfficial) return "官方登录（无自定义端点）";
                string wireDesc = P.WireApi switch
                {
                    "chat" => "Chat 格式 (需路由转译)",
                    "anthropic" => "Anthropic 格式 (需路由转译)",
                    "responses" => "Responses 原生",
                    _ => P.WireApi
                };
                return string.Join("   ·   ",
                    new[]
                    {
                        string.IsNullOrEmpty(P.BaseUrl) ? null : P.BaseUrl,
                        wireDesc,
                        string.IsNullOrWhiteSpace(P.BearerToken) ? "auth.json" : "bearer",
                    }.Where(s => s != null));
            }
        }
    }

    // ---------- Claude CLI ----------

    void RefreshClaude()
    {
        _claudeProviders = CliStore.LoadClaude();
        var currentName = ClaudeCli.CurrentProviderName();
        var currentUrl = ClaudeCli.CurrentBaseUrl();

        var rows = _claudeProviders.Select(p => new ClaudeRow
        {
            P = p,
            IsCurrent = p.IsOfficial
                ? string.IsNullOrEmpty(currentName) && string.IsNullOrEmpty(currentUrl)
                : (!string.IsNullOrEmpty(currentName) && (string.Equals(currentName, p.Name, StringComparison.OrdinalIgnoreCase) || string.Equals(currentName, p.Id, StringComparison.OrdinalIgnoreCase))) ||
                  (!string.IsNullOrEmpty(currentUrl) && string.Equals(currentUrl.TrimEnd('/'), p.BaseUrl?.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)),
        }).ToList();

        ClaudeList.ItemsSource = null;
        ClaudeList.ItemsSource = rows;

        var active = rows.FirstOrDefault(r => r.IsCurrent);
        ClaudeCurrentText.Text = active != null
            ? active.Name + (active.P.IsOfficial ? "" : "  (" + active.P.BaseUrl + ")")
            : (!string.IsNullOrEmpty(currentName) ? currentName : (string.IsNullOrEmpty(currentUrl) ? "官方（无自定义端点）" : "未收录的端点: " + currentUrl));

        ClaudeEmpty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateRouterUI();
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
        bool autoEnabled = false;
        if (row.RequiresRouter && !LocalProxyServer.IsClaudeCliEnabled)
        {
            LocalProxyServer.SetClaudeCliEnabled(true);
            autoEnabled = true;
        }

        try { ClaudeCli.Apply(row.P); }
        catch (Exception ex) { ShowToast("应用失败：" + ex.Message, isError: true); return; }
        RefreshClaude();
        if (LocalProxyServer.IsClaudeCliEnabled && !row.P.IsOfficial)
        {
            ShowToast(autoEnabled
                ? $"Claude CLI 已切换到「{row.Name}」（已自动开启本地路由）"
                : $"Claude CLI 已切换到「{row.Name}」（本地路由已接管）");
        }
        else
        {
            ShowToast($"Claude CLI 已切换到「{row.Name}」");
        }
    }

    async void EditClaude(ClaudeProvider? existing)
    {
        if (_activeProviderDialog != null && _activeProviderDialog.IsLoaded)
        {
            _activeProviderDialog.Activate();
            _activeProviderDialog.Focus();
            ShowToast("已有正在编辑的供应商窗口，请先保存或关闭该窗口");
            return;
        }

        var dlg = new ProviderDialog(ProviderDialogMode.Claude, existing) { Owner = this };
        _activeProviderDialog = dlg;
        dlg.Show();
        var ok = await dlg.WaitForResultAsync();
        _activeProviderDialog = null;

        if (!ok || dlg.ResultClaude == null) return;
        var p = dlg.ResultClaude;
        int idx = -1;
        if (existing != null)
        {
            idx = _claudeProviders.IndexOf(existing);
            if (idx < 0 && !string.IsNullOrEmpty(existing.Id))
                idx = _claudeProviders.FindIndex(x => string.Equals(x.Id, existing.Id, StringComparison.OrdinalIgnoreCase));
            if (idx < 0 && !string.IsNullOrEmpty(existing.Name))
                idx = _claudeProviders.FindIndex(x => string.Equals(x.Name, existing.Name, StringComparison.OrdinalIgnoreCase));
        }
        if (idx < 0 && !string.IsNullOrEmpty(p.Id))
            idx = _claudeProviders.FindIndex(x => string.Equals(x.Id, p.Id, StringComparison.OrdinalIgnoreCase));
        if (idx < 0 && !string.IsNullOrEmpty(p.Name))
            idx = _claudeProviders.FindIndex(x => string.Equals(x.Name, p.Name, StringComparison.OrdinalIgnoreCase));

        if (idx >= 0) _claudeProviders[idx] = p;
        else
        {
            _claudeProviders.Add(p);
            idx = _claudeProviders.Count - 1;
        }

        for (int i = _claudeProviders.Count - 1; i >= 0; i--)
        {
            if (i != idx)
            {
                var other = _claudeProviders[i];
                bool sameId = !string.IsNullOrEmpty(p.Id) && !string.IsNullOrEmpty(other.Id) && string.Equals(p.Id, other.Id, StringComparison.OrdinalIgnoreCase);
                bool sameName = string.Equals(p.Name, other.Name, StringComparison.OrdinalIgnoreCase);
                if (sameId || sameName)
                {
                    _claudeProviders.RemoveAt(i);
                    if (i < idx) idx--;
                }
            }
        }
        CliStore.SaveClaude(_claudeProviders);

        var currentName = ClaudeCli.CurrentProviderName();
        var currentUrl = ClaudeCli.CurrentBaseUrl()?.TrimEnd('/');
        var isCurrent = (existing != null && (
            (!string.IsNullOrEmpty(currentName) && (string.Equals(existing.Name, currentName, StringComparison.OrdinalIgnoreCase) || string.Equals(existing.Id, currentName, StringComparison.OrdinalIgnoreCase))) ||
            (!string.IsNullOrEmpty(currentUrl) && string.Equals(existing.BaseUrl?.TrimEnd('/'), currentUrl, StringComparison.OrdinalIgnoreCase))
        )) || (
            (!string.IsNullOrEmpty(currentName) && (string.Equals(p.Name, currentName, StringComparison.OrdinalIgnoreCase) || string.Equals(p.Id, currentName, StringComparison.OrdinalIgnoreCase))) ||
            (!string.IsNullOrEmpty(currentUrl) && string.Equals(p.BaseUrl?.TrimEnd('/'), currentUrl, StringComparison.OrdinalIgnoreCase))
        );

        if (isCurrent)
        {
            var dummyRow = new ClaudeRow { P = p, IsDesktop = false };
            if (dummyRow.RequiresRouter && !LocalProxyServer.IsClaudeCliEnabled)
            {
                LocalProxyServer.SetClaudeCliEnabled(true);
            }
            try { ClaudeCli.Apply(p); } catch { }
        }

        RefreshClaude();
        ShowToast(isCurrent ? $"已保存并同步生效当前配置「{p.Name}」" : $"已保存供应商「{p.Name}」");
    }

    void OnRefreshClaude(object sender, RoutedEventArgs e) => RefreshClaude();

    void OnOpenClaudeSettings(object sender, RoutedEventArgs e) => OpenFile(ClaudeCli.SettingsPath);

    // ---------- Claude Desktop ----------

    void RefreshDesktop()
    {
        _desktopProviders = CliStore.LoadClaudeDesktop();
        var currentName = ClaudeDesktopCli.CurrentProviderName();
        var currentUrl = ClaudeDesktopCli.CurrentGatewayUrl();

        var rows = _desktopProviders.Select(p => new ClaudeRow
        {
            P = p,
            IsDesktop = true,
            IsCurrent = p.IsOfficial
                ? string.IsNullOrEmpty(currentName) && string.IsNullOrEmpty(currentUrl)
                : (!string.IsNullOrEmpty(currentName) && (string.Equals(currentName, p.Name, StringComparison.OrdinalIgnoreCase) || string.Equals(currentName, p.Id, StringComparison.OrdinalIgnoreCase))) ||
                  (!string.IsNullOrEmpty(currentUrl) && string.Equals(currentUrl.TrimEnd('/'), p.BaseUrl?.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)),
        }).ToList();

        DesktopList.ItemsSource = null;
        DesktopList.ItemsSource = rows;

        var active = rows.FirstOrDefault(r => r.IsCurrent);
        DesktopCurrentText.Text = active != null
            ? active.Name + (active.P.IsOfficial ? "" : "  (" + active.P.BaseUrl + ")")
            : (!string.IsNullOrEmpty(currentName) ? currentName : (string.IsNullOrEmpty(currentUrl) ? "官方（无自定义网关）" : "未收录的网关: " + currentUrl));

        DesktopEmpty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateRouterUI();
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
        bool autoEnabled = false;
        if (row.RequiresRouter && !LocalProxyServer.IsClaudeDesktopEnabled)
        {
            LocalProxyServer.SetClaudeDesktopEnabled(true);
            autoEnabled = true;
        }

        try { ClaudeDesktopCli.Apply(row.P); }
        catch (Exception ex) { ShowToast("应用失败：" + ex.Message, isError: true); return; }
        RefreshDesktop();
        if (LocalProxyServer.IsClaudeDesktopEnabled && !row.P.IsOfficial)
        {
            ShowToast(autoEnabled
                ? $"Claude 客户端已切换到「{row.Name}」（已自动开启本地路由）"
                : $"Claude 客户端已切换到「{row.Name}」（本地路由已接管）");
        }
        else
        {
            ShowToast($"Claude 客户端已切换到「{row.Name}」（需重启生效）");
        }
    }

    async void EditDesktop(ClaudeProvider? existing)
    {
        if (_activeProviderDialog != null && _activeProviderDialog.IsLoaded)
        {
            _activeProviderDialog.Activate();
            _activeProviderDialog.Focus();
            ShowToast("已有正在编辑的供应商窗口，请先保存或关闭该窗口");
            return;
        }

        var dlg = new ProviderDialog(ProviderDialogMode.ClaudeDesktop, existing) { Owner = this };
        _activeProviderDialog = dlg;
        dlg.Show();
        var ok = await dlg.WaitForResultAsync();
        _activeProviderDialog = null;

        if (!ok || dlg.ResultClaude == null) return;
        var p = dlg.ResultClaude;
        int idx = -1;
        if (existing != null)
        {
            idx = _desktopProviders.IndexOf(existing);
            if (idx < 0 && !string.IsNullOrEmpty(existing.Id))
                idx = _desktopProviders.FindIndex(x => string.Equals(x.Id, existing.Id, StringComparison.OrdinalIgnoreCase));
            if (idx < 0 && !string.IsNullOrEmpty(existing.Name))
                idx = _desktopProviders.FindIndex(x => string.Equals(x.Name, existing.Name, StringComparison.OrdinalIgnoreCase));
        }
        if (idx < 0 && !string.IsNullOrEmpty(p.Id))
            idx = _desktopProviders.FindIndex(x => string.Equals(x.Id, p.Id, StringComparison.OrdinalIgnoreCase));
        if (idx < 0 && !string.IsNullOrEmpty(p.Name))
            idx = _desktopProviders.FindIndex(x => string.Equals(x.Name, p.Name, StringComparison.OrdinalIgnoreCase));

        if (idx >= 0) _desktopProviders[idx] = p;
        else
        {
            _desktopProviders.Add(p);
            idx = _desktopProviders.Count - 1;
        }

        for (int i = _desktopProviders.Count - 1; i >= 0; i--)
        {
            if (i != idx)
            {
                var other = _desktopProviders[i];
                bool sameId = !string.IsNullOrEmpty(p.Id) && !string.IsNullOrEmpty(other.Id) && string.Equals(p.Id, other.Id, StringComparison.OrdinalIgnoreCase);
                bool sameName = string.Equals(p.Name, other.Name, StringComparison.OrdinalIgnoreCase);
                if (sameId || sameName)
                {
                    _desktopProviders.RemoveAt(i);
                    if (i < idx) idx--;
                }
            }
        }
        CliStore.SaveClaudeDesktop(_desktopProviders);

        var currentName = ClaudeDesktopCli.CurrentProviderName();
        var currentUrl = ClaudeDesktopCli.CurrentGatewayUrl()?.TrimEnd('/');
        var isCurrent = (existing != null && (
            (!string.IsNullOrEmpty(currentName) && (string.Equals(existing.Name, currentName, StringComparison.OrdinalIgnoreCase) || string.Equals(existing.Id, currentName, StringComparison.OrdinalIgnoreCase))) ||
            (!string.IsNullOrEmpty(currentUrl) && string.Equals(existing.BaseUrl?.TrimEnd('/'), currentUrl, StringComparison.OrdinalIgnoreCase))
        )) || (
            (!string.IsNullOrEmpty(currentName) && (string.Equals(p.Name, currentName, StringComparison.OrdinalIgnoreCase) || string.Equals(p.Id, currentName, StringComparison.OrdinalIgnoreCase))) ||
            (!string.IsNullOrEmpty(currentUrl) && string.Equals(p.BaseUrl?.TrimEnd('/'), currentUrl, StringComparison.OrdinalIgnoreCase))
        );

        if (isCurrent)
        {
            var dummyRow = new ClaudeRow { P = p, IsDesktop = true };
            if (dummyRow.RequiresRouter && !LocalProxyServer.IsClaudeDesktopEnabled)
            {
                LocalProxyServer.SetClaudeDesktopEnabled(true);
            }
            try { ClaudeDesktopCli.Apply(p); } catch { }
        }

        RefreshDesktop();
        ShowToast(isCurrent ? $"已保存并同步更新当前客户端配置「{p.Name}」（需重启 Claude 生效）" : $"已保存供应商「{p.Name}」");
    }

    async void OnRestartClaudeDesktop(object sender, RoutedEventArgs e)
    {
        ShowToast("正在重启 Claude 客户端…");
        try
        {
            await ClaudeProcess.RestartClaudeAsync();
            ShowToast("Claude 客户端已重启");
        }
        catch (Exception ex)
        {
            ShowToast("重启 Claude 失败：" + ex.Message, isError: true);
        }
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
                : string.Equals(current, p.Id, StringComparison.OrdinalIgnoreCase) ||
                  (!string.IsNullOrEmpty(current) && string.Equals(current, p.Name, StringComparison.OrdinalIgnoreCase)),
        }).ToList();

        CodexList.ItemsSource = null;
        CodexList.ItemsSource = rows;

        var active = rows.FirstOrDefault(r => r.IsCurrent);
        CodexCurrentText.Text = active != null
            ? active.Name + (active.P.IsOfficial ? "" : "  (" + active.P.BaseUrl + ")")
            : (string.IsNullOrEmpty(current) ? "官方（无自定义端点）" : "未收录的供应商: " + current);

        CodexEmpty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateRouterUI();
    }

    void UpdateRouterUI()
    {
        bool isCodex = LocalProxyServer.IsCodexEnabled;
        bool isClaudeCli = LocalProxyServer.IsClaudeCliEnabled;
        bool isClaudeDesktop = LocalProxyServer.IsClaudeDesktopEnabled;
        int activeCount = (isCodex ? 1 : 0) + (isClaudeCli ? 1 : 0) + (isClaudeDesktop ? 1 : 0);
        int port = LocalProxyServer.Port;

        var onBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x10, 0xB9, 0x81)); // Green
        var offBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x94, 0xA3, 0xB8)); // Gray

        var activeBg = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xEC, 0xFD, 0xF5)); // Soft emerald
        var activeBorder = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x10, 0xB9, 0x81)); // Vivid emerald border
        var activeFg = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x04, 0x78, 0x57)); // Deep emerald text
        var chipActiveBorder = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xA7, 0xF3, 0xD0));

        var inactiveBg = System.Windows.Media.Brushes.White;
        var inactiveBorder = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xCB, 0xD5, 0xE1));
        var inactiveFg = (System.Windows.Media.Brush)FindResource("TextDimBrush");
        var chipInactiveBg = (System.Windows.Media.Brush)FindResource("ChipBrush");
        var chipInactiveBorder = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE2, 0xE8, 0xF0));
        var chipInactiveFg = (System.Windows.Media.Brush)FindResource("TextMutedBrush");

        // Global TitleBar
        if (TitleBarRouterBtn != null)
        {
            TitleBarRouterBtn.Background = activeCount > 0 ? activeBg : inactiveBg;
            TitleBarRouterBtn.BorderBrush = activeCount > 0 ? activeBorder : inactiveBorder;
        }
        if (RouterStatusDot != null) RouterStatusDot.Fill = activeCount > 0 ? onBrush : offBrush;
        if (RouterStatusText != null)
        {
            RouterStatusText.Text = activeCount > 0 ? $"⚡ 本地路由 :{port} ({activeCount}/3)" : "本地路由: 已停用";
            RouterStatusText.Foreground = activeCount > 0 ? activeFg : inactiveFg;
            RouterStatusText.FontWeight = activeCount > 0 ? FontWeights.SemiBold : FontWeights.Normal;
        }

        // Codex
        if (CodexToolbarRouterBtn != null)
        {
            CodexToolbarRouterBtn.Background = isCodex ? activeBg : inactiveBg;
            CodexToolbarRouterBtn.BorderBrush = isCodex ? activeBorder : inactiveBorder;
        }
        if (CodexToolbarRouterDot != null) CodexToolbarRouterDot.Fill = isCodex ? onBrush : offBrush;
        if (CodexToolbarRouterText != null)
        {
            CodexToolbarRouterText.Text = isCodex ? $"⚡ 本地路由：已开启 (:{port})" : "本地路由：已停用";
            CodexToolbarRouterText.Foreground = isCodex ? activeFg : inactiveFg;
            CodexToolbarRouterText.FontWeight = isCodex ? FontWeights.SemiBold : FontWeights.Normal;
        }
        if (CodexRouterChip != null)
        {
            CodexRouterChip.Background = isCodex ? activeBg : chipInactiveBg;
            CodexRouterChip.BorderBrush = isCodex ? chipActiveBorder : chipInactiveBorder;
        }
        if (CodexRouterDot != null) CodexRouterDot.Fill = isCodex ? onBrush : offBrush;
        if (CodexRouterChipText != null)
        {
            CodexRouterChipText.Foreground = isCodex ? activeFg : chipInactiveFg;
            CodexRouterChipText.FontWeight = isCodex ? FontWeights.SemiBold : FontWeights.Normal;
            var active = CodexCli.GetActiveProvider();
            if (active != null && !active.IsOfficial)
            {
                var wire = (active.WireApi ?? "").Trim().ToLowerInvariant();
                var modeDesc = wire == "chat" ? "协议转译 (Chat↔Responses)" : "透明转发";
                CodexRouterChipText.Text = isCodex
                    ? $"本地路由已接管 (:{port} · {modeDesc} · 免重启)"
                    : "直连直通模式（未开启路由）";
            }
            else
            {
                CodexRouterChipText.Text = isCodex ? $"本地路由待命中 (:{port})" : "直连直通模式";
            }
        }

        // Claude CLI
        if (ClaudeToolbarRouterBtn != null)
        {
            ClaudeToolbarRouterBtn.Background = isClaudeCli ? activeBg : inactiveBg;
            ClaudeToolbarRouterBtn.BorderBrush = isClaudeCli ? activeBorder : inactiveBorder;
        }
        if (ClaudeToolbarRouterDot != null) ClaudeToolbarRouterDot.Fill = isClaudeCli ? onBrush : offBrush;
        if (ClaudeToolbarRouterText != null)
        {
            ClaudeToolbarRouterText.Text = isClaudeCli ? $"⚡ 本地路由：已开启 (:{port})" : "本地路由：已停用";
            ClaudeToolbarRouterText.Foreground = isClaudeCli ? activeFg : inactiveFg;
            ClaudeToolbarRouterText.FontWeight = isClaudeCli ? FontWeights.SemiBold : FontWeights.Normal;
        }
        if (ClaudeRouterChip != null)
        {
            ClaudeRouterChip.Background = isClaudeCli ? activeBg : chipInactiveBg;
            ClaudeRouterChip.BorderBrush = isClaudeCli ? chipActiveBorder : chipInactiveBorder;
        }
        if (ClaudeRouterDot != null) ClaudeRouterDot.Fill = isClaudeCli ? onBrush : offBrush;
        if (ClaudeRouterChipText != null)
        {
            ClaudeRouterChipText.Foreground = isClaudeCli ? activeFg : chipInactiveFg;
            ClaudeRouterChipText.FontWeight = isClaudeCli ? FontWeights.SemiBold : FontWeights.Normal;
            var active = ClaudeCli.GetActiveProvider();
            if (active != null && !active.IsOfficial)
            {
                ClaudeRouterChipText.Text = isClaudeCli
                    ? $"本地路由已接管 (:{port} · 透明转发)"
                    : "直连直通模式（未开启路由）";
            }
            else
            {
                ClaudeRouterChipText.Text = isClaudeCli ? $"本地路由待命中 (:{port})" : "直连直通模式";
            }
        }

        // Claude Desktop
        if (DesktopToolbarRouterBtn != null)
        {
            DesktopToolbarRouterBtn.Background = isClaudeDesktop ? activeBg : inactiveBg;
            DesktopToolbarRouterBtn.BorderBrush = isClaudeDesktop ? activeBorder : inactiveBorder;
        }
        if (DesktopToolbarRouterDot != null) DesktopToolbarRouterDot.Fill = isClaudeDesktop ? onBrush : offBrush;
        if (DesktopToolbarRouterText != null)
        {
            DesktopToolbarRouterText.Text = isClaudeDesktop ? $"⚡ 本地路由：已开启 (:{port})" : "本地路由：已停用";
            DesktopToolbarRouterText.Foreground = isClaudeDesktop ? activeFg : inactiveFg;
            DesktopToolbarRouterText.FontWeight = isClaudeDesktop ? FontWeights.SemiBold : FontWeights.Normal;
        }
        if (DesktopRouterChip != null)
        {
            DesktopRouterChip.Background = isClaudeDesktop ? activeBg : chipInactiveBg;
            DesktopRouterChip.BorderBrush = isClaudeDesktop ? chipActiveBorder : chipInactiveBorder;
        }
        if (DesktopRouterDot != null) DesktopRouterDot.Fill = isClaudeDesktop ? onBrush : offBrush;
        if (DesktopRouterChipText != null)
        {
            DesktopRouterChipText.Foreground = isClaudeDesktop ? activeFg : chipInactiveFg;
            DesktopRouterChipText.FontWeight = isClaudeDesktop ? FontWeights.SemiBold : FontWeights.Normal;
            var active = ClaudeDesktopCli.GetActiveProvider();
            if (active != null && !active.IsOfficial)
            {
                DesktopRouterChipText.Text = isClaudeDesktop
                    ? $"本地路由已接管 (:{port} · 透明转发)"
                    : "直连直通模式（未开启路由）";
            }
            else
            {
                DesktopRouterChipText.Text = isClaudeDesktop ? $"本地路由待命中 (:{port})" : "直连直通模式";
            }
        }
    }

    void OnToggleCodexProxy(object sender, RoutedEventArgs e)
    {
        var newState = !LocalProxyServer.IsCodexEnabled;
        LocalProxyServer.SetCodexEnabled(newState);
        RefreshCodex();
        if (newState)
        {
            ShowToast($"Codex 本地路由已开启 (端口 :{LocalProxyServer.Port})，已接管 Codex 请求");
        }
        else
        {
            ShowToast("Codex 本地路由已停用，Codex 已切回直连模式");
        }
    }

    void OnToggleClaudeCliProxy(object sender, RoutedEventArgs e)
    {
        var newState = !LocalProxyServer.IsClaudeCliEnabled;
        LocalProxyServer.SetClaudeCliEnabled(newState);
        RefreshClaude();
        if (newState)
        {
            ShowToast($"Claude CLI 本地路由已开启 (端口 :{LocalProxyServer.Port})，已接管 Claude CLI 请求");
        }
        else
        {
            ShowToast("Claude CLI 本地路由已停用，Claude CLI 已切回直连模式");
        }
    }

    void OnToggleClaudeDesktopProxy(object sender, RoutedEventArgs e)
    {
        var newState = !LocalProxyServer.IsClaudeDesktopEnabled;
        LocalProxyServer.SetClaudeDesktopEnabled(newState);
        RefreshDesktop();
        if (newState)
        {
            ShowToast($"Claude 客户端本地路由已开启 (端口 :{LocalProxyServer.Port})，已接管 Claude 客户端请求");
        }
        else
        {
            ShowToast("Claude 客户端本地路由已停用，Claude 客户端已切回直连模式");
        }
    }

    void OnToggleProxyServer(object sender, RoutedEventArgs e)
    {
        var newState = !LocalProxyServer.IsEnabled;
        LocalProxyServer.SetEnabled(newState);
        RefreshCodex();
        RefreshClaude();
        RefreshDesktop();
        if (newState)
        {
            ShowToast($"全部本地路由已开启 (端口 :{LocalProxyServer.Port})");
        }
        else
        {
            ShowToast("全部本地路由已停用，各应用已切回直连模式");
        }
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
        bool autoEnabled = false;
        if (row.RequiresRouter && !LocalProxyServer.IsCodexEnabled)
        {
            LocalProxyServer.SetCodexEnabled(true);
            autoEnabled = true;
        }

        try { CodexCli.Apply(row.P); }
        catch (Exception ex) { ShowToast("应用失败：" + ex.Message, isError: true); return; }
        RefreshCodex();
        if (LocalProxyServer.IsCodexEnabled && !row.P.IsOfficial)
        {
            ShowToast(autoEnabled
                ? $"Codex 已切换到「{row.Name}」（已自动开启本地路由转译）"
                : $"Codex 已热切换到「{row.Name}」（本地路由已接管，无需重启客户端）");
        }
        else
        {
            ShowToast($"Codex 已切换到「{row.Name}」（若客户端已开，请重启生效）");
        }
    }

    async void EditCodex(CodexProvider? existing)
    {
        if (_activeProviderDialog != null && _activeProviderDialog.IsLoaded)
        {
            _activeProviderDialog.Activate();
            _activeProviderDialog.Focus();
            ShowToast("已有正在编辑的供应商窗口，请先保存或关闭该窗口");
            return;
        }

        var dlg = new ProviderDialog(ProviderDialogMode.Codex, existing) { Owner = this };
        _activeProviderDialog = dlg;
        dlg.Show();
        var ok = await dlg.WaitForResultAsync();
        _activeProviderDialog = null;

        if (!ok || dlg.ResultCodex == null) return;
        var p = dlg.ResultCodex;
        int idx = -1;
        if (existing != null)
        {
            idx = _codexProviders.IndexOf(existing);
            if (idx < 0 && !string.IsNullOrEmpty(existing.Id))
                idx = _codexProviders.FindIndex(x => string.Equals(x.Id, existing.Id, StringComparison.OrdinalIgnoreCase));
            if (idx < 0 && !string.IsNullOrEmpty(existing.Name))
                idx = _codexProviders.FindIndex(x => string.Equals(x.Name, existing.Name, StringComparison.OrdinalIgnoreCase));
        }
        if (idx < 0 && !string.IsNullOrEmpty(p.Id))
            idx = _codexProviders.FindIndex(x => string.Equals(x.Id, p.Id, StringComparison.OrdinalIgnoreCase));
        if (idx < 0 && !string.IsNullOrEmpty(p.Name))
            idx = _codexProviders.FindIndex(x => string.Equals(x.Name, p.Name, StringComparison.OrdinalIgnoreCase));

        if (idx >= 0) _codexProviders[idx] = p;
        else
        {
            _codexProviders.Add(p);
            idx = _codexProviders.Count - 1;
        }

        for (int i = _codexProviders.Count - 1; i >= 0; i--)
        {
            if (i != idx)
            {
                var other = _codexProviders[i];
                bool sameId = !string.IsNullOrEmpty(p.Id) && !string.IsNullOrEmpty(other.Id) && string.Equals(p.Id, other.Id, StringComparison.OrdinalIgnoreCase);
                bool sameName = string.Equals(p.Name, other.Name, StringComparison.OrdinalIgnoreCase);
                if (sameId || sameName)
                {
                    _codexProviders.RemoveAt(i);
                    if (i < idx) idx--;
                }
            }
        }
        CliStore.SaveCodex(_codexProviders);

        var currentId = CodexCli.CurrentProviderId();
        var isCurrent = (existing != null && (
            (!string.IsNullOrEmpty(currentId) && (string.Equals(existing.Id, currentId, StringComparison.OrdinalIgnoreCase) || string.Equals(existing.Name, currentId, StringComparison.OrdinalIgnoreCase))) ||
            (existing.IsOfficial && string.IsNullOrEmpty(currentId))
        )) || (
            (!string.IsNullOrEmpty(currentId) && (string.Equals(p.Id, currentId, StringComparison.OrdinalIgnoreCase) || string.Equals(p.Name, currentId, StringComparison.OrdinalIgnoreCase))) ||
            (p.IsOfficial && string.IsNullOrEmpty(currentId))
        );

        if (isCurrent)
        {
            var dummyRow = new CodexRow { P = p };
            if (dummyRow.RequiresRouter && !LocalProxyServer.IsCodexEnabled)
            {
                LocalProxyServer.SetCodexEnabled(true);
            }
            try { CodexCli.Apply(p); } catch { }
        }

        RefreshCodex();
        ShowToast(isCurrent ? $"已保存并同步生效当前配置「{p.Name}」" : $"已保存供应商「{p.Name}」");
    }


    async void OnRestartCodex(object sender, RoutedEventArgs e)
    {
        ShowToast("正在重启 Codex 客户端…");
        try
        {
            await CodexProcess.RestartCodexAsync();
            ShowToast("Codex 客户端已重启");
        }
        catch (Exception ex)
        {
            ShowToast("重启 Codex 失败：" + ex.Message, isError: true);
        }
    }

    void OnOpenCodexConfig(object sender, RoutedEventArgs e) => OpenFile(CodexCli.ConfigPath);


    // ---------- OpenCode ----------

    public class OpenCodeRow
    {
        public OpenCodeProvider P { get; init; } = null!;
        public bool IsInConfig { get; init; }
        public bool IsDefault { get; init; }
        public string Name => string.IsNullOrEmpty(P.Name) ? P.Id : P.Name;
        public string Initial
        {
            get
            {
                var s = string.IsNullOrWhiteSpace(P.Name) ? P.Id : P.Name;
                return string.IsNullOrWhiteSpace(s) ? "O" : s.Trim().Substring(0, 1).ToUpperInvariant();
            }
        }
        public string SubText => string.Join("   ·   ",
            new string?[]
            {
                string.IsNullOrEmpty(P.BaseUrl) ? null : P.BaseUrl,
                P.Npm,
                ModelsSummary(),
            }.Where(s => !string.IsNullOrEmpty(s)));

        public Visibility PoolBadgeVisibility => IsInConfig ? Visibility.Visible : Visibility.Collapsed;
        public Visibility AddBtnVisibility => !IsInConfig ? Visibility.Visible : Visibility.Collapsed;
        public Visibility RemoveBtnVisibility => IsInConfig ? Visibility.Visible : Visibility.Collapsed;

        string ModelsSummary()
        {
            try
            {
                if (P.CustomModels != null && P.CustomModels.Count > 0)
                    return P.CustomModels.Count + " 个模型";
                if (string.IsNullOrWhiteSpace(P.ModelsJson)) return "未配置模型";
                if (JsonNode.Parse(P.ModelsJson) is JsonObject models)
                {
                    var count = models.Count;
                    return count == 0 ? "未配置模型" : count + " 个模型";
                }
                return "未配置模型";
            }
            catch
            {
                return "未配置模型";
            }
        }
    }

    void RefreshOpencode()
    {
        _openCodeProviders = CliStore.LoadOpencode();
        var liveIds = new HashSet<string>(OpenCodeCli.ProviderIds(), StringComparer.OrdinalIgnoreCase);

        int inPoolCount = _openCodeProviders.Count(p => liveIds.Contains(p.Id));
        if (OcPoolCountText != null)
            OcPoolCountText.Text = $"{inPoolCount} / {_openCodeProviders.Count} 个供应商";

        var rows = _openCodeProviders.Select(p =>
        {
            bool inCfg = liveIds.Contains(p.Id);
            return new OpenCodeRow
            {
                P = p,
                IsInConfig = inCfg,
            };
        }).OrderByDescending(r => r.IsInConfig).ThenBy(r => r.Name).ToList();

        OcList.ItemsSource = null;
        OcList.ItemsSource = rows;
        OcEmpty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    void OnAddOpencode(object sender, RoutedEventArgs e) => EditOpencode(null);

    void OnRowAddOc(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not OpenCodeRow row) return;
        try
        {
            OpenCodeCli.SaveProvider(row.P);
            RefreshOpencode();
            ShowToast($"已将「{row.Name}」加入 OpenCode 配置池");
        }
        catch (Exception ex)
        {
            ShowToast("添加失败：" + ex.Message, isError: true);
        }
    }

    void OnRowRemoveOc(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not OpenCodeRow row) return;
        try
        {
            OpenCodeCli.DeleteProvider(row.P.Id);
            RefreshOpencode();
            ShowToast($"已从 OpenCode 配置池移除「{row.Name}」");
        }
        catch (Exception ex)
        {
            ShowToast("移除失败：" + ex.Message, isError: true);
        }
    }

    void OnCardEditOc(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is OpenCodeRow row) EditOpencode(row.P);
    }

    void OnCardDeleteOc(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not OpenCodeRow row) return;
        if (!Confirm($"彻底删除供应商「{row.Name}」？\n（将从列表及 opencode.json 中移除）")) return;
        try
        {
            _openCodeProviders.RemoveAll(x => string.Equals(x.Id, row.P.Id, StringComparison.OrdinalIgnoreCase) || string.Equals(x.Name, row.P.Name, StringComparison.OrdinalIgnoreCase));
            CliStore.SaveOpencode(_openCodeProviders);
            OpenCodeCli.DeleteProvider(row.P.Id);
            var curr = OpenCodeCli.CurrentModel();
            if (!string.IsNullOrEmpty(curr) && (string.Equals(curr, row.P.Id, StringComparison.OrdinalIgnoreCase) || curr.StartsWith(row.P.Id + "/", StringComparison.Ordinal)))
            {
                OpenCodeCli.ClearDefaultModel();
            }
            RefreshOpencode();
            ShowToast($"已删除供应商「{row.Name}」");
        }
        catch (Exception ex) { ShowToast("删除失败：" + ex.Message, isError: true); }
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
                case CopyTarget.Pi:
                    UpsertPi(ProviderConvert.ToPi(row.P));
                    done.Add("Pi");
                    break;
            }
        }
        RefreshClaude(); RefreshDesktop(); RefreshCodex(); RefreshPi();
        ShowToast($"已复制「{row.P.Id}」到: {string.Join("、", done)}");
    }

    async void EditOpencode(OpenCodeProvider? existing)
    {
        if (_activeProviderDialog != null && _activeProviderDialog.IsLoaded)
        {
            _activeProviderDialog.Activate();
            _activeProviderDialog.Focus();
            ShowToast("已有正在编辑的供应商窗口，请先保存或关闭该窗口");
            return;
        }

        var dlg = new ProviderDialog(ProviderDialogMode.OpenCode, existing) { Owner = this };
        _activeProviderDialog = dlg;
        dlg.Show();
        var ok = await dlg.WaitForResultAsync();
        _activeProviderDialog = null;

        if (!ok || dlg.ResultOpenCode == null) return;
        var p = dlg.ResultOpenCode;
        try
        {
            if (existing != null && !string.Equals(existing.Id, p.Id, StringComparison.OrdinalIgnoreCase))
            {
                _openCodeProviders.RemoveAll(x => string.Equals(x.Id, existing.Id, StringComparison.OrdinalIgnoreCase));
                OpenCodeCli.DeleteProvider(existing.Id);
            }

            UpsertOpencode(p);

            var liveIds = new HashSet<string>(OpenCodeCli.ProviderIds(), StringComparer.OrdinalIgnoreCase);
            if ((existing != null && liveIds.Contains(existing.Id)) || liveIds.Contains(p.Id))
            {
                OpenCodeCli.SaveProvider(p);
            }

            RefreshOpencode();
            ShowToast($"已保存供应商「{p.Id}」");
        }
        catch (Exception ex) { ShowToast("保存失败：" + ex.Message, isError: true); }
    }

    void OnRefreshOpencode(object sender, RoutedEventArgs e)
    {
        RefreshOpencode();
        ShowToast($"已同步并刷新 OpenCode 配置 (共 {_openCodeProviders.Count} 个供应商)");
    }

    void OnOpenOcConfig(object sender, RoutedEventArgs e) => OpenFile(OpenCodeCli.ConfigPath);

    void UpsertOpencode(OpenCodeProvider p)
    {
        var i = _openCodeProviders.FindIndex(x => (!string.IsNullOrEmpty(p.Id) && string.Equals(x.Id, p.Id, StringComparison.OrdinalIgnoreCase)) || string.Equals(x.Name, p.Name, StringComparison.OrdinalIgnoreCase));
        if (i >= 0) _openCodeProviders[i] = p; else _openCodeProviders.Add(p);
        _openCodeProviders = CliStore.DeduplicateOpenCode(_openCodeProviders);
        CliStore.SaveOpencode(_openCodeProviders);
    }

    // ---------- Pi ----------

    public class PiRow
    {
        public PiProvider P { get; init; } = null!;
        public bool IsInConfig { get; init; }
        public bool IsDefault { get; init; }
        public string Name => string.IsNullOrEmpty(P.Name) ? P.Id : P.Name;
        public string Initial
        {
            get
            {
                var s = string.IsNullOrWhiteSpace(P.Name) ? P.Id : P.Name;
                return string.IsNullOrWhiteSpace(s) ? "P" : s.Trim().Substring(0, 1).ToUpperInvariant();
            }
        }
        public string SubText => string.Join("   ·   ",
            new string?[]
            {
                string.IsNullOrEmpty(P.BaseUrl) ? null : P.BaseUrl,
                P.Api,
                ModelsSummary(),
            }.Where(s => !string.IsNullOrEmpty(s)));

        public Visibility PoolBadgeVisibility => IsInConfig ? Visibility.Visible : Visibility.Collapsed;
        public Visibility AddBtnVisibility => !IsInConfig ? Visibility.Visible : Visibility.Collapsed;
        public Visibility RemoveBtnVisibility => IsInConfig ? Visibility.Visible : Visibility.Collapsed;

        string ModelsSummary()
        {
            try
            {
                if (P.CustomModels != null && P.CustomModels.Count > 0)
                    return P.CustomModels.Count + " 个模型";
                if (string.IsNullOrWhiteSpace(P.ModelsJson)) return "未配置模型";
                if (JsonNode.Parse(P.ModelsJson) is JsonArray arr)
                    return arr.Count == 0 ? "未配置模型" : arr.Count + " 个模型";
                if (JsonNode.Parse(P.ModelsJson) is JsonObject obj)
                    return obj.Count == 0 ? "未配置模型" : obj.Count + " 个模型";
                return "未配置模型";
            }
            catch
            {
                return "未配置模型";
            }
        }
    }

    void RefreshPi()
    {
        _piProviders = CliStore.LoadPiProviders();
        var liveIds = new HashSet<string>(PiCli.ProviderIds(), StringComparer.OrdinalIgnoreCase);

        int inPoolCount = _piProviders.Count(p => liveIds.Contains(p.Id));
        if (PiPoolCountText != null)
            PiPoolCountText.Text = $"{inPoolCount} / {_piProviders.Count} 个供应商";

        var rows = _piProviders.Select(p =>
        {
            bool inCfg = liveIds.Contains(p.Id);
            return new PiRow
            {
                P = p,
                IsInConfig = inCfg,
            };
        }).OrderByDescending(r => r.IsInConfig).ThenBy(r => r.Name).ToList();

        PiList.ItemsSource = null;
        PiList.ItemsSource = rows;
        PiEmpty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    void OnAddPi(object sender, RoutedEventArgs e) => EditPi(null);

    void OnRowAddPi(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not PiRow row) return;
        try
        {
            PiCli.SaveProvider(row.P);
            RefreshPi();
            ShowToast($"已将「{row.Name}」加入 Pi 配置池");
        }
        catch (Exception ex)
        {
            ShowToast("添加失败：" + ex.Message, isError: true);
        }
    }

    void OnRowRemovePi(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not PiRow row) return;
        try
        {
            PiCli.DeleteProvider(row.P.Id);
            RefreshPi();
            ShowToast($"已从 Pi 配置池移除「{row.Name}」");
        }
        catch (Exception ex)
        {
            ShowToast("移除失败：" + ex.Message, isError: true);
        }
    }

    void OnCardEditPi(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is PiRow row) EditPi(row.P);
    }

    void OnCardDeletePi(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not PiRow row) return;
        if (!Confirm($"彻底删除供应商「{row.Name}」？\n（将从列表及 models.json 中移除）")) return;
        try
        {
            _piProviders.RemoveAll(x => string.Equals(x.Id, row.P.Id, StringComparison.OrdinalIgnoreCase) || string.Equals(x.Name, row.P.Name, StringComparison.OrdinalIgnoreCase));
            CliStore.SavePiProviders(_piProviders);
            PiCli.DeleteProvider(row.P.Id);
            RefreshPi();
            ShowToast($"已删除供应商「{row.Name}」");
        }
        catch (Exception ex) { ShowToast("删除失败：" + ex.Message, isError: true); }
    }

    void OnCardCopyPi(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not PiRow row) return;
        var dlg = new CopyTargetsDialog(row.Name, CopyTarget.Pi) { Owner = this };
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
                case CopyTarget.OpenCode:
                    UpsertOpencode(ProviderConvert.ToOpencode(row.P));
                    done.Add("OpenCode");
                    break;
            }
        }
        RefreshClaude(); RefreshDesktop(); RefreshCodex(); RefreshOpencode();
        ShowToast($"已复制「{row.Name}」到: {string.Join("、", done)}");
    }

    async void EditPi(PiProvider? existing)
    {
        if (_activeProviderDialog != null && _activeProviderDialog.IsLoaded)
        {
            _activeProviderDialog.Activate();
            _activeProviderDialog.Focus();
            ShowToast("已有正在编辑的供应商窗口，请先保存或关闭该窗口");
            return;
        }

        var dlg = new ProviderDialog(ProviderDialogMode.Pi, existing) { Owner = this };
        _activeProviderDialog = dlg;
        dlg.Show();
        var ok = await dlg.WaitForResultAsync();
        _activeProviderDialog = null;

        if (!ok || dlg.ResultPi == null) return;
        var p = dlg.ResultPi;
        try
        {
            if (existing != null && !string.Equals(existing.Id, p.Id, StringComparison.OrdinalIgnoreCase))
            {
                _piProviders.RemoveAll(x => string.Equals(x.Id, existing.Id, StringComparison.OrdinalIgnoreCase));
                PiCli.DeleteProvider(existing.Id);
            }

            UpsertPi(p);

            var liveIds = new HashSet<string>(PiCli.ProviderIds(), StringComparer.OrdinalIgnoreCase);
            if ((existing != null && liveIds.Contains(existing.Id)) || liveIds.Contains(p.Id))
            {
                PiCli.SaveProvider(p);
            }

            RefreshPi();
            ShowToast($"已保存供应商「{p.Name ?? p.Id}」");
        }
        catch (Exception ex) { ShowToast("保存失败：" + ex.Message, isError: true); }
    }

    void OnRefreshPi(object sender, RoutedEventArgs e)
    {
        RefreshPi();
        ShowToast($"已同步并刷新 Pi 配置 (共 {_piProviders.Count} 个供应商)");
    }

    void OnOpenPiModels(object sender, RoutedEventArgs e) => OpenFile(PiCli.ModelsPath);

    void OnOpenPiDir(object sender, RoutedEventArgs e)
    {
        var dir = Path.GetDirectoryName(PiCli.AuthPath);
        if (dir == null || !Directory.Exists(dir)) { ShowToast("未找到 Pi 目录", isError: true); return; }
        Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
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
            ShowToast($"已抓取快照 {name}");
        }
        catch (Exception ex) { ShowToast("抓取失败：" + ex.Message, isError: true); }
    }

    void UpsertPi(PiProvider p)
    {
        var i = _piProviders.FindIndex(x => (!string.IsNullOrEmpty(p.Id) && string.Equals(x.Id, p.Id, StringComparison.OrdinalIgnoreCase)) || string.Equals(x.Name, p.Name, StringComparison.OrdinalIgnoreCase));
        if (i >= 0) _piProviders[i] = p; else _piProviders.Add(p);
        _piProviders = CliStore.DeduplicatePi(_piProviders);
        CliStore.SavePiProviders(_piProviders);
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
                case CopyTarget.OpenCode:
                    UpsertOpencode(ProviderConvert.ToOpencode(src));
                    done.Add("OpenCode");
                    break;
                case CopyTarget.Pi:
                    UpsertPi(ProviderConvert.ToPi(src));
                    done.Add("Pi");
                    break;
            }
        }
        RefreshClaude(); RefreshDesktop(); RefreshCodex(); RefreshOpencode(); RefreshPi();
        ShowToast($"已复制「{src.Name}」到: {string.Join("、", done)}");
    }

    void CopyCodexProvider(CodexProvider src)
    {
        var dlg = new CopyTargetsDialog(src.Name, CopyTarget.Codex) { Owner = this };
        if (dlg.ShowDialog() != true) return;

        var done = new List<string>();
        foreach (var t in dlg.Targets)
        {
            switch (t)
            {
                case CopyTarget.ClaudeCli:
                    UpsertClaude(ProviderConvert.ToClaude(src));
                    done.Add("Claude CLI");
                    break;
                case CopyTarget.ClaudeDesktop:
                    UpsertDesktop(ProviderConvert.ToClaude(src));
                    done.Add("Claude 客户端");
                    break;
                case CopyTarget.Codex:
                    UpsertCodex(src);
                    done.Add("Codex");
                    break;
                case CopyTarget.OpenCode:
                    UpsertOpencode(ProviderConvert.ToOpencode(src));
                    done.Add("OpenCode");
                    break;
                case CopyTarget.Pi:
                    UpsertPi(ProviderConvert.ToPi(src));
                    done.Add("Pi");
                    break;
            }
        }
        RefreshClaude(); RefreshDesktop(); RefreshCodex(); RefreshOpencode(); RefreshPi();
        ShowToast($"已复制「{src.Name}」到: {string.Join("、", done)}");
    }

    void UpsertClaude(ClaudeProvider p)
    {
        var i = _claudeProviders.FindIndex(x => (!string.IsNullOrEmpty(p.Id) && string.Equals(x.Id, p.Id, StringComparison.OrdinalIgnoreCase)) || string.Equals(x.Name, p.Name, StringComparison.OrdinalIgnoreCase));
        if (i >= 0) _claudeProviders[i] = p; else _claudeProviders.Add(p);
        _claudeProviders = CliStore.DeduplicateClaude(_claudeProviders);
        CliStore.SaveClaude(_claudeProviders);
    }

    void UpsertDesktop(ClaudeProvider p)
    {
        var i = _desktopProviders.FindIndex(x => (!string.IsNullOrEmpty(p.Id) && string.Equals(x.Id, p.Id, StringComparison.OrdinalIgnoreCase)) || string.Equals(x.Name, p.Name, StringComparison.OrdinalIgnoreCase));
        if (i >= 0) _desktopProviders[i] = p; else _desktopProviders.Add(p);
        _desktopProviders = CliStore.DeduplicateClaude(_desktopProviders);
        CliStore.SaveClaudeDesktop(_desktopProviders);
    }

    void UpsertCodex(CodexProvider p)
    {
        var i = _codexProviders.FindIndex(x => (!string.IsNullOrEmpty(p.Id) && string.Equals(x.Id, p.Id, StringComparison.OrdinalIgnoreCase)) || string.Equals(x.Name, p.Name, StringComparison.OrdinalIgnoreCase));
        if (i >= 0) _codexProviders[i] = p; else _codexProviders.Add(p);
        _codexProviders = CliStore.DeduplicateCodex(_codexProviders);
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
                RefreshClaude(); RefreshDesktop(); RefreshCodex(); RefreshOpencode(); RefreshPi();
                ShowToast("已从 cc-switch 导入供应商");
            }
        }
        catch (Exception ex) { ShowToast("读取 cc-switch 数据失败：" + ex.Message, isError: true); return; }
        RefreshClaude(); RefreshDesktop(); RefreshCodex(); RefreshOpencode(); RefreshPi();
    }

    // ---------- Window Controls & Toast ----------

    void OnCardsPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is ScrollViewer sv)
        {
            sv.ScrollToVerticalOffset(sv.VerticalOffset - e.Delta);
            e.Handled = true;
        }
    }

    void InitTrayIcon()
    {
        try
        {
            _notifyIcon = new System.Windows.Forms.NotifyIcon
            {
                Text = "APISwitch",
                Visible = true
            };

            var exePath = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exePath) && File.Exists(exePath))
            {
                _notifyIcon.Icon = System.Drawing.Icon.ExtractAssociatedIcon(exePath);
            }
            if (_notifyIcon.Icon == null)
            {
                var icoPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "app.ico");
                if (File.Exists(icoPath))
                    _notifyIcon.Icon = new System.Drawing.Icon(icoPath);
                else
                    _notifyIcon.Icon = System.Drawing.SystemIcons.Application;
            }

            _notifyIcon.MouseClick += (_, e) =>
            {
                if (e.Button == System.Windows.Forms.MouseButtons.Left)
                {
                    RestoreFromTray();
                }
            };
            _notifyIcon.DoubleClick += (_, _) => RestoreFromTray();

            _notifyIcon.ContextMenuStrip = ModernTrayMenu.Create(
                onShow: RestoreFromTray,
                onRefresh: () => Dispatcher.Invoke(() => _ = RefreshAllAgQuotasAsync(silent: false)),
                onLaunchIde: () => Dispatcher.Invoke(() =>
                {
                    try { AgProcess.StartIde(); }
                    catch (Exception ex) { ShowToast("启动 IDE 失败：" + ex.Message, isError: true); }
                }),
                onExit: ExitApp,
                version: AppVersionText?.Text ?? "v0.1.1"
            );
        }
        catch { }
    }

    void MinimizeToTray()
    {
        WindowState = WindowState.Minimized;
        Hide();
        ShowInTaskbar = false;
        _agQuotaTimer?.Stop();
    }

    public void RestoreAndActivate()
    {
        if (!IsVisible)
        {
            Show();
        }
        ShowInTaskbar = true;
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
        OnForegroundEntered();
    }

    void RestoreFromTray() => RestoreAndActivate();

    void ExitApp()
    {
        _isExiting = true;
        if (_notifyIcon != null)
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _notifyIcon = null;
        }
        System.Windows.Application.Current.Shutdown();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_isExiting)
        {
            e.Cancel = true;
            MinimizeToTray();
            return;
        }
        base.OnClosing(e);
    }

    void OnMinWindow(object sender, RoutedEventArgs e) => MinimizeToTray();

    void OnMaxWindow(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    void OnCloseWindow(object sender, RoutedEventArgs e) => MinimizeToTray();

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

    // ---------- Update Management ----------

    async void OnCheckUpdateClick(object sender, RoutedEventArgs e)
    {
        if (_latestUpdate != null && _latestUpdate.HasUpdate)
        {
            new UpdateDialog(_latestUpdate) { Owner = this }.ShowDialog();
            return;
        }

        SetBusy("正在检查最新版本…");
        try
        {
            var info = await UpdateService.CheckForUpdatesAsync();
            ClearBusy();

            if (info == null)
            {
                ShowToast("检查更新失败，请确认网络连接或稍后重试", isError: true);
                return;
            }

            _latestUpdate = info;
            if (info.HasUpdate)
            {
                UpdateBadge.Visibility = Visibility.Visible;
                new UpdateDialog(info) { Owner = this }.ShowDialog();
            }
            else
            {
                UpdateBadge.Visibility = Visibility.Collapsed;
                ShowToast($"当前已是最新版本 ({info.CurrentVersion})");
            }
        }
        catch (Exception ex)
        {
            ClearBusy();
            ShowToast("检查更新异常：" + ex.Message, isError: true);
        }
    }

    async Task CheckUpdateSilentAsync()
    {
        try
        {
            await Task.Delay(2500);
            var info = await UpdateService.CheckForUpdatesAsync();
            if (info != null && info.HasUpdate)
            {
                _latestUpdate = info;
                Dispatcher.Invoke(() =>
                {
                    UpdateBadge.Visibility = Visibility.Visible;
                    ShowToast($"⚡ 发现新版本 {info.LatestVersion}，点击右上角版本号查看更新");
                });
            }
        }
        catch
        {
            // 静默模式忽略网络波动
        }
    }
}


using System.Collections.ObjectModel;
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
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MediaColor = System.Windows.Media.Color;
using WpfPoint = System.Windows.Point;
using APISwitch.Dialogs;
using APISwitch.Models;
using APISwitch.Services;

namespace APISwitch;

public partial class MainWindow
{
    public class CodexRow
    {
        public CodexProvider P { get; init; } = null!;
        public bool IsCurrent { get; init; }
        public string Name => P.Name;
        public string Initial => Name.Length > 0 ? Name.Substring(0, 1).ToUpperInvariant() : "?";
        public string BaseUrl => P.IsOfficial ? I18nService.T("Common.OfficialNoEndpoint") : (string.IsNullOrEmpty(P.BaseUrl) ? "—" : P.BaseUrl!);
        public string WireApi => P.IsOfficial ? "—" : P.WireApi;

        public bool RequiresRouter => !P.IsOfficial && (
            string.Equals(P.WireApi, "chat", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(P.WireApi, "anthropic", StringComparison.OrdinalIgnoreCase) ||
            (P.ExtraOptions != null && P.ExtraOptions.TryGetValue("require_proxy", out var rp) && bool.TryParse(rp, out var b) && b));

        public Visibility RouterBadgeVisibility => RequiresRouter ? Visibility.Visible : Visibility.Collapsed;

        public bool IsRouterActive => LocalProxyServer.IsCodexEnabled;

        public string RouterBadgeText => IsRouterActive ? I18nService.T("Common.NeedRouterOn") : I18nService.T("Common.NeedRouterOff");

        public string RouterBadgeTooltip => IsRouterActive
            ? I18nService.F("Row.TipCodexActiveFmt", P.WireApi == "chat" ? "Chat Completions" : (P.WireApi == "anthropic" ? "Anthropic Messages" : P.WireApi))
            : I18nService.F("Row.TipCodexInactiveFmt", P.WireApi == "chat" ? "Chat Completions" : (P.WireApi == "anthropic" ? "Anthropic Messages" : P.WireApi));

        public System.Windows.Media.Brush RouterBadgeBackground => IsRouterActive
            ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xEC, 0xFD, 0xF5))
            : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFE, 0xF2, 0xF2));

        public System.Windows.Media.Brush RouterBadgeBorderBrush => IsRouterActive
            ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xA7, 0xF3, 0xD0))
            : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFC, 0xA5, 0xA5));

        public System.Windows.Media.Brush RouterBadgeForeground => IsRouterActive
            ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x04, 0x77, 0x58))
            : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xDC, 0x26, 0x26));

        public string SubText
        {
            get
            {
                if (P.IsOfficial) return I18nService.T("Common.OfficialNoEndpoint");
                string wireDesc = P.WireApi switch
                {
                    "chat" => I18nService.T("Row.SubChatNeedsTranslate"),
                    "anthropic" => I18nService.T("Row.SubAnthropicNeedsTranslate"),
                    "responses" => I18nService.T("Row.SubResponsesNative"),
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

        _codexCollection.Clear();
        var seenCodex = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in rows)
        {
            var key = (!string.IsNullOrEmpty(r.P.Id) ? r.P.Id : r.P.Name).Trim();
            if (seenCodex.Add(key))
            {
                _codexCollection.Add(r);
            }
        }
        if (CodexList.ItemsSource != _codexCollection) CodexList.ItemsSource = _codexCollection;

        var active = rows.FirstOrDefault(r => r.IsCurrent);
        CodexCurrentText.Text = active != null
            ? active.Name + (active.P.IsOfficial ? "" : "  (" + active.P.BaseUrl + ")")
            : (string.IsNullOrEmpty(current) ? I18nService.T("Msg.OfficialCurrent") : I18nService.F("Msg.UnlistedProviderFmt", current));

        CodexEmpty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateRouterUI();
    }

    void UpdateRouterUI()
    {
        bool isCodex = LocalProxyServer.IsCodexEnabled;
        bool isClaudeCli = LocalProxyServer.IsClaudeCliEnabled;
        bool isClaudeDesktop = LocalProxyServer.IsClaudeDesktopEnabled;
        int port = LocalProxyServer.Port;

        // ===== 三态语义色 =====
        // ON（路由生效）：鲜艳翠绿 + 微光
        var onBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x05, 0x96, 0x69));   // vivid emerald dot
        var activeBg = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xD1, 0xFA, 0xE5));   // vivid emerald bg
        var activeBorder = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x05, 0x96, 0x69)); // vivid emerald border
        var activeFg = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x04, 0x77, 0x58));   // deep emerald text
        var chipActiveBorder = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x6E, 0xE7, 0xB7));
        var onGlow = new System.Windows.Media.Effects.DropShadowEffect
        {
            Color = System.Windows.Media.Color.FromRgb(0x10, 0xB9, 0x81),
            BlurRadius = 10,
            ShadowDepth = 0,
            Opacity = 0.55,
        };

        // 需开启但未开启（告警）：警示红
        var warnBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xDC, 0x26, 0x26));   // red dot
        var warnBg = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFE, 0xF2, 0xF2));      // red bg
        var warnBorder = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFC, 0xCA, 0xCA));  // red border
        var warnFg = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xB9, 0x1C, 0x1C));     // deep red text
        var chipWarnBorder = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFC, 0xA5, 0xA5));

        // OFF 且无需路由（中性）：灰
        var offBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x94, 0xA3, 0xB8));
        var inactiveBg = System.Windows.Media.Brushes.White;
        var inactiveBorder = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xCB, 0xD5, 0xE1));
        var inactiveFg = (System.Windows.Media.Brush)FindResource("TextDimBrush");
        var chipInactiveBg = (System.Windows.Media.Brush)FindResource("ChipBrush");
        var chipInactiveBorder = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE2, 0xE8, 0xF0));
        var chipInactiveFg = (System.Windows.Media.Brush)FindResource("TextMutedBrush");

        bool anyOn = isCodex || isClaudeCli || isClaudeDesktop;

        // Codex
        bool codexNeeds = CodexProviderNeedsRouter(CodexCli.GetActiveProvider());
        ApplyChannelRouterUi(
            isCodex, codexNeeds,
            CodexToolbarRouterBtn, CodexToolbarRouterDot, CodexToolbarRouterText, port,
            onBrush, activeBg, activeBorder, activeFg,
            warnBrush, warnBg, warnBorder, warnFg,
            offBrush, inactiveBg, inactiveBorder, inactiveFg);
        if (CodexRouterChip != null)
        {
            CodexRouterChip.Background = isCodex ? activeBg : (codexNeeds ? warnBg : chipInactiveBg);
            CodexRouterChip.BorderBrush = isCodex ? chipActiveBorder : (codexNeeds ? chipWarnBorder : chipInactiveBorder);
        }
        if (CodexRouterDot != null) CodexRouterDot.Fill = isCodex ? onBrush : (codexNeeds ? warnBrush : offBrush);
        if (CodexRouterChipText != null)
        {
            CodexRouterChipText.Foreground = isCodex ? activeFg : (codexNeeds ? warnFg : chipInactiveFg);
            CodexRouterChipText.FontWeight = isCodex ? FontWeights.SemiBold : (codexNeeds ? FontWeights.SemiBold : FontWeights.Normal);
            var active = CodexCli.GetActiveProvider();
            if (active != null && !active.IsOfficial)
            {
                var wire = (active.WireApi ?? "").Trim().ToLowerInvariant();
                var modeDesc = wire == "chat" ? I18nService.T("Router.ModeTranslate") : I18nService.T("Router.ModeTransparent");
                CodexRouterChipText.Text = isCodex
                    ? I18nService.F("Router.TakeoverFmt", port, modeDesc)
                    : I18nService.T("Router.DirectModeNoRouter");
            }
            else
            {
                CodexRouterChipText.Text = isCodex ? I18nService.F("Router.StandbyFmt", port) : I18nService.T("Router.DirectMode");
            }
        }

        // Claude CLI
        bool claudeNeeds = ClaudeProviderNeedsRouter(ClaudeCli.GetActiveProvider(), isDesktop: false);
        ApplyChannelRouterUi(
            isClaudeCli, claudeNeeds,
            ClaudeToolbarRouterBtn, ClaudeToolbarRouterDot, ClaudeToolbarRouterText, port,
            onBrush, activeBg, activeBorder, activeFg,
            warnBrush, warnBg, warnBorder, warnFg,
            offBrush, inactiveBg, inactiveBorder, inactiveFg);
        if (ClaudeRouterChip != null)
        {
            ClaudeRouterChip.Background = isClaudeCli ? activeBg : (claudeNeeds ? warnBg : chipInactiveBg);
            ClaudeRouterChip.BorderBrush = isClaudeCli ? chipActiveBorder : (claudeNeeds ? chipWarnBorder : chipInactiveBorder);
        }
        if (ClaudeRouterDot != null) ClaudeRouterDot.Fill = isClaudeCli ? onBrush : (claudeNeeds ? warnBrush : offBrush);
        if (ClaudeRouterChipText != null)
        {
            ClaudeRouterChipText.Foreground = isClaudeCli ? activeFg : (claudeNeeds ? warnFg : chipInactiveFg);
            ClaudeRouterChipText.FontWeight = isClaudeCli ? FontWeights.SemiBold : (claudeNeeds ? FontWeights.SemiBold : FontWeights.Normal);
            var active = ClaudeCli.GetActiveProvider();
            if (active != null && !active.IsOfficial)
            {
                ClaudeRouterChipText.Text = isClaudeCli
                    ? I18nService.F("Router.TakeoverTransparentFmt", port)
                    : I18nService.T("Router.DirectModeNoRouter");
            }
            else
            {
                ClaudeRouterChipText.Text = isClaudeCli ? I18nService.F("Router.StandbyFmt", port) : I18nService.T("Router.DirectMode");
            }
        }

        // Claude Desktop
        bool desktopNeeds = ClaudeProviderNeedsRouter(ClaudeDesktopCli.GetActiveProvider(), isDesktop: true);
        ApplyChannelRouterUi(
            isClaudeDesktop, desktopNeeds,
            DesktopToolbarRouterBtn, DesktopToolbarRouterDot, DesktopToolbarRouterText, port,
            onBrush, activeBg, activeBorder, activeFg,
            warnBrush, warnBg, warnBorder, warnFg,
            offBrush, inactiveBg, inactiveBorder, inactiveFg);
        if (DesktopRouterChip != null)
        {
            DesktopRouterChip.Background = isClaudeDesktop ? activeBg : (desktopNeeds ? warnBg : chipInactiveBg);
            DesktopRouterChip.BorderBrush = isClaudeDesktop ? chipActiveBorder : (desktopNeeds ? chipWarnBorder : chipInactiveBorder);
        }
        if (DesktopRouterDot != null) DesktopRouterDot.Fill = isClaudeDesktop ? onBrush : (desktopNeeds ? warnBrush : offBrush);
        if (DesktopRouterChipText != null)
        {
            DesktopRouterChipText.Foreground = isClaudeDesktop ? activeFg : (desktopNeeds ? warnFg : chipInactiveFg);
            DesktopRouterChipText.FontWeight = isClaudeDesktop ? FontWeights.SemiBold : (desktopNeeds ? FontWeights.SemiBold : FontWeights.Normal);
            var active = ClaudeDesktopCli.GetActiveProvider();
            if (active != null && !active.IsOfficial)
            {
                DesktopRouterChipText.Text = isClaudeDesktop
                    ? I18nService.F("Router.TakeoverTransparentFmt", port)
                    : I18nService.T("Router.DirectModeNoRouter");
            }
            else
            {
                DesktopRouterChipText.Text = isClaudeDesktop ? I18nService.F("Router.StandbyFmt", port) : I18nService.T("Router.DirectMode");
            }
        }
    }

    /// <summary>渠道工具栏三态配色：开=翠绿 / 关且需路由=警示红 / 关且无需=灰。</summary>
    static void ApplyChannelRouterUi(
        bool isOn, bool needs,
        System.Windows.Controls.Button? btn, System.Windows.Shapes.Ellipse? dot, System.Windows.Controls.TextBlock? text, int port,
        System.Windows.Media.SolidColorBrush onBrush,
        System.Windows.Media.SolidColorBrush activeBg, System.Windows.Media.SolidColorBrush activeBorder, System.Windows.Media.SolidColorBrush activeFg,
        System.Windows.Media.SolidColorBrush warnBrush,
        System.Windows.Media.SolidColorBrush warnBg, System.Windows.Media.SolidColorBrush warnBorder, System.Windows.Media.SolidColorBrush warnFg,
        System.Windows.Media.SolidColorBrush offBrush,
        System.Windows.Media.Brush inactiveBg, System.Windows.Media.SolidColorBrush inactiveBorder, System.Windows.Media.Brush inactiveFg)
    {
        var bg = isOn ? activeBg : (needs ? warnBg : inactiveBg);
        var border = isOn ? activeBorder : (needs ? warnBorder : inactiveBorder);
        var fg = isOn ? activeFg : (needs ? warnFg : inactiveFg);

        if (btn != null)
        {
            btn.Background = bg;
            btn.BorderBrush = border;
        }
        if (dot != null) dot.Fill = isOn ? onBrush : (needs ? warnBrush : offBrush);
        if (text != null)
        {
            text.Text = isOn ? I18nService.F("Router.ToolbarOnFmt", port) : I18nService.T("Router.ToolbarOff");
            text.Foreground = fg;
            text.FontWeight = isOn || needs ? FontWeights.SemiBold : FontWeights.Normal;
        }
    }

    /// <summary>Codex 供应商是否必须走本地路由（与 CodexRow.RequiresRouter 同规则）。</summary>
    static bool CodexProviderNeedsRouter(CodexProvider? p) =>
        p != null && !p.IsOfficial && (
            string.Equals(p.WireApi, "chat", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(p.WireApi, "anthropic", StringComparison.OrdinalIgnoreCase) ||
            (p.ExtraOptions != null && p.ExtraOptions.TryGetValue("require_proxy", out var rp) && bool.TryParse(rp, out var b) && b));

    /// <summary>Claude 供应商是否必须走本地路由（与 ClaudeRow.RequiresRouter 同规则）。</summary>
    static bool ClaudeProviderNeedsRouter(ClaudeProvider? p, bool isDesktop) =>
        p != null && !p.IsOfficial && (
            string.Equals(p.WireApi, "chat", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(p.WireApi, "responses", StringComparison.OrdinalIgnoreCase) ||
            (isDesktop && string.Equals(p.AccessMode, "mapping", StringComparison.OrdinalIgnoreCase)) ||
            (!isDesktop && p.ModelMappings != null && p.ModelMappings.Any(m => m.Supports1m)) ||
            (p.ExtraOptions != null && p.ExtraOptions.TryGetValue("require_proxy", out var rp) && bool.TryParse(rp, out var b) && b));

    void OnToggleCodexProxy(object sender, RoutedEventArgs e)
    {
        var newState = !LocalProxyServer.IsCodexEnabled;
        LocalProxyServer.SetCodexEnabled(newState);
        RefreshCodex();
        if (newState)
        {
            ShowToast(I18nService.F("Router.CodexOnFmt", LocalProxyServer.Port));
        }
        else
        {
            ShowToast(I18nService.T("Router.CodexOff"));
        }
    }

    void OnToggleClaudeCliProxy(object sender, RoutedEventArgs e)
    {
        var newState = !LocalProxyServer.IsClaudeCliEnabled;
        LocalProxyServer.SetClaudeCliEnabled(newState);
        RefreshClaude();
        if (newState)
        {
            ShowToast(I18nService.F("Router.CliOnFmt", LocalProxyServer.Port));
        }
        else
        {
            ShowToast(I18nService.T("Router.CliOff"));
        }
    }

    void OnToggleClaudeDesktopProxy(object sender, RoutedEventArgs e)
    {
        var newState = !LocalProxyServer.IsClaudeDesktopEnabled;
        LocalProxyServer.SetClaudeDesktopEnabled(newState);
        RefreshDesktop();
        if (newState)
        {
            ShowToast(I18nService.F("Router.DesktopOnFmt", LocalProxyServer.Port));
        }
        else
        {
            ShowToast(I18nService.T("Router.DesktopOff"));
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
        if (!Confirm(I18nService.F("Common.DeleteProviderConfirmFmt", row.Name))) return;
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
        catch (Exception ex) { ShowToast(I18nService.F("Common.ApplyFailFmt", ex.Message), isError: true); return; }
        RefreshCodex();
        if (LocalProxyServer.IsCodexEnabled && !row.P.IsOfficial)
        {
            ShowToast(autoEnabled
                ? I18nService.F("Msg.CodexSwitchedAutoFmt", row.Name)
                : I18nService.F("Msg.CodexSwitchedHotFmt", row.Name));
        }
        else
        {
            ShowToast(I18nService.F("Msg.CodexSwitchedRestartFmt", row.Name));
        }
    }

    async void EditCodex(CodexProvider? existing)
    {
        if (_activeProviderDialog != null && _activeProviderDialog.IsLoaded)
        {
            _activeProviderDialog.Activate();
            _activeProviderDialog.Focus();
            ShowToast(I18nService.T("Msg.EditorBusy"));
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
        // 自动获取模型：用户没有手动配置时，从供应商 /models 拉取真实模型列表，
        // 这样无需逐个手填，Codex 的 /model 切换器就能列出该供应商提供的全部模型。
        // （第三方中转站的模型名不在 Codex 内置目录里，不写自定义目录就只剩默认模型。）
        if (p.AutoFetchModels && p.CustomModels.Count == 0)
        {
            var fetched = await TryFetchCodexModelsAsync(p);
            if (fetched.Count > 0)
            {
                p.CustomModels = fetched
                    .Select(m => new Models.ProviderModelEntry { Id = m, Name = m, ContextWindow = "1m" })
                    .ToList();
                if (idx >= 0) _codexProviders[idx] = p;
                ShowToast(I18nService.F("Msg.CodexModelsAutoFetchedFmt", fetched.Count));
            }
            else
            {
                ShowToast(I18nService.T("Msg.CodexModelsAutoFetchEmpty"), isError: true);
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
        ShowToast(isCurrent ? I18nService.F("Msg.SavedCurrentFmt", p.Name) : I18nService.F("Msg.SavedProviderFmt", p.Name));
    }

    /// <summary>
    /// 从 Codex 供应商的 /models 端点拉取真实模型列表。失败返回空列表（不阻塞保存）。
    /// </summary>
    async Task<List<string>> TryFetchCodexModelsAsync(Models.CodexProvider p)
    {
        try
        {
            var key = !string.IsNullOrWhiteSpace(p.ApiKey) ? p.ApiKey : p.BearerToken;
            return await Services.ModelFetchService.FetchModelsAsync(p.BaseUrl ?? "", key, p.CustomHeaders);
        }
        catch (Exception ex)
        {
            App.LogStartup($"CodexAutoFetch: {p.Name} failed: {ex.Message}");
            return new List<string>();
        }
    }


    async void OnRestartCodex(object sender, RoutedEventArgs e)
    {
        ShowToast(I18nService.F("Msg.RestartingFmt", "Codex"));
        try
        {
            var r = await CodexProcess.RestartCodexAsync();
            if (r.Ok)
                ShowToast(I18nService.F("Msg.RestartedFmt", "Codex"));
            else
                ShowToast(I18nService.F("Msg.RestartNoProcessFmt", "Codex"), isError: true);
        }
        catch (Exception ex)
        {
            ShowToast(I18nService.F("Msg.RestartFailFmt", "Codex", ex.Message), isError: true);
        }
    }

    void OnOpenCodexConfig(object sender, RoutedEventArgs e) => OpenFile(CodexCli.ConfigPath);


}

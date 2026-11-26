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

public partial class MainWindow : Window
{
    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;


    ObservableCollection<Profile> _profiles = new();
    ObservableCollection<CodexRow> _codexCollection = new();
    ObservableCollection<ClaudeRow> _claudeCollection = new();
    ObservableCollection<ClaudeRow> _desktopCollection = new();
    ObservableCollection<OpenCodeRow> _ocCollection = new();
    ObservableCollection<PiRow> _piCollection = new();

    List<ClaudeProvider> _claudeProviders = new();
    List<ClaudeProvider> _desktopProviders = new();
    List<CodexProvider> _codexProviders = new();
    List<PiAccount> _piAccounts = new();
    List<OpenCodeProvider> _openCodeProviders = new();
    List<PiProvider> _piProviders = new();
    UpdateInfo? _latestUpdate;

    System.Windows.Threading.DispatcherTimer? _agQuotaTimer;
    bool _isRefreshingAgQuotas = false;
    DateTime _lastAgQuotaRefreshUtc = DateTime.MinValue;
    CancellationTokenSource? _batchCts;
    System.Windows.Forms.NotifyIcon? _notifyIcon;
    bool _isExiting = false;
    bool _trayTransition = false;
    ProviderDialog? _activeProviderDialog;

    public MainWindow()
    {
        App.LogStartup("MainWindow ctor: Before InitializeComponent");
        InitializeComponent();
        App.LogStartup("MainWindow ctor: After InitializeComponent");
        ProfileList.ItemsSource = _profiles;
        CodexList.ItemsSource = _codexCollection;
        ClaudeList.ItemsSource = _claudeCollection;
        DesktopList.ItemsSource = _desktopCollection;
        OcList.ItemsSource = _ocCollection;
        PiList.ItemsSource = _piCollection;
        RestoreTabOrder();
        ApplyTabTheme((Tabs.SelectedItem as TabItem)?.Tag?.ToString() ?? "antigravity");

        Activated += OnWindowActivated;
        Deactivated += OnWindowDeactivated;
        StateChanged += OnWindowStateChanged;

        Loaded += (_, _) =>
        {
            App.LogStartup("MainWindow Loaded: start");
            ApplyTabTheme((Tabs.SelectedItem as TabItem)?.Tag?.ToString() ?? "antigravity");
            ShowCurrentVersion();
            UsageStorage.CleanupOldData();
            _ = System.Threading.Tasks.Task.Run(() => UsageScanner.ScanAll());
            LocalProxyServer.StateChanged += () => Dispatcher.Invoke(UpdateRouterUI);
            I18nService.LanguageChanged += () =>
            {
                RebuildTrayMenu();
                Dispatcher.Invoke(() =>
                {
                    _ = RefreshAntigravityAsync();
                    UpdateFloatingWidgetButtonVisual();
                    _floatingWidget?.RefreshLocalization();
                });
            };
            RefreshAll();
            UpdateRouterUI();
            if (AppSettingsService.Current.AutoCheckUpdate)
            {
                _ = CheckUpdateSilentAsync();
            }
            InitAgQuotaTimer();
            InitTrayIcon();
            InitFloatingWidget();
            InitBusyWatchdog();
            App.LogStartup("MainWindow Loaded: finished");
        };
        App.LogStartup("MainWindow ctor: finished");
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

    public void RefreshAll()
    {
        _ = RefreshAntigravityAsync();
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
            bool visibleOnAg = IsVisible && WindowState != WindowState.Minimized
                && (Tabs?.SelectedItem as TabItem)?.Tag?.ToString() == "antigravity";
            bool widgetActive = AppSettingsService.Current.FloatingWidgetEnabled
                && _floatingWidget is { IsVisible: true };
            var elapsed = DateTime.UtcNow - _lastAgQuotaRefreshUtc;
            if (visibleOnAg)
            {
                _ = RefreshAllAgQuotasAsync(silent: true, activeOnly: false);
            }
            else if ((widgetActive && elapsed >= TimeSpan.FromMinutes(2)) || elapsed >= TimeSpan.FromMinutes(5))
            {
                _ = RefreshAllAgQuotasAsync(silent: true, activeOnly: true);
            }
        };
        _agQuotaTimer.Start();
        _ = RefreshAllAgQuotasAsync(silent: true);
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
        // 保持定时器持续运行，确保用户切入 AGY 编码期间 APISwitch 仍在后台定期同步最新额度
    }

    void OnWindowStateChanged(object? sender, EventArgs e)
    {
        if (_trayTransition) return;
        // 最小化 = 最小化到任务栏（任务栏上留着这个任务的按钮，标准 Windows 行为），
        // 这里不做任何拦截/改写；只有非最小化且处于前台时才立即触发一次同步。
        if (WindowState != WindowState.Minimized && IsActive)
        {
            OnForegroundEntered();
        }
    }

    void OnForegroundEntered()
    {
        if (_agQuotaTimer != null && !_agQuotaTimer.IsEnabled)
            _agQuotaTimer.Start();

        if ((Tabs?.SelectedItem as TabItem)?.Tag?.ToString() == "antigravity" &&
            (DateTime.UtcNow - _lastAgQuotaRefreshUtc).TotalSeconds >= 30)
        {
            _ = RefreshAllAgQuotasAsync(silent: true, activeOnly: false);
        }
    }

    void OnTabChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source != Tabs || !IsLoaded || _isReorderingTabs) return;
        var tag = (Tabs?.SelectedItem as TabItem)?.Tag?.ToString() ?? "";
        ApplyTabTheme(tag);

        switch (tag)
        {
            case "antigravity":
                _ = RefreshAntigravityAsync();
                if ((DateTime.UtcNow - _lastAgQuotaRefreshUtc).TotalSeconds >= 30)
                    _ = RefreshAllAgQuotasAsync(silent: true, activeOnly: false);
                break;
            case "codex": RefreshCodex(); break;
            case "claude": RefreshClaude(); break;
            case "desktop": RefreshDesktop(); break;
            case "opencode": RefreshOpencode(); break;
            case "pi": RefreshPi(); break;
        }
    }

    void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        Process.Start(new ProcessStartInfo(AgPaths.ProfilesDir) { UseShellExecute = true });
    }

    void OnOpenSettings(object sender, RoutedEventArgs e)
    {
        var tag = (Tabs?.SelectedItem as TabItem)?.Tag?.ToString() ?? "antigravity";
        var dlg = new SettingsDialog(tag, this) { Owner = this };
        dlg.ShowDialog();
    }

    void OnOpenUsageMonitor(object sender, RoutedEventArgs e)
    {
        var dlg = new UsageMonitorWindow { Owner = this };
        dlg.ShowDialog();
    }

    void OnOpenBugFeedbackClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("https://github.com/kbkkb/APISwitch/issues") { UseShellExecute = true });
        }
        catch { }
    }

    static string GetTabTitle(TabItem tab) => (tab.Tag as string) switch
    {
        "antigravity" => "Antigravity",
        "codex" => "Codex",
        "claude" => "Claude Code",
        "desktop" => "Claude Desktop",
        "opencode" => "OpenCode",
        "pi" => "Pi",
        _ => tab.Header?.ToString() ?? I18nService.T("Common.AppFallback")
    };

    void ApplyTabTheme(string tag)
    {
        MediaColor primaryColor;
        MediaColor gradientEndColor;
        MediaColor hoverColor;
        MediaColor hoverGradientEndColor;
        MediaColor activeBgColor;
        MediaColor activeBorderColor;
        MediaColor activeTabFgColor;
        MediaColor titleBarBgColor;
        MediaColor titleBarBorderColor;
        MediaColor sidebarBgColor;
        MediaColor sidebarBorderColor;
        MediaColor windowBgColor;
        MediaColor pageBgStartColor;
        MediaColor pageBgEndColor;
        MediaColor heroCardBgEndColor;
        MediaColor heroCardBorderColor;
        MediaColor ghostHoverBgColor;
        MediaColor ghostHoverBorderColor;

        switch (tag)
        {
            case "codex":
                // OpenAI Emerald Green
                primaryColor = MediaColor.FromRgb(0x10, 0xA3, 0x7F);
                gradientEndColor = MediaColor.FromRgb(0x05, 0x96, 0x69);
                hoverColor = MediaColor.FromRgb(0x04, 0x78, 0x57);
                hoverGradientEndColor = MediaColor.FromRgb(0x06, 0x5F, 0x46);
                activeBgColor = MediaColor.FromRgb(0xC4, 0xF3, 0xDE);
                activeBorderColor = MediaColor.FromRgb(0x34, 0xD3, 0x99);
                activeTabFgColor = MediaColor.FromRgb(0x06, 0x5F, 0x46);
                titleBarBgColor = MediaColor.FromRgb(0xD3, 0xEF, 0xE3);
                titleBarBorderColor = MediaColor.FromRgb(0xB9, 0xE5, 0xD2);
                sidebarBgColor = MediaColor.FromRgb(0xDC, 0xF4, 0xEB);
                sidebarBorderColor = MediaColor.FromRgb(0xBF, 0xE7, 0xD6);
                windowBgColor = MediaColor.FromRgb(0xDC, 0xF4, 0xEB);
                pageBgStartColor = MediaColor.FromRgb(0xE8, 0xF8, 0xF1);
                pageBgEndColor = MediaColor.FromRgb(0xD8, 0xF3, 0xE6);
                heroCardBgEndColor = MediaColor.FromRgb(0xEC, 0xFD, 0xF5);
                heroCardBorderColor = MediaColor.FromRgb(0xA7, 0xF3, 0xD0);
                ghostHoverBgColor = MediaColor.FromRgb(0xEC, 0xFD, 0xF5);
                ghostHoverBorderColor = MediaColor.FromRgb(0xA7, 0xF3, 0xD0);
                break;

            case "claude":
            case "desktop":
                // Anthropic Terracotta / Warm Sand
                primaryColor = MediaColor.FromRgb(0xD9, 0x77, 0x06);
                gradientEndColor = MediaColor.FromRgb(0xEA, 0x58, 0x0C);
                hoverColor = MediaColor.FromRgb(0xB4, 0x53, 0x09);
                hoverGradientEndColor = MediaColor.FromRgb(0xC2, 0x41, 0x0C);
                activeBgColor = MediaColor.FromRgb(0xFC, 0xE3, 0xCB);
                activeBorderColor = MediaColor.FromRgb(0xFB, 0x92, 0x3C);
                activeTabFgColor = MediaColor.FromRgb(0x9A, 0x34, 0x12);
                titleBarBgColor = MediaColor.FromRgb(0xF0, 0xDF, 0xCD);
                titleBarBorderColor = MediaColor.FromRgb(0xE4, 0xCD, 0xAF);
                sidebarBgColor = MediaColor.FromRgb(0xF7, 0xE8, 0xD8);
                sidebarBorderColor = MediaColor.FromRgb(0xE8, 0xD4, 0xBE);
                windowBgColor = MediaColor.FromRgb(0xF7, 0xE8, 0xD8);
                pageBgStartColor = MediaColor.FromRgb(0xFF, 0xF2, 0xE4);
                pageBgEndColor = MediaColor.FromRgb(0xF9, 0xE6, 0xD2);
                heroCardBgEndColor = MediaColor.FromRgb(0xFF, 0xF7, 0xED);
                heroCardBorderColor = MediaColor.FromRgb(0xFE, 0xD7, 0xAA);
                ghostHoverBgColor = MediaColor.FromRgb(0xFF, 0xF7, 0xED);
                ghostHoverBorderColor = MediaColor.FromRgb(0xFE, 0xD7, 0xAA);
                break;

            case "opencode":
                // Cyber Sky Cyan / Tech Blue
                primaryColor = MediaColor.FromRgb(0x02, 0x84, 0xC7);
                gradientEndColor = MediaColor.FromRgb(0x0E, 0xA5, 0xE9);
                hoverColor = MediaColor.FromRgb(0x03, 0x69, 0xA1);
                hoverGradientEndColor = MediaColor.FromRgb(0x02, 0x84, 0xC7);
                activeBgColor = MediaColor.FromRgb(0xBD, 0xE3, 0xFB);
                activeBorderColor = MediaColor.FromRgb(0x38, 0xBD, 0xF8);
                activeTabFgColor = MediaColor.FromRgb(0x03, 0x69, 0xA1);
                titleBarBgColor = MediaColor.FromRgb(0xCC, 0xE6, 0xFA);
                titleBarBorderColor = MediaColor.FromRgb(0xB0, 0xD7, 0xF6);
                sidebarBgColor = MediaColor.FromRgb(0xD8, 0xED, 0xFC);
                sidebarBorderColor = MediaColor.FromRgb(0xB9, 0xDC, 0xF7);
                windowBgColor = MediaColor.FromRgb(0xD8, 0xED, 0xFC);
                pageBgStartColor = MediaColor.FromRgb(0xE4, 0xF3, 0xFD);
                pageBgEndColor = MediaColor.FromRgb(0xD2, 0xEA, 0xFD);
                heroCardBgEndColor = MediaColor.FromRgb(0xF0, 0xF9, 0xFF);
                heroCardBorderColor = MediaColor.FromRgb(0xBA, 0xE6, 0xFD);
                ghostHoverBgColor = MediaColor.FromRgb(0xF0, 0xF9, 0xFF);
                ghostHoverBorderColor = MediaColor.FromRgb(0xBA, 0xE6, 0xFD);
                break;

            case "pi":
                // Math Geek Violet / Purple
                primaryColor = MediaColor.FromRgb(0x7C, 0x3A, 0xED);
                gradientEndColor = MediaColor.FromRgb(0x93, 0x33, 0xEA);
                hoverColor = MediaColor.FromRgb(0x6D, 0x28, 0xD9);
                hoverGradientEndColor = MediaColor.FromRgb(0x7E, 0x22, 0xCE);
                activeBgColor = MediaColor.FromRgb(0xD9, 0xC8, 0xFB);
                activeBorderColor = MediaColor.FromRgb(0xA7, 0x8B, 0xFA);
                activeTabFgColor = MediaColor.FromRgb(0x5B, 0x21, 0xB6);
                titleBarBgColor = MediaColor.FromRgb(0xDE, 0xD3, 0xFA);
                titleBarBorderColor = MediaColor.FromRgb(0xC8, 0xB6, 0xF5);
                sidebarBgColor = MediaColor.FromRgb(0xE7, 0xDC, 0xFD);
                sidebarBorderColor = MediaColor.FromRgb(0xD0, 0xC1, 0xF7);
                windowBgColor = MediaColor.FromRgb(0xE7, 0xDC, 0xFD);
                pageBgStartColor = MediaColor.FromRgb(0xEF, 0xE6, 0xFE);
                pageBgEndColor = MediaColor.FromRgb(0xE2, 0xD4, 0xFD);
                heroCardBgEndColor = MediaColor.FromRgb(0xF5, 0xF3, 0xFF);
                heroCardBorderColor = MediaColor.FromRgb(0xDD, 0xD6, 0xFE);
                ghostHoverBgColor = MediaColor.FromRgb(0xF5, 0xF3, 0xFF);
                ghostHoverBorderColor = MediaColor.FromRgb(0xDD, 0xD6, 0xFE);
                break;

            case "antigravity":
            default:
                // Google Indigo / Tech Blue
                primaryColor = MediaColor.FromRgb(0x4F, 0x46, 0xE5);
                gradientEndColor = MediaColor.FromRgb(0x63, 0x66, 0xF1);
                hoverColor = MediaColor.FromRgb(0x43, 0x38, 0xCA);
                hoverGradientEndColor = MediaColor.FromRgb(0x4F, 0x46, 0xE5);
                activeBgColor = MediaColor.FromRgb(0xDB, 0xE5, 0xFE);
                activeBorderColor = MediaColor.FromRgb(0x81, 0x8C, 0xF8);
                activeTabFgColor = MediaColor.FromRgb(0x43, 0x38, 0xCA);
                titleBarBgColor = MediaColor.FromRgb(0xE0, 0xE7, 0xF8);
                titleBarBorderColor = MediaColor.FromRgb(0xCB, 0xD7, 0xEE);
                sidebarBgColor = MediaColor.FromRgb(0xE8, 0xEE, 0xFB);
                sidebarBorderColor = MediaColor.FromRgb(0xCF, 0xDB, 0xEE);
                windowBgColor = MediaColor.FromRgb(0xE8, 0xEE, 0xFB);
                pageBgStartColor = MediaColor.FromRgb(0xED, 0xF2, 0xFE);
                pageBgEndColor = MediaColor.FromRgb(0xE2, 0xEA, 0xF8);
                heroCardBgEndColor = MediaColor.FromRgb(0xEE, 0xF2, 0xFF);
                heroCardBorderColor = MediaColor.FromRgb(0xC7, 0xD2, 0xFE);
                ghostHoverBgColor = MediaColor.FromRgb(0xEE, 0xF2, 0xFF);
                ghostHoverBorderColor = MediaColor.FromRgb(0xC7, 0xD2, 0xFE);
                break;
        }

        // Apply to Window / Title / Sidebar / Page Backgrounds
        Resources["WindowBgBrush"] = new SolidColorBrush(windowBgColor);
        Resources["TitleBarBgBrush"] = new SolidColorBrush(titleBarBgColor);
        Resources["TitleBarBorderBrush"] = new SolidColorBrush(titleBarBorderColor);
        Resources["SidebarBgBrush"] = new SolidColorBrush(sidebarBgColor);
        Resources["SidebarBorderBrush"] = new SolidColorBrush(sidebarBorderColor);

        var pageGrad = new LinearGradientBrush { StartPoint = new WpfPoint(0, 0), EndPoint = new WpfPoint(0, 1) };
        pageGrad.GradientStops.Add(new GradientStop(pageBgStartColor, 0));
        pageGrad.GradientStops.Add(new GradientStop(pageBgEndColor, 1));
        Resources["TabPageBgBrush"] = pageGrad;

        var heroGrad = new LinearGradientBrush { StartPoint = new WpfPoint(0, 0), EndPoint = new WpfPoint(0, 1) };
        heroGrad.GradientStops.Add(new GradientStop(MediaColor.FromRgb(0xFF, 0xFF, 0xFF), 0));
        heroGrad.GradientStops.Add(new GradientStop(heroCardBgEndColor, 1));
        Resources["HeroCardBgBrush"] = heroGrad;
        Resources["HeroCardBorderBrush"] = new SolidColorBrush(heroCardBorderColor);

        Resources["GhostHoverBgBrush"] = new SolidColorBrush(ghostHoverBgColor);
        Resources["GhostHoverBorderBrush"] = new SolidColorBrush(ghostHoverBorderColor);

        // Tab state brushes
        Resources["TabAccentBrush"] = new SolidColorBrush(activeTabFgColor);
        Resources["TabAccentHoverBrush"] = new SolidColorBrush(hoverColor);
        Resources["TabActiveBgBrush"] = new SolidColorBrush(activeBgColor);
        Resources["TabActiveBorderBrush"] = new SolidColorBrush(activeBorderColor);

        var grad = new LinearGradientBrush { StartPoint = new WpfPoint(0, 0), EndPoint = new WpfPoint(1, 0) };
        grad.GradientStops.Add(new GradientStop(primaryColor, 0));
        grad.GradientStops.Add(new GradientStop(gradientEndColor, 1));
        Resources["TabAccentGradient"] = grad;

        var gradHover = new LinearGradientBrush { StartPoint = new WpfPoint(0, 0), EndPoint = new WpfPoint(1, 0) };
        gradHover.GradientStops.Add(new GradientStop(hoverColor, 0));
        gradHover.GradientStops.Add(new GradientStop(hoverGradientEndColor, 1));
        Resources["TabAccentGradientHover"] = gradHover;

        // Propagate to global Accent brushes in MainWindow
        Resources["AccentBrush"] = new SolidColorBrush(primaryColor);
        Resources["AccentHoverBrush"] = new SolidColorBrush(hoverColor);
        Resources["AccentGradient"] = grad;
        Resources["AccentGradientHover"] = gradHover;
        Resources["CardActiveBorderBrush"] = new SolidColorBrush(primaryColor);
        Resources["CardActiveBrush"] = new SolidColorBrush(activeBgColor);
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
                    done.Add(I18nService.T("Common.ClaudeClient"));
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
        ShowToast(I18nService.F("Msg.CopiedTargetsFmt", src.Name, string.Join(I18nService.T("Common.ListSep"), done)));
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
                    done.Add(I18nService.T("Common.ClaudeClient"));
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
        ShowToast(I18nService.F("Msg.CopiedTargetsFmt", src.Name, string.Join(I18nService.T("Common.ListSep"), done)));
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

    public void OnImportCc(object sender, RoutedEventArgs e)
    {
        if (!CcSwitchImport.IsAvailable)
        {
            ShowToast(I18nService.T("Msg.CcNotFound"), isError: true);
            return;
        }
        var dlg = new ImportDialog { Owner = this };
        try
        {
            if (dlg.ShowDialog() == true)
            {
                RefreshClaude(); RefreshDesktop(); RefreshCodex(); RefreshOpencode(); RefreshPi();
                ShowToast(I18nService.T("Msg.CcImported"));
            }
        }
        catch (Exception ex) { ShowToast(I18nService.F("Msg.CcReadFailFmt", ex.Message), isError: true); return; }
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

    System.Windows.Threading.DispatcherTimer? _toastTimer;

    public void ShowToast(string msg, bool isError = false, bool isInfo = false)
    {
        Dispatcher.Invoke(() =>
        {
            _toastTimer?.Stop();
            ToastText.Text = msg;
            if (isInfo)
            {
                ToastIcon.Data = (System.Windows.Media.Geometry)FindResource("IconRefresh");
                ToastIcon.Fill = (System.Windows.Media.Brush)FindResource("AccentBrush");
                ToastBanner.BorderBrush = (System.Windows.Media.Brush)FindResource("AccentBrush");
            }
            else if (isError)
            {
                ToastIcon.Data = (System.Windows.Media.Geometry)FindResource("IconClose");
                ToastIcon.Fill = (System.Windows.Media.Brush)FindResource("DangerBrush");
                ToastBanner.BorderBrush = (System.Windows.Media.Brush)FindResource("DangerBrush");
            }
            else
            {
                ToastIcon.Data = (System.Windows.Media.Geometry)FindResource("IconCheck");
                ToastIcon.Fill = (System.Windows.Media.Brush)FindResource("GreenBrush");
                ToastBanner.BorderBrush = (System.Windows.Media.Brush)FindResource("CardBorderBrush");
            }

            ToastBanner.Visibility = Visibility.Visible;
            var anim = new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180));
            ToastBanner.BeginAnimation(OpacityProperty, anim);

            _toastTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(isInfo ? 5.0 : 2.6) };
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
        if (!File.Exists(path)) { Warn(I18nService.F("Common.FileNotFoundFmt", path)); return; }
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    /// <summary>
    /// 进入忙碌状态：只显示遮罩，绝不把整个 Window 设为 IsEnabled=false。
    /// 原因：WPF 窗口被禁用后，点击它只会"激活窗口"而不派发点击事件
    /// （部分自定义按钮样式没有 IsEnabled=False 的视觉反馈，界面看起来完全正常却点不动），
    /// 且一旦某条路径漏调 ClearBusy，窗口就永久卡在禁用态。
    public void SetBusy(string text)
    {
        BusyText.Text = text;
        _busySince = DateTime.UtcNow;
        BusyOverlay.Visibility = Visibility.Visible;
    }

    public void ClearBusy()
    {
        BusyOverlay.Visibility = Visibility.Collapsed;
        _busySince = DateTime.MinValue;
    }
    /// <summary>
    /// 兜底看门狗：忙碌遮罩绝不允许永久停留。
    /// 即使将来某条异步路径漏调 ClearBusy，界面也不会被遮罩卡死。
    /// </summary>
    void InitBusyWatchdog()
    {
        var timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(10)
        };
        timer.Tick += (_, _) =>
        {
            if (BusyOverlay.Visibility == Visibility.Visible &&
                DateTime.UtcNow - _busySince > BusyMaxDuration)
            {
                App.LogStartup($"BusyWatchdog: overlay stuck since {_busySince:O}, force clearing");
                ClearBusy();
            }
        };
        timer.Start();
    }

    /// <summary>忙碌遮罩允许停留的最长时间，超过则由看门狗强制收起。</summary>
    static readonly TimeSpan BusyMaxDuration = TimeSpan.FromMinutes(10);

    /// <summary>本次忙碌状态的起始时刻（UTC）。</summary>
    DateTime _busySince = DateTime.MinValue;

    static bool Confirm(string msg) =>
        MessageBox.Show(msg, "APISwitch", MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK;

    static void Warn(string msg) =>
        MessageBox.Show(msg, "APISwitch", MessageBoxButton.OK, MessageBoxImage.Warning);

    static void Info(string msg) =>
        MessageBox.Show(msg, "APISwitch", MessageBoxButton.OK, MessageBoxImage.Information);

    // ---------- Update Management ----------

    public void ApplyUpdateInfo(UpdateInfo info)
    {
        _latestUpdate = info;
        if (info.HasUpdate)
        {
            // 有新版本：按钮高亮（红字 + 闪电图标 + 升级徽章）
            if (UpdateCheckBtn != null) UpdateCheckBtn.Visibility = Visibility.Visible;
            if (UpdateIcon != null) UpdateIcon.Visibility = Visibility.Visible;
            if (AppVersionText != null)
            {
                AppVersionText.Text = info.LatestVersion; // LatestVersion 已带 v 前缀（UpdateService 保证）
                AppVersionText.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xEF, 0x44, 0x44));
                AppVersionText.FontWeight = FontWeights.Bold;
            }
            if (UpdateBadge != null) UpdateBadge.Visibility = Visibility.Visible;
            if (UpdateBadgeText != null) UpdateBadgeText.Text = I18nService.T("Main.Upgrade");
        }
        else
        {
            // 无更新：常驻显示当前版本（朴素样式）
            ShowCurrentVersion();
        }
    }

    /// <summary>左上角常驻显示当前程序版本（无更新时的朴素样式）。</summary>
    void ShowCurrentVersion()
    {
        if (UpdateCheckBtn != null) UpdateCheckBtn.Visibility = Visibility.Visible;
        if (UpdateIcon != null) UpdateIcon.Visibility = Visibility.Collapsed;
        if (UpdateBadge != null) UpdateBadge.Visibility = Visibility.Collapsed;
        var ver = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        if (ver != null && AppVersionText != null)
        {
            AppVersionText.Text = $"v{ver.Major}.{ver.Minor}.{ver.Build}";
            AppVersionText.Foreground = (System.Windows.Media.Brush)FindResource("TextDimBrush");
            AppVersionText.FontWeight = FontWeights.Normal;
        }
    }

    public async void OnCheckUpdateClick(object sender, RoutedEventArgs e)
    {
        if (_latestUpdate != null && _latestUpdate.HasUpdate)
        {
            new UpdateDialog(_latestUpdate) { Owner = this }.ShowDialog();
            return;
        }

        ShowToast(I18nService.T("Msg.CheckingUpdate"), isInfo: true);
        try
        {
            var info = await UpdateService.CheckForUpdatesAsync();

            if (info == null)
            {
                ShowToast(I18nService.T("Msg.CheckUpdateFail"), isError: true);
                return;
            }

            ApplyUpdateInfo(info);
            if (info.HasUpdate)
            {
                new UpdateDialog(info) { Owner = this }.ShowDialog();
            }
            else
            {
                ShowToast(I18nService.F("Msg.LatestVersion", info.CurrentVersion));
            }
        }
        catch (Exception ex)
        {
            ShowToast(I18nService.F("Msg.CheckUpdateError", ex.Message), isError: true);
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
                    if (UpdateCheckBtn != null) UpdateCheckBtn.Visibility = Visibility.Visible;
                    if (UpdateIcon != null) UpdateIcon.Visibility = Visibility.Visible;
                    if (AppVersionText != null)
                    {
                        AppVersionText.Text = info.LatestVersion; // LatestVersion 已带 v 前缀（UpdateService 保证）
                        AppVersionText.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xEF, 0x44, 0x44));
                        AppVersionText.FontWeight = FontWeights.Bold;
                    }
                    UpdateBadge.Visibility = Visibility.Visible;
                    ShowToast(I18nService.F("Msg.NewVersionClickTip", info.LatestVersion));
                });
            }
        }
        catch
        {
            // 静默模式忽略网络波动
        }
    }
}

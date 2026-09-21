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

    System.Windows.Forms.NotifyIcon? _notifyIcon;
    bool _isExiting = false;
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
            LocalProxyServer.StateChanged += () => Dispatcher.Invoke(UpdateRouterUI);
            I18nService.LanguageChanged += RebuildTrayMenu;
            RefreshAll();
            UpdateRouterUI();
            if (AppSettingsService.Current.AutoCheckUpdate)
            {
                _ = CheckUpdateSilentAsync();
            }
            InitAgQuotaTimer();
            InitTrayIcon();
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
            if (IsActive && WindowState != WindowState.Minimized && (Tabs?.SelectedItem as TabItem)?.Tag?.ToString() == "antigravity")
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

        if ((Tabs?.SelectedItem as TabItem)?.Tag?.ToString() == "antigravity")
        {
            _ = RefreshAllAgQuotasAsync(silent: true);
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
                RefreshAntigravity();
                _ = RefreshAllAgQuotasAsync(silent: true);
                break;
            case "codex": RefreshCodex(); break;
            case "claude": RefreshClaude(); break;
            case "desktop": RefreshDesktop(); break;
            case "opencode": RefreshOpencode(); break;
            case "pi": RefreshPi(); break;
        }
    }

    // ---------- Antigravity ----------

    void RefreshAntigravity()
    {
        var db = AgPaths.FindStateDb();
        DbPathText.Text = db ?? I18nService.T("Ag.DbNotFound");

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
                if (email == null) DbPathText.Text = I18nService.F("Msg.DbReadFailFmt", db, ex.Message);
            }
        }

        CurrentAccountText.Text = email ?? I18nService.T("Ag.NoAccount");
        StateChip.Text = state switch
        {
            "signedIn" => I18nService.T("Ag.SignedIn"),
            "signedOut" => I18nService.T("Ag.SignedOut"),
            null => I18nService.T("Ag.StateUnknown"),
            _ => state,
        };
        PlanChip.Text = string.IsNullOrEmpty(plan) ? "—" : plan;

        var running = AgProcess.IsRunning();
        IdeChip.Text = running ? I18nService.T("Ag.IdeRunning") : I18nService.T("Ag.IdeNotRunning");
        IdeChip.Foreground = running ? System.Windows.Media.Brushes.LightGreen : null;

        var loaded = ProfileStore.Load();

        // If no profiles loaded yet and Antigravity Tools is available, auto import
        if (loaded.Count == 0 && AgToolsService.IsInstalled())
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

        _profiles.Clear();
        var seenAg = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in loaded)
        {
            var key = (!string.IsNullOrEmpty(p.Email) ? p.Email : p.Name).Trim();
            if (!string.IsNullOrEmpty(key) && seenAg.Add(key))
            {
                p.IsCurrent = email != null && email.Equals(p.Email, StringComparison.OrdinalIgnoreCase);
                _profiles.Add(p);
            }
        }

        if (ProfileList.ItemsSource != _profiles)
        {
            ProfileList.ItemsSource = _profiles;
        }
        AgEmpty.Visibility = _profiles.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    async void OnImportFromAgTools(object sender, RoutedEventArgs e)
    {
        SetBusy(I18nService.T("Msg.AgImporting"));
        try
        {
            var imported = await AgToolsService.ImportAccountsAsync();
            ClearBusy();
            RefreshAntigravity();
            if (imported.Count > 0)
                ShowToast(I18nService.F("Msg.AgImportedFmt", imported.Count));
            else
                ShowToast(I18nService.T("Msg.AgImportNone"), isError: true);
        }
        catch (Exception ex)
        {
            ClearBusy();
            ShowToast(I18nService.F("Msg.AgImportFailFmt", ex.Message), isError: true);
        }
    }

    async void OnNewAccountLogin(object sender, RoutedEventArgs e)
    {
        SetBusy(I18nService.T("Msg.AgLoginBusy"));
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            var profile = await AgAuthFlow.StartGoogleLoginAsync(cts.Token);
            ClearBusy();
            RefreshAntigravity();
            ShowToast(I18nService.F("Msg.AgLoginOkFmt", profile.Email));
        }
        catch (OperationCanceledException)
        {
            ClearBusy();
            ShowToast(I18nService.T("Msg.AgLoginCanceled"), isError: true);
        }
        catch (Exception ex)
        {
            ClearBusy();
            ShowToast(I18nService.F("Msg.AgLoginFailFmt", ex.Message), isError: true);
        }
    }

    async void OnCardActivateQuota(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not Profile target) return;
        if (target.IsActivating) return; // 防抖：激活中忽略重复点击

        target.IsActivating = true;
        try
        {
            SetBusy(I18nService.F("Msg.AgActivatingFmt", target.Email));
            var (ok, latencyMs, msg, rateLimited) = await AgQuotaService.ActivateAccountQuotaAsync(target);
            ClearBusy();
            RefreshAntigravity();
            if (ok)
            {
                ShowToast(I18nService.F("Msg.AgActivatedFmt", target.Email, latencyMs));
            }
            else if (rateLimited)
            {
                // 429 用尽不是“失败”：提示重置时间，不用错误样式
                var reset = target.QuotaExhaustedResetAt.HasValue
                    ? target.QuotaExhaustedResetAt.Value.ToLocalTime().ToString("HH:mm")
                    : "—";
                ShowToast(I18nService.F("Msg.AgQuotaExhaustedFmt", target.Email, reset), isInfo: true);
            }
            else
            {
                ShowToast(I18nService.F("Msg.AgActivateFailFmt", msg), isError: true);
            }
        }
        catch (Exception ex)
        {
            ClearBusy();
            ShowToast(I18nService.F("Msg.AgActivateErrorFmt", ex.Message), isError: true);
        }
        finally
        {
            target.IsActivating = false;
        }
    }

    async void OnBatchActivateAntigravity(object sender, RoutedEventArgs e)
    {
        if (_profiles.Count == 0)
        {
            ShowToast(I18nService.T("Msg.AgBatchEmpty"), isError: true);
            return;
        }

        var count = _profiles.Count;
        int successCount = 0;
        int failCount = 0;

        for (int i = 0; i < _profiles.Count; i++)
        {
            var p = _profiles[i];
            p.IsActivating = true;
            Dispatcher.Invoke(() => ProfileList.Items.Refresh());
            SetBusy(I18nService.F("Msg.AgBatchProgressFmt", i + 1, count, p.Email));

            try
            {
                var (ok, latencyMs, msg, rateLimited) = await AgQuotaService.ActivateAccountQuotaAsync(p);
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
            finally
            {
                p.IsActivating = false;
            }

            RefreshAntigravity();

            if (i < _profiles.Count - 1)
            {
                var delay = Random.Shared.Next(1500, 3000);
                SetBusy(I18nService.F("Msg.AgBatchCooldownFmt", i + 1, count, delay / 1000.0));
                await Task.Delay(delay);
            }
        }

        ClearBusy();
        RefreshAntigravity();
        ShowToast(I18nService.F("Msg.AgBatchDoneFmt", successCount, failCount));
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
                    ShowToast(I18nService.F("Msg.AgRefreshQuotaProFmt", target.Email));
                else
                    ShowToast(I18nService.F("Msg.AgRefreshQuotaFmt", target.Email));
            }
            else
            {
                ShowToast(I18nService.F("Msg.AgRefreshQuotaFailFmt", target.Email), isError: true);
            }
        }
        catch (Exception ex)
        {
            ShowToast(I18nService.F("Msg.AgRefreshQuotaErrorFmt", ex.Message), isError: true);
        }
    }

    void OnRowSwitchAntigravity(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is Profile p) _ = SwitchToProfile(p);
    }

    async Task SwitchToProfile(Profile target)
    {
        SetBusy(I18nService.F("Msg.AgSwitchingFmt", target.Email));
        try
        {
            var stopped = await Task.Run(AgProcess.StopIdeAsync);
            if (!stopped)
            {
                ClearBusy();
                ShowToast(I18nService.T("Msg.AgSwitchIdeCloseFail"), isError: true);
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
            ShowToast(I18nService.F("Msg.AgSwitchFailFmt", ex.Message), isError: true);
            return;
        }

        if (AutoRestartCheck.IsChecked == true)
        {
            try { AgProcess.StartIde(); }
            catch (Exception ex) { ClearBusy(); ShowToast(I18nService.F("Msg.AgIdeRestartFailFmt", ex.Message), isError: true); return; }
        }

        ClearBusy();
        RefreshAntigravity();
        ShowToast((AutoRestartCheck.IsChecked == true ? I18nService.F("Msg.AgSwitchedRestartFmt", target.Email) : I18nService.F("Msg.AgSwitchedFmt", target.Email)));
    }

    void OnCardDeleteAntigravity(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not Profile target) return;
        if (!Confirm(I18nService.F("Common.DeleteArchiveConfirmFmt", target.Email))) return;
        try { ProfileStore.Delete(target); }
        catch (Exception ex) { Warn(I18nService.F("Common.DeleteFailFmt", ex.Message)); return; }
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
                if (!silent) ShowToast(I18nService.T("Msg.AgRefreshNone"));
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
                    ShowToast(I18nService.F("Msg.AgRefreshAllDoneFmt", _profiles.Count));
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
        catch (Exception ex) { Warn(I18nService.F("Common.LaunchIdeFailFmt", ex.Message)); }
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

    void OnOpenBugFeedbackClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("https://github.com/kbkkb/APISwitch/issues") { UseShellExecute = true });
        }
        catch { }
    }

    // ==================== Modern Real-time Floating Drag & Drop Engine ====================

    enum DragCategory { None, Tab, Card }
    DragCategory _activeDragCategory = DragCategory.None;

    // Tab dragging
    TabItem? _draggedTab;
    WpfPoint _tabDragStartPoint;
    WpfPoint _tabGrabOffset;
    bool _isTabDragging = false;
    bool _isReorderingTabs = false;

    // Card dragging
    System.Windows.Controls.ListBox? _draggedCardListBox;
    ListBoxItem? _draggedCardItem;
    object? _draggedCardData;
    WpfPoint _cardDragStartPoint;
    WpfPoint _cardGrabOffset;
    bool _isCardDragging = false;
    bool _isReorderingCards = false;
    double _cardFixedX = 250;

    static string TabOrderFile => Path.Combine(AgPaths.AppData, "APISwitch", "tab-order.json");

    static T? FindVisualParent<T>(DependencyObject? child) where T : DependencyObject
    {
        while (child != null)
        {
            if (child is T parent) return parent;
            child = System.Windows.Media.VisualTreeHelper.GetParent(child);
        }
        return null;
    }

    static bool IsVisualChildOf(DependencyObject? child, DependencyObject parent)
    {
        while (child != null)
        {
            if (ReferenceEquals(child, parent)) return true;
            child = VisualTreeHelper.GetParent(child);
        }
        return false;
    }

    void OnTabItemPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;

        // The TabItem Header is on the sidebar (Column 0, width 236).
        // If the click is anywhere on the right content area (X >= 236), ignore completely so cards can be dragged!
        var pos = e.GetPosition(this);
        if (pos.X >= 236) return;

        var dep = e.OriginalSource as DependencyObject;
        if (sender is TabItem tab)
        {
            var bd = tab.Template?.FindName("Bd", tab) as FrameworkElement ?? tab;
            if (bd != null && !IsVisualChildOf(dep, bd))
            {
                return;
            }

            // Clear any card drag state
            _draggedCardListBox = null;
            _draggedCardItem = null;
            _draggedCardData = null;
            _isCardDragging = false;

            _draggedTab = tab;
            _tabDragStartPoint = pos;
            _tabGrabOffset = e.GetPosition(bd);
            _isTabDragging = false;
        }
    }

    void OnCardListPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        if (sender is not System.Windows.Controls.ListBox lb) return;

        // Clear any tab drag state
        _draggedTab = null;
        _isTabDragging = false;

        var dep = e.OriginalSource as DependencyObject;
        if (FindVisualParent<System.Windows.Controls.Primitives.ButtonBase>(dep) != null ||
            FindVisualParent<System.Windows.Controls.TextBox>(dep) != null ||
            FindVisualParent<System.Windows.Controls.PasswordBox>(dep) != null ||
            FindVisualParent<System.Windows.Controls.ComboBox>(dep) != null ||
            FindVisualParent<System.Windows.Controls.CheckBox>(dep) != null)
        {
            return;
        }

        var item = FindVisualParent<ListBoxItem>(dep);
        if (item != null && item.DataContext != null)
        {
            _draggedCardListBox = lb;
            _draggedCardItem = item;
            _draggedCardData = item.DataContext;
            _cardDragStartPoint = e.GetPosition(this);
            _cardGrabOffset = e.GetPosition(item);
            _isCardDragging = false;
        }
    }

    void OnWindowPreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            if (_isTabDragging || _isCardDragging)
            {
                EndAllDrag();
            }
            return;
        }

        var currentPos = e.GetPosition(this);

        // 1. Check initiation of Tab Drag
        if (_draggedTab != null && !_isTabDragging && _activeDragCategory == DragCategory.None)
        {
            var diff = currentPos - _tabDragStartPoint;
            if (Math.Abs(diff.Y) > 5 || Math.Abs(diff.X) > 5)
            {
                StartTabDrag(currentPos);
            }
        }

        // 2. Check initiation of Card Drag
        if (_draggedCardItem != null && !_isCardDragging && _activeDragCategory == DragCategory.None)
        {
            var diff = currentPos - _cardDragStartPoint;
            if (Math.Abs(diff.Y) > 5 || Math.Abs(diff.X) > 5)
            {
                StartCardDrag(currentPos);
            }
        }

        // 3. Process Tab Dragging
        if (_isTabDragging && _draggedTab != null)
        {
            UpdateGhostPosition();
            LiveReorderTab(currentPos);
            e.Handled = true;
            return;
        }

        // 4. Process Card Dragging
        if (_isCardDragging && _draggedCardListBox != null && _draggedCardData != null)
        {
            UpdateGhostPosition();
            LiveReorderCard(currentPos);
            e.Handled = true;
            return;
        }
    }

    void StartTabDrag(WpfPoint mousePos)
    {
        if (_draggedTab == null) return;
        _isTabDragging = true;
        _activeDragCategory = DragCategory.Tab;

        // Render only the sidebar tab button Bd, NEVER the full content page
        FrameworkElement targetVisual = _draggedTab;
        if (_draggedTab.Template?.FindName("Bd", _draggedTab) is FrameworkElement bd)
        {
            targetVisual = bd;
        }

        double w = targetVisual.ActualWidth > 0 ? targetVisual.ActualWidth : 216;
        double h = targetVisual.ActualHeight > 0 ? targetVisual.ActualHeight : 42;

        if (_tabGrabOffset.X <= 0 || _tabGrabOffset.X > w || _tabGrabOffset.Y <= 0 || _tabGrabOffset.Y > h)
        {
            _tabGrabOffset = new WpfPoint(Math.Min(30, w / 2), Math.Min(20, h / 2));
        }

        DragGhostRect.Width = w;
        DragGhostRect.Height = h;
        DragGhostBorder.Width = w;
        DragGhostBorder.Height = h;
        DragGhostBorder.CornerRadius = new CornerRadius(8);
        DragGhostRect.RadiusX = 8;
        DragGhostRect.RadiusY = 8;

        int width = Math.Max(1, (int)Math.Ceiling(w));
        int height = Math.Max(1, (int)Math.Ceiling(h));

        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            var vb = new VisualBrush(targetVisual)
            {
                Stretch = Stretch.None,
                Viewbox = new Rect(0, 0, w, h),
                ViewboxUnits = BrushMappingMode.Absolute
            };
            dc.DrawRectangle(vb, null, new Rect(0, 0, w, h));
        }
        var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        DragGhostRect.Fill = new ImageBrush(rtb);

        targetVisual.Opacity = 0.35;
        DragOverlayCanvas.Visibility = Visibility.Visible;
        UpdateGhostPosition();

        this.CaptureMouse();
        Mouse.OverrideCursor = System.Windows.Input.Cursors.SizeAll;
    }

    void StartCardDrag(WpfPoint mousePos)
    {
        if (_draggedCardItem == null || _draggedCardListBox == null) return;
        _isCardDragging = true;
        _activeDragCategory = DragCategory.Card;

        double w = _draggedCardItem.ActualWidth;
        double h = _draggedCardItem.ActualHeight;
        if (w <= 0 || h <= 0) return;

        try
        {
            var origin = _draggedCardItem.TranslatePoint(new WpfPoint(0, 0), this);
            _cardFixedX = Math.Max(200, origin.X);
        }
        catch
        {
            _cardFixedX = 250;
        }

        if (_cardGrabOffset.X <= 0 || _cardGrabOffset.X > w || _cardGrabOffset.Y <= 0 || _cardGrabOffset.Y > h)
        {
            _cardGrabOffset = new WpfPoint(Math.Min(40, w / 2), Math.Min(25, h / 2));
        }

        DragGhostRect.Width = w;
        DragGhostRect.Height = h;
        DragGhostBorder.Width = w;
        DragGhostBorder.Height = h;
        DragGhostBorder.CornerRadius = new CornerRadius(12);
        DragGhostRect.RadiusX = 12;
        DragGhostRect.RadiusY = 12;

        int width = Math.Max(1, (int)Math.Ceiling(w));
        int height = Math.Max(1, (int)Math.Ceiling(h));

        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            var vb = new VisualBrush(_draggedCardItem)
            {
                Stretch = Stretch.None,
                Viewbox = new Rect(0, 0, w, h),
                ViewboxUnits = BrushMappingMode.Absolute
            };
            dc.DrawRectangle(vb, null, new Rect(0, 0, w, h));
        }
        var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        DragGhostRect.Fill = new ImageBrush(rtb);

        _draggedCardItem.Opacity = 0.25;
        DragOverlayCanvas.Visibility = Visibility.Visible;
        UpdateGhostPosition();

        this.CaptureMouse();
        Mouse.OverrideCursor = System.Windows.Input.Cursors.SizeAll;
    }

    void UpdateGhostPosition()
    {
        if (DragOverlayCanvas == null || DragGhostBorder == null) return;

        double ghostH = DragGhostBorder.Height > 0 ? DragGhostBorder.Height : 50;
        var mouseWin = Mouse.GetPosition(this);

        double x;
        double y;

        if (_isTabDragging)
        {
            x = 8;
            y = mouseWin.Y - _tabGrabOffset.Y;
            double minY = 46;
            double maxY = Math.Max(minY, this.ActualHeight - ghostH - 12);
            y = Math.Clamp(y, minY, maxY);
        }
        else // Card dragging
        {
            double deltaX = Math.Clamp(mouseWin.X - _cardDragStartPoint.X, -25, 25);
            x = _cardFixedX + deltaX;

            y = mouseWin.Y - _cardGrabOffset.Y;
            double minY = 46;
            double maxY = Math.Max(minY, this.ActualHeight - ghostH - 12);
            y = Math.Clamp(y, minY, maxY);
        }

        Canvas.SetLeft(DragGhostBorder, x);
        Canvas.SetTop(DragGhostBorder, y);
    }

    void OnWindowPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_isTabDragging || _isCardDragging)
        {
            EndAllDrag();
            e.Handled = true;
        }
        else
        {
            _draggedTab = null;
            _draggedCardItem = null;
            _draggedCardData = null;
            _draggedCardListBox = null;
            _isTabDragging = false;
            _isCardDragging = false;
            _activeDragCategory = DragCategory.None;
        }
    }

    protected override void OnLostMouseCapture(System.Windows.Input.MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (_isReorderingTabs || _isReorderingCards) return;
        if (Mouse.LeftButton == MouseButtonState.Pressed)
        {
            if (_isTabDragging || _isCardDragging)
            {
                this.CaptureMouse();
                return;
            }
        }
        if (_isTabDragging || _isCardDragging)
        {
            EndAllDrag();
        }
    }

    void EndAllDrag()
    {
        bool wasTabDragging = _isTabDragging;
        bool wasCardDragging = _isCardDragging;
        var finishedCardLb = _draggedCardListBox;

        DragOverlayCanvas.Visibility = Visibility.Collapsed;
        DragGhostRect.Fill = null;

        if (_draggedTab != null)
        {
            _draggedTab.Opacity = 1.0;
            if (_draggedTab.Template?.FindName("Bd", _draggedTab) is FrameworkElement bd)
            {
                bd.Opacity = 1.0;
            }
        }
        if (_draggedCardItem != null)
        {
            _draggedCardItem.Opacity = 1.0;
        }
        if (finishedCardLb != null)
        {
            for (int i = 0; i < finishedCardLb.Items.Count; i++)
            {
                if (finishedCardLb.ItemContainerGenerator.ContainerFromIndex(i) is ListBoxItem lbi)
                {
                    lbi.Opacity = 1.0;
                }
            }
        }

        this.ReleaseMouseCapture();
        Mouse.OverrideCursor = null;

        if (wasTabDragging)
        {
            SaveTabOrder();
        }

        if (wasCardDragging && finishedCardLb != null)
        {
            SaveCardOrder(finishedCardLb);
        }

        _draggedTab = null;
        _isTabDragging = false;
        _isReorderingTabs = false;

        _draggedCardListBox = null;
        _draggedCardItem = null;
        _draggedCardData = null;
        _isCardDragging = false;
        _isReorderingCards = false;
        _activeDragCategory = DragCategory.None;
    }

    void SaveCardOrder(System.Windows.Controls.ListBox lb)
    {
        try
        {
            if (lb == ProfileList)
            {
                var deduped = _profiles.GroupBy(p => !string.IsNullOrEmpty(p.Email) ? p.Email : p.Name, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
                ProfileStore.SaveOrder(deduped);
            }
            else if (lb == CodexList)
            {
                var list = CliStore.DeduplicateCodex(_codexCollection.Select(r => r.P).ToList());
                _codexProviders = list;
                CliStore.SaveCodex(list);
            }
            else if (lb == ClaudeList)
            {
                var list = CliStore.DeduplicateClaude(_claudeCollection.Select(r => r.P).ToList());
                _claudeProviders = list;
                CliStore.SaveClaude(list);
            }
            else if (lb == DesktopList)
            {
                var list = CliStore.DeduplicateClaude(_desktopCollection.Select(r => r.P).ToList());
                _desktopProviders = list;
                CliStore.SaveClaudeDesktop(list);
            }
            else if (lb == OcList)
            {
                var list = CliStore.DeduplicateOpenCode(_ocCollection.Select(r => r.P).ToList());
                _openCodeProviders = list;
                CliStore.SaveOpencode(list);
            }
            else if (lb == PiList)
            {
                var list = CliStore.DeduplicatePi(_piCollection.Select(r => r.P).ToList());
                _piProviders = list;
                CliStore.SavePiProviders(list);
            }
        }
        catch { }
    }

    void LiveReorderTab(WpfPoint currentPos)
    {
        if (_draggedTab == null || _isReorderingTabs) return;

        int currentIndex = Tabs.Items.IndexOf(_draggedTab);
        if (currentIndex < 0) return;

        // Check swapping with upper tab
        if (currentIndex > 0 && Tabs.Items[currentIndex - 1] is TabItem prevTab)
        {
            var prevVisual = prevTab.Template?.FindName("Bd", prevTab) as FrameworkElement ?? prevTab;
            if (TryGetMidY(prevVisual, this, out double prevMid) && currentPos.Y < prevMid)
            {
                _isReorderingTabs = true;
                try
                {
                    var selected = Tabs.SelectedItem;
                    Tabs.Items.RemoveAt(currentIndex);
                    Tabs.Items.Insert(currentIndex - 1, _draggedTab);
                    Tabs.SelectedItem = selected;
                    Tabs.UpdateLayout();
                    this.CaptureMouse();
                }
                catch { }
                finally
                {
                    _isReorderingTabs = false;
                }
                return;
            }
        }

        // Check swapping with lower tab
        if (currentIndex < Tabs.Items.Count - 1 && Tabs.Items[currentIndex + 1] is TabItem nextTab)
        {
            var nextVisual = nextTab.Template?.FindName("Bd", nextTab) as FrameworkElement ?? nextTab;
            if (TryGetMidY(nextVisual, this, out double nextMid) && currentPos.Y > nextMid)
            {
                _isReorderingTabs = true;
                try
                {
                    var selected = Tabs.SelectedItem;
                    Tabs.Items.RemoveAt(currentIndex);
                    Tabs.Items.Insert(currentIndex + 1, _draggedTab);
                    Tabs.SelectedItem = selected;
                    Tabs.UpdateLayout();
                    this.CaptureMouse();
                }
                catch { }
                finally
                {
                    _isReorderingTabs = false;
                }
                return;
            }
        }
    }

    static bool TryGetMidY(FrameworkElement? el, UIElement relativeTo, out double midY)
    {
        midY = 0;
        if (el == null || !el.IsLoaded || !el.IsVisible) return false;
        try
        {
            var pt = el.TranslatePoint(new WpfPoint(0, el.ActualHeight / 2), relativeTo);
            midY = pt.Y;
            return true;
        }
        catch
        {
            return false;
        }
    }

    void LiveReorderCard(WpfPoint currentPos)
    {
        if (_draggedCardListBox == null || _draggedCardData == null || _isReorderingCards) return;

        var lb = _draggedCardListBox;
        int count = lb.Items.Count;
        if (count <= 1) return;

        int currentIndex = -1;
        for (int i = 0; i < count; i++)
        {
            if (ReferenceEquals(lb.Items[i], _draggedCardData) || IsMatchingRow(lb.Items[i], _draggedCardData))
            {
                currentIndex = i;
                break;
            }
        }
        if (currentIndex < 0) return;

        // Auto-scroll when dragging near top/bottom edges of the scroll container
        var sv = FindVisualParent<ScrollViewer>(lb);
        if (sv != null)
        {
            try
            {
                var svTopLeft = sv.TranslatePoint(new WpfPoint(0, 0), this);
                double relY = currentPos.Y - svTopLeft.Y;
                if (relY < 40 && sv.VerticalOffset > 0)
                {
                    sv.ScrollToVerticalOffset(Math.Max(0, sv.VerticalOffset - 14));
                }
                else if (relY > sv.ActualHeight - 40 && sv.VerticalOffset < sv.ScrollableHeight)
                {
                    sv.ScrollToVerticalOffset(Math.Min(sv.ScrollableHeight, sv.VerticalOffset + 14));
                }
            }
            catch { }
        }

        // Check upper card (Move UP)
        if (currentIndex > 0)
        {
            var prevContainer = (lb.ItemContainerGenerator.ContainerFromIndex(currentIndex - 1) as FrameworkElement)
                                ?? FindListBoxItemByData(lb, lb.Items[currentIndex - 1]);
            if (TryGetMidY(prevContainer, this, out double prevMid) && currentPos.Y < prevMid)
            {
                SwapCardItems(currentIndex, currentIndex - 1);
                return;
            }
        }

        // Check lower card (Move DOWN)
        if (currentIndex < count - 1)
        {
            var nextContainer = (lb.ItemContainerGenerator.ContainerFromIndex(currentIndex + 1) as FrameworkElement)
                                ?? FindListBoxItemByData(lb, lb.Items[currentIndex + 1]);
            if (TryGetMidY(nextContainer, this, out double nextMid) && currentPos.Y > nextMid)
            {
                SwapCardItems(currentIndex, currentIndex + 1);
                return;
            }
        }
    }

    void SwapCardItems(int fromIndex, int toIndex)
    {
        if (_isReorderingCards) return;
        _isReorderingCards = true;
        try
        {
            var lb = _draggedCardListBox;
            if (lb == null) return;
            int count = lb.Items.Count;
            if (fromIndex < 0 || fromIndex >= count || toIndex < 0 || toIndex >= count || fromIndex == toIndex) return;

            if (lb == ProfileList)
            {
                _profiles.Move(fromIndex, toIndex);
            }
            else if (lb == CodexList)
            {
                _codexCollection.Move(fromIndex, toIndex);
            }
            else if (lb == ClaudeList)
            {
                _claudeCollection.Move(fromIndex, toIndex);
            }
            else if (lb == DesktopList)
            {
                _desktopCollection.Move(fromIndex, toIndex);
            }
            else if (lb == OcList)
            {
                _ocCollection.Move(fromIndex, toIndex);
            }
            else if (lb == PiList)
            {
                _piCollection.Move(fromIndex, toIndex);
            }

            lb.UpdateLayout();

            // Synchronize opacity: ensure only the item at toIndex is dimmed, all others 1.0
            for (int i = 0; i < lb.Items.Count; i++)
            {
                var lbi = lb.ItemContainerGenerator.ContainerFromIndex(i) as ListBoxItem
                          ?? FindListBoxItemByData(lb, lb.Items[i]);
                if (lbi != null)
                {
                    if (i == toIndex)
                    {
                        lbi.Opacity = 0.25;
                        _draggedCardItem = lbi;
                    }
                    else
                    {
                        lbi.Opacity = 1.0;
                    }
                }
            }
        }
        catch { }
        finally
        {
            _isReorderingCards = false;
        }
    }

    static bool IsMatchingRow(object? a, object? b)
    {
        if (a == null || b == null) return false;
        if (ReferenceEquals(a, b)) return true;
        if (a is Profile pa && b is Profile pb)
            return string.Equals(pa.Email, pb.Email, StringComparison.OrdinalIgnoreCase);
        if (a is CodexRow ca && b is CodexRow cb)
            return (string.Equals(ca.P.Id, cb.P.Id, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(ca.P.Name, cb.P.Name, StringComparison.OrdinalIgnoreCase)) ||
                   string.Equals(ca.P.Name, cb.P.Name, StringComparison.OrdinalIgnoreCase);
        if (a is ClaudeRow cla && b is ClaudeRow clb)
            return string.Equals(cla.P.Name, clb.P.Name, StringComparison.OrdinalIgnoreCase);
        if (a is OpenCodeRow oa && b is OpenCodeRow ob)
            return string.Equals(oa.P.Id, ob.P.Id, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(oa.P.Name, ob.P.Name, StringComparison.OrdinalIgnoreCase);
        if (a is PiRow pia && b is PiRow pib)
            return string.Equals(pia.P.Id, pib.P.Id, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(pia.P.Name, pib.P.Name, StringComparison.OrdinalIgnoreCase);
        return false;
    }

    static IEnumerable<T> FindVisualChildren<T>(DependencyObject? depObj) where T : DependencyObject
    {
        if (depObj == null) yield break;
        int count = VisualTreeHelper.GetChildrenCount(depObj);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(depObj, i);
            if (child is T t) yield return t;
            foreach (var grandChild in FindVisualChildren<T>(child))
                yield return grandChild;
        }
    }

    static ListBoxItem? FindListBoxItemByData(System.Windows.Controls.ListBox lb, object? data)
    {
        if (data == null) return null;
        if (lb.ItemContainerGenerator.ContainerFromItem(data) is ListBoxItem item)
            return item;
        return FindVisualChildren<ListBoxItem>(lb).FirstOrDefault(x => ReferenceEquals(x.DataContext, data) || IsMatchingRow(x.DataContext, data));
    }

    void SaveTabOrder()
    {
        try
        {
            var tags = Tabs.Items.OfType<TabItem>()
                .Select(t => t.Tag as string)
                .Where(t => !string.IsNullOrEmpty(t))
                .ToList();
            var dir = Path.GetDirectoryName(TabOrderFile);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(TabOrderFile, JsonSerializer.Serialize(tags));
        }
        catch { }
    }

    void RestoreTabOrder()
    {
        try
        {
            if (!File.Exists(TabOrderFile)) return;
            var tags = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(TabOrderFile));
            if (tags == null || tags.Count == 0) return;

            var allTabs = Tabs.Items.OfType<TabItem>().ToList();
            _isReorderingTabs = true;
            try
            {
                Tabs.Items.Clear();
                foreach (var tag in tags)
                {
                    var tab = allTabs.FirstOrDefault(t => (t.Tag as string) == tag);
                    if (tab != null)
                    {
                        Tabs.Items.Add(tab);
                        allTabs.Remove(tab);
                    }
                }
                foreach (var remaining in allTabs)
                {
                    Tabs.Items.Add(remaining);
                }
                if (Tabs.Items.Count > 0)
                {
                    Tabs.SelectedIndex = 0;
                }
            }
            finally
            {
                _isReorderingTabs = false;
            }
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

    // ---------- Shared row types ----------

    public class ClaudeRow
    {
        public ClaudeProvider P { get; init; } = null!;
        public bool IsCurrent { get; init; }
        public bool IsDesktop { get; init; }
        public string Name => P.Name;
        public string Initial => Name.Length > 0 ? Name.Substring(0, 1).ToUpperInvariant() : "?";
        public string BaseUrl => P.IsOfficial ? I18nService.T("Common.OfficialNoEndpoint") : (string.IsNullOrEmpty(P.BaseUrl) ? "—" : P.BaseUrl!);
        public string Model => string.IsNullOrEmpty(P.Model) ? "—" : P.Model!;

        public bool RequiresRouter => !P.IsOfficial && (
            string.Equals(P.WireApi, "chat", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(P.WireApi, "responses", StringComparison.OrdinalIgnoreCase) ||
            (IsDesktop && string.Equals(P.AccessMode, "mapping", StringComparison.OrdinalIgnoreCase)) ||
            (!IsDesktop && P.ModelMappings != null && P.ModelMappings.Any(m => m.Supports1m)) ||
            (P.ExtraOptions != null && P.ExtraOptions.TryGetValue("require_proxy", out var rp) && bool.TryParse(rp, out var b) && b));

        public Visibility RouterBadgeVisibility => RequiresRouter ? Visibility.Visible : Visibility.Collapsed;

        public bool IsRouterActive => IsDesktop ? LocalProxyServer.IsClaudeDesktopEnabled : LocalProxyServer.IsClaudeCliEnabled;

        public string RouterBadgeText => IsRouterActive ? I18nService.T("Common.NeedRouterOn") : I18nService.T("Common.NeedRouterOff");

        public string RouterBadgeTooltip => IsRouterActive
            ? (string.Equals(P.WireApi, "chat", StringComparison.OrdinalIgnoreCase)
                ? I18nService.T("Row.TipChatActive")
                : (string.Equals(P.WireApi, "responses", StringComparison.OrdinalIgnoreCase)
                    ? I18nService.T("Row.TipResponsesActive")
                    : (IsDesktop && string.Equals(P.AccessMode, "mapping", StringComparison.OrdinalIgnoreCase)
                        ? I18nService.T("Row.TipMappingActive")
                        : I18nService.T("Row.Tip1mActive"))))
            : (string.Equals(P.WireApi, "chat", StringComparison.OrdinalIgnoreCase)                ? I18nService.T("Row.TipChatInactive")
                : (string.Equals(P.WireApi, "responses", StringComparison.OrdinalIgnoreCase)
                    ? I18nService.T("Row.TipResponsesInactive")
                    : (IsDesktop && string.Equals(P.AccessMode, "mapping", StringComparison.OrdinalIgnoreCase)
                        ? I18nService.T("Row.TipMappingInactive")
                        : I18nService.T("Row.Tip1mInactive"))));

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
                if (P.IsOfficial) return I18nService.T("Common.OfficialNoEndpoint");

                var parts = new List<string?> { string.IsNullOrEmpty(P.BaseUrl) ? "—" : P.BaseUrl };

                if (string.Equals(P.WireApi, "chat", StringComparison.OrdinalIgnoreCase))
                {
                    parts.Add(I18nService.T("Row.SubChatNeedsRouter"));
                }
                else if (string.Equals(P.WireApi, "responses", StringComparison.OrdinalIgnoreCase))
                {
                    parts.Add(I18nService.T("Row.SubResponsesNeedsRouter"));
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
                        parts.Add(I18nService.F("Row.SubModelFmt", P.Model));
                    }
                }
                else if (!string.IsNullOrEmpty(P.Model))
                {
                    parts.Add(I18nService.F("Row.SubModelFmt", P.Model));
                }

                return string.Join("   ·   ", parts.Where(s => !string.IsNullOrWhiteSpace(s)));
            }
        }

        public string Status => IsCurrent ? I18nService.T("Row.StatusCurrent") : "";
    }

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

        _claudeCollection.Clear();
        var seenClaude = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in rows)
        {
            var key = (!string.IsNullOrEmpty(r.P.Id) ? r.P.Id : r.P.Name).Trim();
            if (seenClaude.Add(key))
            {
                _claudeCollection.Add(r);
            }
        }
        if (ClaudeList.ItemsSource != _claudeCollection) ClaudeList.ItemsSource = _claudeCollection;

        var active = rows.FirstOrDefault(r => r.IsCurrent);
        ClaudeCurrentText.Text = active != null
            ? active.Name + (active.P.IsOfficial ? "" : "  (" + active.P.BaseUrl + ")")
            : (!string.IsNullOrEmpty(currentName) ? currentName : (string.IsNullOrEmpty(currentUrl) ? I18nService.T("Msg.OfficialCurrent") : I18nService.F("Msg.UnlistedEndpointFmt", currentUrl)));

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
        if (!Confirm(I18nService.F("Common.DeleteProviderConfirmFmt", row.Name))) return;
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
        catch (Exception ex) { ShowToast(I18nService.F("Common.ApplyFailFmt", ex.Message), isError: true); return; }
        RefreshClaude();
        if (LocalProxyServer.IsClaudeCliEnabled && !row.P.IsOfficial)
        {
            ShowToast(autoEnabled
                ? I18nService.F("Msg.SwitchedRouterAutoFmt", "Claude CLI", row.Name)
                : I18nService.F("Msg.SwitchedRouterFmt", "Claude CLI", row.Name));
        }
        else
        {
            ShowToast(I18nService.F("Msg.SwitchedPlainFmt", "Claude CLI", row.Name));
        }
    }

    async void EditClaude(ClaudeProvider? existing)
    {
        if (_activeProviderDialog != null && _activeProviderDialog.IsLoaded)
        {
            _activeProviderDialog.Activate();
            _activeProviderDialog.Focus();
            ShowToast(I18nService.T("Msg.EditorBusy"));
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
        ShowToast(isCurrent ? I18nService.F("Msg.SavedCurrentFmt", p.Name) : I18nService.F("Msg.SavedProviderFmt", p.Name));
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

        _desktopCollection.Clear();
        var seenDesk = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in rows)
        {
            var key = (!string.IsNullOrEmpty(r.P.Id) ? r.P.Id : r.P.Name).Trim();
            if (seenDesk.Add(key))
            {
                _desktopCollection.Add(r);
            }
        }
        if (DesktopList.ItemsSource != _desktopCollection) DesktopList.ItemsSource = _desktopCollection;

        var active = rows.FirstOrDefault(r => r.IsCurrent);
        DesktopCurrentText.Text = active != null
            ? active.Name + (active.P.IsOfficial ? "" : "  (" + active.P.BaseUrl + ")")
            : (!string.IsNullOrEmpty(currentName) ? currentName : (string.IsNullOrEmpty(currentUrl) ? I18nService.T("Msg.OfficialGatewayCurrent") : I18nService.F("Msg.UnlistedGatewayFmt", currentUrl)));

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
        if (!Confirm(I18nService.F("Common.DeleteProviderConfirmFmt", row.Name))) return;
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
        catch (Exception ex) { ShowToast(I18nService.F("Common.ApplyFailFmt", ex.Message), isError: true); return; }
        RefreshDesktop();
        if (LocalProxyServer.IsClaudeDesktopEnabled && !row.P.IsOfficial)
        {
            ShowToast(autoEnabled
                ? I18nService.F("Msg.SwitchedRouterAutoFmt", I18nService.T("Common.ClaudeClient"), row.Name)
                : I18nService.F("Msg.SwitchedRouterFmt", I18nService.T("Common.ClaudeClient"), row.Name));
        }
        else
        {
            ShowToast(I18nService.F("Msg.DesktopSwitchedNeedRestartFmt", row.Name));
        }
    }

    async void EditDesktop(ClaudeProvider? existing)
    {
        if (_activeProviderDialog != null && _activeProviderDialog.IsLoaded)
        {
            _activeProviderDialog.Activate();
            _activeProviderDialog.Focus();
            ShowToast(I18nService.T("Msg.EditorBusy"));
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
        ShowToast(isCurrent ? I18nService.F("Msg.SavedDesktopCurrentFmt", p.Name) : I18nService.F("Msg.SavedProviderFmt", p.Name));
    }

    async void OnRestartClaudeDesktop(object sender, RoutedEventArgs e)
    {
        ShowToast(I18nService.F("Msg.RestartingFmt", "Claude"));
        try
        {
            await ClaudeProcess.RestartClaudeAsync();
            ShowToast(I18nService.F("Msg.RestartedFmt", "Claude"));
        }
        catch (Exception ex)
        {
            ShowToast(I18nService.F("Msg.RestartFailFmt", "Claude", ex.Message), isError: true);
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
            RouterStatusText.Text = activeCount > 0 ? I18nService.F("Router.TitleOnFmt", port, activeCount) : I18nService.T("Main.RouterOff");
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
            CodexToolbarRouterText.Text = isCodex ? I18nService.F("Router.ToolbarOnFmt", port) : I18nService.T("Router.ToolbarOff");
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
        if (ClaudeToolbarRouterBtn != null)
        {
            ClaudeToolbarRouterBtn.Background = isClaudeCli ? activeBg : inactiveBg;
            ClaudeToolbarRouterBtn.BorderBrush = isClaudeCli ? activeBorder : inactiveBorder;
        }
        if (ClaudeToolbarRouterDot != null) ClaudeToolbarRouterDot.Fill = isClaudeCli ? onBrush : offBrush;
        if (ClaudeToolbarRouterText != null)
        {
            ClaudeToolbarRouterText.Text = isClaudeCli ? I18nService.F("Router.ToolbarOnFmt", port) : I18nService.T("Router.ToolbarOff");
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
                    ? I18nService.F("Router.TakeoverTransparentFmt", port)
                    : I18nService.T("Router.DirectModeNoRouter");
            }
            else
            {
                ClaudeRouterChipText.Text = isClaudeCli ? I18nService.F("Router.StandbyFmt", port) : I18nService.T("Router.DirectMode");
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
            DesktopToolbarRouterText.Text = isClaudeDesktop ? I18nService.F("Router.ToolbarOnFmt", port) : I18nService.T("Router.ToolbarOff");
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
                    ? I18nService.F("Router.TakeoverTransparentFmt", port)
                    : I18nService.T("Router.DirectModeNoRouter");
            }
            else
            {
                DesktopRouterChipText.Text = isClaudeDesktop ? I18nService.F("Router.StandbyFmt", port) : I18nService.T("Router.DirectMode");
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

    void OnToggleProxyServer(object sender, RoutedEventArgs e)
    {
        var newState = !LocalProxyServer.IsEnabled;
        LocalProxyServer.SetEnabled(newState);
        RefreshCodex();
        RefreshClaude();
        RefreshDesktop();
        if (newState)
        {
            ShowToast(I18nService.F("Router.AllOnFmt", LocalProxyServer.Port));
        }
        else
        {
            ShowToast(I18nService.T("Router.AllOff"));
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


    async void OnRestartCodex(object sender, RoutedEventArgs e)
    {
        ShowToast(I18nService.F("Msg.RestartingFmt", "Codex"));
        try
        {
            await CodexProcess.RestartCodexAsync();
            ShowToast(I18nService.F("Msg.RestartedFmt", "Codex"));
        }
        catch (Exception ex)
        {
            ShowToast(I18nService.F("Msg.RestartFailFmt", "Codex", ex.Message), isError: true);
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
        public Visibility UnpoolBadgeVisibility => !IsInConfig ? Visibility.Visible : Visibility.Collapsed;
        public Visibility AddBtnVisibility => !IsInConfig ? Visibility.Visible : Visibility.Collapsed;
        public Visibility RemoveBtnVisibility => IsInConfig ? Visibility.Visible : Visibility.Collapsed;

        string ModelsSummary()
        {
            try
            {
                if (P.CustomModels != null && P.CustomModels.Count > 0)
                    return I18nService.F("Common.ModelsCountFmt", P.CustomModels.Count);
                if (string.IsNullOrWhiteSpace(P.ModelsJson)) return I18nService.T("Common.NoModels");
                if (JsonNode.Parse(P.ModelsJson) is JsonObject models)
                {
                    var count = models.Count;
                    return count == 0 ? I18nService.T("Common.NoModels") : I18nService.F("Common.ModelsCountFmt", count);
                }
                return I18nService.T("Common.NoModels");
            }
            catch
            {
                return I18nService.T("Common.NoModels");
            }
        }
    }

    void RefreshOpencode()
    {
        _openCodeProviders = CliStore.LoadOpencode();
        var liveIds = new HashSet<string>(OpenCodeCli.ProviderIds(), StringComparer.OrdinalIgnoreCase);

        int inPoolCount = _openCodeProviders.Count(p => (!string.IsNullOrEmpty(p.Id) && liveIds.Contains(p.Id)) || (!string.IsNullOrEmpty(p.Name) && liveIds.Contains(p.Name)));
        if (OcPoolCountText != null)
            OcPoolCountText.Text = I18nService.F("Common.PoolCountFmt", inPoolCount, _openCodeProviders.Count);
        UpdateOcDefaultModelText();

        var rows = _openCodeProviders.Select(p =>
        {
            bool inCfg = (!string.IsNullOrEmpty(p.Id) && liveIds.Contains(p.Id)) || (!string.IsNullOrEmpty(p.Name) && liveIds.Contains(p.Name));
            return new OpenCodeRow
            {
                P = p,
                IsInConfig = inCfg,
            };
        }).ToList();

        _ocCollection.Clear();
        var seenOc = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in rows)
        {
            var key = (!string.IsNullOrEmpty(r.P?.Id) ? r.P.Id : (r.P?.Name ?? "")).Trim();
            if (!string.IsNullOrEmpty(key) && seenOc.Add(key))
            {
                _ocCollection.Add(r);
            }
        }
        if (OcList.ItemsSource != _ocCollection) OcList.ItemsSource = _ocCollection;
        OcEmpty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    void OnAddOpencode(object sender, RoutedEventArgs e) => EditOpencode(null);

    void OnRowAddOc(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not OpenCodeRow row) return;
        try
        {
            OpenCodeCli.SaveProvider(row.P,
                setDefaultModel: !string.IsNullOrEmpty(row.P.DefaultModel), row.P.DefaultModel);
            RefreshOpencode();
            ShowToast(I18nService.F("Msg.OcAddedFmt", row.Name));
        }
        catch (Exception ex)
        {
            ShowToast(I18nService.F("Common.AddFailFmt", ex.Message), isError: true);
        }
    }

    void OnRowRemoveOc(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not OpenCodeRow row) return;
        try
        {
            OpenCodeCli.DeleteProvider(row.P.Id);
            RefreshOpencode();
            ShowToast(I18nService.F("Msg.OcRemovedFmt", row.Name));
        }
        catch (Exception ex)
        {
            ShowToast(I18nService.F("Common.RemoveFailFmt", ex.Message), isError: true);
        }
    }

    void OnCardEditOc(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is OpenCodeRow row) EditOpencode(row.P);
    }

    void OnCardDeleteOc(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not OpenCodeRow row) return;
        if (!Confirm(I18nService.F("Common.DeleteForeverFmt", row.Name, "opencode.json"))) return;
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
            ShowToast(I18nService.F("Msg.DeletedProviderFmt", row.Name));
        }
        catch (Exception ex) { ShowToast(I18nService.F("Common.DeleteFailFmt", ex.Message), isError: true); }
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
                    done.Add(I18nService.T("Common.ClaudeClient"));
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
        ShowToast(I18nService.F("Msg.CopiedTargetsFmt", row.P.Id, string.Join(I18nService.T("Common.ListSep"), done)));
    }

    async void EditOpencode(OpenCodeProvider? existing)
    {
        if (_activeProviderDialog != null && _activeProviderDialog.IsLoaded)
        {
            _activeProviderDialog.Activate();
            _activeProviderDialog.Focus();
            ShowToast(I18nService.T("Msg.EditorBusy"));
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
                OpenCodeCli.SaveProvider(p, setDefaultModel: !string.IsNullOrEmpty(p.DefaultModel), p.DefaultModel);
            // OpenCode 思考强度随 models[] options 一并写出（SaveProvider 内部处理）
            }

            RefreshOpencode();
            ShowToast(I18nService.F("Msg.SavedProviderFmt", p.Id));
        }
        catch (Exception ex) { ShowToast(I18nService.F("Common.SaveFailFmt", ex.Message), isError: true); }
    }

    void OnRefreshOpencode(object sender, RoutedEventArgs e)
    {
        RefreshOpencode();
        ShowToast(I18nService.F("Msg.OcRefreshedFmt", _openCodeProviders.Count));
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
        public Visibility UnpoolBadgeVisibility => !IsInConfig ? Visibility.Visible : Visibility.Collapsed;
        public Visibility AddBtnVisibility => !IsInConfig ? Visibility.Visible : Visibility.Collapsed;
        public Visibility RemoveBtnVisibility => IsInConfig ? Visibility.Visible : Visibility.Collapsed;

        string ModelsSummary()
        {
            try
            {
                if (P.CustomModels != null && P.CustomModels.Count > 0)
                    return I18nService.F("Common.ModelsCountFmt", P.CustomModels.Count);
                if (string.IsNullOrWhiteSpace(P.ModelsJson)) return I18nService.T("Common.NoModels");
                if (JsonNode.Parse(P.ModelsJson) is JsonArray arr)
                    return arr.Count == 0 ? I18nService.T("Common.NoModels") : I18nService.F("Common.ModelsCountFmt", arr.Count);
                if (JsonNode.Parse(P.ModelsJson) is JsonObject obj)
                    return obj.Count == 0 ? I18nService.T("Common.NoModels") : I18nService.F("Common.ModelsCountFmt", obj.Count);
                return I18nService.T("Common.NoModels");
            }
            catch
            {
                return I18nService.T("Common.NoModels");
            }
        }
    }

    void RefreshPi()
    {
        _piProviders = CliStore.LoadPiProviders();
        var liveIds = new HashSet<string>(PiCli.ProviderIds(), StringComparer.OrdinalIgnoreCase);

        int inPoolCount = _piProviders.Count(p => (!string.IsNullOrEmpty(p.Id) && liveIds.Contains(p.Id)) || (!string.IsNullOrEmpty(p.Name) && liveIds.Contains(p.Name)));
        if (PiPoolCountText != null)
            PiPoolCountText.Text = I18nService.F("Common.PoolCountFmt", inPoolCount, _piProviders.Count);
        UpdatePiDefaultModelText();

        var rows = _piProviders.Select(p =>
        {
            bool inCfg = (!string.IsNullOrEmpty(p.Id) && liveIds.Contains(p.Id)) || (!string.IsNullOrEmpty(p.Name) && liveIds.Contains(p.Name));
            return new PiRow
            {
                P = p,
                IsInConfig = inCfg,
            };
        }).ToList();

        _piCollection.Clear();
        var seenPi = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in rows)
        {
            var key = (!string.IsNullOrEmpty(r.P?.Id) ? r.P.Id : (r.P?.Name ?? "")).Trim();
            if (!string.IsNullOrEmpty(key) && seenPi.Add(key))
            {
                _piCollection.Add(r);
            }
        }
        if (PiList.ItemsSource != _piCollection) PiList.ItemsSource = _piCollection;
        PiEmpty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    void OnAddPi(object sender, RoutedEventArgs e) => EditPi(null);

    void OnRowAddPi(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not PiRow row) return;
        try
        {
            PiCli.SaveProvider(row.P,
                setDefaultModel: !string.IsNullOrEmpty(row.P.DefaultModel), row.P.DefaultModel);
            RefreshPi();
            ShowToast(I18nService.F("Msg.PiAddedFmt", row.Name));
        }
        catch (Exception ex)
        {
            ShowToast(I18nService.F("Common.AddFailFmt", ex.Message), isError: true);
        }
    }

    void OnRowRemovePi(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not PiRow row) return;
        try
        {
            PiCli.DeleteProvider(row.P.Id);
            RefreshPi();
            ShowToast(I18nService.F("Msg.PiRemovedFmt", row.Name));
        }
        catch (Exception ex)
        {
            ShowToast(I18nService.F("Common.RemoveFailFmt", ex.Message), isError: true);
        }
    }

    void OnCardEditPi(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is PiRow row) EditPi(row.P);
    }

    void OnCardDeletePi(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not PiRow row) return;
        if (!Confirm(I18nService.F("Common.DeleteForeverFmt", row.Name, "models.json"))) return;
        try
        {
            _piProviders.RemoveAll(x => string.Equals(x.Id, row.P.Id, StringComparison.OrdinalIgnoreCase) || string.Equals(x.Name, row.P.Name, StringComparison.OrdinalIgnoreCase));
            CliStore.SavePiProviders(_piProviders);
            PiCli.DeleteProvider(row.P.Id);
            RefreshPi();
            ShowToast(I18nService.F("Msg.DeletedProviderFmt", row.Name));
        }
        catch (Exception ex) { ShowToast(I18nService.F("Common.DeleteFailFmt", ex.Message), isError: true); }
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
                    done.Add(I18nService.T("Common.ClaudeClient"));
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
        ShowToast(I18nService.F("Msg.CopiedTargetsFmt", row.Name, string.Join(I18nService.T("Common.ListSep"), done)));
    }

    async void EditPi(PiProvider? existing)
    {
        if (_activeProviderDialog != null && _activeProviderDialog.IsLoaded)
        {
            _activeProviderDialog.Activate();
            _activeProviderDialog.Focus();
            ShowToast(I18nService.T("Msg.EditorBusy"));
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
                PiCli.SaveProvider(p, setDefaultModel: !string.IsNullOrEmpty(p.DefaultModel), p.DefaultModel);
            }

            RefreshPi();
            ShowToast(I18nService.F("Msg.SavedProviderFmt", p.Name ?? p.Id));
        }
        catch (Exception ex) { ShowToast(I18nService.F("Common.SaveFailFmt", ex.Message), isError: true); }
    }

    void OnRefreshPi(object sender, RoutedEventArgs e)
    {
        RefreshPi();
        ShowToast(I18nService.F("Msg.PiRefreshedFmt", _piProviders.Count));
    }

    /// <summary>从 ~/.pi/agent/settings.json 实时读取默认 provider/model 显示在英雄卡。</summary>
    void UpdatePiDefaultModelText()
    {
        if (PiDefaultModelText == null) return;
        try
        {
            var (prov, model) = PiCli.CurrentDefaults();
            PiDefaultModelText.Text = string.IsNullOrEmpty(prov) && string.IsNullOrEmpty(model)
                ? I18nService.T("Common.NotSet")
                : string.Join(" / ", new[] { prov, model }.Where(s => !string.IsNullOrEmpty(s)));
        }
        catch
        {
            PiDefaultModelText.Text = I18nService.T("Common.NotSet");
        }
    }

    /// <summary>从 opencode.json 顶层 model ("provider/model") 实时读取默认模型显示在英雄卡。</summary>
    void UpdateOcDefaultModelText()
    {
        if (OcDefaultModelText == null) return;
        try
        {
            var cur = OpenCodeCli.CurrentModel();
            OcDefaultModelText.Text = string.IsNullOrEmpty(cur) ? I18nService.T("Common.NotSet") : cur;
        }
        catch
        {
            OcDefaultModelText.Text = I18nService.T("Common.NotSet");
        }
    }

    void OnOpenPiModels(object sender, RoutedEventArgs e) => OpenFile(PiCli.ModelsPath);

    void OnOpenPiDir(object sender, RoutedEventArgs e)
    {
        var dir = Path.GetDirectoryName(PiCli.AuthPath);
        if (dir == null || !Directory.Exists(dir)) { ShowToast(I18nService.T("Msg.PiDirNotFound"), isError: true); return; }
        Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
    }

    void OnCapturePi(object sender, RoutedEventArgs e)
    {
        if (!PiCli.IsInstalled) { ShowToast(I18nService.T("Msg.PiNotInstalled"), isError: true); return; }
        var (prov, _) = PiCli.CurrentDefaults();
        var name = $"pi-{prov ?? "default"}-{DateTime.Now:yyyyMMdd-HHmmss}";
        try
        {
            var account = PiCli.Capture(name);
            var idx = _piAccounts.FindIndex(a => a.Name == name);
            if (idx >= 0) _piAccounts[idx] = account; else _piAccounts.Add(account);
            CliStore.SavePi(_piAccounts);
            ShowToast(I18nService.F("Msg.PiCapturedFmt", name));
        }
        catch (Exception ex) { ShowToast(I18nService.F("Msg.PiCaptureFailFmt", ex.Message), isError: true); }
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

    void RebuildTrayMenu()
    {
        try
        {
            Dispatcher.Invoke(() =>
            {
                if (_notifyIcon != null && _notifyIcon.ContextMenuStrip is IDisposable old)
                {
                    _notifyIcon.ContextMenuStrip = null;
                    old.Dispose();
                }
                if (_notifyIcon != null)
                {
                    _notifyIcon.ContextMenuStrip = ModernTrayMenu.Create(
                        onShow: RestoreFromTray,
                        onRefresh: () => Dispatcher.Invoke(() => _ = RefreshAllAgQuotasAsync(silent: false)),
                        onLaunchIde: () => Dispatcher.Invoke(() =>
                        {
                            try { AgProcess.StartIde(); }
                            catch (Exception ex) { ShowToast(I18nService.F("Common.LaunchIdeFailFmt", ex.Message), isError: true); }
                        }),
                        onExit: ExitApp,
                        version: AppVersionText?.Text ?? "v0.1.1"
                    );
                }
            });
        }
        catch { }
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
                try
                {
                    var streamInfo = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico"));
                    if (streamInfo?.Stream != null)
                        _notifyIcon.Icon = new System.Drawing.Icon(streamInfo.Stream);
                }
                catch { }
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
                    catch (Exception ex) { ShowToast(I18nService.F("Common.LaunchIdeFailFmt", ex.Message), isError: true); }
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
            if (AppSettingsService.Current.MinimizeToTrayOnClose)
            {
                e.Cancel = true;
                MinimizeToTray();
                return;
            }
            else
            {
                ExitApp();
                return;
            }
        }
        base.OnClosing(e);
    }

    void OnMinWindow(object sender, RoutedEventArgs e) => MinimizeToTray();

    void OnMaxWindow(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    void OnCloseWindow(object sender, RoutedEventArgs e)
    {
        if (AppSettingsService.Current.MinimizeToTrayOnClose)
        {
            MinimizeToTray();
        }
        else
        {
            ExitApp();
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

    public void ApplyUpdateInfo(UpdateInfo info)
    {
        _latestUpdate = info;
        if (info.HasUpdate)
        {
            if (UpdateCheckBtn != null) UpdateCheckBtn.Visibility = Visibility.Visible;
            if (AppVersionText != null) AppVersionText.Text = $"v{info.LatestVersion}";
            if (UpdateBadgeText != null) UpdateBadgeText.Text = I18nService.T("Main.Upgrade");
        }
        else
        {
            if (UpdateCheckBtn != null) UpdateCheckBtn.Visibility = Visibility.Collapsed;
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


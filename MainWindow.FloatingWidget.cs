using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using APISwitch.Dialogs;
using APISwitch.Models;
using APISwitch.Services;
using MediaColor = System.Windows.Media.Color;

namespace APISwitch;

public partial class MainWindow
{
    FloatingWidgetWindow? _floatingWidget;
    FileSystemWatcher? _agBrainWatcher;
    FileSystemWatcher? _agHubStorageWatcher;
    System.Windows.Threading.DispatcherTimer? _agWidgetScanTimer;
    CancellationTokenSource? _agWatchDebounceCts;
    CancellationTokenSource? _agHubWatchDebounceCts;
    int _agQuickScanRunning;

    void InitFloatingWidget()
    {
        InitAgBrainWatcher();
        InitAgHubStorageWatcher();

        _agWidgetScanTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(30)
        };
        _agWidgetScanTimer.Tick += (_, _) =>
        {
            if (_agBrainWatcher == null)
                InitAgBrainWatcher();
            if (_agHubStorageWatcher == null)
                InitAgHubStorageWatcher();
            if (AppSettingsService.Current.FloatingWidgetEnabled && _floatingWidget is { IsVisible: true })
                _ = TriggerAntigravityQuickScanAsync();
        };
        _agWidgetScanTimer.Start();

        UpdateFloatingWidgetButtonVisual();
        if (AppSettingsService.Current.FloatingWidgetEnabled)
        {
            EnsureFloatingWidgetShown();
            _ = TriggerAntigravityQuickScanAsync();
        }
    }

    void InitAgBrainWatcher()
    {
        if (_agBrainWatcher != null) return;
        try
        {
            var brainDir = Path.Combine(ClaudeCli.HomeDir, ".gemini", "antigravity", "brain");
            if (!Directory.Exists(brainDir)) return;

            var watcher = new FileSystemWatcher(brainDir, "transcript.jsonl")
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                EnableRaisingEvents = true
            };
            watcher.Changed += OnAgBrainTranscriptChanged;
            watcher.Created += OnAgBrainTranscriptChanged;
            _agBrainWatcher = watcher;
        }
        catch
        {
            // 监听失败时由 15s 定时器平滑兜底
        }
    }

    void OnAgBrainTranscriptChanged(object sender, FileSystemEventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            _agWatchDebounceCts?.Cancel();
            _agWatchDebounceCts?.Dispose();
            var cts = new CancellationTokenSource();
            _agWatchDebounceCts = cts;
            _ = DebouncedAgQuickScanAsync(cts.Token);
        });
    }

    void InitAgHubStorageWatcher()
    {
        if (_agHubStorageWatcher != null) return;
        try
        {
            var hubDir = Path.GetDirectoryName(AgPaths.HubAppStoragePath);
            if (string.IsNullOrEmpty(hubDir) || !Directory.Exists(hubDir)) return;

            var watcher = new FileSystemWatcher(hubDir, "app_storage.json")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                EnableRaisingEvents = true
            };
            watcher.Changed += OnAgHubStorageChanged;
            watcher.Created += OnAgHubStorageChanged;
            watcher.Renamed += OnAgHubStorageChanged;
            _agHubStorageWatcher = watcher;
        }
        catch { }
    }

    void OnAgHubStorageChanged(object sender, FileSystemEventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            _agHubWatchDebounceCts?.Cancel();
            _agHubWatchDebounceCts?.Dispose();
            var cts = new CancellationTokenSource();
            _agHubWatchDebounceCts = cts;
            _ = DebouncedAgHubAccountSyncAsync(cts.Token);
        });
    }

    async Task DebouncedAgHubAccountSyncAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(500, ct);
            if (ct.IsCancellationRequested) return;
            await Task.Run(() =>
            {
                var fastEmail = AgHookService.ReadCurrentAntigravityEmailFast();
                if (!string.IsNullOrEmpty(fastEmail))
                    UsageStorage.RecordAccountSession(fastEmail);
            }, ct);
            if (!ct.IsCancellationRequested)
                await RefreshAntigravityAsync();
        }
        catch (OperationCanceledException) { }
        catch { }
    }

    async Task DebouncedAgQuickScanAsync(CancellationToken ct)
    {
        try
        {
            int delayMs = (AppSettingsService.Current.FloatingWidgetEnabled && _floatingWidget is { IsVisible: true })
                ? 4000
                : 12000;
            await Task.Delay(delayMs, ct);
            if (!ct.IsCancellationRequested)
                await TriggerAntigravityQuickScanAsync();
        }
        catch (OperationCanceledException) { }
    }

    async Task TriggerAntigravityQuickScanAsync()
    {
        if (Interlocked.CompareExchange(ref _agQuickScanRunning, 1, 0) != 0) return;
        try
        {
            await Task.Run(() => UsageScanner.ScanAntigravityQuick());
            if (AppSettingsService.Current.FloatingWidgetEnabled && _floatingWidget is { IsVisible: true })
            {
                var stats = await Task.Run(() => UsageStorage.GetAntigravityWidgetStats());
                await Dispatcher.InvokeAsync(() => _floatingWidget?.UpdateUsageStats(stats));
            }
        }
        catch { }
        finally
        {
            Interlocked.Exchange(ref _agQuickScanRunning, 0);
        }
    }

    void EnsureFloatingWidgetShown()
    {
        if (_floatingWidget == null)
        {
            _floatingWidget = new FloatingWidgetWindow(this);
        }
        _floatingWidget.UpdateProfiles(_profiles);
        if (!_floatingWidget.IsVisible)
        {
            _floatingWidget.Show();
            _floatingWidget.RestoreAfterShow();
        }
    }

    void OnToggleFloatingWidgetClick(object sender, RoutedEventArgs e) => ToggleFloatingWidget();

    public void ToggleFloatingWidget() =>
        SetFloatingWidgetEnabled(!AppSettingsService.Current.FloatingWidgetEnabled, showToast: false);

    public void SetFloatingWidgetEnabled(bool enabled, bool showToast = false)
    {
        AppSettingsService.Current.FloatingWidgetEnabled = enabled;
        AppSettingsService.Save();

        if (enabled)
        {
            if (_agQuotaTimer != null && !_agQuotaTimer.IsEnabled)
                _agQuotaTimer.Start();
            EnsureFloatingWidgetShown();
            _ = TriggerAntigravityQuickScanAsync();
        }
        else
        {
            _floatingWidget?.PrepareForHide();
            _floatingWidget?.Hide();
            if (showToast && IsVisible)
                ShowToast(I18nService.T("Widget.HiddenToast"), isInfo: true);
        }

        UpdateFloatingWidgetButtonVisual();
    }

    void UpdateFloatingWidgetButtonVisual()
    {
        if (AgFloatingWidgetDot == null || AgFloatingWidgetBtn == null || AgFloatingWidgetBtnText == null) return;
        bool on = AppSettingsService.Current.FloatingWidgetEnabled;
        AgFloatingWidgetDot.Fill = new SolidColorBrush(on
            ? MediaColor.FromRgb(0x16, 0xA3, 0x4A)
            : MediaColor.FromRgb(0x94, 0xA3, 0xB8));
        AgFloatingWidgetBtn.Background = on
            ? new SolidColorBrush(MediaColor.FromRgb(0xEE, 0xF2, 0xFF))
            : System.Windows.Media.Brushes.White;
        AgFloatingWidgetBtn.BorderBrush = on
            ? new SolidColorBrush(MediaColor.FromRgb(0xA5, 0xB4, 0xFC))
            : new SolidColorBrush(MediaColor.FromRgb(0xCB, 0xD5, 0xE1));
        AgFloatingWidgetBtnText.Foreground = on
            ? new SolidColorBrush(MediaColor.FromRgb(0x43, 0x38, 0xCA))
            : (System.Windows.Media.Brush)FindResource("TextDimBrush");
        AgFloatingWidgetBtnText.Text = I18nService.T(on ? "Ag.WidgetBtnOn" : "Ag.WidgetBtnOff");
    }

    public async Task RefreshFromFloatingWidgetAsync()
    {
        _floatingWidget?.SetRefreshing(true);
        try
        {
            await Task.WhenAll(
                TriggerAntigravityQuickScanAsync(),
                RefreshAllAgQuotasAsync(silent: false));
        }
        finally
        {
            _floatingWidget?.SetRefreshing(false);
        }
    }

    public Task SwitchProfileFromWidgetAsync(Profile target) => SwitchToProfile(target);

    public void OpenAntigravityUsageMonitorFromWidget() =>
        UsageMonitorWindow.ShowOrActivate(this, focusAntigravityTab: true);

    void NotifyFloatingWidgetProfilesUpdated()
    {
        _floatingWidget?.UpdateProfiles(_profiles);
    }

    void DisposeFloatingWidget()
    {
        try
        {
            _agWatchDebounceCts?.Cancel();
            _agWatchDebounceCts?.Dispose();
            _agWatchDebounceCts = null;
            _agHubWatchDebounceCts?.Cancel();
            _agHubWatchDebounceCts?.Dispose();
            _agHubWatchDebounceCts = null;
        }
        catch { }

        try
        {
            _agBrainWatcher?.Dispose();
            _agBrainWatcher = null;
            _agHubStorageWatcher?.Dispose();
            _agHubStorageWatcher = null;
        }
        catch { }

        try
        {
            _agWidgetScanTimer?.Stop();
            _agWidgetScanTimer = null;
        }
        catch { }

        try
        {
            _floatingWidget?.ForceClose();
            _floatingWidget = null;
        }
        catch { }
    }
}

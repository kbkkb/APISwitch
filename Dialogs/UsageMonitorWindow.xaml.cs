using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using APISwitch.Models;
using APISwitch.Services;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;
using Color = System.Windows.Media.Color;
using Cursors = System.Windows.Input.Cursors;
using Point = System.Windows.Point;
using RadioButton = System.Windows.Controls.RadioButton;
using Rectangle = System.Windows.Shapes.Rectangle;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;
using Size = System.Windows.Size;

namespace APISwitch.Dialogs;

public partial class UsageMonitorWindow : Window
{
    private static UsageMonitorWindow? _activeInstance;
    const double BarTrackWidth = 140;

    DateTime? _selectedDay;
    string? _agentFilter;
    string _searchQuery = "";
    bool _wasAntigravityTab;
    string _sortColumn = "total"; // "total" | "cost" | "reqs" | "cache"
    bool _sortDescending = true;
    bool _chartCollapsed;
    bool _allExpanded;

    List<UsageStorage.DailyTotal> _daily = new();
    List<UsageStorage.DailyTotal> _mainDaily = new();
    List<UsageStorage.DailyTotal> _agDaily = new();
    UsageStorage.UsageSummary _mainSummary = new(0, 0, 0, 0);
    UsageStorage.UsageSummary _agSummary = new(0, 0, 0, 0);
    UsageStorage.PerformanceStats _mainPerf = new(0, 0, 0, 0, 0, 0, 0, 0);
    UsageStorage.PerformanceStats _agPerf = new(0, 0, 0, 0, 0, 0, 0, 0);
    double _mainCostUsd;
    double _agCostUsd;
    DateTime _lastEffFrom = DateTime.Now;
    DateTime _lastEffTo = DateTime.Now;

    List<UsageStorage.AgentTotal> _allAgentTotals = new();
    List<ProviderGroupVM> _allProviderGroups = new();
    List<AgAccountGroupVM> _allAgAccountGroups = new();
    List<AgentRowVM> _allAgentRows = new();
    List<ModelGroupVM> _allModelGroups = new();
    List<CallRowVM> _allCalls = new();

    List<ProviderGroupVM> _filteredProviders = new();
    List<AgAccountGroupVM> _filteredAg = new();
    List<AgentRowVM> _filteredAgents = new();
    List<ModelGroupVM> _filteredModels = new();
    List<CallRowVM> _filteredCalls = new();
    readonly List<ClientFilterItemVM> _clientFilters = new();

    bool _hasUsageData;
    bool _updatingRangeFields;
    bool _isRefreshingTokens;
    bool _refreshPending;
    string _chartMode = "bar"; // "bar" or "heatmap"
    internal Task<int> InitialScanTask { get; private set; } = Task.FromResult(0);

    bool IsAntigravityTabActive => TabAntigravityRadio?.IsChecked == true;
    string? EffectiveAgentFilter => IsAntigravityTabActive ? UsageStorage.AgentAntigravity : _agentFilter;

    static readonly (Brush Fg, Brush BadgeBg, Brush BadgeBorder)[] AccountPalette =
    {
        (MakeFrozenBrush(0x4F, 0x46, 0xE5), MakeFrozenBrush(0xEE, 0xF2, 0xFF), MakeFrozenBrush(0xC7, 0xD2, 0xFE)), // Indigo
        (MakeFrozenBrush(0x0D, 0x94, 0x88), MakeFrozenBrush(0xF0, 0xFD, 0xFA), MakeFrozenBrush(0x99, 0xF6, 0xE4)), // Teal
        (MakeFrozenBrush(0xD9, 0x77, 0x06), MakeFrozenBrush(0xFF, 0xFB, 0xEB), MakeFrozenBrush(0xFD, 0xE6, 0x8A)), // Amber
        (MakeFrozenBrush(0x7C, 0x3A, 0xED), MakeFrozenBrush(0xF5, 0xF3, 0xFF), MakeFrozenBrush(0xDD, 0xD6, 0xFE)), // Violet
        (MakeFrozenBrush(0x02, 0x84, 0xC7), MakeFrozenBrush(0xF0, 0xF9, 0xFF), MakeFrozenBrush(0xBA, 0xE6, 0xFD)), // Sky
        (MakeFrozenBrush(0xE1, 0x1D, 0x48), MakeFrozenBrush(0xFF, 0xF1, 0xF2), MakeFrozenBrush(0xFE, 0xCD, 0xD3)), // Rose
    };

    static readonly (Brush Fg, Brush BadgeBg, Brush BadgeBorder) UnattributedPalette =
        (MakeFrozenBrush(0x64, 0x74, 0x8B), MakeFrozenBrush(0xF8, 0xFA, 0xFC), MakeFrozenBrush(0xE2, 0xE8, 0xF0));

    /// <summary>
    /// 单实例非模态打开或唤醒「用量监控」窗口（绝不使用 ShowDialog 禁用悬浮窗或主窗口）。
    /// </summary>
    public static UsageMonitorWindow ShowOrActivate(Window? centerNear = null, bool focusAntigravityTab = false)
    {
        if (_activeInstance is { IsLoaded: true } existing)
        {
            if (existing.WindowState == WindowState.Minimized)
                existing.WindowState = WindowState.Normal;
            if (focusAntigravityTab && existing.TabAntigravityRadio != null)
                existing.TabAntigravityRadio.IsChecked = true;
            existing.Show();
            existing.Activate();
            return existing;
        }

        var win = new UsageMonitorWindow();
        _activeInstance = win;
        if (centerNear is { IsVisible: true } && centerNear.WindowState != WindowState.Minimized)
        {
            double aw = centerNear.ActualWidth > 0 ? centerNear.ActualWidth : centerNear.Width;
            double ah = centerNear.ActualHeight > 0 ? centerNear.ActualHeight : centerNear.Height;
            if (!double.IsNaN(aw) && !double.IsNaN(ah) && aw > 0 && ah > 0)
            {
                var wa = SystemParameters.WorkArea;
                double left = centerNear.Left + (aw - win.Width) / 2.0;
                double top = centerNear.Top + (ah - win.Height) / 2.0;
                win.WindowStartupLocation = WindowStartupLocation.Manual;
                win.Left = Math.Clamp(left, wa.Left + 8, Math.Max(wa.Left + 8, wa.Right - win.Width - 8));
                win.Top = Math.Clamp(top, wa.Top + 8, Math.Max(wa.Top + 8, wa.Bottom - win.Height - 8));
            }
        }
        if (focusAntigravityTab && win.TabAntigravityRadio != null)
            win.TabAntigravityRadio.IsChecked = true;
        win.Show();
        win.Activate();
        return win;
    }

    public UsageMonitorWindow()
    {
        InitializeComponent();
        Closed += (_, _) =>
        {
            if (ReferenceEquals(_activeInstance, this))
                _activeInstance = null;
        };
        InitClientFilterBar();
        StateChanged += OnWindowStateChanged;
        InitRangeDefaults();
        Loaded += OnWindowLoaded;
    }

    async void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        RefreshData();
        UsagePricingService.TriggerBackgroundAutoSyncIfNeeded(() =>
            Dispatcher.BeginInvoke(new Action(RefreshData)));
        InitialScanTask = Task.Run(() => UsageScanner.ScanForWindowOpen(TimeSpan.FromSeconds(25)));
        int added = await InitialScanTask;
        if (added > 0) RefreshData();
        _ = Task.Run(() => AgQuotaAnalyticsService.WarmAccountCapCache());
    }

    void OnWindowStateChanged(object? sender, EventArgs e)
    {
        if (MaxRestoreBtn != null)
        {
            var iconKey = WindowState == WindowState.Maximized ? "IconRestore" : "IconMax";
            if (TryFindResource(iconKey) is { } res) MaxRestoreBtn.Tag = res;
        }
        if (Content is Border root)
        {
            if (WindowState == WindowState.Maximized)
            {
                root.Margin = new Thickness(7);
                root.CornerRadius = new CornerRadius(0);
            }
            else
            {
                root.Margin = new Thickness(0);
                root.CornerRadius = new CornerRadius(12);
            }
        }
    }

    // ---------- 时间范围选择 ----------

    void InitRangeDefaults()
    {
        Quick30DRadio.IsChecked = true;
        ApplyQuickRange(30);
    }

    void ApplyQuickRange(int days)
    {
        var now = DateTime.Now;
        var to = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0);
        DateTime from = days <= 1
            ? now.Date
            : now.Date.AddDays(-(days - 1));

        _updatingRangeFields = true;
        try
        {
            StartPicker.SetValue(from, silent: true);
            EndPicker.SetValue(to, silent: true);
        }
        finally
        {
            _updatingRangeFields = false;
        }
        _selectedDay = null;
        UpdateFocusedDayChip();
        UpdateClearFilterButtonVisibility();
    }

    void OnQuickRangeChecked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        if (sender is RadioButton rb && int.TryParse(rb.Tag?.ToString(), out int days))
        {
            ApplyQuickRange(days);
            if (days >= 90 && ChartModeHeatmapRadio != null) ChartModeHeatmapRadio.IsChecked = true;
            else if (days < 90 && ChartModeBarRadio != null) ChartModeBarRadio.IsChecked = true;
            RefreshData();
        }
    }

    void OnRangeFieldEdited(object? sender, EventArgs e)
    {
        if (_updatingRangeFields) return;
        _updatingRangeFields = true;
        try
        {
            foreach (var r in new[] { Quick1DRadio, Quick7DRadio, Quick30DRadio, Quick180DRadio, Quick365DRadio })
                if (r != null) r.IsChecked = false;
        }
        finally { _updatingRangeFields = false; }
    }

    void OnToggleCustomRangePopupClick(object sender, RoutedEventArgs e)
    {
        if (CustomRangePopup != null)
            CustomRangePopup.IsOpen = !CustomRangePopup.IsOpen;
    }

    void OnApplyCustomRangePopup(object sender, RoutedEventArgs e)
    {
        if (CustomRangePopup != null)
            CustomRangePopup.IsOpen = false;
        OnApplyRange(sender, e);
    }

    void OnToggleChartVisibilityClick(object sender, RoutedEventArgs e)
    {
        _chartCollapsed = !_chartCollapsed;
        if (TrendCanvas != null)
            TrendCanvas.Visibility = _chartCollapsed ? Visibility.Collapsed : Visibility.Visible;
        if (ToggleChartBtn != null)
            ToggleChartBtn.Content = _chartCollapsed ? "▾" : "▴";
        if (!_chartCollapsed)
            ScheduleChartRedraw();
    }

    void OnToggleExpandAllClick(object sender, RoutedEventArgs e)
    {
        _allExpanded = !_allExpanded;
        if (ProvidersPanel?.Visibility == Visibility.Visible && ProvidersList.ItemsSource is IEnumerable<ProviderGroupVM> provs)
        {
            foreach (var g in provs) g.Expanded = _allExpanded;
        }
        else if (AntigravityPanel?.Visibility == Visibility.Visible && AntigravityList.ItemsSource is IEnumerable<AgAccountGroupVM> ags)
        {
            foreach (var g in ags) g.Expanded = _allExpanded;
        }
        else if (ModelsPanel?.Visibility == Visibility.Visible && ModelsList.ItemsSource is IEnumerable<ModelGroupVM> mods)
        {
            foreach (var g in mods) g.Expanded = _allExpanded;
        }
        UpdateExpandAllBtnText();
    }

    void UpdateExpandAllBtnText()
    {
        if (ExpandCollapseAllBtn == null) return;
        bool canExpand = ProvidersPanel?.Visibility == Visibility.Visible ||
                         AntigravityPanel?.Visibility == Visibility.Visible ||
                         ModelsPanel?.Visibility == Visibility.Visible;
        ExpandCollapseAllBtn.Visibility = canExpand ? Visibility.Visible : Visibility.Collapsed;
        ExpandCollapseAllBtn.Content = I18nService.T(_allExpanded ? "Usage.CollapseAllBtn" : "Usage.ExpandAllBtn");
    }

    void OnSortColumnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string col) return;
        if (string.Equals(_sortColumn, col, StringComparison.OrdinalIgnoreCase))
            _sortDescending = !_sortDescending;
        else
        {
            _sortColumn = col;
            _sortDescending = true;
        }
        SortAllViewGroups();
        ApplySearchFilter();
        UpdateSortHeaderLabels();
    }

    static double ComputeCacheRatio(long inTok, long cr, long cw)
    {
        long denom = inTok >= cr ? inTok + cw : inTok + cr + cw;
        return denom > 0 ? (double)cr / denom : 0.0;
    }

    void SortAllViewGroups()
    {
        double KeyProv(ProviderGroupVM x) => _sortColumn switch
        {
            "cost" => x.CostUsd,
            "reqs" => x.Requests,
            "cache" => ComputeCacheRatio(x.InTokens, x.CacheReadTokens, x.CacheWriteTokens),
            _ => x.Total
        };
        double KeyModelRow(ModelRowVM x) => _sortColumn switch
        {
            "cost" => x.CostUsd,
            "reqs" => x.Requests,
            "cache" => ComputeCacheRatio(x.InTokens, x.CacheReadTokens, x.CacheWriteTokens),
            _ => x.Total
        };
        double KeyAg(AgAccountGroupVM x) => _sortColumn switch
        {
            "cost" => x.CostUsd,
            "reqs" => x.Requests,
            "cache" => ComputeCacheRatio(x.InTokens, x.CacheReadTokens, x.CacheWriteTokens),
            _ => x.Total
        };
        double KeyAgent(AgentRowVM x) => _sortColumn switch
        {
            "cost" => x.CostUsd,
            "reqs" => x.Requests,
            "cache" => ComputeCacheRatio(x.InTokens, x.CacheReadTokens, x.CacheWriteTokens),
            _ => x.Total
        };
        double KeyModGrp(ModelGroupVM x) => _sortColumn switch
        {
            "cost" => x.CostUsd,
            "reqs" => x.Requests,
            "cache" => ComputeCacheRatio(x.InTokens, x.CacheReadTokens, x.CacheWriteTokens),
            _ => x.Total
        };

        foreach (var pg in _allProviderGroups)
            pg.Models = (_sortDescending ? pg.Models.OrderByDescending(KeyModelRow) : pg.Models.OrderBy(KeyModelRow)).ToList();
        _allProviderGroups = (_sortDescending ? _allProviderGroups.OrderByDescending(KeyProv) : _allProviderGroups.OrderBy(KeyProv)).ToList();

        foreach (var ag in _allAgAccountGroups)
            ag.Models = (_sortDescending ? ag.Models.OrderByDescending(KeyModelRow) : ag.Models.OrderBy(KeyModelRow)).ToList();
        _allAgAccountGroups = _allAgAccountGroups
            .OrderBy(x => x.IsUnattributed ? 1 : 0)
            .ThenBy(x => _sortDescending ? -KeyAg(x) : KeyAg(x))
            .ToList();

        _allAgentRows = (_sortDescending ? _allAgentRows.OrderByDescending(KeyAgent) : _allAgentRows.OrderBy(KeyAgent)).ToList();
        _allModelGroups = (_sortDescending ? _allModelGroups.OrderByDescending(KeyModGrp) : _allModelGroups.OrderBy(KeyModGrp)).ToList();
    }

    void UpdateSortHeaderLabels()
    {
        string arrow = _sortDescending ? " ↓" : " ↑";
        var activeFg = MakeFrozenBrush(0x0F, 0x17, 0x2A);
        var mutedFg = MakeFrozenBrush(0x64, 0x74, 0x8B);

        void ApplySet(TextBlock? reqTb, TextBlock? cacheTb, TextBlock? costTb, TextBlock? totTb)
        {
            SetOne(reqTb, "Usage.ColReqs", "reqs");
            SetOne(cacheTb, "Usage.ColCacheHit", "cache");
            SetOne(costTb, "Usage.ColCost", "cost");
            SetOne(totTb, "Usage.ColTotal", "total");
        }
        void SetOne(TextBlock? tb, string resKey, string colId)
        {
            if (tb == null) return;
            bool act = string.Equals(_sortColumn, colId, StringComparison.OrdinalIgnoreCase);
            tb.Text = I18nService.T(resKey) + (act ? arrow : "");
            tb.Foreground = act ? activeFg : mutedFg;
        }

        ApplySet(ProvSortReqsText, ProvSortCacheText, ProvSortCostText, ProvSortTotalText);
        ApplySet(AgSortReqsText, AgSortCacheText, AgSortCostText, AgSortTotalText);
        ApplySet(AgentSortReqsText, AgentSortCacheText, AgentSortCostText, AgentSortTotalText);
        ApplySet(ModelSortReqsText, ModelSortCacheText, ModelSortCostText, ModelSortTotalText);
    }

    void OnApplyRange(object sender, RoutedEventArgs e)
    {
        if (!TryGetRange(out _, out _))
        {
            MessageBox.Show(I18nService.T("Usage.InvalidRange"), I18nService.T("Usage.Title"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _selectedDay = null;
        UpdateFocusedDayChip();
        UpdateClearFilterButtonVisibility();
        RefreshData();
    }

    void OnClearFocusedDayClick(object sender, RoutedEventArgs e)
    {
        if (_selectedDay == null) return;
        _selectedDay = null;
        UpdateFocusedDayChip();
        UpdateClearFilterButtonVisibility();
        RefreshData();
    }

    void UpdateFocusedDayChip()
    {
        if (FocusedDayChip == null) return;
        if (_selectedDay.HasValue)
        {
            FocusedDayChip.Content = I18nService.F("Usage.FocusedDayFmt", _selectedDay.Value.ToString("MM-dd"));
            FocusedDayChip.Visibility = Visibility.Visible;
        }
        else
        {
            FocusedDayChip.Visibility = Visibility.Collapsed;
        }
    }

    void OnChartModeChanged(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton rb || rb.Tag == null) return;
        _chartMode = rb.Tag.ToString() ?? "bar";
        if (BarLegendPanel != null) BarLegendPanel.Visibility = _chartMode == "bar" ? Visibility.Visible : Visibility.Collapsed;
        if (HeatmapLegendPanel != null) HeatmapLegendPanel.Visibility = _chartMode == "heatmap" ? Visibility.Visible : Visibility.Collapsed;
        if (IsLoaded) ScheduleChartRedraw();
    }

    bool TryGetRange(out DateTime from, out DateTime to)
    {
        from = StartPicker.Value;
        to = EndPicker.Value;
        return to >= from;
    }

    // ---------- 筛选与检索 ----------

    static SolidColorBrush MakeFrozenBrush(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    void InitClientFilterBar()
    {
        _clientFilters.Clear();

        void AddSvgClient(string agent, string label, string iconKey, byte r, byte g, byte b, string? tooltip = null)
        {
            _clientFilters.Add(new ClientFilterItemVM
            {
                Agent = agent,
                Label = label,
                IconGeometry = TryFindResource(iconKey) as Geometry,
                IconBrush = MakeFrozenBrush(r, g, b),
                IsSelected = string.IsNullOrEmpty(agent),
                PillOpacity = 1.0,
                TooltipText = tooltip ?? label
            });
        }

        AddSvgClient("", I18nService.T("Usage.ClientAllPill"), "IconAllClients", 0x4F, 0x46, 0xE5, I18nService.T("Usage.FilterAllAgents"));

        BitmapImage? agLogo = null;
        try
        {
            agLogo = new BitmapImage(new Uri("pack://application:,,,/Assets/antigravity.png", UriKind.Absolute));
            if (agLogo.CanFreeze) agLogo.Freeze();
        }
        catch { }

        _clientFilters.Add(new ClientFilterItemVM
        {
            Agent = UsageStorage.AgentAntigravity,
            Label = AgentName(UsageStorage.AgentAntigravity),
            ImageSource = agLogo,
            IconGeometry = agLogo == null ? TryFindResource("IconAllClients") as Geometry : null,
            IconBrush = agLogo == null ? MakeFrozenBrush(0x4F, 0x46, 0xE5) : null,
            IsSelected = false,
            PillOpacity = 1.0,
            TooltipText = AgentName(UsageStorage.AgentAntigravity)
        });

        AddSvgClient(UsageStorage.AgentCodex, AgentName(UsageStorage.AgentCodex), "IconOpenAi", 0x05, 0x96, 0x69);
        AddSvgClient(UsageStorage.AgentClaudeCli, AgentName(UsageStorage.AgentClaudeCli), "IconClaude", 0xD9, 0x77, 0x06);
        AddSvgClient(UsageStorage.AgentClaudeDesktop, AgentName(UsageStorage.AgentClaudeDesktop), "IconDesktop", 0xB4, 0x53, 0x09);
        AddSvgClient(UsageStorage.AgentOpenCode, AgentName(UsageStorage.AgentOpenCode), "IconOpenCode", 0x02, 0x84, 0xC7);
        AddSvgClient(UsageStorage.AgentPi, AgentName(UsageStorage.AgentPi), "IconPi", 0x7C, 0x3A, 0xED);
        AddSvgClient(UsageStorage.AgentStepCode, AgentName(UsageStorage.AgentStepCode), "IconStepFun", 0x0E, 0x74, 0x90);
        AddSvgClient(UsageStorage.AgentZCode, AgentName(UsageStorage.AgentZCode), "IconZai", 0xC2, 0x41, 0x0C);
        AddSvgClient(UsageStorage.AgentDsh, AgentName(UsageStorage.AgentDsh), "IconDeepSeek", 0x4D, 0x6B, 0xFE);
        AddSvgClient(UsageStorage.AgentGeminiCli, AgentName(UsageStorage.AgentGeminiCli), "IconGemini", 0x1A, 0x73, 0xE8);

        if (ClientFilterList != null)
            ClientFilterList.ItemsSource = _clientFilters;
    }

    void OnClientPillClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is ClientFilterItemVM item)
            SelectAgentFilter(item.Agent);
    }

    internal void SelectAgentFilter(string? agent)
    {
        var normalized = string.IsNullOrWhiteSpace(agent) ? null : agent.Trim();
        if (string.Equals(_agentFilter, normalized, StringComparison.OrdinalIgnoreCase))
        {
            SyncClientFilterSelection();
            return;
        }
        _agentFilter = normalized;
        SyncClientFilterSelection();
        UpdateClearFilterButtonVisibility();
        if (IsLoaded) RefreshData();
    }

    void OnAgentRowFilterClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is AgentRowVM row)
            ToggleAgentFilter(row.Agent);
    }

    void ToggleAgentFilter(string agent)
    {
        if (string.IsNullOrWhiteSpace(agent)) return;
        var next = string.Equals(_agentFilter, agent, StringComparison.OrdinalIgnoreCase) ? null : agent;
        SelectAgentFilter(next);
    }

    void SyncClientFilterSelection()
    {
        foreach (var item in _clientFilters)
        {
            item.IsSelected = string.IsNullOrEmpty(item.Agent)
                ? string.IsNullOrEmpty(_agentFilter)
                : string.Equals(_agentFilter, item.Agent, StringComparison.OrdinalIgnoreCase);
        }
    }

    void SyncClientFilterStates(List<UsageStorage.AgentTotal> allAgents, Dictionary<string, double> costByAgent)
    {
        var totalsByAgent = allAgents
            .Where(a => !string.IsNullOrWhiteSpace(a.Agent))
            .GroupBy(a => a.Agent, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => (Tokens: g.Sum(x => x.InTokens + x.OutTokens), Reqs: g.Sum(x => x.Requests)),
                StringComparer.OrdinalIgnoreCase);

        long grandTokens = allAgents.Sum(a => a.InTokens + a.OutTokens);
        long grandReqs = allAgents.Sum(a => a.Requests);
        double grandCost = costByAgent.Values.Sum();

        SyncClientFilterSelection();
        foreach (var item in _clientFilters)
        {
            if (string.IsNullOrEmpty(item.Agent))
            {
                string allTitle = I18nService.T("Usage.FilterAllAgents");
                item.PillOpacity = 1.0;
                item.TooltipText = grandReqs > 0 || grandTokens > 0
                    ? $"{allTitle} · {FormatTokens(grandTokens)} · {UsagePricingService.FormatUsd(grandCost)} ({grandReqs:N0} reqs)"
                    : allTitle;
                continue;
            }

            totalsByAgent.TryGetValue(item.Agent, out var stat);
            costByAgent.TryGetValue(item.Agent, out var cUsd);
            bool hasData = stat.Reqs > 0 || stat.Tokens > 0;
            item.PillOpacity = item.IsSelected || hasData ? 1.0 : 0.62;
            item.TooltipText = hasData
                ? $"{item.Label} · {FormatTokens(stat.Tokens)} · {UsagePricingService.FormatUsd(cUsd)} ({stat.Reqs:N0} reqs)"
                : $"{item.Label} · 0";
        }
    }

    void OnSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        _searchQuery = SearchBox.Text.Trim();
        UpdateClearFilterButtonVisibility();
        ApplySearchFilter();
    }

    void OnClearFiltersClick(object sender, RoutedEventArgs e)
    {
        if (!IsAntigravityTabActive)
            _agentFilter = null;
        _selectedDay = null;
        _searchQuery = "";
        if (SearchBox != null) SearchBox.Text = "";
        SyncClientFilterSelection();
        UpdateFocusedDayChip();
        UpdateClearFilterButtonVisibility();
        RefreshData();
    }

    void UpdateClearFilterButtonVisibility()
    {
        if (ClearFilterBtn == null) return;
        bool active = (!IsAntigravityTabActive && !string.IsNullOrEmpty(_agentFilter))
                   || !string.IsNullOrEmpty(_searchQuery)
                   || _selectedDay.HasValue;
        ClearFilterBtn.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---------- 数据加载与绑定 ----------

    async void RefreshData()
    {
        if (_isRefreshingTokens)
        {
            _refreshPending = true;
            return;
        }
        if (!TryGetRange(out var from, out var to))
        {
            _hasUsageData = false;
            UpdateEmptyOverlayVisibility();
            return;
        }

        _isRefreshingTokens = true;
        _refreshPending = false;
        var effFrom = _selectedDay.HasValue ? _selectedDay.Value.Date : from;
        var effTo = _selectedDay.HasValue ? _selectedDay.Value.Date.AddDays(1).AddSeconds(-1) : to;

        long fromTs = new DateTimeOffset(from).ToUnixTimeSeconds();
        long toTs = new DateTimeOffset(to).ToUnixTimeSeconds();
        long effFromTs = new DateTimeOffset(effFrom).ToUnixTimeSeconds();
        long effToTs = new DateTimeOffset(effTo).ToUnixTimeSeconds();
        string? filterAgent = _agentFilter;

        var expandedProviders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in _allProviderGroups) if (g.Expanded) expandedProviders.Add(g.Name);

        var expandedAgAccounts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in _allAgAccountGroups) if (g.Expanded) expandedAgAccounts.Add(g.AccountKey);

        var expandedModels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in _allModelGroups) if (g.Expanded) expandedModels.Add(g.Name);

        string unknownModelLabel = I18nService.T("Usage.UnknownModel");
        string unattrShortLabel = I18nService.T("Usage.AgUnattributedShort");

        UsageStorage.UsageSummary mainSummary, agSummary;
        UsageStorage.PerformanceStats mainPerf, agPerf;
        List<UsageStorage.DailyTotal> mainDaily, agDaily;
        List<UsageStorage.AgentTotal> allAgentTotals;
        Dictionary<string, double> costByAgent;
        double mainCostUsd, agCostUsd;
        List<ProviderGroupVM> provGroups;
        List<AgAccountGroupVM> rawAgGroups;
        List<ModelGroupVM> modelGroups;
        List<AgentRowVM> agentRows;
        List<CallRowVM> callRows;

        try
        {
            // 全部 SQLite 查询、正则模型归并、价格解算与 5 套列表 VM 构建均在后台线程一次性完成，主线程零卡顿
            (mainSummary, agSummary, mainPerf, agPerf, mainDaily, agDaily, allAgentTotals, costByAgent, mainCostUsd, agCostUsd,
             provGroups, rawAgGroups, modelGroups, agentRows, callRows) = await Task.Run(() =>
            {
                var sMain = UsageStorage.GetSummary(effFromTs, effToTs, filterAgent);
                var pMain = UsageStorage.GetPerformance(effFromTs, effToTs, filterAgent);
                var dMain = UsageStorage.GetDailyTotals(fromTs, toTs, filterAgent);

                bool alreadyAg = string.Equals(filterAgent, UsageStorage.AgentAntigravity, StringComparison.OrdinalIgnoreCase);
                var sAg = alreadyAg ? sMain : UsageStorage.GetSummary(effFromTs, effToTs, UsageStorage.AgentAntigravity);
                var pAg = alreadyAg ? pMain : UsageStorage.GetPerformance(effFromTs, effToTs, UsageStorage.AgentAntigravity);
                var dAg = alreadyAg ? dMain : UsageStorage.GetDailyTotals(fromTs, toTs, UsageStorage.AgentAntigravity);

                var pm = UsageStorage.GetProviderModelTotals(effFromTs, effToTs, 500, filterAgent);
                var agAcct = UsageStorage.GetAntigravityAccountModelTotals(effFromTs, effToTs, 600);
                var apm = UsageStorage.GetAgentProviderModelTotals(effFromTs, effToTs, null);
                var agTotals = UsageStorage.GetAgentTotals(effFromTs, effToTs);
                var ev = UsageStorage.GetRecentEvents(effFromTs, effToTs, 300, filterAgent);
                var profs = ProfileStore.Load();
                var curEmail = AgHookService.ReadCurrentAntigravityEmailFast();

                var cByAgent = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                double mCost = 0.0;
                double aCost = 0.0;
                foreach (var row in apm)
                {
                    double c = UsagePricingService.EstimateCostUsd(
                        row.Model, row.Provider, row.Agent, row.InTokens, row.OutTokens, row.CacheRead, row.CacheWrite);
                    cByAgent[row.Agent] = (cByAgent.TryGetValue(row.Agent, out var prev) ? prev : 0.0) + c;
                    if (string.IsNullOrEmpty(filterAgent) ||
                        string.Equals(row.Agent, filterAgent, StringComparison.OrdinalIgnoreCase))
                    {
                        mCost += c;
                    }
                    if (string.Equals(row.Agent, UsageStorage.AgentAntigravity, StringComparison.OrdinalIgnoreCase))
                    {
                        aCost += c;
                    }
                }

                // 1. 供应商分组
                var pGroups = pm
                    .GroupBy(r => string.IsNullOrEmpty(r.Provider) ? unknownModelLabel : r.Provider)
                    .Select(g =>
                    {
                        long gIn = g.Sum(m => m.InTokens);
                        long gOut = g.Sum(m => m.OutTokens);
                        long gReq = g.Sum(m => m.Requests);
                        long gTotal = gIn + gOut;
                        long gCr = g.Sum(m => m.CacheRead);
                        long gCw = g.Sum(m => m.CacheWrite);
                        string inferredAgent = InferAgentFromProvider(g.Key, filterAgent);

                        var models = g.Select(m =>
                        {
                            long mTotal = m.InTokens + m.OutTokens;
                            double rowCost = UsagePricingService.EstimateCostUsd(
                                m.Model, g.Key, inferredAgent, m.InTokens, m.OutTokens, m.CacheRead, m.CacheWrite);
                            return new ModelRowVM
                            {
                                Name = string.IsNullOrEmpty(m.Model) ? unknownModelLabel : m.Model,
                                RawModel = m.Model ?? "",
                                ProviderKey = g.Key,
                                ProviderLabel = g.Key,
                                AgentKey = inferredAgent,
                                InTokens = m.InTokens,
                                OutTokens = m.OutTokens,
                                Requests = m.Requests,
                                Total = mTotal,
                                CacheReadTokens = m.CacheRead,
                                CacheWriteTokens = m.CacheWrite,
                                CostUsd = rowCost,
                                ShareOfProvider = gTotal > 0 ? mTotal * 100.0 / gTotal : 0,
                            };
                        }).OrderByDescending(m => m.Total).ThenByDescending(m => m.Requests).ToList();

                        return new ProviderGroupVM
                        {
                            Name = g.Key,
                            InTokens = gIn,
                            OutTokens = gOut,
                            Requests = gReq,
                            Total = gTotal,
                            CacheReadTokens = gCr,
                            CacheWriteTokens = gCw,
                            CostUsd = models.Sum(x => x.CostUsd),
                            Models = models,
                            Expanded = expandedProviders.Contains(g.Key),
                        };
                    })
                    .OrderByDescending(x => x.Total).ThenByDescending(x => x.Requests)
                    .ToList();

                long provGrand = pGroups.Sum(x => x.Total);
                foreach (var pg in pGroups) pg.ShareOfAll = provGrand > 0 ? pg.Total * 100.0 / provGrand : 0;

                // 2. 反重力分账号分组
                var profileMap = profs
                    .Where(p => !string.IsNullOrWhiteSpace(p.Email))
                    .GroupBy(p => p.Email.Trim().ToLowerInvariant(), StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

                var agGroups = agAcct
                    .GroupBy(r => r.AccountEmail ?? "", StringComparer.OrdinalIgnoreCase)
                    .Select(g =>
                    {
                        string email = g.Key.Trim().ToLowerInvariant();
                        bool isUnattr = string.IsNullOrEmpty(email);
                        long gIn = g.Sum(m => m.InTokens);
                        long gOut = g.Sum(m => m.OutTokens);
                        long gReq = g.Sum(m => m.Requests);
                        long gTotal = gIn + gOut;
                        long gCr = g.Sum(m => m.CacheRead);
                        long gCw = g.Sum(m => m.CacheWrite);

                        string agProvKey = isUnattr ? "Antigravity" : email;
                        string agProvLabel = isUnattr ? "Antigravity" : $"Antigravity · {email}";
                        var models = g.Select(m =>
                        {
                            long mTotal = m.InTokens + m.OutTokens;
                            double rowCost = UsagePricingService.EstimateCostUsd(
                                m.Model, agProvKey, UsageStorage.AgentAntigravity, m.InTokens, m.OutTokens, m.CacheRead, m.CacheWrite);
                            return new ModelRowVM
                            {
                                Name = string.IsNullOrEmpty(m.Model) ? unknownModelLabel : m.Model,
                                RawModel = m.Model ?? "",
                                ProviderKey = agProvKey,
                                ProviderLabel = agProvLabel,
                                AgentKey = UsageStorage.AgentAntigravity,
                                InTokens = m.InTokens,
                                OutTokens = m.OutTokens,
                                Requests = m.Requests,
                                Total = mTotal,
                                CacheReadTokens = m.CacheRead,
                                CacheWriteTokens = m.CacheWrite,
                                CostUsd = rowCost,
                                ShareOfProvider = gTotal > 0 ? mTotal * 100.0 / gTotal : 0,
                            };
                        }).OrderByDescending(m => m.Total).ThenByDescending(m => m.Requests).ToList();

                        profileMap.TryGetValue(email, out var prof);
                        string tierText = prof != null && !string.IsNullOrWhiteSpace(prof.SubscriptionTier)
                            ? prof.TierDisplay
                            : "";
                        var (tierFg, tierBg) = ResolveTierColors(prof?.SubscriptionTier);
                        string acctKey = isUnattr ? "__unattributed__" : email;

                        return new AgAccountGroupVM
                        {
                            AccountKey = acctKey,
                            Email = email,
                            IsUnattributed = isUnattr,
                            DisplayName = isUnattr ? unattrShortLabel : email,
                            ShortLabel = isUnattr
                                ? unattrShortLabel
                                : (email.Contains('@') ? email.Split('@')[0] : email),
                            TierText = tierText,
                            TierFg = tierFg,
                            TierBg = tierBg,
                            IsCurrentAccount = !isUnattr && !string.IsNullOrEmpty(curEmail) &&
                                               string.Equals(email, curEmail, StringComparison.OrdinalIgnoreCase),
                            InTokens = gIn,
                            OutTokens = gOut,
                            Requests = gReq,
                            Total = gTotal,
                            CacheReadTokens = gCr,
                            CacheWriteTokens = gCw,
                            CostUsd = models.Sum(x => x.CostUsd),
                            Models = models,
                            Expanded = expandedAgAccounts.Contains(acctKey),
                        };
                    })
                    .OrderBy(x => x.IsUnattributed ? 1 : 0)
                    .ThenByDescending(x => x.Total)
                    .ThenByDescending(x => x.Requests)
                    .ToList();

                long agGrand = agGroups.Sum(x => x.Total);
                int colorIdx = 0;
                foreach (var ag in agGroups)
                {
                    ag.ShareOfAll = agGrand > 0 ? ag.Total * 100.0 / agGrand : 0;
                    if (ag.IsUnattributed)
                    {
                        ag.AccentBrush = UnattributedPalette.Fg;
                        ag.BadgeBg = UnattributedPalette.BadgeBg;
                        ag.BadgeBorder = UnattributedPalette.BadgeBorder;
                        ag.TitleBrush = MakeFrozenBrush(0x64, 0x74, 0x8B);
                    }
                    else
                    {
                        var pal = AccountPalette[colorIdx % AccountPalette.Length];
                        colorIdx++;
                        ag.AccentBrush = pal.Fg;
                        ag.BadgeBg = pal.BadgeBg;
                        ag.BadgeBorder = pal.BadgeBorder;
                        ag.TitleBrush = MakeFrozenBrush(0x0F, 0x17, 0x2A);
                    }
                }

                // 3. 模型汇总分组
                var rawCleanKeys = pm
                    .Select(r => NormalizeModelKey(r.Model))
                    .Where(k => k.Length > 0 && !k.Contains("--") && !k.Contains('.'))
                    .Distinct()
                    .ToList();

                string ModelGroupKeyOf(string raw)
                {
                    var k = NormalizeModelKey(raw);
                    if (k.Length == 0) return "";
                    return ResolveDeploymentName(k, rawCleanKeys);
                }

                var mGroups = pm
                    .GroupBy(r => ModelGroupKeyOf(r.Model))
                    .Select(g =>
                    {
                        long mgIn = g.Sum(x => x.InTokens);
                        long mgOut = g.Sum(x => x.OutTokens);
                        long mgReq = g.Sum(x => x.Requests);
                        long mgTotal = mgIn + mgOut;
                        long mgCr = g.Sum(x => x.CacheRead);
                        long mgCw = g.Sum(x => x.CacheWrite);

                        string displayName = string.IsNullOrEmpty(g.Key)
                            ? unknownModelLabel
                            : g.Select(x => CleanModelName(x.Model))
                                .Where(s => !string.IsNullOrEmpty(s))
                                .OrderBy(s => s.Length)
                                .FirstOrDefault() ?? g.Key;

                        var providers = g
                            .GroupBy(x => (
                                Provider: string.IsNullOrEmpty(x.Provider) ? unknownModelLabel : x.Provider,
                                RawModel: x.Model ?? ""))
                            .Select(p =>
                            {
                                long pIn = p.Sum(x => x.InTokens);
                                long pOut = p.Sum(x => x.OutTokens);
                                long pTotal = pIn + pOut;
                                long pCr = p.Sum(x => x.CacheRead);
                                long pCw = p.Sum(x => x.CacheWrite);
                                string infAgent = InferAgentFromProvider(p.Key.Provider, filterAgent);
                                double pCost = UsagePricingService.EstimateCostUsd(
                                    p.Key.RawModel, p.Key.Provider, infAgent, pIn, pOut, pCr, pCw);
                                return new ModelProviderRowVM
                                {
                                    Provider = p.Key.Provider,
                                    RawModel = p.Key.RawModel,
                                    GroupName = displayName,
                                    AgentKey = infAgent,
                                    InTokens = pIn,
                                    OutTokens = pOut,
                                    Requests = p.Sum(x => x.Requests),
                                    Total = pTotal,
                                    CacheReadTokens = pCr,
                                    CacheWriteTokens = pCw,
                                    CostUsd = pCost,
                                    ShareOfModel = mgTotal > 0 ? pTotal * 100.0 / mgTotal : 0,
                                };
                            })
                            .OrderByDescending(p => p.Total).ThenByDescending(p => p.Requests)
                            .ToList();

                        return new ModelGroupVM
                        {
                            Name = displayName,
                            InTokens = mgIn,
                            OutTokens = mgOut,
                            Requests = mgReq,
                            Total = mgTotal,
                            CacheReadTokens = mgCr,
                            CacheWriteTokens = mgCw,
                            CostUsd = providers.Sum(x => x.CostUsd),
                            Providers = providers,
                            Expanded = expandedModels.Contains(displayName),
                        };
                    })
                    .OrderByDescending(x => x.Total).ThenByDescending(x => x.Requests)
                    .ToList();

                long modelGrand = mGroups.Sum(x => x.Total);
                foreach (var mg in mGroups) mg.ShareOfAll = modelGrand > 0 ? mg.Total * 100.0 / modelGrand : 0;

                // 4. 客户端汇总表
                long agentGrand = agTotals.Sum(a => a.InTokens + a.OutTokens);
                var aRows = agTotals.Select(a =>
                {
                    long t = a.InTokens + a.OutTokens;
                    cByAgent.TryGetValue(a.Agent, out var aUsd);
                    return new AgentRowVM
                    {
                        Agent = a.Agent,
                        Name = AgentName(a.Agent),
                        InTokens = a.InTokens,
                        OutTokens = a.OutTokens,
                        Requests = a.Requests,
                        Total = t,
                        CacheReadTokens = a.CacheRead,
                        CacheWriteTokens = a.CacheWrite,
                        CostUsd = aUsd,
                        ShareOfAll = agentGrand > 0 ? t * 100.0 / agentGrand : 0,
                    };
                }).OrderByDescending(x => x.Total).ThenByDescending(x => x.Requests).ToList();

                // 5. 最近调用明细
                var cRows = ev.Select(c =>
                {
                    var dt = c.Ts.ToLocalTime();
                    string effProv = !string.IsNullOrWhiteSpace(c.ProviderId) ? c.ProviderId : c.ProviderName;
                    double callCost = UsagePricingService.EstimateCostUsd(
                        c.Model, effProv, c.Agent, c.InTokens, c.OutTokens, c.CacheReadTokens, c.CacheWriteTokens);
                    return new CallRowVM
                    {
                        TimeText = dt.ToString("MM-dd HH:mm:ss"),
                        Agent = c.Agent,
                        RawProviderKey = effProv,
                        Provider = string.IsNullOrEmpty(c.ProviderName) ? "—" : c.ProviderName,
                        Model = string.IsNullOrEmpty(c.Model) ? "—" : c.Model,
                        InTokens = c.InTokens,
                        OutTokens = c.OutTokens,
                        Total = c.InTokens + c.OutTokens,
                        CacheReadTokens = c.CacheReadTokens,
                        CacheWriteTokens = c.CacheWriteTokens,
                        CostUsd = callCost,
                        DurationMs = c.DurationMs,
                        TtftMs = c.TtftMs,
                    };
                }).ToList();

                return (sMain, sAg, pMain, pAg, dMain, dAg, agTotals, cByAgent, mCost, aCost, pGroups, agGroups, mGroups, aRows, cRows);
            });
        }
        finally
        {
            _isRefreshingTokens = false;
        }

        _mainSummary = mainSummary;
        _agSummary = agSummary;
        _mainPerf = mainPerf;
        _agPerf = agPerf;
        _mainDaily = mainDaily;
        _agDaily = agDaily;
        _mainCostUsd = mainCostUsd;
        _agCostUsd = agCostUsd;
        _lastEffFrom = effFrom;
        _lastEffTo = effTo;

        _allAgentTotals = allAgentTotals;
        _allProviderGroups = provGroups;
        _allAgAccountGroups = rawAgGroups;
        _allModelGroups = modelGroups;
        _allAgentRows = agentRows;
        _allCalls = callRows;

        SyncClientFilterStates(allAgentTotals, costByAgent);
        UpdateFocusedDayChip();
        UpdateClearFilterButtonVisibility();

        if (_selectedDay.HasValue)
            RangeSummaryText.Text = $"{_selectedDay.Value:yyyy-MM-dd} (24h)";
        else
            RangeSummaryText.Text = $"{from:MM-dd HH:mm} → {to:MM-dd HH:mm}";

        int attributedAcctCount = rawAgGroups.Count(x => !x.IsUnattributed && x.Total > 0);
        if (AgAccountsSummaryText != null)
            AgAccountsSummaryText.Text = I18nService.F("Usage.AgAccountsSummaryFmt", attributedAcctCount);
        if (AgAccountChipsList != null)
            AgAccountChipsList.ItemsSource = rawAgGroups.Where(x => !x.IsUnattributed || x.ShareOfAll >= 0.2).ToList();

        SortAllViewGroups();
        UpdateSortHeaderLabels();
        ApplySearchFilter();
        ApplyKpisAndMixForCurrentTab();
        UpdateExpandAllBtnText();
        UpdateEmptyOverlayVisibility();
        ScheduleChartRedraw();

        if (_refreshPending)
        {
            _refreshPending = false;
            RefreshData();
        }
    }

    /// <summary>
    /// 零查库内存秒切顶部 6 格指标带、每日趋势图数据源与占比条（在「反重力明细」与其余 4 个页签间切换仅需 &lt;0.2ms）。
    /// </summary>
    void ApplyKpisAndMixForCurrentTab()
    {
        bool isAgTab = IsAntigravityTabActive;
        var summary = isAgTab ? _agSummary : _mainSummary;
        var perf = isAgTab ? _agPerf : _mainPerf;
        var daily = isAgTab ? _agDaily : _mainDaily;
        double activeTotalCostUsd = isAgTab ? _agCostUsd : _mainCostUsd;
        string? activeAgent = EffectiveAgentFilter;

        _daily = daily;

        long totalTokens = summary.TotalIn + summary.TotalOut;
        TotalTokensText.Text = FormatTokens(totalTokens);
        long activeDays = Math.Max(1, summary.ActiveDays);
        TotalSubText.Text = totalTokens > 0
            ? I18nService.F("Usage.ActiveDaysSubFmt", summary.ActiveDays, FormatTokens(totalTokens / activeDays))
            : "—";

        EstCostText.Text = UsagePricingService.FormatUsd(activeTotalCostUsd);
        EstCostSubText.Text = activeTotalCostUsd > 0.000001
            ? I18nService.F("Usage.EstCostSubFmt", UsagePricingService.FormatUsd(activeTotalCostUsd / activeDays))
            : "—";

        InTokensText.Text = FormatTokens(summary.TotalIn);
        if (perf.CacheRead > 0 || perf.CacheWrite > 0)
        {
            long freshEst = summary.TotalIn >= perf.CacheRead
                ? summary.TotalIn - perf.CacheRead
                : summary.TotalIn;
            InSubText.Text = I18nService.F("Usage.FreshCacheSubFmt",
                FormatTokens(freshEst), FormatTokens(perf.CacheRead));
        }
        else
        {
            InSubText.Text = summary.Requests > 0
                ? $"avg {FormatTokens(summary.TotalIn / Math.Max(1, summary.Requests))}/req"
                : "—";
        }

        OutTokensText.Text = FormatTokens(summary.TotalOut);
        long avgOut = summary.Requests > 0 ? summary.TotalOut / summary.Requests : 0;
        string ioRatio = summary.TotalOut > 0 && summary.TotalIn > 0
            ? $"{(double)summary.TotalIn / summary.TotalOut:0.#}:1"
            : "—";
        OutSubText.Text = summary.Requests > 0
            ? I18nService.F("Usage.IoRatioSubFmt", FormatTokens(avgOut), ioRatio)
            : "—";

        RequestsText.Text = summary.Requests.ToString("N0", CultureInfo.CurrentCulture);
        if (summary.Requests > 0)
        {
            double daySpan = Math.Max(1.0, (_lastEffTo - _lastEffFrom).TotalDays);
            double dailyReq = summary.Requests / daySpan;
            long peakReq = daily.Count > 0 ? daily.Max(d => d.Requests) : summary.Requests;
            ReqSubText.Text = I18nService.F("Usage.ReqDailySubFmt",
                dailyReq.ToString("0.#", CultureInfo.CurrentCulture),
                peakReq.ToString("N0", CultureInfo.CurrentCulture));
        }
        else
        {
            ReqSubText.Text = "—";
        }

        if (perf.CacheRead + perf.CacheWrite > 0)
        {
            CacheHitText.Text = FormatCacheHitRate(summary.TotalIn, perf.CacheRead, perf.CacheWrite);
            CacheHitDetailText.Text = I18nService.F("Usage.CacheHitDetailFmt",
                FormatTokens(perf.CacheRead), FormatTokens(perf.CacheWrite));
        }
        else
        {
            CacheHitText.Text = "—";
            CacheHitDetailText.Text = I18nService.T("Usage.CacheHitNoData");
        }

        if (isAgTab)
            RenderAntigravityAccountMixBar(_allAgAccountGroups);
        else
            RenderClientMixBar(_allAgentTotals);

        _hasUsageData = isAgTab
            ? (_allAgAccountGroups.Count > 0 || summary.Requests > 0)
            : (summary.Requests > 0 || _allProviderGroups.Count > 0 || _daily.Count > 0);

        UpdateFooterStatus(summary, perf, activeTotalCostUsd, activeAgent);
    }

    static string InferAgentFromProvider(string? provider, string? activeAgent)
    {
        if (!string.IsNullOrEmpty(activeAgent)) return activeAgent;
        if (string.Equals(provider, "Antigravity", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(provider, UsageStorage.AgentAntigravity, StringComparison.OrdinalIgnoreCase))
            return UsageStorage.AgentAntigravity;
        if (string.Equals(provider, UsageStorage.AgentGeminiCli, StringComparison.OrdinalIgnoreCase))
            return UsageStorage.AgentGeminiCli;
        if (string.Equals(provider, UsageStorage.AgentClaudeCli, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(provider, UsageStorage.AgentClaudeDesktop, StringComparison.OrdinalIgnoreCase))
            return UsageStorage.AgentClaudeCli;
        if (string.Equals(provider, UsageStorage.AgentCodex, StringComparison.OrdinalIgnoreCase))
            return UsageStorage.AgentCodex;
        return "";
    }

    static (Brush Fg, Brush Bg) ResolveTierColors(string? tier)
    {
        var t = (tier ?? "").Trim().ToLowerInvariant();
        if (t.Contains("ultra", StringComparison.Ordinal))
            return (MakeFrozenBrush(0x7C, 0x3A, 0xED), MakeFrozenBrush(0xF3, 0xE8, 0xFF));
        if (t.Contains("pro", StringComparison.Ordinal))
            return (MakeFrozenBrush(0x4F, 0x46, 0xE5), MakeFrozenBrush(0xEE, 0xF2, 0xFF));
        if (t.Contains("plus", StringComparison.Ordinal))
            return (MakeFrozenBrush(0x02, 0x84, 0xC7), MakeFrozenBrush(0xE0, 0xF2, 0xFE));
        return (MakeFrozenBrush(0x47, 0x55, 0x69), MakeFrozenBrush(0xF1, 0xF5, 0xF9));
    }

    void ApplySearchFilter()
    {
        if (string.IsNullOrEmpty(_searchQuery))
        {
            _filteredProviders = _allProviderGroups;
            _filteredAg = _allAgAccountGroups;
            _filteredAgents = _allAgentRows;
            _filteredModels = _allModelGroups;
            _filteredCalls = _allCalls;
            BindVisibleTabItemsSourceOnly();
            return;
        }

        string q = _searchQuery;

        // 1. 供应商面板
        var filteredProvs = new List<ProviderGroupVM>();
        foreach (var pg in _allProviderGroups)
        {
            if (pg.Name.Contains(q, StringComparison.OrdinalIgnoreCase))
            {
                filteredProvs.Add(pg);
                continue;
            }
            var subModels = pg.Models
                .Where(m => m.Name.Contains(q, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (subModels.Count > 0)
            {
                filteredProvs.Add(new ProviderGroupVM
                {
                    Name = pg.Name,
                    InTokens = pg.InTokens,
                    OutTokens = pg.OutTokens,
                    Requests = pg.Requests,
                    Total = pg.Total,
                    CacheReadTokens = pg.CacheReadTokens,
                    CacheWriteTokens = pg.CacheWriteTokens,
                    CostUsd = pg.CostUsd,
                    ShareOfAll = pg.ShareOfAll,
                    Models = subModels,
                    Expanded = true,
                });
            }
        }
        _filteredProviders = filteredProvs;

        // 1B. 反重力明细面板（匹配邮箱、档位或旗下模型名）
        var filteredAg = new List<AgAccountGroupVM>();
        foreach (var ag in _allAgAccountGroups)
        {
            if (ag.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                ag.TierText.Contains(q, StringComparison.OrdinalIgnoreCase))
            {
                filteredAg.Add(ag);
                continue;
            }
            var subModels = ag.Models
                .Where(m => m.Name.Contains(q, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (subModels.Count > 0)
            {
                filteredAg.Add(new AgAccountGroupVM
                {
                    AccountKey = ag.AccountKey,
                    Email = ag.Email,
                    IsUnattributed = ag.IsUnattributed,
                    DisplayName = ag.DisplayName,
                    ShortLabel = ag.ShortLabel,
                    TierText = ag.TierText,
                    TierFg = ag.TierFg,
                    TierBg = ag.TierBg,
                    IsCurrentAccount = ag.IsCurrentAccount,
                    AccentBrush = ag.AccentBrush,
                    BadgeBg = ag.BadgeBg,
                    BadgeBorder = ag.BadgeBorder,
                    TitleBrush = ag.TitleBrush,
                    InTokens = ag.InTokens,
                    OutTokens = ag.OutTokens,
                    Requests = ag.Requests,
                    Total = ag.Total,
                    CacheReadTokens = ag.CacheReadTokens,
                    CacheWriteTokens = ag.CacheWriteTokens,
                    CostUsd = ag.CostUsd,
                    ShareOfAll = ag.ShareOfAll,
                    Models = subModels,
                    Expanded = true,
                });
            }
        }
        _filteredAg = filteredAg;

        // 2. 客户端面板
        _filteredAgents = _allAgentRows
            .Where(a => a.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                     || a.Agent.Contains(q, StringComparison.OrdinalIgnoreCase))
            .ToList();

        // 3. 模型面板
        var filteredModels = new List<ModelGroupVM>();
        foreach (var mg in _allModelGroups)
        {
            if (mg.Name.Contains(q, StringComparison.OrdinalIgnoreCase))
            {
                filteredModels.Add(mg);
                continue;
            }
            var subProvs = mg.Providers
                .Where(p => p.Provider.Contains(q, StringComparison.OrdinalIgnoreCase)
                         || p.RawModel.Contains(q, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (subProvs.Count > 0)
            {
                filteredModels.Add(new ModelGroupVM
                {
                    Name = mg.Name,
                    InTokens = mg.InTokens,
                    OutTokens = mg.OutTokens,
                    Requests = mg.Requests,
                    Total = mg.Total,
                    CacheReadTokens = mg.CacheReadTokens,
                    CacheWriteTokens = mg.CacheWriteTokens,
                    CostUsd = mg.CostUsd,
                    ShareOfAll = mg.ShareOfAll,
                    Providers = subProvs,
                    Expanded = true,
                });
            }
        }
        _filteredModels = filteredModels;

        // 4. 调用明细面板
        _filteredCalls = _allCalls
            .Where(c => c.Provider.Contains(q, StringComparison.OrdinalIgnoreCase)
                     || c.Model.Contains(q, StringComparison.OrdinalIgnoreCase)
                     || c.AgentName.Contains(q, StringComparison.OrdinalIgnoreCase)
                     || c.Agent.Contains(q, StringComparison.OrdinalIgnoreCase))
            .ToList();

        BindVisibleTabItemsSourceOnly();
    }

    /// <summary>
    /// 按需仅向当前处于可见态的单个 ListBox 绑定 ItemsSource，隐藏的 4 个面板零视觉树构建开销。
    /// </summary>
    void BindVisibleTabItemsSourceOnly()
    {
        if (ProvidersPanel?.Visibility == Visibility.Visible)
        {
            if (!ReferenceEquals(ProvidersList.ItemsSource, _filteredProviders))
                ProvidersList.ItemsSource = _filteredProviders;
        }
        else if (AntigravityPanel?.Visibility == Visibility.Visible)
        {
            if (!ReferenceEquals(AntigravityList.ItemsSource, _filteredAg))
                AntigravityList.ItemsSource = _filteredAg;
        }
        else if (AgentsPanel?.Visibility == Visibility.Visible)
        {
            if (!ReferenceEquals(AgentsList.ItemsSource, _filteredAgents))
                AgentsList.ItemsSource = _filteredAgents;
        }
        else if (ModelsPanel?.Visibility == Visibility.Visible)
        {
            if (!ReferenceEquals(ModelsList.ItemsSource, _filteredModels))
                ModelsList.ItemsSource = _filteredModels;
        }
        else if (CallsPanel?.Visibility == Visibility.Visible)
        {
            if (!ReferenceEquals(CallsList.ItemsSource, _filteredCalls))
                CallsList.ItemsSource = _filteredCalls;
        }
    }

    void RenderClientMixBar(List<UsageStorage.AgentTotal> allAgents)
    {
        if (ClientMixBar == null || ClientMixPanel == null) return;
        if (MixBarLabelText != null)
            MixBarLabelText.Text = I18nService.T("Usage.ClientMixLabel");
        ClientMixBar.Children.Clear();

        long total = allAgents.Sum(a => a.InTokens + a.OutTokens);
        if (total <= 0)
        {
            ClientMixPanel.Visibility = Visibility.Collapsed;
            return;
        }

        ClientMixPanel.Visibility = Visibility.Visible;
        var sorted = allAgents
            .Select(a => (a.Agent, Total: a.InTokens + a.OutTokens, a.Requests))
            .Where(x => x.Total > 0)
            .OrderByDescending(x => x.Total)
            .ToList();

        const double trackW = 170.0;
        double usedW = 0;
        for (int i = 0; i < sorted.Count; i++)
        {
            var item = sorted[i];
            double frac = (double)item.Total / total;
            double segW = i == sorted.Count - 1
                ? Math.Max(0, trackW - usedW)
                : Math.Max(3.0, Math.Round(trackW * frac, 1));
            if (usedW + segW > trackW) segW = Math.Max(0, trackW - usedW);
            usedW += segW;
            if (segW < 0.5) continue;

            var (fg, _) = AgentColors(item.Agent);
            bool dimmed = !string.IsNullOrEmpty(_agentFilter) &&
                          !string.Equals(_agentFilter, item.Agent, StringComparison.OrdinalIgnoreCase);

            var seg = new Border
            {
                Width = segW,
                Height = 7,
                Background = fg,
                Opacity = dimmed ? 0.28 : 1.0,
                Cursor = Cursors.Hand,
                Margin = i > 0 ? new Thickness(1, 0, 0, 0) : new Thickness(0),
                ToolTip = $"{AgentName(item.Agent)}: {FormatTokens(item.Total)} ({frac * 100:F1}% · {item.Requests:N0} reqs)"
            };
            string capturedAgent = item.Agent;
            seg.MouseLeftButtonUp += (_, _) => ToggleAgentFilter(capturedAgent);
            ClientMixBar.Children.Add(seg);
        }
    }

    void RenderAntigravityAccountMixBar(List<AgAccountGroupVM> agGroups)
    {
        if (ClientMixBar == null || ClientMixPanel == null) return;
        if (MixBarLabelText != null)
            MixBarLabelText.Text = I18nService.T("Usage.AgAccountMixLabel");
        ClientMixBar.Children.Clear();

        long total = agGroups.Sum(a => a.Total);
        if (total <= 0)
        {
            ClientMixPanel.Visibility = Visibility.Collapsed;
            return;
        }

        ClientMixPanel.Visibility = Visibility.Visible;
        var sorted = agGroups.Where(x => x.Total > 0).ToList();
        const double trackW = 170.0;
        double usedW = 0;
        for (int i = 0; i < sorted.Count; i++)
        {
            var item = sorted[i];
            double frac = (double)item.Total / total;
            double segW = i == sorted.Count - 1
                ? Math.Max(0, trackW - usedW)
                : Math.Max(3.0, Math.Round(trackW * frac, 1));
            if (usedW + segW > trackW) segW = Math.Max(0, trackW - usedW);
            usedW += segW;
            if (segW < 0.5) continue;

            var seg = new Border
            {
                Width = segW,
                Height = 7,
                Background = item.AccentBrush,
                Cursor = Cursors.Hand,
                Margin = i > 0 ? new Thickness(1, 0, 0, 0) : new Thickness(0),
                ToolTip = $"{item.DisplayName}: {FormatTokens(item.Total)} · {item.CostText} ({frac * 100:F1}% · {item.Requests:N0} reqs)"
            };
            var captured = item;
            seg.MouseLeftButtonUp += (_, _) => captured.Expanded = !captured.Expanded;
            ClientMixBar.Children.Add(seg);
        }
    }

    void UpdateFooterStatus(UsageStorage.UsageSummary summary, UsageStorage.PerformanceStats perf, double costUsd, string? activeAgent)
    {
        if (FooterStatusText == null) return;

        var parts = new List<string>
        {
            I18nService.F("Usage.FooterSummary", summary.Requests.ToString("N0", CultureInfo.CurrentCulture), summary.ActiveDays)
        };
        if (costUsd > 0.000001)
            parts.Add($"≈ {UsagePricingService.FormatUsd(costUsd)}");
        double avgSpeed = perf.TotalDurationMs > 0 && perf.TotalOutTokens > 0
            ? perf.TotalOutTokens * 1000.0 / perf.TotalDurationMs
            : 0;
        if (avgSpeed > 0)
            parts.Add($"avg {avgSpeed:0.#} t/s");
        double avgTtft = perf.TtftSamples > 0 ? perf.TotalTtftMs / (double)perf.TtftSamples : 0;
        if (avgTtft > 0)
        {
            string ttft = avgTtft >= 1000
                ? $"{avgTtft / 1000.0:0.00}s"
                : $"{Math.Round(avgTtft)}ms";
            parts.Add($"TTFT {ttft}");
        }
        if (!string.IsNullOrEmpty(activeAgent))
            parts.Add($"@{AgentName(activeAgent)}");
        FooterStatusText.Text = string.Join(" · ", parts);
    }

    void UpdateEmptyOverlayVisibility()
    {
        if (EmptyOverlay == null) return;
        EmptyOverlay.Visibility = !_hasUsageData ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---------- 趋势图渲染（柱状堆叠 + 方格热力） ----------

    void OnChartSizeChanged(object sender, SizeChangedEventArgs e) => ScheduleChartRedraw();

    void ScheduleChartRedraw()
    {
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(DrawTrendChart));
    }

    internal void TestRenderExtremeTrend(List<UsageStorage.DailyTotal> sample)
    {
        _daily = sample;
        DrawTrendChart();
    }

    void DrawTrendChart()
    {
        var canvas = TrendCanvas;
        if (canvas == null) return;
        canvas.Children.Clear();

        if (_daily.Count == 0) return;

        bool isHeatmap = _chartMode == "heatmap";
        if (isHeatmap)
            DrawHeatmapChart(canvas);
        else
            DrawBarChart(canvas);
    }

    void DrawBarChart(Canvas canvas)
    {
        double w = canvas.ActualWidth > 0 ? canvas.ActualWidth : 940;
        double h = canvas.ActualHeight > 0 ? canvas.ActualHeight : 96;
        const double topPad = 8, bottomPad = 18, leftPad = 6, rightAxisW = 54;
        double plotW = Math.Max(80, w - leftPad - rightAxisW);
        double plotH = Math.Max(20, h - topPad - bottomPad);

        // 在 ≤92 天区间下补齐连续日历空日，使柱状图时间轴真实等距；极端自检样本保持原序列
        var series = _daily;
        if (TryGetRange(out var rFrom, out var rTo))
        {
            int spanDays = (int)(rTo.Date - rFrom.Date).TotalDays + 1;
            if (spanDays >= 2 && spanDays <= 92 &&
                _daily.All(x => DateTime.TryParse(x.Day, out var dt) && dt.Date >= rFrom.Date && dt.Date <= rTo.Date))
            {
                var byDay = _daily.ToDictionary(x => x.Day, x => x);
                var filled = new List<UsageStorage.DailyTotal>(spanDays);
                for (int k = 0; k < spanDays; k++)
                {
                    var key = rFrom.Date.AddDays(k).ToString("yyyy-MM-dd");
                    filled.Add(byDay.TryGetValue(key, out var hit) ? hit : new UsageStorage.DailyTotal(key, 0, 0, 0));
                }
                series = filled;
            }
        }

        long maxTotal = series.Max(d => d.InTokens + d.OutTokens);
        if (maxTotal <= 0) maxTotal = 1;

        var gridBrush = new SolidColorBrush(Color.FromRgb(0xEB, 0xF0, 0xF7));
        var tickTextBrush = new SolidColorBrush(Color.FromRgb(0x94, 0xA3, 0xB8));

        for (int i = 0; i <= 2; i++)
        {
            double frac = i / 2.0;
            double y = topPad + plotH * (1 - frac);
            var line = new Line
            {
                X1 = leftPad,
                X2 = leftPad + plotW,
                Y1 = y,
                Y2 = y,
                Stroke = gridBrush,
                StrokeThickness = i == 0 ? 1.2 : 1,
                StrokeDashArray = i == 0 ? null : new DoubleCollection { 3, 3 },
            };
            canvas.Children.Add(line);

            if (i > 0)
            {
                long tickVal = (long)Math.Round(maxTotal * frac);
                var lbl = new TextBlock
                {
                    Text = FormatTokens(tickVal),
                    FontSize = 9.5,
                    Width = rightAxisW - 6,
                    TextAlignment = TextAlignment.Right,
                    Foreground = tickTextBrush,
                };
                Canvas.SetLeft(lbl, leftPad + plotW + 4);
                Canvas.SetTop(lbl, Math.Max(0, y - 7));
                canvas.Children.Add(lbl);
            }
        }

        int n = series.Count;
        double slot = plotW / n;
        double barW = Math.Clamp(slot * 0.62, 2.5, 28);

        var inBrush = new SolidColorBrush(Color.FromRgb(0xC7, 0xD2, 0xFE));
        var outBrush = new SolidColorBrush(Color.FromRgb(0x4F, 0x46, 0xE5));
        var inSelBrush = new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B));
        var outSelBrush = new SolidColorBrush(Color.FromRgb(0xB4, 0x53, 0x09));
        var selWash = new SolidColorBrush(Color.FromRgb(0xEE, 0xF2, 0xFF));

        for (int i = 0; i < n; i++)
        {
            var d = series[i];
            double cx = leftPad + slot * (i + 0.5);
            double x = cx - barW / 2;
            long dayTotal = d.InTokens + d.OutTokens;
            double totalH = dayTotal > 0 ? Math.Clamp(plotH * dayTotal / maxTotal, 2.5, plotH) : 0;
            double outH = d.OutTokens > 0
                ? Math.Min(totalH, Math.Max(1.8, plotH * d.OutTokens / maxTotal))
                : 0;
            double inH = Math.Max(0, totalH - outH);
            double baseY = topPad + plotH;
            bool selected = _selectedDay.HasValue && d.Day == _selectedDay.Value.ToString("yyyy-MM-dd");

            if (selected)
            {
                var wash = new Rectangle
                {
                    Width = Math.Max(barW + 6, Math.Min(slot, barW + 10)),
                    Height = plotH,
                    RadiusX = 4,
                    RadiusY = 4,
                    Fill = selWash,
                };
                Canvas.SetLeft(wash, cx - wash.Width / 2);
                Canvas.SetTop(wash, topPad);
                canvas.Children.Add(wash);
            }

            if (inH > 0)
            {
                var rectIn = new Rectangle
                {
                    Width = barW,
                    Height = inH,
                    Fill = selected ? inSelBrush : inBrush,
                    RadiusX = outH > 0 ? 0 : 2.5,
                    RadiusY = outH > 0 ? 0 : 2.5,
                };
                Canvas.SetLeft(rectIn, x);
                Canvas.SetTop(rectIn, baseY - inH);
                canvas.Children.Add(rectIn);
            }

            if (outH > 0)
            {
                var rectOut = new Rectangle
                {
                    Width = barW,
                    Height = outH,
                    Fill = selected ? outSelBrush : outBrush,
                    RadiusX = 2.5,
                    RadiusY = 2.5,
                };
                Canvas.SetLeft(rectOut, x);
                Canvas.SetTop(rectOut, baseY - inH - outH);
                canvas.Children.Add(rectOut);
            }

            string tip = $"{d.Day}\n"
                       + $"{I18nService.F("Usage.ChartTooltipTotal", FormatTokens(dayTotal))}\n"
                       + $"{I18nService.F("Usage.InOut", FormatTokens(d.InTokens), FormatTokens(d.OutTokens))}\n"
                       + $"{I18nService.F("Usage.ChartDayCallsFmt", d.Requests.ToString("N0", CultureInfo.CurrentCulture))}";

            var hit = new Rectangle
            {
                Width = Math.Max(slot, 6),
                Height = plotH + 4,
                Fill = Brushes.Transparent,
                Cursor = Cursors.Hand,
                Tag = d.Day,
                ToolTip = tip,
            };
            hit.MouseLeftButtonUp += OnDaySlotClick;
            Canvas.SetLeft(hit, leftPad + slot * i);
            Canvas.SetTop(hit, topPad);
            canvas.Children.Add(hit);
        }

        var labelIndices = new HashSet<int> { 0, n - 1 };
        if (n >= 7) labelIndices.Add(n / 2);
        if (n >= 16)
        {
            labelIndices.Add(n / 4);
            labelIndices.Add(3 * n / 4);
        }

        foreach (int idx in labelIndices.OrderBy(i => i))
        {
            double cx = leftPad + slot * (idx + 0.5);
            var dtStr = series[idx].Day.Length >= 10 ? series[idx].Day[5..] : series[idx].Day;
            var tb = new TextBlock
            {
                Text = dtStr,
                FontSize = 9.5,
                Foreground = tickTextBrush,
            };
            tb.Measure(new Size(60, 20));
            double maxLblX = Math.Max(leftPad, leftPad + plotW - tb.DesiredSize.Width);
            double lx = Math.Clamp(cx - tb.DesiredSize.Width / 2, leftPad, maxLblX);
            Canvas.SetLeft(tb, lx);
            Canvas.SetTop(tb, topPad + plotH + 3);
            canvas.Children.Add(tb);
        }
    }

    void DrawHeatmapChart(Canvas canvas)
    {
        double w = canvas.ActualWidth > 0 ? canvas.ActualWidth : 940;
        double h = canvas.ActualHeight > 0 ? canvas.ActualHeight : 106;
        const double leftPad = 24, topPad = 15;

        if (!DateTime.TryParse(_daily[0].Day, out var firstDay)) return;
        if (!DateTime.TryParse(_daily[^1].Day, out var lastDay)) return;

        DateTime? rangeStart = null, rangeEnd = null;
        // 正常筛选下按完整 52 周年度网格铺满全宽，并对所选区间外日期做柔和淡化，彻底消除右侧留白
        if (TryGetRange(out var rFrom, out var rTo) &&
            _daily.All(x => DateTime.TryParse(x.Day, out var dt) && dt.Date >= rFrom.Date.AddDays(-7) && dt.Date <= rTo.Date.AddDays(7)))
        {
            rangeStart = rFrom.Date;
            rangeEnd = rTo.Date;
            if (rTo.Date > lastDay) lastDay = rTo.Date;
            if (rFrom.Date < firstDay) firstDay = rFrom.Date;
            var annualStart = lastDay.AddDays(-363);
            if (annualStart < firstDay) firstDay = annualStart;
            if ((lastDay - firstDay).TotalDays > 371)
                firstDay = lastDay.AddDays(-370);
        }

        int startDow = ((int)firstDay.DayOfWeek + 6) % 7;
        var gridStart = firstDay.AddDays(-startDow);
        int endDow = ((int)lastDay.DayOfWeek + 6) % 7;
        var gridEnd = lastDay.AddDays(6 - endDow);

        int totalDays = (int)(gridEnd - gridStart).TotalDays + 1;
        int totalCols = Math.Max(1, (totalDays + 6) / 7);

        double availW = Math.Max(200, w - leftPad - 6);
        double colPitch = Math.Clamp(availW / totalCols, 9.0, 26.0);
        double rowPitch = Math.Clamp((h - topPad - 2) / 7.0, 9.0, 13.0);
        double cellW = Math.Max(6.0, Math.Floor(colPitch - 3.0));
        double cellH = Math.Max(6.0, Math.Floor(rowPitch - 2.2));

        var tickTextBrush = new SolidColorBrush(Color.FromRgb(0x94, 0xA3, 0xB8));
        string[] dowKeys = { "Usage.Dow1", "Usage.Dow3", "Usage.Dow5" };
        int[] dowRows = { 0, 2, 4 };
        for (int r = 0; r < 3; r++)
        {
            var tb = new TextBlock
            {
                Text = I18nService.T(dowKeys[r]),
                FontSize = 9,
                Foreground = tickTextBrush,
            };
            double y = topPad + dowRows[r] * rowPitch + (cellH - 10) / 2.0;
            Canvas.SetLeft(tb, 4);
            Canvas.SetTop(tb, Math.Max(topPad, y));
            canvas.Children.Add(tb);
        }

        var map = _daily.ToDictionary(d => d.Day, d => d);
        long maxTotal = _daily.Max(d => d.InTokens + d.OutTokens);
        if (maxTotal <= 0) maxTotal = 1;

        var cOutRange = new SolidColorBrush(Color.FromRgb(0xF8, 0xFA, 0xFC));
        var cOutStroke = new SolidColorBrush(Color.FromRgb(0xEE, 0xF2, 0xF7));
        var c0 = new SolidColorBrush(Color.FromRgb(0xEE, 0xF2, 0xF7));
        var c0Stroke = new SolidColorBrush(Color.FromRgb(0xE2, 0xE8, 0xF0));
        var c1 = new SolidColorBrush(Color.FromRgb(0xC7, 0xD2, 0xFE));
        var c2 = new SolidColorBrush(Color.FromRgb(0x81, 0x8C, 0xF8));
        var c3 = new SolidColorBrush(Color.FromRgb(0x63, 0x66, 0xF1));
        var c4 = new SolidColorBrush(Color.FromRgb(0x43, 0x38, 0xCA));
        var selStroke = new SolidColorBrush(Color.FromRgb(0xD9, 0x77, 0x06));

        int lastLabeledMonth = -1;
        double lastMonthX = -100;

        for (int idx = 0; idx < totalDays; idx++)
        {
            var curDate = gridStart.AddDays(idx);
            if (curDate < firstDay || curDate > lastDay) continue;

            int col = idx / 7;
            int row = idx % 7;
            double x = leftPad + col * colPitch;
            double y = topPad + row * rowPitch;

            if (curDate.Month != lastLabeledMonth && (curDate.Day <= 7 || idx == 0))
            {
                if (x - lastMonthX >= 28)
                {
                    lastLabeledMonth = curDate.Month;
                    lastMonthX = x;
                    string monthText = curDate.ToString("MMM", CultureInfo.CurrentCulture);
                    var mtb = new TextBlock
                    {
                        Text = monthText,
                        FontSize = 9.5,
                        Foreground = tickTextBrush,
                    };
                    Canvas.SetLeft(mtb, x);
                    Canvas.SetTop(mtb, 0);
                    canvas.Children.Add(mtb);
                }
            }

            bool inSelectedRange = (!rangeStart.HasValue || curDate.Date >= rangeStart.Value) &&
                                   (!rangeEnd.HasValue || curDate.Date <= rangeEnd.Value);

            string key = curDate.ToString("yyyy-MM-dd");
            map.TryGetValue(key, out var d);
            long dayTotal = d != null ? (d.InTokens + d.OutTokens) : 0;

            Brush fill = inSelectedRange ? c0 : cOutRange;
            if (dayTotal > 0)
            {
                double ratio = Math.Log10(dayTotal + 1) / Math.Log10(maxTotal + 1);
                fill = ratio switch
                {
                    >= 0.85 => c4,
                    >= 0.62 => c3,
                    >= 0.38 => c2,
                    _ => c1,
                };
            }

            bool selected = _selectedDay.HasValue && key == _selectedDay.Value.ToString("yyyy-MM-dd");
            string tip = d != null
                ? $"{key}\n{I18nService.F("Usage.ChartTooltipTotal", FormatTokens(dayTotal))}\n{I18nService.F("Usage.InOut", FormatTokens(d.InTokens), FormatTokens(d.OutTokens))}\n{I18nService.F("Usage.ChartDayCallsFmt", d.Requests.ToString("N0", CultureInfo.CurrentCulture))}"
                : $"{key}\n{I18nService.F("Usage.ChartTooltipTotal", "0")}";

            var cell = new Rectangle
            {
                Width = cellW,
                Height = cellH,
                RadiusX = 2.6,
                RadiusY = 2.6,
                Fill = fill,
                Stroke = selected ? selStroke : (dayTotal == 0 ? (inSelectedRange ? c0Stroke : cOutStroke) : null),
                StrokeThickness = selected ? 1.8 : (dayTotal == 0 ? 0.65 : 0),
                Cursor = Cursors.Hand,
                Tag = key,
                ToolTip = tip,
            };
            cell.MouseLeftButtonUp += OnDaySlotClick;
            Canvas.SetLeft(cell, x);
            Canvas.SetTop(cell, y);
            canvas.Children.Add(cell);
        }
    }

    void OnDaySlotClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is string dayStr && DateTime.TryParse(dayStr, out var day))
        {
            _selectedDay = _selectedDay?.Date == day.Date ? null : day.Date;
            UpdateFocusedDayChip();
            UpdateClearFilterButtonVisibility();
            RefreshData();
        }
    }

    // ---------- 视图切换 ----------

    void OnTabProvidersChecked(object sender, RoutedEventArgs e) => SwitchView(ProvidersPanel);
    void OnTabAntigravityChecked(object sender, RoutedEventArgs e) => SwitchView(AntigravityPanel);
    void OnTabAgentsChecked(object sender, RoutedEventArgs e) => SwitchView(AgentsPanel);
    void OnTabModelsChecked(object sender, RoutedEventArgs e) => SwitchView(ModelsPanel);
    void OnTabCallsChecked(object sender, RoutedEventArgs e) => SwitchView(CallsPanel);

    void SwitchView(FrameworkElement target)
    {
        if (ProvidersPanel == null || AntigravityPanel == null || AgentsPanel == null || ModelsPanel == null || CallsPanel == null)
            return;

        bool nextAgTab = target == AntigravityPanel;
        ProvidersPanel.Visibility = target == ProvidersPanel ? Visibility.Visible : Visibility.Collapsed;
        AntigravityPanel.Visibility = nextAgTab ? Visibility.Visible : Visibility.Collapsed;
        AgentsPanel.Visibility = target == AgentsPanel ? Visibility.Visible : Visibility.Collapsed;
        ModelsPanel.Visibility = target == ModelsPanel ? Visibility.Visible : Visibility.Collapsed;
        CallsPanel.Visibility = target == CallsPanel ? Visibility.Visible : Visibility.Collapsed;

        // 「反重力明细」页签固定聚焦 Antigravity，自动收起多客户端胶囊行以留出展示空间
        if (ClientFilterDivider != null)
            ClientFilterDivider.Visibility = nextAgTab ? Visibility.Collapsed : Visibility.Visible;
        if (ClientFilterList != null)
            ClientFilterList.Visibility = nextAgTab ? Visibility.Collapsed : Visibility.Visible;

        bool modeChanged = nextAgTab != _wasAntigravityTab;
        _wasAntigravityTab = nextAgTab;
        _allExpanded = false;

        BindVisibleTabItemsSourceOnly();
        UpdateFocusedDayChip();
        UpdateClearFilterButtonVisibility();
        UpdateExpandAllBtnText();

        if (modeChanged && IsLoaded)
        {
            ApplyKpisAndMixForCurrentTab();
        }

        UpdateEmptyOverlayVisibility();
        ScheduleChartRedraw();
    }

    void OnToggleProviderExpand(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is ProviderGroupVM g)
            g.Expanded = !g.Expanded;
    }

    void OnToggleAgAccountExpand(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is AgAccountGroupVM g)
            g.Expanded = !g.Expanded;
    }

    void OnToggleModelExpand(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is ModelGroupVM g)
            g.Expanded = !g.Expanded;
    }

    // ---------- 顶部工具操作（定价中心 / 刷新 / 导出 CSV / 回填 / 帮助 / 窗口控制） ----------

    void OnOpenPricingCenterClick(object sender, RoutedEventArgs e)
    {
        var dlg = new PricingCenterDialog { Owner = this };
        dlg.ShowDialog();
        if (dlg.HasChanges)
            RefreshData();
    }

    void OnOpenAgQuotaInsightsClick(object sender, RoutedEventArgs e)
    {
        string? initialEmail = _allAgAccountGroups.FirstOrDefault(x => !x.IsUnattributed && x.IsCurrentAccount)?.Email
                               ?? _allAgAccountGroups.FirstOrDefault(x => !x.IsUnattributed)?.Email;
        AgQuotaInsightsDialog.ShowOrActivate(initialEmail, this, RefreshData);
    }

    void OnOpenAccountRowInsightsClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is FrameworkElement fe && fe.DataContext is AgAccountGroupVM vm && !vm.IsUnattributed)
        {
            AgQuotaInsightsDialog.ShowOrActivate(vm.Email, this, RefreshData);
        }
    }

    void OnAgChipMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is AgAccountGroupVM vm && !vm.IsUnattributed)
        {
            AgQuotaInsightsDialog.ShowOrActivate(vm.Email, this, RefreshData);
        }
    }

    async void OnRefreshNowClick(object sender, RoutedEventArgs e)
    {
        if (RefreshBtn == null) return;
        RefreshBtn.IsEnabled = false;
        try
        {
            await Task.Run(() => UsageScanner.ScanAll());
            RefreshData();
        }
        finally
        {
            RefreshBtn.IsEnabled = true;
        }
    }

    async void OnExportCsvClick(object sender, RoutedEventArgs e)
    {
        if (!TryGetRange(out var from, out var to)) return;
        var effFrom = _selectedDay.HasValue ? _selectedDay.Value.Date : from;
        var effTo = _selectedDay.HasValue ? _selectedDay.Value.Date.AddDays(1).AddSeconds(-1) : to;
        long effFromTs = new DateTimeOffset(effFrom).ToUnixTimeSeconds();
        long effToTs = new DateTimeOffset(effTo).ToUnixTimeSeconds();
        string? activeAgent = EffectiveAgentFilter;
        string q = _searchQuery;

        var rows = await Task.Run(() => UsageStorage.GetRecentEvents(effFromTs, effToTs, 5000, activeAgent));
        if (!string.IsNullOrEmpty(q))
        {
            rows = rows.Where(r =>
                r.ProviderName.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                r.Model.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                r.Agent.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                AgentName(r.Agent).Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        var dlg = new SaveFileDialog
        {
            Title = I18nService.T("Usage.ExportCsvBtn"),
            Filter = "CSV (*.csv)|*.csv|JSON (*.json)|*.json",
            FileName = $"APISwitch-usage-{DateTime.Now:yyyyMMdd-HHmm}.csv",
            DefaultExt = ".csv",
        };
        if (dlg.ShowDialog(this) != true) return;

        bool exportJson = dlg.FilterIndex == 2 || dlg.FileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
        if (exportJson)
        {
            long sumIn = 0, sumOut = 0, sumCr = 0, sumCw = 0;
            double sumCost = 0;
            foreach (var r in rows)
            {
                sumIn += r.InTokens;
                sumOut += r.OutTokens;
                sumCr += r.CacheReadTokens;
                sumCw += r.CacheWriteTokens;
                string effProv = !string.IsNullOrWhiteSpace(r.ProviderId) ? r.ProviderId : r.ProviderName;
                sumCost += UsagePricingService.EstimateCostUsd(
                    r.Model, effProv, r.Agent, r.InTokens, r.OutTokens, r.CacheReadTokens, r.CacheWriteTokens);
            }

            var payload = new
            {
                exportedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                range = new
                {
                    from = effFrom.ToString("yyyy-MM-dd HH:mm:ss"),
                    to = effTo.ToString("yyyy-MM-dd HH:mm:ss"),
                    clientFilter = string.IsNullOrEmpty(activeAgent) ? "all" : activeAgent,
                    searchQuery = q
                },
                summary = new
                {
                    requests = rows.Count,
                    totalTokens = sumIn + sumOut,
                    inputTokens = sumIn,
                    outputTokens = sumOut,
                    cacheReadTokens = sumCr,
                    cacheWriteTokens = sumCw,
                    cacheHitRate = FormatCacheHitRate(sumIn, sumCr, sumCw),
                    estimatedCostUsd = Math.Round(sumCost, 4)
                },
                records = rows.Select(r => new
                {
                    time = r.Ts.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                    client = AgentName(r.Agent),
                    agentId = r.Agent,
                    provider = r.ProviderName,
                    model = r.Model,
                    inputTokens = r.InTokens,
                    outputTokens = r.OutTokens,
                    totalTokens = r.InTokens + r.OutTokens,
                    cacheReadTokens = r.CacheReadTokens,
                    cacheWriteTokens = r.CacheWriteTokens,
                    cacheHitRate = FormatCacheHitRate(r.InTokens, r.CacheReadTokens, r.CacheWriteTokens),
                    estimatedCostUsd = Math.Round(UsagePricingService.EstimateCostUsd(
                        r.Model, !string.IsNullOrWhiteSpace(r.ProviderId) ? r.ProviderId : r.ProviderName,
                        r.Agent, r.InTokens, r.OutTokens, r.CacheReadTokens, r.CacheWriteTokens), 6),
                    durationMs = r.DurationMs,
                    ttftMs = r.TtftMs
                })
            };

            var jsonOpts = new System.Text.Json.JsonSerializerOptions { WriteIndented = true };
            var jsonStr = System.Text.Json.JsonSerializer.Serialize(payload, jsonOpts);
            await File.WriteAllTextAsync(dlg.FileName, jsonStr, new UTF8Encoding(false));
        }
        else
        {
            var sb = new StringBuilder();
            sb.AppendLine("Time,Client,Provider,Model,InputTokens,OutputTokens,TotalTokens,EstCostUSD,CacheReadTokens,CacheWriteTokens,CacheHitRate,DurationMs,TtftMs");
            foreach (var r in rows)
            {
                var dt = r.Ts.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
                long tot = r.InTokens + r.OutTokens;
                string hit = FormatCacheHitRate(r.InTokens, r.CacheReadTokens, r.CacheWriteTokens);
                string effProv = !string.IsNullOrWhiteSpace(r.ProviderId) ? r.ProviderId : r.ProviderName;
                double estUsd = UsagePricingService.EstimateCostUsd(
                    r.Model, effProv, r.Agent, r.InTokens, r.OutTokens, r.CacheReadTokens, r.CacheWriteTokens);
                sb.AppendLine(string.Join(",",
                    CsvEscape(dt),
                    CsvEscape(AgentName(r.Agent)),
                    CsvEscape(r.ProviderName),
                    CsvEscape(r.Model),
                    r.InTokens.ToString(CultureInfo.InvariantCulture),
                    r.OutTokens.ToString(CultureInfo.InvariantCulture),
                    tot.ToString(CultureInfo.InvariantCulture),
                    estUsd.ToString("0.000000", CultureInfo.InvariantCulture),
                    r.CacheReadTokens.ToString(CultureInfo.InvariantCulture),
                    r.CacheWriteTokens.ToString(CultureInfo.InvariantCulture),
                    CsvEscape(hit),
                    r.DurationMs.ToString(CultureInfo.InvariantCulture),
                    r.TtftMs.ToString(CultureInfo.InvariantCulture)));
            }

            await File.WriteAllTextAsync(dlg.FileName, sb.ToString(), new UTF8Encoding(true));
        }

        FooterStatusText.Text = I18nService.F("Usage.ExportCsvDoneFmt", rows.Count, System.IO.Path.GetFileName(dlg.FileName));
    }

    static string CsvEscape(string? val)
    {
        var s = val ?? "";
        if (s.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0)
            return "\"" + s.Replace("\"", "\"\"") + "\"";
        return s;
    }

    async void OnBackfillClick(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(I18nService.T("Usage.BackfillConfirmMsg"),
                I18nService.T("Usage.BackfillConfirmTitle"),
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        if (BackfillBtn != null) BackfillBtn.IsEnabled = false;
        try
        {
            var result = await Task.Run(() => UsageScanner.BackfillAll());
            var (_, withCache, withTiming) = await Task.Run(() => UsageStorage.CountPerfRows());
            RefreshData();
            MessageBox.Show(I18nService.F("Usage.BackfillDoneFmt", result.Written, withCache, withTiming),
                I18nService.T("Usage.BackfillBtn"), MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(I18nService.F("Usage.BackfillFailFmt", ex.Message),
                I18nService.T("Usage.BackfillBtn"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            if (BackfillBtn != null) BackfillBtn.IsEnabled = true;
        }
    }

    void OnHelpClick(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(I18nService.T("Usage.Note"), I18nService.T("Usage.Title"),
            MessageBoxButton.OK, MessageBoxImage.Information);
    }

    void OnToggleMaximize(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    void OnClose(object sender, RoutedEventArgs e) => Close();

    void Header_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            OnToggleMaximize(sender, e);
            return;
        }
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }

    // ---------- 模型名归一化与格式化工具 ----------

    static readonly string[] ModelStripSuffixes = { "-orchestrator", "-minimal", "-xhigh", "-medium", "-latest", "-trial", "-high", "-free", "-low" };

    static string CleanModelName(string raw)
    {
        var m = (raw ?? "").Trim();
        if (m.Length == 0) return "";
        int slash = m.LastIndexOf('/');
        if (slash >= 0) m = m[(slash + 1)..].Trim();
        int colon = m.IndexOf(':');
        if (colon > 0) m = m[..colon].Trim();
        if (m.EndsWith("[1m]", StringComparison.OrdinalIgnoreCase)) m = m[..^4].Trim();
        var date = Regex.Match(m, @"-\d{4,8}$");
        if (date.Success) m = m[..date.Index];
        foreach (var s in ModelStripSuffixes)
            if (m.EndsWith(s, StringComparison.OrdinalIgnoreCase)) { m = m[..^s.Length]; break; }
        return m;
    }

    static string NormalizeModelKey(string raw) => CleanModelName(raw).ToLowerInvariant();

    static string ResolveDeploymentName(string key, IEnumerable<string> allKeys)
    {
        if (key.Length == 0 || !(key.Contains("--") || key.Contains('.'))) return key;
        foreach (var shortKey in allKeys.Where(k => k.Length > 0 && k.Length < key.Length)
                     .OrderByDescending(k => k.Length))
        {
            int idx = key.IndexOf(shortKey, StringComparison.Ordinal);
            while (idx >= 0)
            {
                int after = idx + shortKey.Length;
                if (after == key.Length || key[after] == '.' || key.AsSpan(after).StartsWith("-server"))
                    return shortKey;
                idx = key.IndexOf(shortKey, idx + 1, StringComparison.Ordinal);
            }
        }
        return key;
    }

    static string AgentName(string agent) => agent switch
    {
        UsageStorage.AgentCodex => I18nService.T("Usage.AgentCodex"),
        UsageStorage.AgentClaudeCli => I18nService.T("Usage.AgentClaudeCli"),
        UsageStorage.AgentClaudeDesktop => I18nService.T("Usage.AgentClaudeDesktop"),
        UsageStorage.AgentOpenCode => I18nService.T("Usage.AgentOpenCode"),
        UsageStorage.AgentPi => I18nService.T("Usage.AgentPi"),
        UsageStorage.AgentStepCode => I18nService.T("Usage.AgentStepCode"),
        UsageStorage.AgentZCode => I18nService.T("Usage.AgentZCode"),
        UsageStorage.AgentDsh => I18nService.T("Usage.AgentDsh"),
        UsageStorage.AgentGeminiCli => I18nService.T("Usage.AgentGeminiCli"),
        UsageStorage.AgentAntigravity => I18nService.T("Usage.AgentAntigravity"),
        _ => agent,
    };

    static (Brush fg, Brush bg) AgentColors(string agent) => agent switch
    {
        UsageStorage.AgentCodex => (new SolidColorBrush(Color.FromRgb(0x05, 0x96, 0x69)),
                                     new SolidColorBrush(Color.FromRgb(0xDC, 0xFC, 0xE7))),
        UsageStorage.AgentClaudeCli or UsageStorage.AgentClaudeDesktop =>
                                    (new SolidColorBrush(Color.FromRgb(0xB4, 0x53, 0x09)),
                                     new SolidColorBrush(Color.FromRgb(0xFE, 0xF3, 0xC7))),
        UsageStorage.AgentOpenCode => (new SolidColorBrush(Color.FromRgb(0x4F, 0x46, 0xE5)),
                                       new SolidColorBrush(Color.FromRgb(0xEE, 0xF2, 0xFF))),
        UsageStorage.AgentPi => (new SolidColorBrush(Color.FromRgb(0x7C, 0x3A, 0xED)),
                                 new SolidColorBrush(Color.FromRgb(0xED, 0xE9, 0xFE))),
        UsageStorage.AgentStepCode => (new SolidColorBrush(Color.FromRgb(0x0E, 0x74, 0x90)),
                                       new SolidColorBrush(Color.FromRgb(0xE0, 0xF2, 0xFE))),
        UsageStorage.AgentZCode => (new SolidColorBrush(Color.FromRgb(0xC2, 0x41, 0x0C)),
                                    new SolidColorBrush(Color.FromRgb(0xFF, 0xED, 0xD5))),
        UsageStorage.AgentDsh => (new SolidColorBrush(Color.FromRgb(0x15, 0x81, 0x4C)),
                                  new SolidColorBrush(Color.FromRgb(0xDC, 0xFC, 0xE7))),
        UsageStorage.AgentGeminiCli => (new SolidColorBrush(Color.FromRgb(0x1A, 0x73, 0xE8)),
                                        new SolidColorBrush(Color.FromRgb(0xE8, 0xF0, 0xFE))),
        UsageStorage.AgentAntigravity => (new SolidColorBrush(Color.FromRgb(0x4F, 0x46, 0xE5)),
                                          new SolidColorBrush(Color.FromRgb(0xE0, 0xE7, 0xFF))),
        _ => (new SolidColorBrush(Color.FromRgb(0x47, 0x55, 0x69)),
              new SolidColorBrush(Color.FromRgb(0xF1, 0xF5, 0xF9))),
    };

    internal static string FormatCacheHitRate(long inTokens, long cacheRead, long cacheWrite)
    {
        if (cacheRead <= 0) return "—";
        long denom = inTokens >= cacheRead
            ? inTokens + cacheWrite
            : inTokens + cacheRead + cacheWrite;
        if (denom <= 0) return "—";
        return (cacheRead * 100.0 / denom).ToString("0.#", CultureInfo.CurrentCulture) + "%";
    }

    static string FormatTokens(long total)
    {
        if (total >= 1_000_000_000) return $"{total / 1_000_000_000.0:F2}B";
        if (total >= 1_000_000) return $"{total / 1_000_000.0:F2}M";
        if (total >= 1_000) return $"{total / 1_000.0:F1}K";
        return total.ToString("N0", CultureInfo.CurrentCulture);
    }

    // ---------- ViewModel ----------

    public class ClientFilterItemVM : INotifyPropertyChanged
    {
        bool _isSelected;
        double _pillOpacity = 1.0;
        string _tooltipText = "";

        public string Agent { get; init; } = "";
        public string Label { get; init; } = "";
        public Geometry? IconGeometry { get; init; }
        public Brush? IconBrush { get; init; }
        public ImageSource? ImageSource { get; init; }
        public Visibility ImageVisibility => ImageSource != null ? Visibility.Visible : Visibility.Collapsed;
        public Visibility PathVisibility => IconGeometry != null ? Visibility.Visible : Visibility.Collapsed;

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected == value) return;
                _isSelected = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
            }
        }

        public double PillOpacity
        {
            get => _pillOpacity;
            set
            {
                if (Math.Abs(_pillOpacity - value) < 0.001) return;
                _pillOpacity = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PillOpacity)));
            }
        }

        public string TooltipText
        {
            get => _tooltipText;
            set
            {
                if (_tooltipText == value) return;
                _tooltipText = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TooltipText)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    public class ProviderGroupVM : INotifyPropertyChanged
    {
        bool _expanded;

        public string Name { get; set; } = "";
        public long InTokens { get; set; }
        public long OutTokens { get; set; }
        public long Requests { get; set; }
        public long Total { get; set; }
        public long CacheReadTokens { get; set; }
        public long CacheWriteTokens { get; set; }
        public double CostUsd { get; set; }
        public double ShareOfAll { get; set; }
        public List<ModelRowVM> Models { get; set; } = new();

        public bool Expanded
        {
            get => _expanded;
            set
            {
                if (_expanded == value) return;
                _expanded = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Expanded)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ChevronAngle)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ModelsVisibility)));
            }
        }

        public double ChevronAngle => Expanded ? 90 : 0;
        public Visibility ModelsVisibility => Expanded ? Visibility.Visible : Visibility.Collapsed;

        public double BarWidth => Math.Clamp(BarTrackWidth * ShareOfAll / 100.0, 0, BarTrackWidth);
        public double AgBarWidth => Math.Clamp(108.0 * ShareOfAll / 100.0, 0, 108.0);
        public string ShareText => $"{ShareOfAll:F1}%";
        public string ReqNumText => Requests.ToString("N0", CultureInfo.CurrentCulture);
        public string InOutText => $"{FormatTokens(InTokens)} / {FormatTokens(OutTokens)}";
        public string TotalText => FormatTokens(Total);
        public string CostText => UsagePricingService.FormatUsd(CostUsd);
        public string ModelCountText => I18nService.F("Usage.ModelsFmt", Models.Count);
        public string CacheText => FormatCacheHitRate(InTokens, CacheReadTokens, CacheWriteTokens);

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    public class AgAccountGroupVM : INotifyPropertyChanged
    {
        bool _expanded;

        public string AccountKey { get; set; } = "";
        public string Email { get; set; } = "";
        public bool IsUnattributed { get; set; }
        public string DisplayName { get; set; } = "";
        public string ShortLabel { get; set; } = "";
        public string TierText { get; set; } = "";
        public Brush TierFg { get; set; } = Brushes.SlateGray;
        public Brush TierBg { get; set; } = Brushes.WhiteSmoke;
        public bool IsCurrentAccount { get; set; }
        public Brush AccentBrush { get; set; } = Brushes.RoyalBlue;
        public Brush BadgeBg { get; set; } = Brushes.AliceBlue;
        public Brush BadgeBorder { get; set; } = Brushes.LightSteelBlue;
        public Brush TitleBrush { get; set; } = Brushes.Black;

        public long InTokens { get; set; }
        public long OutTokens { get; set; }
        public long Requests { get; set; }
        public long Total { get; set; }
        public long CacheReadTokens { get; set; }
        public long CacheWriteTokens { get; set; }
        public double CostUsd { get; set; }
        public double ShareOfAll { get; set; }
        public List<ModelRowVM> Models { get; set; } = new();

        public Visibility TierVisibility => string.IsNullOrEmpty(TierText) ? Visibility.Collapsed : Visibility.Visible;
        public Visibility CurrentBadgeVisibility => IsCurrentAccount ? Visibility.Visible : Visibility.Collapsed;
        public Visibility DetailBtnVisibility => IsUnattributed ? Visibility.Collapsed : Visibility.Visible;

        public bool Expanded
        {
            get => _expanded;
            set
            {
                if (_expanded == value) return;
                _expanded = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Expanded)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ChevronAngle)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ModelsVisibility)));
            }
        }

        public double ChevronAngle => Expanded ? 90 : 0;
        public Visibility ModelsVisibility => Expanded ? Visibility.Visible : Visibility.Collapsed;

        public double BarWidth => Math.Clamp(BarTrackWidth * ShareOfAll / 100.0, 0, BarTrackWidth);
        public double AgBarWidth => Math.Clamp(108.0 * ShareOfAll / 100.0, 0, 108.0);
        public string ShareText => $"{ShareOfAll:F1}%";
        public string ReqNumText => Requests.ToString("N0", CultureInfo.CurrentCulture);
        public string InOutText => $"{FormatTokens(InTokens)} / {FormatTokens(OutTokens)}";
        public string TotalText => FormatTokens(Total);
        public string CostText => UsagePricingService.FormatUsd(CostUsd);
        public string ModelCountText => I18nService.F("Usage.ModelsFmt", Models.Count);
        public string CacheText => FormatCacheHitRate(InTokens, CacheReadTokens, CacheWriteTokens);

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    public sealed class ModelHoverCardVM
    {
        public string ModelTitle { get; init; } = "";
        public string RawModelSubtitle { get; init; } = "";
        public Visibility RawSubtitleVisibility => string.IsNullOrEmpty(RawModelSubtitle) ? Visibility.Collapsed : Visibility.Visible;
        public string ProviderDisplay { get; init; } = "—";
        public string SourceBadgeText { get; init; } = "";
        public Brush SourceBadgeFg { get; init; } = Brushes.SlateGray;
        public Brush SourceBadgeBg { get; init; } = Brushes.WhiteSmoke;
        public Brush SourceBadgeBorder { get; init; } = Brushes.LightGray;
        public string InputRateText { get; init; } = "—";
        public string OutputRateText { get; init; } = "—";
        public string CacheReadRateText { get; init; } = "—";
        public string CacheWriteRateText { get; init; } = "—";
        public string RowBreakdownText { get; init; } = "";
        public string RowCostSummaryText { get; init; } = "";
    }

    static string FormatUnitRatePerMillion(double rate)
    {
        if (rate <= 0.0000001) return "$0.00";
        if (rate < 0.01) return "$" + rate.ToString("0.0000", CultureInfo.InvariantCulture);
        if (rate < 1.0 && Math.Abs(rate * 100 - Math.Round(rate * 100)) > 0.001)
            return "$" + rate.ToString("0.000", CultureInfo.InvariantCulture).TrimEnd('0').TrimEnd('.');
        return "$" + rate.ToString("0.00", CultureInfo.InvariantCulture);
    }

    static ModelHoverCardVM BuildModelHoverCard(
        string displayModel,
        string? rawModelId,
        string? providerKey,
        string? providerDisplay,
        string? agentKey,
        long inTokens,
        long outTokens,
        long cacheReadTokens,
        long cacheWriteTokens,
        double costUsd)
    {
        string lookupModel = !string.IsNullOrWhiteSpace(rawModelId) && rawModelId != "—"
            ? rawModelId
            : displayModel;
        var detail = UsagePricingService.ResolveRateDetailed(lookupModel, providerKey, agentKey);
        var r = detail.Rate;

        (string srcText, Brush fg, Brush bg, Brush bd) = detail.SourceKind switch
        {
            "override_exact" => (I18nService.T("Usage.TipSrcExact"), MakeFrozenBrush(0x43, 0x38, 0xCA), MakeFrozenBrush(0xEE, 0xF2, 0xFF), MakeFrozenBrush(0xC7, 0xD2, 0xFE)),
            "override_global" => (I18nService.T("Usage.TipSrcGlobal"), MakeFrozenBrush(0x43, 0x38, 0xCA), MakeFrozenBrush(0xEE, 0xF2, 0xFF), MakeFrozenBrush(0xC7, 0xD2, 0xFE)),
            "ag_ledger" => (I18nService.T("Usage.TipSrcLedger"), MakeFrozenBrush(0x04, 0x78, 0x57), MakeFrozenBrush(0xEC, 0xFD, 0xF5), MakeFrozenBrush(0xA7, 0xF3, 0xD0)),
            "cloud_sync" => (I18nService.T("Usage.TipSrcCloud"), MakeFrozenBrush(0x0F, 0x76, 0x6E), MakeFrozenBrush(0xF0, 0xFD, 0xFA), MakeFrozenBrush(0x99, 0xF6, 0xE4)),
            _ => (I18nService.T("Usage.TipSrcBuiltin"), MakeFrozenBrush(0x47, 0x55, 0x69), MakeFrozenBrush(0xF1, 0xF5, 0xF9), MakeFrozenBrush(0xE2, 0xE8, 0xF0)),
        };

        if (detail.MultiplierApplied.HasValue)
        {
            srcText += I18nService.F("Usage.TipMultFmt", detail.MultiplierApplied.Value.ToString("0.##", CultureInfo.InvariantCulture));
        }

        string subtitle = (!string.IsNullOrWhiteSpace(rawModelId) &&
                           !string.Equals(rawModelId, displayModel, StringComparison.OrdinalIgnoreCase))
            ? rawModelId
            : "";

        string provText = string.IsNullOrWhiteSpace(providerDisplay) ? "—" : providerDisplay;
        if (!string.IsNullOrWhiteSpace(agentKey))
        {
            string aName = AgentName(agentKey);
            if (!string.IsNullOrEmpty(aName) &&
                !provText.Contains(aName, StringComparison.OrdinalIgnoreCase))
            {
                provText = $"{provText} ({aName})";
            }
        }

        long cr = Math.Max(0, cacheReadTokens);
        long cw = Math.Max(0, cacheWriteTokens);
        long freshIn = inTokens >= cr + cw ? inTokens - cr - cw : (inTokens >= cr ? inTokens - cr : Math.Max(0, inTokens));

        return new ModelHoverCardVM
        {
            ModelTitle = string.IsNullOrWhiteSpace(displayModel) ? "—" : displayModel,
            RawModelSubtitle = subtitle,
            ProviderDisplay = provText,
            SourceBadgeText = srcText,
            SourceBadgeFg = fg,
            SourceBadgeBg = bg,
            SourceBadgeBorder = bd,
            InputRateText = FormatUnitRatePerMillion(r.InputPerMillion),
            OutputRateText = FormatUnitRatePerMillion(r.OutputPerMillion),
            CacheReadRateText = r.CacheReadPerMillion > 0.0000001 ? FormatUnitRatePerMillion(r.CacheReadPerMillion) : "—",
            CacheWriteRateText = r.CacheWritePerMillion > 0.0000001 ? FormatUnitRatePerMillion(r.CacheWritePerMillion) : "—",
            RowBreakdownText = I18nService.F("Usage.TipRowFootFmt", FormatTokens(freshIn), FormatTokens(cr), FormatTokens(outTokens)),
            RowCostSummaryText = I18nService.F("Usage.TipRowCostFmt", UsagePricingService.FormatUsd(costUsd, compactSubCent: false)),
        };
    }

    internal void ShowRenderPreviewTooltipOverlay()
    {
        var sampleCard = _filteredModels.FirstOrDefault()?.HoverCard
                         ?? _filteredProviders.FirstOrDefault()?.Models.FirstOrDefault()?.HoverCard;
        if (sampleCard == null || Content is not Border rootBorder || rootBorder.Child is not Grid rootGrid) return;
        if (TryFindResource("ModelHoverTipStyle") is not Style tipStyle) return;

        DataTemplate? contentTpl = null;
        foreach (var s in tipStyle.Setters.OfType<Setter>())
        {
            if (s.Property == ContentControl.ContentTemplateProperty && s.Value is DataTemplate dt)
            {
                contentTpl = dt;
                break;
            }
        }
        if (contentTpl == null) return;

        var cardPreview = new Border
        {
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            VerticalAlignment = System.Windows.VerticalAlignment.Top,
            Margin = new Thickness(56, 422, 0, 0),
            Background = Brushes.White,
            BorderBrush = MakeFrozenBrush(0xCB, 0xD5, 0xE1),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(13, 11, 13, 11),
            MinWidth = 320,
            MaxWidth = 430,
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = Color.FromRgb(0x0F, 0x17, 0x2A),
                BlurRadius = 18,
                ShadowDepth = 5,
                Opacity = 0.16
            },
            Child = new ContentPresenter
            {
                Content = sampleCard,
                ContentTemplate = contentTpl
            }
        };
        Grid.SetRow(cardPreview, 1);
        System.Windows.Controls.Panel.SetZIndex(cardPreview, 999);
        rootGrid.Children.Add(cardPreview);
    }

    public class ModelRowVM
    {
        ModelHoverCardVM? _hoverCard;

        public string Name { get; set; } = "";
        public string RawModel { get; set; } = "";
        public string ProviderKey { get; set; } = "";
        public string ProviderLabel { get; set; } = "";
        public string AgentKey { get; set; } = "";
        public long InTokens { get; set; }
        public long OutTokens { get; set; }
        public long Requests { get; set; }
        public long Total { get; set; }
        public long CacheReadTokens { get; set; }
        public long CacheWriteTokens { get; set; }
        public double CostUsd { get; set; }
        public double ShareOfProvider { get; set; }

        public ModelHoverCardVM HoverCard => _hoverCard ??= BuildModelHoverCard(
            Name, RawModel, ProviderKey, ProviderLabel, AgentKey,
            InTokens, OutTokens, CacheReadTokens, CacheWriteTokens, CostUsd);

        public double BarWidth => Math.Clamp(BarTrackWidth * ShareOfProvider / 100.0, 0, BarTrackWidth);
        public string ShareText => $"{ShareOfProvider:F1}%";
        public string ReqNumText => Requests.ToString("N0", CultureInfo.CurrentCulture);
        public string InOutText => $"{FormatTokens(InTokens)} / {FormatTokens(OutTokens)}";
        public string TotalText => FormatTokens(Total);
        public string CostText => UsagePricingService.FormatUsd(CostUsd);
        public string CacheText => FormatCacheHitRate(InTokens, CacheReadTokens, CacheWriteTokens);
    }

    public class ModelGroupVM : INotifyPropertyChanged
    {
        bool _expanded;
        ModelHoverCardVM? _hoverCard;

        public string Name { get; set; } = "";
        public long InTokens { get; set; }
        public long OutTokens { get; set; }
        public long Requests { get; set; }
        public long Total { get; set; }
        public long CacheReadTokens { get; set; }
        public long CacheWriteTokens { get; set; }
        public double CostUsd { get; set; }
        public double ShareOfAll { get; set; }
        public List<ModelProviderRowVM> Providers { get; set; } = new();

        public ModelHoverCardVM HoverCard => _hoverCard ??= BuildGroupCard();

        ModelHoverCardVM BuildGroupCard()
        {
            var top = Providers.FirstOrDefault();
            var distinctProvs = Providers
                .Select(p => p.NameText)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            string provLabel = distinctProvs.Count switch
            {
                0 => "—",
                1 => distinctProvs[0],
                2 => $"{distinctProvs[0]} · {distinctProvs[1]}",
                _ => I18nService.F("Usage.TipMultiProvFmt", distinctProvs[0], distinctProvs.Count)
            };
            string? rawSample = top != null && !string.IsNullOrWhiteSpace(top.RawModel) ? top.RawModel : Name;
            string? agentSample = distinctProvs.Count == 1 ? top?.AgentKey : null;
            return BuildModelHoverCard(
                Name, rawSample, top?.Provider, provLabel, agentSample,
                InTokens, OutTokens, CacheReadTokens, CacheWriteTokens, CostUsd);
        }

        public bool Expanded
        {
            get => _expanded;
            set
            {
                if (_expanded == value) return;
                _expanded = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Expanded)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ChevronAngle)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ProvidersVisibility)));
            }
        }

        public double ChevronAngle => Expanded ? 90 : 0;
        public Visibility ProvidersVisibility => Expanded ? Visibility.Visible : Visibility.Collapsed;

        public double BarWidth => Math.Clamp(BarTrackWidth * ShareOfAll / 100.0, 0, BarTrackWidth);
        public double AgBarWidth => Math.Clamp(108.0 * ShareOfAll / 100.0, 0, 108.0);
        public string ShareText => $"{ShareOfAll:F1}%";
        public string ReqNumText => Requests.ToString("N0", CultureInfo.CurrentCulture);
        public string InOutText => $"{FormatTokens(InTokens)} / {FormatTokens(OutTokens)}";
        public string TotalText => FormatTokens(Total);
        public string CostText => UsagePricingService.FormatUsd(CostUsd);
        public string ProviderCountText => I18nService.F("Usage.ProvidersFmt", Providers.Count);
        public string CacheText => FormatCacheHitRate(InTokens, CacheReadTokens, CacheWriteTokens);

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    public class ModelProviderRowVM
    {
        ModelHoverCardVM? _hoverCard;

        public string Provider { get; set; } = "";
        public string RawModel { get; set; } = "";
        public string GroupName { get; set; } = "";
        public string AgentKey { get; set; } = "";
        public long InTokens { get; set; }
        public long OutTokens { get; set; }
        public long Requests { get; set; }
        public long Total { get; set; }
        public long CacheReadTokens { get; set; }
        public long CacheWriteTokens { get; set; }
        public double CostUsd { get; set; }
        public double ShareOfModel { get; set; }

        public ModelHoverCardVM HoverCard => _hoverCard ??= BuildModelHoverCard(
            string.IsNullOrWhiteSpace(GroupName) ? RawModel : GroupName,
            RawModel, Provider, NameText, AgentKey,
            InTokens, OutTokens, CacheReadTokens, CacheWriteTokens, CostUsd);

        public string NameText => string.IsNullOrEmpty(Provider) ? I18nService.T("Usage.UnknownModel") : Provider;
        public string RawModelText => string.Equals(RawModel, GroupName, StringComparison.OrdinalIgnoreCase)
            ? ""
            : I18nService.F("Usage.RawModelFmt", RawModel);
        public double BarWidth => Math.Clamp(BarTrackWidth * ShareOfModel / 100.0, 0, BarTrackWidth);
        public string ShareText => $"{ShareOfModel:F1}%";
        public string ReqNumText => Requests.ToString("N0", CultureInfo.CurrentCulture);
        public string InOutText => $"{FormatTokens(InTokens)} / {FormatTokens(OutTokens)}";
        public string TotalText => FormatTokens(Total);
        public string CostText => UsagePricingService.FormatUsd(CostUsd);
        public string CacheText => FormatCacheHitRate(InTokens, CacheReadTokens, CacheWriteTokens);
    }

    public class AgentRowVM
    {
        public string Name { get; set; } = "";
        public string Agent { get; set; } = "";
        public long InTokens { get; set; }
        public long OutTokens { get; set; }
        public long Requests { get; set; }
        public long Total { get; set; }
        public long CacheReadTokens { get; set; }
        public long CacheWriteTokens { get; set; }
        public double CostUsd { get; set; }
        public double ShareOfAll { get; set; }

        public Brush AgentBrush => AgentColors(Agent).fg;
        public double BarWidth => Math.Clamp(BarTrackWidth * ShareOfAll / 100.0, 0, BarTrackWidth);
        public double AgBarWidth => Math.Clamp(108.0 * ShareOfAll / 100.0, 0, 108.0);
        public string ShareText => $"{ShareOfAll:F1}%";
        public string ReqNumText => Requests.ToString("N0", CultureInfo.CurrentCulture);
        public string InOutText => $"{FormatTokens(InTokens)} / {FormatTokens(OutTokens)}";
        public string CacheText => FormatCacheHitRate(InTokens, CacheReadTokens, CacheWriteTokens);
        public string CostText => UsagePricingService.FormatUsd(CostUsd);
        public string TotalText => FormatTokens(Total);
    }

    public class CallRowVM
    {
        ModelHoverCardVM? _hoverCard;

        public string TimeText { get; set; } = "";
        public string Agent { get; set; } = "";
        public string RawProviderKey { get; set; } = "";
        public string Provider { get; set; } = "";
        public string Model { get; set; } = "";
        public long InTokens { get; set; }
        public long OutTokens { get; set; }
        public long Total { get; set; }

        public long CacheReadTokens { get; set; }
        public long CacheWriteTokens { get; set; }
        public double CostUsd { get; set; }
        public long DurationMs { get; set; }
        public long TtftMs { get; set; }

        public ModelHoverCardVM HoverCard => _hoverCard ??= BuildModelHoverCard(
            Model, null, !string.IsNullOrWhiteSpace(RawProviderKey) ? RawProviderKey : Provider,
            Provider, Agent, InTokens, OutTokens, CacheReadTokens, CacheWriteTokens, CostUsd);

        public string AgentName => UsageMonitorWindow.AgentName(Agent);
        public Brush AgentBrush => AgentColors(Agent).fg;
        public Brush AgentBg => AgentColors(Agent).bg;
        public string InText => FormatTokens(InTokens);
        public string OutText => FormatTokens(OutTokens);
        public string TotalText => FormatTokens(Total);
        public string CostText => UsagePricingService.FormatUsd(CostUsd, compactSubCent: false);
        public string CacheText => FormatCacheHitRate(InTokens, CacheReadTokens, CacheWriteTokens);

        public string SpeedText
        {
            get
            {
                if (DurationMs <= 0 || OutTokens <= 0) return "—";
                return (OutTokens * 1000.0 / DurationMs).ToString("0.#", CultureInfo.CurrentCulture) + " t/s";
            }
        }

        public string TtftText
        {
            get
            {
                if (TtftMs <= 0) return "—";
                if (TtftMs < 1000) return TtftMs + " ms";
                return (TtftMs / 1000.0).ToString("0.00", CultureInfo.CurrentCulture) + " s";
            }
        }
    }
}

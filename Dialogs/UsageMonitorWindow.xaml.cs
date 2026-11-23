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
using APISwitch.Services;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
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
    const double BarTrackWidth = 170;

    DateTime? _selectedDay;
    string? _agentFilter;
    string _searchQuery = "";

    List<UsageStorage.DailyTotal> _daily = new();
    List<UsageStorage.AgentTotal> _allAgentTotals = new();
    List<ProviderGroupVM> _allProviderGroups = new();
    List<AgentRowVM> _allAgentRows = new();
    List<ModelGroupVM> _allModelGroups = new();
    List<CallRowVM> _allCalls = new();
    readonly List<ClientFilterItemVM> _clientFilters = new();

    bool _hasUsageData;
    bool _updatingRangeFields;
    bool _isRefreshingTokens;
    bool _refreshPending;
    string _chartMode = "bar"; // "bar" or "heatmap"
    internal Task<int> InitialScanTask { get; private set; } = Task.FromResult(0);

    public UsageMonitorWindow()
    {
        InitializeComponent();
        InitClientFilterBar();
        StateChanged += OnWindowStateChanged;
        InitRangeDefaults();
        Loaded += OnWindowLoaded;
    }

    async void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        RefreshData();
        InitialScanTask = Task.Run(() => UsageScanner.ScanAll());
        int added = await InitialScanTask;
        if (added > 0) RefreshData();
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

    void SyncClientFilterStates(List<UsageStorage.AgentTotal> allAgents)
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

        SyncClientFilterSelection();
        foreach (var item in _clientFilters)
        {
            if (string.IsNullOrEmpty(item.Agent))
            {
                string allTitle = I18nService.T("Usage.FilterAllAgents");
                item.PillOpacity = 1.0;
                item.TooltipText = grandReqs > 0 || grandTokens > 0
                    ? $"{allTitle} · {FormatTokens(grandTokens)} ({grandReqs:N0} reqs)"
                    : allTitle;
                continue;
            }

            totalsByAgent.TryGetValue(item.Agent, out var stat);
            bool hasData = stat.Reqs > 0 || stat.Tokens > 0;
            item.PillOpacity = item.IsSelected || hasData ? 1.0 : 0.62;
            item.TooltipText = hasData
                ? $"{item.Label} · {FormatTokens(stat.Tokens)} ({stat.Reqs:N0} reqs)"
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
        bool active = !string.IsNullOrEmpty(_agentFilter)
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
        string? activeAgent = _agentFilter;

        var expandedProviders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (ProvidersList.ItemsSource is IEnumerable<ProviderGroupVM> oldProvs)
            foreach (var g in oldProvs) if (g.Expanded) expandedProviders.Add(g.Name);

        var expandedModels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (ModelsList.ItemsSource is IEnumerable<ModelGroupVM> oldMods)
            foreach (var g in oldMods) if (g.Expanded) expandedModels.Add(g.Name);

        UsageStorage.UsageSummary summary;
        UsageStorage.PerformanceStats perf;
        List<UsageStorage.DailyTotal> daily;
        List<UsageStorage.ProviderModelTotal> pmRows;
        List<UsageStorage.AgentTotal> allAgentTotals;
        List<UsageStorage.UsageEventRow> calls;

        try
        {
            (summary, perf, daily, pmRows, allAgentTotals, calls) = await Task.Run(() =>
            {
                var s = UsageStorage.GetSummary(effFromTs, effToTs, activeAgent);
                var p = UsageStorage.GetPerformance(effFromTs, effToTs, activeAgent);
                var d = UsageStorage.GetDailyTotals(fromTs, toTs, activeAgent);
                var pm = UsageStorage.GetProviderModelTotals(effFromTs, effToTs, 400, activeAgent);
                var ag = UsageStorage.GetAgentTotals(effFromTs, effToTs);
                var ev = UsageStorage.GetRecentEvents(effFromTs, effToTs, 300, activeAgent);
                return (s, p, d, pm, ag, ev);
            });
        }
        finally
        {
            _isRefreshingTokens = false;
        }

        _daily = daily;
        _allAgentTotals = allAgentTotals;

        SyncClientFilterStates(allAgentTotals);
        UpdateFocusedDayChip();
        UpdateClearFilterButtonVisibility();

        // 标题栏区间摘要胶囊
        if (_selectedDay.HasValue)
            RangeSummaryText.Text = $"{_selectedDay.Value:yyyy-MM-dd} (24h)";
        else
            RangeSummaryText.Text = $"{from:MM-dd HH:mm} → {to:MM-dd HH:mm}";

        // 5 张 KPI 卡主指标 + 次行分析摘要
        long totalTokens = summary.TotalIn + summary.TotalOut;
        TotalTokensText.Text = FormatTokens(totalTokens);
        long activeDays = Math.Max(1, summary.ActiveDays);
        TotalSubText.Text = totalTokens > 0
            ? I18nService.F("Usage.ActiveDaysSubFmt", summary.ActiveDays, FormatTokens(totalTokens / activeDays))
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
            double daySpan = Math.Max(1.0, (effTo - effFrom).TotalDays);
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

        // 客户端分布多色占比条（支持直接点击切换筛选）
        RenderClientMixBar(allAgentTotals);

        // 供应商分组（带下钻模型明细）
        var provGroups = pmRows
            .GroupBy(r => string.IsNullOrEmpty(r.Provider) ? I18nService.T("Usage.UnknownModel") : r.Provider)
            .Select(g =>
            {
                long gIn = g.Sum(m => m.InTokens);
                long gOut = g.Sum(m => m.OutTokens);
                long gReq = g.Sum(m => m.Requests);
                long gTotal = gIn + gOut;
                long gCacheRead = g.Sum(m => m.CacheRead);
                long gCacheWrite = g.Sum(m => m.CacheWrite);
                var models = g.Select(m =>
                {
                    long mTotal = m.InTokens + m.OutTokens;
                    return new ModelRowVM
                    {
                        Name = string.IsNullOrEmpty(m.Model) ? I18nService.T("Usage.UnknownModel") : m.Model,
                        InTokens = m.InTokens,
                        OutTokens = m.OutTokens,
                        Requests = m.Requests,
                        Total = mTotal,
                        CacheReadTokens = m.CacheRead,
                        CacheWriteTokens = m.CacheWrite,
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
                    CacheReadTokens = gCacheRead,
                    CacheWriteTokens = gCacheWrite,
                    Models = models,
                    Expanded = expandedProviders.Contains(g.Key),
                };
            })
            .OrderByDescending(x => x.Total).ThenByDescending(x => x.Requests)
            .ToList();

        long provGrand = provGroups.Sum(x => x.Total);
        foreach (var pg in provGroups) pg.ShareOfAll = provGrand > 0 ? pg.Total * 100.0 / provGrand : 0;
        _allProviderGroups = provGroups;

        // 模型汇总分组（模型名归一化 + 长部署名归并）
        var rawCleanKeys = pmRows
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

        var modelGroups = pmRows
            .GroupBy(r => ModelGroupKeyOf(r.Model))
            .Select(g =>
            {
                long mgIn = g.Sum(x => x.InTokens);
                long mgOut = g.Sum(x => x.OutTokens);
                long mgReq = g.Sum(x => x.Requests);
                long mgTotal = mgIn + mgOut;
                long mgCacheRead = g.Sum(x => x.CacheRead);
                long mgCacheWrite = g.Sum(x => x.CacheWrite);

                string displayName = string.IsNullOrEmpty(g.Key)
                    ? I18nService.T("Usage.UnknownModel")
                    : g.Select(x => CleanModelName(x.Model))
                        .Where(s => !string.IsNullOrEmpty(s))
                        .OrderBy(s => s.Length)
                        .FirstOrDefault() ?? g.Key;

                var providers = g
                    .GroupBy(x => (
                        Provider: string.IsNullOrEmpty(x.Provider) ? I18nService.T("Usage.UnknownModel") : x.Provider,
                        RawModel: x.Model ?? ""))
                    .Select(p =>
                    {
                        long pIn = p.Sum(x => x.InTokens);
                        long pOut = p.Sum(x => x.OutTokens);
                        long pTotal = pIn + pOut;
                        return new ModelProviderRowVM
                        {
                            Provider = p.Key.Provider,
                            RawModel = p.Key.RawModel,
                            GroupName = displayName,
                            InTokens = pIn,
                            OutTokens = pOut,
                            Requests = p.Sum(x => x.Requests),
                            Total = pTotal,
                            CacheReadTokens = p.Sum(x => x.CacheRead),
                            CacheWriteTokens = p.Sum(x => x.CacheWrite),
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
                    CacheReadTokens = mgCacheRead,
                    CacheWriteTokens = mgCacheWrite,
                    Providers = providers,
                    Expanded = expandedModels.Contains(displayName),
                };
            })
            .OrderByDescending(x => x.Total).ThenByDescending(x => x.Requests)
            .ToList();

        long modelGrand = modelGroups.Sum(x => x.Total);
        foreach (var mg in modelGroups) mg.ShareOfAll = modelGrand > 0 ? mg.Total * 100.0 / modelGrand : 0;
        _allModelGroups = modelGroups;

        // 客户端汇总表（始终展示当前区间的全量客户端对比，若已激活全局客户端筛选则在 AgentsView 也能直观对照）
        long agentGrand = allAgentTotals.Sum(a => a.InTokens + a.OutTokens);
        _allAgentRows = allAgentTotals.Select(a =>
        {
            long t = a.InTokens + a.OutTokens;
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
                ShareOfAll = agentGrand > 0 ? t * 100.0 / agentGrand : 0,
            };
        }).OrderByDescending(x => x.Total).ThenByDescending(x => x.Requests).ToList();

        // 最近调用明细
        _allCalls = calls.Select(c =>
        {
            var dt = c.Ts.ToLocalTime();
            return new CallRowVM
            {
                TimeText = dt.ToString("MM-dd HH:mm:ss"),
                Agent = c.Agent,
                Provider = string.IsNullOrEmpty(c.ProviderName) ? "—" : c.ProviderName,
                Model = string.IsNullOrEmpty(c.Model) ? "—" : c.Model,
                InTokens = c.InTokens,
                OutTokens = c.OutTokens,
                Total = c.InTokens + c.OutTokens,
                CacheReadTokens = c.CacheReadTokens,
                CacheWriteTokens = c.CacheWriteTokens,
                DurationMs = c.DurationMs,
                TtftMs = c.TtftMs,
            };
        }).ToList();

        _hasUsageData = summary.Requests > 0 || provGroups.Count > 0 || _daily.Count > 0;

        ApplySearchFilter();
        UpdateEmptyOverlayVisibility();
        UpdateFooterStatus(summary, perf);

        ScheduleChartRedraw();

        if (_refreshPending)
        {
            _refreshPending = false;
            RefreshData();
        }
    }

    void ApplySearchFilter()
    {
        if (string.IsNullOrEmpty(_searchQuery))
        {
            ProvidersList.ItemsSource = _allProviderGroups;
            AgentsList.ItemsSource = _allAgentRows;
            ModelsList.ItemsSource = _allModelGroups;
            CallsList.ItemsSource = _allCalls;
            return;
        }

        string q = _searchQuery;

        // 1. 供应商面板：匹配供应商名或旗下任意模型名
        var filteredProvs = new List<ProviderGroupVM>();
        foreach (var pg in _allProviderGroups)
        {
            bool provMatch = pg.Name.Contains(q, StringComparison.OrdinalIgnoreCase);
            if (provMatch)
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
                    ShareOfAll = pg.ShareOfAll,
                    Models = subModels,
                    Expanded = true,
                });
            }
        }
        ProvidersList.ItemsSource = filteredProvs;

        // 2. 客户端面板：匹配客户端名或标识
        AgentsList.ItemsSource = _allAgentRows
            .Where(a => a.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                     || a.Agent.Contains(q, StringComparison.OrdinalIgnoreCase))
            .ToList();

        // 3. 模型面板：匹配模型名、供应商名或原始部署串
        var filteredModels = new List<ModelGroupVM>();
        foreach (var mg in _allModelGroups)
        {
            bool modMatch = mg.Name.Contains(q, StringComparison.OrdinalIgnoreCase);
            if (modMatch)
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
                    ShareOfAll = mg.ShareOfAll,
                    Providers = subProvs,
                    Expanded = true,
                });
            }
        }
        ModelsList.ItemsSource = filteredModels;

        // 4. 调用明细面板
        CallsList.ItemsSource = _allCalls
            .Where(c => c.Provider.Contains(q, StringComparison.OrdinalIgnoreCase)
                     || c.Model.Contains(q, StringComparison.OrdinalIgnoreCase)
                     || c.AgentName.Contains(q, StringComparison.OrdinalIgnoreCase)
                     || c.Agent.Contains(q, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    void RenderClientMixBar(List<UsageStorage.AgentTotal> allAgents)
    {
        if (ClientMixBar == null || ClientMixPanel == null) return;
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

    void UpdateFooterStatus(UsageStorage.UsageSummary summary, UsageStorage.PerformanceStats perf)
    {
        if (FooterStatusText == null) return;

        var parts = new List<string>
        {
            I18nService.F("Usage.FooterSummary", summary.Requests.ToString("N0", CultureInfo.CurrentCulture), summary.ActiveDays)
        };
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
        if (!string.IsNullOrEmpty(_agentFilter))
            parts.Add($"@{AgentName(_agentFilter)}");
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
        double h = canvas.ActualHeight > 0 ? canvas.ActualHeight : 106;
        const double topPad = 8, bottomPad = 18, leftPad = 6, rightAxisW = 54;
        double plotW = Math.Max(80, w - leftPad - rightAxisW);
        double plotH = Math.Max(20, h - topPad - bottomPad);

        long maxTotal = _daily.Max(d => d.InTokens + d.OutTokens);
        if (maxTotal <= 0) maxTotal = 1;

        var gridBrush = new SolidColorBrush(Color.FromRgb(0xEB, 0xF0, 0xF7));
        var tickTextBrush = new SolidColorBrush(Color.FromRgb(0x94, 0xA3, 0xB8));

        // 水平参考虚线 + 右对齐清晰 Y 轴数值（避免越界与重叠）
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

        int n = _daily.Count;
        double slot = plotW / n;
        double barW = Math.Clamp(slot * 0.58, 2.5, 30);

        var inBrush = new SolidColorBrush(Color.FromRgb(0xA5, 0xB4, 0xFC));
        var outBrush = new SolidColorBrush(Color.FromRgb(0x4F, 0x46, 0xE5));
        var inSelBrush = new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B));
        var outSelBrush = new SolidColorBrush(Color.FromRgb(0xB4, 0x53, 0x09));
        var selWash = new SolidColorBrush(Color.FromRgb(0xEE, 0xF2, 0xFF));

        for (int i = 0; i < n; i++)
        {
            var d = _daily[i];
            double cx = leftPad + slot * (i + 0.5);
            double x = cx - barW / 2;
            long dayTotal = d.InTokens + d.OutTokens;
            double totalH = dayTotal > 0 ? Math.Clamp(plotH * dayTotal / maxTotal, 2.0, plotH) : 0;
            double outH = d.OutTokens > 0
                ? Math.Min(totalH, Math.Max(1.5, plotH * d.OutTokens / maxTotal))
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
                    RadiusX = outH > 0 ? 0 : 2,
                    RadiusY = outH > 0 ? 0 : 2,
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
                    RadiusX = 2,
                    RadiusY = 2,
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

        // X 轴稀疏日期标注（首 / 中 / 尾）
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
            var dtStr = _daily[idx].Day.Length >= 10 ? _daily[idx].Day[5..] : _daily[idx].Day;
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
        const double leftPad = 24, topPad = 16;

        if (!DateTime.TryParse(_daily[0].Day, out var firstDay)) return;
        if (!DateTime.TryParse(_daily[^1].Day, out var lastDay)) return;

        int startDow = ((int)firstDay.DayOfWeek + 6) % 7;
        var gridStart = firstDay.AddDays(-startDow);
        int endDow = ((int)lastDay.DayOfWeek + 6) % 7;
        var gridEnd = lastDay.AddDays(6 - endDow);

        int totalDays = (int)(gridEnd - gridStart).TotalDays + 1;
        int totalCols = (totalDays + 6) / 7;

        double availW = Math.Max(200, w - leftPad - 10);
        double colPitch = Math.Clamp(availW / Math.Max(totalCols, 1), 8.5, 13.5);
        double maxByH = Math.Floor((h - topPad - 2) / 7.0);
        if (maxByH >= 8.0) colPitch = Math.Min(colPitch, maxByH);
        double cellSize = Math.Max(5.5, Math.Floor(colPitch - 2.0));

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
            double y = topPad + dowRows[r] * colPitch + (cellSize - 10) / 2.0;
            Canvas.SetLeft(tb, 4);
            Canvas.SetTop(tb, Math.Max(topPad, y));
            canvas.Children.Add(tb);
        }

        var map = _daily.ToDictionary(d => d.Day, d => d);
        long maxTotal = _daily.Max(d => d.InTokens + d.OutTokens);
        if (maxTotal <= 0) maxTotal = 1;

        var c0 = new SolidColorBrush(Color.FromRgb(0xF1, 0xF5, 0xF9));
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
            double y = topPad + row * colPitch;

            if (curDate.Month != lastLabeledMonth && (curDate.Day <= 7 || idx == 0))
            {
                if (x - lastMonthX >= 26)
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

            string key = curDate.ToString("yyyy-MM-dd");
            map.TryGetValue(key, out var d);
            long dayTotal = d != null ? (d.InTokens + d.OutTokens) : 0;

            Brush fill = c0;
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
                Width = cellSize,
                Height = cellSize,
                RadiusX = 2.5,
                RadiusY = 2.5,
                Fill = fill,
                Stroke = selected ? selStroke : (dayTotal == 0 ? c0Stroke : null),
                StrokeThickness = selected ? 1.8 : (dayTotal == 0 ? 0.7 : 0),
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
    void OnTabAgentsChecked(object sender, RoutedEventArgs e) => SwitchView(AgentsPanel);
    void OnTabModelsChecked(object sender, RoutedEventArgs e) => SwitchView(ModelsPanel);
    void OnTabCallsChecked(object sender, RoutedEventArgs e) => SwitchView(CallsPanel);

    void SwitchView(FrameworkElement target)
    {
        if (ProvidersPanel == null || AgentsPanel == null || ModelsPanel == null || CallsPanel == null)
            return;

        ProvidersPanel.Visibility = target == ProvidersPanel ? Visibility.Visible : Visibility.Collapsed;
        AgentsPanel.Visibility = target == AgentsPanel ? Visibility.Visible : Visibility.Collapsed;
        ModelsPanel.Visibility = target == ModelsPanel ? Visibility.Visible : Visibility.Collapsed;
        CallsPanel.Visibility = target == CallsPanel ? Visibility.Visible : Visibility.Collapsed;

        UpdateFocusedDayChip();
        UpdateEmptyOverlayVisibility();
        ScheduleChartRedraw();
    }

    void OnToggleProviderExpand(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is ProviderGroupVM g)
            g.Expanded = !g.Expanded;
    }

    void OnToggleModelExpand(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is ModelGroupVM g)
            g.Expanded = !g.Expanded;
    }

    // ---------- 顶部工具操作（刷新 / 导出 CSV / 回填 / 帮助 / 窗口控制） ----------

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
        string? activeAgent = _agentFilter;
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
            Filter = "CSV (*.csv)|*.csv",
            FileName = $"APISwitch-usage-{DateTime.Now:yyyyMMdd-HHmm}.csv",
            DefaultExt = ".csv",
        };
        if (dlg.ShowDialog(this) != true) return;

        var sb = new StringBuilder();
        sb.AppendLine("Time,Client,Provider,Model,InputTokens,OutputTokens,TotalTokens,CacheReadTokens,CacheWriteTokens,CacheHitRate,DurationMs,TtftMs");
        foreach (var r in rows)
        {
            var dt = r.Ts.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
            long tot = r.InTokens + r.OutTokens;
            string hit = FormatCacheHitRate(r.InTokens, r.CacheReadTokens, r.CacheWriteTokens);
            sb.AppendLine(string.Join(",",
                CsvEscape(dt),
                CsvEscape(AgentName(r.Agent)),
                CsvEscape(r.ProviderName),
                CsvEscape(r.Model),
                r.InTokens.ToString(CultureInfo.InvariantCulture),
                r.OutTokens.ToString(CultureInfo.InvariantCulture),
                tot.ToString(CultureInfo.InvariantCulture),
                r.CacheReadTokens.ToString(CultureInfo.InvariantCulture),
                r.CacheWriteTokens.ToString(CultureInfo.InvariantCulture),
                CsvEscape(hit),
                r.DurationMs.ToString(CultureInfo.InvariantCulture),
                r.TtftMs.ToString(CultureInfo.InvariantCulture)));
        }

        await File.WriteAllTextAsync(dlg.FileName, sb.ToString(), new UTF8Encoding(true));
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
        public string ShareText => $"{ShareOfAll:F1}%";
        public string ReqNumText => Requests.ToString("N0", CultureInfo.CurrentCulture);
        public string InOutText => $"{FormatTokens(InTokens)} / {FormatTokens(OutTokens)}";
        public string TotalText => FormatTokens(Total);
        public string ModelCountText => I18nService.F("Usage.ModelsFmt", Models.Count);
        public string CacheText => FormatCacheHitRate(InTokens, CacheReadTokens, CacheWriteTokens);

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    public class ModelRowVM
    {
        public string Name { get; set; } = "";
        public long InTokens { get; set; }
        public long OutTokens { get; set; }
        public long Requests { get; set; }
        public long Total { get; set; }
        public long CacheReadTokens { get; set; }
        public long CacheWriteTokens { get; set; }
        public double ShareOfProvider { get; set; }

        public double BarWidth => Math.Clamp(BarTrackWidth * ShareOfProvider / 100.0, 0, BarTrackWidth);
        public string ShareText => $"{ShareOfProvider:F1}%";
        public string ReqNumText => Requests.ToString("N0", CultureInfo.CurrentCulture);
        public string InOutText => $"{FormatTokens(InTokens)} / {FormatTokens(OutTokens)}";
        public string TotalText => FormatTokens(Total);
        public string CacheText => FormatCacheHitRate(InTokens, CacheReadTokens, CacheWriteTokens);
    }

    public class ModelGroupVM : INotifyPropertyChanged
    {
        bool _expanded;

        public string Name { get; set; } = "";
        public long InTokens { get; set; }
        public long OutTokens { get; set; }
        public long Requests { get; set; }
        public long Total { get; set; }
        public long CacheReadTokens { get; set; }
        public long CacheWriteTokens { get; set; }
        public double ShareOfAll { get; set; }
        public List<ModelProviderRowVM> Providers { get; set; } = new();

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
        public string ShareText => $"{ShareOfAll:F1}%";
        public string ReqNumText => Requests.ToString("N0", CultureInfo.CurrentCulture);
        public string InOutText => $"{FormatTokens(InTokens)} / {FormatTokens(OutTokens)}";
        public string TotalText => FormatTokens(Total);
        public string ProviderCountText => I18nService.F("Usage.ProvidersFmt", Providers.Count);
        public string CacheText => FormatCacheHitRate(InTokens, CacheReadTokens, CacheWriteTokens);

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    public class ModelProviderRowVM
    {
        public string Provider { get; set; } = "";
        public string RawModel { get; set; } = "";
        public string GroupName { get; set; } = "";
        public long InTokens { get; set; }
        public long OutTokens { get; set; }
        public long Requests { get; set; }
        public long Total { get; set; }
        public long CacheReadTokens { get; set; }
        public long CacheWriteTokens { get; set; }
        public double ShareOfModel { get; set; }

        public string NameText => string.IsNullOrEmpty(Provider) ? I18nService.T("Usage.UnknownModel") : Provider;
        public string RawModelText => string.Equals(RawModel, GroupName, StringComparison.OrdinalIgnoreCase)
            ? ""
            : I18nService.F("Usage.RawModelFmt", RawModel);
        public double BarWidth => Math.Clamp(BarTrackWidth * ShareOfModel / 100.0, 0, BarTrackWidth);
        public string ShareText => $"{ShareOfModel:F1}%";
        public string ReqNumText => Requests.ToString("N0", CultureInfo.CurrentCulture);
        public string InOutText => $"{FormatTokens(InTokens)} / {FormatTokens(OutTokens)}";
        public string TotalText => FormatTokens(Total);
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
        public double ShareOfAll { get; set; }

        public Brush AgentBrush => AgentColors(Agent).fg;
        public double BarWidth => Math.Clamp(BarTrackWidth * ShareOfAll / 100.0, 0, BarTrackWidth);
        public string ShareText => $"{ShareOfAll:F1}%";
        public string ReqNumText => Requests.ToString("N0", CultureInfo.CurrentCulture);
        public string InOutText => $"{FormatTokens(InTokens)} / {FormatTokens(OutTokens)}";
        public string CacheText => FormatCacheHitRate(InTokens, CacheReadTokens, CacheWriteTokens);
        public string TotalText => FormatTokens(Total);
    }

    public class CallRowVM
    {
        public string TimeText { get; set; } = "";
        public string Agent { get; set; } = "";
        public string Provider { get; set; } = "";
        public string Model { get; set; } = "";
        public long InTokens { get; set; }
        public long OutTokens { get; set; }
        public long Total { get; set; }

        public long CacheReadTokens { get; set; }
        public long CacheWriteTokens { get; set; }
        public long DurationMs { get; set; }
        public long TtftMs { get; set; }

        public string AgentName => UsageMonitorWindow.AgentName(Agent);
        public Brush AgentBrush => AgentColors(Agent).fg;
        public Brush AgentBg => AgentColors(Agent).bg;
        public string InText => FormatTokens(InTokens);
        public string OutText => FormatTokens(OutTokens);
        public string TotalText => FormatTokens(Total);
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

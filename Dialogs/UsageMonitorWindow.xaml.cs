using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using Cursors = System.Windows.Input.Cursors;
using Brushes = System.Windows.Media.Brushes;
using APISwitch.Services;

namespace APISwitch.Dialogs;

public partial class UsageMonitorWindow : Window
{
    const double BarTrackWidth = 170;

    List<UsageStorage.DailyTotal> _daily = new();
    bool _updatingRangeFields;
    DateTime? _selectedDay;
    int _refreshGen;
    System.Windows.Threading.DispatcherTimer? _chartRedrawTimer;

    public UsageMonitorWindow()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            InitRangeDefaults();
            // 先扫描本地会话数据（应用未运行期间客户端产生的用量也能入库），再刷新
            Task.Run(() => UsageScanner.ScanAll())
                .ContinueWith(_ => Dispatcher.Invoke(RefreshData));
        };
        SizeChanged += (_, _) => ScheduleChartRedraw();
    }

    // ---------- 时间范围 ----------
    void InitRangeDefaults()
    {
        _updatingRangeFields = true;
        var today = DateTime.Now.Date;
        StartPicker.SetValue(today, silent: true);
        EndPicker.SetValue(today.AddMinutes(23 * 60 + 59), silent: true);
        _updatingRangeFields = false;
        Quick1DRadio.IsChecked = true;
    }

    void ApplyQuickRange(int days)
    {
        _updatingRangeFields = true;
        // 近 N 天 = 今天 + 前 N-1 天，整天对齐（0 点 ~ 23:59:59），不按当前时刻截断
        var today = DateTime.Now.Date;
        StartPicker.SetValue(today.AddDays(-(days - 1)), silent: true);
        EndPicker.SetValue(today.AddMinutes(23 * 60 + 59), silent: true);
        _updatingRangeFields = false;
        RefreshData();
    }

    void OnQuickRangeChecked(object sender, RoutedEventArgs e)
    {
        if (_updatingRangeFields) return;
        if (sender is FrameworkElement { Tag: string tag } && int.TryParse(tag, out var days))
            ApplyQuickRange(days);
    }

    /// <summary>用户通过选择组件修改区间 → 取消快速范围选中态（静默，不触发刷新）。</summary>
    void OnRangeFieldEdited(object? sender, EventArgs e)
    {
        if (_updatingRangeFields) return;
        _updatingRangeFields = true;
        Quick1DRadio.IsChecked = false;
        Quick7DRadio.IsChecked = false;
        Quick30DRadio.IsChecked = false;
        _updatingRangeFields = false;
    }

    void OnApplyRange(object sender, RoutedEventArgs e) => RefreshData();

    bool TryGetRange(out DateTime from, out DateTime to)
    {
        from = StartPicker.Value;
        to = EndPicker.Value;
        // 结束时刻为 23:59 时视为当天结束（包含最后一秒的调用）
        if (to.Hour == 23 && to.Minute == 59 && to.Second == 0)
            to = to.AddSeconds(59);
        return to >= from;
    }

    // ---------- 数据刷新 ----------

    async void RefreshData()
    {
        if (!TryGetRange(out var from, out var to))
        {
            EmptyOverlay.Visibility = Visibility.Visible;
            return;
        }

        // 点击选中某天时：汇总卡片与下方明细切换为当天数据；趋势图始终展示完整区间
        if (_selectedDay.HasValue && (_selectedDay.Value < from.Date || _selectedDay.Value > to.Date))
            _selectedDay = null; // 范围变更后选中日已不在区间内，自动取消选择
        var effFrom = _selectedDay?.Date ?? from;
        var effTo = _selectedDay.HasValue ? _selectedDay.Value.Date.AddDays(1).AddSeconds(-1) : to;
        long fromTs = new DateTimeOffset(from).ToUnixTimeSeconds();
        long toTs = new DateTimeOffset(to).ToUnixTimeSeconds();
        long effFromTs = new DateTimeOffset(effFrom).ToUnixTimeSeconds();
        long effToTs = new DateTimeOffset(effTo).ToUnixTimeSeconds();

        // 快速连续刷新（快速点选柱子/切换区间）时，只应用最后一次的结果
        int gen = ++_refreshGen;

        // 所有 SQLite 查询挪到后台线程执行，避免 UI 线程迟滞
        var (s, dailyRows, pmTotals, agentTotals, events) = await Task.Run(() =>
        {
            var summary = UsageStorage.GetSummary(effFromTs, effToTs);
            var daily = UsageStorage.GetDailyTotals(fromTs, toTs);
            var pm = UsageStorage.GetProviderModelTotals(effFromTs, effToTs);
            var ag = UsageStorage.GetAgentTotals(effFromTs, effToTs);
            var evs = UsageStorage.GetRecentEvents(effFromTs, effToTs, limit: 400);
            return (summary, daily, pm, ag, evs);
        }).ConfigureAwait(true);

        if (gen != _refreshGen) return; // 已有更新的刷新在途，丢弃本次结果
        TotalTokensText.Text = FormatTokens(s.TotalIn + s.TotalOut);
        InTokensText.Text = FormatTokens(s.TotalIn);
        OutTokensText.Text = FormatTokens(s.TotalOut);
        RequestsText.Text = s.Requests.ToString("N0", CultureInfo.CurrentCulture);
        RangeSummaryText.Text = $"{effFrom:MM-dd HH:mm} ~ {effTo:MM-dd HH:mm}";

        var hasData = s.Requests > 0 || s.TotalIn + s.TotalOut > 0;
        EmptyOverlay.Visibility = hasData ? Visibility.Collapsed : Visibility.Visible;

        // 每日趋势：补齐区间内没有调用的日期（空柱），便于直观区分活跃/空闲日；
        // 区间过长（>92 天）时不补齐，避免柱形过密不可读
        _daily = dailyRows;
        int spanDays = (to.Date - from.Date).Days + 1;
        if (spanDays > 1 && spanDays <= 92)
        {
            var byDay = _daily.ToDictionary(x => x.Day, StringComparer.Ordinal);
            var filled = new List<UsageStorage.DailyTotal>(spanDays);
            for (var day = from.Date; day <= to.Date; day = day.AddDays(1))
            {
                var key = day.ToString("yyyy-MM-dd");
                filled.Add(byDay.TryGetValue(key, out var v) ? v : new UsageStorage.DailyTotal(key, 0, 0));
            }
            _daily = filled;
        }
        DrawTrendChart();

        // 供应商下钻：provider × model 交叉汇总
        var providerGroups = new List<ProviderGroupVM>();
        foreach (var g in pmTotals
                     .GroupBy(x => x.Provider, StringComparer.OrdinalIgnoreCase))
        {
            var models = g.OrderByDescending(m => m.InTokens + m.OutTokens).ToList();
            long gIn = models.Sum(m => m.InTokens);
            long gOut = models.Sum(m => m.OutTokens);
            long gReq = models.Sum(m => m.Requests);
            long gTotal = gIn + gOut;
            var group = new ProviderGroupVM
            {
                Name = g.Key,
                InTokens = gIn,
                OutTokens = gOut,
                Requests = gReq,
                Total = gTotal,
                Models = models.Select(m =>
                {
                    long mTotal = m.InTokens + m.OutTokens;
                    return new ModelRowVM
                    {
                        Name = string.IsNullOrEmpty(m.Model) ? I18nService.T("Usage.UnknownModel") : m.Model,
                        InTokens = m.InTokens,
                        OutTokens = m.OutTokens,
                        Requests = m.Requests,
                        Total = mTotal,
                        ShareOfProvider = gTotal > 0 ? 100.0 * mTotal / gTotal : 0,
                    };
                }).ToList(),
            };
            group.ShareOfAll = 0; // 由下方统一按全局总量计算
            providerGroups.Add(group);
        }

        long grandTotal = providerGroups.Sum(x => x.Total);
        if (grandTotal > 0)
        {
            foreach (var g in providerGroups) g.ShareOfAll = 100.0 * g.Total / grandTotal;
        }
        ProvidersPanel.ItemsSource = providerGroups;

        // 模型汇总：跨供应商合并同模型（大小写 / 前缀 / 变体后缀 / 日期快照 / 部署名归一化）
        var allModelKeys = new HashSet<string>(pmTotals.Select(x => NormalizeModelKey(x.Model)), StringComparer.Ordinal);
        var modelGroups = new List<ModelGroupVM>();
        foreach (var g in pmTotals
                     .GroupBy(x => string.IsNullOrEmpty(x.Model) ? "" : ResolveDeploymentName(NormalizeModelKey(x.Model), allModelKeys), StringComparer.Ordinal)
                     .OrderByDescending(g => g.Sum(x => x.InTokens + x.OutTokens)))
        {
            // 显示名：优先取用量最大的"干净"原名（非部署长名），清理后保留原大小写
            var rep = g.Where(x => !string.IsNullOrEmpty(x.Model)
                                && !x.Model.Contains("--") && !x.Model.Contains('.'))
                       .OrderByDescending(x => x.InTokens + x.OutTokens)
                       .FirstOrDefault()?.Model
                      ?? g.OrderBy(x => CleanModelName(x.Model).Length)
                           .ThenByDescending(x => x.InTokens + x.OutTokens)
                           .First().Model;
            long mgIn = g.Sum(x => x.InTokens);
            long mgOut = g.Sum(x => x.OutTokens);
            long mgReq = g.Sum(x => x.Requests);
            long mgTotal = mgIn + mgOut;

            var displayName = string.IsNullOrEmpty(rep) ? I18nService.T("Usage.UnknownModel") : CleanModelName(rep);

            var providerRows = g
                .GroupBy(x => x.Provider, StringComparer.OrdinalIgnoreCase)
                .Select(p =>
                {
                    long pIn = p.Sum(x => x.InTokens);
                    long pOut = p.Sum(x => x.OutTokens);
                    long pTotal = pIn + pOut;
                    return new ModelProviderRowVM
                    {
                        Provider = p.Key,
                        RawModel = p.OrderByDescending(x => x.InTokens + x.OutTokens).First().Model,
                        GroupName = displayName,
                        InTokens = pIn,
                        OutTokens = pOut,
                        Requests = p.Sum(x => x.Requests),
                        Total = pTotal,
                        ShareOfModel = mgTotal > 0 ? 100.0 * pTotal / mgTotal : 0,
                    };
                })
                .OrderByDescending(p => p.Total)
                .ToList();

            modelGroups.Add(new ModelGroupVM
            {
                Name = displayName,
                InTokens = mgIn,
                OutTokens = mgOut,
                Requests = mgReq,
                Total = mgTotal,
                Providers = providerRows,
            });
        }
        long modelGrand = modelGroups.Sum(x => x.Total);
        if (modelGrand > 0)
            foreach (var mg in modelGroups) mg.ShareOfAll = 100.0 * mg.Total / modelGrand;
        ModelsPanel.ItemsSource = modelGroups;
        // 客户端汇总
        // agentTotals 已在后台查询中获得
        long agentGrand = agentTotals.Sum(a => a.InTokens + a.OutTokens);
        AgentsPanel.ItemsSource = agentTotals.Select(a => new AgentRowVM
        {
            Name = AgentName(a.Agent),
            Agent = a.Agent,
            InTokens = a.InTokens,
            OutTokens = a.OutTokens,
            Requests = a.Requests,
            Total = a.InTokens + a.OutTokens,
            ShareOfAll = agentGrand > 0 ? 100.0 * (a.InTokens + a.OutTokens) / agentGrand : 0,
        }).ToList();

        // 调用明细
        // events 已在后台查询中获得
        CallsList.ItemsSource = events.Select(ev => new CallRowVM
        {
            TimeText = ev.Ts.ToString("MM-dd HH:mm:ss"),
            Agent = ev.Agent,
            Provider = !string.IsNullOrEmpty(ev.ProviderId) ? ev.ProviderId
                     : !string.IsNullOrEmpty(ev.ProviderName) ? ev.ProviderName
                     : AgentName(ev.Agent),
            Model = string.IsNullOrEmpty(ev.Model) ? I18nService.T("Usage.UnknownModel") : ev.Model,
            InTokens = ev.InTokens,
            OutTokens = ev.OutTokens,
            Total = ev.InTokens + ev.OutTokens,
        }).ToList();
    }

    // ---------- 每日趋势图（Canvas 绘制） ----------

    void OnChartSizeChanged(object sender, SizeChangedEventArgs e) => ScheduleChartRedraw();

    /// <summary>尺寸变化防抖：拖动窗口大小时每帧都触发重绘会卡，合并为停顿 120ms 后重绘一次。</summary>
    void ScheduleChartRedraw()
    {
        if (_chartRedrawTimer == null)
        {
            _chartRedrawTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(120),
            };
            _chartRedrawTimer.Tick += (_, _) =>
            {
                _chartRedrawTimer!.Stop();
                DrawTrendChart();
            };
        }
        _chartRedrawTimer.Stop();
        _chartRedrawTimer.Start();
    }

    void DrawTrendChart()
    {
        var canvas = TrendCanvas;
        if (canvas == null) return;
        canvas.Children.Clear();
        if (_daily.Count == 0) return;

        double w = canvas.ActualWidth > 0 ? canvas.ActualWidth : 900;
        double h = canvas.ActualHeight > 0 ? canvas.ActualHeight : 128;
        const double topPad = 12, bottomPad = 20, sidePad = 6;
        double plotH = h - topPad - bottomPad;

        long maxTotal = _daily.Max(d => d.InTokens + d.OutTokens);
        if (maxTotal <= 0) maxTotal = 1;

        // 横向网格线 + 右侧刻度标签
        for (int i = 1; i <= 3; i++)
        {
            double y = topPad + plotH * (1 - i / 4.0);
            var line = new System.Windows.Shapes.Line
            {
                X1 = sidePad, X2 = w - sidePad, Y1 = y, Y2 = y,
                Stroke = new SolidColorBrush(Color.FromRgb(0xE2, 0xE8, 0xF0)),
                StrokeThickness = 1,
                StrokeDashArray = new DoubleCollection { 2, 3 },
            };
            canvas.Children.Add(line);
            var lbl = new TextBlock
            {
                Text = FormatTokens(maxTotal * i / 4),
                FontSize = 9,
                Foreground = new SolidColorBrush(Color.FromRgb(0x94, 0xA3, 0xB8)),
            };
            Canvas.SetTop(lbl, y - 6);
            Canvas.SetLeft(lbl, w - sidePad - 44);
            canvas.Children.Add(lbl);
        }
        // 基线
        var baseline = new System.Windows.Shapes.Line
        {
            X1 = sidePad, X2 = w - sidePad, Y1 = topPad + plotH, Y2 = topPad + plotH,
            Stroke = new SolidColorBrush(Color.FromRgb(0xCB, 0xD5, 0xE1)),
            StrokeThickness = 1,
        };
        canvas.Children.Add(baseline);

        int n = _daily.Count;
        double slot = (w - sidePad * 2) / n;
        double barW = Math.Clamp(slot * 0.56, 5, 34);
        var inFill = new SolidColorBrush(Color.FromRgb(0xC7, 0xD2, 0xFE));
        var outFill = new SolidColorBrush(Color.FromRgb(0x63, 0x66, 0xF1));
        // 选中日：柱形变色高亮（更深的填充 + 描边）
        var selInFill = new SolidColorBrush(Color.FromRgb(0x81, 0x8C, 0xF8));
        var selOutFill = new SolidColorBrush(Color.FromRgb(0x31, 0x2E, 0x81));
        var selBorder = new SolidColorBrush(Color.FromRgb(0x4F, 0x46, 0xE5));
        int labelStep = Math.Max(1, (int)Math.Ceiling(n / 12.0));

        for (int i = 0; i < n; i++)
        {
            var d = _daily[i];
            double cx = sidePad + slot * (i + 0.5);
            double inH = plotH * d.InTokens / maxTotal;
            double outH = plotH * d.OutTokens / maxTotal;
            double baseY = topPad + plotH;
            bool selected = _selectedDay.HasValue && d.Day == _selectedDay.Value.ToString("yyyy-MM-dd");

            var tip = $"{d.Day}\n{I18nService.F("Usage.InOut", FormatTokens(d.InTokens), FormatTokens(d.OutTokens))}";

            // 选中日：柱形变色 + 描边高亮（不再用整列背景）
            if (inH > 0)
            {
                var bar = new Border
                {
                    Width = barW,
                    Height = Math.Max(inH, 1),
                    Background = selected ? selInFill : inFill,
                    CornerRadius = new CornerRadius(2, 2, 0, 0),
                    BorderBrush = selected ? selBorder : null,
                    BorderThickness = selected ? new Thickness(1) : default,
                };
                Canvas.SetLeft(bar, cx - barW / 2);
                Canvas.SetTop(bar, baseY - inH - outH);
                canvas.Children.Add(bar);
            }
            if (outH > 0)
            {
                var bar = new Border
                {
                    Width = barW,
                    Height = Math.Max(outH, 1),
                    Background = selected ? selOutFill : outFill,
                    CornerRadius = new CornerRadius(2, 2, 0, 0),
                    BorderBrush = selected ? selBorder : null,
                    BorderThickness = selected ? new Thickness(1) : default,
                };
                Canvas.SetLeft(bar, cx - barW / 2);
                Canvas.SetTop(bar, baseY - outH);
                canvas.Children.Add(bar);
            }

            // 选中日为空白天时：基线画一个小色块标记，保证选中可见
            if (selected && inH <= 0 && outH <= 0)
            {
                var stub = new Border
                {
                    Width = barW,
                    Height = 3,
                    Background = selOutFill,
                    CornerRadius = new CornerRadius(1.5),
                };
                Canvas.SetLeft(stub, cx - barW / 2);
                Canvas.SetTop(stub, baseY - 3);
                canvas.Children.Add(stub);
            }

            // 选中日：柱顶显示当天总消耗
            if (selected)
            {
                double barTop = baseY - inH - outH;
                var totalLbl = new TextBlock
                {
                    Text = FormatTokens(d.InTokens + d.OutTokens),
                    FontSize = 10,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x4F, 0x46, 0xE5)),
                    Width = slot,
                    TextAlignment = TextAlignment.Center,
                };
                Canvas.SetLeft(totalLbl, sidePad + slot * i);
                Canvas.SetTop(totalLbl, Math.Max(barTop - 17, 0));
                canvas.Children.Add(totalLbl);
            }

            // 日期标签（选中日常显并加粗着色）
            if (i % labelStep == 0 || i == n - 1 || selected)
            {
                var lbl = new TextBlock
                {
                    Text = d.Day.Length >= 10 ? d.Day.Substring(5) : d.Day,
                    FontSize = 9,
                    FontWeight = selected ? FontWeights.Bold : FontWeights.Normal,
                    Foreground = new SolidColorBrush(selected ? Color.FromRgb(0x4F, 0x46, 0xE5) : Color.FromRgb(0x94, 0xA3, 0xB8)),
                    Width = slot,
                    TextAlignment = TextAlignment.Center,
                };
                Canvas.SetLeft(lbl, sidePad + slot * i);
                Canvas.SetTop(lbl, h - 17);
                canvas.Children.Add(lbl);
            }

            // 整列点击热区（提示挂热区上）：点击选中当天，再次点击取消，点其他列切换
            var hit = new Border
            {
                Width = slot,
                Height = h,
                Background = Brushes.Transparent,
                ToolTip = tip,
                Cursor = Cursors.Hand,
                Tag = d.Day,
            };
            Canvas.SetLeft(hit, sidePad + slot * i);
            Canvas.SetTop(hit, 0);
            hit.MouseLeftButtonUp += OnDaySlotClick;
            canvas.Children.Add(hit);
        }
    }

    /// <summary>点击趋势图某天列：选中/取消选中；汇总卡片与下方明细随之切换为当天数据。</summary>
    void OnDaySlotClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string day }
            && DateTime.TryParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
        {
            _selectedDay = _selectedDay == d.Date ? null : d.Date;
            RefreshData();
        }
    }

    // ---------- 视图切换 ----------

    void OnTabProvidersChecked(object sender, RoutedEventArgs e) => SwitchView(ProvidersPanel);
    void OnTabAgentsChecked(object sender, RoutedEventArgs e) => SwitchView(AgentsPanel);
    void OnTabModelsChecked(object sender, RoutedEventArgs e) => SwitchView(ModelsPanel);
    void OnTabCallsChecked(object sender, RoutedEventArgs e) => SwitchView(CallsPanel);

    void SwitchView(UIElement panel)
    {
        if (panel == null) return;
        ProvidersPanel.Visibility = panel == ProvidersPanel ? Visibility.Visible : Visibility.Collapsed;
        AgentsPanel.Visibility = panel == AgentsPanel ? Visibility.Visible : Visibility.Collapsed;
        ModelsPanel.Visibility = panel == ModelsPanel ? Visibility.Visible : Visibility.Collapsed;
        CallsPanel.Visibility = panel == CallsPanel ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---------- 展开/收起 ----------

    void OnToggleProviderExpand(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ProviderGroupVM g)
            g.Expanded = !g.Expanded;
    }

    void OnToggleModelExpand(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ModelGroupVM mg)
            mg.Expanded = !mg.Expanded;
    }

    // ---------- 窗口操作 ----------

    void Header_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }

    void OnClose(object sender, RoutedEventArgs e) => Close();

    // ---------- 工具 ----------

    // ---------- 模型名归一化（跨供应商合并同模型） ----------

    static readonly string[] ModelStripSuffixes = { "-latest", "-free" };

    /// <summary>
    /// 清理模型显示名：去掉组织/路由前缀（zai-org/、pro/）、:variant 变体（kimi-k3:test）、
    /// [1M] 上下文标记、尾部日期快照（-0731、-20250929）与通道标记（-free/-latest），保留原大小写。
    /// </summary>
    static string CleanModelName(string raw)
    {
        var m = (raw ?? "").Trim();
        if (m.Length == 0) return "";
        int slash = m.LastIndexOf('/');
        if (slash >= 0) m = m[(slash + 1)..].Trim();
        int colon = m.IndexOf(':');
        if (colon > 0) m = m[..colon].Trim();
        if (m.EndsWith("[1m]", StringComparison.OrdinalIgnoreCase)) m = m[..^4].Trim();
        var date = System.Text.RegularExpressions.Regex.Match(m, @"-\d{4,8}$");
        if (date.Success) m = m[..date.Index];
        foreach (var s in ModelStripSuffixes)
            if (m.EndsWith(s, StringComparison.OrdinalIgnoreCase)) { m = m[..^s.Length]; break; }
        return m;
    }

    /// <summary>模型分组键：清理后小写。</summary>
    static string NormalizeModelKey(string raw) => CleanModelName(raw).ToLowerInvariant();

    /// <summary>
    /// 部署长名归并：kingblue…--ep-kimi-k3-server.us-west.modal.direct 这类 modal 部署串
    /// 归并到其包含的已知短模型名。短名后必须紧跟部署特征（-server / . / 结尾），
    /// 避免 glm-5.3 误并入 glm-5.3-flash。
    /// </summary>
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
        _ => (new SolidColorBrush(Color.FromRgb(0x47, 0x55, 0x69)),
              new SolidColorBrush(Color.FromRgb(0xF1, 0xF5, 0xF9))),
    };

    static string FormatTokens(long total)
    {
        if (total >= 1_000_000_000) return $"{total / 1_000_000_000.0:F2}B";
        if (total >= 1_000_000) return $"{total / 1_000_000.0:F2}M";
        if (total >= 1_000) return $"{total / 1_000.0:F1}K";
        return total.ToString("N0", CultureInfo.CurrentCulture);
    }

    // ---------- ViewModel ----------

    public class ProviderGroupVM : INotifyPropertyChanged
    {
        bool _expanded;

        public string Name { get; set; } = "";
        public long InTokens { get; set; }
        public long OutTokens { get; set; }
        public long Requests { get; set; }
        public long Total { get; set; }
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
        public string ReqText => I18nService.F("Usage.AgentRequests", Requests.ToString("N0", CultureInfo.CurrentCulture));
        public string InOutText => I18nService.F("Usage.InOut", FormatTokens(InTokens), FormatTokens(OutTokens));
        public string TotalText => FormatTokens(Total);
        public string ModelCountText => I18nService.F("Usage.ModelsFmt", Models.Count);

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    public class ModelRowVM
    {
        public string Name { get; set; } = "";
        public long InTokens { get; set; }
        public long OutTokens { get; set; }
        public long Requests { get; set; }
        public long Total { get; set; }
        public double ShareOfProvider { get; set; }

        public double BarWidth => Math.Clamp(BarTrackWidth * ShareOfProvider / 100.0, 0, BarTrackWidth);
        public string ShareText => $"{ShareOfProvider:F1}%";
        public string ReqText => I18nService.F("Usage.AgentRequests", Requests.ToString("N0", CultureInfo.CurrentCulture));
        public string InOutText => I18nService.F("Usage.InOut", FormatTokens(InTokens), FormatTokens(OutTokens));
        public string TotalText => FormatTokens(Total);
    }

    /// <summary>模型汇总分组：同一模型（归一化后）跨供应商合并的统计行。</summary>
    public class ModelGroupVM : INotifyPropertyChanged
    {
        bool _expanded;

        public string Name { get; set; } = "";
        public long InTokens { get; set; }
        public long OutTokens { get; set; }
        public long Requests { get; set; }
        public long Total { get; set; }
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
        public string ReqText => I18nService.F("Usage.AgentRequests", Requests.ToString("N0", CultureInfo.CurrentCulture));
        public string InOutText => I18nService.F("Usage.InOut", FormatTokens(InTokens), FormatTokens(OutTokens));
        public string TotalText => FormatTokens(Total);
        public string ProviderCountText => I18nService.F("Usage.ProvidersFmt", Providers.Count);

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    /// <summary>模型分组下的供应商细分行（含原始模型名，便于核对归并是否正确）。</summary>
    public class ModelProviderRowVM
    {
        public string Provider { get; set; } = "";
        public string RawModel { get; set; } = "";
        public string GroupName { get; set; } = "";
        public long InTokens { get; set; }
        public long OutTokens { get; set; }
        public long Requests { get; set; }
        public long Total { get; set; }
        public double ShareOfModel { get; set; }

        public string NameText => string.IsNullOrEmpty(Provider) ? I18nService.T("Usage.UnknownModel") : Provider;
        public string RawModelText => string.Equals(RawModel, GroupName, StringComparison.OrdinalIgnoreCase)
            ? ""
            : I18nService.F("Usage.RawModelFmt", RawModel);
        public double BarWidth => Math.Clamp(BarTrackWidth * ShareOfModel / 100.0, 0, BarTrackWidth);
        public string ShareText => $"{ShareOfModel:F1}%";
        public string ReqText => I18nService.F("Usage.AgentRequests", Requests.ToString("N0", CultureInfo.CurrentCulture));
        public string InOutText => I18nService.F("Usage.InOut", FormatTokens(InTokens), FormatTokens(OutTokens));
        public string TotalText => FormatTokens(Total);
    }

    public class AgentRowVM
    {
        public string Name { get; set; } = "";
        public string Agent { get; set; } = "";
        public long InTokens { get; set; }
        public long OutTokens { get; set; }
        public long Requests { get; set; }
        public long Total { get; set; }
        public double ShareOfAll { get; set; }

        public Brush AgentBrush => AgentColors(Agent).fg;
        public double BarWidth => Math.Clamp(BarTrackWidth * ShareOfAll / 100.0, 0, BarTrackWidth);
        public string ShareText => $"{ShareOfAll:F1}%";
        public string ReqText => I18nService.F("Usage.AgentRequests", Requests.ToString("N0", CultureInfo.CurrentCulture));
        public string InOutText => I18nService.F("Usage.InOut", FormatTokens(InTokens), FormatTokens(OutTokens));
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

        public string AgentName => UsageMonitorWindow.AgentName(Agent);
        public Brush AgentBrush => AgentColors(Agent).fg;
        public Brush AgentBg => AgentColors(Agent).bg;
        public string InText => FormatTokens(InTokens);
        public string OutText => FormatTokens(OutTokens);
        public string TotalText => FormatTokens(Total);
    }
}

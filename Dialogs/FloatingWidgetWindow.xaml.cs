using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using APISwitch.Models;
using APISwitch.Services;
using MediaBrush = System.Windows.Media.Brush;
using MediaColor = System.Windows.Media.Color;
using WpfPoint = System.Windows.Point;
using WpfSize = System.Windows.Size;

namespace APISwitch.Dialogs;

public partial class FloatingWidgetWindow : Window
{
    private readonly MainWindow _mainWindow;
    private List<Profile> _profiles = new();
    private UsageStorage.AntigravityWidgetStats _stats = new(0, 0, 0, 0, 0, 0, "", null, 0);

    private bool _isExpanded;
    private bool _isModelsOpen;
    private bool _forceClosing;
    private bool _isTransitioningExpand;

    // 不可变边缘锚点（仅在首次初始化、用户拖拽松手或手动重置时更新，任何自动布局/冷启动宽度变化均只读使用）
    private double _anchorX;
    private double _anchorY;
    private bool _anchorRight = true;
    private bool _anchorBottom = true;
    private bool _anchorInitialized;
    private double _lastCapsuleWidth = 280;
    private double _lastCapsuleHeight = 58;

    // 胶囊拖拽手势状态
    private bool _capsuleMouseDown;
    private bool _capsuleDragging;
    private WpfPoint _dragStartScreenDip;
    private double _dragStartLeft;
    private double _dragStartTop;

    // 展开头部拖拽手势状态（避免误点导致位置变化）
    private bool _expandedMouseDown;
    private bool _expandedDragging;
    private WpfPoint _expDragStartScreenDip;
    private double _expDragStartLeft;
    private double _expDragStartTop;

    private static readonly MediaBrush BrushGreen = FreezeBrush(0x16, 0xA3, 0x4A);
    private static readonly MediaBrush BrushAmber = FreezeBrush(0xD9, 0x77, 0x06);
    private static readonly MediaBrush BrushRed = FreezeBrush(0xDC, 0x26, 0x26);
    private static readonly MediaBrush BrushGemBlue = FreezeBrush(0x02, 0x84, 0xC7);
    private static readonly MediaBrush BrushAccent = FreezeBrush(0x4F, 0x46, 0xE5);
    private static readonly MediaBrush BrushClaudeViolet = FreezeBrush(0x7C, 0x3A, 0xED);
    private static readonly MediaBrush BrushMuted = FreezeBrush(0x64, 0x74, 0x8B);
    private static readonly MediaBrush BrushRowActiveBg = FreezeBrush(0xF0, 0xFD, 0xF4);
    private static readonly MediaBrush BrushRowActiveBorder = FreezeBrush(0xBB, 0xF7, 0xD0);
    private static readonly MediaBrush BrushRowIdleBg = FreezeBrush(0xF8, 0xFA, 0xFC);
    private static readonly MediaBrush BrushRowIdleBorder = FreezeBrush(0xE2, 0xE8, 0xF0);

    public sealed class WidgetAccountRow
    {
        public required Profile Profile { get; init; }
        public required string Email { get; init; }
        public required string GemShort { get; init; }
        public required string ClaudeShort { get; init; }
        public required bool IsCurrent { get; init; }
        public required MediaBrush DotBrush { get; init; }
        public MediaBrush RowBg => IsCurrent ? BrushRowActiveBg : BrushRowIdleBg;
        public MediaBrush RowBorder => IsCurrent ? BrushRowActiveBorder : BrushRowIdleBorder;
        public FontWeight EmailWeight => IsCurrent ? FontWeights.SemiBold : FontWeights.Normal;
        public Visibility CurrentBadgeVisibility => IsCurrent ? Visibility.Visible : Visibility.Collapsed;
        public Visibility SwitchBtnVisibility => IsCurrent ? Visibility.Collapsed : Visibility.Visible;
    }

    public FloatingWidgetWindow(MainWindow mainWindow)
    {
        InitializeComponent();
        _mainWindow = mainWindow;
        Loaded += OnWidgetLoaded;
        SizeChanged += OnWidgetSizeChanged;
    }

    static MediaBrush FreezeBrush(byte r, byte g, byte b)
    {
        var bsh = new SolidColorBrush(MediaColor.FromRgb(r, g, b));
        bsh.Freeze();
        return bsh;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        EnsureAnchorLoaded();
        RootGrid.Measure(new WpfSize(double.PositiveInfinity, double.PositiveInfinity));
        ApplyAnchorPosition();
    }

    void OnWidgetLoaded(object sender, RoutedEventArgs e)
    {
        EnsureAnchorLoaded();
        RenderAll();
        RootGrid.Measure(new WpfSize(double.PositiveInfinity, double.PositiveInfinity));
        UpdateLayout();
        ApplyAnchorPosition();
    }

    void OnWidgetSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!IsLoaded || _isTransitioningExpand || _capsuleDragging || _expandedDragging || !_anchorInitialized)
            return;
        ApplyAnchorPosition();
    }

    public void PrepareForHide()
    {
        if (_isExpanded)
            SetExpanded(false);
    }

    public void RestoreAfterShow()
    {
        EnsureAnchorLoaded();
        RootGrid.Measure(new WpfSize(double.PositiveInfinity, double.PositiveInfinity));
        UpdateLayout();
        ApplyAnchorPosition();
    }

    public void ForceClose()
    {
        _forceClosing = true;
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_forceClosing)
        {
            e.Cancel = true;
            _mainWindow.SetFloatingWidgetEnabled(false, showToast: true);
            return;
        }
        base.OnClosing(e);
    }

    public void UpdateProfiles(IEnumerable<Profile> profiles)
    {
        _profiles = profiles.ToList();
        RenderAll();
    }

    public void UpdateUsageStats(UsageStorage.AntigravityWidgetStats stats)
    {
        _stats = stats;
        RenderAll();
    }

    public void RefreshLocalization()
    {
        RenderAll();
    }

    public void SetRefreshing(bool isRefreshing)
    {
        if (ExpandedRefreshBtn != null)
            ExpandedRefreshBtn.IsEnabled = !isRefreshing;
    }

    void RenderAll()
    {
        if (!IsLoaded && !IsVisible && _profiles.Count == 0 && _stats.TodayRequests == 0)
            return;

        var active = _profiles.FirstOrDefault(p => p.IsCurrent) ?? _profiles.FirstOrDefault();

        if (active == null)
        {
            CapsuleInitialText.Text = "?";
            CapsuleStatusDot.Fill = BrushMuted;
            CapsuleGemPrimaryText.Text = "G —";
            CapsuleGemPrimaryText.Foreground = BrushMuted;
            CapsuleGemWeekText.Visibility = Visibility.Collapsed;
            CapsuleClaudePrimaryText.Text = "C —";
            CapsuleClaudePrimaryText.Foreground = BrushMuted;
            CapsuleClaudeWeekText.Visibility = Visibility.Collapsed;

            ExpandedInitialText.Text = "?";
            ExpandedEmailText.Text = I18nService.T("Widget.NoAccount");
            ExpandedTierText.Text = "—";
            ExpandedSyncText.Text = "";

            SetPoolVisual(Gem5hPctText, Gem5hCdBadge, Gem5hCdText, Gem5hBar, null, "—", "", null, null, BrushGreen);
            SetPoolVisual(GemWeekPctText, GemWeekCdBadge, GemWeekCdText, GemWeekBar, null, "—", "", null, null, BrushAccent);
            SetPoolVisual(Claude5hPctText, Claude5hCdBadge, Claude5hCdText, Claude5hBar, null, "—", "", null, null, BrushGreen);
            SetPoolVisual(ClaudeWeekPctText, ClaudeWeekCdBadge, ClaudeWeekCdText, ClaudeWeekBar, null, "—", "", null, null, BrushClaudeViolet);

            Gem5hBlock.Visibility = Visibility.Visible;
            Claude5hBlock.Visibility = Visibility.Visible;

            ModelsSection.Visibility = Visibility.Collapsed;
            WidgetModelsList.ItemsSource = null;
        }
        else
        {
            var email = !string.IsNullOrEmpty(active.Email) ? active.Email : active.Name;
            CapsuleInitialText.Text = active.Initial;
            ExpandedInitialText.Text = active.Initial;
            ExpandedEmailText.Text = email;
            ExpandedTierText.Text = active.TierDisplay;
            ExpandedSyncText.Text = active.QuotaUpdatedText;

            bool hasGem5h = active.IsProTier || active.Quota5hFraction.HasValue;
            bool has3p5h = active.IsProTier || active.Quota3p5hFraction.HasValue;

            // 胶囊双维度文本：5h + 周限
            if (hasGem5h)
            {
                CapsuleGemPrimaryText.Text = $"G {active.Quota5hPercentText}";
                CapsuleGemPrimaryText.Foreground = GetFractionBrush(active.Quota5hFraction, BrushGemBlue);
                if (active.QuotaWeeklyFraction.HasValue)
                {
                    CapsuleGemWeekText.Text = "· " + I18nService.F("Widget.ShortWeekFmt", active.QuotaWeeklyPercentText);
                    CapsuleGemWeekText.Foreground = GetFractionBrush(active.QuotaWeeklyFraction, BrushGemBlue);
                    CapsuleGemWeekText.Visibility = Visibility.Visible;
                }
                else
                {
                    CapsuleGemWeekText.Visibility = Visibility.Collapsed;
                }
            }
            else
            {
                CapsuleGemPrimaryText.Text = "G " + I18nService.F("Widget.ShortWeekFmt", active.QuotaWeeklyPercentText);
                CapsuleGemPrimaryText.Foreground = GetFractionBrush(active.QuotaWeeklyFraction, BrushGemBlue);
                CapsuleGemWeekText.Visibility = Visibility.Collapsed;
            }

            if (has3p5h)
            {
                CapsuleClaudePrimaryText.Text = $"C {active.Quota3p5hPercentText}";
                CapsuleClaudePrimaryText.Foreground = GetFractionBrush(active.Quota3p5hFraction, BrushClaudeViolet);
                if (active.Quota3pWeeklyFraction.HasValue)
                {
                    CapsuleClaudeWeekText.Text = "· " + I18nService.F("Widget.ShortWeekFmt", active.Quota3pWeeklyPercentText);
                    CapsuleClaudeWeekText.Foreground = GetFractionBrush(active.Quota3pWeeklyFraction, BrushClaudeViolet);
                    CapsuleClaudeWeekText.Visibility = Visibility.Visible;
                }
                else
                {
                    CapsuleClaudeWeekText.Visibility = Visibility.Collapsed;
                }
            }
            else
            {
                CapsuleClaudePrimaryText.Text = "C " + I18nService.F("Widget.ShortWeekFmt", active.Quota3pWeeklyPercentText);
                CapsuleClaudePrimaryText.Foreground = GetFractionBrush(active.Quota3pWeeklyFraction, BrushClaudeViolet);
                CapsuleClaudeWeekText.Visibility = Visibility.Collapsed;
            }

            double? minFrac = MinNonNull(
                hasGem5h ? active.Quota5hFraction : null,
                active.QuotaWeeklyFraction,
                has3p5h ? active.Quota3p5hFraction : null,
                active.Quota3pWeeklyFraction);
            CapsuleStatusDot.Fill = active.QuotaExhausted || (minFrac.HasValue && minFrac.Value <= 0.02)
                ? BrushRed
                : (minFrac.HasValue && minFrac.Value <= 0.20 ? BrushAmber : BrushGreen);

            // 展开双栏 5h + 周限细节
            Gem5hBlock.Visibility = hasGem5h ? Visibility.Visible : Visibility.Collapsed;
            Claude5hBlock.Visibility = has3p5h ? Visibility.Visible : Visibility.Collapsed;

            SetPoolVisual(Gem5hPctText, Gem5hCdBadge, Gem5hCdText, Gem5hBar,
                active.Quota5hFraction, active.Quota5hPercentText, active.Quota5hCountdown,
                active.Quota5hCountdownBg, active.Quota5hCountdownFg, BrushGreen);
            SetPoolVisual(GemWeekPctText, GemWeekCdBadge, GemWeekCdText, GemWeekBar,
                active.QuotaWeeklyFraction, active.QuotaWeeklyPercentText, active.QuotaWeeklyCountdown,
                active.QuotaWeeklyCountdownBg, active.QuotaWeeklyCountdownFg, BrushAccent);

            SetPoolVisual(Claude5hPctText, Claude5hCdBadge, Claude5hCdText, Claude5hBar,
                active.Quota3p5hFraction, active.Quota3p5hPercentText, active.Quota3p5hCountdown,
                active.Quota3p5hCountdownBg, active.Quota3p5hCountdownFg, BrushGreen);
            SetPoolVisual(ClaudeWeekPctText, ClaudeWeekCdBadge, ClaudeWeekCdText, ClaudeWeekBar,
                active.Quota3pWeeklyFraction, active.Quota3pWeeklyPercentText, active.Quota3pWeeklyCountdown,
                active.Quota3pWeeklyCountdownBg, active.Quota3pWeeklyCountdownFg, BrushClaudeViolet);

            // 折叠可用模型抽屉
            var models = active.MergedAvailableModels;
            if (models.Count > 0)
            {
                ModelsSection.Visibility = Visibility.Visible;
                SetModelsBarText(I18nService.F("Widget.ModelsHeaderFmt", models.Count), _isModelsOpen ? "▾" : "▸");
                ModelsDrawer.Visibility = _isModelsOpen ? Visibility.Visible : Visibility.Collapsed;
                WidgetModelsList.ItemsSource = models;
            }
            else
            {
                ModelsSection.Visibility = Visibility.Collapsed;
                WidgetModelsList.ItemsSource = null;
            }
        }

        // Antigravity 单端今日统计与最新速率
        string totalStr = FormatCompactTokens(_stats.TodayTotalTokens);
        string inStr = FormatCompactTokens(_stats.TodayInTokens);
        string outStr = FormatCompactTokens(_stats.TodayOutTokens);
        CapsuleTokensText.Text = totalStr;
        TodayTokensText.Text = totalStr;
        TodaySubText.Text = _stats.TodayRequests > 0
            ? I18nService.F("Widget.TodaySubFmt", _stats.TodayRequests, inStr, outStr)
            : I18nService.T("Widget.NoCallsToday");

        if (_stats.LastSpeedTokPerSec > 0.1)
        {
            CapsuleSpeedText.Text = $"{Math.Round(_stats.LastSpeedTokPerSec)} tok/s";
            CapsuleSpeedText.Foreground = BrushGreen;
            LatestSpeedText.Text = $"{_stats.LastSpeedTokPerSec:0.0} tok/s";
            LatestSpeedText.Foreground = BrushGreen;
        }
        else
        {
            CapsuleSpeedText.Text = "— tok/s";
            CapsuleSpeedText.Foreground = BrushMuted;
            LatestSpeedText.Text = "—";
            LatestSpeedText.Foreground = BrushMuted;
        }

        AvgSpeedSubText.Text = _stats.TodayAvgSpeedTokPerSec > 0.1
            ? I18nService.F("Widget.AvgSpeedFmt", $"{_stats.TodayAvgSpeedTokPerSec:0.0}")
            : I18nService.T("Widget.NoCallsToday");

        RecentModelText.Text = FormatFriendlyModelName(_stats.LastModel);
        RecentModelText.ToolTip = string.IsNullOrEmpty(_stats.LastModel) ? null : _stats.LastModel;
        TtftSubText.Text = I18nService.F("Widget.TtftFmt", FormatTtft(_stats.LastTtftMs));

        // 账号快捷切换列表（展示 5h/周 对比）
        SwitchHeaderText.Text = I18nService.F("Widget.SwitchHeaderFmt", _profiles.Count);
        SwitcherSection.Visibility = _profiles.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        var rows = new List<WidgetAccountRow>(_profiles.Count);
        foreach (var p in _profiles)
        {
            bool pHasGem5h = p.IsProTier || p.Quota5hFraction.HasValue;
            bool pHas3p5h = p.IsProTier || p.Quota3p5hFraction.HasValue;
            string gemPair = pHasGem5h && p.QuotaWeeklyFraction.HasValue
                ? $"G {p.Quota5hPercentText}/{p.QuotaWeeklyPercentText}"
                : $"G {(pHasGem5h ? p.Quota5hPercentText : p.QuotaWeeklyPercentText)}";
            string cPair = pHas3p5h && p.Quota3pWeeklyFraction.HasValue
                ? $"C {p.Quota3p5hPercentText}/{p.Quota3pWeeklyPercentText}"
                : $"C {(pHas3p5h ? p.Quota3p5hPercentText : p.Quota3pWeeklyPercentText)}";

            var minF = MinNonNull(
                pHasGem5h ? p.Quota5hFraction : null,
                p.QuotaWeeklyFraction,
                pHas3p5h ? p.Quota3p5hFraction : null,
                p.Quota3pWeeklyFraction);
            var dot = p.IsCurrent
                ? BrushGreen
                : (p.QuotaExhausted || (minF.HasValue && minF.Value <= 0.02) ? BrushRed : BrushMuted);

            rows.Add(new WidgetAccountRow
            {
                Profile = p,
                Email = !string.IsNullOrEmpty(p.Email) ? p.Email : p.Name,
                GemShort = gemPair,
                ClaudeShort = cPair,
                IsCurrent = p.IsCurrent,
                DotBrush = dot
            });
        }
        WidgetAccountsList.ItemsSource = rows;
    }

    void SetModelsBarText(string headerText, string chevron)
    {
        ModelsToggleText.Text = headerText;
        ModelsChevronText.Text = chevron;
    }

    static void SetPoolVisual(
        TextBlock pctText,
        Border cdBadge,
        TextBlock cdText,
        System.Windows.Controls.ProgressBar bar,
        double? frac,
        string pctStr,
        string cdStr,
        MediaBrush? cdBg,
        MediaBrush? cdFg,
        MediaBrush normalBrush)
    {
        pctText.Text = pctStr;
        pctText.Foreground = GetFractionBrush(frac, normalBrush);
        bar.Value = frac.HasValue ? Math.Clamp(frac.Value, 0.0, 1.0) : 0.0;
        bar.Foreground = GetFractionBrush(frac, normalBrush);

        if (!string.IsNullOrEmpty(cdStr))
        {
            cdBadge.Visibility = Visibility.Visible;
            cdText.Text = cdStr;
            if (cdBg != null) cdBadge.Background = cdBg;
            if (cdFg != null) cdText.Foreground = cdFg;
        }
        else
        {
            cdBadge.Visibility = Visibility.Collapsed;
        }
    }

    static double? MinNonNull(params double?[] vals)
    {
        double? min = null;
        foreach (var v in vals)
        {
            if (v.HasValue && (!min.HasValue || v.Value < min.Value))
                min = v.Value;
        }
        return min;
    }

    static MediaBrush GetFractionBrush(double? fraction, MediaBrush normalBrush)
    {
        if (!fraction.HasValue) return BrushMuted;
        if (fraction.Value <= 0.02) return BrushRed;
        if (fraction.Value <= 0.20) return BrushAmber;
        return normalBrush;
    }

    static string FormatCompactTokens(long tokens)
    {
        if (tokens <= 0) return "0";
        if (tokens >= 1_000_000_000) return $"{tokens / 1_000_000_000.0:0.##}B";
        if (tokens >= 1_000_000) return $"{tokens / 1_000_000.0:0.##}M";
        if (tokens >= 1_000) return $"{tokens / 1_000.0:0.#}K";
        return tokens.ToString(CultureInfo.InvariantCulture);
    }

    static string FormatTtft(long ms)
    {
        if (ms <= 0) return "—";
        if (ms >= 1000) return $"{ms / 1000.0:0.1}s";
        return $"{ms}ms";
    }

    static string FormatFriendlyModelName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "—";
        var s = raw.Trim();
        if (s.StartsWith("claude-", StringComparison.OrdinalIgnoreCase))
        {
            var parts = s.Split('-', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 4 && int.TryParse(parts[^2], out _) && int.TryParse(parts[^1], out _))
            {
                var family = string.Join(" ", parts[1..^2].Select(Capitalize));
                return $"Claude {family} {parts[^2]}.{parts[^1]}";
            }
            return string.Join(" ", parts.Select(Capitalize));
        }
        if (s.StartsWith("gemini-", StringComparison.OrdinalIgnoreCase))
        {
            var parts = s.Split('-', StringSplitOptions.RemoveEmptyEntries);
            return string.Join(" ", parts.Select(Capitalize));
        }
        return s;

        static string Capitalize(string part) =>
            part.Length == 0 ? part : char.ToUpperInvariant(part[0]) + part[1..];
    }

    // ==================== 不可变边缘锚点定位与拖拽记忆 ====================

    public static (double Left, double Top) ComputeAnchoredPlacement(
        double anchorX, double anchorY, bool anchorRight, bool anchorBottom,
        double width, double height, Rect workArea)
    {
        double rawLeft = anchorRight ? (anchorX - width) : anchorX;
        double rawTop = anchorBottom ? (anchorY - height) : anchorY;

        double minLeft = workArea.Left - 4;
        double maxLeft = Math.Max(minLeft, workArea.Right - width + 4);
        double minTop = workArea.Top - 4;
        double maxTop = Math.Max(minTop, workArea.Bottom - height + 4);

        return (Math.Clamp(rawLeft, minLeft, maxLeft), Math.Clamp(rawTop, minTop, maxTop));
    }

    public void SetExpanded(bool expanded)
    {
        if (_isExpanded == expanded) return;
        _isTransitioningExpand = true;
        try
        {
            EnsureAnchorLoaded();
            if (expanded)
            {
                if (ActualWidth > 20 && ActualHeight > 20)
                {
                    _lastCapsuleWidth = ActualWidth;
                    _lastCapsuleHeight = ActualHeight;
                }
                _isExpanded = true;
                CapsuleView.Visibility = Visibility.Collapsed;
                ExpandedView.Visibility = Visibility.Visible;
            }
            else
            {
                _isExpanded = false;
                ExpandedView.Visibility = Visibility.Collapsed;
                CapsuleView.Visibility = Visibility.Visible;
            }

            RootGrid.Measure(new WpfSize(double.PositiveInfinity, double.PositiveInfinity));
            UpdateLayout();
            ApplyAnchorPosition();
        }
        finally
        {
            _isTransitioningExpand = false;
        }
    }

    void EnsureAnchorLoaded()
    {
        if (_anchorInitialized) return;
        var s = AppSettingsService.Current;
        if (s.FloatingWidgetAnchorX.HasValue && s.FloatingWidgetAnchorY.HasValue)
        {
            _anchorX = s.FloatingWidgetAnchorX.Value;
            _anchorY = s.FloatingWidgetAnchorY.Value;
            _anchorRight = s.FloatingWidgetAnchorRight ?? true;
            _anchorBottom = s.FloatingWidgetAnchorBottom ?? true;
            _anchorInitialized = true;
        }
        else if (s.FloatingWidgetCapsuleLeft.HasValue && s.FloatingWidgetCapsuleTop.HasValue)
        {
            _anchorX = s.FloatingWidgetCapsuleLeft.Value;
            _anchorY = s.FloatingWidgetCapsuleTop.Value;
            _anchorRight = false;
            _anchorBottom = false;
            _anchorInitialized = true;
            PersistAnchor();
        }
        else
        {
            var wa = SystemParameters.WorkArea;
            _anchorRight = true;
            _anchorBottom = true;
            _anchorX = wa.Right - 16;
            _anchorY = wa.Bottom - 16;
            _anchorInitialized = true;
            PersistAnchor();
        }
    }

    void ApplyAnchorPosition()
    {
        if (!_anchorInitialized) return;
        double w = ActualWidth > 0 ? ActualWidth : RootGrid.DesiredSize.Width;
        double h = ActualHeight > 0 ? ActualHeight : RootGrid.DesiredSize.Height;
        if (w <= 0 || h <= 0) return;

        if (!_isExpanded && w > 20 && h > 20)
        {
            _lastCapsuleWidth = w;
            _lastCapsuleHeight = h;
        }

        double approxLeft = _anchorRight ? (_anchorX - w) : _anchorX;
        double approxTop = _anchorBottom ? (_anchorY - h) : _anchorY;
        var wa = GetWorkAreaForAnchor(approxLeft, approxTop, w, h);

        var (targetLeft, targetTop) = ComputeAnchoredPlacement(_anchorX, _anchorY, _anchorRight, _anchorBottom, w, h, wa);
        Left = targetLeft;
        Top = targetTop;
    }

    void CommitUserDragPosition(double rawLeft, double rawTop, double currentW, double currentH)
    {
        if (currentW <= 0 || currentH <= 0) return;
        var wa = GetWorkAreaForAnchor(rawLeft, rawTop, currentW, currentH);

        double minLeft = wa.Left - 4;
        double maxLeft = Math.Max(minLeft, wa.Right - currentW + 4);
        double minTop = wa.Top - 4;
        double maxTop = Math.Max(minTop, wa.Bottom - currentH + 4);
        double clampedLeft = Math.Clamp(rawLeft, minLeft, maxLeft);
        double clampedTop = Math.Clamp(rawTop, minTop, maxTop);

        _anchorRight = (clampedLeft + currentW / 2.0) >= (wa.Left + wa.Width / 2.0);
        _anchorBottom = (clampedTop + currentH / 2.0) >= (wa.Top + wa.Height / 2.0);
        _anchorX = _anchorRight ? (clampedLeft + currentW) : clampedLeft;
        _anchorY = _anchorBottom ? (clampedTop + currentH) : clampedTop;
        _anchorInitialized = true;

        PersistAnchor();
        ApplyAnchorPosition();
    }

    void PersistAnchor()
    {
        var s = AppSettingsService.Current;
        s.FloatingWidgetAnchorX = Math.Round(_anchorX, 1);
        s.FloatingWidgetAnchorY = Math.Round(_anchorY, 1);
        s.FloatingWidgetAnchorRight = _anchorRight;
        s.FloatingWidgetAnchorBottom = _anchorBottom;

        double capW = _lastCapsuleWidth > 20 ? _lastCapsuleWidth : 280;
        double capH = _lastCapsuleHeight > 20 ? _lastCapsuleHeight : 58;
        s.FloatingWidgetCapsuleLeft = Math.Round(_anchorRight ? (_anchorX - capW) : _anchorX, 1);
        s.FloatingWidgetCapsuleTop = Math.Round(_anchorBottom ? (_anchorY - capH) : _anchorY, 1);
        AppSettingsService.Save();
    }

    (double ScaleX, double ScaleY) GetDpiScales()
    {
        var source = PresentationSource.FromVisual(this);
        if (source?.CompositionTarget != null)
        {
            var m = source.CompositionTarget.TransformToDevice;
            if (m.M11 > 0 && m.M22 > 0) return (m.M11, m.M22);
        }
        var dpi = VisualTreeHelper.GetDpi(this);
        return (dpi.DpiScaleX > 0 ? dpi.DpiScaleX : 1.0, dpi.DpiScaleY > 0 ? dpi.DpiScaleY : 1.0);
    }

    Rect GetWorkAreaForAnchor(double left, double top, double width, double height)
    {
        var (scaleX, scaleY) = GetDpiScales();
        double cx = double.IsNaN(left) ? 0 : left + Math.Max(width, 40) / 2.0;
        double cy = double.IsNaN(top) ? 0 : top + Math.Max(height, 40) / 2.0;

        var pt = new System.Drawing.Point((int)Math.Round(cx * scaleX), (int)Math.Round(cy * scaleY));
        var waPx = System.Windows.Forms.Screen.FromPoint(pt).WorkingArea;
        return new Rect(waPx.Left / scaleX, waPx.Top / scaleY, waPx.Width / scaleX, waPx.Height / scaleY);
    }

    public void ResetToBottomRight()
    {
        var wa = SystemParameters.WorkArea;
        _anchorRight = true;
        _anchorBottom = true;
        _anchorX = wa.Right - 16;
        _anchorY = wa.Bottom - 16;
        _anchorInitialized = true;
        PersistAnchor();
        ApplyAnchorPosition();
    }

    // ==================== 鼠标拖拽与交互事件 ====================

    void Capsule_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _capsuleMouseDown = true;
        _capsuleDragging = false;
        _dragStartScreenDip = ToDipScreenPoint(e.GetPosition(this));
        _dragStartLeft = Left;
        _dragStartTop = Top;
        CapsuleView.CaptureMouse();
        e.Handled = true;
    }

    void Capsule_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_capsuleMouseDown || e.LeftButton != MouseButtonState.Pressed) return;
        var cur = ToDipScreenPoint(e.GetPosition(this));
        double dx = cur.X - _dragStartScreenDip.X;
        double dy = cur.Y - _dragStartScreenDip.Y;
        if (!_capsuleDragging && (Math.Abs(dx) > 5 || Math.Abs(dy) > 5))
            _capsuleDragging = true;

        if (_capsuleDragging)
        {
            Left = _dragStartLeft + dx;
            Top = _dragStartTop + dy;
        }
    }

    void Capsule_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_capsuleMouseDown) return;
        _capsuleMouseDown = false;
        if (CapsuleView.IsMouseCaptured)
            CapsuleView.ReleaseMouseCapture();

        if (_capsuleDragging)
        {
            _capsuleDragging = false;
            double w = ActualWidth > 0 ? ActualWidth : RootGrid.DesiredSize.Width;
            double h = ActualHeight > 0 ? ActualHeight : RootGrid.DesiredSize.Height;
            CommitUserDragPosition(Left, Top, w, h);
        }
        else
        {
            SetExpanded(true);
        }
        e.Handled = true;
    }

    void ExpandedHeader_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) return;
        if (FindParentButton(e.OriginalSource as DependencyObject) != null) return;

        _expandedMouseDown = true;
        _expandedDragging = false;
        _expDragStartScreenDip = ToDipScreenPoint(e.GetPosition(this));
        _expDragStartLeft = Left;
        _expDragStartTop = Top;
        ExpandedHeaderGrid.CaptureMouse();
        e.Handled = true;
    }

    void ExpandedHeader_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_expandedMouseDown || e.LeftButton != MouseButtonState.Pressed) return;
        var cur = ToDipScreenPoint(e.GetPosition(this));
        double dx = cur.X - _expDragStartScreenDip.X;
        double dy = cur.Y - _expDragStartScreenDip.Y;
        if (!_expandedDragging && (Math.Abs(dx) > 5 || Math.Abs(dy) > 5))
            _expandedDragging = true;

        if (_expandedDragging)
        {
            Left = _expDragStartLeft + dx;
            Top = _expDragStartTop + dy;
        }
    }

    void ExpandedHeader_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_expandedMouseDown) return;
        _expandedMouseDown = false;
        if (ExpandedHeaderGrid.IsMouseCaptured)
            ExpandedHeaderGrid.ReleaseMouseCapture();

        if (_expandedDragging)
        {
            _expandedDragging = false;
            double expW = ActualWidth > 0 ? ActualWidth : RootGrid.DesiredSize.Width;
            double expH = ActualHeight > 0 ? ActualHeight : RootGrid.DesiredSize.Height;
            CommitUserDragPosition(Left, Top, expW, expH);
        }
        e.Handled = true;
    }

    WpfPoint ToDipScreenPoint(WpfPoint windowRelDip)
    {
        var px = PointToScreen(windowRelDip);
        var (sx, sy) = GetDpiScales();
        return new WpfPoint(px.X / sx, px.Y / sy);
    }

    static System.Windows.Controls.Button? FindParentButton(DependencyObject? d)
    {
        while (d != null)
        {
            if (d is System.Windows.Controls.Button btn) return btn;
            d = VisualTreeHelper.GetParent(d);
        }
        return null;
    }

    void Widget_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        var menu = new System.Windows.Controls.ContextMenu
        {
            Style = (Style)FindResource("WidgetContextMenuStyle")
        };
        var itemStyle = (Style)FindResource("WidgetMenuItemStyle");

        var miRefresh = new System.Windows.Controls.MenuItem { Header = I18nService.T("Widget.RefreshTip"), Style = itemStyle };
        miRefresh.Click += async (_, _) => await _mainWindow.RefreshFromFloatingWidgetAsync();
        menu.Items.Add(miRefresh);

        var miUsage = new System.Windows.Controls.MenuItem { Header = I18nService.T("Widget.OpenUsageTip"), Style = itemStyle };
        miUsage.Click += (_, _) => _mainWindow.OpenAntigravityUsageMonitorFromWidget();
        menu.Items.Add(miUsage);

        var miMain = new System.Windows.Controls.MenuItem { Header = I18nService.T("Widget.OpenMainTip"), Style = itemStyle };
        miMain.Click += (_, _) => _mainWindow.RestoreAndActivate();
        menu.Items.Add(miMain);

        var miResetPos = new System.Windows.Controls.MenuItem { Header = I18nService.T("Widget.ResetPosition"), Style = itemStyle };
        miResetPos.Click += (_, _) => ResetToBottomRight();
        menu.Items.Add(miResetPos);

        menu.Items.Add(new Separator { Margin = new Thickness(6, 3, 6, 3), Background = BrushRowIdleBorder });

        var miHide = new System.Windows.Controls.MenuItem
        {
            Header = I18nService.T("Tray.HideWidget"),
            Style = itemStyle,
            Foreground = BrushRed
        };
        miHide.Click += (_, _) => _mainWindow.SetFloatingWidgetEnabled(false, showToast: true);
        menu.Items.Add(miHide);

        menu.IsOpen = true;
        e.Handled = true;
    }

    async void OnRefreshClick(object sender, RoutedEventArgs e)
    {
        await _mainWindow.RefreshFromFloatingWidgetAsync();
    }

    void OnOpenUsageClick(object sender, RoutedEventArgs e)
    {
        _mainWindow.OpenAntigravityUsageMonitorFromWidget();
    }

    void OnOpenMainClick(object sender, RoutedEventArgs e)
    {
        _mainWindow.RestoreAndActivate();
    }

    void OnCollapseClick(object sender, RoutedEventArgs e)
    {
        SetExpanded(false);
    }

    void OnToggleModelsClick(object sender, RoutedEventArgs e)
    {
        _isModelsOpen = !_isModelsOpen;
        RenderAll();
    }

    async void OnSwitchAccountClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not WidgetAccountRow row) return;
        if (row.IsCurrent) return;
        await _mainWindow.SwitchProfileFromWidgetAsync(row.Profile);
    }
}

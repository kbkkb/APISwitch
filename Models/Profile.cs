using System.ComponentModel;
using System.Text.Json.Serialization;
using APISwitch.Services;

namespace APISwitch.Models;

public class Profile : INotifyPropertyChanged
{
    public string Name { get; set; } = "";
    public string Email { get; set; } = "";
    public string? Plan { get; set; }
    public string? AuthState { get; set; }
    public DateTime CapturedAtUtc { get; set; }
    public Dictionary<string, string> Values { get; set; } = new();

    // OAuth & Quota Data
    public string? RefreshToken { get; set; }
    public string? AccessToken { get; set; }
    public string? IdToken { get; set; }
    public long? ExpiryTimestamp { get; set; }
    public string? SubscriptionTier { get; set; }
    public bool? IsPaidSubscription { get; set; }

    // 5H Quota (Gemini)
    public double? Quota5hFraction { get; set; }
    public string? Quota5hResetTime { get; set; }

    // Weekly Quota (Gemini)
    public double? QuotaWeeklyFraction { get; set; }
    public string? QuotaWeeklyResetTime { get; set; }

    // Claude / GPT (3P) Quotas
    public double? Quota3p5hFraction { get; set; }
    public string? Quota3p5hResetTime { get; set; }
    public double? Quota3pWeeklyFraction { get; set; }
    public string? Quota3pWeeklyResetTime { get; set; }

    // Gemini 4 Argon Quotas (Independent pool on paid Pro plans)
    public double? QuotaArgon5hFraction { get; set; }
    public string? QuotaArgon5hResetTime { get; set; }
    public double? QuotaArgonWeeklyFraction { get; set; }
    public string? QuotaArgonWeeklyResetTime { get; set; }

    /// <summary>
    /// 前端是否展示独立的 Gemini 4 Argon 配额栏。
    /// 当前默认关闭（false），仅保留双栏展示；若后续 Pro 再次开放独立高级模型额度池，改为 true 即可一键恢复三栏展示。
    /// </summary>
    public static bool ShowArgonQuotaColumn => false;

    public static System.Windows.GridLength ArgonQuotaColumnWidth =>
        ShowArgonQuotaColumn ? new System.Windows.GridLength(1, System.Windows.GridUnitType.Star) : new System.Windows.GridLength(0);

    public static System.Windows.GridLength ArgonSeparatorColumnWidth =>
        ShowArgonQuotaColumn ? new System.Windows.GridLength(16) : new System.Windows.GridLength(0);

    public static System.Windows.Visibility ArgonQuotaColumnVisibility =>
        ShowArgonQuotaColumn ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

    // Virtual Device Profile (sync with storage.json)
    public DeviceProfile? DeviceProfile { get; set; }

    // Quota Updated Time
    public DateTime? QuotaUpdatedAt { get; set; }

    // Per-model detailed quotas (populated by fetchAvailableModels)
    public List<AgModelQuotaInfo>? ModelQuotas { get; set; }

    // Activation Status (Mode A: Google Cloud Code official handshake)
    public bool? IsActivated { get; set; }
    public DateTime? ActivatedAt { get; set; }
    public long? ActivationLatencyMs { get; set; }
    public string? ActivationError { get; set; }

    // 配额用尽重置时间（429 时由 Google 响应捕获，用于倒计时展示）
    public DateTime? QuotaExhaustedResetAt { get; set; }

    // 最近一次激活是否因 429 配额用尽而失败（用于徽章/Toast 区分“用尽”与“真失败”）
    public bool QuotaExhausted { get; set; }

    // 激活中标志（UI 微交互：按钮旋转动画 + 防抖）
    [JsonIgnore]
    public bool IsActivating
    {
        get => _isActivating;
        set { if (_isActivating != value) { _isActivating = value; OnPropertyChanged(nameof(IsActivating)); } }
    }
    private bool _isActivating;

    // 刷新额度中标志（UI 微交互：刷新按钮旋转动画 + 防抖）
    [JsonIgnore]
    public bool IsRefreshingQuota
    {
        get => _isRefreshingQuota;
        set { if (_isRefreshingQuota != value) { _isRefreshingQuota = value; OnPropertyChanged(nameof(IsRefreshingQuota)); } }
    }
    private bool _isRefreshingQuota;

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    [JsonIgnore]
    public bool HasActivationStatus => IsActivated.HasValue;

    [JsonIgnore]
    public string ActivationStatusText
    {
        get
        {
            if (IsActivated == true)
            {
                if (ActivationLatencyMs.HasValue && ActivationLatencyMs.Value > 0)
                    return I18nService.F("Ag.ActivatedLatencyFmt", ActivationLatencyMs.Value);
                return I18nService.T("Ag.Activated");
            }

            // 以实时配额为准判断是否仍处于用尽状态：配额恢复后即使 429 遗留标记未清，
            // 也不应继续显示倒计时（修复"未激活却显示错误倒计时"）。
            double? minRemaining = MinRemainingFraction();
            if (minRemaining.HasValue)
            {
                if (minRemaining.Value <= 0.02)
                {
                    var resetAt = ConstrainingResetUtc();
                    if (resetAt.HasValue)
                    {
                        var left = resetAt.Value - DateTime.UtcNow;
                        if (left.TotalSeconds > 0)
                            return I18nService.F("Ag.ExhaustedCountdownFmt", FormatCountdown(left));
                    }
                }
                // 配额充足（或用尽但拿不到重置时间）→ 不属于用尽态，继续往下走
            }
            else if (QuotaExhaustedResetAt.HasValue)
            {
                // 完全拿不到实时配额时，退回 429 路径记录的标记
                if (QuotaExhaustedResetAt.Value > DateTime.UtcNow)
                    return I18nService.F("Ag.ExhaustedCountdownFmt",
                        FormatCountdown(QuotaExhaustedResetAt.Value - DateTime.UtcNow));
                // 重置时间已过：配额应已恢复，归为待激活而非"失败"
                return I18nService.T("Ag.PendingActivation");
            }

            if (IsActivated == false)
                return I18nService.T("Ag.ActivationFailed");
            return I18nService.T("Ag.PendingActivation");
        }
    }

    /// <summary>各配额桶中最小剩余比例；无任何配额数据返回 null。</summary>
    double? MinRemainingFraction()
    {
        double? min = null;
        void Consider(double? v)
        {
            if (v.HasValue && (min == null || v.Value < min)) min = v;
        }
        Consider(Quota5hFraction);
        Consider(QuotaWeeklyFraction);
        Consider(Quota3p5hFraction);
        Consider(Quota3pWeeklyFraction);
        Consider(QuotaArgon5hFraction);
        Consider(QuotaArgonWeeklyFraction);
        return min;
    }

    /// <summary>
    /// 当前真正接近用尽（≤2%）的桶中，最早的未来重置时间。
    /// 只统计"已用尽"的桶，避免拿仍充裕的桶的重置时间来做倒计时。
    /// </summary>
    internal DateTime? ConstrainingResetUtc()
    {
        DateTime? earliest = null;
        void Consider(double? fraction, string? raw)
        {
            if (!fraction.HasValue || fraction.Value > 0.02) return;
            var dt = ParseResetUtc(raw);
            if (dt.HasValue && dt.Value > DateTime.UtcNow && dt.Value < DateTime.UtcNow.AddDays(8))
                if (earliest == null || dt.Value < earliest) earliest = dt.Value;
        }
        Consider(Quota5hFraction, Quota5hResetTime);
        Consider(QuotaWeeklyFraction, QuotaWeeklyResetTime);
        Consider(Quota3p5hFraction, Quota3p5hResetTime);
        Consider(Quota3pWeeklyFraction, Quota3pWeeklyResetTime);
        Consider(QuotaArgon5hFraction, QuotaArgon5hResetTime);
        Consider(QuotaArgonWeeklyFraction, QuotaArgonWeeklyResetTime);
        return earliest;
    }

    /// <summary>倒计时时长的人类可读格式：≥1天用 d/h，≥1小时用 h/m，否则 m/s。</summary>
    static string FormatCountdown(TimeSpan left)
    {
        if (left.TotalSeconds <= 0) return "";
        if (left.TotalDays >= 1) return $"{(int)left.TotalDays}d {left.Hours}h";
        if (left.TotalHours >= 1) return $"{(int)left.TotalHours}h {left.Minutes}m";
        return $"{left.Minutes}m {left.Seconds}s";
    }

    /// <summary>时区安全的 UTC 解析（与 AgQuotaService.ParseResetUtc 语义一致）。</summary>
    static DateTime? ParseResetUtc(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return null;
        if (DateTime.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var dt))
            return dt;
        return null;
    }

    [JsonIgnore]
    public string ActivationTooltip
    {
        get
        {
            if (IsActivated == true)
            {
                var time = ActivatedAt.HasValue ? ActivatedAt.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "";
                return I18nService.F("Ag.ActTipOkFmt", ActivationLatencyMs ?? 0, time);
            }
            if (IsActivated == false)
            {
                return I18nService.F("Ag.ActTipFailFmt", ActivationError ?? I18nService.T("Ag.ActUnknownError"));
            }
            return I18nService.T("Ag.ActTipPending");
        }
    }

    [JsonIgnore]
    public string FilePath { get; set; } = "";

    [JsonIgnore]
    public bool IsCurrent { get; set; }

    [JsonIgnore]
    public string Initial => string.IsNullOrEmpty(Email) ? "?" : Email.Substring(0, 1).ToUpperInvariant();

    [JsonIgnore]
    public string CapturedAtLocal => CapturedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

    [JsonIgnore]
    public string PlanText => string.IsNullOrEmpty(Plan) ? "—" : Plan;

    [JsonIgnore]
    public bool IsUltra => (SubscriptionTier?.Contains("Ultra", StringComparison.OrdinalIgnoreCase) ?? false)
        || (Plan?.Contains("Ultra", StringComparison.OrdinalIgnoreCase) ?? false);

    [JsonIgnore]
    public bool IsProTier => IsUltra
        || (SubscriptionTier?.Contains("Pro", StringComparison.OrdinalIgnoreCase) ?? false)
        || (Plan?.Contains("Pro", StringComparison.OrdinalIgnoreCase) ?? false);

    [JsonIgnore]
    public bool IsPaidPro => !IsUltra && IsProTier && (IsPaidSubscription == true || HasArgonQuota);

    [JsonIgnore]
    public bool IsPromoPro => !IsUltra && IsProTier && !IsPaidPro;

    [JsonIgnore]
    public bool IsPlus => !IsUltra && !IsProTier
        && ((SubscriptionTier?.Contains("Plus", StringComparison.OrdinalIgnoreCase) ?? false)
            || (Plan?.Contains("Plus", StringComparison.OrdinalIgnoreCase) ?? false));

    [JsonIgnore]
    public string TierDisplay
    {
        get
        {
            if (IsUltra) return I18nService.T("Ag.TierUltra");
            if (IsPaidPro) return I18nService.T("Ag.TierProPaid");
            if (IsPromoPro) return I18nService.T("Ag.TierProPromo");
            if (IsProTier) return I18nService.T("Ag.TierPro");
            if (IsPlus) return I18nService.T("Ag.TierPlus");
            if (!string.IsNullOrEmpty(SubscriptionTier) && !SubscriptionTier.Contains("Starter", StringComparison.OrdinalIgnoreCase))
                return SubscriptionTier;
            return I18nService.T("Ag.TierStarterFree");
        }
    }

    [JsonIgnore]
    public string TierTooltip
    {
        get
        {
            if (IsUltra) return I18nService.T("Ag.TierUltraTooltip");
            if (IsPaidPro) return I18nService.T("Ag.TierProPaidTooltip");
            if (IsPromoPro) return I18nService.T("Ag.TierProPromoTooltip");
            if (IsProTier) return I18nService.T("Ag.TierProTooltip");
            if (IsPlus) return I18nService.T("Ag.TierPlusTooltip");
            return I18nService.T("Ag.TierStarterTooltip");
        }
    }

    [JsonIgnore]
    public string ArgonColumnTooltip
    {
        get
        {
            if (HasArgonQuota) return I18nService.T("Ag.ArgonQuotaTooltip");
            if (IsUltra) return I18nService.T("Ag.ArgonSharedWithGemini");
            return I18nService.T("Ag.ArgonRequiresPaidPro");
        }
    }

    static System.Windows.Media.SolidColorBrush CreateFrozenBrush(byte r, byte g, byte b)
    {
        var brush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    static readonly System.Windows.Media.Brush MutedBadgeFg = CreateFrozenBrush(0x94, 0xA3, 0xB8);
    static readonly System.Windows.Media.Brush IdleBadgeFg = CreateFrozenBrush(0x64, 0x74, 0x8B);
    static readonly System.Windows.Media.Brush IdleBadgeBg = CreateFrozenBrush(0xF1, 0xF5, 0xF9);
    static readonly System.Windows.Media.Brush ReadyBadgeFg = CreateFrozenBrush(0x04, 0x78, 0x57);
    static readonly System.Windows.Media.Brush ReadyBadgeBg = CreateFrozenBrush(0xEC, 0xFD, 0xF5);
    static readonly System.Windows.Media.Brush ExhaustedBadgeFg = CreateFrozenBrush(0xB4, 0x53, 0x09);
    static readonly System.Windows.Media.Brush ExhaustedBadgeBg = CreateFrozenBrush(0xFE, 0xF3, 0xC7);
    static readonly System.Windows.Media.Brush ActiveBadgeFg = CreateFrozenBrush(0x03, 0x69, 0xA1);
    static readonly System.Windows.Media.Brush ActiveBadgeBg = CreateFrozenBrush(0xE0, 0xF2, 0xFE);

    /// <summary>
    /// 判断配额桶是否处于「满额且尚未开始计时」状态：
    /// Google API 对未开始计时的满额桶（100%）会返回动态滑动的整周期占位 resetTime（如 +5h / +7d）。
    /// </summary>
    internal static bool IsTimerStandby(double? fraction, string? resetTimeRaw, DateTime? referenceUtc, TimeSpan windowDuration)
    {
        if (!fraction.HasValue || fraction.Value < 0.9999) return false;
        if (string.IsNullOrEmpty(resetTimeRaw)) return true;
        if (ParseResetUtc(resetTimeRaw) is { } resetUtc)
        {
            var baseUtc = referenceUtc.HasValue ? referenceUtc.Value.ToUniversalTime() : DateTime.UtcNow;
            var spanAtSync = resetUtc.ToUniversalTime() - baseUtc;
            if (spanAtSync >= windowDuration - TimeSpan.FromMinutes(2))
                return true;
        }
        return false;
    }

    static string FormatLocalResetTime(DateTime utcDt)
    {
        var local = utcDt.ToUniversalTime().ToLocalTime();
        var today = DateTime.Now.Date;
        if (local.Date == today)
            return $"{I18nService.T("Ag.TodayPrefix")} {local:HH:mm}";
        if (local.Date == today.AddDays(1))
            return $"{I18nService.T("Ag.TomorrowPrefix")} {local:HH:mm}";
        return local.ToString("MM-dd HH:mm");
    }

    static string FormatLeft5h(TimeSpan diff)
    {
        if (diff.TotalHours >= 1)
            return I18nService.F("Ag.LeftHoursMinFmt", (int)diff.TotalHours, diff.Minutes);
        return I18nService.F("Ag.LeftMinutesFmt", diff.Minutes);
    }

    static string FormatLeftWeekly(TimeSpan diff)
    {
        if (diff.TotalDays >= 1)
            return I18nService.F("Ag.LeftDaysHoursFmt", (int)diff.TotalDays, diff.Hours);
        if (diff.TotalHours >= 1)
            return I18nService.F("Ag.LeftHoursFmt", (int)diff.TotalHours);
        return I18nService.F("Ag.LeftMinFmt", diff.Minutes);
    }

    readonly record struct QuotaBadgeState(
        string Text,
        System.Windows.Media.Brush Fg,
        System.Windows.Media.Brush Bg,
        string? Tooltip);

    QuotaBadgeState EvaluateQuotaBadge(bool isSupported, double? fraction, string? resetTimeRaw, bool is5h, bool isArgon = false)
    {
        if (is5h)
        {
            if (!isSupported || (isArgon && !fraction.HasValue))
                return new QuotaBadgeState(I18nService.T("Ag.No5hLimit"), MutedBadgeFg, System.Windows.Media.Brushes.Transparent, null);
            if (!fraction.HasValue)
                return new QuotaBadgeState(I18nService.T("Ag.NotActivatedShort"), MutedBadgeFg, System.Windows.Media.Brushes.Transparent, null);
            if (IsTimerStandby(fraction, resetTimeRaw, QuotaUpdatedAt, TimeSpan.FromHours(5)))
                return new QuotaBadgeState(
                    I18nService.T("Ag.TimerNotStarted"),
                    IdleBadgeFg,
                    IdleBadgeBg,
                    I18nService.F("Ag.TimerIdleTipFmt", I18nService.T("Ag.Window5h")));
        }
        else
        {
            if (!fraction.HasValue)
            {
                if (isArgon && QuotaUpdatedAt.HasValue)
                {
                    var noArgonText = SubscriptionTier?.Contains("Ultra", StringComparison.OrdinalIgnoreCase) == true
                        ? I18nService.T("Ag.ArgonSharedWithGemini")
                        : I18nService.T("Ag.ArgonNotAvailable");
                    return new QuotaBadgeState(noArgonText, MutedBadgeFg, System.Windows.Media.Brushes.Transparent, null);
                }
                return new QuotaBadgeState("", MutedBadgeFg, System.Windows.Media.Brushes.Transparent, null);
            }
            if (IsTimerStandby(fraction, resetTimeRaw, QuotaUpdatedAt, TimeSpan.FromDays(7)))
                return new QuotaBadgeState(
                    I18nService.T("Ag.TimerNotStarted"),
                    IdleBadgeFg,
                    IdleBadgeBg,
                    I18nService.F("Ag.TimerIdleTipFmt", I18nService.T("Ag.WindowWeekly")));
        }

        if (ParseResetUtc(resetTimeRaw) is { } dt)
        {
            var diff = dt.ToUniversalTime() - DateTime.UtcNow;
            if (diff.TotalSeconds <= 0)
            {
                return new QuotaBadgeState(
                    I18nService.T("Ag.TimerReadyRefresh"),
                    ReadyBadgeFg,
                    ReadyBadgeBg,
                    I18nService.T("Ag.TimerReadyTip"));
            }

            var leftText = is5h ? FormatLeft5h(diff) : FormatLeftWeekly(diff);
            var resetTimeText = FormatLocalResetTime(dt);
            if (fraction.Value <= 0.02)
            {
                return new QuotaBadgeState(
                    leftText,
                    ExhaustedBadgeFg,
                    ExhaustedBadgeBg,
                    I18nService.F("Ag.TimerExhaustedTipFmt", resetTimeText));
            }

            return new QuotaBadgeState(
                leftText,
                ActiveBadgeFg,
                ActiveBadgeBg,
                I18nService.F("Ag.TimerResetAtTipFmt", resetTimeText));
        }

        return new QuotaBadgeState("", MutedBadgeFg, System.Windows.Media.Brushes.Transparent, null);
    }

    [JsonIgnore]
    public bool Has5hQuota => IsProTier && Quota5hFraction.HasValue;

    [JsonIgnore]
    public double Quota5hPercentValue
    {
        get => (IsProTier && Quota5hFraction.HasValue) ? Math.Round(Quota5hFraction.Value * 100.0, 1) : 0.0;
        set { }
    }

    [JsonIgnore]
    public string Quota5hPercentText => (IsProTier && Quota5hFraction.HasValue) ? $"{Math.Round(Quota5hFraction.Value * 100.0)}%" : (IsProTier ? "100%" : "—");

    [JsonIgnore]
    public string Quota5hCountdown => EvaluateQuotaBadge(IsProTier, Quota5hFraction, Quota5hResetTime, is5h: true).Text;
    [JsonIgnore]
    public System.Windows.Media.Brush Quota5hCountdownFg => EvaluateQuotaBadge(IsProTier, Quota5hFraction, Quota5hResetTime, is5h: true).Fg;
    [JsonIgnore]
    public System.Windows.Media.Brush Quota5hCountdownBg => EvaluateQuotaBadge(IsProTier, Quota5hFraction, Quota5hResetTime, is5h: true).Bg;
    [JsonIgnore]
    public string? Quota5hCountdownTooltip => EvaluateQuotaBadge(IsProTier, Quota5hFraction, Quota5hResetTime, is5h: true).Tooltip;
    [JsonIgnore]
    public System.Windows.Visibility Quota5hCountdownVisibility =>
        string.IsNullOrEmpty(Quota5hCountdown) ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;

    [JsonIgnore]
    public double QuotaWeeklyPercentValue
    {
        get => QuotaWeeklyFraction.HasValue ? Math.Round(QuotaWeeklyFraction.Value * 100.0, 1) : (IsProTier ? 100.0 : 0.0);
        set { }
    }

    [JsonIgnore]
    public string QuotaWeeklyPercentText => QuotaWeeklyFraction.HasValue ? $"{Math.Round(QuotaWeeklyFraction.Value * 100.0)}%" : "—";

    [JsonIgnore]
    public string QuotaWeeklyCountdown => EvaluateQuotaBadge(true, QuotaWeeklyFraction, QuotaWeeklyResetTime, is5h: false).Text;
    [JsonIgnore]
    public System.Windows.Media.Brush QuotaWeeklyCountdownFg => EvaluateQuotaBadge(true, QuotaWeeklyFraction, QuotaWeeklyResetTime, is5h: false).Fg;
    [JsonIgnore]
    public System.Windows.Media.Brush QuotaWeeklyCountdownBg => EvaluateQuotaBadge(true, QuotaWeeklyFraction, QuotaWeeklyResetTime, is5h: false).Bg;
    [JsonIgnore]
    public string? QuotaWeeklyCountdownTooltip => EvaluateQuotaBadge(true, QuotaWeeklyFraction, QuotaWeeklyResetTime, is5h: false).Tooltip;
    [JsonIgnore]
    public System.Windows.Visibility QuotaWeeklyCountdownVisibility =>
        string.IsNullOrEmpty(QuotaWeeklyCountdown) ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;

    [JsonIgnore]
    public bool Has3pQuota => Quota3p5hFraction.HasValue || Quota3pWeeklyFraction.HasValue;

    [JsonIgnore]
    public double Quota3p5hPercentValue
    {
        get => (IsProTier && Quota3p5hFraction.HasValue) ? Math.Round(Quota3p5hFraction.Value * 100.0, 1) : 0.0;
        set { }
    }

    [JsonIgnore]
    public string Quota3p5hPercentText => (IsProTier && Quota3p5hFraction.HasValue) ? $"{Math.Round(Quota3p5hFraction.Value * 100.0)}%" : (IsProTier ? "100%" : "—");

    [JsonIgnore]
    public string Quota3p5hCountdown => EvaluateQuotaBadge(IsProTier, Quota3p5hFraction, Quota3p5hResetTime, is5h: true).Text;
    [JsonIgnore]
    public System.Windows.Media.Brush Quota3p5hCountdownFg => EvaluateQuotaBadge(IsProTier, Quota3p5hFraction, Quota3p5hResetTime, is5h: true).Fg;
    [JsonIgnore]
    public System.Windows.Media.Brush Quota3p5hCountdownBg => EvaluateQuotaBadge(IsProTier, Quota3p5hFraction, Quota3p5hResetTime, is5h: true).Bg;
    [JsonIgnore]
    public string? Quota3p5hCountdownTooltip => EvaluateQuotaBadge(IsProTier, Quota3p5hFraction, Quota3p5hResetTime, is5h: true).Tooltip;
    [JsonIgnore]
    public System.Windows.Visibility Quota3p5hCountdownVisibility =>
        string.IsNullOrEmpty(Quota3p5hCountdown) ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;

    [JsonIgnore]
    public double Quota3pWeeklyPercentValue
    {
        get => Quota3pWeeklyFraction.HasValue ? Math.Round(Quota3pWeeklyFraction.Value * 100.0, 1) : (IsProTier ? 100.0 : 0.0);
        set { }
    }

    [JsonIgnore]
    public string Quota3pWeeklyPercentText => Quota3pWeeklyFraction.HasValue ? $"{Math.Round(Quota3pWeeklyFraction.Value * 100.0)}%" : "—";

    [JsonIgnore]
    public string Quota3pWeeklyCountdown => EvaluateQuotaBadge(true, Quota3pWeeklyFraction, Quota3pWeeklyResetTime, is5h: false).Text;
    [JsonIgnore]
    public System.Windows.Media.Brush Quota3pWeeklyCountdownFg => EvaluateQuotaBadge(true, Quota3pWeeklyFraction, Quota3pWeeklyResetTime, is5h: false).Fg;
    [JsonIgnore]
    public System.Windows.Media.Brush Quota3pWeeklyCountdownBg => EvaluateQuotaBadge(true, Quota3pWeeklyFraction, Quota3pWeeklyResetTime, is5h: false).Bg;
    [JsonIgnore]
    public string? Quota3pWeeklyCountdownTooltip => EvaluateQuotaBadge(true, Quota3pWeeklyFraction, Quota3pWeeklyResetTime, is5h: false).Tooltip;
    [JsonIgnore]
    public System.Windows.Visibility Quota3pWeeklyCountdownVisibility =>
        string.IsNullOrEmpty(Quota3pWeeklyCountdown) ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;

    [JsonIgnore]
    public bool HasArgonQuota => QuotaArgon5hFraction.HasValue || QuotaArgonWeeklyFraction.HasValue;

    [JsonIgnore]
    public double QuotaArgon5hPercentValue
    {
        get => (IsProTier && QuotaArgon5hFraction.HasValue) ? Math.Round(QuotaArgon5hFraction.Value * 100.0, 1) : 0.0;
        set { }
    }

    [JsonIgnore]
    public string QuotaArgon5hPercentText => (IsProTier && QuotaArgon5hFraction.HasValue) ? $"{Math.Round(QuotaArgon5hFraction.Value * 100.0)}%" : "—";

    [JsonIgnore]
    public string QuotaArgon5hCountdown => EvaluateQuotaBadge(IsProTier, QuotaArgon5hFraction, QuotaArgon5hResetTime, is5h: true, isArgon: true).Text;
    [JsonIgnore]
    public System.Windows.Media.Brush QuotaArgon5hCountdownFg => EvaluateQuotaBadge(IsProTier, QuotaArgon5hFraction, QuotaArgon5hResetTime, is5h: true, isArgon: true).Fg;
    [JsonIgnore]
    public System.Windows.Media.Brush QuotaArgon5hCountdownBg => EvaluateQuotaBadge(IsProTier, QuotaArgon5hFraction, QuotaArgon5hResetTime, is5h: true, isArgon: true).Bg;
    [JsonIgnore]
    public string? QuotaArgon5hCountdownTooltip => EvaluateQuotaBadge(IsProTier, QuotaArgon5hFraction, QuotaArgon5hResetTime, is5h: true, isArgon: true).Tooltip;
    [JsonIgnore]
    public System.Windows.Visibility QuotaArgon5hCountdownVisibility =>
        string.IsNullOrEmpty(QuotaArgon5hCountdown) ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;

    [JsonIgnore]
    public double QuotaArgonWeeklyPercentValue
    {
        get => QuotaArgonWeeklyFraction.HasValue ? Math.Round(QuotaArgonWeeklyFraction.Value * 100.0, 1) : 0.0;
        set { }
    }

    [JsonIgnore]
    public string QuotaArgonWeeklyPercentText => QuotaArgonWeeklyFraction.HasValue ? $"{Math.Round(QuotaArgonWeeklyFraction.Value * 100.0)}%" : "—";

    [JsonIgnore]
    public string QuotaArgonWeeklyCountdown => EvaluateQuotaBadge(true, QuotaArgonWeeklyFraction, QuotaArgonWeeklyResetTime, is5h: false, isArgon: true).Text;
    [JsonIgnore]
    public System.Windows.Media.Brush QuotaArgonWeeklyCountdownFg => EvaluateQuotaBadge(true, QuotaArgonWeeklyFraction, QuotaArgonWeeklyResetTime, is5h: false, isArgon: true).Fg;
    [JsonIgnore]
    public System.Windows.Media.Brush QuotaArgonWeeklyCountdownBg => EvaluateQuotaBadge(true, QuotaArgonWeeklyFraction, QuotaArgonWeeklyResetTime, is5h: false, isArgon: true).Bg;
    [JsonIgnore]
    public string? QuotaArgonWeeklyCountdownTooltip => EvaluateQuotaBadge(true, QuotaArgonWeeklyFraction, QuotaArgonWeeklyResetTime, is5h: false, isArgon: true).Tooltip;
    [JsonIgnore]
    public System.Windows.Visibility QuotaArgonWeeklyCountdownVisibility =>
        string.IsNullOrEmpty(QuotaArgonWeeklyCountdown) ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;

    [JsonIgnore]
    public string QuotaUpdatedText
    {
        get
        {
            if (!QuotaUpdatedAt.HasValue) return I18nService.T("Ag.NotSynced");
            var diff = DateTime.UtcNow - QuotaUpdatedAt.Value;
            if (diff.TotalMinutes < 1) return I18nService.T("Ag.UpdatedJustNow");
            if (diff.TotalHours < 1) return I18nService.F("Ag.UpdatedMinutesAgoFmt", (int)diff.TotalMinutes);
            if (diff.TotalDays < 1) return I18nService.F("Ag.UpdatedAtTimeFmt", QuotaUpdatedAt.Value.ToLocalTime().ToString("HH:mm"));
            return I18nService.F("Ag.UpdatedAtTimeFmt", QuotaUpdatedAt.Value.ToLocalTime().ToString("MM-dd HH:mm"));
        }
    }
}

public class DeviceProfile
{
    public string? MachineId { get; set; }
    public string? MacMachineId { get; set; }
    public string? DevDeviceId { get; set; }
    public string? SqmId { get; set; }
}

/// <summary>Antigravity 单模型实时配额与能力快照（源自 fetchAvailableModels）。</summary>
public class AgModelQuotaInfo
{
    public string ModelId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public double? RemainingFraction { get; set; }
    public string? ResetTime { get; set; }
    public bool Recommended { get; set; }
    public bool Disabled { get; set; }
    public bool SupportsThinking { get; set; }
    public bool SupportsImages { get; set; }
    public int MaxTokens { get; set; }
    public int MaxOutputTokens { get; set; }
}

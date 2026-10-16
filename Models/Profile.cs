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

    // Virtual Device Profile (sync with storage.json)
    public DeviceProfile? DeviceProfile { get; set; }

    // Quota Updated Time
    public DateTime? QuotaUpdatedAt { get; set; }

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
    public string TierDisplay
    {
        get
        {
            if (IsUltra) return I18nService.T("Ag.TierUltra");
            if (IsPaidPro) return I18nService.T("Ag.TierProPaid");
            if (IsPromoPro) return I18nService.T("Ag.TierProPromo");
            if (IsProTier) return I18nService.T("Ag.TierPro");
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
    public string Quota5hCountdown
    {
        get
        {
            if (!IsProTier) return I18nService.T("Ag.No5hLimit");
            if (!Quota5hFraction.HasValue) return I18nService.T("Ag.NotActivatedShort");
            if (string.IsNullOrEmpty(Quota5hResetTime)) return I18nService.T("Ag.FullStandby");
            if (ParseResetUtc(Quota5hResetTime) is { } dt)
            {
                var diff = dt.ToUniversalTime() - DateTime.UtcNow;
                if (diff.TotalSeconds <= 0) return I18nService.T("Ag.ResetDone");
                if (diff.TotalHours >= 1)
                    return I18nService.F("Ag.LeftHoursMinFmt", (int)diff.TotalHours, diff.Minutes);
                return I18nService.F("Ag.LeftMinutesFmt", diff.Minutes);
            }
            return "";
        }
    }

    [JsonIgnore]
    public double QuotaWeeklyPercentValue
    {
        get => QuotaWeeklyFraction.HasValue ? Math.Round(QuotaWeeklyFraction.Value * 100.0, 1) : (IsProTier ? 100.0 : 0.0);
        set { }
    }

    [JsonIgnore]
    public string QuotaWeeklyPercentText => QuotaWeeklyFraction.HasValue ? $"{Math.Round(QuotaWeeklyFraction.Value * 100.0)}%" : "—";

    [JsonIgnore]
    public string QuotaWeeklyCountdown
    {
        get
        {
            if (string.IsNullOrEmpty(QuotaWeeklyResetTime)) return "";
            if (ParseResetUtc(QuotaWeeklyResetTime) is { } dt)
            {
                var diff = dt.ToUniversalTime() - DateTime.UtcNow;
                if (diff.TotalSeconds <= 0) return I18nService.T("Ag.RefreshedDone");
                if (diff.TotalDays >= 1)
                    return I18nService.F("Ag.LeftDaysHoursFmt", (int)diff.TotalDays, diff.Hours);
                if (diff.TotalHours >= 1)
                    return I18nService.F("Ag.LeftHoursFmt", (int)diff.TotalHours);
                return I18nService.F("Ag.LeftMinFmt", diff.Minutes);
            }
            return "";
        }
    }

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
    public string Quota3p5hCountdown
    {
        get
        {
            if (!IsProTier) return I18nService.T("Ag.No5hLimit");
            if (!Quota3p5hFraction.HasValue) return I18nService.T("Ag.NotActivatedShort");
            if (string.IsNullOrEmpty(Quota3p5hResetTime)) return I18nService.T("Ag.FullStandby");
            if (ParseResetUtc(Quota3p5hResetTime) is { } dt)
            {
                var diff = dt.ToUniversalTime() - DateTime.UtcNow;
                if (diff.TotalSeconds <= 0) return I18nService.T("Ag.ResetDone");
                if (diff.TotalHours >= 1) return I18nService.F("Ag.LeftHoursMinFmt", (int)diff.TotalHours, diff.Minutes);
                return I18nService.F("Ag.LeftMinutesFmt", diff.Minutes);
            }
            return "";
        }
    }

    [JsonIgnore]
    public double Quota3pWeeklyPercentValue
    {
        get => Quota3pWeeklyFraction.HasValue ? Math.Round(Quota3pWeeklyFraction.Value * 100.0, 1) : (IsProTier ? 100.0 : 0.0);
        set { }
    }

    [JsonIgnore]
    public string Quota3pWeeklyPercentText => Quota3pWeeklyFraction.HasValue ? $"{Math.Round(Quota3pWeeklyFraction.Value * 100.0)}%" : "—";

    [JsonIgnore]
    public string Quota3pWeeklyCountdown
    {
        get
        {
            if (string.IsNullOrEmpty(Quota3pWeeklyResetTime)) return "";
            if (ParseResetUtc(Quota3pWeeklyResetTime) is { } dt)
            {
                var diff = dt.ToUniversalTime() - DateTime.UtcNow;
                if (diff.TotalSeconds <= 0) return I18nService.T("Ag.RefreshedDone");
                if (diff.TotalDays >= 1) return I18nService.F("Ag.LeftDaysHoursFmt", (int)diff.TotalDays, diff.Hours);
                if (diff.TotalHours >= 1) return I18nService.F("Ag.LeftHoursFmt", (int)diff.TotalHours);
                return I18nService.F("Ag.LeftMinFmt", diff.Minutes);
            }
            return "";
        }
    }

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
    public string QuotaArgon5hCountdown
    {
        get
        {
            if (!QuotaArgon5hFraction.HasValue) return I18nService.T("Ag.No5hLimit");
            if (string.IsNullOrEmpty(QuotaArgon5hResetTime)) return I18nService.T("Ag.FullStandby");
            if (ParseResetUtc(QuotaArgon5hResetTime) is { } dt)
            {
                var diff = dt.ToUniversalTime() - DateTime.UtcNow;
                if (diff.TotalSeconds <= 0) return I18nService.T("Ag.ResetDone");
                if (diff.TotalHours >= 1) return I18nService.F("Ag.LeftHoursMinFmt", (int)diff.TotalHours, diff.Minutes);
                return I18nService.F("Ag.LeftMinutesFmt", diff.Minutes);
            }
            return "";
        }
    }

    [JsonIgnore]
    public double QuotaArgonWeeklyPercentValue
    {
        get => QuotaArgonWeeklyFraction.HasValue ? Math.Round(QuotaArgonWeeklyFraction.Value * 100.0, 1) : 0.0;
        set { }
    }

    [JsonIgnore]
    public string QuotaArgonWeeklyPercentText => QuotaArgonWeeklyFraction.HasValue ? $"{Math.Round(QuotaArgonWeeklyFraction.Value * 100.0)}%" : "—";

    [JsonIgnore]
    public string QuotaArgonWeeklyCountdown
    {
        get
        {
            if (!string.IsNullOrEmpty(QuotaArgonWeeklyResetTime) && ParseResetUtc(QuotaArgonWeeklyResetTime) is { } dt)
            {
                var diff = dt.ToUniversalTime() - DateTime.UtcNow;
                if (diff.TotalSeconds <= 0) return I18nService.T("Ag.RefreshedDone");
                if (diff.TotalDays >= 1) return I18nService.F("Ag.LeftDaysHoursFmt", (int)diff.TotalDays, diff.Hours);
                if (diff.TotalHours >= 1) return I18nService.F("Ag.LeftHoursFmt", (int)diff.TotalHours);
                return I18nService.F("Ag.LeftMinFmt", diff.Minutes);
            }
            if (!QuotaArgonWeeklyFraction.HasValue && QuotaUpdatedAt.HasValue)
            {
                if (SubscriptionTier?.Contains("Ultra", StringComparison.OrdinalIgnoreCase) == true)
                    return I18nService.T("Ag.ArgonSharedWithGemini");
                return I18nService.T("Ag.ArgonNotAvailable");
            }
            return "";
        }
    }

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

using System.Text.Json.Serialization;
using APISwitch.Services;

namespace APISwitch.Models;

public class Profile
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

    // Virtual Device Profile (sync with storage.json)
    public DeviceProfile? DeviceProfile { get; set; }

    // Quota Updated Time
    public DateTime? QuotaUpdatedAt { get; set; }

    // Activation Status (Mode A: Google Cloud Code official handshake)
    public bool? IsActivated { get; set; }
    public DateTime? ActivatedAt { get; set; }
    public long? ActivationLatencyMs { get; set; }
    public string? ActivationError { get; set; }

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
            if (IsActivated == false)
            {
                return I18nService.T("Ag.ActivationFailed");
            }
            return I18nService.T("Ag.PendingActivation");
        }
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
    public string TierDisplay
    {
        get
        {
            if (!string.IsNullOrEmpty(SubscriptionTier))
            {
                if (SubscriptionTier.Contains("Starter", StringComparison.OrdinalIgnoreCase)) return I18nService.T("Ag.TierStarterFree");
                if (SubscriptionTier.Contains("Pro", StringComparison.OrdinalIgnoreCase)) return I18nService.T("Ag.TierPro");
                if (SubscriptionTier.Contains("Ultra", StringComparison.OrdinalIgnoreCase)) return I18nService.T("Ag.TierUltra");
                return SubscriptionTier;
            }
            if (!string.IsNullOrEmpty(Plan))
            {
                if (Plan.Contains("Starter", StringComparison.OrdinalIgnoreCase)) return I18nService.T("Ag.TierStarterFree");
                if (Plan.Contains("Pro", StringComparison.OrdinalIgnoreCase)) return I18nService.T("Ag.TierPro");
                return Plan;
            }
            return I18nService.T("Ag.TierStarterFree");
        }
    }

    [JsonIgnore]
    public bool IsProTier => (SubscriptionTier?.Contains("Pro", StringComparison.OrdinalIgnoreCase) ?? false)
        || (SubscriptionTier?.Contains("Ultra", StringComparison.OrdinalIgnoreCase) ?? false)
        || (Plan?.Contains("Pro", StringComparison.OrdinalIgnoreCase) ?? false);

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
            if (DateTime.TryParse(Quota5hResetTime, out var dt))
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
            if (DateTime.TryParse(QuotaWeeklyResetTime, out var dt))
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
            if (DateTime.TryParse(Quota3p5hResetTime, out var dt))
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
            if (DateTime.TryParse(Quota3pWeeklyResetTime, out var dt))
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

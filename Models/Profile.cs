using System.Text.Json.Serialization;

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
                    return $"● 已激活 ({ActivationLatencyMs.Value}ms)";
                return "● 已激活";
            }
            if (IsActivated == false)
            {
                return "● 激活失败";
            }
            return "○ 待激活";
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
                return $"官方协议握手激活成功\n响应耗时: {ActivationLatencyMs}ms\n激活时间: {time}";
            }
            if (IsActivated == false)
            {
                return $"握手失败: {ActivationError ?? "未知错误"}";
            }
            return "尚未执行握手激活，可点击卡片右侧「激活」或顶部「批量激活」";
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
                if (SubscriptionTier.Contains("Starter", StringComparison.OrdinalIgnoreCase)) return "Starter 免费版";
                if (SubscriptionTier.Contains("Pro", StringComparison.OrdinalIgnoreCase)) return "Google AI Pro";
                if (SubscriptionTier.Contains("Ultra", StringComparison.OrdinalIgnoreCase)) return "Google AI Ultra";
                return SubscriptionTier;
            }
            if (!string.IsNullOrEmpty(Plan))
            {
                if (Plan.Contains("Starter", StringComparison.OrdinalIgnoreCase)) return "Starter 免费版";
                if (Plan.Contains("Pro", StringComparison.OrdinalIgnoreCase)) return "Google AI Pro";
                return Plan;
            }
            return "Starter 免费版";
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
            if (!IsProTier) return "免费版无5H限制";
            if (!Quota5hFraction.HasValue) return "未激活";
            if (string.IsNullOrEmpty(Quota5hResetTime)) return "满额待命";
            if (DateTime.TryParse(Quota5hResetTime, out var dt))
            {
                var diff = dt.ToUniversalTime() - DateTime.UtcNow;
                if (diff.TotalSeconds <= 0) return "已重置";
                if (diff.TotalHours >= 1)
                    return $"剩 {(int)diff.TotalHours}h {diff.Minutes}m";
                return $"剩 {diff.Minutes}m";
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
                if (diff.TotalSeconds <= 0) return "已刷新";
                if (diff.TotalDays >= 1)
                    return $"剩 {(int)diff.TotalDays}天{diff.Hours}小时";
                if (diff.TotalHours >= 1)
                    return $"剩 {(int)diff.TotalHours}小时";
                return $"剩 {diff.Minutes}分钟";
            }
            return "";
        }
    }

    [JsonIgnore]
    public bool Has3pQuota => IsProTier && (Quota3p5hFraction.HasValue || Quota3pWeeklyFraction.HasValue);

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
            if (!IsProTier) return "免费版仅限Gemini";
            if (!Quota3p5hFraction.HasValue) return "未激活";
            if (string.IsNullOrEmpty(Quota3p5hResetTime)) return "满额待命";
            if (DateTime.TryParse(Quota3p5hResetTime, out var dt))
            {
                var diff = dt.ToUniversalTime() - DateTime.UtcNow;
                if (diff.TotalSeconds <= 0) return "已重置";
                if (diff.TotalHours >= 1) return $"剩 {(int)diff.TotalHours}h {diff.Minutes}m";
                return $"剩 {diff.Minutes}m";
            }
            return "";
        }
    }

    [JsonIgnore]
    public double Quota3pWeeklyPercentValue
    {
        get => (IsProTier && Quota3pWeeklyFraction.HasValue) ? Math.Round(Quota3pWeeklyFraction.Value * 100.0, 1) : 0.0;
        set { }
    }

    [JsonIgnore]
    public string Quota3pWeeklyPercentText => (IsProTier && Quota3pWeeklyFraction.HasValue) ? $"{Math.Round(Quota3pWeeklyFraction.Value * 100.0)}%" : (IsProTier ? "100%" : "—");

    [JsonIgnore]
    public string Quota3pWeeklyCountdown
    {
        get
        {
            if (!IsProTier) return "仅Pro订阅可用";
            if (string.IsNullOrEmpty(Quota3pWeeklyResetTime)) return "";
            if (DateTime.TryParse(Quota3pWeeklyResetTime, out var dt))
            {
                var diff = dt.ToUniversalTime() - DateTime.UtcNow;
                if (diff.TotalSeconds <= 0) return "已刷新";
                if (diff.TotalDays >= 1) return $"剩 {(int)diff.TotalDays}天{diff.Hours}小时";
                if (diff.TotalHours >= 1) return $"剩 {(int)diff.TotalHours}小时";
                return $"剩 {diff.Minutes}分钟";
            }
            return "";
        }
    }

    [JsonIgnore]
    public string QuotaUpdatedText
    {
        get
        {
            if (!QuotaUpdatedAt.HasValue) return "未同步";
            var diff = DateTime.UtcNow - QuotaUpdatedAt.Value;
            if (diff.TotalMinutes < 1) return "刚刚更新";
            if (diff.TotalHours < 1) return $"{(int)diff.TotalMinutes}分钟前更新";
            if (diff.TotalDays < 1) return QuotaUpdatedAt.Value.ToLocalTime().ToString("HH:mm") + " 更新";
            return QuotaUpdatedAt.Value.ToLocalTime().ToString("MM-dd HH:mm") + " 更新";
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

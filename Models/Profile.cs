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
    /// <summary>用户设定的订阅起始日期（本地日期），用于按月统计订阅周期。</summary>
    public DateTime? SubscriptionStartDate { get; set; }
    /// <summary>每月订阅账单结算锚点日（1-31）；未设置时自动按 SubscriptionStartDate 或首次记录日期推算。</summary>
    public int? BillingCycleDay { get; set; }

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
    public List<AgModelQuotaInfo>? ModelQuotas
    {
        get => _modelQuotas;
        set { _modelQuotas = value; _cachedMergedModels = null; }
    }
    private List<AgModelQuotaInfo>? _modelQuotas;
    private List<ProfileModelChip>? _cachedMergedModels;

    /// <summary>清空模型目录缓存并批量通知 UI 属性刷新（不重建 ListBoxItem 容器，完整保留折叠展开态）。</summary>
    public void NotifyQuotaUiChanged()
    {
        _cachedMergedModels = null;
        OnPropertyChanged(string.Empty);
    }

    /// <summary>原地同步来自磁盘/接口的账号元数据与配额，保留 UI 交互态（展开、旋转动画等）。</summary>
    public void UpdateFrom(Profile src)
    {
        Name = src.Name;
        Email = src.Email;
        Plan = src.Plan;
        AuthState = src.AuthState;
        CapturedAtUtc = src.CapturedAtUtc;
        Values = src.Values;
        RefreshToken = src.RefreshToken;
        AccessToken = src.AccessToken;
        IdToken = src.IdToken;
        ExpiryTimestamp = src.ExpiryTimestamp;
        SubscriptionTier = src.SubscriptionTier;
        IsPaidSubscription = src.IsPaidSubscription;
        if (src.SubscriptionStartDate.HasValue) SubscriptionStartDate = src.SubscriptionStartDate;
        if (src.BillingCycleDay.HasValue) BillingCycleDay = src.BillingCycleDay;
        Quota5hFraction = src.Quota5hFraction;
        Quota5hResetTime = src.Quota5hResetTime;
        QuotaWeeklyFraction = src.QuotaWeeklyFraction;
        QuotaWeeklyResetTime = src.QuotaWeeklyResetTime;
        Quota3p5hFraction = src.Quota3p5hFraction;
        Quota3p5hResetTime = src.Quota3p5hResetTime;
        Quota3pWeeklyFraction = src.Quota3pWeeklyFraction;
        Quota3pWeeklyResetTime = src.Quota3pWeeklyResetTime;
        QuotaArgon5hFraction = src.QuotaArgon5hFraction;
        QuotaArgon5hResetTime = src.QuotaArgon5hResetTime;
        QuotaArgonWeeklyFraction = src.QuotaArgonWeeklyFraction;
        QuotaArgonWeeklyResetTime = src.QuotaArgonWeeklyResetTime;
        DeviceProfile = src.DeviceProfile;
        QuotaUpdatedAt = src.QuotaUpdatedAt;
        ModelQuotas = src.ModelQuotas;
        IsActivated = src.IsActivated;
        ActivatedAt = src.ActivatedAt;
        GeminiActivatedAt = src.GeminiActivatedAt;
        ThirdPartyActivatedAt = src.ThirdPartyActivatedAt;
        ArgonActivatedAt = src.ArgonActivatedAt;
        ActivationLatencyMs = src.ActivationLatencyMs;
        ActivationError = src.ActivationError;
        QuotaExhaustedResetAt = src.QuotaExhaustedResetAt;
        QuotaExhausted = src.QuotaExhausted;
        IsCurrent = src.IsCurrent;
        if (!string.IsNullOrEmpty(src.FilePath)) FilePath = src.FilePath;
        NotifyQuotaUiChanged();
    }

    // Activation Status (Mode A: Google Cloud Code official handshake)
    public bool? IsActivated { get; set; }
    public DateTime? ActivatedAt { get; set; }
    public DateTime? GeminiActivatedAt { get; set; }
    public DateTime? ThirdPartyActivatedAt { get; set; }
    public DateTime? ArgonActivatedAt { get; set; }
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

    // 可用模型折叠面板展开状态
    [JsonIgnore]
    public bool IsModelsExpanded
    {
        get => _isModelsExpanded;
        set
        {
            if (_isModelsExpanded != value)
            {
                _isModelsExpanded = value;
                OnPropertyChanged(nameof(IsModelsExpanded));
                OnPropertyChanged(nameof(ModelsDrawerVisibility));
            }
        }
    }
    private bool _isModelsExpanded;

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

    public readonly record struct PoolActivationPlan(
        bool NeedGemini,
        bool NeedThirdParty,
        bool NeedArgon,
        bool GeminiAlreadyActive,
        bool ThirdPartyAlreadyActive,
        bool ArgonAlreadyActive)
    {
        public bool NeedsAnyCliCall => NeedGemini || NeedThirdParty || NeedArgon;
        public bool AllSupportedAlreadyActive => !NeedsAnyCliCall && (GeminiAlreadyActive || ThirdPartyAlreadyActive || ArgonAlreadyActive);

        public string DescribePendingPools()
        {
            var parts = new List<string>(3);
            if (NeedGemini) parts.Add("Gemini");
            if (NeedArgon) parts.Add("Argon");
            if (NeedThirdParty) parts.Add("Claude/GPT");
            return string.Join(" + ", parts);
        }
    }

    bool IsCycleStarted(double? fraction, string? resetRaw, TimeSpan window, DateTime? poolActivatedAt, DateTime nowUtc)
    {
        if (!fraction.HasValue) return false;
        var resetUtc = ParseResetUtc(resetRaw);
        if (!resetUtc.HasValue || resetUtc.Value <= nowUtc) return false;

        var refUtc = QuotaUpdatedAt.HasValue && QuotaUpdatedAt.Value <= nowUtc
            ? QuotaUpdatedAt.Value
            : nowUtc;

        if (!IsTimerStandby(fraction, resetRaw, refUtc, window))
            return true;

        if (poolActivatedAt.HasValue)
        {
            var actUtc = poolActivatedAt.Value.ToUniversalTime();
            var cycleStartUtc = resetUtc.Value.ToUniversalTime() - window - TimeSpan.FromSeconds(45);
            if (actUtc >= cycleStartUtc && actUtc <= nowUtc + TimeSpan.FromMinutes(1))
                return true;
        }

        return false;
    }

    /// <summary>
    /// 按配额池评估当前账号哪些池尚未开始计时（需要发消息激活），哪些池已在计时或已耗尽等待重置（直接跳过）。
    /// 非 Pro 账号（Starter 免费版 / Plus）一律以 7 天周限额周期为准，避免因附加的 3p-5h 到期而误触发周限额重复激活。
    /// </summary>
    public PoolActivationPlan EvaluatePoolActivationNeed(DateTime? nowUtcOverride = null)
    {
        var nowUtc = nowUtcOverride ?? DateTime.UtcNow;
        bool track5hWindow = IsProTier;

        // 1. Gemini 池
        bool geminiActive;
        bool needGemini;
        if (QuotaWeeklyFraction.HasValue && QuotaWeeklyFraction.Value <= 0.02 &&
            ParseResetUtc(QuotaWeeklyResetTime) is { } gw && gw > nowUtc)
        {
            geminiActive = true;
            needGemini = false;
        }
        else if (track5hWindow && Quota5hFraction.HasValue)
        {
            geminiActive = IsCycleStarted(Quota5hFraction, Quota5hResetTime, TimeSpan.FromHours(5), GeminiActivatedAt, nowUtc);
            needGemini = !geminiActive;
        }
        else if (QuotaWeeklyFraction.HasValue)
        {
            geminiActive = IsCycleStarted(QuotaWeeklyFraction, QuotaWeeklyResetTime, TimeSpan.FromDays(7), GeminiActivatedAt, nowUtc);
            needGemini = !geminiActive;
        }
        else if (Quota5hFraction.HasValue)
        {
            geminiActive = IsCycleStarted(Quota5hFraction, Quota5hResetTime, TimeSpan.FromHours(5), GeminiActivatedAt, nowUtc);
            needGemini = !geminiActive;
        }
        else
        {
            geminiActive = false;
            needGemini = true;
        }

        // 2. Claude/GPT (3P) 池
        bool supports3p = _modelQuotas != null && _modelQuotas.Count > 0
            ? _modelQuotas.Any(m => !m.Disabled &&
                (m.ModelId.Contains("claude", StringComparison.OrdinalIgnoreCase) ||
                 m.ModelId.Contains("gpt", StringComparison.OrdinalIgnoreCase)))
            : (Has3pQuota || !QuotaUpdatedAt.HasValue);

        bool thirdPartyActive = false;
        bool needThirdParty = false;
        if (supports3p)
        {
            if ((Quota3pWeeklyFraction.HasValue && Quota3pWeeklyFraction.Value <= 0.02 &&
                 ParseResetUtc(Quota3pWeeklyResetTime) is { } p3w && p3w > nowUtc) ||
                (!track5hWindow && Quota3p5hFraction.HasValue && Quota3p5hFraction.Value <= 0.02 &&
                 ParseResetUtc(Quota3p5hResetTime) is { } p3h && p3h > nowUtc))
            {
                thirdPartyActive = true;
                needThirdParty = false;
            }
            else if (track5hWindow && Quota3p5hFraction.HasValue)
            {
                thirdPartyActive = IsCycleStarted(Quota3p5hFraction, Quota3p5hResetTime, TimeSpan.FromHours(5), ThirdPartyActivatedAt, nowUtc);
                needThirdParty = !thirdPartyActive;
            }
            else if (Quota3pWeeklyFraction.HasValue)
            {
                thirdPartyActive = IsCycleStarted(Quota3pWeeklyFraction, Quota3pWeeklyResetTime, TimeSpan.FromDays(7), ThirdPartyActivatedAt, nowUtc);
                needThirdParty = !thirdPartyActive;
            }
            else if (Quota3p5hFraction.HasValue)
            {
                thirdPartyActive = IsCycleStarted(Quota3p5hFraction, Quota3p5hResetTime, TimeSpan.FromHours(5), ThirdPartyActivatedAt, nowUtc);
                needThirdParty = !thirdPartyActive;
            }
            else if (ThirdPartyActivatedAt.HasValue &&
                     (nowUtc - ThirdPartyActivatedAt.Value.ToUniversalTime()) < (track5hWindow ? TimeSpan.FromHours(5) : TimeSpan.FromDays(7)))
            {
                thirdPartyActive = true;
                needThirdParty = false;
            }
            else
            {
                var m3p = _modelQuotas?.FirstOrDefault(m =>
                    !m.Disabled && m.RemainingFraction.HasValue &&
                    (m.ModelId.Contains("claude", StringComparison.OrdinalIgnoreCase) ||
                     m.ModelId.Contains("gpt", StringComparison.OrdinalIgnoreCase)));
                if (m3p != null && ParseResetUtc(m3p.ResetTime) is { } mr && mr > nowUtc)
                {
                    var win = (mr - nowUtc).TotalHours <= 6 ? TimeSpan.FromHours(5) : TimeSpan.FromDays(7);
                    thirdPartyActive = IsCycleStarted(m3p.RemainingFraction, m3p.ResetTime, win, ThirdPartyActivatedAt, nowUtc);
                    needThirdParty = !thirdPartyActive;
                }
                else
                {
                    thirdPartyActive = false;
                    needThirdParty = true;
                }
            }
        }

        // 3. 独立 Argon 池（仅当存在不与 Gemini 共用周期的独立 Argon 桶且开关开启时）
        bool argonActive = false;
        bool needArgon = false;
        if (ShowArgonQuotaColumn && HasArgonQuota &&
            !string.Equals(QuotaArgon5hResetTime ?? QuotaArgonWeeklyResetTime, Quota5hResetTime ?? QuotaWeeklyResetTime, StringComparison.Ordinal))
        {
            if (QuotaArgonWeeklyFraction.HasValue && QuotaArgonWeeklyFraction.Value <= 0.02 &&
                ParseResetUtc(QuotaArgonWeeklyResetTime) is { } aw && aw > nowUtc)
            {
                argonActive = true;
                needArgon = false;
            }
            else if (track5hWindow && QuotaArgon5hFraction.HasValue)
            {
                argonActive = IsCycleStarted(QuotaArgon5hFraction, QuotaArgon5hResetTime, TimeSpan.FromHours(5), ArgonActivatedAt, nowUtc);
                needArgon = !argonActive;
            }
            else if (QuotaArgonWeeklyFraction.HasValue)
            {
                argonActive = IsCycleStarted(QuotaArgonWeeklyFraction, QuotaArgonWeeklyResetTime, TimeSpan.FromDays(7), ArgonActivatedAt, nowUtc);
                needArgon = !argonActive;
            }
        }

        return new PoolActivationPlan(needGemini, needThirdParty, needArgon, geminiActive, thirdPartyActive, argonActive);
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

    QuotaBadgeState EvaluateQuotaBadge(bool isSupported, double? fraction, string? resetTimeRaw, bool is5h, bool isArgon = false, DateTime? poolActivatedAt = null)
    {
        if (is5h)
        {
            if (!isSupported || (isArgon && !fraction.HasValue))
                return new QuotaBadgeState(I18nService.T("Ag.No5hLimit"), MutedBadgeFg, System.Windows.Media.Brushes.Transparent, null);
            if (!fraction.HasValue)
                return new QuotaBadgeState(I18nService.T("Ag.NotActivatedShort"), MutedBadgeFg, System.Windows.Media.Brushes.Transparent, null);
            if (!IsCycleStarted(fraction, resetTimeRaw, TimeSpan.FromHours(5), poolActivatedAt, DateTime.UtcNow) &&
                IsTimerStandby(fraction, resetTimeRaw, QuotaUpdatedAt, TimeSpan.FromHours(5)))
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
            if (!IsCycleStarted(fraction, resetTimeRaw, TimeSpan.FromDays(7), poolActivatedAt, DateTime.UtcNow) &&
                IsTimerStandby(fraction, resetTimeRaw, QuotaUpdatedAt, TimeSpan.FromDays(7)))
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
    public bool Has5hQuota => Quota5hFraction.HasValue;

    bool HasGemini5hSupport => IsProTier || Quota5hFraction.HasValue;
    bool Has3p5hSupport => IsProTier || Quota3p5hFraction.HasValue;

    [JsonIgnore]
    public double Quota5hPercentValue
    {
        get => (HasGemini5hSupport && Quota5hFraction.HasValue) ? Math.Round(Quota5hFraction.Value * 100.0, 1) : 0.0;
        set { }
    }

    [JsonIgnore]
    public string Quota5hPercentText => (HasGemini5hSupport && Quota5hFraction.HasValue) ? $"{Math.Round(Quota5hFraction.Value * 100.0)}%" : (IsProTier ? "100%" : "—");

    [JsonIgnore]
    public string Quota5hCountdown => EvaluateQuotaBadge(HasGemini5hSupport, Quota5hFraction, Quota5hResetTime, is5h: true, poolActivatedAt: GeminiActivatedAt).Text;
    [JsonIgnore]
    public System.Windows.Media.Brush Quota5hCountdownFg => EvaluateQuotaBadge(HasGemini5hSupport, Quota5hFraction, Quota5hResetTime, is5h: true, poolActivatedAt: GeminiActivatedAt).Fg;
    [JsonIgnore]
    public System.Windows.Media.Brush Quota5hCountdownBg => EvaluateQuotaBadge(HasGemini5hSupport, Quota5hFraction, Quota5hResetTime, is5h: true, poolActivatedAt: GeminiActivatedAt).Bg;
    [JsonIgnore]
    public string? Quota5hCountdownTooltip => EvaluateQuotaBadge(HasGemini5hSupport, Quota5hFraction, Quota5hResetTime, is5h: true, poolActivatedAt: GeminiActivatedAt).Tooltip;
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
    public string QuotaWeeklyCountdown => EvaluateQuotaBadge(true, QuotaWeeklyFraction, QuotaWeeklyResetTime, is5h: false, poolActivatedAt: GeminiActivatedAt).Text;
    [JsonIgnore]
    public System.Windows.Media.Brush QuotaWeeklyCountdownFg => EvaluateQuotaBadge(true, QuotaWeeklyFraction, QuotaWeeklyResetTime, is5h: false, poolActivatedAt: GeminiActivatedAt).Fg;
    [JsonIgnore]
    public System.Windows.Media.Brush QuotaWeeklyCountdownBg => EvaluateQuotaBadge(true, QuotaWeeklyFraction, QuotaWeeklyResetTime, is5h: false, poolActivatedAt: GeminiActivatedAt).Bg;
    [JsonIgnore]
    public string? QuotaWeeklyCountdownTooltip => EvaluateQuotaBadge(true, QuotaWeeklyFraction, QuotaWeeklyResetTime, is5h: false, poolActivatedAt: GeminiActivatedAt).Tooltip;
    [JsonIgnore]
    public System.Windows.Visibility QuotaWeeklyCountdownVisibility =>
        string.IsNullOrEmpty(QuotaWeeklyCountdown) ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;

    [JsonIgnore]
    public bool Has3pQuota => Quota3p5hFraction.HasValue || Quota3pWeeklyFraction.HasValue;

    [JsonIgnore]
    public double Quota3p5hPercentValue
    {
        get => (Has3p5hSupport && Quota3p5hFraction.HasValue) ? Math.Round(Quota3p5hFraction.Value * 100.0, 1) : 0.0;
        set { }
    }

    [JsonIgnore]
    public string Quota3p5hPercentText => (Has3p5hSupport && Quota3p5hFraction.HasValue) ? $"{Math.Round(Quota3p5hFraction.Value * 100.0)}%" : (IsProTier ? "100%" : "—");

    [JsonIgnore]
    public string Quota3p5hCountdown => EvaluateQuotaBadge(Has3p5hSupport, Quota3p5hFraction, Quota3p5hResetTime, is5h: true, poolActivatedAt: ThirdPartyActivatedAt).Text;
    [JsonIgnore]
    public System.Windows.Media.Brush Quota3p5hCountdownFg => EvaluateQuotaBadge(Has3p5hSupport, Quota3p5hFraction, Quota3p5hResetTime, is5h: true, poolActivatedAt: ThirdPartyActivatedAt).Fg;
    [JsonIgnore]
    public System.Windows.Media.Brush Quota3p5hCountdownBg => EvaluateQuotaBadge(Has3p5hSupport, Quota3p5hFraction, Quota3p5hResetTime, is5h: true, poolActivatedAt: ThirdPartyActivatedAt).Bg;
    [JsonIgnore]
    public string? Quota3p5hCountdownTooltip => EvaluateQuotaBadge(Has3p5hSupport, Quota3p5hFraction, Quota3p5hResetTime, is5h: true, poolActivatedAt: ThirdPartyActivatedAt).Tooltip;
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
    public string Quota3pWeeklyCountdown => EvaluateQuotaBadge(true, Quota3pWeeklyFraction, Quota3pWeeklyResetTime, is5h: false, poolActivatedAt: ThirdPartyActivatedAt).Text;
    [JsonIgnore]
    public System.Windows.Media.Brush Quota3pWeeklyCountdownFg => EvaluateQuotaBadge(true, Quota3pWeeklyFraction, Quota3pWeeklyResetTime, is5h: false, poolActivatedAt: ThirdPartyActivatedAt).Fg;
    [JsonIgnore]
    public System.Windows.Media.Brush Quota3pWeeklyCountdownBg => EvaluateQuotaBadge(true, Quota3pWeeklyFraction, Quota3pWeeklyResetTime, is5h: false, poolActivatedAt: ThirdPartyActivatedAt).Bg;
    [JsonIgnore]
    public string? Quota3pWeeklyCountdownTooltip => EvaluateQuotaBadge(true, Quota3pWeeklyFraction, Quota3pWeeklyResetTime, is5h: false, poolActivatedAt: ThirdPartyActivatedAt).Tooltip;
    [JsonIgnore]
    public System.Windows.Visibility Quota3pWeeklyCountdownVisibility =>
        string.IsNullOrEmpty(Quota3pWeeklyCountdown) ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;

    // ---------- 主窗口卡片 5H / 周限美元额度反推展示（O(1) 内存读取） ----------

    string BuildCardUsdBadgeText(bool supported, string bucket, double? fraction)
    {
        if (!supported || !fraction.HasValue) return "";
        if (!AgQuotaAnalyticsService.TryGetCardPoolUsd(this, bucket, fraction, out var remUsd, out var capUsd, out _, out _))
            return "";
        return $"≈ {UsagePricingService.FormatUsd(remUsd)} / {UsagePricingService.FormatUsd(capUsd)}";
    }

    string? BuildCardUsdTooltip(bool supported, string bucket, double? fraction)
    {
        if (!supported || !fraction.HasValue) return null;
        if (!AgQuotaAnalyticsService.TryGetCardPoolUsd(this, bucket, fraction, out var remUsd, out var capUsd, out var spentUsd, out var measured))
            return null;
        string src = measured ? I18nService.T("AgInsight.BadgeMeasured") : I18nService.T("AgInsight.BadgeTierRef");
        return I18nService.F(
            "AgInsight.CardUsdTipFmt",
            UsagePricingService.FormatUsd(spentUsd),
            UsagePricingService.FormatUsd(remUsd),
            UsagePricingService.FormatUsd(capUsd),
            src);
    }

    [JsonIgnore]
    public string Quota5hUsdText => BuildCardUsdBadgeText(HasGemini5hSupport, AgQuotaAnalyticsService.BucketGemini5h, Quota5hFraction);
    [JsonIgnore]
    public string? Quota5hUsdTooltip => BuildCardUsdTooltip(HasGemini5hSupport, AgQuotaAnalyticsService.BucketGemini5h, Quota5hFraction);
    [JsonIgnore]
    public System.Windows.Visibility Quota5hUsdVisibility =>
        string.IsNullOrEmpty(Quota5hUsdText) ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;

    [JsonIgnore]
    public string QuotaWeeklyUsdText => BuildCardUsdBadgeText(true, AgQuotaAnalyticsService.BucketGeminiWeekly, QuotaWeeklyFraction);
    [JsonIgnore]
    public string? QuotaWeeklyUsdTooltip => BuildCardUsdTooltip(true, AgQuotaAnalyticsService.BucketGeminiWeekly, QuotaWeeklyFraction);
    [JsonIgnore]
    public System.Windows.Visibility QuotaWeeklyUsdVisibility =>
        string.IsNullOrEmpty(QuotaWeeklyUsdText) ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;

    [JsonIgnore]
    public string Quota3p5hUsdText => BuildCardUsdBadgeText(Has3p5hSupport, AgQuotaAnalyticsService.Bucket3p5h, Quota3p5hFraction);
    [JsonIgnore]
    public string? Quota3p5hUsdTooltip => BuildCardUsdTooltip(Has3p5hSupport, AgQuotaAnalyticsService.Bucket3p5h, Quota3p5hFraction);
    [JsonIgnore]
    public System.Windows.Visibility Quota3p5hUsdVisibility =>
        string.IsNullOrEmpty(Quota3p5hUsdText) ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;

    [JsonIgnore]
    public string Quota3pWeeklyUsdText => BuildCardUsdBadgeText(Has3pQuota, AgQuotaAnalyticsService.Bucket3pWeekly, Quota3pWeeklyFraction);
    [JsonIgnore]
    public string? Quota3pWeeklyUsdTooltip => BuildCardUsdTooltip(Has3pQuota, AgQuotaAnalyticsService.Bucket3pWeekly, Quota3pWeeklyFraction);
    [JsonIgnore]
    public System.Windows.Visibility Quota3pWeeklyUsdVisibility =>
        string.IsNullOrEmpty(Quota3pWeeklyUsdText) ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;

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
    public string QuotaArgon5hCountdown => EvaluateQuotaBadge(IsProTier, QuotaArgon5hFraction, QuotaArgon5hResetTime, is5h: true, isArgon: true, poolActivatedAt: ArgonActivatedAt).Text;
    [JsonIgnore]
    public System.Windows.Media.Brush QuotaArgon5hCountdownFg => EvaluateQuotaBadge(IsProTier, QuotaArgon5hFraction, QuotaArgon5hResetTime, is5h: true, isArgon: true, poolActivatedAt: ArgonActivatedAt).Fg;
    [JsonIgnore]
    public System.Windows.Media.Brush QuotaArgon5hCountdownBg => EvaluateQuotaBadge(IsProTier, QuotaArgon5hFraction, QuotaArgon5hResetTime, is5h: true, isArgon: true, poolActivatedAt: ArgonActivatedAt).Bg;
    [JsonIgnore]
    public string? QuotaArgon5hCountdownTooltip => EvaluateQuotaBadge(IsProTier, QuotaArgon5hFraction, QuotaArgon5hResetTime, is5h: true, isArgon: true, poolActivatedAt: ArgonActivatedAt).Tooltip;
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
    public string QuotaArgonWeeklyCountdown => EvaluateQuotaBadge(true, QuotaArgonWeeklyFraction, QuotaArgonWeeklyResetTime, is5h: false, isArgon: true, poolActivatedAt: ArgonActivatedAt).Text;
    [JsonIgnore]
    public System.Windows.Media.Brush QuotaArgonWeeklyCountdownFg => EvaluateQuotaBadge(true, QuotaArgonWeeklyFraction, QuotaArgonWeeklyResetTime, is5h: false, isArgon: true, poolActivatedAt: ArgonActivatedAt).Fg;
    [JsonIgnore]
    public System.Windows.Media.Brush QuotaArgonWeeklyCountdownBg => EvaluateQuotaBadge(true, QuotaArgonWeeklyFraction, QuotaArgonWeeklyResetTime, is5h: false, isArgon: true, poolActivatedAt: ArgonActivatedAt).Bg;
    [JsonIgnore]
    public string? QuotaArgonWeeklyCountdownTooltip => EvaluateQuotaBadge(true, QuotaArgonWeeklyFraction, QuotaArgonWeeklyResetTime, is5h: false, isArgon: true, poolActivatedAt: ArgonActivatedAt).Tooltip;
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

    // ==================== Available Models Catalog (Merged & Picker-Filtered) ====================

    static readonly string[] KnownThinkingSuffixes = { "-xhigh", "-high", "-medium", "-low", "-thinking" };

    static bool IsLegacyHiddenModel(AgModelQuotaInfo m)
    {
        var id = m.ModelId.Trim();
        if (string.IsNullOrWhiteSpace(id)) return true;
        if (m.Disabled) return true;
        if (id.StartsWith("tab_", StringComparison.OrdinalIgnoreCase) ||
            id.StartsWith("chat_", StringComparison.OrdinalIgnoreCase))
            return true;
        if (id.Contains("-tiered", StringComparison.OrdinalIgnoreCase) ||
            id.Contains("-sonar", StringComparison.OrdinalIgnoreCase) ||
            id.Contains("-image", StringComparison.OrdinalIgnoreCase) ||
            id.Contains("flash-lite", StringComparison.OrdinalIgnoreCase) ||
            id.StartsWith("gemini-2.5-", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(id, "gemini-3-flash", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(id, "gemini-3.8-flash-tiered", StringComparison.OrdinalIgnoreCase))
            return true;
        return false;
    }

    static string DeriveFamilyKey(AgModelQuotaInfo m)
    {
        if (!string.IsNullOrWhiteSpace(m.GroupKey)) return m.GroupKey.Trim();
        var id = m.ModelId.Trim();
        foreach (var sfx in KnownThinkingSuffixes)
        {
            if (id.EndsWith(sfx, StringComparison.OrdinalIgnoreCase) && id.Length > sfx.Length)
                return id.Substring(0, id.Length - sfx.Length);
        }
        return id;
    }

    static string DeriveFamilyTitle(AgModelQuotaInfo m, string familyKey)
    {
        if (!string.IsNullOrWhiteSpace(m.GroupDisplayName)) return m.GroupDisplayName.Trim();
        var dn = m.DisplayName?.Trim() ?? "";
        if (!string.IsNullOrEmpty(dn) && !string.Equals(dn, m.ModelId, StringComparison.OrdinalIgnoreCase))
        {
            var parenIdx = dn.LastIndexOf(" (", StringComparison.Ordinal);
            if (parenIdx > 0 && dn.EndsWith(")", StringComparison.Ordinal))
                return dn.Substring(0, parenIdx).Trim();
            return dn;
        }
        // Format slug e.g. gemini-4-hydrogen -> Gemini 4 Hydrogen
        var parts = familyKey.Split('-', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < parts.Length; i++)
        {
            if (string.Equals(parts[i], "gpt", StringComparison.OrdinalIgnoreCase)) parts[i] = "GPT";
            else if (string.Equals(parts[i], "oss", StringComparison.OrdinalIgnoreCase)) parts[i] = "OSS";
            else if (char.IsLetter(parts[i][0]))
                parts[i] = char.ToUpperInvariant(parts[i][0]) + parts[i][1..];
        }
        return string.Join(" ", parts);
    }

    static (int Rank, string ShortLabel) ParseThinkingLevel(AgModelQuotaInfo m)
    {
        var raw = m.ThinkingDisplayName?.Trim() ?? "";
        var id = m.ModelId.Trim();
        if (string.IsNullOrEmpty(raw))
        {
            if (id.EndsWith("-low", StringComparison.OrdinalIgnoreCase)) raw = "Low";
            else if (id.EndsWith("-medium", StringComparison.OrdinalIgnoreCase)) raw = "Medium";
            else if (id.EndsWith("-xhigh", StringComparison.OrdinalIgnoreCase)) raw = "Extra High";
            else if (id.EndsWith("-high", StringComparison.OrdinalIgnoreCase)) raw = "High";
            else if (id.EndsWith("-thinking", StringComparison.OrdinalIgnoreCase) ||
                     (m.DisplayName?.Contains("(Thinking)", StringComparison.OrdinalIgnoreCase) ?? false))
                raw = "Thinking";
        }
        if (string.Equals(raw, "Extra High", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(raw, "XHigh", StringComparison.OrdinalIgnoreCase)) return (3, "XHigh");
        if (string.Equals(raw, "High", StringComparison.OrdinalIgnoreCase)) return (2, "High");
        if (string.Equals(raw, "Medium", StringComparison.OrdinalIgnoreCase)) return (1, "Medium");
        if (string.Equals(raw, "Low", StringComparison.OrdinalIgnoreCase)) return (0, "Low");
        if (string.Equals(raw, "Thinking", StringComparison.OrdinalIgnoreCase)) return (4, I18nService.T("Ag.ModelCapThinking"));
        return string.IsNullOrEmpty(raw) ? (-1, "") : (5, raw);
    }

    static int ModelSortRank(string familyKey, int pickerOrder)
    {
        if (pickerOrder >= 0) return pickerOrder;
        if (familyKey.Contains("gemini-4-neon", StringComparison.OrdinalIgnoreCase)) return 10;
        if (familyKey.Contains("gemini-4-carbon", StringComparison.OrdinalIgnoreCase)) return 11;
        if (familyKey.Contains("gemini-4-argon", StringComparison.OrdinalIgnoreCase)) return 12;
        if (familyKey.Contains("gemini-4", StringComparison.OrdinalIgnoreCase)) return 13;
        if (familyKey.Contains("gemini-3.8", StringComparison.OrdinalIgnoreCase)) return 14;
        if (familyKey.Contains("claude-opus-5", StringComparison.OrdinalIgnoreCase)) return 20;
        if (familyKey.Contains("claude-sonnet-5", StringComparison.OrdinalIgnoreCase)) return 21;
        if (familyKey.Contains("claude", StringComparison.OrdinalIgnoreCase)) return 22;
        if (familyKey.Contains("gpt", StringComparison.OrdinalIgnoreCase)) return 30;
        return 50;
    }

    List<ProfileModelChip> BuildMergedAvailableModels()
    {
        if (_modelQuotas == null || _modelQuotas.Count == 0)
            return new List<ProfileModelChip>();

        bool hasPickerMetadata = _modelQuotas.Any(m => m.IsPickerVisible || m.PickerOrder >= 0);
        var visibleRaw = hasPickerMetadata
            ? _modelQuotas.Where(m => m.IsPickerVisible && !m.Disabled && !string.IsNullOrWhiteSpace(m.ModelId)).ToList()
            : _modelQuotas.Where(m => !IsLegacyHiddenModel(m)).ToList();

        if (visibleRaw.Count == 0)
            return new List<ProfileModelChip>();

        var grouped = new Dictionary<string, (string Title, List<AgModelQuotaInfo> Items, int MinOrder)>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in visibleRaw)
        {
            var key = DeriveFamilyKey(m);
            if (!grouped.TryGetValue(key, out var bucket))
            {
                bucket = (DeriveFamilyTitle(m, key), new List<AgModelQuotaInfo>(), int.MaxValue);
            }
            bucket.Items.Add(m);
            if (m.PickerOrder >= 0 && m.PickerOrder < bucket.MinOrder)
                bucket.MinOrder = m.PickerOrder;
            grouped[key] = bucket;
        }

        var chips = new List<ProfileModelChip>(grouped.Count);
        foreach (var kv in grouped.OrderBy(x => ModelSortRank(x.Key, x.Value.MinOrder == int.MaxValue ? -1 : x.Value.MinOrder))
                                  .ThenBy(x => x.Value.Title, StringComparer.OrdinalIgnoreCase))
        {
            var items = kv.Value.Items;
            var rep = items[0];
            bool isGemini = kv.Key.Contains("gemini", StringComparison.OrdinalIgnoreCase);
            bool is3p = !isGemini;

            var levels = items
                .Select(ParseThinkingLevel)
                .Where(x => x.Rank >= 0 && !string.IsNullOrEmpty(x.ShortLabel))
                .DistinctBy(x => x.ShortLabel)
                .OrderBy(x => x.Rank)
                .ToList();

            string levelsBadge = "";
            if (levels.Count >= 2 && levels[0].Rank <= 3 && levels[^1].Rank <= 3)
                levelsBadge = $"{levels[0].ShortLabel}–{levels[^1].ShortLabel}";
            else if (levels.Count == 1)
                levelsBadge = levels[0].ShortLabel;
            else if (levels.Count > 1)
                levelsBadge = string.Join(" / ", levels.Select(l => l.ShortLabel));
            else if (items.Any(x => x.SupportsThinking))
                levelsBadge = I18nService.T("Ag.ModelCapThinking");

            int maxCtx = items.Max(x => x.MaxTokens);
            bool hasThink = items.Any(x => x.SupportsThinking);
            bool hasImg = items.Any(x => x.SupportsImages);
            var tipParts = new List<string> { $"{kv.Value.Title}  ({kv.Key})" };
            if (levels.Count > 0)
                tipParts.Add(string.Join(" / ", levels.Select(l => l.ShortLabel)));
            var caps = new List<string>();
            if (hasThink) caps.Add(I18nService.T("Ag.ModelCapThinking"));
            if (hasImg) caps.Add(I18nService.T("Ag.ModelCapImages"));
            if (maxCtx >= 1000) caps.Add(I18nService.F("Ag.ModelContextFmt", maxCtx / 1000));
            if (caps.Count > 0) tipParts.Add(string.Join(" · ", caps));

            chips.Add(new ProfileModelChip(
                kv.Key,
                kv.Value.Title,
                levelsBadge,
                isGemini,
                is3p,
                string.Join("\n", tipParts)));
        }

        return chips;
    }

    [JsonIgnore]
    public List<ProfileModelChip> MergedAvailableModels =>
        _cachedMergedModels ??= BuildMergedAvailableModels();

    [JsonIgnore]
    public int AvailableModelCount => MergedAvailableModels.Count;

    [JsonIgnore]
    public bool HasModelCatalog => AvailableModelCount > 0;

    [JsonIgnore]
    public System.Windows.Visibility HasModelCatalogVisibility =>
        HasModelCatalog ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

    [JsonIgnore]
    public System.Windows.Visibility ModelsDrawerVisibility =>
        IsModelsExpanded && HasModelCatalog ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

    [JsonIgnore]
    public string AvailableModelsHeaderText =>
        I18nService.F("Ag.AvailableModelsFmt", AvailableModelCount);
}

public sealed record ProfileModelChip(
    string FamilyKey,
    string DisplayTitle,
    string LevelsBadge,
    bool IsGemini,
    bool Is3p,
    string Tooltip)
{
    static System.Windows.Media.SolidColorBrush FreezeBrush(byte r, byte g, byte b)
    {
        var br = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(r, g, b));
        br.Freeze();
        return br;
    }

    static readonly System.Windows.Media.Brush GemChipBg = FreezeBrush(0xF0, 0xF4, 0xFF);
    static readonly System.Windows.Media.Brush GemChipBorder = FreezeBrush(0xC7, 0xD2, 0xFE);
    static readonly System.Windows.Media.Brush GemTitleFg = FreezeBrush(0x31, 0x2E, 0x81);
    static readonly System.Windows.Media.Brush GemBadgeBg = FreezeBrush(0xE0, 0xE7, 0xFF);
    static readonly System.Windows.Media.Brush GemBadgeFg = FreezeBrush(0x43, 0x38, 0xCA);

    static readonly System.Windows.Media.Brush ThirdChipBg = FreezeBrush(0xF7, 0xF3, 0xFF);
    static readonly System.Windows.Media.Brush ThirdChipBorder = FreezeBrush(0xDD, 0xD6, 0xFE);
    static readonly System.Windows.Media.Brush ThirdTitleFg = FreezeBrush(0x4C, 0x1D, 0x95);
    static readonly System.Windows.Media.Brush ThirdBadgeBg = FreezeBrush(0xED, 0xE9, 0xFE);
    static readonly System.Windows.Media.Brush ThirdBadgeFg = FreezeBrush(0x6D, 0x28, 0xD9);

    public System.Windows.Media.Brush ChipBg => IsGemini ? GemChipBg : ThirdChipBg;
    public System.Windows.Media.Brush ChipBorder => IsGemini ? GemChipBorder : ThirdChipBorder;
    public System.Windows.Media.Brush TitleFg => IsGemini ? GemTitleFg : ThirdTitleFg;
    public System.Windows.Media.Brush BadgeBg => IsGemini ? GemBadgeBg : ThirdBadgeBg;
    public System.Windows.Media.Brush BadgeFg => IsGemini ? GemBadgeFg : ThirdBadgeFg;
    public System.Windows.Visibility LevelsBadgeVisibility =>
        string.IsNullOrEmpty(LevelsBadge) ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;
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
    public bool IsPickerVisible { get; set; }
    public int PickerOrder { get; set; } = -1;
    public string? GroupKey { get; set; }
    public string? GroupDisplayName { get; set; }
    public string? ThinkingDisplayName { get; set; }
}

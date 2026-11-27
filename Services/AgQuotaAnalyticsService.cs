using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using APISwitch.Models;
using Microsoft.Data.Sqlite;

namespace APISwitch.Services;

/// <summary>
/// Antigravity 账号配额深度透视与损耗核算引擎：
/// 1) 结合 quota_snapshots 与分账号精确等比账单，动态反推各账号 5H / 周限池的真实 100% 美元上限与剩余额度；
/// 2) 自动去抖识别「正常到期重置」与「⚡ 官方提前/集体重置（多账号同时段回满）」；
/// 3) 按账号独立订阅月周期（每月账单锚点日）核算：月度实际消耗、月度估算总供给、到期未用完作废额度与满额未激活空转损耗。
/// </summary>
public static class AgQuotaAnalyticsService
{
    public const string BucketGemini5h = "gemini-5h";
    public const string BucketGeminiWeekly = "gemini-weekly";
    public const string BucketArgonWeekly = "gemini-argon-weekly";
    public const string Bucket3p5h = "3p-5h";
    public const string Bucket3pWeekly = "3p-weekly";

    private sealed record PreparedDataset(
        long DbToken,
        long PricingVer,
        long BuiltTs,
        string ProfilesSignature,
        List<RawSnap> AllSnaps,
        List<RawAgEvent> AllEvents,
        Dictionary<string, List<RawAgEvent>> EventsByAccount,
        Dictionary<(string Email, string Bucket), List<RawSnap>> SnapsByAcctBucket,
        Dictionary<string, long> EarliestTsByAcct,
        List<CycleSegment> Segments,
        Dictionary<(string Email, string Bucket), (double CapUsd, bool Measured)> CalibratedCaps,
        List<QuotaResetEventItem> EnrichedResets,
        Dictionary<string, List<QuotaResetEventItem>> ResetsByAccount,
        List<MassResetClusterSummary> MassClusters);

    private static readonly object _prepLock = new();
    private static PreparedDataset? _cachedDataset;
    private static System.Collections.Concurrent.ConcurrentDictionary<(string Email, string Bucket), (double CapUsd, double CycleSpentUsd, bool Measured)> _cardCapCache = new();
    private static long _cardCacheWarmedTicks;
    private static int _warmingInFlight;

    public static bool IsSnapshotWarm => _cachedDataset != null && System.Threading.Interlocked.Read(ref _cardCacheWarmedTicks) > 0;

    /// <summary>秒级读取已缓存的历史出现过的 Antigravity 账号邮箱集合（零 SQLite I/O）。</summary>
    public static IReadOnlyCollection<string> GetCachedKnownEmails()
    {
        var ds = _cachedDataset;
        return ds != null ? ds.EarliestTsByAcct.Keys.ToList() : Array.Empty<string>();
    }

    /// <summary>清空主界面卡片与分析快照缓存（供沙盒自检或数据重置调用）。</summary>
    public static void ResetCache()
    {
        lock (_prepLock)
        {
            _cachedDataset = null;
        }
        _cardCapCache.Clear();
        System.Threading.Interlocked.Exchange(ref _cardCacheWarmedTicks, 0);
    }

    private static string ComputeProfilesSignature(IReadOnlyList<Profile> profiles)
    {
        var sb = new System.Text.StringBuilder(profiles.Count * 28);
        foreach (var p in profiles)
        {
            sb.Append((p.Email ?? "").Trim().ToLowerInvariant()).Append('|')
              .Append(p.SubscriptionTier ?? "").Append('|')
              .Append(p.IsPaidSubscription?.ToString() ?? "").Append(';');
        }
        return sb.ToString();
    }

    private static PreparedDataset GetOrBuildDataset(IReadOnlyList<Profile> profiles, long nowTs, bool allowCache)
    {
        long memVer = UsageStorage.MutationVersion;
        long priceVer = UsagePricingService.PricingVersion;
        string profSig = ComputeProfilesSignature(profiles);

        if (allowCache)
        {
            var cur = _cachedDataset;
            if (cur != null &&
                cur.DbToken == memVer &&
                cur.PricingVer == priceVer &&
                cur.ProfilesSignature == profSig &&
                Math.Abs(nowTs - cur.BuiltTs) <= 120)
            {
                return cur;
            }
        }

        lock (_prepLock)
        {
            if (allowCache && _cachedDataset != null &&
                _cachedDataset.DbToken == memVer &&
                _cachedDataset.PricingVer == priceVer &&
                _cachedDataset.ProfilesSignature == profSig &&
                Math.Abs(nowTs - _cachedDataset.BuiltTs) <= 120)
            {
                return _cachedDataset;
            }

            long lookbackTs = nowTs - 200L * 86400L;
            var (allSnaps, allEvents, earliest) = LoadRawDataFromDb(lookbackTs);

            var eventsByAcct = allEvents
                .GroupBy(e => e.Email, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.OrderBy(x => x.Ts).ToList(), StringComparer.OrdinalIgnoreCase);

            var snapsByAcctBucket = allSnaps
                .GroupBy(s => (s.Email.ToLowerInvariant(), s.Bucket))
                .ToDictionary(g => g.Key, g => g.OrderBy(x => x.Ts).ToList());

            var (segments, rawResets) = ExtractSegmentsAndResets(snapsByAcctBucket, eventsByAcct);
            var calibrated = CalibratePoolCaps(profiles, segments, nowTs);
            var (enrichedResets, massClusters) = ClassifyAndEnrichResets(rawResets, calibrated);

            var resetsByAcct = enrichedResets
                .GroupBy(r => r.AccountEmail, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.OrderByDescending(x => x.ResetTimeLocal).ToList(), StringComparer.OrdinalIgnoreCase);

            var built = new PreparedDataset(
                memVer, priceVer, nowTs, profSig,
                allSnaps, allEvents, eventsByAcct, snapsByAcctBucket, earliest,
                segments, calibrated, enrichedResets, resetsByAcct, massClusters);

            if (allowCache)
                _cachedDataset = built;
            return built;
        }
    }

    /// <summary>在后台线程预热全部账号的 5H / 周限 100% 美元容量上限与完整内存快照，供主界面与透视窗零阻塞秒开。</summary>
    public static void WarmAccountCapCache(IReadOnlyList<Profile>? profiles = null)
    {
        if (System.Threading.Interlocked.CompareExchange(ref _warmingInFlight, 1, 0) != 0)
            return;
        try
        {
            var list = profiles ?? ProfileStore.Load();
            var nowUtc = DateTime.UtcNow;
            long nowTs = new DateTimeOffset(nowUtc).ToUnixTimeSeconds();
            var ds = GetOrBuildDataset(list, nowTs, allowCache: true);

            var next = new System.Collections.Concurrent.ConcurrentDictionary<(string Email, string Bucket), (double CapUsd, double CycleSpentUsd, bool Measured)>();
            foreach (var p in list)
            {
                string em = (p.Email ?? "").Trim().ToLowerInvariant();
                if (string.IsNullOrEmpty(em)) continue;
                ds.EventsByAccount.TryGetValue(em, out var acctEvents);
                ds.ResetsByAccount.TryGetValue(em, out var acctResets);
                var livePools = BuildLivePoolInsights(p, em, nowUtc, nowTs, acctEvents, ds.CalibratedCaps, acctResets);
                foreach (var lp in livePools)
                {
                    if (!lp.IsSupported) continue;
                    next[(em, lp.BucketId)] = (lp.EstimatedCapUsd, lp.CurrentCycleSpentUsd, lp.IsMeasuredFromHistory);
                }
            }

            _cardCapCache = next;
            System.Threading.Interlocked.Exchange(ref _cardCacheWarmedTicks, DateTime.UtcNow.Ticks);
        }
        catch { }
        finally
        {
            System.Threading.Interlocked.Exchange(ref _warmingInFlight, 0);
        }
    }

    /// <summary>主界面账号卡片纯内存 O(1) 零阻塞查询（严禁在调用线程触碰 SQLite 或磁盘锁）。</summary>
    public static bool TryGetCardPoolUsd(
        Profile profile,
        string bucket,
        double? liveFraction,
        out double remainingUsd,
        out double capUsd,
        out double cycleSpentUsd,
        out bool isMeasured)
    {
        remainingUsd = 0;
        capUsd = 0;
        cycleSpentUsd = 0;
        isMeasured = false;

        string em = (profile.Email ?? "").Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(em)) return false;

        if (_cardCapCache.TryGetValue((em, bucket), out var hit) && hit.CapUsd > 0.05)
        {
            capUsd = hit.CapUsd;
            cycleSpentUsd = hit.CycleSpentUsd;
            isMeasured = hit.Measured;
        }
        else
        {
            capUsd = DefaultTierPriorCap(NormalizeTierGroup(profile), bucket);
            isMeasured = false;
            if (System.Threading.Interlocked.Read(ref _cardCacheWarmedTicks) == 0 && !AgPaths.IsSandboxed)
            {
                _ = System.Threading.Tasks.Task.Run(() => WarmAccountCapCache());
            }
        }

        if (capUsd <= 0.05) return false;
        double frac = Math.Clamp(liveFraction ?? (profile.IsProTier ? 1.0 : 0.0), 0.0, 1.0);
        remainingUsd = frac * capUsd;
        return true;
    }

    public sealed record PoolLiveInsight(
        string BucketId,
        string PoolTitle,
        bool IsWeekly,
        bool IsSupported,
        bool IsStandby,
        double RemainingFraction,
        DateTime? LockedResetLocal,
        string ResetStatusText,
        double CurrentCycleSpentUsd,
        long CurrentCycleTokens,
        double EstimatedCapUsd,
        double EstimatedRemainingUsd,
        bool IsMeasuredFromHistory);

    public sealed record QuotaResetEventItem(
        DateTime ResetTimeLocal,
        string AccountEmail,
        string BucketId,
        string PoolTitle,
        bool IsEarlyOfficialReset,
        bool IsGlobalMassReset,
        int MassResetAccountCount,
        double PreResetRemainingFraction,
        DateTime? PreviousScheduledResetLocal,
        double CycleConsumedUsd,
        double ExpiredWasteUsd,
        double ImpliedCapUsd);

    public sealed record MassResetClusterSummary(
        DateTime OccurredLocal,
        int AccountCount,
        List<string> Accounts,
        double TotalRestoredFractionAvg);

    public sealed record SubscriptionMonthReport(
        string Email,
        string TierDisplay,
        int BillingAnchorDay,
        DateTime? SubscriptionStartDate,
        DateTime PeriodStartLocal,
        DateTime PeriodEndLocal,
        int MonthOffset,
        double TotalDays,
        double ElapsedDays,
        double ActualConsumedUsd,
        long ActualTokens,
        long ActualRequests,
        double EstimatedMonthlySupplyUsd,
        double ElapsedSupplyUsd,
        double ExpiredUnusedWasteUsd,
        double IdleStandbyWasteUsd,
        double OfficialMassResetBonusUsd,
        int OfficialMassResetCount,
        List<PoolLiveInsight> Pools,
        List<QuotaResetEventItem> AccountResetHistory,
        List<MassResetClusterSummary> GlobalMassResets)
    {
        public double TotalWastedUsd => ExpiredUnusedWasteUsd + IdleStandbyWasteUsd;
        public double UtilizationRatePct =>
            EstimatedMonthlySupplyUsd > 0.05 ? Math.Clamp(ActualConsumedUsd * 100.0 / EstimatedMonthlySupplyUsd, 0.0, 100.0) : 0.0;
    }

    private readonly record struct RawSnap(long Ts, string Email, string Bucket, double Rem, long ResetTs);
    private readonly record struct RawAgEvent(long Ts, string Email, string PoolKind, double CostUsd, long Tokens);
    private readonly record struct CycleSegment(
        string Email,
        string Bucket,
        long StartTs,
        long EndTs,
        double StartRem,
        double EndRem,
        long ResetTs,
        double SpentUsd,
        long Tokens);

    /// <summary>计算指定账号在指定订阅月偏移（0=当前订阅月，-1=上一订阅月）下的完整配额透视报告。</summary>
    public static SubscriptionMonthReport AnalyzeAccount(
        Profile targetProfile,
        IReadOnlyList<Profile>? allProfiles = null,
        int monthOffset = 0,
        DateTime? nowLocalOverride = null)
    {
        var profiles = allProfiles ?? ProfileStore.Load();
        string targetEmail = (targetProfile.Email ?? "").Trim().ToLowerInvariant();
        var nowLocal = nowLocalOverride ?? DateTime.Now;
        var nowUtc = nowLocal.ToUniversalTime();
        long nowTs = new DateTimeOffset(nowUtc).ToUnixTimeSeconds();

        // 1. 命中或构建 200 天预分组内存快照（切换账号与翻阅历史月份均为 0 次 SQLite 查询、<0.5ms 内存读取）
        var ds = GetOrBuildDataset(profiles, nowTs, allowCache: !nowLocalOverride.HasValue && !AgPaths.IsSandboxed);

        // 2. 确定该账号的每月账单结算锚点日（1-31）
        int anchorDay = ResolveBillingAnchorDay(targetProfile, ds.EarliestTsByAcct);
        var (periodStartLocal, periodEndLocal) = ComputeMonthlyBillingWindow(nowLocal, anchorDay, monthOffset);
        long pStartTs = new DateTimeOffset(periodStartLocal.ToUniversalTime()).ToUnixTimeSeconds();
        long pEndTs = new DateTimeOffset(periodEndLocal.ToUniversalTime()).ToUnixTimeSeconds();
        long effEndTs = Math.Min(nowTs, pEndTs);

        double totalDays = Math.Max(1.0, (periodEndLocal - periodStartLocal).TotalDays);
        double elapsedDays = nowLocal <= periodStartLocal
            ? 0.0
            : (nowLocal >= periodEndLocal ? totalDays : (nowLocal - periodStartLocal).TotalDays);

        ds.EventsByAccount.TryGetValue(targetEmail, out var acctEvents);
        ds.ResetsByAccount.TryGetValue(targetEmail, out var acctResetsRaw);
        var acctResets = acctResetsRaw ?? new List<QuotaResetEventItem>();
        var calibratedCaps = ds.CalibratedCaps;

        // 3. 计算当前实时 5H 与 7d 配额池状态及本周期实际消耗
        var livePools = BuildLivePoolInsights(
            targetProfile, targetEmail, nowUtc, nowTs, acctEvents, calibratedCaps, acctResets);

        // 4. 核算目标订阅月周期 [pStartTs, pEndTs) 内的实际消耗、估算总供给与浪费拆解（仅遍历当前账号自身列表）
        double monthActualUsd = 0;
        long monthTokens = 0;
        long monthReqs = 0;
        if (acctEvents != null)
        {
            foreach (var ev in acctEvents)
            {
                if (ev.Ts < pStartTs) continue;
                if (ev.Ts >= pEndTs) break;
                monthActualUsd += ev.CostUsd;
                monthTokens += ev.Tokens;
                monthReqs++;
            }
        }

        double gemWeeklyCap = GetCap(calibratedCaps, targetEmail, BucketGeminiWeekly, targetProfile).CapUsd;
        bool hasArgonPool = Profile.ShowArgonQuotaColumn &&
                            (targetProfile.QuotaArgonWeeklyFraction.HasValue ||
                             ds.Segments.Any(s => string.Equals(s.Email, targetEmail, StringComparison.OrdinalIgnoreCase) && s.Bucket == BucketArgonWeekly));
        double argonWeeklyCap = hasArgonPool
            ? GetCap(calibratedCaps, targetEmail, BucketArgonWeekly, targetProfile).CapUsd
            : 0.0;
        double p3WeeklyCap = GetCap(calibratedCaps, targetEmail, Bucket3pWeekly, targetProfile).CapUsd;
        double combinedWeeklyCap = gemWeeklyCap + argonWeeklyCap + p3WeeklyCap;

        double estMonthlySupplyUsd = (totalDays / 7.0) * combinedWeeklyCap;
        double elapsedSupplyUsd = (elapsedDays / 7.0) * combinedWeeklyCap;

        // 4.1 重置作废浪费（正常到期未用完）vs 官方集体重置额外回血红利
        double expiredWasteUsd = 0.0;
        double massResetBonusUsd = 0.0;
        int massResetCountInMonth = 0;

        var seenMassResetClustersInMonth = new HashSet<long>();
        foreach (var r in acctResets)
        {
            long rTs = new DateTimeOffset(r.ResetTimeLocal.ToUniversalTime()).ToUnixTimeSeconds();
            if (rTs < pStartTs || rTs >= pEndTs) continue;
            if (!IsWeeklyBucket(r.BucketId)) continue;

            if (r.IsEarlyOfficialReset)
            {
                double restoredUsedFrac = Math.Clamp(1.0 - r.PreResetRemainingFraction, 0.0, 1.0);
                double cap = GetCap(calibratedCaps, targetEmail, r.BucketId, targetProfile).CapUsd;
                massResetBonusUsd += restoredUsedFrac * cap;
                long bucketHour = rTs / 2700;
                if (seenMassResetClustersInMonth.Add(bucketHour))
                    massResetCountInMonth++;
            }
            else
            {
                expiredWasteUsd += r.ExpiredWasteUsd;
            }
        }
        estMonthlySupplyUsd += massResetBonusUsd;
        elapsedSupplyUsd += massResetBonusUsd;

        // 4.2 满额未激活待命空转损耗
        double idleWasteUsd = 0.0;
        if (effEndTs > pStartTs)
        {
            idleWasteUsd += ComputeIdleStandbyLossUsd(ds.SnapsByAcctBucket, targetEmail, BucketGeminiWeekly, pStartTs, effEndTs, gemWeeklyCap);
            if (hasArgonPool && argonWeeklyCap > 0)
                idleWasteUsd += ComputeIdleStandbyLossUsd(ds.SnapsByAcctBucket, targetEmail, BucketArgonWeekly, pStartTs, effEndTs, argonWeeklyCap);
            idleWasteUsd += ComputeIdleStandbyLossUsd(ds.SnapsByAcctBucket, targetEmail, Bucket3pWeekly, pStartTs, effEndTs, p3WeeklyCap);
        }

        return new SubscriptionMonthReport(
            Email: targetProfile.Email ?? "",
            TierDisplay: targetProfile.TierDisplay,
            BillingAnchorDay: anchorDay,
            SubscriptionStartDate: targetProfile.SubscriptionStartDate,
            PeriodStartLocal: periodStartLocal,
            PeriodEndLocal: periodEndLocal,
            MonthOffset: monthOffset,
            TotalDays: totalDays,
            ElapsedDays: elapsedDays,
            ActualConsumedUsd: monthActualUsd,
            ActualTokens: monthTokens,
            ActualRequests: monthReqs,
            EstimatedMonthlySupplyUsd: estMonthlySupplyUsd,
            ElapsedSupplyUsd: elapsedSupplyUsd,
            ExpiredUnusedWasteUsd: expiredWasteUsd,
            IdleStandbyWasteUsd: idleWasteUsd,
            OfficialMassResetBonusUsd: massResetBonusUsd,
            OfficialMassResetCount: massResetCountInMonth,
            Pools: livePools,
            AccountResetHistory: acctResets.Take(40).ToList(),
            GlobalMassResets: ds.MassClusters.Take(20).ToList());
    }

    public static int ResolveBillingAnchorDay(Profile p, IReadOnlyDictionary<string, long>? earliestTsByAcct = null)
    {
        if (p.BillingCycleDay is >= 1 and <= 31)
            return p.BillingCycleDay.Value;
        if (p.SubscriptionStartDate.HasValue)
            return Math.Clamp(p.SubscriptionStartDate.Value.Day, 1, 31);

        if (p.CapturedAtUtc > new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc))
            return Math.Clamp(p.CapturedAtUtc.ToLocalTime().Day, 1, 31);

        string em = (p.Email ?? "").Trim().ToLowerInvariant();
        if (earliestTsByAcct != null && earliestTsByAcct.TryGetValue(em, out long firstTs) && firstTs > 0)
            return Math.Clamp(DateTimeOffset.FromUnixTimeSeconds(firstTs).LocalDateTime.Day, 1, 31);

        return 1;
    }

    /// <summary>按每月结算锚点日计算账单月起止本地时间 [Start, End)。</summary>
    public static (DateTime StartLocal, DateTime EndLocal) ComputeMonthlyBillingWindow(DateTime refLocal, int anchorDay, int monthOffset = 0)
    {
        int day = Math.Clamp(anchorDay, 1, 31);
        var today = refLocal.Date;

        int curAnchorClamped = Math.Min(day, DateTime.DaysInMonth(today.Year, today.Month));
        DateTime baseStart;
        if (today.Day >= curAnchorClamped)
        {
            baseStart = new DateTime(today.Year, today.Month, curAnchorClamped, 0, 0, 0, DateTimeKind.Local);
        }
        else
        {
            var prev = today.AddMonths(-1);
            int prevClamped = Math.Min(day, DateTime.DaysInMonth(prev.Year, prev.Month));
            baseStart = new DateTime(prev.Year, prev.Month, prevClamped, 0, 0, 0, DateTimeKind.Local);
        }

        var targetStartMonth = baseStart.AddMonths(monthOffset);
        int startClamped = Math.Min(day, DateTime.DaysInMonth(targetStartMonth.Year, targetStartMonth.Month));
        var start = new DateTime(targetStartMonth.Year, targetStartMonth.Month, startClamped, 0, 0, 0, DateTimeKind.Local);

        var nextMonth = start.AddMonths(1);
        int endClamped = Math.Min(day, DateTime.DaysInMonth(nextMonth.Year, nextMonth.Month));
        var end = new DateTime(nextMonth.Year, nextMonth.Month, endClamped, 0, 0, 0, DateTimeKind.Local);

        return (start, end);
    }

    private static (List<RawSnap> Snaps, List<RawAgEvent> Events, Dictionary<string, long> EarliestByAcct) LoadRawDataFromDb(long fromTs)
    {
        var snaps = new List<RawSnap>();
        var events = new List<RawAgEvent>();
        var earliest = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        try
        {
            UsageStorage.EnsureInit();
            using var conn = new SqliteConnection($"Data Source={UsageStorage.DbPath}");
            conn.Open();

            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"SELECT ts, LOWER(TRIM(account_email)), bucket, remaining_fraction, reset_time
FROM quota_snapshots
WHERE ts >= $from AND account_email <> ''
ORDER BY LOWER(TRIM(account_email)) ASC, bucket ASC, ts ASC;";
                cmd.Parameters.AddWithValue("$from", fromTs);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    long ts = r.GetInt64(0);
                    string em = r.IsDBNull(1) ? "" : r.GetString(1);
                    string bkt = r.IsDBNull(2) ? "" : r.GetString(2);
                    double rem = r.GetDouble(3);
                    long rst = r.GetInt64(4);
                    if (string.IsNullOrEmpty(em) || string.IsNullOrEmpty(bkt)) continue;
                    if (!Profile.ShowArgonQuotaColumn && bkt.Contains("argon", StringComparison.OrdinalIgnoreCase)) continue;
                    snaps.Add(new RawSnap(ts, em, bkt, rem, rst));
                    if (!earliest.TryGetValue(em, out long prevMin) || ts < prevMin)
                        earliest[em] = ts;
                }
            }

            // 按 (账号, 模型) 缓存单价与池分类，将 4.3 万次字符串解析压缩为 ~15 次字典读取
            var rateMemo = new Dictionary<(string Email, string Model), (UsagePricingService.ModelRate Rate, string PoolKind)>();

            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"SELECT ts, provider_id, model, in_tokens, out_tokens, cache_read_tokens, cache_write_tokens
FROM usage_events
WHERE agent = $ag AND ts >= $from AND provider_id LIKE 'ag:%'
ORDER BY ts ASC;";
                cmd.Parameters.AddWithValue("$ag", UsageStorage.AgentAntigravity);
                cmd.Parameters.AddWithValue("$from", fromTs);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    long ts = r.GetInt64(0);
                    string pid = r.IsDBNull(1) ? "" : r.GetString(1);
                    if (pid.Length <= 3) continue;
                    string em = pid.Substring(3).Trim().ToLowerInvariant();
                    string model = r.IsDBNull(2) ? "" : r.GetString(2);
                    long tin = r.GetInt64(3);
                    long tout = r.GetInt64(4);
                    long cr = Math.Max(0, r.GetInt64(5));
                    long cw = Math.Max(0, r.GetInt64(6));

                    var mKey = (em, model);
                    if (!rateMemo.TryGetValue(mKey, out var cached))
                    {
                        var resolvedRate = UsagePricingService.ResolveRate(model, em, UsageStorage.AgentAntigravity);
                        cached = (resolvedRate, ClassifyEventPoolKind(model));
                        rateMemo[mKey] = cached;
                    }

                    long freshIn = tin >= cr + cw ? tin - cr - cw : (tin >= cr ? tin - cr : Math.Max(0, tin));
                    double usd = (freshIn * cached.Rate.InputPerMillion
                                + cr * cached.Rate.CacheReadPerMillion
                                + cw * cached.Rate.CacheWritePerMillion
                                + Math.Max(0, tout) * cached.Rate.OutputPerMillion) / 1_000_000.0;
                    if (usd < 0) usd = 0;

                    events.Add(new RawAgEvent(ts, em, cached.PoolKind, usd, tin + tout));
                    if (!earliest.TryGetValue(em, out long prevMin) || ts < prevMin)
                        earliest[em] = ts;
                }
            }
        }
        catch { }

        return (snaps, events, earliest);
    }

    private static string ClassifyEventPoolKind(string model)
    {
        if (string.IsNullOrEmpty(model)) return "gemini";
        if (model.Contains("claude", StringComparison.OrdinalIgnoreCase) ||
            model.Contains("gpt", StringComparison.OrdinalIgnoreCase))
            return "3p";
        if (model.Contains("argon", StringComparison.OrdinalIgnoreCase))
            return "argon";
        return "gemini";
    }

    private static bool MatchesBucketPool(string bucket, string eventPoolKind)
    {
        if (bucket.StartsWith("3p-", StringComparison.OrdinalIgnoreCase))
            return eventPoolKind == "3p";
        if (bucket.Contains("argon", StringComparison.OrdinalIgnoreCase))
            return eventPoolKind == "argon";
        return eventPoolKind == "gemini";
    }

    private readonly record struct RawResetCandidate(
        long ResetTs,
        string Email,
        string Bucket,
        bool IsEarly,
        double PreResetRem,
        long PrevScheduledResetTs,
        double CycleSpentUsd,
        double CycleDropFrac);

    private static (List<CycleSegment> Segments, List<RawResetCandidate> Resets) ExtractSegmentsAndResets(
        Dictionary<(string Email, string Bucket), List<RawSnap>> snapsByAcctBucket,
        Dictionary<string, List<RawAgEvent>> eventsByAcct)
    {
        var segments = new List<CycleSegment>();
        var resets = new List<RawResetCandidate>();

        foreach (var (grpKey, list) in snapsByAcctBucket)
        {
            if (list.Count < 2) continue;

            string email = grpKey.Email;
            string bucket = grpKey.Bucket;
            long windowSec = IsWeeklyBucket(bucket) ? 7L * 86400L : 5L * 3600L;

            eventsByAcct.TryGetValue(email, out var acctEvents);

            long segStartTs = list[0].Ts;
            long segEndTs = list[0].Ts;
            double segStartRem = list[0].Rem;
            double segMinRem = list[0].Rem;
            long segLockedReset = IsLockedCycle(list[0], windowSec) ? list[0].ResetTs : 0;
            long lastRecordedResetTs = 0;

            for (int i = 1; i < list.Count; i++)
            {
                var prev = list[i - 1];
                var cur = list[i];

                // 过滤 Google 后端多节点滚动发版期的 20 分钟短暂回跳伪影：
                // 若刚刚发生过回血，随后 20 分钟内偶发跳回旧残血值，且未来 20 分钟内又回到高值，则跳过该旧节点脏读
                if (lastRecordedResetTs > 0 && (cur.Ts - lastRecordedResetTs) <= 1200 && cur.Rem < prev.Rem - 0.15)
                {
                    bool recoversSoon = false;
                    for (int k = i + 1; k < list.Count && (list[k].Ts - cur.Ts) <= 1200; k++)
                    {
                        if (list[k].Rem >= prev.Rem - 0.05) { recoversSoon = true; break; }
                    }
                    if (recoversSoon) continue;
                }

                bool jumpedUp = (cur.Rem - segMinRem) >= 0.04 && cur.Rem >= prev.Rem + 0.03;
                bool resetShifted = segLockedReset > 0 && cur.ResetTs > segLockedReset + 1800 && cur.Ts >= segLockedReset - 1800;

                if (jumpedUp || resetShifted)
                {
                    double drop = Math.Max(0.0, segStartRem - segMinRem);
                    var (spentUsd, tok) = SumPoolEvents(acctEvents, bucket, segStartTs - 30, prev.Ts + 30);
                    if (drop >= 0.05 && segEndTs > segStartTs)
                    {
                        segments.Add(new CycleSegment(email, bucket, segStartTs, prev.Ts, segStartRem, segMinRem, segLockedReset, spentUsd, tok));
                    }

                    // 距上一次同一池重置超过 25 分钟才记为新一次真实重置事件（消除发版抖动重复计数，并过滤 100% 滑窗假重置）
                    bool meaningfulReset = IsWeeklyBucket(bucket) ? (segMinRem < 0.985) : (segMinRem <= 0.75);
                    if (cur.Ts - lastRecordedResetTs > 1500 && meaningfulReset)
                    {
                        bool isEarly = segLockedReset > 0 && cur.Ts < (segLockedReset - 1800) && jumpedUp;
                        // 若未记录到锁定时间但周池在 < 5 天内从 <85% 满血跳回 100%，同样属于官方提前重置
                        if (!isEarly && IsWeeklyBucket(bucket) && jumpedUp && cur.Rem >= 0.98 && segMinRem < 0.88 && (cur.Ts - segStartTs) < 5 * 86400)
                        {
                            if (segLockedReset == 0) isEarly = true;
                        }

                        resets.Add(new RawResetCandidate(
                            ResetTs: cur.Ts,
                            Email: email,
                            Bucket: bucket,
                            IsEarly: isEarly,
                            PreResetRem: Math.Clamp(segMinRem, 0.0, 1.0),
                            PrevScheduledResetTs: segLockedReset,
                            CycleSpentUsd: spentUsd,
                            CycleDropFrac: drop));
                        lastRecordedResetTs = cur.Ts;
                    }

                    segStartTs = cur.Ts;
                    segEndTs = cur.Ts;
                    segStartRem = cur.Rem;
                    segMinRem = cur.Rem;
                    segLockedReset = IsLockedCycle(cur, windowSec) ? cur.ResetTs : 0;
                }
                else
                {
                    segEndTs = cur.Ts;
                    if (cur.Rem > segStartRem && segStartRem < 0.01)
                        segStartRem = cur.Rem;
                    if (cur.Rem < segMinRem)
                        segMinRem = cur.Rem;
                    if (segLockedReset == 0 && IsLockedCycle(cur, windowSec))
                        segLockedReset = cur.ResetTs;
                }
            }

            // 尾段仍在进行的活跃周期也加入容量拟合样本池
            double tailDrop = Math.Max(0.0, segStartRem - segMinRem);
            if (tailDrop >= 0.05 && segEndTs > segStartTs)
            {
                var (tailUsd, tailTok) = SumPoolEvents(acctEvents, bucket, segStartTs - 30, segEndTs + 30);
                segments.Add(new CycleSegment(email, bucket, segStartTs, segEndTs, segStartRem, segMinRem, segLockedReset, tailUsd, tailTok));
            }
        }

        return (segments, resets);
    }

    private static bool IsLockedCycle(RawSnap s, long windowSec)
    {
        if (s.ResetTs <= 0) return false;
        if (s.Rem < 0.999) return true;
        long remainSec = s.ResetTs - s.Ts;
        return remainSec > 0 && remainSec < (windowSec - 300);
    }

    private static (double Usd, long Tokens) SumPoolEvents(List<RawAgEvent>? events, string bucket, long fromTs, long toTs)
    {
        if (events == null || events.Count == 0 || toTs < fromTs) return (0.0, 0L);
        int lo = 0, hi = events.Count;
        while (lo < hi)
        {
            int mid = lo + ((hi - lo) >> 1);
            if (events[mid].Ts < fromTs) lo = mid + 1;
            else hi = mid;
        }

        double usd = 0;
        long tok = 0;
        for (int i = lo; i < events.Count; i++)
        {
            var e = events[i];
            if (e.Ts > toTs) break;
            if (MatchesBucketPool(bucket, e.PoolKind))
            {
                usd += e.CostUsd;
                tok += e.Tokens;
            }
        }
        return (usd, tok);
    }

    private static Dictionary<(string Email, string Bucket), (double CapUsd, bool Measured)> CalibratePoolCaps(
        IReadOnlyList<Profile> profiles,
        List<CycleSegment> segments,
        long nowTs)
    {
        var result = new Dictionary<(string Email, string Bucket), (double CapUsd, bool Measured)>();
        var tierByEmail = profiles
            .Where(p => !string.IsNullOrWhiteSpace(p.Email))
            .GroupBy(p => p.Email.Trim().ToLowerInvariant(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => NormalizeTierGroup(g.First()), StringComparer.OrdinalIgnoreCase);

        var perAcctPoolSamples = new Dictionary<(string Email, string Bucket), List<(double Cap, double Weight)>>();
        var perTierPoolCaps = new Dictionary<(string TierGroup, string Bucket), List<double>>();

        foreach (var seg in segments)
        {
            double drop = seg.StartRem - seg.EndRem;
            if (drop < 0.06 || seg.SpentUsd < 0.08) continue;
            double implied = seg.SpentUsd / drop;
            if (implied < 0.5 || implied > 500.0) continue;

            // 近 7 天样本赋予 3 倍权重，以便官方调整池权重后快速跟随最新真实上限
            double ageDays = Math.Max(0.0, (nowTs - seg.EndTs) / 86400.0);
            double recencyWeight = ageDays <= 3.0 ? 4.0 : (ageDays <= 10.0 ? 2.2 : 1.0);
            double w = drop * recencyWeight;

            var key = (seg.Email.ToLowerInvariant(), seg.Bucket);
            if (!perAcctPoolSamples.TryGetValue(key, out var list))
                perAcctPoolSamples[key] = list = new List<(double, double)>();
            list.Add((implied, w));
        }

        foreach (var (key, samples) in perAcctPoolSamples)
        {
            double wSum = samples.Sum(x => x.Weight);
            if (wSum <= 0) continue;
            double est = samples.Sum(x => x.Cap * x.Weight) / wSum;
            result[key] = (est, true);

            string tg = tierByEmail.TryGetValue(key.Email, out var t) ? t : "pro";
            if (!perTierPoolCaps.TryGetValue((tg, key.Bucket), out var tlist))
                perTierPoolCaps[(tg, key.Bucket)] = tlist = new List<double>();
            tlist.Add(est);
        }

        // 为尚无足够消耗样本的账号/池填充同档位实测中位数或保守基准
        string[] allBuckets = { BucketGemini5h, BucketGeminiWeekly, BucketArgonWeekly, Bucket3p5h, Bucket3pWeekly };
        foreach (var p in profiles)
        {
            string em = (p.Email ?? "").Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(em)) continue;
            string tg = NormalizeTierGroup(p);
            foreach (var bkt in allBuckets)
            {
                if (result.ContainsKey((em, bkt))) continue;
                if (perTierPoolCaps.TryGetValue((tg, bkt), out var peers) && peers.Count > 0)
                {
                    var sorted = peers.OrderBy(x => x).ToList();
                    result[(em, bkt)] = (sorted[sorted.Count / 2], true);
                }
                else
                {
                    result[(em, bkt)] = (DefaultTierPriorCap(tg, bkt), false);
                }
            }
        }

        return result;
    }

    private static string NormalizeTierGroup(Profile p)
    {
        if (p.IsUltra) return "ultra";
        if (p.IsProTier) return p.IsPaidSubscription == false ? "pro_promo" : "pro";
        var t = (p.SubscriptionTier ?? "").ToLowerInvariant();
        if (t.Contains("plus", StringComparison.Ordinal)) return "plus";
        return "starter";
    }

    private static double DefaultTierPriorCap(string tierGroup, string bucket) => (tierGroup, bucket) switch
    {
        ("ultra", BucketGemini5h) => 45.0,
        ("ultra", BucketGeminiWeekly) => 180.0,
        ("ultra", BucketArgonWeekly) => 35.0,
        ("ultra", Bucket3p5h) => 8.0,
        ("ultra", Bucket3pWeekly) => 18.0,

        ("pro" or "pro_promo", BucketGemini5h) => 20.0,
        ("pro" or "pro_promo", BucketGeminiWeekly) => 75.0,
        ("pro", BucketArgonWeekly) => 15.0,
        ("pro_promo", BucketArgonWeekly) => 0.0,
        ("pro" or "pro_promo", Bucket3p5h) => 4.0,
        ("pro" or "pro_promo", Bucket3pWeekly) => 7.2,

        ("plus", BucketGemini5h) => 10.0,
        ("plus", BucketGeminiWeekly) => 30.0,
        ("plus", Bucket3p5h) => 2.5,
        ("plus", Bucket3pWeekly) => 5.0,

        (_, BucketGeminiWeekly) => 10.0,
        (_, Bucket3pWeekly) => 3.0,
        (_, Bucket3p5h) => 1.5,
        _ => 0.0
    };

    private static (double CapUsd, bool Measured) GetCap(
        Dictionary<(string Email, string Bucket), (double CapUsd, bool Measured)> map,
        string email,
        string bucket,
        Profile profile)
    {
        if (map.TryGetValue((email.ToLowerInvariant(), bucket), out var val))
            return val;
        return (DefaultTierPriorCap(NormalizeTierGroup(profile), bucket), false);
    }

    private static (List<QuotaResetEventItem> Enriched, List<MassResetClusterSummary> Clusters) ClassifyAndEnrichResets(
        List<RawResetCandidate> rawResets,
        Dictionary<(string Email, string Bucket), (double CapUsd, bool Measured)> caps)
    {
        // 识别周限池提前回血聚类（45 分钟内 >= 2 个账号，或单账号提前 >12 小时直接满血）
        var earlyWeekly = rawResets
            .Where(r => r.IsEarly && IsWeeklyBucket(r.Bucket))
            .OrderBy(r => r.ResetTs)
            .ToList();

        var clusters = new List<(long StartTs, long EndTs, HashSet<string> Accounts, List<double> Restored)>();
        foreach (var ew in earlyWeekly)
        {
            if (clusters.Count > 0 && (ew.ResetTs - clusters[^1].EndTs) <= 2700)
            {
                var last = clusters[^1];
                last.Accounts.Add(ew.Email);
                last.Restored.Add(Math.Clamp(1.0 - ew.PreResetRem, 0.0, 1.0));
                clusters[^1] = (last.StartTs, ew.ResetTs, last.Accounts, last.Restored);
            }
            else
            {
                clusters.Add((
                    ew.ResetTs,
                    ew.ResetTs,
                    new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ew.Email },
                    new List<double> { Math.Clamp(1.0 - ew.PreResetRem, 0.0, 1.0) }));
            }
        }

        var enriched = new List<QuotaResetEventItem>(rawResets.Count);
        foreach (var r in rawResets.OrderByDescending(x => x.ResetTs))
        {
            int massAcctCount = 0;
            bool isGlobalMass = false;
            if (r.IsEarly)
            {
                foreach (var c in clusters)
                {
                    if (r.ResetTs >= c.StartTs - 1800 && r.ResetTs <= c.EndTs + 1800)
                    {
                        massAcctCount = c.Accounts.Count;
                        isGlobalMass = c.Accounts.Count >= 2 ||
                                       (r.PrevScheduledResetTs > 0 && (r.PrevScheduledResetTs - r.ResetTs) >= 12 * 3600);
                        break;
                    }
                }
            }

            double capUsd = caps.TryGetValue((r.Email.ToLowerInvariant(), r.Bucket), out var cv) ? cv.CapUsd : 15.0;
            double impliedCap = (r.CycleDropFrac >= 0.08 && r.CycleSpentUsd >= 0.10)
                ? (r.CycleSpentUsd / r.CycleDropFrac)
                : capUsd;
            double expiredWaste = (r.IsEarly || !IsWeeklyBucket(r.Bucket)) ? 0.0 : (r.PreResetRem * capUsd);

            DateTime? prevSchedLocal = r.PrevScheduledResetTs > 0
                ? DateTimeOffset.FromUnixTimeSeconds(r.PrevScheduledResetTs).LocalDateTime
                : null;

            enriched.Add(new QuotaResetEventItem(
                ResetTimeLocal: DateTimeOffset.FromUnixTimeSeconds(r.ResetTs).LocalDateTime,
                AccountEmail: r.Email,
                BucketId: r.Bucket,
                PoolTitle: FormatPoolTitle(r.Bucket),
                IsEarlyOfficialReset: r.IsEarly,
                IsGlobalMassReset: isGlobalMass,
                MassResetAccountCount: Math.Max(1, massAcctCount),
                PreResetRemainingFraction: r.PreResetRem,
                PreviousScheduledResetLocal: prevSchedLocal,
                CycleConsumedUsd: r.CycleSpentUsd,
                ExpiredWasteUsd: expiredWaste,
                ImpliedCapUsd: impliedCap));
        }

        var clusterSummaries = clusters
            .Where(c => c.Accounts.Count >= 2 || c.Restored.Any(x => x >= 0.15))
            .OrderByDescending(c => c.StartTs)
            .Select(c => new MassResetClusterSummary(
                OccurredLocal: DateTimeOffset.FromUnixTimeSeconds(c.StartTs).LocalDateTime,
                AccountCount: c.Accounts.Count,
                Accounts: c.Accounts.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList(),
                TotalRestoredFractionAvg: c.Restored.Count > 0 ? c.Restored.Average() : 0.0))
            .ToList();

        return (enriched, clusterSummaries);
    }

    private static List<PoolLiveInsight> BuildLivePoolInsights(
        Profile p,
        string email,
        DateTime nowUtc,
        long nowTs,
        List<RawAgEvent>? acctEvents,
        Dictionary<(string Email, string Bucket), (double CapUsd, bool Measured)> calibratedCaps,
        List<QuotaResetEventItem>? acctResets)
    {
        var list = new List<PoolLiveInsight>();

        void AddPool(string bucket, bool supported, double? remFrac, string? resetIso, TimeSpan window)
        {
            string title = FormatPoolTitle(bucket);
            bool isWeekly = IsWeeklyBucket(bucket);
            if (!supported)
            {
                list.Add(new PoolLiveInsight(
                    bucket, title, isWeekly, false, true, 0, null,
                    I18nService.T("Ag.No5hLimit"), 0, 0, 0, 0, false));
                return;
            }

            double rem = Math.Clamp(remFrac ?? 1.0, 0.0, 1.0);
            bool standby = Profile.IsTimerStandby(remFrac ?? 1.0, resetIso, p.QuotaUpdatedAt, window);
            DateTime? lockedResetLocal = null;
            long cycleStartTs = nowTs - (long)window.TotalSeconds;

            if (!standby &&
                !string.IsNullOrEmpty(resetIso) &&
                DateTime.TryParse(resetIso, CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var rstUtc))
            {
                lockedResetLocal = rstUtc.ToLocalTime();
                long nominalStart = new DateTimeOffset(rstUtc - window).ToUnixTimeSeconds();
                cycleStartTs = nominalStart;
            }

            // 若当前周期内刚经历过提前重置，以最近一次重置时间为真实起点
            var latestReset = acctResets?.FirstOrDefault(r => r.BucketId == bucket);
            if (latestReset != null)
            {
                long rTs = new DateTimeOffset(latestReset.ResetTimeLocal.ToUniversalTime()).ToUnixTimeSeconds();
                if (rTs > cycleStartTs && rTs <= nowTs)
                    cycleStartTs = rTs;
            }

            double curUsd = 0;
            long curTok = 0;
            if (!standby || rem < 0.999)
            {
                var summed = SumPoolEvents(acctEvents, bucket, cycleStartTs - 30, nowTs + 60);
                curUsd = summed.Usd;
                curTok = summed.Tokens;
            }

            var (baseCap, measured) = GetCap(calibratedCaps, email, bucket, p);
            double usedFrac = Math.Max(0.0, 1.0 - rem);
            double estCap = baseCap;
            bool isMeas = measured;

            if (!standby && usedFrac >= 0.08 && curUsd >= 0.15)
            {
                double liveCap = curUsd / usedFrac;
                if (liveCap >= 0.5 && liveCap <= 500.0)
                {
                    estCap = measured ? (0.65 * liveCap + 0.35 * baseCap) : liveCap;
                    isMeas = true;
                }
            }

            double estRemain = rem * estCap;
            string statusText;
            if (standby)
            {
                statusText = I18nService.T("AgInsight.StandbyNotStarted");
            }
            else if (lockedResetLocal.HasValue)
            {
                var left = lockedResetLocal.Value.ToUniversalTime() - nowUtc;
                string leftStr = left.TotalSeconds <= 0
                    ? I18nService.T("Ag.ResetDone")
                    : (left.TotalDays >= 1
                        ? I18nService.F("Ag.LeftDaysHoursFmt", (int)left.TotalDays, left.Hours)
                        : I18nService.F("Ag.LeftHoursMinFmt", (int)left.TotalHours, left.Minutes));
                statusText = $"{lockedResetLocal.Value:MM-dd HH:mm} ({leftStr})";
            }
            else
            {
                statusText = I18nService.T("Ag.NotSynced");
            }

            list.Add(new PoolLiveInsight(
                bucket, title, isWeekly, true, standby, rem, lockedResetLocal,
                statusText, curUsd, curTok, estCap, estRemain, isMeas));
        }

        bool hasGemini5h = p.Quota5hFraction.HasValue || !string.IsNullOrEmpty(p.Quota5hResetTime);
        AddPool(BucketGemini5h, hasGemini5h, p.Quota5hFraction, p.Quota5hResetTime, TimeSpan.FromHours(5));
        AddPool(BucketGeminiWeekly, true, p.QuotaWeeklyFraction, p.QuotaWeeklyResetTime, TimeSpan.FromDays(7));

        bool hasArgon = Profile.ShowArgonQuotaColumn &&
                        (p.QuotaArgonWeeklyFraction.HasValue || (p.IsProTier && p.IsPaidSubscription != false) || p.IsUltra);
        if (hasArgon)
            AddPool(BucketArgonWeekly, true, p.QuotaArgonWeeklyFraction, p.QuotaArgonWeeklyResetTime, TimeSpan.FromDays(7));

        bool has3p5h = p.Quota3p5hFraction.HasValue || !string.IsNullOrEmpty(p.Quota3p5hResetTime);
        if (has3p5h)
            AddPool(Bucket3p5h, true, p.Quota3p5hFraction, p.Quota3p5hResetTime, TimeSpan.FromHours(5));
        AddPool(Bucket3pWeekly, true, p.Quota3pWeeklyFraction, p.Quota3pWeeklyResetTime, TimeSpan.FromDays(7));

        return list;
    }

    private static double ComputeIdleStandbyLossUsd(
        Dictionary<(string Email, string Bucket), List<RawSnap>> snapsByAcctBucket,
        string email,
        string bucket,
        long fromTs,
        long toTs,
        double weeklyCapUsd)
    {
        if (weeklyCapUsd <= 0 || toTs <= fromTs) return 0.0;
        if (!snapsByAcctBucket.TryGetValue((email.ToLowerInvariant(), bucket), out var rawList) || rawList.Count < 2)
            return 0.0;

        int lo = 0, hi = rawList.Count;
        long minTs = fromTs - 3600;
        while (lo < hi)
        {
            int mid = lo + ((hi - lo) >> 1);
            if (rawList[mid].Ts < minTs) lo = mid + 1;
            else hi = mid;
        }

        long idleSec = 0;
        const long weekSec = 7L * 86400L;

        for (int i = Math.Max(1, lo); i < rawList.Count; i++)
        {
            var a = rawList[i - 1];
            var b = rawList[i];
            if (a.Ts > toTs + 3600) break;
            long segStart = Math.Max(fromTs, a.Ts);
            long segEnd = Math.Min(toTs, b.Ts);
            long dt = segEnd - segStart;
            if (dt <= 0 || dt > 86400) continue;

            // 前后两点均处于 100% 未激活待命滑窗状态
            bool aStandby = a.Rem >= 0.999 && (a.ResetTs == 0 || (a.ResetTs - a.Ts) >= weekSec - 600);
            bool bStandby = b.Rem >= 0.999 && (b.ResetTs == 0 || (b.ResetTs - b.Ts) >= weekSec - 600);
            if (aStandby && bStandby)
                idleSec += dt;
        }

        return (idleSec / (double)weekSec) * weeklyCapUsd;
    }

    private static bool IsWeeklyBucket(string bucket) =>
        bucket.EndsWith("weekly", StringComparison.OrdinalIgnoreCase);

    public static string FormatPoolTitle(string bucket) => bucket switch
    {
        BucketGemini5h => I18nService.T("AgInsight.PoolGem5h"),
        BucketGeminiWeekly => I18nService.T("AgInsight.PoolGemWeekly"),
        BucketArgonWeekly => I18nService.T("AgInsight.PoolArgonWeekly"),
        Bucket3p5h => I18nService.T("AgInsight.Pool3p5h"),
        Bucket3pWeekly => I18nService.T("AgInsight.Pool3pWeekly"),
        _ => bucket
    };
}

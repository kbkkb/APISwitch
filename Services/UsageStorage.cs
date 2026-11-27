using Microsoft.Data.Sqlite;
using System.IO;

namespace APISwitch.Services;

/// <summary>
/// 用量监控存储：记录经本地网关的第三方请求 token 消耗与 Antigravity 配额快照。
/// 存储于 %APPDATA%\APISwitch\usage.db（SQLite）。
/// </summary>
public static class UsageStorage
{
    internal static string DbPath => Path.Combine(AgPaths.AppData, "APISwitch", "usage.db");
    static string? _initializedPath;
    static long _mutationVersion = 1;

    /// <summary>进程内纯内存原子写入代数（0 I/O、0 锁，供 UI 缓存瞬时判脏）。</summary>
    public static long MutationVersion => System.Threading.Interlocked.Read(ref _mutationVersion);

    internal static void BumpMutationVersion() => System.Threading.Interlocked.Increment(ref _mutationVersion);

    // agent 类型常量（与本地网关路由对应）
    public const string AgentCodex = "codex";
    public const string AgentClaudeCli = "claude-cli";
    public const string AgentClaudeDesktop = "claude-desktop";
    public const string AgentOpenCode = "opencode";
    public const string AgentPi = "pi";

    // v0.3.2 新增：通过本地会话/存储文件统计的 CLI agent（不做供应商替换，仅统计）
    public const string AgentStepCode = "stepcode";
    public const string AgentZCode = "zcode";
    public const string AgentDsh = "dsh";
    public const string AgentGeminiCli = "gemini-cli";
    public const string AgentAntigravity = "antigravity";

    internal static void EnsureInit()
    {
        var path = DbPath;
        if (_initializedPath == path) return;
        lock (typeof(UsageStorage))
        {
            path = DbPath;
            if (_initializedPath == path) return;
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            using var conn = new SqliteConnection($"Data Source={path}");
            conn.Open();

            var cmd = conn.CreateCommand();
            cmd.CommandText = @"
PRAGMA journal_mode=WAL;
PRAGMA synchronous=NORMAL;
PRAGMA busy_timeout=5000;

CREATE TABLE IF NOT EXISTS usage_events (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    ts INTEGER NOT NULL,
    agent TEXT NOT NULL,
    provider_id TEXT NOT NULL DEFAULT '',
    provider_name TEXT NOT NULL DEFAULT '',
    model TEXT NOT NULL DEFAULT '',
    requests INTEGER NOT NULL DEFAULT 1,
    in_tokens INTEGER NOT NULL DEFAULT 0,
    out_tokens INTEGER NOT NULL DEFAULT 0,
    cache_read_tokens INTEGER NOT NULL DEFAULT 0,
    cache_write_tokens INTEGER NOT NULL DEFAULT 0,
    duration_ms INTEGER NOT NULL DEFAULT 0,
    ttft_ms INTEGER NOT NULL DEFAULT 0,
    source_id TEXT NOT NULL DEFAULT ''
);
CREATE INDEX IF NOT EXISTS idx_usage_ts ON usage_events(ts);
CREATE INDEX IF NOT EXISTS idx_usage_agent ON usage_events(agent);
CREATE INDEX IF NOT EXISTS idx_usage_agent_ts ON usage_events(agent, ts);

CREATE TABLE IF NOT EXISTS quota_snapshots (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    ts INTEGER NOT NULL,
    account_email TEXT NOT NULL DEFAULT '',
    bucket TEXT NOT NULL DEFAULT '',
    remaining_fraction REAL NOT NULL DEFAULT 0,
    reset_time INTEGER NOT NULL DEFAULT 0
);
CREATE INDEX IF NOT EXISTS idx_quota_ts ON quota_snapshots(ts);
CREATE INDEX IF NOT EXISTS idx_quota_acct ON quota_snapshots(account_email);
CREATE INDEX IF NOT EXISTS idx_quota_acct_bkt_ts ON quota_snapshots(account_email, bucket, ts);

CREATE TABLE IF NOT EXISTS account_sessions (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    start_ts INTEGER NOT NULL,
    last_verified_ts INTEGER NOT NULL,
    account_email TEXT NOT NULL DEFAULT ''
);
CREATE INDEX IF NOT EXISTS idx_acct_sess_start ON account_sessions(start_ts);
";
            cmd.ExecuteNonQuery();
            MigrateUsageEventsColumns(conn);
            _initializedPath = path;
        }
    }

    /// <summary>
    /// 旧库补列：用量监控的缓存/速度/首字/幂等去重键是后加的，
    /// 用 PRAGMA 探测后 ALTER TABLE 补齐，并建立条件唯一索引。
    /// </summary>
    static void MigrateUsageEventsColumns(SqliteConnection conn)
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var probe = conn.CreateCommand())
        {
            probe.CommandText = "PRAGMA table_info(usage_events);";
            using var r = probe.ExecuteReader();
            while (r.Read())
            {
                if (!r.IsDBNull(1)) existing.Add(r.GetString(1));
            }
        }

        void Add(string name, string ddl)
        {
            if (existing.Contains(name)) return;
            try
            {
                using var c = conn.CreateCommand();
                c.CommandText = $"ALTER TABLE usage_events ADD COLUMN {ddl};";
                c.ExecuteNonQuery();
            }
            catch { }
        }

        Add("cache_read_tokens", "cache_read_tokens INTEGER NOT NULL DEFAULT 0");
        Add("cache_write_tokens", "cache_write_tokens INTEGER NOT NULL DEFAULT 0");
        Add("duration_ms", "duration_ms INTEGER NOT NULL DEFAULT 0");
        Add("ttft_ms", "ttft_ms INTEGER NOT NULL DEFAULT 0");
        Add("source_id", "source_id TEXT NOT NULL DEFAULT ''");

        try
        {
            using var idx = conn.CreateCommand();
            idx.CommandText = @"
CREATE UNIQUE INDEX IF NOT EXISTS idx_usage_source_id ON usage_events(source_id) WHERE source_id <> '';
CREATE INDEX IF NOT EXISTS idx_usage_agent_ts ON usage_events(agent, ts);
CREATE INDEX IF NOT EXISTS idx_quota_acct_bkt_ts ON quota_snapshots(account_email, bucket, ts);";
            idx.ExecuteNonQuery();
        }
        catch { }
    }

    const string UpsertUsageSql = @"INSERT INTO usage_events
(ts, agent, provider_id, provider_name, model, requests, in_tokens, out_tokens,
 cache_read_tokens, cache_write_tokens, duration_ms, ttft_ms, source_id)
VALUES ($ts, $agent, $pid, $pname, $model, 1, $tin, $tout, $cr, $cw, $dur, $ttft, $sid)
ON CONFLICT(source_id) WHERE source_id <> '' DO UPDATE SET
  in_tokens = MAX(usage_events.in_tokens, excluded.in_tokens),
  out_tokens = MAX(usage_events.out_tokens, excluded.out_tokens),
  cache_read_tokens = MAX(usage_events.cache_read_tokens, excluded.cache_read_tokens),
  cache_write_tokens = MAX(usage_events.cache_write_tokens, excluded.cache_write_tokens),
  duration_ms = MAX(usage_events.duration_ms, excluded.duration_ms),
  ttft_ms = CASE WHEN usage_events.ttft_ms > 0 AND excluded.ttft_ms > 0
                 THEN MIN(usage_events.ttft_ms, excluded.ttft_ms)
                 ELSE MAX(usage_events.ttft_ms, excluded.ttft_ms) END,
  model = CASE WHEN usage_events.model = '' AND excluded.model <> '' THEN excluded.model ELSE usage_events.model END,
  provider_id = CASE
    WHEN usage_events.provider_id <> '' AND usage_events.provider_id <> 'custom' THEN usage_events.provider_id
    WHEN excluded.provider_id <> '' THEN excluded.provider_id
    ELSE usage_events.provider_id END,
  provider_name = CASE
    WHEN usage_events.provider_name <> '' AND usage_events.provider_name NOT IN ('Codex', 'Claude Code', 'OpenCode', 'Pi', 'StepCode', 'zcode', 'dsh', 'Google', 'Antigravity', 'any') THEN usage_events.provider_name
    WHEN excluded.provider_name <> '' THEN excluded.provider_name
    ELSE usage_events.provider_name END;";

    /// <summary>记录一次网关请求的 token 消耗。</summary>
    public static void RecordUsage(string agent, string providerId, string providerName, string model,
        long inTokens, long outTokens, DateTime? ts = null)
        => RecordUsage(agent, providerId, providerName, model, inTokens, outTokens, ts,
            cacheReadTokens: 0, cacheWriteTokens: 0, durationMs: 0, ttftMs: 0, sourceId: "");

    /// <summary>
    /// 记录一次请求的用量与性能指标。
    /// sourceId 非空时自动按 source_id 幂等合并（防止分块/会话恢复/网关与扫描双轨重复统计）。
    /// </summary>
    public static void RecordUsage(string agent, string providerId, string providerName, string model,
        long inTokens, long outTokens, DateTime? ts,
        long cacheReadTokens, long cacheWriteTokens, long durationMs, long ttftMs,
        string sourceId = "")
    {
        try
        {
            EnsureInit();
            using var conn = new SqliteConnection($"Data Source={DbPath}");
            conn.Open();
            var cmd = conn.CreateCommand();
            cmd.CommandText = UpsertUsageSql;
            var t = (ts ?? DateTime.Now).ToUniversalTime();
            cmd.Parameters.AddWithValue("$ts", new DateTimeOffset(t).ToUnixTimeSeconds());
            cmd.Parameters.AddWithValue("$agent", agent);
            cmd.Parameters.AddWithValue("$pid", providerId ?? "");
            cmd.Parameters.AddWithValue("$pname", providerName ?? "");
            cmd.Parameters.AddWithValue("$model", model ?? "");
            cmd.Parameters.AddWithValue("$tin", Math.Max(0, inTokens));
            cmd.Parameters.AddWithValue("$tout", Math.Max(0, outTokens));
            cmd.Parameters.AddWithValue("$cr", Math.Max(0, cacheReadTokens));
            cmd.Parameters.AddWithValue("$cw", Math.Max(0, cacheWriteTokens));
            cmd.Parameters.AddWithValue("$dur", Math.Max(0, durationMs));
            cmd.Parameters.AddWithValue("$ttft", Math.Max(0, ttftMs));
            cmd.Parameters.AddWithValue("$sid", sourceId ?? "");
            cmd.ExecuteNonQuery();
            BumpMutationVersion();
        }
        catch { }
    }

    /// <summary>单条待入库用量记录。</summary>
    public record UsageRecord(string Agent, string ProviderId, string ProviderName, string Model,
        long InTokens, long OutTokens, DateTime Ts,
        long CacheReadTokens = 0, long CacheWriteTokens = 0, long DurationMs = 0, long TtftMs = 0,
        string SourceId = "");

    /// <summary>批量写入用量事件（使用单事务批处理与幂等 Upsert）。返回是否成功提交。</summary>
    public static bool RecordBatch(IEnumerable<UsageRecord> records)
    {
        try
        {
            EnsureInit();
            using var conn = new SqliteConnection($"Data Source={DbPath}");
            conn.Open();
            using var tx = conn.BeginTransaction();
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = UpsertUsageSql;

            var pTs = cmd.Parameters.Add("$ts", SqliteType.Integer);
            var pAgent = cmd.Parameters.Add("$agent", SqliteType.Text);
            var pPid = cmd.Parameters.Add("$pid", SqliteType.Text);
            var pPname = cmd.Parameters.Add("$pname", SqliteType.Text);
            var pModel = cmd.Parameters.Add("$model", SqliteType.Text);
            var pTin = cmd.Parameters.Add("$tin", SqliteType.Integer);
            var pTout = cmd.Parameters.Add("$tout", SqliteType.Integer);
            var pCr = cmd.Parameters.Add("$cr", SqliteType.Integer);
            var pCw = cmd.Parameters.Add("$cw", SqliteType.Integer);
            var pDur = cmd.Parameters.Add("$dur", SqliteType.Integer);
            var pTtft = cmd.Parameters.Add("$ttft", SqliteType.Integer);
            var pSid = cmd.Parameters.Add("$sid", SqliteType.Text);

            foreach (var r in records)
            {
                var t = r.Ts.ToUniversalTime();
                pTs.Value = new DateTimeOffset(t).ToUnixTimeSeconds();
                pAgent.Value = r.Agent;
                pPid.Value = r.ProviderId ?? "";
                pPname.Value = r.ProviderName ?? "";
                pModel.Value = r.Model ?? "";
                pTin.Value = Math.Max(0, r.InTokens);
                pTout.Value = Math.Max(0, r.OutTokens);
                pCr.Value = Math.Max(0, r.CacheReadTokens);
                pCw.Value = Math.Max(0, r.CacheWriteTokens);
                pDur.Value = Math.Max(0, r.DurationMs);
                pTtft.Value = Math.Max(0, r.TtftMs);
                pSid.Value = r.SourceId ?? "";
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
            BumpMutationVersion();
            return true;
        }
        catch (Exception ex)
        {
            App.LogStartup("UsageStorage.RecordBatch failed: " + ex.Message);
            return false;
        }
    }

    static string BuildAgentFilter(SqliteCommand cmd, string? agentFilter)
    {
        if (string.IsNullOrWhiteSpace(agentFilter)) return "";
        cmd.Parameters.AddWithValue("$af", agentFilter);
        return " AND agent = $af";
    }

    /// <summary>性能与缓存指标汇总。</summary>
    public record PerformanceStats(
        long CacheRead, long CacheWrite,
        long TimedRequests, long TotalDurationMs, long TotalOutTokens,
        long TtftSamples, long TotalTtftMs,
        long MaxSpeedTokensPerSec);

    /// <summary>
    /// 汇总指定时间范围的缓存命中与速度/首字指标。
    /// 缓存命中率 = cacheRead / (input + cacheRead + cacheWrite)。
    /// </summary>
    public static PerformanceStats GetPerformance(long fromTs, long toTs, string? agentFilter = null)
    {
        try
        {
            EnsureInit();
            using var conn = new SqliteConnection($"Data Source={DbPath}");
            conn.Open();
            var cmd = conn.CreateCommand();
            var af = BuildAgentFilter(cmd, agentFilter);
            cmd.CommandText = $@"SELECT
  COALESCE(SUM(cache_read_tokens),0),
  COALESCE(SUM(cache_write_tokens),0),
  COALESCE(SUM(CASE WHEN duration_ms > 0 THEN 1 ELSE 0 END),0),
  COALESCE(SUM(duration_ms),0),
  COALESCE(SUM(CASE WHEN duration_ms > 0 THEN out_tokens ELSE 0 END),0),
  COALESCE(SUM(CASE WHEN ttft_ms > 0 THEN 1 ELSE 0 END),0),
  COALESCE(SUM(ttft_ms),0),
  COALESCE(MAX(CASE WHEN duration_ms > 0 THEN out_tokens * 1000.0 / duration_ms END),0)
FROM usage_events WHERE ts >= $from AND ts <= $to{af};";
            cmd.Parameters.AddWithValue("$from", fromTs);
            cmd.Parameters.AddWithValue("$to", toTs);
            using var r = cmd.ExecuteReader();
            if (r.Read())
            {
                return new PerformanceStats(
                    r.GetInt64(0), r.GetInt64(1), r.GetInt64(2), r.GetInt64(3),
                    r.GetInt64(4), r.GetInt64(5), r.GetInt64(6),
                    (long)Math.Round(r.GetDouble(7)));
            }
        }
        catch { }
        return new PerformanceStats(0, 0, 0, 0, 0, 0, 0, 0);
    }

    public static string FormatAntigravityProviderId(string? email) =>
        string.IsNullOrWhiteSpace(email) ? "" : "ag:" + email.Trim().ToLowerInvariant();

    public static string FormatAntigravityProviderName(string? email) =>
        string.IsNullOrWhiteSpace(email) ? "Antigravity" : "Antigravity · " + email.Trim().ToLowerInvariant();

    /// <summary>记录一次配额快照（Antigravity）。</summary>
    public static void RecordQuotaSnapshot(string accountEmail, string bucket, double remainingFraction, DateTime? resetTime, DateTime? snapshotUtc = null)
    {
        try
        {
            EnsureInit();
            using var conn = new SqliteConnection($"Data Source={DbPath}");
            conn.Open();
            var cmd = conn.CreateCommand();
            cmd.CommandText = @"INSERT INTO quota_snapshots (ts, account_email, bucket, remaining_fraction, reset_time)
VALUES ($ts, $acct, $bucket, $frac, $reset);";
            long tsSec = snapshotUtc.HasValue
                ? new DateTimeOffset(resetTimeValue(snapshotUtc)).ToUnixTimeSeconds()
                : DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            cmd.Parameters.AddWithValue("$ts", tsSec);
            cmd.Parameters.AddWithValue("$acct", accountEmail ?? "");
            cmd.Parameters.AddWithValue("$bucket", bucket ?? "");
            cmd.Parameters.AddWithValue("$frac", remainingFraction);
            cmd.Parameters.AddWithValue("$reset", resetTime.HasValue ? new DateTimeOffset(resetTimeValue(resetTime)).ToUnixTimeSeconds() : 0L);
            cmd.ExecuteNonQuery();
            BumpMutationVersion();
        }
        catch { }
    }

    /// <summary>
    /// 记录或续期 APISwitch 在线确认过的当前 Antigravity 登录账号时间段。
    /// 若距上次确认超过 45 分钟（说明期间程序曾退出），则新建独立时间段而不跨空白期串联。
    /// </summary>
    public static void RecordAccountSession(string? accountEmail, DateTime? tsUtc = null)
    {
        var email = accountEmail?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(email) || !email.Contains('@')) return;

        try
        {
            EnsureInit();
            long nowTs = new DateTimeOffset((tsUtc ?? DateTime.UtcNow).ToUniversalTime()).ToUnixTimeSeconds();
            using var conn = new SqliteConnection($"Data Source={DbPath}");
            conn.Open();

            long lastId = 0, lastVerified = 0;
            string lastEmail = "";
            using (var sel = conn.CreateCommand())
            {
                sel.CommandText = "SELECT id, account_email, last_verified_ts FROM account_sessions ORDER BY start_ts DESC, id DESC LIMIT 1;";
                using var r = sel.ExecuteReader();
                if (r.Read())
                {
                    lastId = r.GetInt64(0);
                    lastEmail = r.IsDBNull(1) ? "" : r.GetString(1);
                    lastVerified = r.GetInt64(2);
                }
            }

            if (lastId > 0 &&
                string.Equals(lastEmail, email, StringComparison.OrdinalIgnoreCase) &&
                nowTs >= lastVerified - 60 &&
                (nowTs - lastVerified) <= 2700)
            {
                if (nowTs > lastVerified)
                {
                    using var upd = conn.CreateCommand();
                    upd.CommandText = "UPDATE account_sessions SET last_verified_ts = $ts WHERE id = $id;";
                    upd.Parameters.AddWithValue("$ts", nowTs);
                    upd.Parameters.AddWithValue("$id", lastId);
                    upd.ExecuteNonQuery();
                }
                return;
            }

            using var ins = conn.CreateCommand();
            ins.CommandText = "INSERT INTO account_sessions (start_ts, last_verified_ts, account_email) VALUES ($s, $v, $e);";
            ins.Parameters.AddWithValue("$s", nowTs);
            ins.Parameters.AddWithValue("$v", nowTs);
            ins.Parameters.AddWithValue("$e", email);
            ins.ExecuteNonQuery();
        }
        catch { }
    }

    public static long GetAccountAttributionSourceWatermark()
    {
        try
        {
            EnsureInit();
            using var conn = new SqliteConnection($"Data Source={DbPath}");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"SELECT
  (SELECT COALESCE(MAX(id), 0) FROM quota_snapshots),
  (SELECT COALESCE(MAX(id), 0) FROM account_sessions),
  (SELECT COALESCE(MAX(last_verified_ts), 0) FROM account_sessions);";
            using var r = cmd.ExecuteReader();
            if (r.Read())
                return (((r.GetInt64(0) * 397L) ^ r.GetInt64(1)) * 397L) ^ r.GetInt64(2);
        }
        catch { }
        return 0;
    }

    /// <summary>
    /// 极速（&lt;0.1ms）读取 usage.db 变更签名，用于分析报告与监控视图的内存快照命中判定。
    /// 同时覆盖新行写入、近期行模型/账号原地补齐与配额快照更新。
    /// </summary>
    public static long GetFastDataChangeToken()
    {
        try
        {
            EnsureInit();
            using var conn = new SqliteConnection($"Data Source={DbPath}");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"SELECT
  (SELECT COALESCE(MAX(id), 0) FROM usage_events),
  (SELECT COALESCE(SUM(in_tokens + out_tokens + LENGTH(provider_id) * 13 + LENGTH(model) * 29), 0)
   FROM usage_events
   WHERE id > (SELECT COALESCE(MAX(id), 0) - 80 FROM usage_events)),
  (SELECT COALESCE(MAX(id), 0) FROM quota_snapshots),
  (SELECT COALESCE(MAX(last_verified_ts), 0) FROM account_sessions);";
            using var r = cmd.ExecuteReader();
            if (r.Read())
            {
                long h = 17;
                h = h * 397L ^ r.GetInt64(0);
                h = h * 397L ^ r.GetInt64(1);
                h = h * 397L ^ r.GetInt64(2);
                h = h * 397L ^ r.GetInt64(3);
                return h;
            }
        }
        catch { }
        return DateTime.UtcNow.Ticks;
    }

    static DateTime resetTimeValue(DateTime? dt) => dt!.Value.Kind == DateTimeKind.Utc ? dt.Value : dt.Value.ToUniversalTime();

    public record UsageSummary(long TotalIn, long TotalOut, long Requests, int ActiveDays);

    /// <summary>Antigravity 桌面悬浮窗专属单端统计快照（今日用量 + 最近一步真实速率/首字/模型）。</summary>
    public sealed record AntigravityWidgetStats(
        long TodayInTokens,
        long TodayOutTokens,
        long TodayCacheReadTokens,
        long TodayRequests,
        double LastSpeedTokPerSec,
        long LastTtftMs,
        string LastModel,
        DateTime? LastCallLocalTime,
        double TodayAvgSpeedTokPerSec)
    {
        public long TodayTotalTokens => TodayInTokens + TodayOutTokens;
    }

    /// <summary>查询 Antigravity 单端的今日统计与最新一步速率/首字耗时。</summary>
    public static AntigravityWidgetStats GetAntigravityWidgetStats(DateTime? localNow = null)
    {
        try
        {
            EnsureInit();
            var now = localNow ?? DateTime.Now;
            var dayStartLocal = new DateTime(now.Year, now.Month, now.Day, 0, 0, 0, DateTimeKind.Local);
            long fromTs = new DateTimeOffset(dayStartLocal.ToUniversalTime()).ToUnixTimeSeconds();
            long toTs = new DateTimeOffset(now.ToUniversalTime().AddMinutes(2)).ToUnixTimeSeconds();

            using var conn = new SqliteConnection($"Data Source={DbPath}");
            conn.Open();

            long todayIn = 0, todayOut = 0, todayCache = 0, todayReqs = 0;
            double todayAvgSpeed = 0;

            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"SELECT
  COALESCE(SUM(in_tokens),0),
  COALESCE(SUM(out_tokens),0),
  COALESCE(SUM(cache_read_tokens),0),
  COALESCE(SUM(requests),0),
  COALESCE(SUM(CASE WHEN duration_ms > 0 THEN out_tokens ELSE 0 END),0),
  COALESCE(SUM(CASE WHEN duration_ms > 0 THEN duration_ms ELSE 0 END),0)
FROM usage_events WHERE agent = $ag AND ts >= $from AND ts <= $to;";
                cmd.Parameters.AddWithValue("$ag", AgentAntigravity);
                cmd.Parameters.AddWithValue("$from", fromTs);
                cmd.Parameters.AddWithValue("$to", toTs);
                using var r = cmd.ExecuteReader();
                if (r.Read())
                {
                    todayIn = r.GetInt64(0);
                    todayOut = r.GetInt64(1);
                    todayCache = r.GetInt64(2);
                    todayReqs = r.GetInt64(3);
                    long timedOut = r.GetInt64(4);
                    long timedDurMs = r.GetInt64(5);
                    if (timedDurMs > 0) todayAvgSpeed = timedOut * 1000.0 / timedDurMs;
                }
            }

            double lastSpeed = 0;
            long lastTtft = 0;
            string lastModel = "";
            DateTime? lastCallLocalTime = null;

            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"SELECT ts, model, out_tokens, duration_ms, ttft_ms
FROM usage_events WHERE agent = $ag AND ts >= $from AND ts <= $to
ORDER BY ts DESC, id DESC LIMIT 30;";
                cmd.Parameters.AddWithValue("$ag", AgentAntigravity);
                cmd.Parameters.AddWithValue("$from", fromTs);
                cmd.Parameters.AddWithValue("$to", toTs);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    if (!lastCallLocalTime.HasValue)
                        lastCallLocalTime = DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(0)).LocalDateTime;
                    if (string.IsNullOrEmpty(lastModel))
                    {
                        var m = r.IsDBNull(1) ? "" : r.GetString(1);
                        if (!string.IsNullOrWhiteSpace(m)) lastModel = m.Trim();
                    }
                    if (lastSpeed <= 0)
                    {
                        long ot = r.GetInt64(2);
                        long dur = r.GetInt64(3);
                        if (dur > 0 && ot > 0) lastSpeed = ot * 1000.0 / dur;
                    }
                    if (lastTtft <= 0)
                    {
                        long ttft = r.GetInt64(4);
                        if (ttft > 0) lastTtft = ttft;
                    }
                    if (lastCallLocalTime.HasValue && !string.IsNullOrEmpty(lastModel) && lastSpeed > 0 && lastTtft > 0)
                        break;
                }
            }

            if (string.IsNullOrEmpty(lastModel))
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"SELECT model FROM usage_events WHERE agent = $ag AND model <> '' ORDER BY ts DESC, id DESC LIMIT 1;";
                cmd.Parameters.AddWithValue("$ag", AgentAntigravity);
                var obj = cmd.ExecuteScalar();
                if (obj is string s && !string.IsNullOrWhiteSpace(s))
                    lastModel = s.Trim();
            }

            return new AntigravityWidgetStats(todayIn, todayOut, todayCache, todayReqs, lastSpeed, lastTtft, lastModel, lastCallLocalTime, todayAvgSpeed);
        }
        catch
        {
            return new AntigravityWidgetStats(0, 0, 0, 0, 0, 0, "", null, 0);
        }
    }

    /// <summary>汇总指定时间范围（UTC 起止秒）的用量。</summary>
    public static UsageSummary GetSummary(long fromTs, long toTs, string? agentFilter = null)
    {
        try
        {
            EnsureInit();
            using var conn = new SqliteConnection($"Data Source={DbPath}");
            conn.Open();
            var cmd = conn.CreateCommand();
            var af = BuildAgentFilter(cmd, agentFilter);
            cmd.CommandText = $@"SELECT COALESCE(SUM(in_tokens),0), COALESCE(SUM(out_tokens),0), COALESCE(SUM(requests),0), COUNT(DISTINCT strftime('%Y-%m-%d', ts, 'unixepoch', 'localtime'))
FROM usage_events WHERE ts >= $from AND ts <= $to{af};";
            cmd.Parameters.AddWithValue("$from", fromTs);
            cmd.Parameters.AddWithValue("$to", toTs);
            using var r = cmd.ExecuteReader();
            if (r.Read())
                return new UsageSummary(r.GetInt64(0), r.GetInt64(1), r.GetInt64(2), r.GetInt32(3));
        }
        catch { }
        return new UsageSummary(0, 0, 0, 0);
    }

    public record UsageEventRow(
        DateTime Ts, string Agent, string ProviderId, string ProviderName, string Model,
        long InTokens, long OutTokens,
        long CacheReadTokens, long CacheWriteTokens, long DurationMs, long TtftMs);

    /// <summary>指定时间范围内的调用明细（按时间倒序）。ProviderId 非空时为真实供应商标识。</summary>
    public static List<UsageEventRow> GetRecentEvents(long fromTs, long toTs, int limit = 300, string? agentFilter = null)
    {
        var list = new List<UsageEventRow>();
        try
        {
            EnsureInit();
            using var conn = new SqliteConnection($"Data Source={DbPath}");
            conn.Open();
            var cmd = conn.CreateCommand();
            var af = BuildAgentFilter(cmd, agentFilter);
            cmd.CommandText = $@"SELECT ts, agent, provider_id, provider_name, model, in_tokens, out_tokens,
       cache_read_tokens, cache_write_tokens, duration_ms, ttft_ms
FROM usage_events WHERE ts >= $from AND ts <= $to{af} ORDER BY ts DESC LIMIT $lim;";
            cmd.Parameters.AddWithValue("$from", fromTs);
            cmd.Parameters.AddWithValue("$to", toTs);
            cmd.Parameters.AddWithValue("$lim", limit);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                list.Add(new UsageEventRow(
                    DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(0)).LocalDateTime,
                    r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4),
                    r.GetInt64(5), r.GetInt64(6), r.GetInt64(7), r.GetInt64(8), r.GetInt64(9), r.GetInt64(10)));
            }
        }
        catch { }
        return list;
    }

    public record AgentTotal(string Agent, long InTokens, long OutTokens, long Requests, long CacheRead = 0, long CacheWrite = 0);

    /// <summary>按 Agent 汇总（指定时间范围）。</summary>
    public static List<AgentTotal> GetAgentTotals(long fromTs, long toTs)
    {
        var list = new List<AgentTotal>();
        try
        {
            EnsureInit();
            using var conn = new SqliteConnection($"Data Source={DbPath}");
            conn.Open();
            var cmd = conn.CreateCommand();
            cmd.CommandText = @"SELECT agent, SUM(in_tokens), SUM(out_tokens), SUM(requests),
       COALESCE(SUM(cache_read_tokens),0), COALESCE(SUM(cache_write_tokens),0)
FROM usage_events WHERE ts >= $from AND ts <= $to GROUP BY agent ORDER BY SUM(in_tokens + out_tokens) DESC;";
            cmd.Parameters.AddWithValue("$from", fromTs);
            cmd.Parameters.AddWithValue("$to", toTs);
            using var r = cmd.ExecuteReader();
            while (r.Read())
                list.Add(new AgentTotal(r.GetString(0), r.GetInt64(1), r.GetInt64(2), r.GetInt64(3), r.GetInt64(4), r.GetInt64(5)));
        }
        catch { }
        return list;
    }

    public record GroupTotal(string Key, long InTokens, long OutTokens, long Requests, long CacheRead, long CacheWrite);

    /// <summary>按模型汇总（指定时间范围，按总量降序）。</summary>
    public static List<GroupTotal> GetModelTotals(long fromTs, long toTs, int limit = 15)
    {
        var list = new List<GroupTotal>();
        try
        {
            EnsureInit();
            using var conn = new SqliteConnection($"Data Source={DbPath}");
            conn.Open();
            var cmd = conn.CreateCommand();
            cmd.CommandText = @"SELECT model, SUM(in_tokens), SUM(out_tokens), SUM(requests),
       COALESCE(SUM(cache_read_tokens),0), COALESCE(SUM(cache_write_tokens),0)
FROM usage_events WHERE ts >= $from AND ts <= $to GROUP BY model ORDER BY SUM(in_tokens + out_tokens) DESC LIMIT $lim;";
            cmd.Parameters.AddWithValue("$from", fromTs);
            cmd.Parameters.AddWithValue("$to", toTs);
            cmd.Parameters.AddWithValue("$lim", limit);
            using var r = cmd.ExecuteReader();
            while (r.Read())
                list.Add(new GroupTotal(r.GetString(0), r.GetInt64(1), r.GetInt64(2), r.GetInt64(3), r.GetInt64(4), r.GetInt64(5)));
        }
        catch { }
        return list;
    }

    /// <summary>按供应商汇总（指定时间范围，按总量降序）。优先取真实供应商 ID。</summary>
    public static List<GroupTotal> GetProviderTotals(long fromTs, long toTs, int limit = 15)
    {
        var list = new List<GroupTotal>();
        try
        {
            EnsureInit();
            using var conn = new SqliteConnection($"Data Source={DbPath}");
            conn.Open();
            var cmd = conn.CreateCommand();
            cmd.CommandText = @"SELECT CASE
       WHEN agent = 'antigravity' THEN 'Antigravity'
       WHEN provider_id <> '' THEN provider_id
       WHEN provider_name <> '' AND provider_name <> 'Codex' AND provider_name <> 'Claude Code'
            AND provider_name <> 'OpenCode' AND provider_name <> 'Pi' AND provider_name <> 'Antigravity' THEN provider_name
       ELSE agent END, SUM(in_tokens), SUM(out_tokens), SUM(requests),
       COALESCE(SUM(cache_read_tokens),0), COALESCE(SUM(cache_write_tokens),0)
FROM usage_events WHERE ts >= $from AND ts <= $to GROUP BY 1 ORDER BY SUM(in_tokens + out_tokens) DESC LIMIT $lim;";
            cmd.Parameters.AddWithValue("$from", fromTs);
            cmd.Parameters.AddWithValue("$to", toTs);
            cmd.Parameters.AddWithValue("$lim", limit);
            using var r = cmd.ExecuteReader();
            while (r.Read())
                list.Add(new GroupTotal(r.GetString(0), r.GetInt64(1), r.GetInt64(2), r.GetInt64(3), r.GetInt64(4), r.GetInt64(5)));
        }
        catch { }
        return list;
    }

    public record ProviderModelTotal(string Provider, string Model, long InTokens, long OutTokens, long Requests, long CacheRead, long CacheWrite);
    public record AntigravityAccountModelTotal(string AccountEmail, string Model, long InTokens, long OutTokens, long Requests, long CacheRead, long CacheWrite);
    public record AgentProviderModelTotal(string Agent, string Provider, string Model, long InTokens, long OutTokens, long Requests, long CacheRead, long CacheWrite);

    /// <summary>按 Google 账号 × 模型汇总 Antigravity 专属明细（供「反重力明细」独立分页使用）。</summary>
    public static List<AntigravityAccountModelTotal> GetAntigravityAccountModelTotals(long fromTs, long toTs, int limit = 600)
    {
        var list = new List<AntigravityAccountModelTotal>();
        try
        {
            EnsureInit();
            using var conn = new SqliteConnection($"Data Source={DbPath}");
            conn.Open();
            var cmd = conn.CreateCommand();
            cmd.CommandText = @"SELECT CASE
       WHEN provider_id LIKE 'ag:%' AND LENGTH(provider_id) > 3 THEN LOWER(TRIM(SUBSTR(provider_id, 4)))
       ELSE '' END,
       model, SUM(in_tokens), SUM(out_tokens), SUM(requests),
       COALESCE(SUM(cache_read_tokens),0), COALESCE(SUM(cache_write_tokens),0)
FROM usage_events
WHERE agent = $ag AND ts >= $from AND ts <= $to
GROUP BY 1, 2 ORDER BY SUM(in_tokens + out_tokens) DESC LIMIT $lim;";
            cmd.Parameters.AddWithValue("$ag", AgentAntigravity);
            cmd.Parameters.AddWithValue("$from", fromTs);
            cmd.Parameters.AddWithValue("$to", toTs);
            cmd.Parameters.AddWithValue("$lim", limit);
            using var r = cmd.ExecuteReader();
            while (r.Read())
                list.Add(new AntigravityAccountModelTotal(
                    r.GetString(0), r.GetString(1), r.GetInt64(2), r.GetInt64(3),
                    r.GetInt64(4), r.GetInt64(5), r.GetInt64(6)));
        }
        catch { }
        return list;
    }

    /// <summary>
    /// 按 客户端 × 供应商 × 模型 三维全量汇总，确保不同供应商同模型的差异化单价/倍率精确作用于客户端汇总与全局 KPI。
    /// </summary>
    public static List<AgentProviderModelTotal> GetAgentProviderModelTotals(long fromTs, long toTs, string? agentFilter = null)
    {
        var list = new List<AgentProviderModelTotal>();
        try
        {
            EnsureInit();
            using var conn = new SqliteConnection($"Data Source={DbPath}");
            conn.Open();
            var cmd = conn.CreateCommand();
            var af = BuildAgentFilter(cmd, agentFilter);
            cmd.CommandText = $@"SELECT agent,
       CASE
         WHEN agent = 'antigravity' THEN
           CASE WHEN provider_id LIKE 'ag:%' AND LENGTH(provider_id) > 3 THEN provider_id ELSE 'Antigravity' END
         WHEN provider_id <> '' THEN provider_id
         WHEN provider_name <> '' AND provider_name NOT IN ('Codex', 'Claude Code', 'OpenCode', 'Pi', 'StepCode', 'zcode', 'dsh', 'Google', 'Antigravity', 'any') THEN provider_name
         ELSE agent END,
       model, SUM(in_tokens), SUM(out_tokens), SUM(requests),
       COALESCE(SUM(cache_read_tokens),0), COALESCE(SUM(cache_write_tokens),0)
FROM usage_events
WHERE ts >= $from AND ts <= $to{af}
GROUP BY 1, 2, 3;";
            cmd.Parameters.AddWithValue("$from", fromTs);
            cmd.Parameters.AddWithValue("$to", toTs);
            using var r = cmd.ExecuteReader();
            while (r.Read())
                list.Add(new AgentProviderModelTotal(
                    r.GetString(0), r.GetString(1), r.GetString(2),
                    r.GetInt64(3), r.GetInt64(4), r.GetInt64(5), r.GetInt64(6), r.GetInt64(7)));
        }
        catch { }
        return list;
    }

    /// <summary>提取历史库中出现过的全部供应商名称（按实际消耗量降序）与模型名称，供「定价中心」下拉框智能补全。</summary>
    public static (List<string> Providers, List<string> Models) GetDistinctProvidersAndModels()
    {
        var orderedProvs = new List<string>();
        var seenProvs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var models = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 建立 UUID -> 可读供应商名称映射，避免把 cc-switch 导入的随机 GUID 直接暴露在下拉框
        var idToFriendlyName = BuildProviderFriendlyNameMap();

        void AddProv(string? raw)
        {
            var s = (raw ?? "").Trim();
            if (string.IsNullOrEmpty(s)) return;
            if (idToFriendlyName.TryGetValue(s, out var friendly) && !string.IsNullOrWhiteSpace(friendly))
                s = friendly;
            else if (Guid.TryParse(s, out _))
                return;
            if (seenProvs.Add(s))
                orderedProvs.Add(s);
        }

        try
        {
            EnsureInit();
            using var conn = new SqliteConnection($"Data Source={DbPath}");
            conn.Open();

            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"SELECT CASE
         WHEN agent = 'antigravity' THEN 'Antigravity'
         WHEN provider_id <> '' THEN provider_id
         WHEN provider_name <> '' AND provider_name NOT IN ('Codex', 'Claude Code', 'OpenCode', 'Pi', 'StepCode', 'zcode', 'dsh', 'Google', 'Antigravity', 'any') THEN provider_name
         ELSE agent END,
         SUM(in_tokens + out_tokens) AS tot
FROM usage_events
GROUP BY 1
ORDER BY tot DESC;";
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    if (!r.IsDBNull(0)) AddProv(r.GetString(0));
                }
            }

            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT model, SUM(in_tokens + out_tokens) AS tot FROM usage_events WHERE model <> '' GROUP BY 1 ORDER BY tot DESC LIMIT 500;";
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    var m = r.IsDBNull(0) ? "" : r.GetString(0).Trim();
                    if (!string.IsNullOrEmpty(m)) models.Add(m);
                }
            }
        }
        catch { }

        AddProv("Antigravity");

        try
        {
            foreach (var p in CliStore.LoadCodex())
                if (!p.IsOfficial) AddProv(!Guid.TryParse(p.Id, out _) && !string.IsNullOrWhiteSpace(p.Id) ? p.Id : p.Name);
            foreach (var p in CliStore.LoadClaude())
                if (!p.IsOfficial) AddProv(!Guid.TryParse(p.Id, out _) && !string.IsNullOrWhiteSpace(p.Id) ? p.Id : p.Name);
            foreach (var p in CliStore.LoadClaudeDesktop())
                if (!p.IsOfficial) AddProv(!Guid.TryParse(p.Id, out _) && !string.IsNullOrWhiteSpace(p.Id) ? p.Id : p.Name);
            foreach (var p in CliStore.LoadOpencode())
                AddProv(!Guid.TryParse(p.Id, out _) && !string.IsNullOrWhiteSpace(p.Id) ? p.Id : p.Name);
            foreach (var p in CliStore.LoadPiProviders())
                AddProv(!Guid.TryParse(p.Id, out _) && !string.IsNullOrWhiteSpace(p.Id) ? p.Id : p.Name);
            foreach (var ag in ProfileStore.Load())
                if (!string.IsNullOrWhiteSpace(ag.Email)) AddProv(ag.Email.Trim().ToLowerInvariant());
        }
        catch { }

        return (orderedProvs, models.ToList());
    }

    internal static Dictionary<string, string> BuildProviderFriendlyNameMap()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var p in CliStore.LoadClaude())
                if (!string.IsNullOrWhiteSpace(p.Id) && !string.IsNullOrWhiteSpace(p.Name))
                    map[p.Id.Trim()] = p.Name.Trim();
            foreach (var p in CliStore.LoadClaudeDesktop())
                if (!string.IsNullOrWhiteSpace(p.Id) && !string.IsNullOrWhiteSpace(p.Name))
                    map[p.Id.Trim()] = p.Name.Trim();
            foreach (var p in CliStore.LoadCodex())
                if (!string.IsNullOrWhiteSpace(p.Id) && !string.IsNullOrWhiteSpace(p.Name))
                    map[p.Id.Trim()] = p.Name.Trim();
            foreach (var p in CliStore.LoadOpencode())
                if (!string.IsNullOrWhiteSpace(p.Id) && !string.IsNullOrWhiteSpace(p.Name))
                    map[p.Id.Trim()] = p.Name.Trim();
            foreach (var p in CliStore.LoadPiProviders())
                if (!string.IsNullOrWhiteSpace(p.Id) && !string.IsNullOrWhiteSpace(p.Name))
                    map[p.Id.Trim()] = p.Name.Trim();
        }
        catch { }
        return map;
    }

    /// <summary>按供应商 × 模型交叉汇总（指定时间范围，按总量降序），用于下钻明细。</summary>
    public static List<ProviderModelTotal> GetProviderModelTotals(long fromTs, long toTs, int limit = 400, string? agentFilter = null)
    {
        var list = new List<ProviderModelTotal>();
        try
        {
            EnsureInit();
            using var conn = new SqliteConnection($"Data Source={DbPath}");
            conn.Open();
            var cmd = conn.CreateCommand();
            var af = BuildAgentFilter(cmd, agentFilter);
            cmd.CommandText = $@"SELECT CASE
       WHEN agent = 'antigravity' THEN 'Antigravity'
       WHEN provider_id <> '' THEN provider_id
       WHEN provider_name <> '' AND provider_name NOT IN ('Codex', 'Claude Code', 'OpenCode', 'Pi', 'StepCode', 'zcode', 'dsh', 'Google', 'Antigravity', 'any') THEN provider_name
       ELSE agent END,
       model, SUM(in_tokens), SUM(out_tokens), SUM(requests),
       COALESCE(SUM(cache_read_tokens),0), COALESCE(SUM(cache_write_tokens),0)
FROM usage_events WHERE ts >= $from AND ts <= $to{af}
GROUP BY 1, 2 ORDER BY SUM(in_tokens + out_tokens) DESC LIMIT $lim;";
            cmd.Parameters.AddWithValue("$from", fromTs);
            cmd.Parameters.AddWithValue("$to", toTs);
            cmd.Parameters.AddWithValue("$lim", limit);
            using var r = cmd.ExecuteReader();
            while (r.Read())
                list.Add(new ProviderModelTotal(r.GetString(0), r.GetString(1), r.GetInt64(2), r.GetInt64(3), r.GetInt64(4), r.GetInt64(5), r.GetInt64(6)));
        }
        catch { }
        return list;
    }

    public record DailyTotal(string Day, long InTokens, long OutTokens, long Requests = 0);

    /// <summary>按天汇总（本地时区），用于每日趋势图。</summary>
    public static List<DailyTotal> GetDailyTotals(long fromTs, long toTs, string? agentFilter = null)
    {
        var list = new List<DailyTotal>();
        try
        {
            EnsureInit();
            using var conn = new SqliteConnection($"Data Source={DbPath}");
            conn.Open();
            var cmd = conn.CreateCommand();
            var af = BuildAgentFilter(cmd, agentFilter);
            cmd.CommandText = $@"SELECT strftime('%Y-%m-%d', ts, 'unixepoch', 'localtime'), SUM(in_tokens), SUM(out_tokens), COALESCE(SUM(requests),0)
FROM usage_events WHERE ts >= $from AND ts <= $to{af} GROUP BY 1 ORDER BY 1;";
            cmd.Parameters.AddWithValue("$from", fromTs);
            cmd.Parameters.AddWithValue("$to", toTs);
            using var r = cmd.ExecuteReader();
            while (r.Read())
                list.Add(new DailyTotal(r.GetString(0), r.GetInt64(1), r.GetInt64(2), r.GetInt64(3)));
        }
        catch { }
        return list;
    }

    public record QuotaSnapshotRow(DateTime Ts, double Remaining);
    public record QuotaSnapshotPoint(long Ts, string AccountEmail, string Bucket, double Remaining, long ResetTime);

    /// <summary>某账号某配额桶的最近趋势（按时间正序，最多 limit 条）。</summary>
    public static List<QuotaSnapshotRow> GetQuotaTrend(string accountEmail, string bucket, int limit = 100)
    {
        var list = new List<QuotaSnapshotRow>();
        try
        {
            EnsureInit();
            using var conn = new SqliteConnection($"Data Source={DbPath}");
            conn.Open();
            var cmd = conn.CreateCommand();
            cmd.CommandText = @"SELECT ts, remaining_fraction FROM (
  SELECT ts, remaining_fraction FROM quota_snapshots
  WHERE account_email = $acct AND bucket = $bucket ORDER BY ts DESC LIMIT $lim
) ORDER BY ts ASC;";
            cmd.Parameters.AddWithValue("$acct", accountEmail ?? "");
            cmd.Parameters.AddWithValue("$bucket", bucket ?? "");
            cmd.Parameters.AddWithValue("$lim", limit);
            using var r = cmd.ExecuteReader();
            while (r.Read())
                list.Add(new QuotaSnapshotRow(DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(0)).LocalDateTime, r.GetDouble(1)));
        }
        catch { }
        return list;
    }

    /// <summary>按时间区间（UTC 秒）读取全部 Antigravity 账号的配额快照（按时间正序）。</summary>
    public static List<QuotaSnapshotPoint> GetQuotaSnapshotsRange(long fromTs, long toTs)
    {
        var list = new List<QuotaSnapshotPoint>();
        try
        {
            EnsureInit();
            using var conn = new SqliteConnection($"Data Source={DbPath}");
            conn.Open();
            var cmd = conn.CreateCommand();
            cmd.CommandText = @"SELECT ts, account_email, bucket, remaining_fraction, reset_time
FROM quota_snapshots WHERE ts >= $from AND ts <= $to ORDER BY ts ASC;";
            cmd.Parameters.AddWithValue("$from", fromTs);
            cmd.Parameters.AddWithValue("$to", toTs);
            using var r = cmd.ExecuteReader();
            while (r.Read())
                list.Add(new QuotaSnapshotPoint(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetDouble(3), r.GetInt64(4)));
        }
        catch { }
        return list;
    }

    /// <summary>启动时清理过期旧数据（默认保留 400 天，覆盖完整一年）。</summary>
    public static void CleanupOldData(int keepDays = 400)
    {
        try
        {
            EnsureInit();
            var cutoff = DateTimeOffset.UtcNow.AddDays(-keepDays).ToUnixTimeSeconds();
            using var conn = new SqliteConnection($"Data Source={DbPath}");
            conn.Open();
            var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM usage_events WHERE ts < $c; DELETE FROM quota_snapshots WHERE ts < $c;";
            cmd.Parameters.AddWithValue("$c", cutoff);
            cmd.ExecuteNonQuery();
        }
        catch { }
    }

    /// <summary>
    /// 备份用量数据库到同目录，文件名带时间戳。返回备份路径，失败返回 null。
    /// 回填属于破坏性操作（先清空事件表再重扫），动手前必须留后路。
    /// </summary>
    public static string? BackupDatabase()
    {
        try
        {
            if (!File.Exists(DbPath)) return null;
            var bak = $"{DbPath}.bak-{DateTime.Now:yyyyMMdd-HHmmss}";
            File.Copy(DbPath, bak, overwrite: false);
            return bak;
        }
        catch { return null; }
    }

    /// <summary>清空用量事件表。回填前调用，避免重扫后同一批请求被重复计数。</summary>
    public static void ClearEvents()
    {
        try
        {
            EnsureInit();
            using var conn = new SqliteConnection($"Data Source={DbPath}");
            conn.Open();
            var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM usage_events;";
            cmd.ExecuteNonQuery();
        }
        catch { }
    }

    /// <summary>事件表中含缓存 / 含耗时数据的行数（回填后验证用）。</summary>
    public static (int Total, int WithCache, int WithTiming) CountPerfRows()
    {
        try
        {
            EnsureInit();
            using var conn = new SqliteConnection($"Data Source={DbPath}");
            conn.Open();
            var cmd = conn.CreateCommand();
            cmd.CommandText = @"SELECT COUNT(*),
  COALESCE(SUM(CASE WHEN cache_read_tokens > 0 OR cache_write_tokens > 0 THEN 1 ELSE 0 END),0),
  COALESCE(SUM(CASE WHEN duration_ms > 0 OR ttft_ms > 0 THEN 1 ELSE 0 END),0)
FROM usage_events;";
            using var r = cmd.ExecuteReader();
            if (r.Read()) return (r.GetInt32(0), r.GetInt32(1), r.GetInt32(2));
        }
        catch { }
        return (0, 0, 0);
    }

    /// <summary>获取指定 Agent 已通过逐条日志入库的本地日期集合（yyyy-MM-dd），用于汇总文件查漏补缺。</summary>
    public static HashSet<string> GetAgentLogDates(string agent)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            EnsureInit();
            using var conn = new SqliteConnection($"Data Source={DbPath}");
            conn.Open();
            var cmd = conn.CreateCommand();
            cmd.CommandText = @"SELECT DISTINCT strftime('%Y-%m-%d', ts, 'unixepoch', 'localtime')
FROM usage_events WHERE agent = $ag AND source_id NOT LIKE 'claude:stats-cache:%';";
            cmd.Parameters.AddWithValue("$ag", agent);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                if (!r.IsDBNull(0)) set.Add(r.GetString(0));
            }
        }
        catch { }
        return set;
    }

    public readonly record struct EmptyModelRow(
        long Id, long Ts, long UncachedIn, long CacheRead, long OutTokens, string SourceId);

    /// <summary>
    /// 为已有但 model 为空的记录原地补齐模型名（用于 Antigravity 等事后异步落盘台账场景，不重复计数 Token/请求数）。
    /// resolver 参数：(tsUnixSec, uncachedInTokens, cacheReadTokens, outTokens, sourceId) -> 模型名或 null。
    /// </summary>
    public static int EnrichEmptyModels(string agent, Func<long, long, long, long, string, string?> resolver)
        => EnrichEmptyModelsBatch(agent, rows =>
        {
            var updates = new Dictionary<long, string>();
            foreach (var r in rows)
            {
                var m = resolver(r.Ts, r.UncachedIn, r.CacheRead, r.OutTokens, r.SourceId);
                if (!string.IsNullOrEmpty(m))
                    updates[r.Id] = m;
            }
            return updates;
        });

    /// <summary>
    /// 批量原地补齐已有但 model 为空的记录（一次读取、单事务提交，供会话库按对话分组解析）。
    /// </summary>
    public static int EnrichEmptyModelsBatch(string agent, Func<IReadOnlyList<EmptyModelRow>, Dictionary<long, string>> resolver, long minTs = 0)
    {
        try
        {
            EnsureInit();
            using var conn = new SqliteConnection($"Data Source={DbPath}");
            conn.Open();

            var rows = new List<EmptyModelRow>();
            using (var sel = conn.CreateCommand())
            {
                string tsClause = minTs > 0 ? " AND ts >= $minTs" : "";
                sel.CommandText = $@"SELECT id, ts, in_tokens, out_tokens, cache_read_tokens, source_id
FROM usage_events WHERE agent = $ag AND model = '' AND source_id <> ''{tsClause};";
                sel.Parameters.AddWithValue("$ag", agent);
                if (minTs > 0) sel.Parameters.AddWithValue("$minTs", minTs);
                using var r = sel.ExecuteReader();
                while (r.Read())
                {
                    long id = r.GetInt64(0);
                    long ts = r.GetInt64(1);
                    long inTok = r.GetInt64(2);
                    long outTok = r.GetInt64(3);
                    long cacheRead = r.GetInt64(4);
                    string sid = r.IsDBNull(5) ? "" : r.GetString(5);
                    long uncachedIn = Math.Max(0, inTok - cacheRead);
                    rows.Add(new EmptyModelRow(id, ts, uncachedIn, cacheRead, outTok, sid));
                }
            }

            if (rows.Count == 0) return 0;

            var updates = resolver(rows);
            if (updates == null || updates.Count == 0) return 0;

            using var tx = conn.BeginTransaction();
            using var upd = conn.CreateCommand();
            upd.Transaction = tx;
            upd.CommandText = "UPDATE usage_events SET model = $m WHERE id = $id AND model = '';";
            var pM = upd.Parameters.Add("$m", SqliteType.Text);
            var pId = upd.Parameters.Add("$id", SqliteType.Integer);
            int applied = 0;
            foreach (var (id, model) in updates)
            {
                if (string.IsNullOrEmpty(model)) continue;
                pM.Value = model;
                pId.Value = id;
                applied += upd.ExecuteNonQuery();
            }
            tx.Commit();
            return applied;
        }
        catch (Exception ex)
        {
            App.LogStartup("UsageStorage.EnrichEmptyModelsBatch failed: " + ex.Message);
            return 0;
        }
    }

    /// <summary>
    /// 基于已在库中且具备真实时间戳（ts）的 Antigravity 已识别模型记录，构建真实时间线，
    /// 对因台账抽样遗漏、子代理分离或短暂延迟导致的空模型行进行高精度补齐（绝不重复计入请求/Token）：
    /// 1) 同会话前后 4h 内已有相同模型夹逼（即时生效）；
    /// 2) 超过冷静期（默认 120s，避开刚切换模型尚未刷盘阶段）后，尝试同会话 ±30m 单模型窗口或同本地日单模型会话邻块；
    /// 3) 子代理会话沿 parent_conversation_id 向上回溯父会话时间线。
    /// </summary>
    public static int EnrichAntigravityFromTimeline(IReadOnlyDictionary<string, string>? parentMap = null, long graceSeconds = 120, long minTs = 0)
    {
        try
        {
            EnsureInit();
            using var conn = new SqliteConnection($"Data Source={DbPath}");
            conn.Open();

            var timeline = new Dictionary<string, List<(long Ts, string Model)>>(StringComparer.OrdinalIgnoreCase);
            var emptyRows = new List<(long Id, long Ts, string ConvId)>();

            using (var sel = conn.CreateCommand())
            {
                string tsClause = minTs > 0 ? " AND ts >= $minTs" : "";
                sel.CommandText = $@"SELECT id, ts, model, source_id
FROM usage_events
WHERE agent = $ag AND source_id LIKE 'ag:%'{tsClause}
ORDER BY ts ASC;";
                sel.Parameters.AddWithValue("$ag", AgentAntigravity);
                if (minTs > 0) sel.Parameters.AddWithValue("$minTs", minTs);
                using var r = sel.ExecuteReader();
                while (r.Read())
                {
                    long id = r.GetInt64(0);
                    long ts = r.GetInt64(1);
                    string model = r.IsDBNull(2) ? "" : r.GetString(2);
                    string sid = r.IsDBNull(3) ? "" : r.GetString(3);
                    var cid = ExtractAgConvId(sid);
                    if (string.IsNullOrEmpty(cid)) continue;

                    if (string.IsNullOrEmpty(model))
                    {
                        emptyRows.Add((id, ts, cid));
                    }
                    else
                    {
                        if (!timeline.TryGetValue(cid, out var list))
                            timeline[cid] = list = new List<(long, string)>();
                        list.Add((ts, model));
                    }
                }
            }

            if (emptyRows.Count == 0 || timeline.Count == 0) return 0;

            long cutoffGraceTs = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - graceSeconds;
            var updates = new List<(long Id, string Model)>();

            foreach (var (id, ts, cid) in emptyRows)
            {
                string resolved = ResolveOnTimeline(timeline, cid, ts, cutoffGraceTs);
                if (string.IsNullOrEmpty(resolved) && parentMap != null && parentMap.Count > 0)
                {
                    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { cid };
                    string cur = cid;
                    for (int depth = 0; depth < 4 && parentMap.TryGetValue(cur, out var par) && seen.Add(par); depth++)
                    {
                        resolved = ResolveOnTimeline(timeline, par, ts, cutoffGraceTs);
                        if (!string.IsNullOrEmpty(resolved)) break;
                        cur = par;
                    }
                }

                if (!string.IsNullOrEmpty(resolved))
                    updates.Add((id, resolved));
            }

            if (updates.Count == 0) return 0;

            using var tx = conn.BeginTransaction();
            using var upd = conn.CreateCommand();
            upd.Transaction = tx;
            upd.CommandText = "UPDATE usage_events SET model = $m WHERE id = $id AND model = '';";
            var pM = upd.Parameters.Add("$m", SqliteType.Text);
            var pId = upd.Parameters.Add("$id", SqliteType.Integer);
            foreach (var (id, model) in updates)
            {
                pM.Value = model;
                pId.Value = id;
                upd.ExecuteNonQuery();
            }
            tx.Commit();
            return updates.Count;
        }
        catch (Exception ex)
        {
            App.LogStartup("UsageStorage.EnrichAntigravityFromTimeline failed: " + ex.Message);
            return 0;
        }
    }

    /// <summary>
    /// 按 source_id 原地补齐历史记录缺失的 duration_ms 与 ttft_ms（仅填补值为 0 的列，不改 Token 与请求数）。
    /// </summary>
    public static int EnrichUsageTiming(string agent, IReadOnlyDictionary<string, (long DurMs, long TtftMs)> timingBySourceId)
    {
        if (timingBySourceId.Count == 0) return 0;
        try
        {
            EnsureInit();
            using var conn = new SqliteConnection($"Data Source={DbPath}");
            conn.Open();

            var updates = new List<(long Id, long Dur, long Ttft)>();
            using (var sel = conn.CreateCommand())
            {
                sel.CommandText = @"SELECT id, duration_ms, ttft_ms, source_id
FROM usage_events
WHERE agent = $ag AND source_id <> '' AND (duration_ms = 0 OR ttft_ms = 0);";
                sel.Parameters.AddWithValue("$ag", agent);
                using var r = sel.ExecuteReader();
                while (r.Read())
                {
                    long id = r.GetInt64(0);
                    long curDur = r.GetInt64(1);
                    long curTtft = r.GetInt64(2);
                    string sid = r.IsDBNull(3) ? "" : r.GetString(3);
                    if (!timingBySourceId.TryGetValue(sid, out var t)) continue;
                    long nextDur = curDur <= 0 && t.DurMs > 0 ? t.DurMs : 0;
                    long nextTtft = curTtft <= 0 && t.TtftMs > 0 ? t.TtftMs : 0;
                    if (nextDur > 0 || nextTtft > 0)
                        updates.Add((id, nextDur, nextTtft));
                }
            }

            if (updates.Count == 0) return 0;

            using var tx = conn.BeginTransaction();
            using var upd = conn.CreateCommand();
            upd.Transaction = tx;
            upd.CommandText = @"UPDATE usage_events
SET duration_ms = CASE WHEN duration_ms <= 0 AND $dur > 0 THEN $dur ELSE duration_ms END,
    ttft_ms = CASE WHEN ttft_ms <= 0 AND $ttft > 0 THEN $ttft ELSE ttft_ms END
WHERE id = $id;";
            var pDur = upd.Parameters.Add("$dur", SqliteType.Integer);
            var pTtft = upd.Parameters.Add("$ttft", SqliteType.Integer);
            var pId = upd.Parameters.Add("$id", SqliteType.Integer);
            foreach (var (id, dur, ttft) in updates)
            {
                pDur.Value = dur;
                pTtft.Value = ttft;
                pId.Value = id;
                upd.ExecuteNonQuery();
            }
            tx.Commit();
            return updates.Count;
        }
        catch (Exception ex)
        {
            App.LogStartup("UsageStorage.EnrichUsageTiming failed: " + ex.Message);
            return 0;
        }
    }

    static string ExtractAgConvId(string sourceId)
    {
        if (!sourceId.StartsWith("ag:", StringComparison.Ordinal)) return "";
        int secondColon = sourceId.IndexOf(':', 3);
        return secondColon > 3 ? sourceId.Substring(3, secondColon - 3) : "";
    }

    static string ResolveOnTimeline(Dictionary<string, List<(long Ts, string Model)>> timeline,
        string cid, long ts, long cutoffGraceTs)
    {
        if (!timeline.TryGetValue(cid, out var list) || list.Count == 0) return "";

        int idx = LowerBound(list, ts);
        (long Ts, string Model)? prev = idx > 0 ? list[idx - 1] : null;
        (long Ts, string Model)? next = idx < list.Count ? list[idx] : null;

        // 1. 前后 4h 夹逼：同对话前后已确定是同一个模型
        if (prev.HasValue && next.HasValue &&
            string.Equals(prev.Value.Model, next.Value.Model, StringComparison.Ordinal) &&
            (next.Value.Ts - prev.Value.Ts) <= 4 * 3600)
        {
            return prev.Value.Model;
        }

        // 新近 120s 内的尾部步骤先留给 pricing_ledger 精确刷盘，不提前单边推断
        if (ts > cutoffGraceTs) return "";

        // 2. ±30 分钟窗口内所有已识别步骤仅出现唯一模型
        int lo = LowerBound(list, ts - 1800);
        int hi = UpperBound(list, ts + 1800);
        string? winModel = null;
        bool winConsistent = true;
        for (int i = lo; i < hi; i++)
        {
            if (winModel == null) winModel = list[i].Model;
            else if (!string.Equals(winModel, list[i].Model, StringComparison.Ordinal))
            {
                winConsistent = false;
                break;
            }
        }
        if (winModel != null && winConsistent) return winModel;

        // 3. 同一本地日内该对话全程只使用过单一模型，且最近记录在 4h 内（覆盖午休衔接同一会话）
        var targetDay = DateTimeOffset.FromUnixTimeSeconds(ts).ToLocalTime().Date;
        string? dayModel = null;
        bool daySingle = true;
        long minGap = long.MaxValue;
        foreach (var item in list)
        {
            if (DateTimeOffset.FromUnixTimeSeconds(item.Ts).ToLocalTime().Date != targetDay) continue;
            if (dayModel == null) dayModel = item.Model;
            else if (!string.Equals(dayModel, item.Model, StringComparison.Ordinal))
            {
                daySingle = false;
                break;
            }
            long gap = Math.Abs(item.Ts - ts);
            if (gap < minGap) minGap = gap;
        }
        if (dayModel != null && daySingle && minGap <= 4 * 3600)
            return dayModel;

        return "";
    }

    static int LowerBound(List<(long Ts, string Model)> list, long ts)
    {
        int lo = 0, hi = list.Count;
        while (lo < hi)
        {
            int mid = lo + ((hi - lo) >> 1);
            if (list[mid].Ts < ts) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }

    static int UpperBound(List<(long Ts, string Model)> list, long ts)
    {
        int lo = 0, hi = list.Count;
        while (lo < hi)
        {
            int mid = lo + ((hi - lo) >> 1);
            if (list[mid].Ts <= ts) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }

    private readonly record struct QuotaDropInterval(long StartTs, long EndTs, string Email, string Pool, double Delta);

    /// <summary>
    /// 原地补齐 Antigravity 用量记录的 Google 账号归属（绝不改变请求数与 Token 数）：
    /// 优先级 1：全局 Stop 钩子离线记录（ag_turn_accounts.jsonl，精确到 conversationId + 回合结束时间，支持子代理父链继承）；
    /// 优先级 2：历史 quota_snapshots 配额池消耗窗口（区分 gemini-* 与 claude/gpt 3p-* 池并按消耗密度评分）；
    /// 优先级 3：APISwitch 在线确认过的活跃账号时间段（account_sessions）；
    /// 第二遍：同对话/父子对话上下文传播与 15 分钟内唯一活跃扣费账号兜底；仍存歧义的老记录诚实保留为空（展示为未区分历史记录）。
    /// </summary>
    public static int EnrichAntigravityAccounts(
        IReadOnlyDictionary<string, string>? parentMap = null,
        IReadOnlyList<AgHookService.HookTurnEntry>? hookEntries = null,
        long reconcileFromTs = 0,
        string? liveActiveEmail = null,
        long minTs = 0)
    {
        try
        {
            EnsureInit();
            using var conn = new SqliteConnection($"Data Source={DbPath}");
            conn.Open();

            long nowTs = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            string normLiveEmail = (liveActiveEmail ?? "").Trim().ToLowerInvariant();
            if (!normLiveEmail.Contains('@')) normLiveEmail = "";

            var candidates = new List<(long Id, long Ts, string Model, string ConvId, string ExistingEmail)>();
            var convKnown = new Dictionary<string, List<(long Ts, string Email)>>(StringComparer.OrdinalIgnoreCase);

            using (var sel = conn.CreateCommand())
            {
                string tsClause = minTs > 0 ? " AND ts >= $minTs" : "";
                sel.CommandText = $@"SELECT id, ts, model, provider_id, source_id
FROM usage_events
WHERE agent = $ag{tsClause}
ORDER BY ts ASC, id ASC;";
                sel.Parameters.AddWithValue("$ag", AgentAntigravity);
                if (minTs > 0) sel.Parameters.AddWithValue("$minTs", minTs);
                using var r = sel.ExecuteReader();
                while (r.Read())
                {
                    long id = r.GetInt64(0);
                    long ts = r.GetInt64(1);
                    string model = r.IsDBNull(2) ? "" : r.GetString(2);
                    string pid = r.IsDBNull(3) ? "" : r.GetString(3);
                    string sid = r.IsDBNull(4) ? "" : r.GetString(4);
                    string cid = ExtractAgConvId(sid);

                    if (string.IsNullOrEmpty(pid))
                    {
                        candidates.Add((id, ts, model, cid, ""));
                    }
                    else if (pid.StartsWith("ag:", StringComparison.OrdinalIgnoreCase) && pid.Length > 3)
                    {
                        string email = pid.Substring(3).Trim().ToLowerInvariant();
                        if (reconcileFromTs > 0 && ts >= reconcileFromTs)
                        {
                            candidates.Add((id, ts, model, cid, email));
                        }
                        else if (!string.IsNullOrEmpty(cid))
                        {
                            if (!convKnown.TryGetValue(cid, out var klist))
                                convKnown[cid] = klist = new List<(long, string)>();
                            klist.Add((ts, email));
                        }
                    }
                }
            }

            if (candidates.Count == 0) return 0;

            // 1. 构建离线 Hook 索引（带 conversationId 的记录严格限定在该会话及子代理链内，绝不跨会话串染）
            var hooksByConv = new Dictionary<string, List<(long Ts, string Email)>>(StringComparer.OrdinalIgnoreCase);
            var unscopedHooks = new List<(long Ts, string Email)>();
            if (hookEntries != null && hookEntries.Count > 0)
            {
                foreach (var h in hookEntries)
                {
                    var em = h.Email?.Trim().ToLowerInvariant() ?? "";
                    if (h.Ts <= 0 || string.IsNullOrEmpty(em) || !em.Contains('@')) continue;
                    if (!string.IsNullOrEmpty(h.ConversationId))
                    {
                        if (!hooksByConv.TryGetValue(h.ConversationId, out var hlist))
                            hooksByConv[h.ConversationId] = hlist = new List<(long, string)>();
                        hlist.Add((h.Ts, em));
                    }
                    else
                    {
                        unscopedHooks.Add((h.Ts, em));
                    }
                }
                unscopedHooks.Sort((a, b) => a.Ts.CompareTo(b.Ts));
                foreach (var kv in hooksByConv)
                    kv.Value.Sort((a, b) => a.Ts.CompareTo(b.Ts));
            }

            // 2. 构建历史 quota_snapshots 消耗下降窗口
            var dropIntervals = LoadQuotaDropIntervals(conn, minTs > 0 ? Math.Max(0, minTs - 86400) : 0);

            // 3. 读取在线已确认的活跃账号时间段（合并相邻同账号窗口）
            var sessions = new List<(long StartTs, long EndTs, string Email)>();
            using (var sCmd = conn.CreateCommand())
            {
                sCmd.CommandText = "SELECT start_ts, last_verified_ts, LOWER(TRIM(account_email)) FROM account_sessions WHERE account_email <> '' ORDER BY start_ts ASC, id ASC;";
                using var sr = sCmd.ExecuteReader();
                while (sr.Read())
                {
                    long s = sr.GetInt64(0);
                    long e = Math.Max(s, sr.GetInt64(1));
                    string em = sr.IsDBNull(2) ? "" : sr.GetString(2);
                    if (!string.IsNullOrEmpty(em) && em.Contains('@'))
                        sessions.Add((s, e, em));
                }
            }

            var resolved = new Dictionary<long, string>();

            // 第一遍：直接权威证据归属（前向因果 Stop Hook -> 在线活跃账号会话 -> 配额快照掉电窗口）
            foreach (var row in candidates)
            {
                string directEmail = ResolveAccountFromHook(hooksByConv, unscopedHooks, parentMap, row.ConvId, row.Ts, sessions);
                if (string.IsNullOrEmpty(directEmail))
                    directEmail = ResolveAccountFromSessions(sessions, row.Ts, normLiveEmail, nowTs);
                if (string.IsNullOrEmpty(directEmail) && string.IsNullOrEmpty(row.ExistingEmail))
                    directEmail = ResolveAccountFromQuotaDrops(dropIntervals, row.Ts, ClassifyAgModelPool(row.Model), sessions);

                string finalDirect = !string.IsNullOrEmpty(directEmail) ? directEmail : row.ExistingEmail;
                if (!string.IsNullOrEmpty(finalDirect))
                {
                    if (!string.Equals(finalDirect, row.ExistingEmail, StringComparison.OrdinalIgnoreCase))
                        resolved[row.Id] = finalDirect;
                    if (!string.IsNullOrEmpty(row.ConvId))
                    {
                        if (!convKnown.TryGetValue(row.ConvId, out var klist))
                            convKnown[row.ConvId] = klist = new List<(long, string)>();
                        klist.Add((row.Ts, finalDirect));
                    }
                }
            }

            foreach (var kv in convKnown)
                kv.Value.Sort((a, b) => a.Ts.CompareTo(b.Ts));

            // 第二遍（多轮收敛，仅针对尚未归属记录）：同对话传播 -> 父子代理会话链传播 -> 15 分钟内唯一活跃扣费账号兜底
            for (int pass = 0; pass < 3; pass++)
            {
                var newlyAdded = new List<(string ConvId, long Ts, string Email)>();
                foreach (var row in candidates)
                {
                    if (!string.IsNullOrEmpty(row.ExistingEmail) || resolved.ContainsKey(row.Id)) continue;

                    string email = "";
                    if (!string.IsNullOrEmpty(row.ConvId))
                    {
                        email = ResolveAccountFromConvTimeline(convKnown, row.ConvId, row.Ts, sessions, normLiveEmail, nowTs);
                        if (string.IsNullOrEmpty(email) && parentMap != null && parentMap.Count > 0)
                        {
                            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { row.ConvId };
                            string cur = row.ConvId;
                            for (int depth = 0; depth < 4 && parentMap.TryGetValue(cur, out var par) && seen.Add(par); depth++)
                            {
                                email = ResolveAccountFromConvTimeline(convKnown, par, row.Ts, sessions, normLiveEmail, nowTs);
                                if (!string.IsNullOrEmpty(email)) break;
                                cur = par;
                            }
                        }
                    }

                    if (string.IsNullOrEmpty(email))
                        email = ResolveSoleNearbyDropAccount(dropIntervals, row.Ts, 900, sessions);

                    if (!string.IsNullOrEmpty(email))
                    {
                        resolved[row.Id] = email;
                        if (!string.IsNullOrEmpty(row.ConvId))
                            newlyAdded.Add((row.ConvId, row.Ts, email));
                    }
                }

                if (newlyAdded.Count == 0) break;
                foreach (var (cid, ts, em) in newlyAdded)
                {
                    if (!convKnown.TryGetValue(cid, out var klist))
                        convKnown[cid] = klist = new List<(long, string)>();
                    klist.Add((ts, em));
                }
                foreach (var kv in convKnown)
                    kv.Value.Sort((a, b) => a.Ts.CompareTo(b.Ts));
            }

            if (resolved.Count == 0) return 0;

            using var tx = conn.BeginTransaction();
            using var upd = conn.CreateCommand();
            upd.Transaction = tx;
            upd.CommandText = "UPDATE usage_events SET provider_id = $pid, provider_name = $pname WHERE id = $id;";
            var pPid = upd.Parameters.Add("$pid", SqliteType.Text);
            var pPname = upd.Parameters.Add("$pname", SqliteType.Text);
            var pId = upd.Parameters.Add("$id", SqliteType.Integer);
            int updated = 0;
            foreach (var (id, email) in resolved)
            {
                pPid.Value = FormatAntigravityProviderId(email);
                pPname.Value = FormatAntigravityProviderName(email);
                pId.Value = id;
                updated += upd.ExecuteNonQuery();
            }
            tx.Commit();
            if (updated > 0) BumpMutationVersion();
            return updated;
        }
        catch (Exception ex)
        {
            App.LogStartup("UsageStorage.EnrichAntigravityAccounts failed: " + ex.Message);
            return 0;
        }
    }

    private static List<QuotaDropInterval> LoadQuotaDropIntervals(SqliteConnection conn, long minTs = 0)
    {
        var intervals = new List<QuotaDropInterval>();
        using var cmd = conn.CreateCommand();
        string tsClause = minTs > 0 ? " AND ts >= $minTs" : "";
        cmd.CommandText = $@"SELECT ts, LOWER(TRIM(account_email)), bucket, remaining_fraction, reset_time
FROM quota_snapshots
WHERE account_email <> ''{tsClause}
ORDER BY LOWER(TRIM(account_email)) ASC, bucket ASC, ts ASC;";
        if (minTs > 0) cmd.Parameters.AddWithValue("$minTs", minTs);
        using var r = cmd.ExecuteReader();

        string prevEmail = "", prevBucket = "";
        long prevTs = 0, prevReset = 0;
        double prevRem = 0;
        bool hasPrev = false;

        while (r.Read())
        {
            long ts = r.GetInt64(0);
            string email = r.IsDBNull(1) ? "" : r.GetString(1);
            string bucket = r.IsDBNull(2) ? "" : r.GetString(2);
            double rem = r.GetDouble(3);
            long reset = r.GetInt64(4);

            if (string.IsNullOrEmpty(email) || !email.Contains('@')) continue;

            if (hasPrev &&
                string.Equals(email, prevEmail, StringComparison.Ordinal) &&
                string.Equals(bucket, prevBucket, StringComparison.Ordinal))
            {
                long dt = ts - prevTs;
                if (dt > 0 && dt <= 172800)
                {
                    double delta = 0;
                    if (prevReset > 0 && reset > prevReset + 180)
                    {
                        // 跨过了周期重置且新周期已经产生消耗
                        if (rem < 0.9995)
                            delta = 1.0 - rem;
                    }
                    else if (prevRem - rem > 0.00005)
                    {
                        delta = prevRem - rem;
                    }

                    if (delta > 0.00005)
                    {
                        intervals.Add(new QuotaDropInterval(prevTs, ts, email, ClassifyQuotaBucketPool(bucket), delta));
                    }
                }
            }

            prevEmail = email;
            prevBucket = bucket;
            prevTs = ts;
            prevRem = rem;
            prevReset = reset;
            hasPrev = true;
        }

        intervals.Sort((a, b) => a.StartTs.CompareTo(b.StartTs));
        return intervals;
    }

    private static string ClassifyQuotaBucketPool(string bucket) =>
        bucket.StartsWith("3p-", StringComparison.OrdinalIgnoreCase) ? "3p" : "gemini";

    private static string ClassifyAgModelPool(string model)
    {
        if (string.IsNullOrEmpty(model)) return "";
        if (model.Contains("claude", StringComparison.OrdinalIgnoreCase) ||
            model.Contains("gpt", StringComparison.OrdinalIgnoreCase))
            return "3p";
        if (model.Contains("gemini", StringComparison.OrdinalIgnoreCase))
            return "gemini";
        return "";
    }

    private static bool HasDifferentSessionBetween(
        List<(long StartTs, long EndTs, string Email)> sessions,
        long t1,
        long t2,
        string candidateEmail)
    {
        if (sessions.Count == 0 || string.IsNullOrEmpty(candidateEmail)) return false;
        long lo = Math.Min(t1, t2);
        long hi = Math.Max(t1, t2);
        foreach (var s in sessions)
        {
            if (s.StartTs > hi + 5) break;
            if (s.EndTs < lo - 5 && s.StartTs < lo - 5) continue;
            if ((s.StartTs >= lo - 5 && s.StartTs <= hi + 5) ||
                (lo >= s.StartTs - 5 && lo <= s.EndTs + 300) ||
                (hi >= s.StartTs - 5 && hi <= s.EndTs + 300))
            {
                if (!string.Equals(s.Email, candidateEmail, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        return false;
    }

    private static string ResolveAccountFromHook(
        Dictionary<string, List<(long Ts, string Email)>> hooksByConv,
        List<(long Ts, string Email)> allHooks,
        IReadOnlyDictionary<string, string>? parentMap,
        string convId,
        long ts,
        List<(long StartTs, long EndTs, string Email)> sessions)
    {
        if (!string.IsNullOrEmpty(convId))
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { convId };
            string cur = convId;
            for (int depth = 0; depth < 5; depth++)
            {
                if (hooksByConv.TryGetValue(cur, out var list) && list.Count > 0)
                {
                    var match = MatchHookList(list, ts, sessions);
                    if (!string.IsNullOrEmpty(match)) return match;
                }
                if (parentMap == null || !parentMap.TryGetValue(cur, out var par) || !seen.Add(par))
                    break;
                cur = par;
            }
        }

        // 若某条 Hook 无 conversationId，仅向前匹配该步骤结束后 5 分钟内的全局唯一 Stop 账号
        if (allHooks.Count > 0)
        {
            string? sole = null;
            foreach (var h in allHooks)
            {
                if (h.Ts < ts - 15) continue;
                if (h.Ts > ts + 300) break;
                if (sole == null) sole = h.Email;
                else if (!string.Equals(sole, h.Email, StringComparison.Ordinal))
                    return "";
            }
            if (sole != null && !HasDifferentSessionBetween(sessions, ts, ts + 60, sole))
                return sole;
        }

        return "";
    }

    private static string MatchHookList(
        List<(long Ts, string Email)> list,
        long ts,
        List<(long StartTs, long EndTs, string Email)> sessions)
    {
        // 1. 严格因果原则：Stop 钩子仅在回合结束触发，只能归属给 >= ts - 15s 的最早一次 Stop！
        //    绝不向过去（h.Ts < ts - 15s）倒推匹配，因为旧回合结束后用户可能已切号并开启尚未结束的新回合。
        foreach (var h in list)
        {
            if (h.Ts >= ts - 15 && h.Ts - ts <= 7200)
            {
                if (!HasDifferentSessionBetween(sessions, ts + 5, h.Ts - 5, h.Email))
                    return h.Email;
                break;
            }
        }

        // 2. 仅当步骤严格处于同一账号前后两次已结束 Stop 钩子之间（且中间无切号记录）时，才允许夹逼继承
        int idx = LowerBound(list, ts);
        (long Ts, string Email)? prev = idx > 0 ? list[idx - 1] : null;
        (long Ts, string Email)? next = idx < list.Count ? list[idx] : null;
        if (prev.HasValue && next.HasValue &&
            string.Equals(prev.Value.Email, next.Value.Email, StringComparison.Ordinal) &&
            (next.Value.Ts - prev.Value.Ts) <= 7200 &&
            !HasDifferentSessionBetween(sessions, prev.Value.Ts, next.Value.Ts, prev.Value.Email))
        {
            return prev.Value.Email;
        }

        return "";
    }

    private static string ResolveAccountFromQuotaDrops(
        List<QuotaDropInterval> intervals,
        long ts,
        string modelPool,
        List<(long StartTs, long EndTs, string Email)> sessions)
    {
        if (intervals.Count == 0) return "";
        string bestEmail = "";
        double bestScore = 0;

        foreach (var d in intervals)
        {
            if (d.StartTs - 45 > ts) break;
            if (d.EndTs + 45 < ts) continue;
            if (HasDifferentSessionBetween(sessions, ts, ts, d.Email)) continue;

            double poolWeight = (modelPool.Length == 0 || string.Equals(d.Pool, modelPool, StringComparison.Ordinal))
                ? 1.0
                : 0.12;
            double density = d.Delta / Math.Max(60.0, d.EndTs - d.StartTs);
            double score = poolWeight * density;
            if (score > bestScore)
            {
                bestScore = score;
                bestEmail = d.Email;
            }
        }

        return bestEmail;
    }

    private static string ResolveAccountFromSessions(
        List<(long StartTs, long EndTs, string Email)> sessions,
        long ts,
        string liveEmail,
        long nowTs)
    {
        if (sessions.Count == 0) return "";
        string? candidate = null;
        for (int i = 0; i < sessions.Count; i++)
        {
            var s = sessions[i];
            if (s.StartTs - 8 > ts) break;

            long nextStart = i + 1 < sessions.Count ? sessions[i + 1].StartTs : long.MaxValue;
            if (ts >= nextStart - 2) continue;

            long effectiveEnd = s.EndTs;
            // 若当前正处于最后一段会话且磁盘当前登录邮箱仍一致，则有效末尾自动延伸至当前时刻
            if (i == sessions.Count - 1 &&
                !string.IsNullOrEmpty(liveEmail) &&
                string.Equals(s.Email, liveEmail, StringComparison.OrdinalIgnoreCase) &&
                nowTs >= s.StartTs)
            {
                effectiveEnd = Math.Max(effectiveEnd, nowTs);
            }

            if (effectiveEnd + 900 < ts) continue;
            if (candidate == null) candidate = s.Email;
            else if (!string.Equals(candidate, s.Email, StringComparison.Ordinal))
                return "";
        }
        return candidate ?? "";
    }

    private static string ResolveAccountFromConvTimeline(
        Dictionary<string, List<(long Ts, string Email)>> convKnown,
        string cid,
        long ts,
        List<(long StartTs, long EndTs, string Email)> sessions,
        string liveEmail,
        long nowTs)
    {
        if (!convKnown.TryGetValue(cid, out var list) || list.Count == 0) return "";

        int idx = LowerBound(list, ts);
        (long Ts, string Email)? prev = idx > 0 ? list[idx - 1] : null;
        (long Ts, string Email)? next = idx < list.Count ? list[idx] : null;

        if (prev.HasValue && next.HasValue &&
            string.Equals(prev.Value.Email, next.Value.Email, StringComparison.Ordinal) &&
            (next.Value.Ts - prev.Value.Ts) <= 12 * 3600 &&
            !HasDifferentSessionBetween(sessions, prev.Value.Ts, next.Value.Ts, prev.Value.Email))
        {
            return prev.Value.Email;
        }

        // 若步骤位于对话最新尾部且距当前时刻很近（正在进行的活跃回合），且与当前实际登录账号不一致，绝不盲目延续旧账号
        if (!next.HasValue && (nowTs - ts) <= 7200 && !string.IsNullOrEmpty(liveEmail) &&
            prev.HasValue && !string.Equals(prev.Value.Email, liveEmail, StringComparison.OrdinalIgnoreCase))
        {
            return "";
        }

        int lo = LowerBound(list, ts - 7200);
        int hi = UpperBound(list, ts + 7200);
        string? winEmail = null;
        bool winConsistent = true;
        long nearestWinTs = ts;
        long bestWinGap = long.MaxValue;
        for (int i = lo; i < hi; i++)
        {
            if (winEmail == null) winEmail = list[i].Email;
            else if (!string.Equals(winEmail, list[i].Email, StringComparison.Ordinal))
            {
                winConsistent = false;
                break;
            }
            long g = Math.Abs(list[i].Ts - ts);
            if (g < bestWinGap) { bestWinGap = g; nearestWinTs = list[i].Ts; }
        }
        if (winEmail != null && winConsistent && !HasDifferentSessionBetween(sessions, nearestWinTs, ts, winEmail))
            return winEmail;

        string first = list[0].Email;
        bool single = true;
        long minGap = long.MaxValue;
        long nearestTs = ts;
        foreach (var item in list)
        {
            if (!string.Equals(item.Email, first, StringComparison.Ordinal))
            {
                single = false;
                break;
            }
            long g = Math.Abs(item.Ts - ts);
            if (g < minGap) { minGap = g; nearestTs = item.Ts; }
        }
        if (single && minGap <= 86400 && !HasDifferentSessionBetween(sessions, nearestTs, ts, first))
            return first;
        return "";
    }

    private static string ResolveSoleNearbyDropAccount(
        List<QuotaDropInterval> intervals,
        long ts,
        long windowSec,
        List<(long StartTs, long EndTs, string Email)> sessions)
    {
        if (intervals.Count == 0) return "";
        string? sole = null;
        foreach (var d in intervals)
        {
            if (d.StartTs - windowSec > ts) break;
            if (d.EndTs + windowSec < ts) continue;
            if (d.EndTs - d.StartTs > 7200) continue;
            if (HasDifferentSessionBetween(sessions, ts, ts, d.Email)) continue;
            if (sole == null) sole = d.Email;
            else if (!string.Equals(sole, d.Email, StringComparison.Ordinal))
                return "";
        }
        return sole ?? "";
    }
}

using Microsoft.Data.Sqlite;
using System.IO;

namespace APISwitch.Services;

/// <summary>
/// 用量监控存储：记录经本地网关的第三方请求 token 消耗与 Antigravity 配额快照。
/// 存储于 %APPDATA%\APISwitch\usage.db（SQLite）。
/// </summary>
public static class UsageStorage
{
    static string DbPath => Path.Combine(AgPaths.AppData, "APISwitch", "usage.db");
    static string? _initializedPath;

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

    static void EnsureInit()
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
            idx.CommandText = "CREATE UNIQUE INDEX IF NOT EXISTS idx_usage_source_id ON usage_events(source_id) WHERE source_id <> '';";
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

    /// <summary>记录一次配额快照（Antigravity）。</summary>
    public static void RecordQuotaSnapshot(string accountEmail, string bucket, double remainingFraction, DateTime? resetTime)
    {
        try
        {
            EnsureInit();
            using var conn = new SqliteConnection($"Data Source={DbPath}");
            conn.Open();
            var cmd = conn.CreateCommand();
            cmd.CommandText = @"INSERT INTO quota_snapshots (ts, account_email, bucket, remaining_fraction, reset_time)
VALUES ($ts, $acct, $bucket, $frac, $reset);";
            cmd.Parameters.AddWithValue("$ts", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            cmd.Parameters.AddWithValue("$acct", accountEmail ?? "");
            cmd.Parameters.AddWithValue("$bucket", bucket ?? "");
            cmd.Parameters.AddWithValue("$frac", remainingFraction);
            cmd.Parameters.AddWithValue("$reset", resetTime.HasValue ? new DateTimeOffset(resetTimeValue(resetTime)).ToUnixTimeSeconds() : 0L);
            cmd.ExecuteNonQuery();
        }
        catch { }
    }

    static DateTime resetTimeValue(DateTime? dt) => dt!.Value.Kind == DateTimeKind.Utc ? dt.Value : dt.Value.ToUniversalTime();

    public record UsageSummary(long TotalIn, long TotalOut, long Requests, int ActiveDays);

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
            cmd.CommandText = @"SELECT CASE WHEN provider_id <> '' THEN provider_id
       WHEN provider_name <> '' AND provider_name <> 'Codex' AND provider_name <> 'Claude Code'
            AND provider_name <> 'OpenCode' AND provider_name <> 'Pi' THEN provider_name
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
            cmd.CommandText = $@"SELECT CASE WHEN provider_id <> '' THEN provider_id
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
    public static int EnrichEmptyModelsBatch(string agent, Func<IReadOnlyList<EmptyModelRow>, Dictionary<long, string>> resolver)
    {
        try
        {
            EnsureInit();
            using var conn = new SqliteConnection($"Data Source={DbPath}");
            conn.Open();

            var rows = new List<EmptyModelRow>();
            using (var sel = conn.CreateCommand())
            {
                sel.CommandText = @"SELECT id, ts, in_tokens, out_tokens, cache_read_tokens, source_id
FROM usage_events WHERE agent = $ag AND model = '' AND source_id <> '';";
                sel.Parameters.AddWithValue("$ag", agent);
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
    public static int EnrichAntigravityFromTimeline(IReadOnlyDictionary<string, string>? parentMap = null, long graceSeconds = 120)
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
                sel.CommandText = @"SELECT id, ts, model, source_id
FROM usage_events
WHERE agent = $ag AND source_id LIKE 'ag:%'
ORDER BY ts ASC;";
                sel.Parameters.AddWithValue("$ag", AgentAntigravity);
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
}

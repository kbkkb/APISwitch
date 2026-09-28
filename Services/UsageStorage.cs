using Microsoft.Data.Sqlite;
using System.IO;

namespace APISwitch.Services;

/// <summary>
/// 用量监控存储：记录经本地网关的第三方请求 token 消耗与 Antigravity 配额快照。
/// 存储于 %APPDATA%\APISwitch\usage.db（SQLite）。
/// </summary>
public static class UsageStorage
{
    static readonly string DbPath = Path.Combine(AgPaths.AppData, "APISwitch", "usage.db");
    static bool _initialized;

    // agent 类型常量（与本地网关路由对应）
    public const string AgentCodex = "codex";
    public const string AgentClaudeCli = "claude-cli";
    public const string AgentClaudeDesktop = "claude-desktop";
    public const string AgentOpenCode = "opencode";
    public const string AgentPi = "pi";

    static void EnsureInit()
    {
        if (_initialized) return;
        lock (typeof(UsageStorage))
        {
            if (_initialized) return;
            var dir = Path.GetDirectoryName(DbPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            using var conn = new SqliteConnection($"Data Source={DbPath}");
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
    out_tokens INTEGER NOT NULL DEFAULT 0
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
            _initialized = true;
        }
    }

    /// <summary>记录一次网关请求的 token 消耗。</summary>
    public static void RecordUsage(string agent, string providerId, string providerName, string model,
        long inTokens, long outTokens, DateTime? ts = null)
    {
        try
        {
            EnsureInit();
            using var conn = new SqliteConnection($"Data Source={DbPath}");
            conn.Open();
            var cmd = conn.CreateCommand();
            cmd.CommandText = @"INSERT INTO usage_events (ts, agent, provider_id, provider_name, model, requests, in_tokens, out_tokens)
VALUES ($ts, $agent, $pid, $pname, $model, 1, $tin, $tout);";
            var t = (ts ?? DateTime.Now).ToUniversalTime();
            cmd.Parameters.AddWithValue("$ts", new DateTimeOffset(t).ToUnixTimeSeconds());
            cmd.Parameters.AddWithValue("$agent", agent);
            cmd.Parameters.AddWithValue("$pid", providerId ?? "");
            cmd.Parameters.AddWithValue("$pname", providerName ?? "");
            cmd.Parameters.AddWithValue("$model", model ?? "");
            cmd.Parameters.AddWithValue("$tin", inTokens);
            cmd.Parameters.AddWithValue("$tout", outTokens);
            cmd.ExecuteNonQuery();
        }
        catch { }
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
    public static UsageSummary GetSummary(long fromTs, long toTs)
    {
        try
        {
            EnsureInit();
            using var conn = new SqliteConnection($"Data Source={DbPath}");
            conn.Open();
            var cmd = conn.CreateCommand();
            cmd.CommandText = @"SELECT COALESCE(SUM(in_tokens),0), COALESCE(SUM(out_tokens),0), COALESCE(SUM(requests),0), COUNT(DISTINCT strftime('%Y-%m-%d', ts, 'unixepoch'))
FROM usage_events WHERE ts >= $from AND ts <= $to;";
            cmd.Parameters.AddWithValue("$from", fromTs);
            cmd.Parameters.AddWithValue("$to", toTs);
            using var r = cmd.ExecuteReader();
            if (r.Read())
                return new UsageSummary(r.GetInt64(0), r.GetInt64(1), r.GetInt64(2), r.GetInt32(3));
        }
        catch { }
        return new UsageSummary(0, 0, 0, 0);
    }

    public record UsageEventRow(DateTime Ts, string Agent, string ProviderId, string ProviderName, string Model, long InTokens, long OutTokens);

    /// <summary>指定时间范围内的调用明细（按时间倒序）。ProviderId 非空时为真实供应商标识。</summary>
    public static List<UsageEventRow> GetRecentEvents(long fromTs, long toTs, int limit = 300)
    {
        var list = new List<UsageEventRow>();
        try
        {
            EnsureInit();
            using var conn = new SqliteConnection($"Data Source={DbPath}");
            conn.Open();
            var cmd = conn.CreateCommand();
            cmd.CommandText = @"SELECT ts, agent, provider_id, provider_name, model, in_tokens, out_tokens
FROM usage_events WHERE ts >= $from AND ts <= $to ORDER BY ts DESC LIMIT $lim;";
            cmd.Parameters.AddWithValue("$from", fromTs);
            cmd.Parameters.AddWithValue("$to", toTs);
            cmd.Parameters.AddWithValue("$lim", limit);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                list.Add(new UsageEventRow(
                    DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(0)).LocalDateTime,
                    r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetInt64(5), r.GetInt64(6)));
            }
        }
        catch { }
        return list;
    }

    public record AgentTotal(string Agent, long InTokens, long OutTokens, long Requests);

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
            cmd.CommandText = @"SELECT agent, SUM(in_tokens), SUM(out_tokens), SUM(requests)
FROM usage_events WHERE ts >= $from AND ts <= $to GROUP BY agent ORDER BY SUM(in_tokens + out_tokens) DESC;";
            cmd.Parameters.AddWithValue("$from", fromTs);
            cmd.Parameters.AddWithValue("$to", toTs);
            using var r = cmd.ExecuteReader();
            while (r.Read())
                list.Add(new AgentTotal(r.GetString(0), r.GetInt64(1), r.GetInt64(2), r.GetInt64(3)));
        }
        catch { }
        return list;
    }

    public record GroupTotal(string Key, long InTokens, long OutTokens, long Requests);

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
            cmd.CommandText = @"SELECT model, SUM(in_tokens), SUM(out_tokens), SUM(requests)
FROM usage_events WHERE ts >= $from AND ts <= $to GROUP BY model ORDER BY SUM(in_tokens + out_tokens) DESC LIMIT $lim;";
            cmd.Parameters.AddWithValue("$from", fromTs);
            cmd.Parameters.AddWithValue("$to", toTs);
            cmd.Parameters.AddWithValue("$lim", limit);
            using var r = cmd.ExecuteReader();
            while (r.Read())
                list.Add(new GroupTotal(r.GetString(0), r.GetInt64(1), r.GetInt64(2), r.GetInt64(3)));
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
       ELSE agent END, SUM(in_tokens), SUM(out_tokens), SUM(requests)
FROM usage_events WHERE ts >= $from AND ts <= $to GROUP BY 1 ORDER BY SUM(in_tokens + out_tokens) DESC LIMIT $lim;";
            cmd.Parameters.AddWithValue("$from", fromTs);
            cmd.Parameters.AddWithValue("$to", toTs);
            cmd.Parameters.AddWithValue("$lim", limit);
            using var r = cmd.ExecuteReader();
            while (r.Read())
                list.Add(new GroupTotal(r.GetString(0), r.GetInt64(1), r.GetInt64(2), r.GetInt64(3)));
        }
        catch { }
        return list;
    }

    public record ProviderModelTotal(string Provider, string Model, long InTokens, long OutTokens, long Requests);

    /// <summary>按供应商 × 模型交叉汇总（指定时间范围，按总量降序），用于下钻明细。</summary>
    public static List<ProviderModelTotal> GetProviderModelTotals(long fromTs, long toTs, int limit = 400)
    {
        var list = new List<ProviderModelTotal>();
        try
        {
            EnsureInit();
            using var conn = new SqliteConnection($"Data Source={DbPath}");
            conn.Open();
            var cmd = conn.CreateCommand();
            cmd.CommandText = @"SELECT CASE WHEN provider_id <> '' THEN provider_id
       WHEN provider_name <> '' AND provider_name <> 'Codex' AND provider_name <> 'Claude Code'
            AND provider_name <> 'OpenCode' AND provider_name <> 'Pi' THEN provider_name
       ELSE agent END,
       model, SUM(in_tokens), SUM(out_tokens), SUM(requests)
FROM usage_events WHERE ts >= $from AND ts <= $to
GROUP BY 1, 2 ORDER BY SUM(in_tokens + out_tokens) DESC LIMIT $lim;";
            cmd.Parameters.AddWithValue("$from", fromTs);
            cmd.Parameters.AddWithValue("$to", toTs);
            cmd.Parameters.AddWithValue("$lim", limit);
            using var r = cmd.ExecuteReader();
            while (r.Read())
                list.Add(new ProviderModelTotal(r.GetString(0), r.GetString(1), r.GetInt64(2), r.GetInt64(3), r.GetInt64(4)));
        }
        catch { }
        return list;
    }

    public record DailyTotal(string Day, long InTokens, long OutTokens);

    /// <summary>按天汇总（本地时区），用于每日趋势图。</summary>
    public static List<DailyTotal> GetDailyTotals(long fromTs, long toTs)
    {
        var list = new List<DailyTotal>();
        try
        {
            EnsureInit();
            using var conn = new SqliteConnection($"Data Source={DbPath}");
            conn.Open();
            var cmd = conn.CreateCommand();
            cmd.CommandText = @"SELECT strftime('%Y-%m-%d', ts, 'unixepoch', 'localtime'), SUM(in_tokens), SUM(out_tokens)
FROM usage_events WHERE ts >= $from AND ts <= $to GROUP BY 1 ORDER BY 1;";
            cmd.Parameters.AddWithValue("$from", fromTs);
            cmd.Parameters.AddWithValue("$to", toTs);
            using var r = cmd.ExecuteReader();
            while (r.Read())
                list.Add(new DailyTotal(r.GetString(0), r.GetInt64(1), r.GetInt64(2)));
        }
        catch { }
        return list;
    }

    public record QuotaSnapshotRow(DateTime Ts, double Remaining);

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

    /// <summary>启动时清理 90 天前的旧数据。</summary>
    public static void CleanupOldData(int keepDays = 90)
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
}

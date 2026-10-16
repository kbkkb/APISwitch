using System;
using System.Globalization;
using System.IO;

namespace APISwitch.Services;

/// <summary>
/// zcode 用量扫描适配器。
///
/// 数据位置：~/.zcode/cli/db/db.sqlite（SQLite）
/// 数据表：model_usage——这是目前已支持的所有 agent 中记录最全的一张表，
///   直接带有 duration_ms 与 time_to_first_token_ms，
///   因此 zcode 是唯一能给出「真实首字耗时」与「真实整轮耗时」的来源
///   （其余 agent 只有消息级时间戳，只能近似）。
///
/// 字段：provider_id / model_id / status / started_at / first_token_at / completed_at /
///   duration_ms / time_to_first_token_ms / input_tokens / output_tokens /
///   reasoning_tokens / cache_creation_input_tokens / cache_read_input_tokens
///
/// 增量水位：按 started_at（毫秒时间戳）推进，与 OpenCode 的做法一致。
/// </summary>
public sealed class ZCodeUsageSource : UsageSourceBase
{
    public override string AgentId => UsageStorage.AgentZCode;

    const string StateKey = "zcode:model_usage";

    protected override int ScanCore()
    {
        var dbPath = Path.Combine(ClaudeCli.HomeDir, ".zcode", "cli", "db", "db.sqlite");
        if (!File.Exists(dbPath)) return 0;   // 用户没装 zcode：静默跳过

        long fromMs = GetWatermark(StateKey);
        int added = 0;
        long maxMs = fromMs;

        try
        {
            // 只读打开：zcode 可能正在运行并持有写锁，只读模式避免干扰它
            using var conn = new Microsoft.Data.Sqlite.SqliteConnection(
                $"Data Source={dbPath};Mode=ReadOnly;Cache=Private");
            conn.Open();

            // 表可能不存在（旧版本 zcode 或非 CLI 安装）——先探测再查
            if (!TableExists(conn, "model_usage")) return 0;

            var cmd = conn.CreateCommand();
            cmd.CommandText = @"SELECT provider_id, model_id, status, started_at,
       duration_ms, time_to_first_token_ms,
       input_tokens, output_tokens, reasoning_tokens,
       cache_creation_input_tokens, cache_read_input_tokens
FROM model_usage WHERE started_at > $from ORDER BY started_at ASC LIMIT 5000;";
            cmd.Parameters.AddWithValue("$from", fromMs);

            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var startedMs = r.GetInt64(3);
                if (startedMs > maxMs) maxMs = startedMs;
                if (ParseRow(r)) added++;
            }        }
        catch
        {
            // DB 被占用 / 损坏 / 版本不兼容：本次跳过，水位不推进，下次重试
            return added;
        }

        if (maxMs > fromMs) SetWatermark(StateKey, maxMs);
        return added;
    }

    // 注意：不能是 static——内部要调用实例方法 Record() 入库
    bool ParseRow(Microsoft.Data.Sqlite.SqliteDataReader r)
    {
        try
        {
            // 只统计成功的请求；失败/取消的请求 token 可能不完整，计入会虚增
            var status = r.GetString(2);
            if (!string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase))
                return false;

            long inTok = r.GetInt64(6);
            long outTok = r.GetInt64(7);
            if (inTok == 0 && outTok == 0) return false;

            var providerId = r.GetString(0);
            var model = r.GetString(1);
            long reasoning = r.IsDBNull(8) ? 0 : r.GetInt64(8);
            long cacheWrite = r.IsDBNull(9) ? 0 : r.GetInt64(9);
            long cacheRead = r.IsDBNull(10) ? 0 : r.GetInt64(10);
            long durationMs = r.IsDBNull(4) ? 0 : r.GetInt64(4);
            long ttftMs = r.IsDBNull(5) ? 0 : r.GetInt64(5);

            // started_at 是毫秒时间戳，转本地时间
            var ts = DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(3)).LocalDateTime;

            Record(model, inTok, outTok, ts, cacheRead, cacheWrite, durationMs, ttftMs,
                providerId: providerId, providerName: "zcode");
            return true;
        }
        catch { return false; }
    }

    static bool TableExists(Microsoft.Data.Sqlite.SqliteConnection conn, string table)
    {
        try
        {
            var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=$n;";
            cmd.Parameters.AddWithValue("$n", table);
            return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
        }
        catch { return false; }
    }
}

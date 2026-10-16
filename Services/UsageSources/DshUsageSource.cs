using System;
using System.Globalization;
using System.IO;
using System.Text.Json.Nodes;

namespace APISwitch.Services;

/// <summary>
/// dsh 用量扫描适配器。
///
/// 数据位置：~/.dsh/storages/session_projcache.json
/// 结构：tables.sessions.&lt;sessionId&gt;.rows.tokenUsage.val
///   = { totals: { uncachedInputTokens, outputTokens, cacheReadTokens, cacheWriteTokens },
///       last:   { turn, step, buckets: {...} } }
/// 另有 rows.sessionStats.val = { turns, steps, llmMs, toolMs, ttftMs, ttftSteps,
///   decodeMs, decodeTokens }，可推出平均速度与平均首字。
///
/// ⚠️ 增量策略（关键）：
/// totals 是「会话累计值」而非增量值，直接按 totals 入库会在每次扫描时重复累加。
/// 因此水位按 sessionId + totals 快照推进：仅当某个 session 的 totals 发生变化时才记账，
/// 且只记「本次与上次的差值」。用 rows.tokenUsage.val.last.turn 作为该 session 的
/// 版本序号，turn 未变则跳过。
/// </summary>
public sealed class DshUsageSource : UsageSourceBase
{
    public override string AgentId => UsageStorage.AgentDsh;

    protected override int ScanCore()
    {
        var path = Path.Combine(ClaudeCli.HomeDir, ".dsh", "storages", "session_projcache.json");
        if (!File.Exists(path)) return 0;   // 用户没装 dsh：静默跳过

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(File.ReadAllText(path));
        }
        catch
        {
            // JSON 损坏或正在被写入：本次跳过，不推进水位
            return 0;
        }
        if (root == null) return 0;

        var sessions = root["tables"]?["sessions"] as JsonObject;
        if (sessions == null) return 0;

        int added = 0;
        foreach (var kv in sessions)
        {
            var sessionId = kv.Key;
            if (string.IsNullOrEmpty(sessionId)) continue;

            try
            {
                added += ScanSession(sessionId, kv.Value, path);
            }
            catch
            {
                // 单个 session 结构异常不影响其他 session
            }
        }
        return added;
    }

    int ScanSession(string sessionId, JsonNode? sessionNode, string filePath)
    {
        var rows = sessionNode?["rows"];
        if (rows == null) return 0;

        var tokenUsage = rows["tokenUsage"]?["val"];
        if (tokenUsage == null) return 0;

        var totals = tokenUsage["totals"];
        if (totals == null) return 0;

        long uncachedIn = totals["uncachedInputTokens"]?.GetValue<long>() ?? 0;
        long outTok = totals["outputTokens"]?.GetValue<long>() ?? 0;
        long cacheRead = totals["cacheReadTokens"]?.GetValue<long>() ?? 0;
        long cacheWrite = totals["cacheWriteTokens"]?.GetValue<long>() ?? 0;
        // 总输入提示词 = 未缓存输入 + 命中缓存读取
        long totalIn = uncachedIn + cacheRead;
        if (totalIn == 0 && outTok == 0) return 0;

        // 用 last.turn 作为该 session 的版本序号：turn 未变说明 totals 没有新增
        long turn = tokenUsage["last"]?["turn"]?.GetValue<long>() ?? 0;
        var wmKey = $"dsh:{sessionId}:turn";
        long lastTurn = GetWatermark(wmKey);
        if (turn != 0 && turn <= lastTurn) return 0;

        // totals 是累计值，本次只记与上次快照的差值，避免重复累加
        var snapKey = $"dsh:{sessionId}:snap";
        long prevIn = GetWatermark(snapKey + ":in");
        long prevOut = GetWatermark(snapKey + ":out");
        long prevCr = GetWatermark(snapKey + ":cr");
        long prevCw = GetWatermark(snapKey + ":cw");

        long dIn = Math.Max(0, totalIn - prevIn);
        long dOut = Math.Max(0, outTok - prevOut);
        long dCr = Math.Max(0, cacheRead - prevCr);
        long dCw = Math.Max(0, cacheWrite - prevCw);

        if (dIn == 0 && dOut == 0) return 0;

        // 真实时间戳：优先读取会话最后一次 prompt 的时间戳（lastPromptAt，毫秒整数），
        // 缺失时回退为文件修改时间；绝对严禁使用 DateTime.Now，否则会将历史会话算入今天
        DateTime ts;
        long lastPromptAt = rows["sessionListMetadata"]?["val"]?["lastPromptAt"]?.GetValue<long>() ?? 0;
        if (lastPromptAt > 0)
        {
            ts = DateTimeOffset.FromUnixTimeMilliseconds(lastPromptAt).LocalDateTime;
        }
        else
        {
            try { ts = File.GetLastWriteTime(filePath); }
            catch { ts = DateTime.Now; }
        }

        // 从 sessionStats 推导速度与首字（dsh 记录了 ttftMs / decodeMs / decodeTokens）
        long durationMs = 0, ttftMs = 0;
        var stats = rows["sessionStats"]?["val"];
        if (stats != null)
        {
            long llmMs = stats["llmMs"]?.GetValue<long>() ?? 0;
            long decodeMs = stats["decodeMs"]?.GetValue<long>() ?? 0;
            long ttftSteps = stats["ttftSteps"]?.GetValue<long>() ?? 0;
            long totalTtft = stats["ttftMs"]?.GetValue<long>() ?? 0;
            if (ttftSteps > 0) ttftMs = totalTtft / ttftSteps;   // 平均首字
            if (decodeMs > 0) durationMs = decodeMs;             // 纯解码耗时
            else if (llmMs > 0) durationMs = llmMs;
        }

        // dsh 无单条请求的模型信息（projcache 是会话级汇总），模型留空由 UI 显示「未知」
        Record("", dIn, dOut, ts, dCr, dCw, durationMs, ttftMs,
            providerName: "dsh");

        SetWatermark(wmKey, turn);
        SetWatermark(snapKey + ":in", totalIn);
        SetWatermark(snapKey + ":out", outTok);
        SetWatermark(snapKey + ":cr", cacheRead);
        SetWatermark(snapKey + ":cw", cacheWrite);
        return 1;
    }
}

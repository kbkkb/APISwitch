using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using ZstdSharp;

namespace APISwitch.Services;

/// <summary>
/// dsh 用量扫描适配器。
///
/// 数据来源（按精确度从高到低双层覆盖）：
/// 1. 主数据源（逐条请求粒度）：~/.dsh/sessions/&lt;project&gt;/&lt;sessionId&gt;/session.v4.jsonl.zstd（兼容旧版 session.jsonl.zstd）。
///    解析每条 assistant/message（含 data.usage、data.stream 末尾 replayState 的真实 provider/model、精确毫秒时间戳与 step/start TTFT/解码耗时）
///    以及失败/重试消耗了 token 的 assistant/attempt 流事件；同一会话目录存在 v4 时优先读取 v4 避免迁移重复。
/// 2. 兜底数据源（会话累计汇总）：~/.dsh/storages/session_projcache/sessions/*.json（新版分片，record.rows 结构）
///    与 ~/.dsh/storages/session_projcache.json（旧版单文件，停止更新前遗留会话）。
///    用于补全其 modelSelection.lastUsed 元数据，并在某会话无 .zstd 逐条日志时按快照差值增量入库。
/// </summary>
public sealed class DshUsageSource : UsageSourceBase
{
    public override string AgentId => UsageStorage.AgentDsh;

    protected override int ScanCore()
    {
        var dshDir = Path.Combine(ClaudeCli.HomeDir, ".dsh");
        if (!Directory.Exists(dshDir)) return 0;

        // 1. 预收集 session_projcache（新版分片目录 + 旧版单文件），供日志缺失模型时回退及兜底补采
        var projSessions = CollectProjCacheSessions(dshDir);
        var coveredByLogs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        int added = 0;

        // 2. 扫描 ~/.dsh/sessions 下所有会话目录中的最优事件日志文件
        var sessionsRoot = Path.Combine(dshDir, "sessions");
        if (Directory.Exists(sessionsRoot))
        {
            foreach (var file in PickSessionLogFiles(sessionsRoot))
            {
                try
                {
                    added += ScanSessionLogFile(file, projSessions, coveredByLogs);
                }
                catch
                {
                    // 单个会话日志损坏或正在写入：不影响其他会话，也不推进水位
                }
            }
        }

        // 3. 对未被 .zstd 逐条日志覆盖的有量会话，通过 session_projcache 按差值水位兜底补齐
        foreach (var entry in projSessions.Values)
        {
            if (coveredByLogs.Contains(entry.SessionId)) continue;
            try
            {
                added += ScanProjCacheFallback(entry);
            }
            catch
            {
                // 单个会话兜底异常静默跳过
            }
        }

        return added;
    }

    sealed class ProjSessionEntry
    {
        public required string SessionId { get; init; }
        public required string FilePath { get; init; }
        public required JsonNode Rows { get; init; }
        public string Provider { get; init; } = "";
        public string Model { get; init; } = "";
    }

    static Dictionary<string, ProjSessionEntry> CollectProjCacheSessions(string dshDir)
    {
        var map = new Dictionary<string, ProjSessionEntry>(StringComparer.OrdinalIgnoreCase);

        // 新版分片：~/.dsh/storages/session_projcache/sessions/*.json
        var splitDir = Path.Combine(dshDir, "storages", "session_projcache", "sessions");
        if (Directory.Exists(splitDir))
        {
            foreach (var path in Directory.GetFiles(splitDir, "*.json", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    var root = ParseJsonShared(path);
                    var rec = root?["record"] ?? root;
                    var rows = rec?["rows"] ?? rec;
                    if (rows == null) continue;
                    var sessionId = Path.GetFileNameWithoutExtension(path);
                    var (provider, model) = ExtractModelSelection(rows);
                    map[sessionId] = new ProjSessionEntry
                    {
                        SessionId = sessionId,
                        FilePath = path,
                        Rows = rows,
                        Provider = provider,
                        Model = model
                    };
                }
                catch { }
            }
        }

        // 旧版单文件：~/.dsh/storages/session_projcache.json（若新版中已有同 ID 则以新版为准）
        var legacyPath = Path.Combine(dshDir, "storages", "session_projcache.json");
        if (File.Exists(legacyPath))
        {
            try
            {
                var root = ParseJsonShared(legacyPath);
                if (root?["tables"]?["sessions"] is JsonObject sessions)
                {
                    foreach (var kv in sessions)
                    {
                        if (string.IsNullOrEmpty(kv.Key) || map.ContainsKey(kv.Key)) continue;
                        var rows = kv.Value?["rows"] ?? kv.Value;
                        if (rows == null) continue;
                        var (provider, model) = ExtractModelSelection(rows);
                        map[kv.Key] = new ProjSessionEntry
                        {
                            SessionId = kv.Key,
                            FilePath = legacyPath,
                            Rows = rows,
                            Provider = provider,
                            Model = model
                        };
                    }
                }
            }
            catch { }
        }

        return map;
    }

    static IEnumerable<string> PickSessionLogFiles(string sessionsRoot)
    {
        var byDir = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var pattern in new[] { "*.zstd", "*.zst", "session*.jsonl" })
        {
            string[] found;
            try { found = Directory.GetFiles(sessionsRoot, pattern, SearchOption.AllDirectories); }
            catch { continue; }
            foreach (var f in found)
            {
                var dir = Path.GetDirectoryName(f);
                if (string.IsNullOrEmpty(dir)) continue;
                if (!byDir.TryGetValue(dir, out var list))
                    byDir[dir] = list = new List<string>();
                list.Add(f);
            }
        }

        foreach (var kv in byDir)
        {
            // 同一会话目录内可能同时存在迁移前的 session.jsonl.zstd 与迁移后的 session.v4.jsonl.zstd；
            // 优先选更高版本（v4 > v3 > ... > 无版本号 > 非压缩），避免跨格式双计
            var best = kv.Value
                .OrderByDescending(GetFormatPriority)
                .ThenByDescending(f => f, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
            if (best != null) yield return best;
        }
    }

    static int GetFormatPriority(string path)
    {
        var name = Path.GetFileName(path).ToLowerInvariant();
        if (name.StartsWith("session.v"))
        {
            int dot = name.IndexOf('.', 9);
            if (dot > 9 && int.TryParse(name[9..dot], out int ver))
                return 100 + ver;
            return 100;
        }
        if (name == "session.jsonl.zstd" || name == "session.jsonl.zst") return 50;
        if (name == "session.jsonl") return 20;
        return 10;
    }

    int ScanSessionLogFile(
        string filePath,
        Dictionary<string, ProjSessionEntry> projSessions,
        HashSet<string> coveredByLogs)
    {
        var fi = new FileInfo(filePath);
        if (!fi.Exists || fi.Length == 0) return 0;

        // 默认按目录名作为会话 ID，稍后若第 0 行 type=="session" 有明确 id 会同步补充覆盖标记
        var dirSessionId = Path.GetFileName(Path.GetDirectoryName(filePath) ?? "") ?? "";
        var lenKey = "dsh:zst_len:" + filePath;
        var mtimeKey = "dsh:zst_mtime:" + filePath;
        var lastLen = GetWatermark(lenKey);
        var lastMtime = GetWatermark(mtimeKey);
        var curMtime = fi.LastWriteTimeUtc.Ticks;

        // 归档大小与修改时间均未变化且已记录覆盖标识：直接跳过解压
        if (lastLen == fi.Length && lastMtime == curMtime && lastLen > 0)
        {
            if (!string.IsNullOrEmpty(dirSessionId)) coveredByLogs.Add(dirSessionId);
            return 0;
        }

        var records = new List<UsageStorage.UsageRecord>();
        string sessionId = dirSessionId;
        string curProvider = "";
        string curModel = "";

        if (!string.IsNullOrEmpty(dirSessionId) && projSessions.TryGetValue(dirSessionId, out var projMeta))
        {
            if (!string.IsNullOrEmpty(projMeta.Provider)) curProvider = projMeta.Provider;
            if (!string.IsNullOrEmpty(projMeta.Model)) curModel = projMeta.Model;
        }

        var stepStarts = new Dictionary<(long Turn, long Step), long>();
        bool anyUsageEvent = false;
        long lineNo = 0;

        using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (Stream rawStream = IsZstdFile(filePath) ? new DecompressionStream(fs) : fs)
        using (var sr = new StreamReader(rawStream, Encoding.UTF8))
        {
            string? line;
            while ((line = sr.ReadLine()) != null)
            {
                lineNo++;
                if (string.IsNullOrWhiteSpace(line)) continue;

                JsonNode? node;
                try { node = JsonNode.Parse(line); }
                catch { continue; }
                if (node == null) continue;

                var type = node["type"]?.GetValue<string>() ?? "";
                switch (type)
                {
                    case "session":
                    {
                        var id = node["id"]?.GetValue<string>();
                        if (!string.IsNullOrEmpty(id))
                        {
                            sessionId = id;
                            if (projSessions.TryGetValue(sessionId, out var meta))
                            {
                                if (string.IsNullOrEmpty(curProvider)) curProvider = meta.Provider;
                                if (string.IsNullOrEmpty(curModel)) curModel = meta.Model;
                            }
                        }
                        break;
                    }
                    case "subagent/descriptor":
                    {
                        var d = node["data"];
                        var p = d?["agentProvider"]?.GetValue<string>();
                        var m = d?["agentModel"]?.GetValue<string>();
                        if (!string.IsNullOrEmpty(p)) curProvider = p;
                        if (!string.IsNullOrEmpty(m)) curModel = m;
                        break;
                    }
                    case "request/header":
                    {
                        var cfg = node["data"]?["header"]?["config"];
                        var p = cfg?["provider"]?.GetValue<string>();
                        var m = cfg?["model"]?.GetValue<string>();
                        if (!string.IsNullOrEmpty(p)) curProvider = p;
                        if (!string.IsNullOrEmpty(m)) curModel = m;
                        break;
                    }
                    case "request/context":
                    case "model/selection":
                    {
                        var d = node["data"];
                        var p = d?["provider"]?.GetValue<string>();
                        var m = d?["model"]?.GetValue<string>();
                        if (!string.IsNullOrEmpty(p)) curProvider = p;
                        if (!string.IsNullOrEmpty(m)) curModel = m;
                        break;
                    }
                    case "step/start":
                    {
                        var d = node["data"];
                        long turn = d?["turn"]?.GetValue<long>() ?? 0;
                        long step = d?["step"]?.GetValue<long>() ?? 0;
                        long timeMs = node["time"]?.GetValue<long>() ?? 0;
                        if (turn > 0 && step > 0 && timeMs > 0)
                            stepStarts[(turn, step)] = timeMs;
                        break;
                    }
                    case "assistant/message":
                    {
                        var rec = BuildRecordFromAssistantMessage(
                            node, sessionId, lineNo, filePath, curProvider, curModel, stepStarts);
                        if (rec != null)
                        {
                            records.Add(rec);
                            anyUsageEvent = true;
                        }
                        break;
                    }
                    case "assistant/attempt":
                    {
                        var rec = BuildRecordFromAssistantAttempt(
                            node, sessionId, lineNo, filePath, curProvider, curModel, stepStarts);
                        if (rec != null)
                        {
                            records.Add(rec);
                            anyUsageEvent = true;
                        }
                        break;
                    }
                }
            }
        }

        if (anyUsageEvent)
        {
            if (!string.IsNullOrEmpty(sessionId)) coveredByLogs.Add(sessionId);
            if (!string.IsNullOrEmpty(dirSessionId)) coveredByLogs.Add(dirSessionId);
        }

        if (records.Count == 0)
        {
            SetWatermark(lenKey, fi.Length);
            SetWatermark(mtimeKey, curMtime);
            return 0;
        }

        if (!RecordBatch(records))
        {
            // 入库未提交成功时不推进文件水位，保证下次重扫不漏
            return 0;
        }

        SetWatermark(lenKey, fi.Length);
        SetWatermark(mtimeKey, curMtime);
        return records.Count;
    }

    UsageStorage.UsageRecord? BuildRecordFromAssistantMessage(
        JsonNode node,
        string sessionId,
        long lineNo,
        string filePath,
        string fallbackProvider,
        string fallbackModel,
        Dictionary<(long Turn, long Step), long> stepStarts)
    {
        var data = node["data"];
        var usage = data?["usage"];
        if (usage == null) return null;

        var (totalIn, outTok, cacheRead, cacheWrite) = ParseUsageTokens(usage);
        if (totalIn == 0 && outTok == 0) return null;

        long turn = data?["turn"]?.GetValue<long>() ?? 0;
        long step = data?["step"]?.GetValue<long>() ?? 0;
        long seq = node["seq"]?.GetValue<long>() ?? lineNo;
        long msgMs = node["time"]?.GetValue<long>() ?? 0;

        string provider = fallbackProvider;
        string model = fallbackModel;
        long firstChunkMs = 0;

        if (data?["stream"] is JsonArray streamArr)
        {
            InspectStreamArray(streamArr, ref provider, ref model, ref firstChunkMs);
        }

        ComputeStepTiming(turn, step, msgMs, firstChunkMs, stepStarts, out long durationMs, out long ttftMs);
        var ts = ResolveLocalTimestamp(msgMs, filePath);

        return new UsageStorage.UsageRecord(
            UsageStorage.AgentDsh,
            provider,
            string.IsNullOrEmpty(provider) ? "dsh" : provider,
            model,
            totalIn,
            outTok,
            ts,
            cacheRead,
            cacheWrite,
            durationMs,
            ttftMs,
            $"dsh:{sessionId}:m:{seq}");
    }

    UsageStorage.UsageRecord? BuildRecordFromAssistantAttempt(
        JsonNode node,
        string sessionId,
        long lineNo,
        string filePath,
        string fallbackProvider,
        string fallbackModel,
        Dictionary<(long Turn, long Step), long> stepStarts)
    {
        var data = node["data"];
        if (data?["stream"] is not JsonArray streamArr) return null;

        JsonNode? lastUsage = null;
        string provider = fallbackProvider;
        string model = fallbackModel;
        long firstChunkMs = 0;

        foreach (var item in streamArr)
        {
            if (item == null) continue;
            if (firstChunkMs == 0)
            {
                long ct = item["time"]?.GetValue<long>() ?? item["time0"]?.GetValue<long>() ?? 0;
                if (ct > 0) firstChunkMs = ct;
            }
            var chunk = item["chunk"];
            if (chunk == null) continue;
            var chunkType = chunk["type"]?.GetValue<string>();
            if (chunkType == "usage" && chunk["usage"] != null)
                lastUsage = chunk["usage"];
            else if (chunkType == "finish")
                ExtractReplayModelProvider(chunk["replayState"], ref provider, ref model);
        }

        if (lastUsage == null) return null;
        var (totalIn, outTok, cacheRead, cacheWrite) = ParseUsageTokens(lastUsage);
        if (totalIn == 0 && outTok == 0) return null;

        long turn = data["turn"]?.GetValue<long>() ?? 0;
        long step = data["step"]?.GetValue<long>() ?? 0;
        long seq = node["seq"]?.GetValue<long>() ?? lineNo;
        long msgMs = node["time"]?.GetValue<long>() ?? 0;

        ComputeStepTiming(turn, step, msgMs, firstChunkMs, stepStarts, out long durationMs, out long ttftMs);
        var ts = ResolveLocalTimestamp(msgMs, filePath);

        return new UsageStorage.UsageRecord(
            UsageStorage.AgentDsh,
            provider,
            string.IsNullOrEmpty(provider) ? "dsh" : provider,
            model,
            totalIn,
            outTok,
            ts,
            cacheRead,
            cacheWrite,
            durationMs,
            ttftMs,
            $"dsh:{sessionId}:a:{seq}");
    }

    int ScanProjCacheFallback(ProjSessionEntry entry)
    {
        var rows = entry.Rows;
        var tokenUsage = rows["tokenUsage"]?["val"];
        if (tokenUsage == null) return 0;

        var totals = tokenUsage["totals"];
        if (totals == null) return 0;

        var (totalIn, outTok, cacheRead, cacheWrite) = ParseUsageTokens(totals);
        if (totalIn == 0 && outTok == 0) return 0;

        long turn = tokenUsage["last"]?["turn"]?.GetValue<long>() ?? 0;
        var snapKey = $"dsh:proj:{entry.SessionId}:snap";
        long prevIn = GetWatermark(snapKey + ":in");
        long prevOut = GetWatermark(snapKey + ":out");
        long prevCr = GetWatermark(snapKey + ":cr");
        long prevCw = GetWatermark(snapKey + ":cw");

        long dIn = Math.Max(0, totalIn - prevIn);
        long dOut = Math.Max(0, outTok - prevOut);
        long dCr = Math.Max(0, cacheRead - prevCr);
        long dCw = Math.Max(0, cacheWrite - prevCw);
        if (dIn == 0 && dOut == 0) return 0;

        long lastPromptAt = rows["sessionListMetadata"]?["val"]?["lastPromptAt"]?.GetValue<long>() ?? 0;
        var ts = ResolveLocalTimestamp(lastPromptAt, entry.FilePath);

        long durationMs = 0, ttftMs = 0;
        var stats = rows["sessionStats"]?["val"];
        if (stats != null)
        {
            long llmMs = stats["llmMs"]?.GetValue<long>() ?? 0;
            long decodeMs = stats["decodeMs"]?.GetValue<long>() ?? 0;
            long ttftSteps = stats["ttftSteps"]?.GetValue<long>() ?? 0;
            long totalTtft = stats["ttftMs"]?.GetValue<long>() ?? 0;
            if (ttftSteps > 0) ttftMs = totalTtft / ttftSteps;
            if (decodeMs > 0) durationMs = decodeMs;
            else if (llmMs > 0) durationMs = llmMs;
        }

        var rec = new UsageStorage.UsageRecord(
            UsageStorage.AgentDsh,
            entry.Provider,
            string.IsNullOrEmpty(entry.Provider) ? "dsh" : entry.Provider,
            entry.Model,
            dIn,
            dOut,
            ts,
            dCr,
            dCw,
            durationMs,
            ttftMs,
            $"dsh:proj:{entry.SessionId}:{turn}:{totalIn}:{outTok}");

        if (!RecordBatch(new[] { rec })) return 0;

        SetWatermark(snapKey + ":in", totalIn);
        SetWatermark(snapKey + ":out", outTok);
        SetWatermark(snapKey + ":cr", cacheRead);
        SetWatermark(snapKey + ":cw", cacheWrite);
        return 1;
    }

    static void InspectStreamArray(
        JsonArray streamArr,
        ref string provider,
        ref string model,
        ref long firstChunkMs)
    {
        foreach (var item in streamArr)
        {
            if (item == null) continue;
            var itemType = item["type"]?.GetValue<string>();
            var chunk = item["chunk"];
            var chunkType = chunk?["type"]?.GetValue<string>();
            if (firstChunkMs == 0 && itemType != "finish" && chunkType != "finish" && chunkType != "usage")
            {
                long ct = item["time"]?.GetValue<long>() ?? item["time0"]?.GetValue<long>() ?? 0;
                if (ct > 0) firstChunkMs = ct;
            }
            if (chunk != null && chunkType == "finish")
            {
                ExtractReplayModelProvider(chunk["replayState"], ref provider, ref model);
            }
        }
    }

    static void ExtractReplayModelProvider(JsonNode? replayState, ref string provider, ref string model)
    {
        var resp = replayState?["response"] ?? replayState;
        if (resp == null) return;
        var p = resp["provider"]?.GetValue<string>();
        var m = resp["model"]?.GetValue<string>() ?? resp["responseModel"]?.GetValue<string>();
        if (!string.IsNullOrEmpty(p)) provider = p;
        if (!string.IsNullOrEmpty(m)) model = m;
    }

    static (string Provider, string Model) ExtractModelSelection(JsonNode rows)
    {
        var lastUsed = rows["modelSelection"]?["val"]?["lastUsed"];
        var provider = lastUsed?["provider"]?.GetValue<string>() ?? "";
        var model = lastUsed?["model"]?.GetValue<string>() ?? "";
        return (provider, model);
    }

    static (long TotalIn, long OutTok, long CacheRead, long CacheWrite) ParseUsageTokens(JsonNode usage)
    {
        long uncachedIn = usage["uncachedInputTokens"]?.GetValue<long>()
            ?? usage["inputTokens"]?.GetValue<long>()
            ?? usage["input_tokens"]?.GetValue<long>()
            ?? 0;
        long outTok = usage["outputTokens"]?.GetValue<long>()
            ?? usage["output_tokens"]?.GetValue<long>()
            ?? 0;
        long cacheRead = usage["cacheReadTokens"]?.GetValue<long>()
            ?? usage["cache_read_tokens"]?.GetValue<long>()
            ?? 0;
        long cacheWrite = usage["cacheWriteTokens"]?.GetValue<long>()
            ?? usage["cache_write_tokens"]?.GetValue<long>()
            ?? 0;
        return (uncachedIn + cacheRead + cacheWrite, outTok, cacheRead, cacheWrite);
    }

    static void ComputeStepTiming(
        long turn,
        long step,
        long msgMs,
        long firstChunkMs,
        Dictionary<(long Turn, long Step), long> stepStarts,
        out long durationMs,
        out long ttftMs)
    {
        durationMs = 0;
        ttftMs = 0;

        long startMs = (turn > 0 && step > 0 && stepStarts.TryGetValue((turn, step), out var s)) ? s : 0;
        if (startMs > 0 && firstChunkMs > startMs)
        {
            long t = firstChunkMs - startMs;
            if (t > 0 && t < 3_600_000) ttftMs = t;
        }
        if (firstChunkMs > 0 && msgMs > firstChunkMs)
        {
            long d = msgMs - firstChunkMs;
            if (d > 0 && d < 3_600_000) durationMs = d;
        }
        else if (startMs > 0 && msgMs > startMs)
        {
            long d = msgMs - startMs;
            if (d > 0 && d < 3_600_000) durationMs = d;
        }
    }

    static DateTime ResolveLocalTimestamp(long unixMs, string fallbackFilePath)
    {
        if (unixMs > 0)
        {
            try { return DateTimeOffset.FromUnixTimeMilliseconds(unixMs).LocalDateTime; }
            catch { }
        }
        try { return File.GetLastWriteTime(fallbackFilePath); }
        catch { return DateTime.Now; }
    }

    static JsonNode? ParseJsonShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return JsonNode.Parse(fs);
    }

    static bool IsZstdFile(string path)
    {
        var ext = Path.GetExtension(path);
        return ext.Equals(".zstd", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".zst", StringComparison.OrdinalIgnoreCase);
    }
}

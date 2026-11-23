using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace APISwitch.Services;

/// <summary>
/// 本地会话用量扫描器：读取各客户端写在磁盘上的会话记录（JSONL / JSON / ZST / SQLite / NDJSON），
/// 解析其中的 token 用量并幂等入库。应用未运行时客户端产生的用量也能被统计到。
///
/// 增量扫描：记录每个文件已处理到的字节偏移或水位（usage_scan_state.json），
/// 配合 UsageStorage 的 source_id 幂等去重键，杜绝重复计数与漏采。
/// </summary>
public static class UsageScanner
{
    const int CurrentScannerVersion = 4;

    static string StateFile => Path.Combine(AgPaths.AppData, "APISwitch", "usage_scan_state.json");

    // 每个文件已处理到的字节偏移或水位
    static Dictionary<string, long> _offsets = new(StringComparer.OrdinalIgnoreCase);

    // 每个文件扫描结束时正在使用的模型/供应商（增量扫描恢复用，避免丢失导致记录为空）
    static Dictionary<string, string> _fileModels = new(StringComparer.OrdinalIgnoreCase);
    static Dictionary<string, string> _fileProviders = new(StringComparer.OrdinalIgnoreCase);

    static int _scannerVersion;
    static string? _loadedStatePath;

    // Codex 轮次计时：turn_id -> (轮次开始时间, 首个 AgentMessage 时间)
    static readonly Dictionary<string, (DateTime Start, DateTime FirstAgent)> _codexTurns = new(StringComparer.Ordinal);

    // 扫描锁：避免主窗口与用量监控窗口并发执行时破坏字典与状态文件
    static readonly object _scanLock = new();

    static readonly IUsageSource[] _sources = new IUsageSource[]
    {
        new StepCodeUsageSource(),
        new ZCodeUsageSource(),
        new DshUsageSource(),
        new GeminiCliUsageSource(),
        new AntigravityUsageSource(),
    };

    static void EnsureStateLoaded()
    {
        var path = StateFile;
        if (_loadedStatePath == path) return;
        LoadState();
        _loadedStatePath = path;
    }

    static void LoadState()
    {
        _offsets = new(StringComparer.OrdinalIgnoreCase);
        _fileModels = new(StringComparer.OrdinalIgnoreCase);
        _fileProviders = new(StringComparer.OrdinalIgnoreCase);
        _scannerVersion = 0;

        try
        {
            if (File.Exists(StateFile))
            {
                var json = File.ReadAllText(StateFile);
                var obj = JsonNode.Parse(json)?.AsObject();
                if (obj != null)
                {
                    if (obj["scanner_version"]?.GetValue<int>() is int ver)
                        _scannerVersion = ver;

                    if (obj["offsets"] is JsonObject offs)
                    {
                        foreach (var kv in offs)
                            if (kv.Value?.GetValue<long>() is long off)
                                _offsets[kv.Key] = off;
                        if (obj["models"] is JsonObject mods)
                            foreach (var kv in mods)
                            {
                                var v = kv.Value?.GetValue<string>();
                                if (!string.IsNullOrEmpty(v)) _fileModels[kv.Key] = v;
                            }
                        if (obj["providers"] is JsonObject provs)
                            foreach (var kv in provs)
                            {
                                var v = kv.Value?.GetValue<string>();
                                if (!string.IsNullOrEmpty(v)) _fileProviders[kv.Key] = v;
                            }
                    }
                    else
                    {
                        foreach (var kv in obj)
                            if (kv.Value?.GetValue<long>() is long off)
                                _offsets[kv.Key] = off;
                    }
                }
            }
        }
        catch { }
    }

    static void SaveState()
    {
        try
        {
            var offs = new JsonObject();
            foreach (var kv in _offsets) offs[kv.Key] = kv.Value;
            var mods = new JsonObject();
            foreach (var kv in _fileModels) mods[kv.Key] = kv.Value;
            var provs = new JsonObject();
            foreach (var kv in _fileProviders) provs[kv.Key] = kv.Value;
            var obj = new JsonObject
            {
                ["scanner_version"] = _scannerVersion,
                ["offsets"] = offs,
                ["models"] = mods,
                ["providers"] = provs,
            };
            var dir = Path.GetDirectoryName(StateFile);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(StateFile, obj.ToJsonString());
            _loadedStatePath = StateFile;
        }
        catch { }
    }

    /// <summary>执行一次全量增量扫描。返回本次新增的记录条数。</summary>
    public static int ScanAll()
    {
        lock (_scanLock)
        {
            EnsureStateLoaded();

            // 版本自愈：老版本数据库缺乏 source_id 去重且存在漏采/缓存口径偏差，
            // 升级后首次扫描自动备份并重建，确保旧账目立刻精准校准。
            if (_scannerVersion < CurrentScannerVersion)
            {
                return BackfillCore(backupIfExists: true).Written;
            }

            return ScanIncrementalCore();
        }
    }

    static int ScanIncrementalCore()
    {
        int added = 0;
        added += ScanCodex();
        added += ScanClaude();
        added += ScanClaudeDesktop();
        added += ScanPi();
        added += ScanOpenCode();

        var ctx = new UsageScanContext(_offsets, _scanLock);
        foreach (var src in _sources)
            added += src.Scan(ctx);

        PruneCodexTurns();
        SaveState();
        return added;
    }

    /// <summary>回填结果。</summary>
    public record BackfillResult(int Written, string? BackupPath);

    /// <summary>
    /// 全量重建用量事件表：备份 → 清空 → 重扫全部会话日志。
    /// </summary>
    public static BackfillResult BackfillAll()
    {
        lock (_scanLock)
        {
            EnsureStateLoaded();
            return BackfillCore(backupIfExists: true);
        }
    }

    static BackfillResult BackfillCore(bool backupIfExists)
    {
        var backup = backupIfExists ? UsageStorage.BackupDatabase() : null;
        UsageStorage.ClearEvents();

        _offsets = new(StringComparer.OrdinalIgnoreCase);
        _fileModels = new(StringComparer.OrdinalIgnoreCase);
        _fileProviders = new(StringComparer.OrdinalIgnoreCase);
        _codexTurns.Clear();
        _scannerVersion = CurrentScannerVersion;

        var written = ScanIncrementalCore();
        return new BackfillResult(written, backup);
    }

    // ---------- Codex ----------

    static int ScanCodex()
    {
        var sessionsRoot = Path.Combine(ClaudeCli.HomeDir, ".codex", "sessions");
        var archivedRoot = Path.Combine(ClaudeCli.HomeDir, ".codex", "archived_sessions");

        var jsonlFiles = new List<string>();
        var zstFiles = new List<string>();

        foreach (var root in new[] { sessionsRoot, archivedRoot })
        {
            if (!Directory.Exists(root)) continue;
            CollectRecentFiles(root, "*.jsonl", 400, jsonlFiles);
            CollectRecentFiles(root, "*.jsonl.zst", 400, zstFiles);
        }

        // 去重：若某会话同时存在未压缩 .jsonl 和 .jsonl.zst，优先扫描未压缩的 .jsonl
        var jsonlSet = new HashSet<string>(jsonlFiles, StringComparer.OrdinalIgnoreCase);
        var scannedThreads = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var coveredFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        int added = 0;
        foreach (var f in jsonlFiles)
        {
            coveredFiles.Add(f);
            added += ScanCodexJsonlFile(f, scannedThreads);
        }

        foreach (var zf in zstFiles)
        {
            var plainPath = zf.EndsWith(".zst", StringComparison.OrdinalIgnoreCase)
                ? zf.Substring(0, zf.Length - 4)
                : zf;
            coveredFiles.Add(plainPath);
            if (jsonlSet.Contains(plainPath)) continue;
            added += ScanCodexZstFile(zf, scannedThreads);
        }

        added += ScanCodexSqliteFallback(coveredFiles, scannedThreads);
        return added;
    }

    static void PruneCodexTurns()
    {
        if (_codexTurns.Count == 0) return;
        var cutoff = DateTime.Now.AddHours(-2);
        var stale = new List<string>();
        foreach (var kv in _codexTurns)
            if (kv.Value.Start < cutoff) stale.Add(kv.Key);
        foreach (var k in stale) _codexTurns.Remove(k);

        if (_codexTurns.Count > 5000)
        {
            foreach (var kv in _codexTurns.OrderBy(kv => kv.Value.Start).Take(_codexTurns.Count - 5000))
                _codexTurns.Remove(kv.Key);
        }
    }

    static int ScanCodexJsonlFile(string file, HashSet<string> scannedThreads)
    {
        long start = _offsets.TryGetValue(file, out var off) ? off : 0;
        var info = new FileInfo(file);
        if (!info.Exists || info.Length <= start) return 0;

        try
        {
            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (start > 0) fs.Seek(start, SeekOrigin.Begin);
            using var reader = new StreamReader(fs);

            var result = ReadCodexStream(reader, file, scannedThreads);
            if (result.Count == 0 || UsageStorage.RecordBatch(result))
            {
                _offsets[file] = fs.Position;
                return result.Count;
            }
        }
        catch { }
        return 0;
    }

    static int ScanCodexZstFile(string file, HashSet<string> scannedThreads)
    {
        var wmKey = "zst:" + file;
        var info = new FileInfo(file);
        if (!info.Exists) return 0;
        if (_offsets.TryGetValue(wmKey, out var prevLen) && prevLen == info.Length) return 0;

        try
        {
            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var zst = new ZstdSharp.DecompressionStream(fs);
            using var reader = new StreamReader(zst);

            // 归档压缩文件从头解析：重置该文件的上下文与 token_count 基准
            _fileModels.Remove(file);
            _fileProviders.Remove(file);
            _offsets.Remove("codex_tc_tot:" + file);

            var result = ReadCodexStream(reader, file, scannedThreads);
            if (result.Count == 0 || UsageStorage.RecordBatch(result))
            {
                _offsets[wmKey] = info.Length;
                return result.Count;
            }
        }
        catch { }
        return 0;
    }

    static List<UsageStorage.UsageRecord> ReadCodexStream(StreamReader reader, string file, HashSet<string> scannedThreads)
    {
        string currentModel = _fileModels.TryGetValue(file, out var m0) ? m0 : "";
        string currentProvider = _fileProviders.TryGetValue(file, out var p0) ? p0 : "";
        string sessionId = Path.GetFileNameWithoutExtension(file);
        bool seenTur = _offsets.TryGetValue("codex_tur:" + file, out var turFlag) && turFlag == 1;
        long prevTcTotal = _offsets.TryGetValue("codex_tc_tot:" + file, out var tcTot) ? tcTot : 0;

        var turRecords = new List<UsageStorage.UsageRecord>();
        var tcRecords = new List<UsageStorage.UsageRecord>();

        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            if (line.Contains("\"session_meta\"", StringComparison.Ordinal))
            {
                try
                {
                    var node = JsonNode.Parse(line);
                    if (node?["type"]?.GetValue<string>() == "session_meta")
                    {
                        var payload = node["payload"];
                        var sid = payload?["id"]?.GetValue<string>() ?? payload?["session_id"]?.GetValue<string>();
                        if (!string.IsNullOrEmpty(sid))
                        {
                            sessionId = sid;
                            scannedThreads.Add(sid);
                        }
                        if (payload?["model_provider"]?.GetValue<string>() is { Length: > 0 } mp)
                            currentProvider = mp;
                        if (payload?["model"]?.GetValue<string>() is { Length: > 0 } mm)
                            currentModel = mm;
                        continue;
                    }
                }
                catch { }
            }

            if (line.Contains("\"turn_context\"", StringComparison.Ordinal))
            {
                try
                {
                    var node = JsonNode.Parse(line);
                    if (node?["type"]?.GetValue<string>() == "turn_context")
                    {
                        var payload = node["payload"];
                        if (payload?["model"]?.GetValue<string>() is { Length: > 0 } tm)
                            currentModel = tm;
                        if (payload?["model_provider"]?.GetValue<string>() is { Length: > 0 } tp)
                            currentProvider = tp;
                        continue;
                    }
                }
                catch { }
            }

            if (line.Contains("thread_settings_applied", StringComparison.Ordinal))
            {
                try
                {
                    var sNode = JsonNode.Parse(line);
                    var settings = sNode?["payload"]?["thread_settings"];
                    if (settings?["model"]?.GetValue<string>() is { Length: > 0 } m)
                        currentModel = m;
                    if (settings?["model_provider_id"]?.GetValue<string>() is { Length: > 0 } p)
                        currentProvider = p;
                    continue;
                }
                catch { }
            }

            if (line.Contains("\"task_started\"", StringComparison.Ordinal))
            {
                try
                {
                    var tn = JsonNode.Parse(line);
                    var turnId = tn?["payload"]?["turn_id"]?.GetValue<string>();
                    if (!string.IsNullOrEmpty(turnId) &&
                        DateTime.TryParse(tn?["timestamp"]?.GetValue<string>(), CultureInfo.InvariantCulture,
                            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var tstart))
                    {
                        _codexTurns[turnId] = (tstart.ToLocalTime(), DateTime.MinValue);
                    }
                }
                catch { }
                continue;
            }

            if (line.Contains("\"item_completed\"", StringComparison.Ordinal))
            {
                try
                {
                    var in_ = JsonNode.Parse(line);
                    if (in_?["payload"]?["item"]?["type"]?.GetValue<string>() == "AgentMessage")
                    {
                        var turnId = in_?["payload"]?["turn_id"]?.GetValue<string>();
                        if (!string.IsNullOrEmpty(turnId) &&
                            DateTime.TryParse(in_?["timestamp"]?.GetValue<string>(), CultureInfo.InvariantCulture,
                                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var tAgent))
                        {
                            var local = tAgent.ToLocalTime();
                            if (_codexTurns.TryGetValue(turnId, out var cur))
                            {
                                if (cur.FirstAgent == DateTime.MinValue)
                                    _codexTurns[turnId] = (cur.Start, local);
                            }
                            else
                            {
                                _codexTurns[turnId] = (local, local);
                            }
                        }
                    }
                }
                catch { }
                continue;
            }

            if (line.Contains("\"token_usage_record\"", StringComparison.Ordinal))
            {
                var rec = ParseCodexUsageRecord(line, file, currentModel, currentProvider);
                if (rec != null)
                {
                    turRecords.Add(rec);
                    seenTur = true;
                }
                continue;
            }

            if (!seenTur && line.Contains("\"token_count\"", StringComparison.Ordinal))
            {
                var tcRec = ParseCodexTokenCount(line, sessionId, currentModel, currentProvider, ref prevTcTotal);
                if (tcRec != null)
                    tcRecords.Add(tcRec);
            }
        }

        _fileModels[file] = currentModel;
        _fileProviders[file] = currentProvider;
        if (seenTur) _offsets["codex_tur:" + file] = 1;
        if (prevTcTotal > 0) _offsets["codex_tc_tot:" + file] = prevTcTotal;

        return seenTur ? turRecords : tcRecords;
    }

    static UsageStorage.UsageRecord? ParseCodexUsageRecord(string line, string file, string currentModel, string currentProvider)
    {
        try
        {
            var node = JsonNode.Parse(line);
            if (node?["type"]?.GetValue<string>() != "token_usage_record") return null;

            var payload = node["payload"];
            var usage = payload?["usage"];
            if (usage == null) return null;

            long inTok = usage["input_tokens"]?.GetValue<long>() ?? 0;
            long outTok = usage["output_tokens"]?.GetValue<long>() ?? 0;
            long cacheRead = usage["cached_input_tokens"]?.GetValue<long>() ?? 0;
            long cacheWrite = usage["cache_write_input_tokens"]?.GetValue<long>() ?? 0;
            if (inTok < cacheRead + cacheWrite) inTok += cacheRead + cacheWrite;
            if (inTok == 0 && outTok == 0) return null;

            DateTime ts = DateTime.Now;
            if (DateTime.TryParse(node?["timestamp"]?.GetValue<string>(),
                    CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var dt))
                ts = dt.ToLocalTime();

            long durationMs = 0;
            long ttftMs = 0;
            var turnId = payload?["turn_id"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(turnId) && _codexTurns.TryGetValue(turnId, out var turn))
            {
                if (turn.Start != DateTime.MinValue && ts > turn.Start)
                    durationMs = (long)(ts - turn.Start).TotalMilliseconds;
                if (turn.FirstAgent != DateTime.MinValue && turn.FirstAgent > turn.Start)
                    ttftMs = (long)(turn.FirstAgent - turn.Start).TotalMilliseconds;
                _codexTurns.Remove(turnId);
            }

            string respId = payload?["response_id"]?.GetValue<string>() ?? "";
            long ordinal = node?["ordinal"]?.GetValue<long>() ?? 0;
            string sid = !string.IsNullOrEmpty(respId)
                ? "codex:" + respId
                : (ordinal > 0 ? $"codex:ord:{Path.GetFileName(file)}:{ordinal}" : "");

            return new UsageStorage.UsageRecord(
                UsageStorage.AgentCodex, currentProvider, "Codex", currentModel,
                inTok, outTok, ts,
                CacheReadTokens: cacheRead,
                CacheWriteTokens: cacheWrite,
                DurationMs: durationMs,
                TtftMs: ttftMs,
                SourceId: sid);
        }
        catch { return null; }
    }

    static UsageStorage.UsageRecord? ParseCodexTokenCount(string line, string sessionId,
        string currentModel, string currentProvider, ref long prevTotalTokens)
    {
        try
        {
            var node = JsonNode.Parse(line);
            if (node?["type"]?.GetValue<string>() != "event_msg") return null;
            var payload = node["payload"];
            if (payload?["type"]?.GetValue<string>() != "token_count") return null;

            var info = payload["info"];
            if (info == null) return null;

            long totalTokens = info["total_token_usage"]?["total_tokens"]?.GetValue<long>() ?? 0;
            if (totalTokens <= prevTotalTokens) return null;
            prevTotalTokens = totalTokens;

            var last = info["last_token_usage"] ?? info["total_token_usage"];
            if (last == null) return null;

            long inTok = last["input_tokens"]?.GetValue<long>() ?? 0;
            long outTok = last["output_tokens"]?.GetValue<long>() ?? 0;
            long cacheRead = last["cached_input_tokens"]?.GetValue<long>() ?? 0;
            long cacheWrite = last["cache_write_input_tokens"]?.GetValue<long>() ?? 0;
            if (inTok < cacheRead + cacheWrite) inTok += cacheRead + cacheWrite;
            if (inTok == 0 && outTok == 0) return null;

            DateTime ts = DateTime.Now;
            if (DateTime.TryParse(node?["timestamp"]?.GetValue<string>(),
                    CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var dt))
                ts = dt.ToLocalTime();

            long ordinal = node?["ordinal"]?.GetValue<long>() ?? totalTokens;
            string sid = $"codex:tc:{sessionId}:{ordinal}";

            return new UsageStorage.UsageRecord(
                UsageStorage.AgentCodex, currentProvider, "Codex", currentModel,
                inTok, outTok, ts,
                CacheReadTokens: cacheRead,
                CacheWriteTokens: cacheWrite,
                SourceId: sid);
        }
        catch { return null; }
    }

    static int ScanCodexSqliteFallback(HashSet<string> coveredFiles, HashSet<string> scannedThreads)
    {
        var dbPath = Path.Combine(ClaudeCli.HomeDir, ".codex", "state_5.sqlite");
        if (!File.Exists(dbPath)) return 0;

        var batch = new List<UsageStorage.UsageRecord>();
        try
        {
            using var conn = new Microsoft.Data.Sqlite.SqliteConnection(
                $"Data Source={dbPath};Mode=ReadOnly;Cache=Private");
            conn.Open();

            using var probe = conn.CreateCommand();
            probe.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='threads';";
            if (Convert.ToInt64(probe.ExecuteScalar()) == 0) return 0;

            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT id, rollout_path, model_provider, model, tokens_used, updated_at FROM threads WHERE tokens_used > 0;";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var id = r.IsDBNull(0) ? "" : r.GetString(0);
                if (string.IsNullOrEmpty(id) || scannedThreads.Contains(id)) continue;

                var rawPath = r.IsDBNull(1) ? "" : r.GetString(1);
                var normPath = rawPath.StartsWith(@"\\?\", StringComparison.Ordinal) ? rawPath.Substring(4) : rawPath;
                if (!string.IsNullOrEmpty(normPath) &&
                    (coveredFiles.Contains(normPath) || File.Exists(normPath) || File.Exists(normPath + ".zst")))
                {
                    continue;
                }

                string wmKey = "codex:thread:" + id;
                long tokensUsed = r.GetInt64(4);
                if (_offsets.TryGetValue(wmKey, out var prevTok) && prevTok == tokensUsed) continue;

                var provider = r.IsDBNull(2) ? "" : r.GetString(2);
                var model = r.IsDBNull(3) ? "" : r.GetString(3);
                long updatedAt = r.IsDBNull(5) ? 0 : r.GetInt64(5);
                var ts = updatedAt > 0
                    ? DateTimeOffset.FromUnixTimeSeconds(updatedAt).LocalDateTime
                    : DateTime.Now;

                long inTok = tokensUsed * 95 / 100;
                long outTok = tokensUsed - inTok;
                batch.Add(new UsageStorage.UsageRecord(
                    UsageStorage.AgentCodex, provider, "Codex", model,
                    inTok, outTok, ts, SourceId: wmKey));
            }

            if (batch.Count > 0 && UsageStorage.RecordBatch(batch))
            {
                foreach (var rec in batch)
                    _offsets[rec.SourceId] = rec.InTokens + rec.OutTokens;
                return batch.Count;
            }
        }
        catch { }
        return 0;
    }

    // ---------- Claude Code ----------

    static int ScanClaude()
    {
        var root = Path.Combine(ClaudeCli.HomeDir, ".claude", "projects");
        int added = 0;

        if (Directory.Exists(root))
        {
            foreach (var file in EnumerateRecentJsonl(root, maxAgeDays: 400))
                added += ScanClaudeFile(file);
        }

        added += ScanClaudeStatsCache();
        return added;
    }

    static int ScanClaudeFile(string file)
    {
        long start = _offsets.TryGetValue(file, out var off) ? off : 0;
        var info = new FileInfo(file);
        if (!info.Exists || info.Length <= start) return 0;

        var deduped = new Dictionary<string, UsageStorage.UsageRecord>(StringComparer.Ordinal);
        var fallback = new List<UsageStorage.UsageRecord>();
        try
        {
            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (start > 0) fs.Seek(start, SeekOrigin.Begin);
            using var reader = new StreamReader(fs);

            DateTime lastUserTs = DateTime.MinValue;
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var rec = ParseClaudeLine(line, ref lastUserTs);
                if (rec == null) continue;

                if (!string.IsNullOrEmpty(rec.SourceId))
                {
                    if (deduped.TryGetValue(rec.SourceId, out var prev))
                        deduped[rec.SourceId] = MergeUsageRecord(prev, rec);
                    else
                        deduped[rec.SourceId] = rec;
                }
                else
                {
                    fallback.Add(rec);
                }
            }

            var batch = new List<UsageStorage.UsageRecord>(deduped.Count + fallback.Count);
            batch.AddRange(deduped.Values);
            batch.AddRange(fallback);

            if (batch.Count == 0 || UsageStorage.RecordBatch(batch))
            {
                _offsets[file] = fs.Position;
                return batch.Count;
            }
        }
        catch { }
        return 0;
    }

    static UsageStorage.UsageRecord? ParseClaudeLine(string line, ref DateTime lastUserTs)
    {
        try
        {
            var node = JsonNode.Parse(line);
            var type = node?["type"]?.GetValue<string>();
            if (type == "user")
            {
                if (DateTime.TryParse(node?["timestamp"]?.GetValue<string>(),
                        CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var ut))
                    lastUserTs = ut.ToLocalTime();
                return null;
            }
            if (type != "assistant") return null;

            var msg = node?["message"];
            var usage = msg?["usage"];
            if (usage == null) return null;

            string model = msg?["model"]?.GetValue<string>() ?? "";
            if (string.Equals(model, "<synthetic>", StringComparison.OrdinalIgnoreCase)) return null;

            long rawIn = usage["input_tokens"]?.GetValue<long>() ?? 0;
            long outTok = usage["output_tokens"]?.GetValue<long>() ?? 0;
            long cacheRead = usage["cache_read_input_tokens"]?.GetValue<long>() ?? 0;
            long cacheWrite = usage["cache_creation_input_tokens"]?.GetValue<long>() ?? 0;

            var inferenceGeo = usage["inference_geo"]?.GetValue<string>();
            bool isUncachedOnly = !string.IsNullOrEmpty(inferenceGeo) || rawIn < (cacheRead + cacheWrite);
            long totalIn = isUncachedOnly ? (rawIn + cacheRead + cacheWrite) : rawIn;

            if (totalIn == 0 && outTok == 0) return null;

            DateTime ts = DateTime.Now;
            if (DateTime.TryParse(node?["timestamp"]?.GetValue<string>(),
                    CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var dt))
                ts = dt.ToLocalTime();

            long durationMs = 0;
            long ttftMs = 0;
            if (lastUserTs != DateTime.MinValue && ts > lastUserTs)
            {
                durationMs = (long)(ts - lastUserTs).TotalMilliseconds;
                ttftMs = durationMs;
            }

            string msgId = msg?["id"]?.GetValue<string>()
                ?? node?["requestId"]?.GetValue<string>()
                ?? node?["uuid"]?.GetValue<string>()
                ?? "";
            string sid = !string.IsNullOrEmpty(msgId) ? "claude:" + msgId : "";

            return new UsageStorage.UsageRecord(
                UsageStorage.AgentClaudeCli, "", "Claude Code", model,
                totalIn, outTok, ts,
                CacheReadTokens: cacheRead,
                CacheWriteTokens: cacheWrite,
                DurationMs: durationMs,
                TtftMs: ttftMs,
                SourceId: sid);
        }
        catch { return null; }
    }

    static int ScanClaudeStatsCache()
    {
        var scPath = Path.Combine(ClaudeCli.HomeDir, ".claude", "stats-cache.json");
        if (!File.Exists(scPath)) return 0;

        long mtimeTicks = File.GetLastWriteTimeUtc(scPath).Ticks;
        const string wmKey = "claude:stats-cache:mtime";
        if (_offsets.TryGetValue(wmKey, out var prevTicks) && prevTicks == mtimeTicks) return 0;

        try
        {
            var text = File.ReadAllText(scPath);
            var root = JsonNode.Parse(text)?.AsObject();
            if (root == null) return 0;

            var modelUsage = root["modelUsage"] as JsonObject;
            var dailyArr = root["dailyModelTokens"] as JsonArray;
            if (dailyArr == null || dailyArr.Count == 0)
            {
                _offsets[wmKey] = mtimeTicks;
                return 0;
            }

            // 仅对已被 Claude 30天清理策略删除、没有任何逐条 JSONL 记录的日期进行兜底补齐
            var existingLogDates = UsageStorage.GetAgentLogDates(UsageStorage.AgentClaudeCli);
            var batch = new List<UsageStorage.UsageRecord>();

            foreach (var dayNode in dailyArr)
            {
                var dateStr = dayNode?["date"]?.GetValue<string>();
                if (string.IsNullOrEmpty(dateStr) || existingLogDates.Contains(dateStr)) continue;
                if (!DateTime.TryParseExact(dateStr, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedDate))
                    continue;

                var ts = new DateTime(parsedDate.Year, parsedDate.Month, parsedDate.Day, 12, 0, 0, DateTimeKind.Local);
                if (dayNode?["tokensByModel"] is not JsonObject tokensByModel) continue;

                foreach (var kv in tokensByModel)
                {
                    var model = kv.Key;
                    long dailyVal = kv.Value?.GetValue<long>() ?? 0;
                    if (dailyVal <= 0) continue;

                    long totalIn, outTok, cr = 0, cw = 0;
                    var mu = modelUsage?[model] as JsonObject;
                    long muIn = mu?["inputTokens"]?.GetValue<long>() ?? 0;
                    long muOut = mu?["outputTokens"]?.GetValue<long>() ?? 0;
                    long muCr = mu?["cacheReadInputTokens"]?.GetValue<long>() ?? 0;
                    long muCw = mu?["cacheCreationInputTokens"]?.GetValue<long>() ?? 0;
                    long denom = muIn + muOut;

                    if (denom > 0)
                    {
                        long uncachedIn = (long)Math.Round(dailyVal * ((double)muIn / denom));
                        outTok = Math.Max(0, dailyVal - uncachedIn);
                        cr = (long)Math.Round(dailyVal * ((double)muCr / denom));
                        cw = (long)Math.Round(dailyVal * ((double)muCw / denom));
                        totalIn = (muIn < muCr + muCw) ? (uncachedIn + cr + cw) : uncachedIn;
                    }
                    else
                    {
                        totalIn = dailyVal * 95 / 100;
                        outTok = dailyVal - totalIn;
                    }

                    batch.Add(new UsageStorage.UsageRecord(
                        UsageStorage.AgentClaudeCli, "", "Claude Code", model,
                        totalIn, outTok, ts,
                        CacheReadTokens: cr,
                        CacheWriteTokens: cw,
                        SourceId: $"claude:stats-cache:{dateStr}:{model}"));
                }
            }

            if (batch.Count == 0 || UsageStorage.RecordBatch(batch))
            {
                _offsets[wmKey] = mtimeTicks;
                return batch.Count;
            }
        }
        catch { }
        return 0;
    }

    // ---------- Claude Desktop ----------

    static IEnumerable<string> GetClaudeDesktopRoots()
    {
        var localOverride = Environment.GetEnvironmentVariable("APISWITCH_LOCALAPPDATA");
        if (!string.IsNullOrWhiteSpace(localOverride))
        {
            yield return Path.Combine(localOverride, "Claude-3p");
            yield break;
        }
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("APISWITCH_HOME")))
            yield break;

        var localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var appData = AgPaths.AppData;
        yield return Path.Combine(localApp, "Claude-3p");
        yield return Path.Combine(appData, "Claude");
    }

    static int ScanClaudeDesktop()
    {
        var roots = GetClaudeDesktopRoots().Where(Directory.Exists).ToList();
        if (roots.Count == 0) return 0;

        // 收集已存在于 ~/.claude/projects 中的 cliSessionId，避免与 CLI 日志重复统计
        var existingCliSessions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var claudeProjRoot = Path.Combine(ClaudeCli.HomeDir, ".claude", "projects");
        if (Directory.Exists(claudeProjRoot))
        {
            try
            {
                foreach (var f in Directory.EnumerateFiles(claudeProjRoot, "*.jsonl", SearchOption.AllDirectories))
                    existingCliSessions.Add(Path.GetFileNameWithoutExtension(f));
            }
            catch { }
        }

        // 建立桌面 local_*.json 的 sessionId -> cliSessionId 映射
        var localToCli = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots)
        {
            var ccDir = Path.Combine(root, "claude-code-sessions");
            if (!Directory.Exists(ccDir)) continue;
            try
            {
                foreach (var f in Directory.EnumerateFiles(ccDir, "local_*.json", SearchOption.AllDirectories))
                {
                    try
                    {
                        var node = JsonNode.Parse(File.ReadAllText(f));
                        var sid = node?["sessionId"]?.GetValue<string>();
                        var cid = node?["cliSessionId"]?.GetValue<string>();
                        if (!string.IsNullOrEmpty(sid) && !string.IsNullOrEmpty(cid))
                            localToCli[sid] = cid;
                    }
                    catch { }
                }
            }
            catch { }
        }

        int added = 0;
        foreach (var root in roots)
        {
            var lamDir = Path.Combine(root, "local-agent-mode-sessions");
            if (!Directory.Exists(lamDir)) continue;

            try
            {
                foreach (var ledger in Directory.EnumerateFiles(lamDir, "*.ndjson", SearchOption.AllDirectories))
                {
                    if (!ledger.Contains("usage-ledger", StringComparison.OrdinalIgnoreCase)) continue;
                    added += ScanClaudeDesktopLedgerFile(ledger, localToCli, existingCliSessions);
                }
            }
            catch { }
        }
        return added;
    }

    static int ScanClaudeDesktopLedgerFile(string file, Dictionary<string, string> localToCli, HashSet<string> existingCliSessions)
    {
        var wmKey = "desktop:" + file;
        long start = _offsets.TryGetValue(wmKey, out var off) ? off : 0;
        var info = new FileInfo(file);
        if (!info.Exists || info.Length <= start) return 0;

        var batch = new List<UsageStorage.UsageRecord>();
        try
        {
            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (start > 0) fs.Seek(start, SeekOrigin.Begin);
            using var reader = new StreamReader(fs);

            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var node = JsonNode.Parse(line);
                    if (node == null) continue;

                    var sid = node["sessionId"]?.GetValue<string>() ?? "";
                    if (!string.IsNullOrEmpty(sid) &&
                        localToCli.TryGetValue(sid, out var cliId) &&
                        existingCliSessions.Contains(cliId))
                    {
                        continue;
                    }

                    long tsMs = node["ts"]?.GetValue<long>() ?? 0;
                    var ts = tsMs > 0
                        ? DateTimeOffset.FromUnixTimeMilliseconds(tsMs).LocalDateTime
                        : File.GetLastWriteTime(file);

                    if (node["models"] is not JsonObject modelsObj) continue;
                    foreach (var kv in modelsObj)
                    {
                        if (kv.Value is not JsonObject mu) continue;
                        long inTok = mu["inputTokens"]?.GetValue<long>() ?? 0;
                        long outTok = mu["outputTokens"]?.GetValue<long>() ?? 0;
                        long cr = mu["cacheReadTokens"]?.GetValue<long>() ?? 0;
                        long cw = mu["cacheWriteTokens"]?.GetValue<long>() ?? 0;
                        if (inTok < cr + cw) inTok += cr + cw;
                        if (inTok == 0 && outTok == 0) continue;

                        string cleanModel = ClaudeCli.Strip1m(kv.Key);
                        string sourceId = $"claude-desktop:{sid}:{tsMs}:{cleanModel}";
                        batch.Add(new UsageStorage.UsageRecord(
                            UsageStorage.AgentClaudeDesktop, "", "Claude Desktop", cleanModel,
                            inTok, outTok, ts,
                            CacheReadTokens: cr,
                            CacheWriteTokens: cw,
                            SourceId: sourceId));
                    }
                }
                catch { }
            }

            if (batch.Count == 0 || UsageStorage.RecordBatch(batch))
            {
                _offsets[wmKey] = fs.Position;
                return batch.Count;
            }
        }
        catch { }
        return 0;
    }

    // ---------- Pi ----------

    static int ScanPi()
    {
        var root = Path.Combine(ClaudeCli.HomeDir, ".pi", "agent", "sessions");
        if (!Directory.Exists(root)) return 0;

        int added = 0;
        foreach (var file in EnumerateRecentJsonl(root, maxAgeDays: 400))
            added += ScanPiFile(file);
        return added;
    }

    static int ScanPiFile(string file)
    {
        long start = _offsets.TryGetValue(file, out var off) ? off : 0;
        var info = new FileInfo(file);
        if (!info.Exists || info.Length <= start) return 0;

        var batch = new List<UsageStorage.UsageRecord>();
        try
        {
            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (start > 0) fs.Seek(start, SeekOrigin.Begin);
            using var reader = new StreamReader(fs);

            string currentModel = _fileModels.TryGetValue(file, out var m0) ? m0 : "";
            string currentProvider = _fileProviders.TryGetValue(file, out var p0) ? p0 : "";
            DateTime lastUserTs = DateTime.MinValue;

            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                if (line.Contains("\"role\":\"user\"", StringComparison.Ordinal) ||
                    line.Contains("\"role\": \"user\"", StringComparison.Ordinal))
                {
                    try
                    {
                        var un = JsonNode.Parse(line);
                        if (DateTime.TryParse(un?["timestamp"]?.GetValue<string>(), CultureInfo.InvariantCulture,
                                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var ut))
                            lastUserTs = ut.ToLocalTime();
                    }
                    catch { }
                    continue;
                }

                if (line.Contains("\"model_change\"", StringComparison.Ordinal))
                {
                    try
                    {
                        var mc = JsonNode.Parse(line);
                        if (mc?["modelId"]?.GetValue<string>() is { Length: > 0 } m) currentModel = m;
                        if (mc?["provider"]?.GetValue<string>() is { Length: > 0 } p) currentProvider = p;
                    }
                    catch { }
                    continue;
                }

                var rec = ParsePiLine(line, currentModel, currentProvider, lastUserTs);
                if (rec != null) batch.Add(rec);
            }

            if (batch.Count == 0 || UsageStorage.RecordBatch(batch))
            {
                _offsets[file] = fs.Position;
                _fileModels[file] = currentModel;
                _fileProviders[file] = currentProvider;
                return batch.Count;
            }
        }
        catch { }
        return 0;
    }

    static UsageStorage.UsageRecord? ParsePiLine(string line, string currentModel, string currentProvider, DateTime lastUserTs)
    {
        try
        {
            var node = JsonNode.Parse(line);
            if (node?["type"]?.GetValue<string>() != "message") return null;
            var msg = node["message"];
            if (msg?["role"]?.GetValue<string>() != "assistant") return null;

            var usage = msg?["usage"];
            if (usage == null) return null;

            long inTok = usage["input"]?.GetValue<long>() ?? 0;
            long outTok = usage["output"]?.GetValue<long>() ?? 0;
            long cacheRead = usage["cacheRead"]?.GetValue<long>() ?? 0;
            long cacheWrite = usage["cacheWrite"]?.GetValue<long>() ?? 0;
            long totalTokens = usage["totalTokens"]?.GetValue<long>() ?? 0;

            // Pi 的 input 仅记录未命中缓存的输入；真实总输入对齐 totalTokens - outTok 或 in + cacheRead + cacheWrite
            long totalInTok = totalTokens > 0
                ? Math.Max(inTok, totalTokens - outTok)
                : inTok + cacheRead + cacheWrite;

            if (totalInTok == 0 && outTok == 0) return null;

            DateTime ts = DateTime.Now;
            if (DateTime.TryParse(node?["timestamp"]?.GetValue<string>(), CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var dt))
                ts = dt.ToLocalTime();

            string model = msg?["model"]?.GetValue<string>() ?? "";
            if (string.IsNullOrEmpty(model)) model = currentModel;
            string provider = msg?["provider"]?.GetValue<string>() ?? "";
            if (string.IsNullOrEmpty(provider)) provider = currentProvider;

            long piDuration = 0;
            if (lastUserTs != DateTime.MinValue && ts > lastUserTs)
                piDuration = (long)(ts - lastUserTs).TotalMilliseconds;

            string msgId = node?["id"]?.GetValue<string>() ?? "";
            string sid = !string.IsNullOrEmpty(msgId) ? "pi:" + msgId : "";

            return new UsageStorage.UsageRecord(
                UsageStorage.AgentPi, provider, "Pi", model,
                totalInTok, outTok, ts,
                CacheReadTokens: cacheRead,
                CacheWriteTokens: cacheWrite,
                DurationMs: piDuration,
                TtftMs: piDuration,
                SourceId: sid);
        }
        catch { return null; }
    }

    // ---------- OpenCode ----------

    static int ScanOpenCode()
    {
        var dbPath = Path.Combine(ClaudeCli.HomeDir, ".local", "share", "opencode", "opencode.db");
        if (!File.Exists(dbPath)) return 0;

        const string stateKey = "opencode.db";
        long fromMs = _offsets.TryGetValue(stateKey, out var w) ? w : 0;
        int totalAdded = 0;

        try
        {
            using var conn = new Microsoft.Data.Sqlite.SqliteConnection(
                $"Data Source={dbPath};Mode=ReadOnly;Cache=Private");
            conn.Open();

            // 循环分页拉取全部历史与新增消息，避免单次 LIMIT 5000 截断遗漏
            while (true)
            {
                var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT id, data, time_created FROM message WHERE time_created > $from ORDER BY time_created ASC LIMIT 5000;";
                cmd.Parameters.AddWithValue("$from", fromMs);

                var batch = new List<UsageStorage.UsageRecord>();
                long maxMs = fromMs;
                int rowCount = 0;

                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read())
                    {
                        rowCount++;
                        var id = r.IsDBNull(0) ? "" : r.GetString(0);
                        var data = r.GetString(1);
                        var ms = r.GetInt64(2);
                        if (ms > maxMs) maxMs = ms;
                        var rec = ParseOpenCodeData(id, data, ms);
                        if (rec != null) batch.Add(rec);
                    }
                }

                if (batch.Count > 0)
                {
                    if (!UsageStorage.RecordBatch(batch)) break;
                    totalAdded += batch.Count;
                }

                if (maxMs > fromMs)
                {
                    fromMs = maxMs;
                    _offsets[stateKey] = maxMs;
                }

                if (rowCount < 5000 || maxMs <= 0) break;
            }
        }
        catch { }
        return totalAdded;
    }

    static UsageStorage.UsageRecord? ParseOpenCodeData(string msgId, string data, long ms)
    {
        try
        {
            var node = JsonNode.Parse(data);
            if (node?["role"]?.GetValue<string>() != "assistant") return null;

            var tokens = node["tokens"];
            if (tokens == null) return null;

            long rawIn = tokens["input"]?.GetValue<long>() ?? 0;
            long rawOut = tokens["output"]?.GetValue<long>() ?? 0;
            long reasoning = tokens["reasoning"]?.GetValue<long>() ?? 0;
            long cacheRead = tokens["cache"]?["read"]?.GetValue<long>() ?? 0;
            long cacheWrite = tokens["cache"]?["write"]?.GetValue<long>() ?? 0;
            long total = tokens["total"]?.GetValue<long>() ?? 0;

            // OpenCode 的 tokens.input 仅为未命中缓存部分，cache.read/write 与 reasoning 均独立计入 total
            long totalIn = rawIn + cacheRead + cacheWrite;
            long totalOut = (total > 0 && total == rawIn + cacheRead + cacheWrite + rawOut)
                ? rawOut
                : (rawOut + reasoning);

            if (totalIn == 0 && totalOut == 0) return null;

            string model = node["modelID"]?.GetValue<string>() ?? "";
            string provider = node["providerID"]?.GetValue<string>() ?? "";
            var ts = DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime;

            long ocDuration = 0;
            var created = node["time"]?["created"]?.GetValue<long>() ?? 0;
            var completed = node["time"]?["completed"]?.GetValue<long>() ?? 0;
            if (created > 0 && completed > created) ocDuration = completed - created;

            string sid = !string.IsNullOrEmpty(msgId) ? "opencode:" + msgId : "";

            return new UsageStorage.UsageRecord(
                UsageStorage.AgentOpenCode, provider, "OpenCode", model,
                totalIn, totalOut, ts,
                CacheReadTokens: cacheRead,
                CacheWriteTokens: cacheWrite,
                DurationMs: ocDuration,
                TtftMs: ocDuration,
                SourceId: sid);
        }
        catch { return null; }
    }

    // ---------- 公共 ----------

    static UsageStorage.UsageRecord MergeUsageRecord(UsageStorage.UsageRecord a, UsageStorage.UsageRecord b) => a with
    {
        InTokens = Math.Max(a.InTokens, b.InTokens),
        OutTokens = Math.Max(a.OutTokens, b.OutTokens),
        CacheReadTokens = Math.Max(a.CacheReadTokens, b.CacheReadTokens),
        CacheWriteTokens = Math.Max(a.CacheWriteTokens, b.CacheWriteTokens),
        DurationMs = Math.Max(a.DurationMs, b.DurationMs),
        TtftMs = a.TtftMs > 0 && b.TtftMs > 0 ? Math.Min(a.TtftMs, b.TtftMs) : Math.Max(a.TtftMs, b.TtftMs),
        Model = !string.IsNullOrEmpty(b.Model) ? b.Model : a.Model,
        Ts = b.Ts > a.Ts ? b.Ts : a.Ts,
    };

    static void CollectRecentFiles(string root, string pattern, int maxAgeDays, List<string> target)
    {
        var cutoff = DateTime.Now.AddDays(-maxAgeDays);
        try
        {
            foreach (var f in Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories))
            {
                try
                {
                    if (File.GetLastWriteTime(f) >= cutoff) target.Add(f);
                }
                catch { }
            }
        }
        catch { }
    }

    /// <summary>枚举最近 N 天修改过的 JSONL 文件（按修改时间倒序）。</summary>
    static IEnumerable<string> EnumerateRecentJsonl(string root, int maxAgeDays)
    {
        var list = new List<string>();
        CollectRecentFiles(root, "*.jsonl", maxAgeDays, list);
        list.Sort((a, b) =>
        {
            try { return File.GetLastWriteTime(b).CompareTo(File.GetLastWriteTime(a)); }
            catch { return 0; }
        });
        return list;
    }

    /// <summary>重置扫描状态（用于调试或全量重扫）。</summary>
    public static void Reset()
    {
        lock (_scanLock)
        {
            _offsets = new(StringComparer.OrdinalIgnoreCase);
            _fileModels = new(StringComparer.OrdinalIgnoreCase);
            _fileProviders = new(StringComparer.OrdinalIgnoreCase);
            _codexTurns.Clear();
            _scannerVersion = CurrentScannerVersion;
            SaveState();
        }
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace APISwitch.Services;

/// <summary>
/// 本地会话用量扫描器：读取各客户端写在磁盘上的会话记录（JSONL），
/// 解析其中的 token 用量并入库。应用未运行时客户端产生的用量也能被统计到。
///
/// 支持：
/// - Codex：~/.codex/sessions/**/*.jsonl（token_usage_record 事件）
/// - Claude Code：~/.claude/projects/**/*.jsonl（assistant 消息 message.usage）
///
/// 增量扫描：记录每个文件已处理到的字节偏移（scan_state.json），避免重复计数。
/// </summary>
public static class UsageScanner
{
    static readonly string StateFile = Path.Combine(AgPaths.AppData, "APISwitch", "usage_scan_state.json");

    // 每个文件已处理到的字节偏移
    static Dictionary<string, long> _offsets = new(StringComparer.OrdinalIgnoreCase);

    // 每个文件扫描结束时正在使用的模型/供应商（增量扫描恢复用，避免丢失导致记录为空）
    static Dictionary<string, string> _fileModels = new(StringComparer.OrdinalIgnoreCase);
    static Dictionary<string, string> _fileProviders = new(StringComparer.OrdinalIgnoreCase);

    static UsageScanner()
    {
        LoadState();
    }

    static void LoadState()
    {
        try
        {
            if (File.Exists(StateFile))
            {
                var json = File.ReadAllText(StateFile);
                var obj = JsonNode.Parse(json)?.AsObject();
                if (obj != null)
                {
                    if (obj["offsets"] is JsonObject offs)
                    {
                        // 新格式：{ offsets: {...}, models: {...}, providers: {...} }
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
                        // 兼容旧格式：扁平 file -> offset
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
                ["offsets"] = offs,
                ["models"] = mods,
                ["providers"] = provs,
            };
            var dir = Path.GetDirectoryName(StateFile);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(StateFile, obj.ToJsonString());
        }
        catch { }
    }

    // ---------- Codex ----------

    static int ScanCodex()
    {
        var root = Path.Combine(ClaudeCli.HomeDir, ".codex", "sessions");
        if (!Directory.Exists(root)) return 0;

        int added = 0;
        foreach (var file in EnumerateRecentJsonl(root, maxAgeDays: 400))
        {
            added += ScanCodexFile(file);
        }
        return added;
    }

    // Codex 轮次计时：turn_id -> (轮次开始时间, 首个 AgentMessage 时间)
    static readonly Dictionary<string, (DateTime Start, DateTime FirstAgent)> _codexTurns = new(StringComparer.Ordinal);

    // 上面这些静态 Dictionary 都不是线程安全的。ScanAll() 会被主窗口和用量监控窗口
    // 各自 Task.Run 触发，两者完全可能并发跑；并发写 Dictionary 会破坏内部结构，
    // 表现为丢条目、IndexOutOfRange，严重时桶链表成环导致遍历卡死。
    // 扫描本身是增量幂等的，用一把锁串行化即可，无副作用。
    static readonly object _scanLock = new();

    /// <summary>
    /// 适配器注册表：新增 CLI agent 只需实现 IUsageSource 并在此追加一行，
    /// 不需要改 ScanAll 的调度逻辑。适配器实例无状态（水位在上下文里），可安全复用。
    /// </summary>
    static readonly IUsageSource[] _sources = new IUsageSource[]
    {
        new StepCodeUsageSource(),
        new ZCodeUsageSource(),
        new DshUsageSource(),
        new GeminiCliUsageSource(),
    };

    /// <summary>执行一次全量增量扫描。返回本次新增的记录条数。</summary>
    public static int ScanAll()
    {
        lock (_scanLock)
        {
            int added = 0;
            added += ScanCodex();
            added += ScanClaude();
            added += ScanPi();
            added += ScanOpenCode();

            // 适配器机制：上下文每次新建，保证拿到当期偏移字典。
            // BackfillAll()/Reset() 会整体替换 _offsets，若在静态构造时捕获引用会变成 stale 字典。
            var ctx = new UsageScanContext(_offsets, _scanLock);
            foreach (var src in _sources)
                added += src.Scan(ctx);

            PruneCodexTurns();
            SaveState();
            return added;
        }
    }

    /// <summary>回填结果。</summary>
    public record BackfillResult(int Written, string? BackupPath);

    /// <summary>
    /// 全量重建用量事件表：备份 → 清空 → 重扫全部会话日志。
    ///
    /// 为什么需要：缓存命中 / 整轮耗时 / 首字这三个指标是后加的，而扫描是增量式的
    /// （按文件字节偏移推进，已消费的部分不会重读）。老用户的 usage.db 里这些字段
    /// 会永远是 0——哪怕换成新版本也一样，因为根本没有新行产生。
    /// 只有回填一次才能把历史数据补齐。
    ///
    /// 安全性：先备份 usage.db（带时间戳）再清空，出事可手动还原。
    /// 重建内容完全来自各客户端本地会话日志（Codex / Claude Code / OpenCode / Pi），
    /// 不依赖网关是否在运行，也不依赖应用此前是否启动过。
    /// </summary>
    public static BackfillResult BackfillAll()
    {
        lock (_scanLock)
        {
            var backup = UsageStorage.BackupDatabase();
            UsageStorage.ClearEvents();

            // 偏移全部作废，让下面这次扫描从每个文件的第一行重新读
            _offsets = new(StringComparer.OrdinalIgnoreCase);
            _fileModels = new(StringComparer.OrdinalIgnoreCase);
            _fileProviders = new(StringComparer.OrdinalIgnoreCase);
            _codexTurns.Clear();

            var written = ScanAll();
            return new BackfillResult(written, backup);
        }
    }

    /// <summary>
    /// 清理不可能再被使用的 Codex 轮次计时条目。
    /// 正常情况下条目在解析到 token_usage_record 时就移除，但轮次被用户中止、
    /// 上游报错、或增量偏移恰好跳过时不会移除，字典会无限增长。
    /// 正常轮次几秒内就出 token 记录，留 2 小时阈值足够保守，不会误删待处理轮次。
    /// 另外加一道硬上限兜底，防止时间戳异常导致的大量残留。
    /// </summary>
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
            // 兜底：按开始时间丢最旧的，直到回到上限
            foreach (var kv in _codexTurns.OrderBy(kv => kv.Value.Start).Take(_codexTurns.Count - 5000))
                _codexTurns.Remove(kv.Key);
        }
    }

    static int ScanCodexFile(string file)
    {
        long start = _offsets.TryGetValue(file, out var off) ? off : 0;
        var info = new FileInfo(file);
        if (info.Length <= start) return 0;

        int added = 0;
        try
        {
            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (start > 0) fs.Seek(start, SeekOrigin.Begin);
            using var reader = new StreamReader(fs);

            // 顺序扫描：track 会话内当前模型（thread_settings_applied 可随时切换）。
            // 增量扫描从上次偏移处继续时，恢复上次结束时的模型/供应商，避免 model_change 在偏移之前导致记录为空
            string currentModel = _fileModels.TryGetValue(file, out var m0) ? m0 : "";
            string currentProvider = _fileProviders.TryGetValue(file, out var p0) ? p0 : "";

            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                // 会话设置变更：更新当前模型/供应商
                if (line.Contains("thread_settings_applied", StringComparison.Ordinal))
                {
                    try
                    {
                        var sNode = JsonNode.Parse(line);
                        var settings = sNode?["payload"]?["thread_settings"];
                        if (settings?["model"]?.GetValue<string>() is { } m && !string.IsNullOrEmpty(m))
                            currentModel = m;
                        if (settings?["model_provider_id"]?.GetValue<string>() is { } p && !string.IsNullOrEmpty(p))
                            currentProvider = p;
                    }
                    catch { }
                    continue;
                }

                // 轮次开始：记录 turn 起点时间，用于后续算耗时/首字
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

                // 首个 AgentMessage 完成：作为首字的近似点（Codex 不落流式增量）
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
                                    // 没看到 task_started（可能被增量偏移跳过）：以本条为轮次起点
                                    _codexTurns[turnId] = (local, local);
                                }
                            }
                        }
                    }
                    catch { }
                    continue;
                }

                added += ParseCodexLine(line, currentModel, currentProvider);
            }

            _offsets[file] = fs.Position;
            _fileModels[file] = currentModel;
            _fileProviders[file] = currentProvider;
        }
        catch { }
        return added;
    }

    static int ParseCodexLine(string line, string currentModel, string currentProvider)
    {
        try
        {
            var node = JsonNode.Parse(line);
            if (node?["type"]?.GetValue<string>() != "token_usage_record") return 0;

            // Codex 的 usage 在 payload 内
            var payload = node["payload"];
            var usage = payload?["usage"];
            if (usage == null) return 0;

            long inTok = usage["input_tokens"]?.GetValue<long>() ?? 0;
            long outTok = usage["output_tokens"]?.GetValue<long>() ?? 0;
            long cacheRead = usage["cached_input_tokens"]?.GetValue<long>() ?? 0;
            long cacheWrite = usage["cache_write_input_tokens"]?.GetValue<long>() ?? 0;
            if (inTok == 0 && outTok == 0) return 0;

            // 时间戳：行级 timestamp（ISO8601 Z）
            DateTime ts = DateTime.Now;
            if (DateTime.TryParse(node?["timestamp"]?.GetValue<string>(),
                    CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var dt))
                ts = dt.ToLocalTime();

            // 耗时 = 轮次内最后一条记录时间 - 轮次开始；首字 = 首个 AgentMessage - 轮次开始。
            // 两者都由消息级时间戳推算，Codex 不落流式增量，属近似值。
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

            UsageStorage.RecordUsage(UsageStorage.AgentCodex, currentProvider, "Codex", currentModel,
                inTok, outTok, ts, cacheRead, cacheWrite, durationMs, ttftMs);
            return 1;
        }
        catch { return 0; }
    }
    // ---------- Claude Code ----------

    static int ScanClaude()
    {
        var root = Path.Combine(ClaudeCli.HomeDir, ".claude", "projects");
        if (!Directory.Exists(root)) return 0;

        int added = 0;
        foreach (var file in EnumerateRecentJsonl(root, maxAgeDays: 400))
        {
            added += ScanClaudeFile(file);
        }
        return added;
    }

    static int ScanClaudeFile(string file)
    {
        long start = _offsets.TryGetValue(file, out var off) ? off : 0;
        var info = new FileInfo(file);
        if (info.Length <= start) return 0;

        int added = 0;
        try
        {
            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (start > 0) fs.Seek(start, SeekOrigin.Begin);
            using var reader = new StreamReader(fs);

            // 计时起点是"本文件内上一条 user 消息"。每个文件都要重置，
            // 否则上一个会话残留的时间戳会污染本文件第一条 assistant 消息，
            // 算出一个跨会话的荒谬耗时（可能几小时甚至几天）。
            _claudeLastUserTs = DateTime.MinValue;

            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                added += ParseClaudeLine(line);
            }

            _offsets[file] = fs.Position;
        }
        catch { }
        return added;
    }

    // 每个 Claude 会话文件内，上一条 user 消息的时间戳（用于推算响应耗时）
    static DateTime _claudeLastUserTs = DateTime.MinValue;

    static int ParseClaudeLine(string line)
    {
        try
        {
            var node = JsonNode.Parse(line);
            var type = node?["type"]?.GetValue<string>();
            if (type == "user")
            {
                // 记录用户消息时间，作为下一条 assistant 消息的计时起点
                if (DateTime.TryParse(node?["timestamp"]?.GetValue<string>(),
                        CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var ut))
                    _claudeLastUserTs = ut.ToLocalTime();
                return 0;
            }
            if (type != "assistant") return 0;

            var usage = node?["message"]?["usage"];
            if (usage == null) return 0;

            long inTok = usage["input_tokens"]?.GetValue<long>() ?? 0;
            long outTok = usage["output_tokens"]?.GetValue<long>() ?? 0;
            long cacheRead = usage["cache_read_input_tokens"]?.GetValue<long>() ?? 0;
            long cacheWrite = usage["cache_creation_input_tokens"]?.GetValue<long>() ?? 0;
            if (inTok == 0 && outTok == 0) return 0;

            string model = node?["message"]?["model"]?.GetValue<string>() ?? "";

            DateTime ts = DateTime.Now;
            if (DateTime.TryParse(node?["timestamp"]?.GetValue<string>(),
                    CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var dt))
                ts = dt.ToLocalTime();

            // 响应耗时 = assistant 消息时间 - 上一条 user 消息时间。
            // 注意：Claude 日志不记录流式首包，所以这是"整轮响应耗时"，
            // 作为首字的保守上界参考，UI 中会标注为估算值。
            long durationMs = 0;
            long ttftMs = 0;
            if (_claudeLastUserTs != DateTime.MinValue && ts > _claudeLastUserTs)
            {
                durationMs = (long)(ts - _claudeLastUserTs).TotalMilliseconds;
                ttftMs = durationMs;
            }

            UsageStorage.RecordUsage(UsageStorage.AgentClaudeCli, "", "Claude Code", model,
                inTok, outTok, ts, cacheRead, cacheWrite, durationMs, ttftMs);
            return 1;
        }
        catch { return 0; }
    }

    // ---------- Pi ----------

    static int ScanPi()
    {
        var root = Path.Combine(ClaudeCli.HomeDir, ".pi", "agent", "sessions");
        if (!Directory.Exists(root)) return 0;

        int added = 0;
        foreach (var file in EnumerateRecentJsonl(root, maxAgeDays: 400))
        {
            added += ScanPiFile(file);
        }
        return added;
    }

    static int ScanPiFile(string file)
    {
        long start = _offsets.TryGetValue(file, out var off) ? off : 0;
        var info = new FileInfo(file);
        if (info.Length <= start) return 0;

        int added = 0;
        try
        {
            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (start > 0) fs.Seek(start, SeekOrigin.Begin);
            using var reader = new StreamReader(fs);

            // 增量扫描从上次偏移处继续时，恢复上次结束时的模型/供应商（model_change 可能已在偏移之前）
            string currentModel = _fileModels.TryGetValue(file, out var m0) ? m0 : "";
            string currentProvider = _fileProviders.TryGetValue(file, out var p0) ? p0 : "";
            _piLastUserTs = DateTime.MinValue;

            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                // user 消息：记录时间作为下一条 assistant 的计时起点
                if (line.Contains("\"role\":\"user\"", StringComparison.Ordinal) ||
                    line.Contains("\"role\": \"user\"", StringComparison.Ordinal))
                {
                    try
                    {
                        var un = JsonNode.Parse(line);
                        if (DateTime.TryParse(un?["timestamp"]?.GetValue<string>(), CultureInfo.InvariantCulture,
                                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var ut))
                            _piLastUserTs = ut.ToLocalTime();
                    }
                    catch { }
                    continue;
                }

                // model_change 事件：更新当前模型/供应商
                if (line.Contains("\"model_change\"", StringComparison.Ordinal))
                {
                    try
                    {
                        var mc = JsonNode.Parse(line);
                        if (mc?["modelId"]?.GetValue<string>() is { } m && !string.IsNullOrEmpty(m)) currentModel = m;
                        if (mc?["provider"]?.GetValue<string>() is { } p && !string.IsNullOrEmpty(p)) currentProvider = p;
                    }
                    catch { }
                    continue;
                }

                added += ParsePiLine(line, currentModel, currentProvider);
            }

            _offsets[file] = fs.Position;
            _fileModels[file] = currentModel;
            _fileProviders[file] = currentProvider;
        }
        catch { }
        return added;
    }

    // 每个 Pi 会话文件内，上一条 user 消息的时间戳（用于推算响应耗时）
    static DateTime _piLastUserTs = DateTime.MinValue;

    static int ParsePiLine(string line, string currentModel, string currentProvider)
    {
        try
        {
            var node = JsonNode.Parse(line);
            if (node?["type"]?.GetValue<string>() != "message") return 0;
            if (node["message"]?["role"]?.GetValue<string>() != "assistant") return 0;

            var usage = node?["message"]?["usage"];
            if (usage == null) return 0;

            // Pi: usage { input, output, reasoning, totalTokens, cost, cacheRead, cacheWrite }
            long inTok = usage["input"]?.GetValue<long>() ?? 0;
            long outTok = usage["output"]?.GetValue<long>() ?? 0;
            long cacheRead = usage["cacheRead"]?.GetValue<long>() ?? 0;
            long cacheWrite = usage["cacheWrite"]?.GetValue<long>() ?? 0;
            if (inTok == 0 && outTok == 0) return 0;

            DateTime ts = DateTime.Now;
            if (DateTime.TryParse(node?["timestamp"]?.GetValue<string>(), CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var dt))
                ts = dt.ToLocalTime();

            // 每条 assistant 消息自带 provider/model 字段（实测存在率 100%），优先使用；
            // 缺失时兜底用会话内 model_change 跟踪值
            var msg = node?["message"];
            string model = msg?["model"]?.GetValue<string>() ?? "";
            if (string.IsNullOrEmpty(model)) model = currentModel;
            string provider = msg?["provider"]?.GetValue<string>() ?? "";
            if (string.IsNullOrEmpty(provider)) provider = currentProvider;

            // 响应耗时：assistant 消息时间 - 上一条 user 消息时间（近似首字）
            long piDuration = 0;
            if (_piLastUserTs != DateTime.MinValue && ts > _piLastUserTs)
                piDuration = (long)(ts - _piLastUserTs).TotalMilliseconds;

            UsageStorage.RecordUsage(UsageStorage.AgentPi, provider, "Pi", model,
                inTok, outTok, ts, cacheRead, cacheWrite, piDuration, piDuration);
            return 1;
        }
        catch { return 0; }
    }

    // ---------- OpenCode ----------

    static int ScanOpenCode()
    {
        var dbPath = Path.Combine(ClaudeCli.HomeDir, ".local", "share", "opencode", "opencode.db");
        if (!File.Exists(dbPath)) return 0;

        // 增量水位：记录已处理到的 message.id 时间戳（毫秒）
        var stateKey = "opencode.db";
        long fromMs = _offsets.TryGetValue(stateKey, out var w) ? w : 0;

        int added = 0;
        long maxMs = fromMs;
        try
        {
            using var conn = new Microsoft.Data.Sqlite.SqliteConnection(
                $"Data Source={dbPath};Mode=ReadOnly;Cache=Private");
            conn.Open();
            var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT data, time_created FROM message WHERE time_created > $from ORDER BY time_created ASC LIMIT 5000;";
            cmd.Parameters.AddWithValue("$from", fromMs);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var data = r.GetString(0);
                var ms = r.GetInt64(1);
                if (ms > maxMs) maxMs = ms;
                if (ParseOpenCodeData(data, ms)) added++;
            }
            if (maxMs > fromMs) _offsets[stateKey] = maxMs;
        }
        catch { }
        return added;
    }

    static bool ParseOpenCodeData(string data, long ms)
    {
        try
        {
            var node = JsonNode.Parse(data);
            if (node?["role"]?.GetValue<string>() != "assistant") return false;

            var tokens = node["tokens"];
            if (tokens == null) return false;

            long inTok = tokens["input"]?.GetValue<long>() ?? 0;
            long outTok = tokens["output"]?.GetValue<long>() ?? 0;
            long cacheRead = tokens["cache"]?["read"]?.GetValue<long>() ?? 0;
            long cacheWrite = tokens["cache"]?["write"]?.GetValue<long>() ?? 0;
            if (inTok == 0 && outTok == 0) return false;

            string model = node["modelID"]?.GetValue<string>() ?? "";
            string provider = node["providerID"]?.GetValue<string>() ?? "";

            var ts = DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime;

            // OpenCode 记录了 time.created / time.completed（毫秒时间戳），
            // 这是唯一能算出"精确整轮耗时"的客户端（无需近似）。
            long ocDuration = 0;
            var created = node["time"]?["created"]?.GetValue<long>() ?? 0;
            var completed = node["time"]?["completed"]?.GetValue<long>() ?? 0;
            if (created > 0 && completed > created) ocDuration = completed - created;

            UsageStorage.RecordUsage(UsageStorage.AgentOpenCode, provider, "OpenCode", model,
                inTok, outTok, ts, cacheRead, cacheWrite, ocDuration, ocDuration);
            return true;
        }
        catch { return false; }
    }

    // ---------- 公共 ----------

    /// <summary>枚举最近 N 天修改过的 JSONL 文件（按修改时间倒序）。</summary>
    static IEnumerable<string> EnumerateRecentJsonl(string root, int maxAgeDays)
    {
        var cutoff = DateTime.Now.AddDays(-maxAgeDays);
        var list = new List<string>();
        try
        {
            foreach (var f in Directory.EnumerateFiles(root, "*.jsonl", SearchOption.AllDirectories))
            {
                try
                {
                    if (File.GetLastWriteTime(f) >= cutoff) list.Add(f);
                }
                catch { }
            }
        }
        catch { }
        list.Sort((a, b) => File.GetLastWriteTime(b).CompareTo(File.GetLastWriteTime(a)));
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
            SaveState();
        }
    }
}

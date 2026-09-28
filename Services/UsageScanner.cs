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

    /// <summary>执行一次全量增量扫描。返回本次新增的记录条数。</summary>
    public static int ScanAll()
    {
        int added = 0;
        added += ScanCodex();
        added += ScanClaude();
        added += ScanPi();
        added += ScanOpenCode();
        SaveState();
        return added;
    }

    // ---------- Codex ----------

    static int ScanCodex()
    {
        var root = Path.Combine(ClaudeCli.HomeDir, ".codex", "sessions");
        if (!Directory.Exists(root)) return 0;

        int added = 0;
        foreach (var file in EnumerateRecentJsonl(root, maxAgeDays: 90))
        {
            added += ScanCodexFile(file);
        }
        return added;
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
            if (inTok == 0 && outTok == 0) return 0;

            // 时间戳：行级 timestamp（ISO8601 Z）
            DateTime ts = DateTime.Now;
            if (DateTime.TryParse(node["timestamp"]?.GetValue<string>(),
                    CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var dt))
                ts = dt.ToLocalTime();

            UsageStorage.RecordUsage(UsageStorage.AgentCodex, currentProvider, "Codex", currentModel, inTok, outTok, ts);
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
        foreach (var file in EnumerateRecentJsonl(root, maxAgeDays: 90))
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

    static int ParseClaudeLine(string line)
    {
        try
        {
            var node = JsonNode.Parse(line);
            if (node?["type"]?.GetValue<string>() != "assistant") return 0;

            var usage = node["message"]?["usage"];
            if (usage == null) return 0;

            long inTok = usage["input_tokens"]?.GetValue<long>() ?? 0;
            long outTok = usage["output_tokens"]?.GetValue<long>() ?? 0;
            if (inTok == 0 && outTok == 0) return 0;

            string model = node["message"]?["model"]?.GetValue<string>() ?? "";

            DateTime ts = DateTime.Now;
            if (DateTime.TryParse(node["timestamp"]?.GetValue<string>(),
                    CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var dt))
                ts = dt.ToLocalTime();

            UsageStorage.RecordUsage(UsageStorage.AgentClaudeCli, "", "Claude Code", model, inTok, outTok, ts);
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
        foreach (var file in EnumerateRecentJsonl(root, maxAgeDays: 90))
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

            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

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

    static int ParsePiLine(string line, string currentModel, string currentProvider)
    {
        try
        {
            var node = JsonNode.Parse(line);
            if (node?["type"]?.GetValue<string>() != "message") return 0;
            if (node["message"]?["role"]?.GetValue<string>() != "assistant") return 0;

            var usage = node["message"]?["usage"];
            if (usage == null) return 0;

            // Pi: usage { input, output, reasoning, totalTokens, cost }
            long inTok = usage["input"]?.GetValue<long>() ?? 0;
            long outTok = usage["output"]?.GetValue<long>() ?? 0;
            if (inTok == 0 && outTok == 0) return 0;

            DateTime ts = DateTime.Now;
            if (DateTime.TryParse(node["timestamp"]?.GetValue<string>(), CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var dt))
                ts = dt.ToLocalTime();

            // 每条 assistant 消息自带 provider/model 字段（实测存在率 100%），优先使用；
            // 缺失时兜底用会话内 model_change 跟踪值
            var msg = node["message"];
            string model = msg?["model"]?.GetValue<string>() ?? "";
            if (string.IsNullOrEmpty(model)) model = currentModel;
            string provider = msg?["provider"]?.GetValue<string>() ?? "";
            if (string.IsNullOrEmpty(provider)) provider = currentProvider;

            UsageStorage.RecordUsage(UsageStorage.AgentPi, provider, "Pi", model, inTok, outTok, ts);
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
            if (inTok == 0 && outTok == 0) return false;

            string model = node["modelID"]?.GetValue<string>() ?? "";
            string provider = node["providerID"]?.GetValue<string>() ?? "";

            var ts = DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime;
            UsageStorage.RecordUsage(UsageStorage.AgentOpenCode, provider, "OpenCode", model, inTok, outTok, ts);
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
        _offsets = new(StringComparer.OrdinalIgnoreCase);
        SaveState();
    }
}

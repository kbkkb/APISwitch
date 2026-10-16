using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json.Nodes;

namespace APISwitch.Services;

/// <summary>
/// Gemini CLI (@google/gemini-cli) 会话扫描适配器。
///
/// 数据位置：~/.gemini/tmp/&lt;project_hash&gt;/chats/session-*.json 和 session-*.jsonl
/// 格式：
///   - .jsonl：实时会话行，包含 type == "gemini" 且含 tokens 对象的消息。
///   - .json：历史会话全量 JSON，messages 数组包含 type == "gemini" 消息。
/// tokens 结构：
///   { "input": 12973, "output": 172, "cached": 0, "thoughts": 419, "total": 13564 }
///   - input 为包含缓存读取的总输入 Prompt Tokens
///   - cached 为命中缓存的 Token (cache_read_tokens)
///   - output 为常规生成 Token
///   - thoughts 为思考/推理 Token (总输出 = output + thoughts，严格对齐官方计费 total)
///
/// 增量与批量策略：
///   - 单文件按单事务批量写入 (RecordBatch)，大幅提升海量历史消息的解析与落库吞吐
///   - .jsonl 文件按字节偏移推进 (GetFileOffset / SetFileOffset)
///   - .json 文件按文件路径标记已扫 (gemini:json:&lt;filePath&gt;)
/// </summary>
public sealed class GeminiCliUsageSource : UsageSourceBase
{
    public override string AgentId => UsageStorage.AgentGeminiCli;

    protected override int ScanCore()
    {
        var tmpRoot = Path.Combine(ClaudeCli.HomeDir, ".gemini", "tmp");
        if (!Directory.Exists(tmpRoot)) return 0; // 用户未安装或未运行 Gemini CLI

        int added = 0;
        foreach (var file in EnumerateChatFiles(tmpRoot, maxAgeDays: 365))
        {
            if (file.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase))
                added += ScanJsonlFile(file);
            else if (file.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                added += ScanJsonFile(file);
        }
        return added;
    }

    int ScanJsonlFile(string file)
    {
        long start = GetFileOffset(file);
        var info = new FileInfo(file);
        if (info.Length <= start) return 0;

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
                // 快速过滤：非 gemini 类型的行直接跳过，避免无谓的 JsonNode 解析
                if (!line.Contains("\"gemini\"", StringComparison.Ordinal) ||
                    !line.Contains("\"tokens\"", StringComparison.Ordinal))
                    continue;

                var r = ParseGeminiLine(line);
                if (r != null) batch.Add(r);
            }

            if (batch.Count > 0) RecordBatch(batch);
            SetFileOffset(file, fs.Position);
        }
        catch { }
        return batch.Count;
    }

    int ScanJsonFile(string file)
    {
        var wmKey = "gemini:json:" + file;
        if (GetWatermark(wmKey) == 1) return 0; // 该文件已完整扫描

        var batch = new List<UsageStorage.UsageRecord>();
        try
        {
            var text = File.ReadAllText(file);
            var root = JsonNode.Parse(text);
            var messages = root?["messages"] as JsonArray;
            if (messages != null)
            {
                foreach (var msgNode in messages)
                {
                    if (msgNode == null) continue;
                    var r = ParseGeminiJsonNode(msgNode);
                    if (r != null) batch.Add(r);
                }
            }

            if (batch.Count > 0) RecordBatch(batch);
            SetWatermark(wmKey, 1);
        }
        catch { }
        return batch.Count;
    }

    UsageStorage.UsageRecord? ParseGeminiLine(string line)
    {
        try
        {
            var node = JsonNode.Parse(line);
            return ParseGeminiJsonNode(node);
        }
        catch { return null; }
    }

    UsageStorage.UsageRecord? ParseGeminiJsonNode(JsonNode? node)
    {
        if (node == null) return null;
        var type = node["type"]?.GetValue<string>();
        if (type != "gemini") return null;

        var tokens = node["tokens"];
        if (tokens == null) return null;

        long inTok = tokens["input"]?.GetValue<long>() ?? 0;
        long outTok = tokens["output"]?.GetValue<long>() ?? 0;
        long thoughts = tokens["thoughts"]?.GetValue<long>() ?? 0;
        long cacheRead = tokens["cached"]?.GetValue<long>() ?? 0;
        long totalOut = outTok + thoughts;

        if (inTok == 0 && totalOut == 0) return null;

        string model = node["model"]?.GetValue<string>() ?? "";
        if (string.IsNullOrEmpty(model)) model = "gemini";

        DateTime ts = DateTime.Now;
        if (DateTime.TryParse(node["timestamp"]?.GetValue<string>(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var dt))
            ts = dt.ToLocalTime();

        return new UsageStorage.UsageRecord(
            AgentId, "", "Google", model,
            inTok, totalOut, ts,
            CacheReadTokens: cacheRead);
    }

    static IEnumerable<string> EnumerateChatFiles(string root, int maxAgeDays)
    {
        var cutoff = DateTime.Now.AddDays(-maxAgeDays);
        var list = new List<string>();
        try
        {
            foreach (var projDir in Directory.EnumerateDirectories(root))
            {
                var chatsDir = Path.Combine(projDir, "chats");
                if (!Directory.Exists(chatsDir)) continue;

                foreach (var f in Directory.EnumerateFiles(chatsDir, "session-*.*"))
                {
                    if (!f.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase) &&
                        !f.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                        continue;

                    try
                    {
                        if (File.GetLastWriteTime(f) >= cutoff) list.Add(f);
                    }
                    catch { }
                }
            }
        }
        catch { }

        list.Sort((a, b) =>
        {
            try { return File.GetLastWriteTime(b).CompareTo(File.GetLastWriteTime(a)); }
            catch { return 0; }
        });
        return list;
    }
}

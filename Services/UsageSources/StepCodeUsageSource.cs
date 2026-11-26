using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace APISwitch.Services;

/// <summary>
/// StepCode 会话扫描适配器。
///
/// 数据位置：~/.stepcode/agent/sessions/&lt;项目名&gt;/&lt;ISO时间戳&gt;_&lt;uuid&gt;.jsonl
/// 格式：JSONL，message 事件的 message.usage 含
///   input / output / cacheRead / cacheWrite / reasoning / totalTokens
/// 模型经 model_change 事件在会话内跟踪。
///
/// 注意：StepCode 只落消息级时间戳，没有流式增量，因此耗时与首字只能近似
/// （本适配器不推导这两项，留 0 由 UI 显示「—」），避免给出不可靠的数字。
/// </summary>
public sealed class StepCodeUsageSource : UsageSourceBase
{
    public override string AgentId => UsageStorage.AgentStepCode;

    protected override int ScanCore()
    {
        var root = Path.Combine(ClaudeCli.HomeDir, ".stepcode", "agent", "sessions");
        if (!Directory.Exists(root)) return 0;   // 用户没装 StepCode：静默跳过

        var files = new List<string>(EnumerateRecentJsonl(root, maxAgeDays: 400));
        bool needRepair = GetWatermark("stepcode:model_fix_v1") == 0;
        if (needRepair)
        {
            foreach (var f in files) SetFileOffset(f, 0);
        }

        int added = 0;
        foreach (var file in files)
            added += ScanFile(file);

        if (needRepair)
            SetWatermark("stepcode:model_fix_v1", 1);

        return added;
    }

    int ScanFile(string file)
    {
        long start = GetFileOffset(file);
        var info = new FileInfo(file);
        if (info.Length <= start) return 0;

        int added = 0;
        var batch = new List<UsageStorage.UsageRecord>();
        try
        {
            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            string? currentModel = GetFileModel(file);

            // 若增量扫描且尚未缓存文件模型，先从文件头预扫最多 50 行提取 model_change / model
            if (start > 0 && string.IsNullOrEmpty(currentModel))
            {
                using (var headReader = new StreamReader(fs, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 4096, leaveOpen: true))
                {
                    for (int i = 0; i < 50; i++)
                    {
                        var hl = headReader.ReadLine();
                        if (hl == null) break;
                        TryExtractModelHint(hl, ref currentModel);
                        if (!string.IsNullOrEmpty(currentModel)) break;
                    }
                }
            }

            if (start > 0) fs.Seek(start, SeekOrigin.Begin);
            using var reader = new StreamReader(fs);

            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var rec = ParseLine(line, ref currentModel);
                if (rec != null) batch.Add(rec);
            }

            if (!string.IsNullOrEmpty(currentModel))
                SetFileModel(file, currentModel);

            if (batch.Count == 0 || RecordBatch(batch))
            {
                SetFileOffset(file, fs.Position);
                added = batch.Count;
            }
        }
        catch
        {
            // 单文件读取失败不影响其他文件；偏移不推进，下次重试
        }
        return added;
    }

    static void TryExtractModelHint(string line, ref string? currentModel)
    {
        try
        {
            var node = JsonNode.Parse(line);
            var m = node?["modelId"]?.GetValue<string>()
                    ?? node?["model"]?.GetValue<string>()
                    ?? node?["message"]?["model"]?.GetValue<string>()
                    ?? node?["message"]?["modelId"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(m)) currentModel = m;
        }
        catch { }
    }

    UsageStorage.UsageRecord? ParseLine(string line, ref string? currentModel)
    {
        try
        {
            var node = JsonNode.Parse(line);
            var type = node?["type"]?.GetValue<string>();

            // 模型切换事件：更新当前模型（兼容 modelId 与 model）
            if (type == "model_change")
            {
                var m = node?["modelId"]?.GetValue<string>() ?? node?["model"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(m)) currentModel = m;
                return null;
            }

            if (type != "message") return null;

            var msg = node?["message"];
            if (msg?["role"]?.GetValue<string>() != "assistant") return null;

            var usage = msg["usage"];
            if (usage == null) return null;

            long inTok = usage["input"]?.GetValue<long>() ?? 0;
            long outTok = usage["output"]?.GetValue<long>() ?? 0;
            long cacheRead = usage["cacheRead"]?.GetValue<long>() ?? 0;
            long cacheWrite = usage["cacheWrite"]?.GetValue<long>() ?? 0;
            long totalTokens = usage["totalTokens"]?.GetValue<long>() ?? 0;

            // StepCode 的 input 仅记录未命中缓存的输入，cacheRead 为命中缓存的输入。
            // 服务商后台（如阶跃星辰 StepFun）对输入 Token 的统计为 Prompt 总量。
            // 若 totalTokens 存在，总输入为 totalTokens - outTok；否则为 inTok + cacheRead + cacheWrite。
            long totalInTok = totalTokens > 0
                ? Math.Max(inTok, totalTokens - outTok)
                : inTok + cacheRead + cacheWrite;

            if (totalInTok == 0 && outTok == 0) return null;

            // 模型优先取消息自带的 model / modelId，缺失时用会话内跟踪值
            string model = msg["model"]?.GetValue<string>()
                           ?? msg["modelId"]?.GetValue<string>()
                           ?? node?["model"]?.GetValue<string>()
                           ?? node?["modelId"]?.GetValue<string>()
                           ?? "";
            if (!string.IsNullOrEmpty(model))
                currentModel = model;
            else
                model = currentModel ?? "";

            DateTime ts = DateTime.Now;
            if (DateTime.TryParse(node?["timestamp"]?.GetValue<string>(),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var dt))
                ts = dt.ToLocalTime();

            string msgId = node?["id"]?.GetValue<string>() ?? msg["id"]?.GetValue<string>() ?? "";
            string sid = !string.IsNullOrEmpty(msgId) ? "stepcode:" + msgId : "";

            return new UsageStorage.UsageRecord(
                AgentId, "", "StepCode", model,
                totalInTok, outTok, ts,
                CacheReadTokens: cacheRead,
                CacheWriteTokens: cacheWrite,
                SourceId: sid);
        }
        catch { return null; }
    }

    /// <summary>枚举最近 N 天内修改过的 jsonl 文件（按修改时间倒序）。</summary>
    static IEnumerable<string> EnumerateRecentJsonl(string root, int maxAgeDays)
    {
        var list = new List<string>();
        try
        {
            var cutoff = DateTime.Now.AddDays(-maxAgeDays);
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
        list.Sort((a, b) =>
        {
            try { return File.GetLastWriteTime(b).CompareTo(File.GetLastWriteTime(a)); }
            catch { return 0; }
        });
        return list;
    }
}

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

        int added = 0;
        foreach (var file in EnumerateRecentJsonl(root, maxAgeDays: 400))
            added += ScanFile(file);
        return added;
    }

    int ScanFile(string file)
    {
        long start = GetFileOffset(file);
        var info = new FileInfo(file);
        if (info.Length <= start) return 0;

        int added = 0;
        try
        {
            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (start > 0) fs.Seek(start, SeekOrigin.Begin);
            using var reader = new StreamReader(fs);

            string? currentModel = "";
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                added += ParseLine(line, ref currentModel);
            }

            SetFileOffset(file, fs.Position);
        }
        catch
        {
            // 单文件读取失败不影响其他文件；偏移不推进，下次重试
        }
        return added;
    }

    // 注意：不能是 static——内部要调用实例方法 Record() 入库
    int ParseLine(string line, ref string? currentModel)
    {
        try
        {
            var node = JsonNode.Parse(line);
            var type = node?["type"]?.GetValue<string>();

            // 模型切换事件：更新当前模型
            if (type == "model_change")
            {
                var m = node?["modelId"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(m)) currentModel = m;
                return 0;
            }

            if (type != "message") return 0;

            var msg = node?["message"];
            if (msg?["role"]?.GetValue<string>() != "assistant") return 0;

            var usage = msg["usage"];
            if (usage == null) return 0;

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

            if (totalInTok == 0 && outTok == 0) return 0;

            // 模型优先取消息自带的 modelId，缺失时用会话内跟踪值
            string model = msg["modelId"]?.GetValue<string>() ?? "";
            if (string.IsNullOrEmpty(model)) model = currentModel ?? "";

            DateTime ts = DateTime.Now;
            if (DateTime.TryParse(node?["timestamp"]?.GetValue<string>(),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var dt))
                ts = dt.ToLocalTime();

            Record(model, totalInTok, outTok, ts, cacheRead, cacheWrite,
                providerName: "StepCode");
            return 1;
        }
        catch { return 0; }
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

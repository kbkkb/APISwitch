using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace APISwitch.Services;

/// <summary>
/// Antigravity 本地会话用量扫描适配器。
///
/// 数据位置：
/// 1) 步级 Token 与时间戳：~/.gemini/antigravity/brain/&lt;conversation-id&gt;/.system_generated/logs/transcript.jsonl
///    注意：同目录下 transcript_full.jsonl 与之同源，仅扫描 transcript.jsonl，杜绝双倍计入。
/// 2) 模型台账映射：~/.gemini/antigravity/pricing_ledger_v1.json（transcript.jsonl 本身不记录 model 字段）
///    以及 ~/.gemini/antigravity/conversation_summaries.db（用于子代理会话回溯父对话 cascadeId）。
/// 3) 速度与首字推算：依据 PLANNER_RESPONSE 与相邻步骤消息时间戳及工具输出文件修改时间推算近似耗时。
///
/// 核验口径：PLANNER_RESPONSE 与 CHECKPOINT 的 input_tokens 为未命中缓存输入、
/// cache_read_tokens 为命中缓存读取，总 Prompt 输入 = input_tokens + cache_read_tokens。
/// </summary>
public sealed class AntigravityUsageSource : UsageSourceBase
{
    public override string AgentId => UsageStorage.AgentAntigravity;
    internal static bool QuickRecentOnly;

    readonly record struct StepStub(
        string Type,
        long StepIndex,
        DateTime TsUtc,
        long OutTokens,
        long UncachedIn,
        long CacheRead,
        string RawModel,
        long ByteOffset,
        bool HasCreatedAt);

    protected override int ScanCore()
    {
        string liveEmail = "";
        if (!AgPaths.IsSandboxed)
        {
            liveEmail = AgHookService.ReadCurrentAntigravityEmailFast();
            if (!string.IsNullOrWhiteSpace(liveEmail))
                UsageStorage.RecordAccountSession(liveEmail);
        }

        var agRoot = Path.Combine(ClaudeCli.HomeDir, ".gemini", "antigravity");
        var brainRoot = Path.Combine(agRoot, "brain");
        if (!Directory.Exists(brainRoot)) return 0;

        var ledger = LedgerLookup.Load(agRoot);
        var convDb = new ConversationDbResolver(agRoot);

        int added = 0;
        var cutoff = QuickRecentOnly ? DateTime.Now.AddHours(-6) : DateTime.Now.AddDays(-400);
        long nowTs = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        long recentDbMinTs = QuickRecentOnly ? (nowTs - 72L * 3600L) : 0L;

        IEnumerable<string> convDirs;
        try { convDirs = Directory.EnumerateDirectories(brainRoot); }
        catch { return 0; }

        foreach (var dir in convDirs)
        {
            var convId = Path.GetFileName(dir);
            if (string.IsNullOrEmpty(convId)) continue;

            var transcriptPath = Path.Combine(dir, ".system_generated", "logs", "transcript.jsonl");
            if (!File.Exists(transcriptPath)) continue;

            try
            {
                if (File.GetLastWriteTime(transcriptPath) < cutoff) continue;
                added += ScanTranscriptFile(convId, dir, transcriptPath, ledger, convDb);
            }
            catch
            {
                // 单会话日志异常不影响其余会话
            }
        }

        // 优先依据 Antigravity 原生 conversations/<cid>.db（含 WAL）补齐所有空模型行；
        // 若 pricing_ledger_v1.json 有更新也一并在同一批事务里兜底补齐（不重复计数 Token/请求数）。
        string wmKey = "ag_ledger_v2:" + ledger.LedgerPath;
        long prevSig = GetWatermark(wmKey);
        bool ledgerChanged = ledger.HasData && ledger.Signature != prevSig;
        var updatedDbWms = new List<(string Key, long Sig)>();

        int enriched = UsageStorage.EnrichEmptyModelsBatch(AgentId, emptyRows =>
        {
            var updates = new Dictionary<long, string>();
            var pendingByConv = new Dictionary<string, List<(long Id, long StepIdx)>>(StringComparer.OrdinalIgnoreCase);

            foreach (var row in emptyRows)
            {
                if (!TryParseSourceId(row.SourceId, out var cid, out long stepIdx)) continue;
                if (stepIdx > 0 && convDb.HasDb(cid))
                {
                    if (!pendingByConv.TryGetValue(cid, out var list))
                        pendingByConv[cid] = list = new List<(long, long)>();
                    list.Add((row.Id, stepIdx));
                }
            }

            foreach (var (cid, items) in pendingByConv)
            {
                long sig = convDb.GetDbAndWalSignature(cid);
                if (sig == 0) continue;
                string cWmKey = "ag_cdb_v1:" + cid;
                if (sig == GetWatermark(cWmKey)) continue;

                var stepMap = convDb.LoadStepsForConv(cid, items.Select(x => x.StepIdx));
                foreach (var (rowId, stepIdx) in items)
                {
                    if (stepMap.TryGetValue(stepIdx, out var m) && !string.IsNullOrEmpty(m))
                        updates[rowId] = m;
                }
                if (!convDb.LastLoadFailed(cid))
                    updatedDbWms.Add((cWmKey, sig));
            }

            if (ledgerChanged)
            {
                foreach (var row in emptyRows)
                {
                    if (updates.ContainsKey(row.Id)) continue;
                    if (!TryParseSourceId(row.SourceId, out var cid, out long stepIdx)) continue;
                    var m = ledger.Resolve(cid, stepIdx, row.UncachedIn, row.CacheRead, row.OutTokens, row.Ts);
                    if (!string.IsNullOrEmpty(m))
                        updates[row.Id] = m;
                }
            }

            return updates;
        }, minTs: recentDbMinTs);

        foreach (var (cWmKey, sig) in updatedDbWms)
            SetWatermark(cWmKey, sig);
        if (ledgerChanged)
            SetWatermark(wmKey, ledger.Signature);
        added += enriched;

        // 历史记录速度与首字一次性就地补齐（后续新扫描步已在 ScanTranscriptFile 内实时计算）
        const long timingVer = 1;
        const string timingWmKey = "ag_timing_ver";
        if (GetWatermark(timingWmKey) != timingVer)
        {
            int tEnriched = BackfillExistingTimings(brainRoot, cutoff);
            SetWatermark(timingWmKey, timingVer);
            added += tEnriched;
        }

        // 基于真实 ts 时间线（含父子对话链），进一步补齐无 gen_metadata 步骤（如 step 1 CHECKPOINT）与子代理会话的模型归属
        const long timelineVer = 2;
        const string tlWmKey = "ag_timeline_ver";
        bool tlUpgrade = GetWatermark(tlWmKey) != timelineVer;
        if (added > 0 || ledgerChanged || tlUpgrade)
        {
            long tlMinTs = tlUpgrade ? 0L : (QuickRecentOnly ? recentDbMinTs : (nowTs - 14L * 86400L));
            int tlEnriched = UsageStorage.EnrichAntigravityFromTimeline(ledger.ParentMap, graceSeconds: 120, minTs: tlMinTs);
            SetWatermark(tlWmKey, timelineVer);
            added += tlEnriched;
        }

        // 多账号归属原地补齐（前向因果 Stop Hook + 在线活跃会话切号屏障 + 历史 quota_snapshots 回溯 + 近期回合纠偏自愈）
        const long acctAttrVer = 3;
        const string acctWmKey = "ag_acct_attr_ver";
        const string hookSigWmKey = "ag_hook_log_sig";
        const string acctSrcWmKey = "ag_acct_src_sig";
        var (hookSig, hookEntries) = AgHookService.LoadTurnEntries();
        long acctSrcSig = UsageStorage.GetAccountAttributionSourceWatermark();
        bool verUpgrade = GetWatermark(acctWmKey) != acctAttrVer;
        bool hookChanged = hookSig != GetWatermark(hookSigWmKey);
        if (added > 0 ||
            hookChanged ||
            acctSrcSig != GetWatermark(acctSrcWmKey) ||
            verUpgrade)
        {
            long reconcileFromTs = verUpgrade
                ? (nowTs - 72L * 3600L)
                : (hookChanged ? (nowTs - 4L * 3600L) : 0L);
            long acctMinTs = verUpgrade ? 0L : (QuickRecentOnly ? recentDbMinTs : (nowTs - 14L * 86400L));
            int acctEnriched = UsageStorage.EnrichAntigravityAccounts(
                ledger.ParentMap, hookEntries, reconcileFromTs, liveEmail, minTs: acctMinTs);
            SetWatermark(acctWmKey, acctAttrVer);
            SetWatermark(hookSigWmKey, hookSig);
            SetWatermark(acctSrcWmKey, acctSrcSig);
            added += acctEnriched;
        }

        return added;
    }

    int ScanTranscriptFile(string convId, string convDir, string filePath, LedgerLookup ledger, ConversationDbResolver convDb)
    {
        long start = GetFileOffset(filePath);
        var info = new FileInfo(filePath);
        if (!info.Exists || info.Length <= start) return 0;

        byte[] delta;
        long bytesToRead = info.Length - start;
        if (bytesToRead <= 0) return 0;

        StepStub? leadingPrev = null;
        using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            if (start > 0)
                leadingPrev = ReadTrailingStepBefore(fs, start);

            fs.Seek(start, SeekOrigin.Begin);
            int cap = (int)Math.Min(bytesToRead, int.MaxValue);
            delta = new byte[cap];
            int totalRead = 0;
            while (totalRead < cap)
            {
                int n = fs.Read(delta, totalRead, cap - totalRead);
                if (n <= 0) break;
                totalRead += n;
            }
            if (totalRead <= 0) return 0;
            if (totalRead < delta.Length)
                Array.Resize(ref delta, totalRead);
        }

        // 仅消费到最后一个换行符为止，防止读取正在追加的半行 JSON 或切碎多字节 UTF-8
        int lastNewline = Array.LastIndexOf(delta, (byte)'\n');
        if (lastNewline < 0) return 0;

        long committedOffset = start + lastNewline + 1;
        var parsed = new List<StepStub>();

        int lineStart = 0;
        for (int i = 0; i <= lastNewline; i++)
        {
            if (delta[i] != (byte)'\n') continue;
            int len = i - lineStart;
            if (len > 0 && delta[lineStart + len - 1] == (byte)'\r') len--;
            if (len > 0)
            {
                // 跳过首行可能存在的 UTF-8 BOM
                if (start == 0 && lineStart == 0 && len >= 3 &&
                    delta[0] == 0xEF && delta[1] == 0xBB && delta[2] == 0xBF)
                {
                    lineStart += 3;
                    len -= 3;
                }
                if (len > 0)
                {
                    var line = Encoding.UTF8.GetString(delta, lineStart, len);
                    if (TryParseStepStub(start + lineStart, line, info.LastWriteTimeUtc, out var stub))
                        parsed.Add(stub);
                }
            }
            lineStart = i + 1;
        }

        var batch = new List<UsageStorage.UsageRecord>();

        var neededSteps = new List<long>();
        if (leadingPrev.HasValue && parsed.Count > 0 && leadingPrev.Value.StepIndex > 0 &&
            string.IsNullOrEmpty(NormalizeModel(leadingPrev.Value.RawModel, null)))
        {
            neededSteps.Add(leadingPrev.Value.StepIndex);
        }
        foreach (var cur in parsed)
        {
            if (cur.StepIndex > 0 && (cur.UncachedIn + cur.CacheRead + cur.OutTokens) > 0 &&
                string.IsNullOrEmpty(NormalizeModel(cur.RawModel, null)))
            {
                neededSteps.Add(cur.StepIndex);
            }
        }
        IReadOnlyDictionary<long, string>? dbStepModels = neededSteps.Count > 0
            ? convDb.LoadStepsForConv(convId, neededSteps)
            : null;

        // 若上一次扫描末尾恰好落在 PLANNER_RESPONSE 且本次首个新步骤已落盘，顺便用 Upsert 更新前一步的生成耗时
        if (leadingPrev.HasValue && parsed.Count > 0)
        {
            var lp = leadingPrev.Value;
            if (lp.Type == "PLANNER_RESPONSE" && lp.OutTokens > 0 && (lp.UncachedIn + lp.CacheRead + lp.OutTokens) > 0)
            {
                ComputeStepTiming(convDir, null, lp, parsed[0], out long prevDur, out _);
                if (prevDur > 0)
                {
                    string prevModel = NormalizeModel(lp.RawModel, null);
                    if (string.IsNullOrEmpty(prevModel) && lp.StepIndex > 0 && dbStepModels != null &&
                        dbStepModels.TryGetValue(lp.StepIndex, out var lpDbModel))
                    {
                        prevModel = lpDbModel;
                    }
                    string prevSid = BuildSourceId(convId, lp);
                    batch.Add(new UsageStorage.UsageRecord(
                        UsageStorage.AgentAntigravity,
                        "",
                        "Antigravity",
                        prevModel ?? "",
                        lp.UncachedIn + lp.CacheRead,
                        lp.OutTokens,
                        lp.TsUtc.ToLocalTime(),
                        CacheReadTokens: lp.CacheRead,
                        CacheWriteTokens: 0,
                        DurationMs: prevDur,
                        TtftMs: 0,
                        SourceId: prevSid));
                }
            }
        }

        int newStepCount = 0;
        for (int i = 0; i < parsed.Count; i++)
        {
            var cur = parsed[i];
            long totalIn = cur.UncachedIn + cur.CacheRead;
            if (totalIn <= 0 && cur.OutTokens <= 0) continue;

            StepStub? prev = i > 0 ? parsed[i - 1] : leadingPrev;
            StepStub? next = i + 1 < parsed.Count ? parsed[i + 1] : null;
            ComputeStepTiming(convDir, prev, cur, next, out long durMs, out long ttftMs);

            string model = NormalizeModel(cur.RawModel, null);
            if (string.IsNullOrEmpty(model) && cur.StepIndex > 0 && dbStepModels != null &&
                dbStepModels.TryGetValue(cur.StepIndex, out var dbModel))
            {
                model = dbModel;
            }
            if (string.IsNullOrEmpty(model) && ledger.HasData)
            {
                long tsSec = new DateTimeOffset(cur.TsUtc).ToUnixTimeSeconds();
                model = ledger.Resolve(convId, cur.StepIndex, cur.UncachedIn, cur.CacheRead, cur.OutTokens, tsSec);
            }

            batch.Add(new UsageStorage.UsageRecord(
                UsageStorage.AgentAntigravity,
                "",
                "Antigravity",
                model,
                totalIn,
                cur.OutTokens,
                cur.TsUtc.ToLocalTime(),
                CacheReadTokens: cur.CacheRead,
                CacheWriteTokens: 0,
                DurationMs: durMs,
                TtftMs: ttftMs,
                SourceId: BuildSourceId(convId, cur)));
            newStepCount++;
        }

        if (batch.Count == 0 || RecordBatch(batch))
        {
            SetFileOffset(filePath, committedOffset);
            return newStepCount;
        }
        return 0;
    }

    static StepStub? ReadTrailingStepBefore(FileStream fs, long endPos)
    {
        try
        {
            long lookback = Math.Min(endPos, 32768);
            long begin = endPos - lookback;
            fs.Seek(begin, SeekOrigin.Begin);
            var buf = new byte[(int)lookback];
            int total = 0;
            while (total < buf.Length)
            {
                int n = fs.Read(buf, total, buf.Length - total);
                if (n <= 0) break;
                total += n;
            }
            if (total <= 1) return null;

            int end = total;
            if (buf[end - 1] == (byte)'\n') end--;
            if (end > 0 && buf[end - 1] == (byte)'\r') end--;
            if (end <= 0) return null;

            int nl = Array.LastIndexOf(buf, (byte)'\n', end - 1);
            int lineStart = nl >= 0 ? nl + 1 : 0;
            int len = end - lineStart;
            if (len <= 0) return null;
            if (begin == 0 && lineStart == 0 && len >= 3 && buf[0] == 0xEF && buf[1] == 0xBB && buf[2] == 0xBF)
            {
                lineStart += 3;
                len -= 3;
            }
            if (len <= 0) return null;
            var line = Encoding.UTF8.GetString(buf, lineStart, len);
            return TryParseStepStub(begin + lineStart, line, DateTime.UtcNow, out var stub) ? stub : null;
        }
        catch
        {
            return null;
        }
    }

    static int BackfillExistingTimings(string brainRoot, DateTime cutoffLocal)
    {
        var timingMap = new Dictionary<string, (long DurMs, long TtftMs)>(StringComparer.Ordinal);
        IEnumerable<string> convDirs;
        try { convDirs = Directory.EnumerateDirectories(brainRoot); }
        catch { return 0; }

        foreach (var dir in convDirs)
        {
            var convId = Path.GetFileName(dir);
            if (string.IsNullOrEmpty(convId)) continue;
            var tp = Path.Combine(dir, ".system_generated", "logs", "transcript.jsonl");
            if (!File.Exists(tp)) continue;
            try
            {
                var fi = new FileInfo(tp);
                if (fi.LastWriteTime < cutoffLocal) continue;
                var steps = new List<StepStub>();
                using (var fs = new FileStream(tp, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var sr = new StreamReader(fs, Encoding.UTF8))
                {
                    string? line;
                    long approxOff = 0;
                    while ((line = sr.ReadLine()) != null)
                    {
                        if (TryParseStepStub(approxOff, line, fi.LastWriteTimeUtc, out var stub))
                            steps.Add(stub);
                        approxOff += Encoding.UTF8.GetByteCount(line) + 1;
                    }
                }

                for (int i = 0; i < steps.Count; i++)
                {
                    var cur = steps[i];
                    if (cur.Type != "PLANNER_RESPONSE" || cur.OutTokens <= 0) continue;
                    StepStub? prev = i > 0 ? steps[i - 1] : null;
                    StepStub? next = i + 1 < steps.Count ? steps[i + 1] : null;
                    ComputeStepTiming(dir, prev, cur, next, out long dur, out long ttft);
                    if (dur > 0 || ttft > 0)
                        timingMap[BuildSourceId(convId, cur)] = (dur, ttft);
                }
            }
            catch { }
        }

        return UsageStorage.EnrichUsageTiming(UsageStorage.AgentAntigravity, timingMap);
    }

    static bool TryParseStepStub(long byteOffset, string line, DateTime fallbackUtc, out StepStub stub)
    {
        stub = default;
        if (string.IsNullOrWhiteSpace(line)) return false;
        try
        {
            var node = JsonNode.Parse(line);
            if (node == null) return false;

            long uncachedIn = node["input_tokens"]?.GetValue<long>() ?? 0;
            long cacheRead = node["cache_read_tokens"]?.GetValue<long>() ?? 0;
            long outTok = node["output_tokens"]?.GetValue<long>() ?? 0;
            var type = node["type"]?.GetValue<string>() ?? "STEP";
            long stepIndex = node["step_index"]?.GetValue<long>() ?? 0;
            var rawModel = node["model"]?.GetValue<string>() ?? "";

            DateTime tsUtc = fallbackUtc;
            bool hasCreated = false;
            var createdRaw = node["created_at"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(createdRaw) && DateTime.TryParse(createdRaw,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var dt))
            {
                tsUtc = dt;
                hasCreated = true;
            }

            stub = new StepStub(type, stepIndex, tsUtc, outTok, uncachedIn, cacheRead, rawModel, byteOffset, hasCreated);
            return true;
        }
        catch
        {
            return false;
        }
    }

    static string BuildSourceId(string convId, StepStub cur)
        => cur.StepIndex > 0
            ? $"ag:{convId}:{cur.StepIndex}:{cur.Type}"
            : $"ag:{convId}:pos:{cur.ByteOffset}";

    /// <summary>
    /// 推算 Antigravity PLANNER_RESPONSE 步骤的近似生成耗时与首字耗时：
    /// - DurationMs：cur.TsUtc 至紧邻后一非 USER_INPUT 步骤（1s..180s，且输出速率 ≤ 800 t/s）；
    /// - TtftMs：由前一步 USER_INPUT/SYSTEM_MESSAGE/CHECKPOINT 或工具步骤 output.txt 落盘时间至 cur.TsUtc。
    /// </summary>
    static void ComputeStepTiming(string convDir, StepStub? prev, StepStub cur, StepStub? next,
        out long durationMs, out long ttftMs)
    {
        durationMs = 0;
        ttftMs = 0;
        if (cur.Type != "PLANNER_RESPONSE" || !cur.HasCreatedAt) return;

        if (cur.OutTokens > 0 && next.HasValue && next.Value.HasCreatedAt &&
            !string.Equals(next.Value.Type, "USER_INPUT", StringComparison.Ordinal))
        {
            long dur = (long)(next.Value.TsUtc - cur.TsUtc).TotalMilliseconds;
            if (dur >= 1000 && dur <= 180_000 && (cur.OutTokens * 1000.0 / dur) <= 800.0)
                durationMs = dur;
        }

        if (prev.HasValue && prev.Value.HasCreatedAt)
        {
            var p = prev.Value;
            if (p.Type is "USER_INPUT" or "SYSTEM_MESSAGE" or "CHECKPOINT")
            {
                long dt = (long)(cur.TsUtc - p.TsUtc).TotalMilliseconds;
                if (dt >= 0 && dt <= 60_000)
                    ttftMs = dt > 0 ? dt : 500;
            }
            else if (p.Type == "GENERIC" && p.StepIndex > 0)
            {
                var outTxt = Path.Combine(convDir, ".system_generated", "steps",
                    p.StepIndex.ToString(CultureInfo.InvariantCulture), "output.txt");
                if (File.Exists(outTxt))
                {
                    var mtimeUtc = File.GetLastWriteTimeUtc(outTxt);
                    long dt = (long)(cur.TsUtc - mtimeUtc).TotalMilliseconds;
                    if (dt >= 50 && dt <= 60_000)
                        ttftMs = dt;
                }
                else
                {
                    long dt = (long)(cur.TsUtc - p.TsUtc).TotalMilliseconds;
                    if (dt >= 1000 && dt <= 8000)
                        ttftMs = dt;
                }
            }
        }
    }

    static bool TryParseSourceId(string sourceId, out string convId, out long stepIndex)
    {
        convId = "";
        stepIndex = 0;
        if (string.IsNullOrEmpty(sourceId) || !sourceId.StartsWith("ag:", StringComparison.Ordinal))
            return false;
        var parts = sourceId.Split(':');
        if (parts.Length < 4) return false;
        convId = parts[1];
        if (!string.Equals(parts[2], "pos", StringComparison.Ordinal))
            long.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out stepIndex);
        return !string.IsNullOrEmpty(convId);
    }

    static readonly Regex EffortParenRegex = new(@"\s*\((?:xhigh|high|medium|low|minimal|orchestrator|thinking)\)\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly string[] TrailingNoiseSuffixes =
    {
        "-orchestrator", "-trial", "-control", "-tiered", "-thinking",
        "-xhigh", "-high", "-medium", "-low", "-minimal", "-n"
    };
    static readonly Regex F19VariantSuffixRegex = new(@"-(?:exp-[a-z0-9]+|[adhnpqr])$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    static readonly Dictionary<string, string> KnownEnumModels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["MODEL_PLACEHOLDER_M16"] = "gemini-3.1-pro",
        ["MODEL_PLACEHOLDER_M36"] = "gemini-3.1-pro",
        ["MODEL_PLACEHOLDER_M20"] = "gemini-3.5-flash",
        ["MODEL_PLACEHOLDER_M132"] = "gemini-3.5-flash",
        ["MODEL_PLACEHOLDER_M187"] = "gemini-3.5-flash",
        ["MODEL_PLACEHOLDER_M26"] = "claude-opus-4-6",
        ["MODEL_PLACEHOLDER_M35"] = "claude-sonnet-4-6",
        ["MODEL_PLACEHOLDER_M71"] = "gemini-3.6-flash",
        ["MODEL_PLACEHOLDER_M72"] = "gemini-3.6-flash",
        ["MODEL_PLACEHOLDER_M73"] = "gemini-3.6-flash",
        ["MODEL_PLACEHOLDER_M266"] = "gemini-3.6-flash",
        ["MODEL_PLACEHOLDER_M298"] = "gemini-3.7-flash",
        ["MODEL_PLACEHOLDER_M299"] = "gemini-3.7-flash",
        ["MODEL_PLACEHOLDER_M318"] = "gemini-3.8-flash",
        ["MODEL_PLACEHOLDER_M319"] = "gemini-3.8-flash",
        ["MODEL_PLACEHOLDER_M322"] = "gemini-3.8-flash",
        ["MODEL_PLACEHOLDER_M401"] = "claude-opus-5-5",
        ["MODEL_PLACEHOLDER_M404"] = "claude-sonnet-5-5",
        ["MODEL_PLACEHOLDER_M418"] = "gemini-4-argon",
        ["MODEL_PLACEHOLDER_M419"] = "gemini-4-argon",
        ["MODEL_PLACEHOLDER_M668"] = "gemini-4-argon",
        ["MODEL_PLACEHOLDER_M525"] = "gemini-4-hydrogen",
        ["MODEL_PLACEHOLDER_M526"] = "gemini-4-hydrogen",
        ["MODEL_PLACEHOLDER_M528"] = "gemini-4-hydrogen",
        ["MODEL_PLACEHOLDER_M530"] = "gemini-4-hydrogen",
        ["MODEL_PLACEHOLDER_M669"] = "gemini-4-hydrogen",
        ["MODEL_OPENAI_GPT_OSS_120B_MEDIUM"] = "gpt-oss-120b",
    };

    static string NormalizeRawF19(string? f19)
    {
        if (string.IsNullOrWhiteSpace(f19)) return "";
        var s = f19.Trim().ToLowerInvariant();
        if (s is "gemini-default" or "gemini-pro-default" or "gemini-flash-worker-agy"
            || s.StartsWith("gemini-pro-orchestrator", StringComparison.Ordinal))
        {
            return "";
        }
        s = s.Replace("3p7", "3.7", StringComparison.Ordinal);
        if (s.StartsWith("gemini-flash-3.6", StringComparison.Ordinal))
            s = "gemini-3.6-flash";
        s = F19VariantSuffixRegex.Replace(s, "");
        var norm = NormalizeModel(s, null);
        if (norm is "gemini-3-flash" or "gemini-3-flash-low") return "gemini-3.5-flash";
        return norm;
    }

    /// <summary>
    /// 把 Antigravity 台账中的多种模型命名（显示名、变体后缀、modelKey）归并为统一小写模型 ID。
    /// 仅针对非占位符的真实模型名返回规范串，占位符与空值返回空串（宁缺毋滥）。
    /// </summary>
    internal static string NormalizeModel(string? model, string? modelKey)
    {
        string raw = (!string.IsNullOrWhiteSpace(modelKey) && !IsPlaceholderModel(modelKey))
            ? modelKey.Trim()
            : (model?.Trim() ?? "");

        if (raw.Length == 0 || IsPlaceholderModel(raw)) return "";

        if (raw.Contains("Orchestrator (Argon)", StringComparison.OrdinalIgnoreCase))
            return "gemini-4-argon";

        raw = EffortParenRegex.Replace(raw, "").Trim();
        var sb = new StringBuilder(raw.Length);
        bool prevDash = false;
        foreach (char ch in raw)
        {
            if (char.IsWhiteSpace(ch))
            {
                if (!prevDash && sb.Length > 0) { sb.Append('-'); prevDash = true; }
            }
            else if (ch == '(' || ch == ')')
            {
                continue;
            }
            else
            {
                char lc = char.ToLowerInvariant(ch);
                sb.Append(lc);
                prevDash = lc == '-';
            }
        }
        string slug = sb.ToString().Trim('-');
        if (slug.Length == 0) return "";

        // 统一 Claude 显示名 "claude-opus-5.5" -> "claude-opus-5-5"（与 fetchAvailableModels 模型 ID 一致）
        if (slug.StartsWith("claude-", StringComparison.Ordinal) && Regex.IsMatch(slug, @"-\d+\.\d+"))
            slug = Regex.Replace(slug, @"(\d)\.(\d)", "$1-$2");

        bool stripped;
        do
        {
            stripped = false;
            foreach (var suffix in TrailingNoiseSuffixes)
            {
                if (slug.Length > suffix.Length && slug.EndsWith(suffix, StringComparison.Ordinal))
                {
                    slug = slug[..^suffix.Length];
                    stripped = true;
                    break;
                }
            }
        } while (stripped);

        return IsPlaceholderModel(slug) ? "" : slug;
    }

    static bool IsPlaceholderModel(string s)
    {
        var t = s.Trim();
        return t.Length == 0
            || t.StartsWith("MODEL_PLACEHOLDER_", StringComparison.OrdinalIgnoreCase)
            || string.Equals(t, "<synthetic>", StringComparison.OrdinalIgnoreCase);
    }

    sealed class LedgerLookup
    {
        readonly Dictionary<(string Cid, long Step), string> _byStep = new();
        readonly Dictionary<(string Cid, long InTok, long CacheRead, long OutTok), List<(long TsSec, string Model)>> _byCascadeTokens = new();
        readonly Dictionary<(long InTok, long CacheRead, long OutTok), List<(long TsSec, string Model)>> _byGlobalTokens = new();
        readonly Dictionary<string, string> _parentMap = new(StringComparer.OrdinalIgnoreCase);

        static readonly object s_cacheLock = new();
        static string? s_cachedRoot;
        static long s_cachedLedgerSig;
        static long s_cachedSummariesSig;
        static LedgerLookup? s_cachedLookup;

        public string LedgerPath { get; init; } = "";
        public long Signature { get; init; }
        public bool HasData => _byCascadeTokens.Count > 0 || _byStep.Count > 0;
        public IReadOnlyDictionary<string, string> ParentMap => _parentMap;

        static long ComputeFileSig(string path)
        {
            try
            {
                var fi = new FileInfo(path);
                if (!fi.Exists) return 0;
                long sig = (fi.LastWriteTimeUtc.Ticks & 0x7FFFFFFFFFFFFFFFL) ^ fi.Length;
                return sig == 0 ? 1 : sig;
            }
            catch { return 0; }
        }

        public static LedgerLookup Load(string agRoot)
        {
            var ledgerPath = Path.Combine(agRoot, "pricing_ledger_v1.json");
            var summariesPath = Path.Combine(agRoot, "conversation_summaries.db");
            long ledgerSig = ComputeFileSig(ledgerPath);
            long summariesSig = ComputeFileSig(summariesPath);

            lock (s_cacheLock)
            {
                if (s_cachedLookup != null &&
                    string.Equals(s_cachedRoot, agRoot, StringComparison.OrdinalIgnoreCase) &&
                    s_cachedLedgerSig == ledgerSig &&
                    s_cachedSummariesSig == summariesSig)
                {
                    return s_cachedLookup;
                }
            }

            var lookup = new LedgerLookup { LedgerPath = ledgerPath, Signature = ledgerSig };
            if (ledgerSig != 0)
            {
                try
                {
                    using var fs = new FileStream(ledgerPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var doc = JsonDocument.Parse(fs);
                    if (doc.RootElement.TryGetProperty("calls", out var callsEl) && callsEl.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var prop in callsEl.EnumerateObject())
                        {
                            var obj = prop.Value;
                            if (obj.ValueKind != JsonValueKind.Object) continue;

                            string cid = obj.TryGetProperty("cascadeId", out var cidEl) ? (cidEl.GetString() ?? "") : "";
                            string callId = obj.TryGetProperty("callId", out var callEl) ? (callEl.GetString() ?? "") : "";
                            string? rawModel = obj.TryGetProperty("model", out var mEl) ? mEl.GetString() : null;
                            string? rawKey = obj.TryGetProperty("modelKey", out var mkEl) ? mkEl.GetString() : null;
                            string norm = NormalizeModel(rawModel, rawKey);
                            if (string.IsNullOrEmpty(norm)) continue;

                            long inTok = ReadLong(obj, "inputTokens");
                            long crTok = ReadLong(obj, "cacheReadTokens");
                            long outTok = ReadLong(obj, "outputTokens");
                            long tsSec = ReadTsSec(obj);

                            int hashIdx = callId.IndexOf('#');
                            if (hashIdx > 0 && hashIdx < callId.Length - 1 &&
                                long.TryParse(callId.AsSpan(hashIdx + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out long stepIdx) &&
                                stepIdx > 0)
                            {
                                string stepCid = string.IsNullOrEmpty(cid) ? callId[..hashIdx] : cid;
                                if (!string.IsNullOrEmpty(stepCid))
                                    lookup._byStep.TryAdd((stepCid, stepIdx), norm);
                            }

                            if (inTok + crTok + outTok <= 0) continue;

                            if (!string.IsNullOrEmpty(cid))
                            {
                                var ck = (cid, inTok, crTok, outTok);
                                if (!lookup._byCascadeTokens.TryGetValue(ck, out var list))
                                    lookup._byCascadeTokens[ck] = list = new List<(long, string)>();
                                list.Add((tsSec, norm));
                            }

                            var gk = (inTok, crTok, outTok);
                            if (!lookup._byGlobalTokens.TryGetValue(gk, out var glist))
                                lookup._byGlobalTokens[gk] = glist = new List<(long, string)>();
                            glist.Add((tsSec, norm));
                        }
                    }
                }
                catch
                {
                    // 台账损坏或正在写入中途时，平稳降级跳过
                }
            }

            LoadParentMap(agRoot, lookup._parentMap);
            lock (s_cacheLock)
            {
                s_cachedRoot = agRoot;
                s_cachedLedgerSig = ledgerSig;
                s_cachedSummariesSig = summariesSig;
                s_cachedLookup = lookup;
            }
            return lookup;
        }

        public string Resolve(string convId, long stepIndex, long uncachedIn, long cacheRead, long outTok, long tsSec)
        {
            if (stepIndex > 0 && _byStep.TryGetValue((convId, stepIndex), out var byStepModel))
                return byStepModel;

            if (uncachedIn + cacheRead + outTok <= 0) return "";

            if (_byCascadeTokens.TryGetValue((convId, uncachedIn, cacheRead, outTok), out var exactList))
                return PickClosest(exactList, tsSec);

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { convId };
            string cur = convId;
            for (int depth = 0; depth < 4 && _parentMap.TryGetValue(cur, out var parent) && seen.Add(parent); depth++)
            {
                if (_byCascadeTokens.TryGetValue((parent, uncachedIn, cacheRead, outTok), out var parentList))
                    return PickClosest(parentList, tsSec);
                cur = parent;
            }

            if (_byGlobalTokens.TryGetValue((uncachedIn, cacheRead, outTok), out var gList))
            {
                string? candidate = null;
                foreach (var (ptTs, ptModel) in gList)
                {
                    if (tsSec > 0 && ptTs > 0 && Math.Abs(ptTs - tsSec) > 180) continue;
                    if (candidate == null) candidate = ptModel;
                    else if (!string.Equals(candidate, ptModel, StringComparison.Ordinal))
                        return "";
                }
                if (candidate != null) return candidate;
            }

            return "";
        }

        static string PickClosest(List<(long TsSec, string Model)> items, long tsSec)
        {
            if (items.Count == 1 || tsSec <= 0) return items[0].Model;
            string best = items[0].Model;
            long bestDiff = long.MaxValue;
            foreach (var (itemTs, itemModel) in items)
            {
                long diff = itemTs > 0 ? Math.Abs(itemTs - tsSec) : long.MaxValue / 2;
                if (diff < bestDiff)
                {
                    bestDiff = diff;
                    best = itemModel;
                }
            }
            return best;
        }

        static long ReadLong(JsonElement obj, string prop)
        {
            if (!obj.TryGetProperty(prop, out var el)) return 0;
            if (el.ValueKind == JsonValueKind.Number && el.TryGetInt64(out long v)) return v;
            return 0;
        }

        static long ReadTsSec(JsonElement obj)
        {
            if (obj.TryGetProperty("timestamp", out var tsEl) && tsEl.ValueKind == JsonValueKind.Number && tsEl.TryGetInt64(out long ms))
                return ms > 10_000_000_000L ? ms / 1000 : ms;
            if (obj.TryGetProperty("created_at", out var caEl) && caEl.ValueKind == JsonValueKind.String &&
                DateTime.TryParse(caEl.GetString(), CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var dt))
                return new DateTimeOffset(dt).ToUnixTimeSeconds();
            return 0;
        }

        static void LoadParentMap(string agRoot, Dictionary<string, string> target)
        {
            var dbPath = Path.Combine(agRoot, "conversation_summaries.db");
            if (!File.Exists(dbPath)) return;
            try
            {
                using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly;Pooling=False");
                conn.DefaultTimeout = 1;
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT conversation_id, parent_conversation_id FROM conversation_summaries WHERE parent_conversation_id <> '';";
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    if (!r.IsDBNull(0) && !r.IsDBNull(1))
                        target[r.GetString(0)] = r.GetString(1);
                }
            }
            catch
            {
            }
        }
    }

    /// <summary>
    /// 直接从 Antigravity 原生 `~/.gemini/antigravity/conversations/&lt;cid&gt;.db`（WAL 只读不占锁）解析每步真实模型。
    /// 解析链路：steps.metadata(f20.f3 = genIdx) -> gen_metadata.data(f1: f21 显示名 / f20.model_enum / f19 兜底)。
    /// </summary>
    sealed class ConversationDbResolver
    {
        sealed class ConvStepCache
        {
            public Dictionary<long, string> Models { get; } = new();
            public bool LoadedFull { get; set; }
            public bool LastFailed { get; set; }
        }

        readonly string _convsDir;
        readonly Dictionary<string, ConvStepCache> _cache = new(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, string> _learnedEnums = new(KnownEnumModels, StringComparer.OrdinalIgnoreCase);

        public ConversationDbResolver(string agRoot)
        {
            _convsDir = Path.Combine(agRoot, "conversations");
        }

        public bool LastLoadFailed(string convId)
            => _cache.TryGetValue(convId, out var c) && c.LastFailed;

        string GetDbPath(string convId) => Path.Combine(_convsDir, convId + ".db");

        public bool HasDb(string convId)
            => !string.IsNullOrWhiteSpace(convId) && Directory.Exists(_convsDir) && File.Exists(GetDbPath(convId));

        public long GetDbAndWalSignature(string convId)
        {
            var dbPath = GetDbPath(convId);
            try
            {
                var dbInfo = new FileInfo(dbPath);
                if (!dbInfo.Exists) return 0;
                long sig = 17;
                sig = unchecked(sig * 31 + dbInfo.LastWriteTimeUtc.Ticks + dbInfo.Length);
                var walInfo = new FileInfo(dbPath + "-wal");
                if (walInfo.Exists)
                    sig = unchecked(sig * 31 + walInfo.LastWriteTimeUtc.Ticks + walInfo.Length);
                long norm = sig & 0x7FFFFFFFFFFFFFFFL;
                return norm == 0 ? 1 : norm;
            }
            catch
            {
                return 0;
            }
        }

        public IReadOnlyDictionary<long, string> LoadStepsForConv(string convId, IEnumerable<long> requestedSteps)
        {
            if (!_cache.TryGetValue(convId, out var entry))
                _cache[convId] = entry = new ConvStepCache();

            var dbPath = GetDbPath(convId);
            if (!File.Exists(dbPath))
                return entry.Models;

            if (entry.LoadedFull)
                return entry.Models;

            var missing = new HashSet<long>();
            foreach (long s in requestedSteps)
            {
                if (s > 0 && !entry.Models.ContainsKey(s))
                    missing.Add(s);
            }
            if (missing.Count == 0)
                return entry.Models;

            try
            {
                entry.LastFailed = false;
                LoadFromDb(dbPath, missing, entry);
            }
            catch
            {
                entry.LastFailed = true;
                // 第三方 WAL 忙碌或损坏时平稳降级，由后续台账/时间线与下一轮签名变化接管
            }
            return entry.Models;
        }

        void LoadFromDb(string dbPath, HashSet<long> missingSteps, ConvStepCache entry)
        {
            using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly;Pooling=False");
            conn.DefaultTimeout = 1;
            conn.Open();

            bool fullScan = missingSteps.Count > 32;
            var stepToGen = new Dictionary<long, long>();
            using (var stepCmd = conn.CreateCommand())
            {
                stepCmd.CommandText = fullScan
                    ? "SELECT idx, metadata FROM steps WHERE metadata IS NOT NULL;"
                    : $"SELECT idx, metadata FROM steps WHERE metadata IS NOT NULL AND idx IN ({JoinLongs(missingSteps)});";
                using var sr = stepCmd.ExecuteReader();
                while (sr.Read())
                {
                    if (sr.IsDBNull(0) || sr.IsDBNull(1)) continue;
                    long sidx = sr.GetInt64(0);
                    var meta = sr.GetFieldValue<byte[]>(1);
                    if (TryReadStepGenIdx(meta, out long gidx) && gidx > 0)
                        stepToGen[sidx] = gidx;
                }
            }

            if (fullScan)
                entry.LoadedFull = true;

            if (stepToGen.Count == 0) return;

            var neededGens = new HashSet<long>(stepToGen.Values);
            var rawGens = new Dictionary<long, (string EnumId, string F21, string F19)>();

            using (var genCmd = conn.CreateCommand())
            {
                bool genByIds = !fullScan && neededGens.Count <= 32;
                genCmd.CommandText = genByIds
                    ? $"SELECT idx, data FROM gen_metadata WHERE data IS NOT NULL AND idx IN ({JoinLongs(neededGens)});"
                    : "SELECT idx, data FROM gen_metadata WHERE data IS NOT NULL;";
                using var gr = genCmd.ExecuteReader();
                while (gr.Read())
                {
                    if (gr.IsDBNull(0) || gr.IsDBNull(1)) continue;
                    long gidx = gr.GetInt64(0);
                    if (!genByIds && !neededGens.Contains(gidx)) continue;

                    var blob = gr.GetFieldValue<byte[]>(1);
                    var parsed = ParseGenMetadata(blob);
                    rawGens[gidx] = parsed;

                    if (!string.IsNullOrEmpty(parsed.EnumId) && !string.IsNullOrEmpty(parsed.F21))
                    {
                        var normF21 = NormalizeModel(parsed.F21, null);
                        if (!string.IsNullOrEmpty(normF21))
                            _learnedEnums.TryAdd(parsed.EnumId, normF21);
                    }
                }
            }

            foreach (var (stepIdx, genIdx) in stepToGen)
            {
                if (!rawGens.TryGetValue(genIdx, out var g)) continue;
                var model = ResolveGenTuple(g.EnumId, g.F21, g.F19);
                if (!string.IsNullOrEmpty(model))
                    entry.Models[stepIdx] = model;
            }
        }

        string ResolveGenTuple(string modelEnum, string f21, string f19)
        {
            var m = NormalizeModel(f21, null);
            if (!string.IsNullOrEmpty(m)) return m;

            if (!string.IsNullOrEmpty(modelEnum) && _learnedEnums.TryGetValue(modelEnum, out var mapped) &&
                !string.IsNullOrEmpty(mapped))
            {
                return mapped;
            }

            return NormalizeRawF19(f19);
        }

        static string JoinLongs(IEnumerable<long> values)
            => string.Join(",", values.Select(v => v.ToString(CultureInfo.InvariantCulture)));

        static bool TryReadStepGenIdx(ReadOnlySpan<byte> meta, out long genIdx)
        {
            genIdx = 0;
            int pos = 0;
            while (pos < meta.Length)
            {
                if (!TryReadVarint(meta, ref pos, out ulong key)) return false;
                int fn = (int)(key >> 3);
                int wt = (int)(key & 7);
                if (fn <= 0) return false;
                if (fn == 20 && wt == 2)
                {
                    if (!TryReadLengthDelimited(meta, ref pos, out var sub)) return false;
                    int sp = 0;
                    while (sp < sub.Length)
                    {
                        if (!TryReadVarint(sub, ref sp, out ulong sk)) return false;
                        int sfn = (int)(sk >> 3);
                        int swt = (int)(sk & 7);
                        if (sfn == 3 && swt == 0)
                        {
                            if (!TryReadVarint(sub, ref sp, out ulong val)) return false;
                            genIdx = (long)val;
                            return true;
                        }
                        if (!TrySkipField(sub, ref sp, swt)) return false;
                    }
                }
                else if (!TrySkipField(meta, ref pos, wt))
                {
                    return false;
                }
            }
            return false;
        }

        static (string EnumId, string F21, string F19) ParseGenMetadata(ReadOnlySpan<byte> data)
        {
            string modelEnum = "", f21 = "", f19 = "";
            int pos = 0;
            while (pos < data.Length)
            {
                if (!TryReadVarint(data, ref pos, out ulong key)) break;
                int fn = (int)(key >> 3);
                int wt = (int)(key & 7);
                if (fn <= 0) break;

                // 顶层 f3 常为数百 KB~数 MB 的工具上下文载荷，直接跳过仅解析紧凑的 f1 元数据块
                if (fn == 1 && wt == 2)
                {
                    if (!TryReadLengthDelimited(data, ref pos, out var f1)) break;
                    int sp = 0;
                    while (sp < f1.Length)
                    {
                        if (!TryReadVarint(f1, ref sp, out ulong sk)) break;
                        int sfn = (int)(sk >> 3);
                        int swt = (int)(sk & 7);
                        if (sfn <= 0) break;

                        if (sfn == 21 && swt == 2)
                        {
                            if (!TryReadLengthDelimited(f1, ref sp, out var slice)) break;
                            f21 = Encoding.UTF8.GetString(slice);
                        }
                        else if (sfn == 19 && swt == 2)
                        {
                            if (!TryReadLengthDelimited(f1, ref sp, out var slice)) break;
                            f19 = Encoding.UTF8.GetString(slice);
                        }
                        else if (sfn == 20 && swt == 2)
                        {
                            if (!TryReadLengthDelimited(f1, ref sp, out var kv)) break;
                            ReadKvEntry(kv, out var k, out var v);
                            if (string.Equals(k, "model_enum", StringComparison.Ordinal) && !string.IsNullOrEmpty(v))
                                modelEnum = v;
                        }
                        else if (!TrySkipField(f1, ref sp, swt))
                        {
                            break;
                        }
                    }
                }
                else if (!TrySkipField(data, ref pos, wt))
                {
                    break;
                }
            }
            return (modelEnum, f21, f19);
        }

        static void ReadKvEntry(ReadOnlySpan<byte> kv, out string keyStr, out string valStr)
        {
            keyStr = "";
            valStr = "";
            int pos = 0;
            while (pos < kv.Length)
            {
                if (!TryReadVarint(kv, ref pos, out ulong key)) break;
                int fn = (int)(key >> 3);
                int wt = (int)(key & 7);
                if (fn <= 0) break;
                if (fn == 1 && wt == 2)
                {
                    if (!TryReadLengthDelimited(kv, ref pos, out var s)) break;
                    keyStr = Encoding.UTF8.GetString(s);
                }
                else if (fn == 2 && wt == 2)
                {
                    if (!TryReadLengthDelimited(kv, ref pos, out var s)) break;
                    valStr = Encoding.UTF8.GetString(s);
                }
                else if (!TrySkipField(kv, ref pos, wt))
                {
                    break;
                }
            }
        }

        static bool TryReadVarint(ReadOnlySpan<byte> data, ref int pos, out ulong value)
        {
            value = 0;
            int shift = 0;
            while (pos < data.Length)
            {
                byte b = data[pos++];
                value |= (ulong)(b & 0x7F) << shift;
                if ((b & 0x80) == 0) return true;
                shift += 7;
                if (shift >= 64) return false;
            }
            return false;
        }

        static bool TryReadLengthDelimited(ReadOnlySpan<byte> data, ref int pos, out ReadOnlySpan<byte> slice)
        {
            slice = default;
            if (!TryReadVarint(data, ref pos, out ulong ulen) || ulen > (ulong)(data.Length - pos))
                return false;
            int len = (int)ulen;
            slice = data.Slice(pos, len);
            pos += len;
            return true;
        }

        static bool TrySkipField(ReadOnlySpan<byte> data, ref int pos, int wireType)
        {
            switch (wireType)
            {
                case 0:
                    return TryReadVarint(data, ref pos, out _);
                case 1:
                    if (data.Length - pos < 8) return false;
                    pos += 8;
                    return true;
                case 2:
                    return TryReadLengthDelimited(data, ref pos, out _);
                case 5:
                    if (data.Length - pos < 4) return false;
                    pos += 4;
                    return true;
                default:
                    return false;
            }
        }
    }
}

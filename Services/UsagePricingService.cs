using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace APISwitch.Services;

public sealed class PricingOverridesConfig
{
    public bool AutoSyncCloudPrices { get; set; } = true;
    public List<ProviderMultiplierRule> ProviderMultipliers { get; set; } = new();
    public List<ModelPriceOverrideRule> ModelOverrides { get; set; } = new();
}

public sealed class ProviderMultiplierRule
{
    public string Provider { get; set; } = "";
    public double Multiplier { get; set; } = 1.0;
    public bool Enabled { get; set; } = true;
    public string? Note { get; set; }
}

public sealed class ModelPriceOverrideRule
{
    /// <summary>"*" 表示对全部供应商生效，否则仅对匹配的供应商生效。</summary>
    public string Provider { get; set; } = "*";
    /// <summary>模型标识（支持精确模型名、归一化模型名，或末尾带 '*' 的前缀通配）。</summary>
    public string Model { get; set; } = "";
    public double InputPerMillion { get; set; }
    public double OutputPerMillion { get; set; }
    public double? CacheReadPerMillion { get; set; }
    public double? CacheWritePerMillion { get; set; }
    public bool Enabled { get; set; } = true;
}

public sealed class ModelPriceCacheFile
{
    public DateTime UpdatedAtUtc { get; set; }
    public int ModelsDevCount { get; set; }
    public int OpenRouterCount { get; set; }
    public int AgLedgerCount { get; set; }
    public Dictionary<string, CachedModelRateDto> Rates { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class CachedModelRateDto
{
    [JsonPropertyName("in")]
    public double Input { get; set; }
    [JsonPropertyName("cr")]
    public double CacheRead { get; set; }
    [JsonPropertyName("cw")]
    public double CacheWrite { get; set; }
    [JsonPropertyName("out")]
    public double Output { get; set; }
    [JsonPropertyName("p")]
    public int Priority { get; set; }
}

/// <summary>
/// 四层动态定价引擎：
/// 1) 用户自定义规则：(供应商 + 模型) 精确单价覆盖 > 供应商整站倍率 × 基准单价 > 全局模型单价覆盖；
/// 2) Antigravity 本地 pricing_ledger_v1.json 官方账本实时自学习（内部新模型第 0 天自动反推单价）；
/// 3) 云端公开模型价格库自动热同步（models.dev/api.json 主源 7700+ 模型 + OpenRouter API 辅源，24h 缓存）；
/// 4) 内置主流模型家族刊例价离线兜底。
/// </summary>
public static class UsagePricingService
{
    public readonly record struct ModelRate(
        double InputPerMillion,
        double CacheReadPerMillion,
        double CacheWritePerMillion,
        double OutputPerMillion);

    public readonly record struct ResolvedRateDetail(
        ModelRate Rate,
        string SourceKind, // "override_exact" | "override_global" | "ag_ledger" | "cloud_sync" | "builtin"
        double? MultiplierApplied);

    public readonly record struct SyncResult(
        bool Success,
        int TotalModels,
        int ModelsDevModels,
        int OpenRouterModels,
        int AgLedgerModels,
        DateTime UpdatedAtUtc,
        string? ErrorMessage);

    private static readonly string[] StripSuffixes =
    {
        "-orchestrator", "-preview", "-minimal", "-xhigh", "-medium",
        "-latest", "-tiered", "-trial", "-high", "-free", "-fast", "-chat", "-low"
    };

    private static readonly HashSet<string> Tier1Providers = new(StringComparer.OrdinalIgnoreCase)
    {
        "anthropic", "openai", "google", "google-vertex", "google-vertex-anthropic",
        "deepseek", "xai", "moonshotai", "moonshot", "zhipuai", "zai", "z-ai",
        "alibaba", "alibaba-cn", "qwen", "stepfun", "minimax", "mistral"
    };

    private static readonly HashSet<string> Tier2Providers = new(StringComparer.OrdinalIgnoreCase)
    {
        "openrouter", "azure", "amazon-bedrock", "fireworks-ai", "togetherai", "deepinfra", "siliconflow"
    };

    private static readonly HttpClient SyncHttp = CreateHttpClient();
    private static readonly object StateLock = new();
    private static readonly SemaphoreSlim SyncGate = new(1, 1);

    private static string? _loadedAppDataRoot;
    private static PricingOverridesConfig _overrides = new();
    private static Dictionary<string, ModelRate> _dynamicRates = new(StringComparer.OrdinalIgnoreCase);
    private static Dictionary<string, string> _providerIdToName = new(StringComparer.OrdinalIgnoreCase);
    private static DateTime _cacheUpdatedAtUtc = DateTime.MinValue;
    private static int _modelsDevCount;
    private static int _openRouterCount;
    private static int _agLedgerCount;
    private static long _lastAgLedgerSig;

    public static string OverridesFilePath =>
        Path.Combine(AgPaths.AppData, "APISwitch", "pricing_overrides.json");

    public static string CacheFilePath =>
        Path.Combine(AgPaths.AppData, "APISwitch", "model_prices_cache.json");

    private static HttpClient CreateHttpClient()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli
        };
        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(15)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("APISwitch/0.3.6");
        return client;
    }

    /// <summary>获取当前缓存状态摘要。</summary>
    public static (int TotalCount, int ModelsDevCount, int OpenRouterCount, int AgLedgerCount, DateTime UpdatedAtUtc) GetCacheStatus()
    {
        EnsureLoaded();
        lock (StateLock)
        {
            return (_dynamicRates.Count, _modelsDevCount, _openRouterCount, _agLedgerCount, _cacheUpdatedAtUtc);
        }
    }

    public static PricingOverridesConfig GetOverridesSnapshot()
    {
        EnsureLoaded();
        lock (StateLock)
        {
            var json = JsonSerializer.Serialize(_overrides);
            return JsonSerializer.Deserialize<PricingOverridesConfig>(json) ?? new PricingOverridesConfig();
        }
    }

    public static void SaveOverrides(PricingOverridesConfig config)
    {
        if (config == null) return;
        lock (StateLock)
        {
            _overrides = config;
            _loadedAppDataRoot = AgPaths.AppData;
            try
            {
                var dir = Path.GetDirectoryName(OverridesFilePath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                var json = JsonSerializer.Serialize(_overrides, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(OverridesFilePath, json, new UTF8Encoding(false));
            }
            catch { }
        }
        System.Threading.Interlocked.Increment(ref _pricingVersion);
    }

    private static long _lastLedgerProbeTick;
    private static long _pricingVersion = 1;
    private static HashSet<string> _agLedgerModelKeys = new(StringComparer.OrdinalIgnoreCase);
    public static long PricingVersion => System.Threading.Interlocked.Read(ref _pricingVersion);

    public static void Reload()
    {
        lock (StateLock)
        {
            _loadedAppDataRoot = null;
            _lastLedgerProbeTick = 0;
        }
        System.Threading.Interlocked.Increment(ref _pricingVersion);
        EnsureLoaded();
    }

    private static void EnsureLoaded()
    {
        var curRoot = AgPaths.AppData;
        lock (StateLock)
        {
            if (string.Equals(_loadedAppDataRoot, curRoot, StringComparison.OrdinalIgnoreCase))
            {
                // 节流本地账本探测（≥2.5s 才触盘一次，杜绝批量算价循环内数万次 NTFS FileInfo 系统调用）
                long nowTick = Environment.TickCount64;
                if (AgPaths.IsSandboxed || nowTick - _lastLedgerProbeTick >= 2500)
                {
                    _lastLedgerProbeTick = nowTick;
                    RefreshAntigravityLedgerInMemoryLocked();
                }
                return;
            }

            _loadedAppDataRoot = curRoot;
            _lastLedgerProbeTick = Environment.TickCount64;
            _overrides = LoadOverridesFromDisk();
            _providerIdToName = UsageStorage.BuildProviderFriendlyNameMap();
            var cache = LoadCacheFromDisk();
            _dynamicRates = new Dictionary<string, ModelRate>(StringComparer.OrdinalIgnoreCase);
            _cacheUpdatedAtUtc = cache?.UpdatedAtUtc ?? DateTime.MinValue;
            _modelsDevCount = cache?.ModelsDevCount ?? 0;
            _openRouterCount = cache?.OpenRouterCount ?? 0;
            _agLedgerCount = cache?.AgLedgerCount ?? 0;
            _lastAgLedgerSig = 0;

            if (cache?.Rates != null && cache.Rates.Count > 0)
            {
                foreach (var kv in cache.Rates)
                {
                    _dynamicRates[kv.Key] = new ModelRate(kv.Value.Input, kv.Value.CacheRead, kv.Value.CacheWrite, kv.Value.Output);
                }
            }
            else if (!AgPaths.IsSandboxed)
            {
                // 首次启动零流量预热：若本机已有 OpenCode 缓存的 models.json，直接离线导入 7700+ 模型单价
                TryBootstrapFromLocalOpenCodeCacheLocked();
            }

            RefreshAntigravityLedgerInMemoryLocked();
        }
    }

    private static PricingOverridesConfig LoadOverridesFromDisk()
    {
        try
        {
            if (File.Exists(OverridesFilePath))
            {
                var txt = File.ReadAllText(OverridesFilePath, Encoding.UTF8);
                var cfg = JsonSerializer.Deserialize<PricingOverridesConfig>(txt);
                if (cfg != null) return cfg;
            }
        }
        catch { }
        return new PricingOverridesConfig();
    }

    private static ModelPriceCacheFile? LoadCacheFromDisk()
    {
        try
        {
            if (File.Exists(CacheFilePath))
            {
                var txt = File.ReadAllText(CacheFilePath, Encoding.UTF8);
                return JsonSerializer.Deserialize<ModelPriceCacheFile>(txt);
            }
        }
        catch { }
        return null;
    }

    private static void TryBootstrapFromLocalOpenCodeCacheLocked()
    {
        try
        {
            var home = ClaudeCli.HomeDir;
            var ocCache = Path.Combine(home, ".cache", "opencode", "models.json");
            if (!File.Exists(ocCache)) return;
            var json = File.ReadAllText(ocCache, Encoding.UTF8);
            var staged = new Dictionary<string, CachedModelRateDto>(StringComparer.OrdinalIgnoreCase);
            int count = ParseModelsDevJson(json, staged);
            if (count > 0)
            {
                foreach (var kv in staged)
                    _dynamicRates[kv.Key] = new ModelRate(kv.Value.Input, kv.Value.CacheRead, kv.Value.CacheWrite, kv.Value.Output);
                _modelsDevCount = count;
            }
        }
        catch { }
    }

    private static void RefreshAntigravityLedgerInMemoryLocked()
    {
        try
        {
            var home = ClaudeCli.HomeDir;
            var ledgerPath = Path.Combine(home, ".gemini", "antigravity", "pricing_ledger_v1.json");
            var fi = new FileInfo(ledgerPath);
            if (!fi.Exists || fi.Length == 0) return;

            long sig = (fi.Length * 397L) ^ fi.LastWriteTimeUtc.Ticks;
            if (sig == _lastAgLedgerSig) return;
            _lastAgLedgerSig = sig;

            var learned = LearnRatesFromAntigravityLedgerFile(ledgerPath);
            if (learned.Count > 0)
            {
                var nextKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var kv in learned)
                {
                    _dynamicRates[kv.Key] = kv.Value;
                    nextKeys.Add(kv.Key);
                }
                _agLedgerModelKeys = nextKeys;
                _agLedgerCount = learned.Count;
            }
        }
        catch { }
    }

    /// <summary>
    /// 从 Antigravity 本地 pricing_ledger_v1.json 的真实 costUSD 样本精确反推各内部模型单价（$/1M）。
    /// </summary>
    internal static Dictionary<string, ModelRate> LearnRatesFromAntigravityLedgerJson(string json)
    {
        var result = new Dictionary<string, ModelRate>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(json)) return result;

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("calls", out var callsEl) || callsEl.ValueKind != JsonValueKind.Object)
                return result;

            var samplesByModel = new Dictionary<string, List<(double InM, double CrM, double OutM, double Cost)>>(StringComparer.OrdinalIgnoreCase);

            foreach (var prop in callsEl.EnumerateObject())
            {
                var c = prop.Value;
                if (c.ValueKind != JsonValueKind.Object) continue;
                if (!c.TryGetProperty("isPriced", out var pricedEl) || pricedEl.ValueKind != JsonValueKind.True) continue;
                if (!c.TryGetProperty("costUSD", out var costEl) || !costEl.TryGetDouble(out double cost) || cost <= 0) continue;

                string mk = c.TryGetProperty("modelKey", out var mkEl) && mkEl.ValueKind == JsonValueKind.String
                    ? mkEl.GetString() ?? ""
                    : (c.TryGetProperty("model", out var mEl) && mEl.ValueKind == JsonValueKind.String ? mEl.GetString() ?? "" : "");
                string norm = NormalizePricingModelKey(mk);
                if (string.IsNullOrEmpty(norm)) continue;

                double inTok = c.TryGetProperty("inputTokens", out var inEl) && inEl.TryGetDouble(out var iv) ? iv : 0;
                double crTok = c.TryGetProperty("cacheReadTokens", out var crEl) && crEl.TryGetDouble(out var crv) ? crv : 0;
                double outTok = c.TryGetProperty("outputTokens", out var outEl) && outEl.TryGetDouble(out var ov) ? ov : 0;
                if (inTok + crTok + outTok <= 0) continue;

                if (!samplesByModel.TryGetValue(norm, out var list))
                    samplesByModel[norm] = list = new List<(double, double, double, double)>();
                if (list.Count < 400)
                    list.Add((inTok / 1_000_000.0, crTok / 1_000_000.0, outTok / 1_000_000.0, cost));
            }

            foreach (var (normKey, pts) in samplesByModel)
            {
                if (pts.Count < 3) continue;
                if (TrySolveLinearRate3(pts, out var rate))
                    result[normKey] = rate;
            }
        }
        catch { }

        return result;
    }

    private static Dictionary<string, ModelRate> LearnRatesFromAntigravityLedgerFile(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var sr = new StreamReader(fs, Encoding.UTF8);
            return LearnRatesFromAntigravityLedgerJson(sr.ReadToEnd());
        }
        catch
        {
            return new Dictionary<string, ModelRate>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static bool TrySolveLinearRate3(
        List<(double InM, double CrM, double OutM, double Cost)> pts,
        out ModelRate rate)
    {
        rate = default;
        bool hasCr = pts.Any(p => p.CrM > 0.0001);
        double[,] a = new double[3, 3];
        double[] b = new double[3];

        foreach (var (x0, x1, x2, y) in pts)
        {
            double[] v = { x0, x1, x2 };
            for (int i = 0; i < 3; i++)
            {
                b[i] += v[i] * y;
                for (int j = 0; j < 3; j++)
                    a[i, j] += v[i] * v[j];
            }
        }

        for (int i = 0; i < 3; i++)
            a[i, i] += 1e-12;

        for (int i = 0; i < 3; i++)
        {
            int piv = i;
            for (int r = i + 1; r < 3; r++)
                if (Math.Abs(a[r, i]) > Math.Abs(a[piv, i])) piv = r;

            if (Math.Abs(a[piv, i]) < 1e-14) return false;

            if (piv != i)
            {
                for (int c = 0; c < 3; c++)
                    (a[i, c], a[piv, c]) = (a[piv, c], a[i, c]);
                (b[i], b[piv]) = (b[piv], b[i]);
            }

            for (int r = 0; r < 3; r++)
            {
                if (r == i) continue;
                double f = a[r, i] / a[i, i];
                for (int c = i; c < 3; c++)
                    a[r, c] -= f * a[i, c];
                b[r] -= f * b[i];
            }
        }

        double pIn = Math.Round(b[0] / a[0, 0], 4);
        double pCr = hasCr ? Math.Round(b[1] / a[1, 1], 4) : Math.Round(pIn * 0.10, 4);
        double pOut = Math.Round(b[2] / a[2, 2], 4);

        if (pIn < 0 || pIn > 300 || pOut < 0 || pOut > 600 || pCr < 0 || pCr > pIn * 1.5 + 0.01)
            return false;

        // 校验拟合残差，防止单一样本退化得出失真系数
        double maxErr = 0;
        foreach (var (x0, x1, x2, y) in pts)
        {
            double pred = x0 * pIn + x1 * pCr + x2 * pOut;
            double err = Math.Abs(pred - y);
            if (err > maxErr) maxErr = err;
        }
        if (maxErr > 0.01) return false;

        rate = new ModelRate(pIn, pCr, pIn, pOut);
        return true;
    }

    /// <summary>
    /// 若已开启自动同步且本地缓存超过 24 小时，则在后台静默更新价格库；更新成功后回调 onUpdated。
    /// </summary>
    public static void TriggerBackgroundAutoSyncIfNeeded(Action? onUpdated = null)
    {
        if (AgPaths.IsSandboxed) return;
        EnsureLoaded();

        bool shouldSync;
        lock (StateLock)
        {
            shouldSync = _overrides.AutoSyncCloudPrices &&
                         (_cacheUpdatedAtUtc == DateTime.MinValue ||
                          (DateTime.UtcNow - _cacheUpdatedAtUtc).TotalHours >= 24);
        }
        if (!shouldSync) return;

        _ = Task.Run(async () =>
        {
            var res = await SyncCloudPricesAsync();
            if (res.Success && res.TotalModels > 0)
                onUpdated?.Invoke();
        });
    }

    /// <summary>
    /// 从 models.dev/api.json + OpenRouter API 拉取全量公开模型价格并融合本地 Antigravity 账本后持久化缓存。
    /// </summary>
    public static async Task<SyncResult> SyncCloudPricesAsync(CancellationToken ct = default)
    {
        EnsureLoaded();
        if (!await SyncGate.WaitAsync(0, ct))
        {
            var cur = GetCacheStatus();
            return new SyncResult(true, cur.TotalCount, cur.ModelsDevCount, cur.OpenRouterCount, cur.AgLedgerCount, cur.UpdatedAtUtc, null);
        }

        try
        {
            var staged = new Dictionary<string, CachedModelRateDto>(StringComparer.OrdinalIgnoreCase);
            int mdCount = 0;
            int orCount = 0;
            string? lastErr = null;

            // 1. 主源：https://models.dev/api.json（7700+ 模型，含 cache_read / cache_write）
            try
            {
                using var resp = await SyncHttp.GetAsync("https://models.dev/api.json", ct);
                if (resp.IsSuccessStatusCode)
                {
                    var json = await resp.Content.ReadAsStringAsync(ct);
                    mdCount = ParseModelsDevJson(json, staged);
                }
                else
                {
                    lastErr = $"models.dev HTTP {(int)resp.StatusCode}";
                }
            }
            catch (Exception ex)
            {
                lastErr = "models.dev: " + ex.Message;
            }

            // 2. 辅源：https://openrouter.ai/api/v1/models（补齐最新上架前沿/开源模型）
            try
            {
                using var resp = await SyncHttp.GetAsync("https://openrouter.ai/api/v1/models", ct);
                if (resp.IsSuccessStatusCode)
                {
                    var json = await resp.Content.ReadAsStringAsync(ct);
                    orCount = ParseOpenRouterJson(json, staged);
                }
            }
            catch (Exception ex)
            {
                lastErr ??= "OpenRouter: " + ex.Message;
            }

            if (staged.Count == 0)
            {
                var cur = GetCacheStatus();
                return new SyncResult(false, cur.TotalCount, cur.ModelsDevCount, cur.OpenRouterCount, cur.AgLedgerCount, cur.UpdatedAtUtc, lastErr ?? "No models fetched");
            }

            // 3. 叠加本地 Antigravity 官方账本自学习单价（最高官方真值优先级）
            var home = ClaudeCli.HomeDir;
            var ledgerPath = Path.Combine(home, ".gemini", "antigravity", "pricing_ledger_v1.json");
            var agLearned = File.Exists(ledgerPath)
                ? LearnRatesFromAntigravityLedgerFile(ledgerPath)
                : new Dictionary<string, ModelRate>(StringComparer.OrdinalIgnoreCase);

            foreach (var kv in agLearned)
            {
                staged[kv.Key] = new CachedModelRateDto
                {
                    Input = kv.Value.InputPerMillion,
                    CacheRead = kv.Value.CacheReadPerMillion,
                    CacheWrite = kv.Value.CacheWritePerMillion,
                    Output = kv.Value.OutputPerMillion,
                    Priority = 100
                };
            }

            var nowUtc = DateTime.UtcNow;
            var cacheFile = new ModelPriceCacheFile
            {
                UpdatedAtUtc = nowUtc,
                ModelsDevCount = mdCount,
                OpenRouterCount = orCount,
                AgLedgerCount = agLearned.Count,
                Rates = staged
            };

            try
            {
                var dir = Path.GetDirectoryName(CacheFilePath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                var outJson = JsonSerializer.Serialize(cacheFile);
                await File.WriteAllTextAsync(CacheFilePath, outJson, new UTF8Encoding(false), ct);
            }
            catch { }

            lock (StateLock)
            {
                var nextDict = new Dictionary<string, ModelRate>(staged.Count, StringComparer.OrdinalIgnoreCase);
                foreach (var kv in staged)
                {
                    nextDict[kv.Key] = new ModelRate(kv.Value.Input, kv.Value.CacheRead, kv.Value.CacheWrite, kv.Value.Output);
                }
                _dynamicRates = nextDict;
                _cacheUpdatedAtUtc = nowUtc;
                _modelsDevCount = mdCount;
                _openRouterCount = orCount;
                _agLedgerCount = agLearned.Count;
            }

            return new SyncResult(true, staged.Count, mdCount, orCount, agLearned.Count, nowUtc, null);
        }
        finally
        {
            SyncGate.Release();
        }
    }

    internal static int ParseModelsDevJson(string json, Dictionary<string, CachedModelRateDto> target)
    {
        if (string.IsNullOrWhiteSpace(json)) return 0;
        int count = 0;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return 0;

            foreach (var provProp in doc.RootElement.EnumerateObject())
            {
                if (provProp.Value.ValueKind != JsonValueKind.Object) continue;
                if (!provProp.Value.TryGetProperty("models", out var modelsEl) || modelsEl.ValueKind != JsonValueKind.Object)
                    continue;

                string provId = provProp.Name;
                int prio = Tier1Providers.Contains(provId) ? 30 : (Tier2Providers.Contains(provId) ? 20 : 10);

                foreach (var mProp in modelsEl.EnumerateObject())
                {
                    if (mProp.Value.ValueKind != JsonValueKind.Object) continue;
                    if (!mProp.Value.TryGetProperty("cost", out var costEl) || costEl.ValueKind != JsonValueKind.Object)
                        continue;

                    double pIn = ReadDouble(costEl, "input");
                    double pOut = ReadDouble(costEl, "output");
                    if (pIn <= 0 && pOut <= 0) continue;
                    if (pIn > 1000 || pOut > 2000) continue;

                    double pCr = costEl.TryGetProperty("cache_read", out var crEl) && TryGetDoubleFlexible(crEl, out double crv) && crv >= 0
                        ? crv
                        : Math.Round(pIn * 0.10, 6);
                    double pCw = costEl.TryGetProperty("cache_write", out var cwEl) && TryGetDoubleFlexible(cwEl, out double cwv) && cwv >= 0
                        ? cwv
                        : pIn;

                    var dto = new CachedModelRateDto
                    {
                        Input = pIn,
                        CacheRead = pCr,
                        CacheWrite = pCw,
                        Output = pOut,
                        Priority = prio
                    };

                    string rawId = mProp.Name.Trim().ToLowerInvariant();
                    PutRateWithPriority(target, rawId, dto);

                    int slash = rawId.LastIndexOf('/');
                    if (slash >= 0 && slash + 1 < rawId.Length)
                        PutRateWithPriority(target, rawId[(slash + 1)..], dto);

                    string norm = NormalizePricingModelKey(rawId);
                    if (!string.IsNullOrEmpty(norm))
                        PutRateWithPriority(target, norm, dto);

                    count++;
                }
            }
        }
        catch { }
        return count;
    }

    internal static int ParseOpenRouterJson(string json, Dictionary<string, CachedModelRateDto> target)
    {
        if (string.IsNullOrWhiteSpace(json)) return 0;
        int count = 0;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("data", out var dataEl) || dataEl.ValueKind != JsonValueKind.Array)
                return 0;

            foreach (var item in dataEl.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                if (!item.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.String) continue;
                if (!item.TryGetProperty("pricing", out var prEl) || prEl.ValueKind != JsonValueKind.Object) continue;

                double perTokIn = ReadDouble(prEl, "prompt");
                double perTokOut = ReadDouble(prEl, "completion");
                if (perTokIn <= 0 && perTokOut <= 0) continue;

                double pIn = Math.Round(perTokIn * 1_000_000.0, 6);
                double pOut = Math.Round(perTokOut * 1_000_000.0, 6);
                if (pIn > 1000 || pOut > 2000) continue;

                double pCr = prEl.TryGetProperty("input_cache_read", out var crEl) && TryGetDoubleFlexible(crEl, out double crTok) && crTok >= 0
                    ? Math.Round(crTok * 1_000_000.0, 6)
                    : Math.Round(pIn * 0.10, 6);
                double pCw = prEl.TryGetProperty("input_cache_write", out var cwEl) && TryGetDoubleFlexible(cwEl, out double cwTok) && cwTok >= 0
                    ? Math.Round(cwTok * 1_000_000.0, 6)
                    : pIn;

                var dto = new CachedModelRateDto
                {
                    Input = pIn,
                    CacheRead = pCr,
                    CacheWrite = pCw,
                    Output = pOut,
                    Priority = 18 // 仅在官方 Tier1 未定义时补充
                };

                string rawId = (idEl.GetString() ?? "").Trim().ToLowerInvariant();
                if (string.IsNullOrEmpty(rawId)) continue;
                PutRateWithPriority(target, rawId, dto);

                int slash = rawId.LastIndexOf('/');
                if (slash >= 0 && slash + 1 < rawId.Length)
                    PutRateWithPriority(target, rawId[(slash + 1)..], dto);

                string norm = NormalizePricingModelKey(rawId);
                if (!string.IsNullOrEmpty(norm))
                    PutRateWithPriority(target, norm, dto);

                count++;
            }
        }
        catch { }
        return count;
    }

    private static void PutRateWithPriority(Dictionary<string, CachedModelRateDto> dict, string key, CachedModelRateDto candidate)
    {
        if (string.IsNullOrEmpty(key)) return;
        if (!dict.TryGetValue(key, out var existing) || candidate.Priority > existing.Priority)
        {
            dict[key] = candidate;
        }
    }

    private static double ReadDouble(JsonElement obj, string prop) =>
        obj.TryGetProperty(prop, out var el) && TryGetDoubleFlexible(el, out double v) ? v : 0.0;

    private static bool TryGetDoubleFlexible(JsonElement el, out double val)
    {
        val = 0;
        if (el.ValueKind == JsonValueKind.Number)
            return el.TryGetDouble(out val);
        if (el.ValueKind == JsonValueKind.String)
            return double.TryParse(el.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out val);
        return false;
    }

    /// <summary>兼容旧签名（不带特定供应商时按空供应商解析）。</summary>
    public static double EstimateCostUsd(
        string? model,
        string? agent,
        long inTokens,
        long outTokens,
        long cacheReadTokens = 0,
        long cacheWriteTokens = 0)
        => EstimateCostUsd(model, provider: null, agent, inTokens, outTokens, cacheReadTokens, cacheWriteTokens);

    /// <summary>
    /// 按 (供应商, 模型, 客户端) 四层动态定价计算等比或实际消耗金额（USD）。
    /// </summary>
    public static double EstimateCostUsd(
        string? model,
        string? provider,
        string? agent,
        long inTokens,
        long outTokens,
        long cacheReadTokens = 0,
        long cacheWriteTokens = 0)
    {
        if (inTokens <= 0 && outTokens <= 0 && cacheReadTokens <= 0 && cacheWriteTokens <= 0)
            return 0.0;

        var rate = ResolveRate(model, provider, agent);
        if (rate.InputPerMillion <= 0 && rate.OutputPerMillion <= 0 &&
            rate.CacheReadPerMillion <= 0 && rate.CacheWritePerMillion <= 0)
        {
            return 0.0;
        }

        long cr = Math.Max(0, cacheReadTokens);
        long cw = Math.Max(0, cacheWriteTokens);
        long freshIn;
        if (inTokens >= cr + cw)
            freshIn = inTokens - cr - cw;
        else if (inTokens >= cr)
            freshIn = inTokens - cr;
        else
            freshIn = Math.Max(0, inTokens);

        long outTok = Math.Max(0, outTokens);

        double cost = (freshIn * rate.InputPerMillion
                     + cr * rate.CacheReadPerMillion
                     + cw * rate.CacheWritePerMillion
                     + outTok * rate.OutputPerMillion) / 1_000_000.0;
        return cost > 0 ? cost : 0.0;
    }

    public static string FormatUsd(double usd, bool compactSubCent = true)
    {
        if (usd <= 0.0000001) return "$0.00";
        if (usd < 0.01)
            return compactSubCent ? "<$0.01" : "$" + usd.ToString("0.0000", CultureInfo.InvariantCulture);
        if (usd >= 1000)
            return "$" + usd.ToString("N2", CultureInfo.InvariantCulture);
        return "$" + usd.ToString("0.00", CultureInfo.InvariantCulture);
    }

    public static ModelRate ResolveRate(string? rawModel, string? agent = null) =>
        ResolveRate(rawModel, provider: null, agent);

    public static ModelRate ResolveRate(string? rawModel, string? provider, string? agent) =>
        ResolveRateDetailed(rawModel, provider, agent).Rate;

    public static ResolvedRateDetail ResolveRateDetailed(string? rawModel, string? provider, string? agent)
    {
        EnsureLoaded();

        string rawLower = (rawModel ?? "").Trim().ToLowerInvariant();
        string normKey = NormalizePricingModelKey(rawModel);

        ModelRate? exactProvOverride = null;
        ModelRate? globalModelOverride = null;
        double? provMultiplier = null;
        ModelRate? dynamicMatch = null;
        string? matchedDynKey = null;
        bool isLedgerKey = false;

        lock (StateLock)
        {
            // 1. 查找用户自定义模型单价覆盖（优先「指定供应商 + 模型」，其次「* 全局 + 模型」）
            if (_overrides.ModelOverrides.Count > 0)
            {
                foreach (var rule in _overrides.ModelOverrides)
                {
                    if (!rule.Enabled || string.IsNullOrWhiteSpace(rule.Model)) continue;
                    if (!MatchesModelRule(rule.Model, rawLower, normKey)) continue;

                    bool isWildcardProv = string.IsNullOrWhiteSpace(rule.Provider) || rule.Provider.Trim() == "*";
                    if (!isWildcardProv)
                    {
                        if (MatchesProviderRule(rule.Provider, provider, agent))
                        {
                            exactProvOverride = ToModelRate(rule);
                            break;
                        }
                    }
                    else if (globalModelOverride == null)
                    {
                        globalModelOverride = ToModelRate(rule);
                    }
                }
            }

            // 命中「指定供应商 + 指定模型」精确单价时直接返回（不再叠乘整站倍率）
            if (exactProvOverride.HasValue)
                return new ResolvedRateDetail(exactProvOverride.Value, "override_exact", null);

            // 2. 查找供应商整站倍率（如中转站 0.2x、公益站 0x）
            if (_overrides.ProviderMultipliers.Count > 0 && (!string.IsNullOrWhiteSpace(provider) || !string.IsNullOrWhiteSpace(agent)))
            {
                foreach (var pm in _overrides.ProviderMultipliers)
                {
                    if (!pm.Enabled || string.IsNullOrWhiteSpace(pm.Provider)) continue;
                    if (MatchesProviderRule(pm.Provider, provider, agent))
                    {
                        provMultiplier = Math.Max(0.0, pm.Multiplier);
                        break;
                    }
                }
            }

            // 3. 若未配置全局模型覆盖，则查动态同步字典（Antigravity 账本 + models.dev + OpenRouter）
            if (!globalModelOverride.HasValue && _dynamicRates.Count > 0 && rawLower.Length > 0)
            {
                if (_dynamicRates.TryGetValue(rawLower, out var r1))
                {
                    dynamicMatch = r1;
                    matchedDynKey = rawLower;
                }
                else
                {
                    int slash = rawLower.LastIndexOf('/');
                    if (slash >= 0 && slash + 1 < rawLower.Length && _dynamicRates.TryGetValue(rawLower[(slash + 1)..], out var r2))
                    {
                        dynamicMatch = r2;
                        matchedDynKey = rawLower[(slash + 1)..];
                    }
                    else if (normKey.Length > 0 && _dynamicRates.TryGetValue(normKey, out var r3))
                    {
                        dynamicMatch = r3;
                        matchedDynKey = normKey;
                    }
                }
                if (matchedDynKey != null && _agLedgerModelKeys.Contains(matchedDynKey))
                    isLedgerKey = true;
            }
        }

        string srcKind = globalModelOverride.HasValue
            ? "override_global"
            : dynamicMatch.HasValue
                ? (isLedgerKey ? "ag_ledger" : "cloud_sync")
                : "builtin";

        ModelRate baseRate = globalModelOverride
                             ?? dynamicMatch
                             ?? ResolveBuiltinFallbackRate(normKey, rawModel, agent);

        if (provMultiplier.HasValue)
        {
            double m = provMultiplier.Value;
            var scaled = new ModelRate(
                baseRate.InputPerMillion * m,
                baseRate.CacheReadPerMillion * m,
                baseRate.CacheWritePerMillion * m,
                baseRate.OutputPerMillion * m);
            return new ResolvedRateDetail(scaled, srcKind, m);
        }

        return new ResolvedRateDetail(baseRate, srcKind, null);
    }

    private static ModelRate ToModelRate(ModelPriceOverrideRule rule)
    {
        double pIn = Math.Max(0, rule.InputPerMillion);
        double pOut = Math.Max(0, rule.OutputPerMillion);
        double pCr = rule.CacheReadPerMillion.HasValue ? Math.Max(0, rule.CacheReadPerMillion.Value) : Math.Round(pIn * 0.10, 6);
        double pCw = rule.CacheWritePerMillion.HasValue ? Math.Max(0, rule.CacheWritePerMillion.Value) : pIn;
        return new ModelRate(pIn, pCr, pCw, pOut);
    }

    private static bool MatchesProviderRule(string ruleProvider, string? actualProvider, string? agent)
    {
        string rp = ruleProvider.Trim();
        if (rp.Length == 0) return false;

        string ap = (actualProvider ?? "").Trim();
        if (ap.Length > 0)
        {
            if (string.Equals(rp, ap, StringComparison.OrdinalIgnoreCase))
                return true;
            if (_providerIdToName.TryGetValue(ap, out var friendlyName) &&
                string.Equals(rp, friendlyName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            // 例如规则填 "Antigravity"，而调用明细里的 provider_name 是 "Antigravity · user@gmail.com" 或 "ag:user@gmail.com"
            if (string.Equals(rp, "Antigravity", StringComparison.OrdinalIgnoreCase) &&
                (ap.StartsWith("Antigravity · ", StringComparison.OrdinalIgnoreCase) ||
                 ap.StartsWith("ag:", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
            // 或者规则直接针对某个 Antigravity 邮箱（如 "user@gmail.com" 或 "Antigravity · user@gmail.com"）
            if (ap.StartsWith("ag:", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(rp, ap.Substring(3), StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            if (ap.StartsWith("Antigravity · ", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(rp, ap.Substring("Antigravity · ".Length), StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        if (!string.IsNullOrWhiteSpace(agent))
        {
            if (string.Equals(rp, agent.Trim(), StringComparison.OrdinalIgnoreCase))
                return true;
            if (string.Equals(rp, "Antigravity", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(agent.Trim(), UsageStorage.AgentAntigravity, StringComparison.OrdinalIgnoreCase) &&
                string.IsNullOrEmpty(ap))
            {
                return true;
            }
        }

        return false;
    }

    private static bool MatchesModelRule(string ruleModel, string rawLower, string normKey)
    {
        string rm = ruleModel.Trim().ToLowerInvariant();
        if (rm.Length == 0) return false;
        if (rm == "*") return true;

        if (rm.EndsWith('*') && rm.Length > 1)
        {
            string prefix = rm[..^1];
            return rawLower.StartsWith(prefix, StringComparison.Ordinal) ||
                   normKey.StartsWith(prefix, StringComparison.Ordinal);
        }

        if (string.Equals(rm, rawLower, StringComparison.Ordinal) ||
            string.Equals(rm, normKey, StringComparison.Ordinal))
        {
            return true;
        }

        string rmNorm = NormalizePricingModelKey(rm);
        return rmNorm.Length > 0 && string.Equals(rmNorm, normKey, StringComparison.Ordinal);
    }

    /// <summary>离线兜底主流模型家族刊例价。</summary>
    private static ModelRate ResolveBuiltinFallbackRate(string key, string? rawModel, string? agent)
    {
        if (key.Length == 0)
        {
            return (agent ?? "").ToLowerInvariant() switch
            {
                UsageStorage.AgentAntigravity => new ModelRate(0.75, 0.075, 0.75, 3.75),
                UsageStorage.AgentClaudeCli or UsageStorage.AgentClaudeDesktop => new ModelRate(3.00, 0.30, 3.75, 15.00),
                UsageStorage.AgentCodex => new ModelRate(2.00, 0.20, 2.00, 12.00),
                UsageStorage.AgentGeminiCli => new ModelRate(0.50, 0.05, 0.50, 3.00),
                _ => new ModelRate(0.60, 0.12, 0.60, 2.40),
            };
        }

        if (key.Contains("claude", StringComparison.Ordinal))
        {
            if (key.Contains("opus", StringComparison.Ordinal))
            {
                if (key.Contains("3-opus", StringComparison.Ordinal) ||
                    key.EndsWith("opus-4", StringComparison.Ordinal) ||
                    key.Contains("opus-4-0", StringComparison.Ordinal) ||
                    key.Contains("opus-4-1", StringComparison.Ordinal) ||
                    key.Contains("opus-4.0", StringComparison.Ordinal) ||
                    key.Contains("opus-4.1", StringComparison.Ordinal))
                {
                    return new ModelRate(15.00, 1.50, 18.75, 75.00);
                }
                return new ModelRate(5.00, 0.50, 6.25, 25.00);
            }
            if (key.Contains("haiku", StringComparison.Ordinal))
                return new ModelRate(1.00, 0.10, 1.25, 5.00);
            return new ModelRate(3.00, 0.30, 3.75, 15.00);
        }

        if (key.Contains("gemini", StringComparison.Ordinal) ||
            key.StartsWith("orchestrator", StringComparison.Ordinal))
        {
            if (key.Contains("flash-lite", StringComparison.Ordinal) ||
                key.Contains("tab_flash_lite", StringComparison.Ordinal) ||
                key.Contains("tab_jump_flash_lite", StringComparison.Ordinal))
            {
                return new ModelRate(0.10, 0.01, 0.10, 0.40);
            }
            if (key.Contains("argon", StringComparison.Ordinal))
                return new ModelRate(2.00, 0.10, 2.00, 10.00);
            if (key.Contains("gemini-4", StringComparison.Ordinal) ||
                key.Contains("gemini-3.1-pro", StringComparison.Ordinal) ||
                key.Contains("gemini-3-pro", StringComparison.Ordinal) ||
                key.Contains("orchestrator", StringComparison.Ordinal))
            {
                return new ModelRate(2.00, 0.20, 2.00, 12.00);
            }
            if (key.Contains("gemini-2.5-pro", StringComparison.Ordinal))
                return new ModelRate(1.25, 0.125, 1.25, 10.00);
            if (key.Contains("3.8-flash", StringComparison.Ordinal) ||
                key.Contains("3.7-flash", StringComparison.Ordinal) ||
                key.Contains("3.6-flash", StringComparison.Ordinal) ||
                key.Contains("3.5-flash", StringComparison.Ordinal))
            {
                return new ModelRate(0.75, 0.075, 0.75, 3.75);
            }
            if (key.Contains("3-flash", StringComparison.Ordinal) ||
                key.Contains("3.1-flash", StringComparison.Ordinal))
            {
                return new ModelRate(0.50, 0.05, 0.50, 3.00);
            }
            return new ModelRate(0.30, 0.03, 0.30, 2.50);
        }

        if (key.Contains("gpt-oss", StringComparison.Ordinal))
            return new ModelRate(0.15, 0.03, 0.15, 0.60);

        if (key.Contains("gpt-", StringComparison.Ordinal) ||
            key.StartsWith("o1", StringComparison.Ordinal) ||
            key.StartsWith("o3", StringComparison.Ordinal) ||
            key.StartsWith("o4", StringComparison.Ordinal) ||
            key.Contains("codex", StringComparison.Ordinal))
        {
            if (key.Contains("nano", StringComparison.Ordinal) || key.Contains("4o-mini", StringComparison.Ordinal))
                return new ModelRate(0.15, 0.015, 0.15, 0.60);
            if (key.Contains("5.4-mini", StringComparison.Ordinal) || key.Contains("5.5-mini", StringComparison.Ordinal))
                return new ModelRate(0.75, 0.075, 0.75, 4.50);
            if (key.Contains("mini", StringComparison.Ordinal))
                return new ModelRate(0.40, 0.04, 0.40, 1.60);
            if (key.Contains("gpt-6", StringComparison.Ordinal) ||
                key.Contains("gpt-5.4", StringComparison.Ordinal) ||
                key.Contains("gpt-5.5", StringComparison.Ordinal) ||
                key.Contains("gpt-5.6", StringComparison.Ordinal))
            {
                return new ModelRate(2.50, 0.25, 2.50, 15.00);
            }
            if (key.Contains("gpt-5", StringComparison.Ordinal) || key.Contains("codex", StringComparison.Ordinal))
                return new ModelRate(1.75, 0.175, 1.75, 14.00);
            return new ModelRate(2.00, 0.50, 2.00, 8.00);
        }

        if (key.Contains("grok", StringComparison.Ordinal))
        {
            if (key.Contains("mini", StringComparison.Ordinal) || key.Contains("fast", StringComparison.Ordinal))
                return new ModelRate(0.30, 0.075, 0.30, 0.50);
            return new ModelRate(3.00, 0.75, 3.00, 15.00);
        }

        if (key.Contains("deepseek", StringComparison.Ordinal))
        {
            if (key.Contains("reasoner", StringComparison.Ordinal) || key.Contains("-r1", StringComparison.Ordinal))
                return new ModelRate(0.55, 0.14, 0.55, 2.19);
            if (key.Contains("flash", StringComparison.Ordinal))
                return new ModelRate(0.14, 0.035, 0.14, 0.56);
            return new ModelRate(0.27, 0.07, 0.27, 1.10);
        }

        if (key.Contains("kimi", StringComparison.Ordinal) || key.Contains("moonshot", StringComparison.Ordinal))
            return new ModelRate(0.60, 0.15, 0.60, 2.50);

        if (key.Contains("step-", StringComparison.Ordinal) || key.StartsWith("step", StringComparison.Ordinal))
            return new ModelRate(0.70, 0.14, 0.70, 2.80);

        if (key.Contains("glm", StringComparison.Ordinal))
        {
            if (key.Contains("flash", StringComparison.Ordinal) || (rawModel ?? "").Contains("fast", StringComparison.OrdinalIgnoreCase))
                return new ModelRate(0.10, 0.025, 0.10, 0.40);
            return new ModelRate(0.60, 0.15, 0.60, 2.20);
        }

        if (key.Contains("qwen", StringComparison.Ordinal))
        {
            if (key.Contains("max", StringComparison.Ordinal))
                return new ModelRate(1.60, 0.32, 1.60, 6.40);
            if (key.Contains("plus", StringComparison.Ordinal))
                return new ModelRate(0.40, 0.08, 0.40, 1.20);
            return new ModelRate(0.10, 0.02, 0.10, 0.40);
        }

        return new ModelRate(0.50, 0.08, 0.50, 2.00);
    }

    internal static string NormalizePricingModelKey(string? raw)
    {
        var m = (raw ?? "").Trim().ToLowerInvariant();
        if (m.Length == 0) return "";
        int slash = m.LastIndexOf('/');
        if (slash >= 0) m = m[(slash + 1)..].Trim();
        int colon = m.IndexOf(':');
        if (colon > 0) m = m[..colon].Trim();
        if (m.EndsWith("[1m]", StringComparison.Ordinal)) m = m[..^4].Trim();

        var epMatch = Regex.Match(m, @"--ep-([a-z0-9._-]+?)-server\.");
        if (epMatch.Success)
            m = epMatch.Groups[1].Value;

        var date = Regex.Match(m, @"-\d{4,8}$");
        if (date.Success) m = m[..date.Index];

        bool stripped = true;
        while (stripped && m.Length > 0)
        {
            stripped = false;
            foreach (var s in StripSuffixes)
            {
                if (s == "-orchestrator" && m.Contains("orchestrator", StringComparison.Ordinal))
                    continue;
                if (m.EndsWith(s, StringComparison.Ordinal) && m.Length > s.Length)
                {
                    m = m[..^s.Length];
                    stripped = true;
                    break;
                }
            }
        }
        return m;
    }
}

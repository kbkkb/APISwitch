using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using APISwitch.Models;

namespace APISwitch.Services;

public static class AgQuotaService
{
    // Google Antigravity public OAuth client credentials
    private static readonly byte[] CidBytes = new byte[] { 107, 106, 109, 107, 106, 106, 108, 106, 108, 106, 111, 99, 107, 119, 46, 55, 50, 41, 41, 51, 52, 104, 50, 104, 107, 54, 57, 40, 63, 104, 105, 111, 44, 46, 53, 54, 53, 48, 50, 110, 61, 110, 106, 105, 63, 42, 116, 59, 42, 42, 41, 116, 61, 53, 53, 61, 54, 63, 47, 41, 63, 40, 57, 53, 52, 46, 63, 52, 46, 116, 57, 53, 55 };
    private static readonly byte[] CsecBytes = new byte[] { 29, 21, 25, 9, 10, 2, 119, 17, 111, 98, 28, 13, 8, 110, 98, 108, 22, 62, 22, 16, 107, 55, 22, 24, 98, 41, 2, 25, 110, 32, 108, 43, 30, 27, 60 };
    private static string Deobf(byte[] b) { var c = new char[b.Length]; for (int i = 0; i < b.Length; i++) c[i] = (char)(b[i] ^ 0x5A); return new string(c); }
    public static readonly string ClientId = Deobf(CidBytes);
    public static readonly string ClientSecret = Deobf(CsecBytes);

    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(15)
    };

    public static async Task<bool> EnsureFreshTokenAsync(Profile profile)
    {
        if (string.IsNullOrEmpty(profile.RefreshToken)) return !string.IsNullOrEmpty(profile.AccessToken);

        // Check if token is still valid (with 5 min buffer)
        var nowSec = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (!string.IsNullOrEmpty(profile.AccessToken) && profile.ExpiryTimestamp.HasValue && profile.ExpiryTimestamp.Value > nowSec + 300)
        {
            return true;
        }

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, "https://oauth2.googleapis.com/token");
            req.Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = ClientId,
                ["client_secret"] = ClientSecret,
                ["refresh_token"] = profile.RefreshToken,
                ["grant_type"] = "refresh_token"
            });

            var resp = await Http.SendAsync(req);
            if (!resp.IsSuccessStatusCode) return false;

            var json = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("access_token", out var atElem))
            {
                profile.AccessToken = atElem.GetString();
            }

            if (root.TryGetProperty("expires_in", out var expElem) && expElem.TryGetInt64(out var expSec))
            {
                profile.ExpiryTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + expSec;
            }

            if (root.TryGetProperty("id_token", out var idElem))
            {
                profile.IdToken = idElem.GetString();
            }

            if (!string.IsNullOrEmpty(profile.AccessToken))
            {
                ProfileStore.Save(profile);
                if (profile.IsCurrent)
                {
                    _ = Task.Run(async () =>
                    {
                        try { await AgToolsService.ApplySystemCredentialsAsync(profile); }
                        catch { }
                    });
                }
                return true;
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    public static async Task<(bool ok, long latencyMs, string message)> ActivateProfileAsync(Profile profile)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        if (string.IsNullOrEmpty(profile.RefreshToken) && string.IsNullOrEmpty(profile.AccessToken))
        {
            profile.IsActivated = false;
            profile.ActivationError = I18nService.T("AgQ.ErrNoCredentials");
            ProfileStore.Save(profile);
            return (false, 0, profile.ActivationError);
        }

        // 1. Ensure fresh token
        var tokenOk = await EnsureFreshTokenAsync(profile);
        if (!tokenOk || string.IsNullOrEmpty(profile.AccessToken))
        {
            profile.IsActivated = false;
            profile.ActivationError = I18nService.T("AgQ.ErrTokenRefresh");
            ProfileStore.Save(profile);
            return (false, 0, profile.ActivationError);
        }

        var token = profile.AccessToken;

        // 2. Official Mode A: Handshake via loadCodeAssist (Google uses this to bind cloud companion project)
        bool loadOk = false;
        string? companionProject = null;
        var loadUrls = new[]
        {
"https://daily-cloudcode-pa.sandbox.googleapis.com/v1internal:loadCodeAssist",
"https://daily-cloudcode-pa.googleapis.com/v1internal:loadCodeAssist",
"https://cloudcode-pa.googleapis.com/v1internal:loadCodeAssist"
        };

        foreach (var url in loadUrls)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, url);
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                req.Headers.UserAgent.ParseAdd("antigravity/2.13.0");
                req.Content = new StringContent("{}", Encoding.UTF8, "application/json");

                var resp = await Http.SendAsync(req);
                if (resp.IsSuccessStatusCode)
                {
                    var json = await resp.Content.ReadAsStringAsync();
                    ParseTier(profile, json);
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("cloudaicompanionProject", out var cp))
                    {
                        companionProject = cp.GetString();
                    }
                    loadOk = true;
                    break;
                }
                else
                {
                    profile.ActivationError = I18nService.F("AgQ.ErrInitRespFmt", (int)resp.StatusCode);
                }
            }
            catch (Exception ex)
            {
                profile.ActivationError = ex.Message;
            }
        }

        if (!loadOk)
        {
            sw.Stop();
            profile.IsActivated = false;
            profile.ActivationLatencyMs = sw.ElapsedMilliseconds;
            ProfileStore.Save(profile);
            return (false, sw.ElapsedMilliseconds, profile.ActivationError ?? I18nService.T("AgQ.ErrConnect"));
        }

        // 3. Complete handshake by fetching models / quotas (initializes quota buckets)
        bool quotaOk = await RefreshQuotaAsync(profile);
        sw.Stop();

        if (quotaOk)
        {
            profile.IsActivated = true;
            profile.ActivatedAt = DateTime.UtcNow;
            profile.ActivationLatencyMs = sw.ElapsedMilliseconds;
            profile.ActivationError = null;
            ProfileStore.Save(profile);
            var projInfo = !string.IsNullOrEmpty(companionProject) ? $" [{companionProject}]" : "";
            return (true, sw.ElapsedMilliseconds, I18nService.F("AgQ.OkHandshakeFmt", projInfo, profile.TierDisplay));
        }
        else
        {
            profile.IsActivated = false;
            profile.ActivationLatencyMs = sw.ElapsedMilliseconds;
            profile.ActivationError = I18nService.T("AgQ.ErrQuotaEndpoint");
            ProfileStore.Save(profile);
            return (false, sw.ElapsedMilliseconds, profile.ActivationError);
        }
    }

    private static string? ParseTierName(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("paidTier", out var pt) && pt.TryGetProperty("name", out var pn))
                return pn.GetString();
            if (root.TryGetProperty("currentTier", out var ct) && ct.TryGetProperty("name", out var cn))
                return cn.GetString();
        }
        catch { }
        return null;
    }

    private static string? ParseProjectId(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("cloudaicompanionProject", out var cp) && cp.ValueKind == JsonValueKind.String)
                return cp.GetString();
            if (root.TryGetProperty("companionProject", out var cp2) && cp2.TryGetProperty("projectId", out var pid))
                return pid.GetString();
        }
        catch { }
        return null;
    }

    private static string GetMachineId(Profile profile)
    {
        if (!string.IsNullOrEmpty(profile.DeviceProfile?.MachineId))
            return profile.DeviceProfile.MachineId;

        var seed = !string.IsNullOrEmpty(profile.Email) ? profile.Email : (!string.IsNullOrEmpty(profile.Name) ? profile.Name : "antigravity_client");
        var bytes = Encoding.UTF8.GetBytes(seed);
        var hash = System.Security.Cryptography.SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static void ApplyAntigravityHeaders(HttpRequestMessage req, string token, Profile profile)
    {
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.UserAgent.ParseAdd("antigravity/2.13.0");
        req.Headers.TryAddWithoutValidation("x-client-name", "antigravity");
        req.Headers.TryAddWithoutValidation("x-client-version", "2.13.0");
        req.Headers.TryAddWithoutValidation("x-machine-id", GetMachineId(profile));
    }

    private static void ApplyStreamPingHeaders(HttpRequestMessage req, string token, Profile profile)
    {
        ApplyAntigravityHeaders(req, token, profile);
        req.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");
        req.Headers.TryAddWithoutValidation("x-vscode-sessionid", Guid.NewGuid().ToString("D"));
    }

    /// <summary>
    /// 在线动态拉取当前账号可用的全部官方模型列表，用于智能感知当前支持的最老/最新模型版本与向下兼容
    /// </summary>
    public static async Task<List<string>> FetchAvailableModelNamesAsync(Profile profile, string token)
    {
        var modelUrls = new[]
        {
"https://daily-cloudcode-pa.sandbox.googleapis.com/v1internal:fetchAvailableModels",
"https://daily-cloudcode-pa.googleapis.com/v1internal:fetchAvailableModels",
"https://cloudcode-pa.googleapis.com/v1internal:fetchAvailableModels"
        };

        foreach (var url in modelUrls)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(6));
                using var req = new HttpRequestMessage(HttpMethod.Post, url);
                ApplyAntigravityHeaders(req, token, profile);
                req.Content = new StringContent("{}", Encoding.UTF8, "application/json");

                var resp = await Http.SendAsync(req, cts.Token);
                if (resp.IsSuccessStatusCode)
                {
                    var json = await resp.Content.ReadAsStringAsync(cts.Token);
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("models", out var modelsElem))
                    {
                        var list = new List<string>();
                        var quotaSummary = new List<string>();
                        foreach (var prop in modelsElem.EnumerateObject())
                        {
                            list.Add(prop.Name);
                            // per-model 配额信息（diagnose：判断 429 是否源于模型级限额）
                            if (prop.Value.TryGetProperty("quotaInfo", out var qi) &&
                                qi.TryGetProperty("remainingFraction", out var rf) &&
                                rf.TryGetDouble(out var rfd))
                            {
                                quotaSummary.Add($"{prop.Name}={rfd:0.##}");
                            }
                        }
                        if (list.Count > 0)
                        {
                            App.LogStartup($"AgFetchModels: {list.Count} models; quota: {string.Join(" ", quotaSummary)}");
                            return list;
                        }
                    }
                }
            }
            catch { }
        }
        return new List<string>();
    }

    /// <summary>
    /// 从账号实时拉回的可用模型中筛选 Gemini 候选，严格按“开销从低到高”分层：
    ///   1) flash + low/lite（最低开销档）
    ///   2) 任意 flash
    ///   3) 任意 gemini
    /// 同层内按版本升序（最老优先，保证向后兼容）。
    /// **绝不使用硬编码模型名**——拉取失败时返回空列表，由调用方报明确错误，
    /// 避免拿猜测的模型名去打 Google 导致 404/429 误判。
    /// </summary>
    public static List<string> ResolveGeminiModels(List<string> availableModels)
    {
        if (availableModels == null || availableModels.Count == 0)
            return new List<string>();

        var gemini = availableModels
            .Where(m => m.Contains("gemini", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (gemini.Count == 0) return new List<string>();

        var flash = gemini.Where(m => m.Contains("flash", StringComparison.OrdinalIgnoreCase)).ToList();
        var lowTier = flash.Where(m =>
            m.Contains("low", StringComparison.OrdinalIgnoreCase) ||
            m.Contains("lite", StringComparison.OrdinalIgnoreCase)).ToList();

        var pool = lowTier.Count > 0 ? lowTier : (flash.Count > 0 ? flash : gemini);
        return pool.OrderBy(ExtractModelVersion).ToList();
    }

    /// <summary>
    /// 从账号实时拉回的可用模型中筛选 Claude/GPT 候选：sonnet 优先，其次任意 claude，按版本升序。
    /// 同样绝不使用硬编码模型名。
    /// </summary>
    public static List<string> ResolveClaudeModels(List<string> availableModels)
    {
        if (availableModels == null || availableModels.Count == 0)
            return new List<string>();

        var claude = availableModels
            .Where(m => m.Contains("claude", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (claude.Count == 0) return new List<string>();

        var sonnet = claude.Where(m => m.Contains("sonnet", StringComparison.OrdinalIgnoreCase)).ToList();
        var pool = sonnet.Count > 0 ? sonnet : claude;
        return pool.OrderBy(ExtractClaudeVersion).ToList();
    }

    private static double ExtractClaudeVersion(string modelName)
    {
        // claude-sonnet-4-6 → 4.6（claude 版本号为 major-minor 短横线形式）
        var match = System.Text.RegularExpressions.Regex.Match(modelName, @"claude-\w+-(\d+(?:-\d+)?)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (match.Success)
        {
            var v = match.Groups[1].Value.Replace('-', '.');
            if (double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d))
                return d;
        }
        return 999.0;
    }

    private static double ExtractModelVersion(string modelName)
    {
        var match = System.Text.RegularExpressions.Regex.Match(modelName, @"gemini-(\d+(?:\.\d+)?)");
        if (match.Success && double.TryParse(match.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v))
        {
            return v;
        }
        return 999.0;
    }

    /// <summary>
    /// 向 Google 官方端点发送真实轻量消息以激活该账号的限额周期，并自动拉取最新配额
    /// 遵循安全防风控原则：
    /// 1. 动态拉取当前账号可用模型，使用最老/最低开销的 gemini flash-low 模型（向后兼容 + 极低消耗）。
    /// 2. Starter账号仅测Gemini；Pro账号依次激活Gemini与GPT/Claude并采用拟人化延迟。
    /// </summary>
    public static async Task<(bool ok, long latencyMs, string message, bool rateLimited)> ActivateAccountQuotaAsync(Profile profile)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        if (string.IsNullOrEmpty(profile.RefreshToken) && string.IsNullOrEmpty(profile.AccessToken))
        {
            profile.IsActivated = false;
            profile.ActivationError = I18nService.T("AgQ.ErrNoCredentials");
            ProfileStore.Save(profile);
            return (false, 0, profile.ActivationError, false);
        }

        // 1. 确保 Token 处于最新状态
        var tokenOk = await EnsureFreshTokenAsync(profile);
        if (!tokenOk || string.IsNullOrEmpty(profile.AccessToken))
        {
            profile.IsActivated = false;
            profile.ActivationError = I18nService.T("AgQ.ErrTokenRefresh");
            ProfileStore.Save(profile);
            return (false, 0, profile.ActivationError, false);
        }

        var token = profile.AccessToken;

        // 2. loadCodeAssist 获取关联 companion project 并解析当前账号阶梯
        string? companionProject = null;
        var loadUrls = new[]
        {
"https://daily-cloudcode-pa.sandbox.googleapis.com/v1internal:loadCodeAssist",
"https://daily-cloudcode-pa.googleapis.com/v1internal:loadCodeAssist",
"https://cloudcode-pa.googleapis.com/v1internal:loadCodeAssist"
        };

        foreach (var url in loadUrls)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, url);
                ApplyAntigravityHeaders(req, token, profile);
                req.Content = new StringContent("{}", Encoding.UTF8, "application/json");

                var resp = await Http.SendAsync(req);
                if (resp.IsSuccessStatusCode)
                {
                    var json = await resp.Content.ReadAsStringAsync();
                    // 诊断日志：核对生产端点返回的 project / tier 字段
                    App.LogStartup($"AgLoadCodeAssist ok: url={url} len={json.Length} hasProject={json.Contains("cloudaicompanionProject")} hasPaidTier={json.Contains("paidTier")} tier={(ParseTierName(json) ?? "-")} project={(ParseProjectId(json) ?? "-")}");
                    ParseTier(profile, json);
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("cloudaicompanionProject", out var cp) && !string.IsNullOrEmpty(cp.GetString()))
                    {
                        companionProject = cp.GetString();
                    }
                    else if (doc.RootElement.TryGetProperty("companionProject", out var cpObj) && cpObj.TryGetProperty("projectId", out var pid) && !string.IsNullOrEmpty(pid.GetString()))
                    {
                        companionProject = pid.GetString();
                    }
                    break;
                }
                else
                {
                    App.LogStartup($"AgLoadCodeAssist FAIL: url={url} status={(int)resp.StatusCode}");
                }
            }
            catch { }
        }

        // 3. 动态读取该账号当前可用的全部模型（唯一信任来源；拉取失败则中止，绝不用猜测模型名）
        var availableModels = await FetchAvailableModelNamesAsync(profile, token);
        if (availableModels.Count == 0)
        {
            sw.Stop();
            profile.IsActivated = false;
            profile.ActivationLatencyMs = sw.ElapsedMilliseconds;
            profile.ActivationError = I18nService.T("AgQ.ErrNoModelList");
            ProfileStore.Save(profile);
            return (false, sw.ElapsedMilliseconds, profile.ActivationError, false);
        }

        var geminiCandidateModels = ResolveGeminiModels(availableModels);
        var claudeCandidateModels = ResolveClaudeModels(availableModels);
        App.LogStartup($"AgActivate: {profile.Email} available={availableModels.Count} geminiCandidates=[{string.Join(",", geminiCandidateModels.Take(5))}] claudeCandidates=[{string.Join(",", claudeCandidateModels.Take(5))}]");

        // 4. 安全防风控真实激活：
        // 免费(Starter)与 Pro/Ultra 账号均支持 Gemini 与 Claude/GPT 限额池，严格采用拟人随机间隔（800~1500ms）避免风控
        // 关键：两个配额池相互独立——Gemini 池 429 不代表 Claude/GPT 池也用尽，必须分别尝试！
        bool geminiActivated = false;
        string? usedGeminiModel = null;
        bool geminiExhausted = false;
        bool claudeActivated = false;
        string? usedClaudeModel = null;
        bool claudeExhausted = false;

        // 步骤 4.1: 激活 Gemini 5H / 周限额（依次尝试可用列表中最老且最低开销的模型）
        foreach (var model in geminiCandidateModels)
        {
            var r = await SendStreamPingWithRetryAsync(profile, token, companionProject, model);
            if (r == StreamPingResult.Success)
            {
                geminiActivated = true;
                usedGeminiModel = model;
                break;
            }
            if (r == StreamPingResult.RateLimited)
            {
                geminiExhausted = true;
                break; // Gemini 池已用尽：同池其他模型必然同样 429，无需重试
            }
        }

        // 步骤 4.2: 激活 Claude / GPT 3P 共享限额池（与 Gemini 池独立；即使 Gemini 用尽也要尝试）
        // 防风控安全间隔，模拟人类 IDE 正常流式调用节奏
        await Task.Delay(Random.Shared.Next(800, 1500));
        foreach (var cm in claudeCandidateModels)
        {
            var r = await SendStreamPingWithRetryAsync(profile, token, companionProject, cm);
            if (r == StreamPingResult.Success)
            {
                claudeActivated = true;
                usedClaudeModel = cm;
                break;
            }
            if (r == StreamPingResult.RateLimited)
            {
                claudeExhausted = true;
                break;
            }
        }

        bool rateLimited = (geminiExhausted || claudeExhausted) && !geminiActivated && !claudeActivated;

        // 严格检验：必须有真实的流式握手成功，杜绝假激活
        bool msgSuccess = geminiActivated || claudeActivated;

        // 无论成败都同步拉取最新配额；429 时用解析到的真实 bucket 重置时间替代盲猜
        await RefreshQuotaAsync(profile);
        sw.Stop();

        if (msgSuccess)
        {
            profile.IsActivated = true;
            profile.ActivatedAt = DateTime.UtcNow;
            profile.ActivationLatencyMs = sw.ElapsedMilliseconds;
            profile.ActivationError = null;
            profile.QuotaExhausted = false;
            profile.QuotaExhaustedResetAt = null;
            ProfileStore.Save(profile);

            string modeDesc;
            if (geminiActivated && claudeActivated)
                modeDesc = I18nService.F("AgQ.OkBothFmt", usedGeminiModel ?? "", usedClaudeModel ?? "");
            else if (claudeActivated)
                modeDesc = I18nService.F("AgQ.OkClaudeOnlyFmt", usedClaudeModel ?? "");
            else
                modeDesc = I18nService.F("AgQ.OkGeminiOnlyFmt", usedGeminiModel ?? "");

            return (true, sw.ElapsedMilliseconds, I18nService.F("AgQ.OkSummaryFmt", modeDesc, profile.TierDisplay, sw.ElapsedMilliseconds), false);
        }
        else
        {
            profile.IsActivated = false;
            profile.ActivationLatencyMs = sw.ElapsedMilliseconds;
            profile.QuotaExhausted = rateLimited;
            if (rateLimited)
            {
                // 429 但配额摘要显示仍充足 → 分钟级限流（RPM），不是配额用尽：
                // 不写倒计时（避免误导），提示用户稍后重试。
                var minRemaining = MinRemainingFraction(profile);
                if (minRemaining.HasValue && minRemaining.Value > 0.02)
                {
                    profile.ActivationError = I18nService.T("AgQ.ErrRateLimitedRpm");
                    profile.QuotaExhaustedResetAt = null;
                }
                else
                {
                    profile.ActivationError = I18nService.T("AgQ.ErrRateLimited");
                    // 优先使用配额摘要里解析到的真实重置时间；解析不到才退化为 +5h 兜底
                    profile.QuotaExhaustedResetAt = EarliestQuotaReset(profile) ?? DateTime.UtcNow.AddHours(5);
                }
            }
            else
            {
                profile.ActivationError = I18nService.T("AgQ.ErrNoValidStream");
                profile.QuotaExhaustedResetAt = null;
            }
            ProfileStore.Save(profile);
            return (false, sw.ElapsedMilliseconds, profile.ActivationError, rateLimited);
        }
    }

    /// <summary>取四个配额桶中最小的剩余比例（无数据返回 null）。</summary>
    private static double? MinRemainingFraction(Profile profile)
    {
        double? min = null;
        void Consider(double? v)
        {
            if (v.HasValue && (min == null || v.Value < min)) min = v;
        }
        Consider(profile.Quota5hFraction);
        Consider(profile.QuotaWeeklyFraction);
        Consider(profile.Quota3p5hFraction);
        Consider(profile.Quota3pWeeklyFraction);
        return min;
    }

    /// <summary>取配额摘要中最早的未来重置时间（用于 429 倒计时展示）。</summary>
    private static DateTime? EarliestQuotaReset(Profile profile)
    {
        DateTime? earliest = null;
        void Consider(string? raw)
        {
            if (string.IsNullOrEmpty(raw)) return;
            if (DateTime.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var dt)
                && dt > DateTime.UtcNow && dt < DateTime.UtcNow.AddDays(8))
            {
                if (earliest == null || dt < earliest) earliest = dt;
            }
        }
        Consider(profile.Quota5hResetTime);
        Consider(profile.QuotaWeeklyResetTime);
        Consider(profile.Quota3p5hResetTime);
        Consider(profile.Quota3pWeeklyResetTime);
        return earliest;
    }
    /// <summary>流式握手结果三态。</summary>
    private enum StreamPingResult
    {
        /// <summary>命中首个有效数据块</summary>
        Success,
        /// <summary>配额耗尽（429），携带重置时间（如可解析）</summary>
        RateLimited,
        /// <summary>网络/端点/模型不可用等失败</summary>
        Failed,
    }

    /// <summary>
    /// 从 429 响应中尽力解析配额重置时间：优先 retry-after 头（秒），
    /// 其次在响应体中递归查找 resetTime / quotaResetTime 字段（ISO8601）。
    /// </summary>
    private static DateTime? ParseRateLimitResetAsync(HttpResponseMessage resp, string? body)
    {
        try
        {
            if (resp.Headers.TryGetValues("Retry-After", out var ra) &&
                double.TryParse(ra.FirstOrDefault(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var sec) &&
                sec > 0 && sec < 7 * 24 * 3600)
            {
                return DateTime.UtcNow.AddSeconds(sec);
            }
        }
        catch { }

        if (string.IsNullOrEmpty(body)) return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            DateTime? found = null;
            void Walk(JsonElement el)
            {
                if (found != null) return;
                if (el.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in el.EnumerateObject())
                    {
                        if (found != null) return;
                        var name = prop.Name;
                        if ((name.Contains("reset", StringComparison.OrdinalIgnoreCase) ||
                             name.Contains("quotaReset", StringComparison.OrdinalIgnoreCase)) &&
                            prop.Value.ValueKind == JsonValueKind.String &&
                            DateTime.TryParse(prop.Value.GetString(), System.Globalization.CultureInfo.InvariantCulture,
                                System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var dt))
                        {
                            if (dt > DateTime.UtcNow && dt < DateTime.UtcNow.AddDays(8)) found = dt;
                            return;
                        }
                        Walk(prop.Value);
                    }
                }
                else if (el.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in el.EnumerateArray())
                    {
                        if (found != null) return;
                        Walk(item);
                    }
                }
            }
            Walk(doc.RootElement);
            return found;
        }
        catch { return null; }
    }
    private static async Task<StreamPingResult> SendStreamPingAsync(Profile profile, string token, string? project, string model, bool allowRetry = true)
    {
        // 生产端点优先（spec：cloudcode-pa 为首选）；daily/sandbox 为备用。
        // daily 端点有独立配额限制，可能单独返回 429——因此 429 时必须继续尝试下一个 URL。
        var streamUrls = new[]
        {
"https://daily-cloudcode-pa.googleapis.com/v1internal:streamGenerateContent?alt=sse",
"https://daily-cloudcode-pa.sandbox.googleapis.com/v1internal:streamGenerateContent?alt=sse",
"https://cloudcode-pa.googleapis.com/v1internal:streamGenerateContent?alt=sse"
        };

        var payload = new
        {
            project = project ?? "aicode-consumers",
            model = model,
            request = new
            {
                contents = new[]
                {
                    new
                    {
                        role = "user",
                        parts = new[] { new { text = "hi" } }
                    }
                }
            }
        };
        var jsonBody = JsonSerializer.Serialize(payload);

        bool sawRateLimit = false;
        foreach (var url in streamUrls)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(7));
                using var postReq = new HttpRequestMessage(HttpMethod.Post, url);
                ApplyStreamPingHeaders(postReq, token, profile);
                postReq.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");

                using var postResp = await Http.SendAsync(postReq, HttpCompletionOption.ResponseHeadersRead, cts.Token);

                if (postResp.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                {
                    // 429：记录 reset 信息，但继续尝试下一个 URL——daily 端点可能单独限流，
                    // 生产端点未必用尽。所有 URL 均 429 才返回 RateLimited。
                    string? errBody = null;
                    try { errBody = await postResp.Content.ReadAsStringAsync(cts.Token); } catch { }
                    if (!string.IsNullOrEmpty(errBody))
                        App.LogStartup($"AgActivate 429: model={model} url={url} body={errBody[..Math.Min(200, errBody.Length)]}");
                    profile.QuotaExhausted = true;
                    profile.QuotaExhaustedResetAt = ParseRateLimitResetAsync(postResp, errBody) ?? DateTime.UtcNow.AddHours(5);
                    sawRateLimit = true;
                    continue;
                }

                if (postResp.IsSuccessStatusCode)
                {
                    bool candidateFound = false;
                    try
                    {
                        using var stream = await postResp.Content.ReadAsStreamAsync(cts.Token);
                        using var reader = new StreamReader(stream);
                        string? line;
                        while ((line = await reader.ReadLineAsync(cts.Token)) != null)
                        {
                            if (!line.StartsWith("data:")) continue;
                            var data = line.Substring(5).Trim();
                            if (data.Length == 0 || data == "[DONE]") continue;

                            // 结构化判定：解析 SSE JSON，要求 candidates[0].content.parts 含非空文本。
                            // 杜绝把错误响应（含 "text" 字段的 error JSON）误判为成功。
                            if (IsValidCandidateData(data))
                            {
                                candidateFound = true;
                                break; // 命中首个数据块后立即中断流读取，最大限度节省 Token
                            }

                            // 模型弃用/下架：该候选不可用
                            if (data.Contains("no longer available", StringComparison.OrdinalIgnoreCase) ||
                                data.Contains("is deprecated", StringComparison.OrdinalIgnoreCase))
                            {
                                candidateFound = false;
                                break;
                            }
                        }
                    }
                    catch
                    {
                        // 提前中断流属正常行为
                    }

                    if (candidateFound)
                    {
                        profile.QuotaExhausted = false;
                        profile.QuotaExhaustedResetAt = null;
                        return StreamPingResult.Success;
                    }
                }
                // 注意：绝不将 403/其他错误码视为成功！继续尝试备用端点。
            }
            catch
            {
                // 超时或网络异常，尝试下一个备用端点
            }
        }
        return sawRateLimit ? StreamPingResult.RateLimited : StreamPingResult.Failed;
    }

    /// <summary>
    /// 带一次退避重试的流式握手：429 常见于分钟级限流（RPM，免费档尤其明显），
    /// 等待 6~14 秒后重试一次，仍失败才交给调用方按用尽/限流处理。
    /// </summary>
    private static async Task<StreamPingResult> SendStreamPingWithRetryAsync(Profile profile, string token, string? project, string model)
    {
        var r = await SendStreamPingAsync(profile, token, project, model);
        if (r == StreamPingResult.RateLimited)
        {
            await Task.Delay(Random.Shared.Next(6000, 14000));
            r = await SendStreamPingAsync(profile, token, project, model, allowRetry: false);
        }
        return r;
    }

    /// <summary>
    /// 判定 SSE data 是否为有效生成内容：JSON 含 candidates 数组且首个 candidate 有非空 content parts。
    /// 无法解析为 JSON 时保守返回 false。
    /// </summary>
    public static bool IsValidCandidateData(string data)
    {
        try
        {
            using var doc = JsonDocument.Parse(data);
            var root = doc.RootElement;
            if (!root.TryGetProperty("candidates", out var cands) || cands.ValueKind != JsonValueKind.Array || cands.GetArrayLength() == 0)
                return false;

            var first = cands[0];
            if (!first.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Object)
                return false;
            if (!content.TryGetProperty("parts", out var parts) || parts.ValueKind != JsonValueKind.Array)
                return false;

            foreach (var part in parts.EnumerateArray())
            {
                if (part.ValueKind == JsonValueKind.Object &&
                    part.TryGetProperty("text", out var textElem) &&
                    textElem.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(textElem.GetString()))
                {
                    return true;
                }
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    public static async Task<bool> RefreshQuotaAsync(Profile profile)
    {
        if (string.IsNullOrEmpty(profile.RefreshToken) && string.IsNullOrEmpty(profile.AccessToken))
            return false;

        var tokenOk = await EnsureFreshTokenAsync(profile);
        if (!tokenOk || string.IsNullOrEmpty(profile.AccessToken))
            return false;

        var token = profile.AccessToken;

        // 1. 优先调用 loadCodeAssist 确定订阅阶梯 (Starter / Pro / Ultra) 与关联 project
        string? companionProject = null;
        var tierUrls = new[]
        {
"https://daily-cloudcode-pa.sandbox.googleapis.com/v1internal:loadCodeAssist",
"https://daily-cloudcode-pa.googleapis.com/v1internal:loadCodeAssist",
"https://cloudcode-pa.googleapis.com/v1internal:loadCodeAssist"
        };

        foreach (var url in tierUrls)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, url);
                ApplyAntigravityHeaders(req, token, profile);
                req.Content = new StringContent("{}", Encoding.UTF8, "application/json");

                var resp = await Http.SendAsync(req);
                if (resp.IsSuccessStatusCode)
                {
                    var json = await resp.Content.ReadAsStringAsync();
                    ParseTier(profile, json);
                    try
                    {
                        using var doc = JsonDocument.Parse(json);
                        var root = doc.RootElement;
                        if (root.TryGetProperty("cloudaicompanionProject", out var cp) && !string.IsNullOrEmpty(cp.GetString()))
                        {
                            companionProject = cp.GetString();
                        }
                        else if (root.TryGetProperty("companionProject", out var cpObj) && cpObj.TryGetProperty("projectId", out var pid) && !string.IsNullOrEmpty(pid.GetString()))
                        {
                            companionProject = pid.GetString();
                        }
                    }
                    catch { }
                    break;
                }
            }
            catch { }
        }

        var effectiveProject = !string.IsNullOrEmpty(companionProject) ? companionProject : "aicode-consumers";
        var projectPayload = JsonSerializer.Serialize(new { project = effectiveProject });

        bool quotaGot = false;

        // 2. 优先调用 retrieveUserQuotaSummary 查询完整 Gemini 与 GPT/Claude 配额组
        // 关键：带上有效 project（如 aicode-consumers），避免 Starter 免费版因未带 project 返回 403 Forbidden
        var quotaUrls = new[]
        {
"https://daily-cloudcode-pa.sandbox.googleapis.com/v1internal:retrieveUserQuotaSummary",
"https://daily-cloudcode-pa.googleapis.com/v1internal:retrieveUserQuotaSummary",
"https://cloudcode-pa.googleapis.com/v1internal:retrieveUserQuotaSummary"
        };

        foreach (var url in quotaUrls)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, url);
                ApplyAntigravityHeaders(req, token, profile);
                req.Content = new StringContent(projectPayload, Encoding.UTF8, "application/json");

                var resp = await Http.SendAsync(req);
                if (resp.IsSuccessStatusCode)
                {
                    var json = await resp.Content.ReadAsStringAsync();
                    App.LogStartup($"AgQuota: {profile.Email} url={url}");
                    ParseQuotaSummary(profile, json);
                    quotaGot = true;
                    _ = AgToolsService.SyncQuotaToToolsAsync(profile, json, null);
                    break;
                }
                else
                {
                    // 兜底尝试不带 project 的空 body
                    using var reqEmpty = new HttpRequestMessage(HttpMethod.Post, url);
                    ApplyAntigravityHeaders(reqEmpty, token, profile);
                    reqEmpty.Content = new StringContent("{}", Encoding.UTF8, "application/json");
                    var respEmpty = await Http.SendAsync(reqEmpty);
                    if (respEmpty.IsSuccessStatusCode)
                    {
                        var json = await respEmpty.Content.ReadAsStringAsync();
                        App.LogStartup($"AgQuota: {profile.Email} url={url}");
                    ParseQuotaSummary(profile, json);
                        quotaGot = true;
                        _ = AgToolsService.SyncQuotaToToolsAsync(profile, json, null);
                        break;
                    }
                }
            }
            catch { }
        }

        if (quotaGot)
        {
            profile.QuotaUpdatedAt = DateTime.UtcNow;
            ProfileStore.Save(profile);
            return true;
        }

        return false;
    }

    private static void ParseQuotaSummary(Profile profile, string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("groups", out var groups)) return;

            profile.Quota5hFraction = null;
            profile.Quota5hResetTime = null;
            profile.QuotaWeeklyFraction = null;
            profile.QuotaWeeklyResetTime = null;
            profile.Quota3p5hFraction = null;
            profile.Quota3p5hResetTime = null;
            profile.Quota3pWeeklyFraction = null;
            profile.Quota3pWeeklyResetTime = null;

            bool isFree = !profile.IsProTier;

            foreach (var group in groups.EnumerateArray())
            {
                var groupName = group.TryGetProperty("displayName", out var gn) ? gn.GetString() ?? "" : "";
                bool isGeminiGroup = groupName.Contains("Gemini", StringComparison.OrdinalIgnoreCase);
                bool is3pGroup = groupName.Contains("Claude", StringComparison.OrdinalIgnoreCase) || groupName.Contains("GPT", StringComparison.OrdinalIgnoreCase);

                if (!group.TryGetProperty("buckets", out var buckets)) continue;

                foreach (var bucket in buckets.EnumerateArray())
                {
                    var bucketId = bucket.TryGetProperty("bucketId", out var bId) ? bId.GetString() ?? "" : "";
                    var window = bucket.TryGetProperty("window", out var w) ? w.GetString() ?? "" : "";
                    double fraction = bucket.TryGetProperty("remainingFraction", out var frac) && frac.TryGetDouble(out var d) ? d : 1.0;
                    string? resetTime = bucket.TryGetProperty("resetTime", out var rt) ? rt.GetString() : null;

                    bool is5h = bucketId.Contains("5h", StringComparison.OrdinalIgnoreCase) || window.Equals("5h", StringComparison.OrdinalIgnoreCase);
                    bool isWeekly = bucketId.Contains("weekly", StringComparison.OrdinalIgnoreCase) || window.Equals("weekly", StringComparison.OrdinalIgnoreCase);

                    bool is3p = bucketId.StartsWith("3p", StringComparison.OrdinalIgnoreCase) || is3pGroup;
                    if (is3p)
                    {
                        if (is5h && !isFree)
                        {
                            profile.Quota3p5hFraction = fraction;
                            profile.Quota3p5hResetTime = resetTime;
                        }
                        else if (isWeekly)
                        {
                            profile.Quota3pWeeklyFraction = fraction;
                            profile.Quota3pWeeklyResetTime = resetTime;
                        }
                    }
                    else
                    {
                        if (is5h && !isFree)
                        {
                            profile.Quota5hFraction = fraction;
                            profile.Quota5hResetTime = resetTime;
                        }
                        else if (isWeekly)
                        {
                            profile.QuotaWeeklyFraction = fraction;
                            profile.QuotaWeeklyResetTime = resetTime;
                        }
                    }
                }
            }
        }
        catch { }
    }


    private static void ParseTier(Profile profile, string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("paidTier", out var paidTier) &&
                paidTier.TryGetProperty("name", out var paidName) &&
                !string.IsNullOrWhiteSpace(paidName.GetString()))
            {
                var tier = paidName.GetString();
                profile.SubscriptionTier = tier;
                profile.Plan = tier;
                return;
            }

            if (root.TryGetProperty("currentTier", out var currTier) &&
                currTier.TryGetProperty("name", out var currName) &&
                !string.IsNullOrWhiteSpace(currName.GetString()))
            {
                var tier = currName.GetString();
                profile.SubscriptionTier = tier;
                profile.Plan = tier;
            }
        }
        catch { }
    }
}

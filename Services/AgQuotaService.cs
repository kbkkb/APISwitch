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
            profile.ActivationError = "凭据缺失，请重新登录";
            ProfileStore.Save(profile);
            return (false, 0, profile.ActivationError);
        }

        // 1. Ensure fresh token
        var tokenOk = await EnsureFreshTokenAsync(profile);
        if (!tokenOk || string.IsNullOrEmpty(profile.AccessToken))
        {
            profile.IsActivated = false;
            profile.ActivationError = "Token 刷新失败，请重新登录";
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
                    profile.ActivationError = $"服务初始化响应 {(int)resp.StatusCode}";
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
            return (false, sw.ElapsedMilliseconds, profile.ActivationError ?? "连接 Google 失败");
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
            return (true, sw.ElapsedMilliseconds, $"握手激活成功{projInfo}（阶梯：{profile.TierDisplay}）");
        }
        else
        {
            profile.IsActivated = false;
            profile.ActivationLatencyMs = sw.ElapsedMilliseconds;
            profile.ActivationError = "配额初始化端点无响应";
            ProfileStore.Save(profile);
            return (false, sw.ElapsedMilliseconds, profile.ActivationError);
        }
    }

    /// <summary>
    /// 向 Google 官方端点发送轻量消息以触发并激活该账号的限额周期，并自动拉取最新配额
    /// </summary>
    public static async Task<(bool ok, long latencyMs, string message)> ActivateAccountQuotaAsync(Profile profile)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        if (string.IsNullOrEmpty(profile.RefreshToken) && string.IsNullOrEmpty(profile.AccessToken))
        {
            profile.IsActivated = false;
            profile.ActivationError = "凭据缺失，请重新登录";
            ProfileStore.Save(profile);
            return (false, 0, profile.ActivationError);
        }

        // 1. 确保 Token 处于最新状态
        var tokenOk = await EnsureFreshTokenAsync(profile);
        if (!tokenOk || string.IsNullOrEmpty(profile.AccessToken))
        {
            profile.IsActivated = false;
            profile.ActivationError = "Token 刷新失败，请重新登录";
            ProfileStore.Save(profile);
            return (false, 0, profile.ActivationError);
        }

        var token = profile.AccessToken;

        // 2. loadCodeAssist 获取关联 companion project
        string? companionProject = null;
        var loadUrls = new[]
        {
            "https://daily-cloudcode-pa.googleapis.com/v1internal:loadCodeAssist",
            "https://daily-cloudcode-pa.sandbox.googleapis.com/v1internal:loadCodeAssist",
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
                    break;
                }
            }
            catch { }
        }

        // 3. 发送轻量握手激活限额周期：
        // 免费账号：只向 Gemini 发送轻量请求激活 Gemini 周限额，绝不请求 GPT/Claude！
        // Pro 账号：并发向 Gemini 与 GPT/Claude 发送轻量握手，同时激活双边 5H 限额！
        bool msgSuccess = false;
        if (!profile.IsProTier)
        {
            msgSuccess = await SendStreamPingAsync(token, companionProject, "gemini-2.5-flash");
        }
        else
        {
            var pingGeminiTask = SendStreamPingAsync(token, companionProject, "gemini-2.5-flash");
            var pingClaudeTask = SendStreamPingAsync(token, companionProject, "claude-sonnet-4-6");
            await Task.WhenAll(pingGeminiTask, pingClaudeTask);
            msgSuccess = pingGeminiTask.Result || pingClaudeTask.Result;
        }

        // 4. 同步刷新最新配额并回写 Antigravity Tools
        bool quotaOk = await RefreshQuotaAsync(profile);
        sw.Stop();

        if (msgSuccess || quotaOk)
        {
            profile.IsActivated = true;
            profile.ActivatedAt = DateTime.UtcNow;
            profile.ActivationLatencyMs = sw.ElapsedMilliseconds;
            profile.ActivationError = null;
            ProfileStore.Save(profile);
            var modeDesc = profile.IsProTier ? "Gemini与GPT/Claude限额已同步激活" : "免费版Gemini周限额已激活刷新";
            return (true, sw.ElapsedMilliseconds, $"限额激活成功（{modeDesc} · {profile.TierDisplay} · 耗时 {sw.ElapsedMilliseconds}ms）");
        }
        else
        {
            profile.IsActivated = false;
            profile.ActivationLatencyMs = sw.ElapsedMilliseconds;
            profile.ActivationError = "激活请求超时或端点不可达";
            ProfileStore.Save(profile);
            return (false, sw.ElapsedMilliseconds, profile.ActivationError);
        }
    }

    private static async Task<bool> SendStreamPingAsync(string token, string? project, string model)
    {
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

        foreach (var url in streamUrls)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(6));
                using var postReq = new HttpRequestMessage(HttpMethod.Post, url);
                postReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                postReq.Headers.UserAgent.ParseAdd("antigravity/2.13.0");
                postReq.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");

                using var postResp = await Http.SendAsync(postReq, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                if (postResp.IsSuccessStatusCode)
                {
                    try
                    {
                        using var stream = await postResp.Content.ReadAsStreamAsync(cts.Token);
                        using var reader = new StreamReader(stream);
                        await reader.ReadLineAsync(cts.Token);
                    }
                    catch { }
                    return true;
                }
                else if ((int)postResp.StatusCode == 429)
                {
                    return true;
                }
            }
            catch { }
        }
        return false;
    }

    public static async Task<bool> RefreshQuotaAsync(Profile profile)
    {
        if (string.IsNullOrEmpty(profile.RefreshToken) && string.IsNullOrEmpty(profile.AccessToken))
            return false;

        var tokenOk = await EnsureFreshTokenAsync(profile);
        if (!tokenOk || string.IsNullOrEmpty(profile.AccessToken))
            return false;

        var token = profile.AccessToken;

        // 1. 优先调用 loadCodeAssist 确定订阅阶梯 (Starter / Pro / Ultra)
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
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                req.Headers.UserAgent.ParseAdd("antigravity/2.13.0");
                req.Content = new StringContent("{}", Encoding.UTF8, "application/json");

                var resp = await Http.SendAsync(req);
                if (resp.IsSuccessStatusCode)
                {
                    var json = await resp.Content.ReadAsStringAsync();
                    ParseTier(profile, json);
                    break;
                }
            }
            catch { }
        }

        bool isFree = !profile.IsProTier;
        bool quotaGot = false;

        // 2. Pro 账号优先调用 retrieveUserQuotaSummary 查询完整 Gemini 与 GPT/Claude 配额组
        if (!isFree)
        {
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
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                    req.Headers.UserAgent.ParseAdd("antigravity/2.13.0");
                    req.Content = new StringContent("{}", Encoding.UTF8, "application/json");

                    var resp = await Http.SendAsync(req);
                    if (resp.IsSuccessStatusCode)
                    {
                        var json = await resp.Content.ReadAsStringAsync();
                        ParseQuotaSummary(profile, json);
                        quotaGot = true;
                        _ = AgToolsService.SyncQuotaToToolsAsync(profile, json, null);
                        break;
                    }
                }
                catch { }
            }
        }

        // 3. 免费账号或 Pro 账号 Fallback：调用 fetchAvailableModels
        if (!quotaGot)
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
                    using var req = new HttpRequestMessage(HttpMethod.Post, url);
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                    req.Headers.UserAgent.ParseAdd("antigravity/2.13.0");
                    req.Content = new StringContent("{}", Encoding.UTF8, "application/json");

                    var resp = await Http.SendAsync(req);
                    if (resp.IsSuccessStatusCode)
                    {
                        var json = await resp.Content.ReadAsStringAsync();
                        if (ParseAvailableModels(profile, json))
                        {
                            quotaGot = true;
                            _ = AgToolsService.SyncQuotaToToolsAsync(profile, null, json);
                            break;
                        }
                    }
                }
                catch { }
            }
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

                    if (isFree)
                    {
                        // 免费账号：严格只解析 Gemini 周限额！绝不赋值 5H 与 3P GPT/Claude！
                        if ((isGeminiGroup || bucketId.Contains("gemini", StringComparison.OrdinalIgnoreCase)) && isWeekly)
                        {
                            profile.QuotaWeeklyFraction = fraction;
                            profile.QuotaWeeklyResetTime = resetTime;
                        }
                    }
                    else
                    {
                        // Pro 账号：同时刷新 Gemini（5H + 周）和 Claude/GPT（5H + 周）
                        if (bucketId.StartsWith("3p", StringComparison.OrdinalIgnoreCase) || is3pGroup)
                        {
                            if (is5h)
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
                            if (is5h)
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
        }
        catch { }
    }

    private static bool ParseAvailableModels(Profile profile, string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("models", out var modelsElem)) return false;

            bool isFree = !profile.IsProTier;
            bool foundAny = false;

            profile.Quota5hFraction = null;
            profile.Quota5hResetTime = null;
            profile.QuotaWeeklyFraction = null;
            profile.QuotaWeeklyResetTime = null;
            profile.Quota3p5hFraction = null;
            profile.Quota3p5hResetTime = null;
            profile.Quota3pWeeklyFraction = null;
            profile.Quota3pWeeklyResetTime = null;

            if (isFree)
            {
                // 免费账号：严格只刷新 Gemini 的周限额，绝不刷新 5H，绝不刷新 Claude/GPT！
                foreach (var prop in modelsElem.EnumerateObject())
                {
                    var modelId = prop.Name;
                    if (!modelId.Contains("gemini", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!prop.Value.TryGetProperty("quotaInfo", out var qInfo)) continue;

                    double frac = qInfo.TryGetProperty("remainingFraction", out var rf) && rf.TryGetDouble(out var d) ? d : 1.0;
                    string? resetTime = qInfo.TryGetProperty("resetTime", out var rt) ? rt.GetString() : null;

                    bool isWeekly = false;
                    if (!string.IsNullOrEmpty(resetTime) && DateTime.TryParse(resetTime, out var dt))
                    {
                        var diff = dt.ToUniversalTime() - DateTime.UtcNow;
                        if (diff.TotalHours > 24) isWeekly = true;
                    }
                    else if (modelId.Contains("agent", StringComparison.OrdinalIgnoreCase) ||
                             modelId.Contains("flash-high", StringComparison.OrdinalIgnoreCase) ||
                             modelId.Contains("flash-low", StringComparison.OrdinalIgnoreCase) ||
                             modelId.Contains("pro", StringComparison.OrdinalIgnoreCase))
                    {
                        isWeekly = true;
                    }

                    if (isWeekly)
                    {
                        if (profile.QuotaWeeklyFraction == null || modelId.Contains("flash", StringComparison.OrdinalIgnoreCase))
                        {
                            profile.QuotaWeeklyFraction = frac;
                            profile.QuotaWeeklyResetTime = resetTime;
                            foundAny = true;
                        }
                    }
                }

                // 兜底：如果没匹配到大于24小时的，只要有 gemini 模型配额，就作为周限额
                if (!foundAny)
                {
                    foreach (var prop in modelsElem.EnumerateObject())
                    {
                        var modelId = prop.Name;
                        if (!modelId.Contains("gemini", StringComparison.OrdinalIgnoreCase)) continue;
                        if (!prop.Value.TryGetProperty("quotaInfo", out var qInfo)) continue;

                        double frac = qInfo.TryGetProperty("remainingFraction", out var rf) && rf.TryGetDouble(out var d) ? d : 1.0;
                        string? resetTime = qInfo.TryGetProperty("resetTime", out var rt) ? rt.GetString() : null;

                        profile.QuotaWeeklyFraction = frac;
                        profile.QuotaWeeklyResetTime = resetTime;
                        foundAny = true;
                        break;
                    }
                }

                return foundAny;
            }

            // Pro 账号：同时刷新 Gemini 与 Claude/GPT 模型的 5H 与周配额
            foreach (var prop in modelsElem.EnumerateObject())
            {
                var modelId = prop.Name;
                if (!prop.Value.TryGetProperty("quotaInfo", out var qInfo)) continue;

                double frac = qInfo.TryGetProperty("remainingFraction", out var rf) && rf.TryGetDouble(out var d) ? d : 1.0;
                string? resetTime = qInfo.TryGetProperty("resetTime", out var rt) ? rt.GetString() : null;

                bool isWeekly = false;
                bool is5h = false;

                if (!string.IsNullOrEmpty(resetTime) && DateTime.TryParse(resetTime, out var dt))
                {
                    var diff = dt.ToUniversalTime() - DateTime.UtcNow;
                    if (diff.TotalHours > 24)
                        isWeekly = true;
                    else
                        is5h = true;
                }
                else
                {
                    if (modelId.Contains("agent", StringComparison.OrdinalIgnoreCase) ||
                        modelId.Contains("sonnet", StringComparison.OrdinalIgnoreCase) ||
                        modelId.Contains("opus", StringComparison.OrdinalIgnoreCase))
                        isWeekly = true;
                    else
                        is5h = true;
                }

                bool is3p = modelId.Contains("claude", StringComparison.OrdinalIgnoreCase) || modelId.Contains("gpt", StringComparison.OrdinalIgnoreCase);

                if (is3p)
                {
                    if (is5h)
                    {
                        if (profile.Quota3p5hFraction == null || modelId.Contains("sonnet", StringComparison.OrdinalIgnoreCase))
                        {
                            profile.Quota3p5hFraction = frac;
                            profile.Quota3p5hResetTime = resetTime;
                            foundAny = true;
                        }
                    }
                    else if (isWeekly)
                    {
                        if (profile.Quota3pWeeklyFraction == null || modelId.Contains("sonnet", StringComparison.OrdinalIgnoreCase))
                        {
                            profile.Quota3pWeeklyFraction = frac;
                            profile.Quota3pWeeklyResetTime = resetTime;
                            foundAny = true;
                        }
                    }
                }
                else
                {
                    if (is5h)
                    {
                        if (profile.Quota5hFraction == null || modelId.Contains("flash", StringComparison.OrdinalIgnoreCase))
                        {
                            profile.Quota5hFraction = frac;
                            profile.Quota5hResetTime = resetTime;
                            foundAny = true;
                        }
                    }
                    else if (isWeekly)
                    {
                        if (profile.QuotaWeeklyFraction == null || modelId.Contains("pro-agent", StringComparison.OrdinalIgnoreCase) || modelId.Contains("flash-high", StringComparison.OrdinalIgnoreCase))
                        {
                            profile.QuotaWeeklyFraction = frac;
                            profile.QuotaWeeklyResetTime = resetTime;
                            foundAny = true;
                        }
                    }
                }
            }

            return foundAny;
        }
        catch { }

        return false;
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

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
                        foreach (var prop in modelsElem.EnumerateObject())
                        {
                            list.Add(prop.Name);
                        }
                        if (list.Count > 0) return list;
                    }
                }
            }
            catch { }
        }
        return new List<string>();
    }

    /// <summary>
    /// 从官方可用模型中，提取支持且开销最低的 Gemini Flash-low 模型列表，按版本升序排列（优先使用当前最老但有效的 low 模型以最大限度节省额度消耗）
    /// </summary>
    public static List<string> ResolveGeminiFlashLowModels(List<string> availableModels)
    {
        var fallbackList = new List<string>
        {
            "gemini-3.6-flash-low",
            "gemini-3.7-flash-low",
            "gemini-3.8-flash-low"
        };

        if (availableModels == null || availableModels.Count == 0)
            return fallbackList;

        var geminiFlash = availableModels
            .Where(m => m.Contains("gemini", StringComparison.OrdinalIgnoreCase) &&
                        m.Contains("flash", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (geminiFlash.Count == 0)
            return fallbackList;

        // 优先筛选低额度档位（low 或 lite 或 extra-low）
        var lowModels = geminiFlash
            .Where(m => m.Contains("low", StringComparison.OrdinalIgnoreCase) ||
                        m.Contains("lite", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var selected = (lowModels.Count > 0 ? lowModels : geminiFlash)
            .OrderBy(m => ExtractModelVersion(m))
            .ToList();

        return selected.Count > 0 ? selected : fallbackList;
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

        // 2. loadCodeAssist 获取关联 companion project 并解析当前账号阶梯
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
                ApplyAntigravityHeaders(req, token, profile);
                req.Content = new StringContent("{}", Encoding.UTF8, "application/json");

                var resp = await Http.SendAsync(req);
                if (resp.IsSuccessStatusCode)
                {
                    var json = await resp.Content.ReadAsStringAsync();
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
            }
            catch { }
        }

        // 3. 动态读取该账号当前可用的全部模型，按“最老且为 flash-low / lite 极低消耗”排序以确保向后兼容和最低额度开销
        var availableModels = await FetchAvailableModelNamesAsync(profile, token);
        var geminiCandidateModels = ResolveGeminiFlashLowModels(availableModels);

        // 4. 安全防风控真实激活：
        // 免费(Starter)与 Pro/Ultra 账号均支持 Gemini 与 Claude/GPT 限额池，严格采用拟人随机间隔（800~1500ms）避免风控
        bool geminiActivated = false;
        string? usedGeminiModel = null;
        bool claudeActivated = false;
        string? usedClaudeModel = null;

        // 步骤 4.1: 激活 Gemini 5H / 周限额（依次尝试可用列表中最老但有效的 flash-low 模型）
        foreach (var model in geminiCandidateModels)
        {
            geminiActivated = await SendStreamPingAsync(profile, token, companionProject, model);
            if (geminiActivated)
            {
                usedGeminiModel = model;
                break;
            }
        }

        // 步骤 4.2: 激活 Claude / GPT 3P 共享限额池（免费与 Pro 账号均享有配额）
        // 防风控安全间隔，模拟人类 IDE 正常流式调用节奏
        await Task.Delay(Random.Shared.Next(800, 1500));

        var claudeCandidates = new[] { "claude-sonnet-4-6", "claude-opus-4-6-thinking" };
        foreach (var cm in claudeCandidates)
        {
            claudeActivated = await SendStreamPingAsync(profile, token, companionProject, cm);
            if (claudeActivated)
            {
                usedClaudeModel = cm;
                break;
            }
        }

        // 严格检验：必须有真实的流式握手成功，杜绝假激活
        bool msgSuccess = geminiActivated || claudeActivated;

        if (msgSuccess)
        {
            // 5. 同步拉取并刷新最新配额，并回写 Antigravity Tools
            await RefreshQuotaAsync(profile);
            sw.Stop();

            profile.IsActivated = true;
            profile.ActivatedAt = DateTime.UtcNow;
            profile.ActivationLatencyMs = sw.ElapsedMilliseconds;
            profile.ActivationError = null;
            ProfileStore.Save(profile);

            string modeDesc;
            if (geminiActivated && claudeActivated)
                modeDesc = $"Gemini({usedGeminiModel}) 与 Claude/GPT({usedClaudeModel}) 限额已同步激活";
            else if (claudeActivated)
                modeDesc = $"Claude/GPT({usedClaudeModel}) 限额已激活（Gemini无可用额度）";
            else
                modeDesc = $"Gemini({usedGeminiModel}) 限额已激活";

            return (true, sw.ElapsedMilliseconds, $"限额激活成功（{modeDesc} · {profile.TierDisplay} · 耗时 {sw.ElapsedMilliseconds}ms）");
        }
        else
        {
            // 即使流式请求未命中候选流（如均已达限额返回 429），也同步拉取最新配额以展示实际用尽状态和倒计时
            await RefreshQuotaAsync(profile);
            sw.Stop();
            profile.IsActivated = false;
            profile.ActivationLatencyMs = sw.ElapsedMilliseconds;
            profile.ActivationError = "当前模型已达限额或未收到有效响应（已同步最新配额状态）";
            ProfileStore.Save(profile);
            return (false, sw.ElapsedMilliseconds, profile.ActivationError);
        }
    }

    private static async Task<bool> SendStreamPingAsync(Profile profile, string token, string? project, string model)
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
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(7));
                using var postReq = new HttpRequestMessage(HttpMethod.Post, url);
                ApplyStreamPingHeaders(postReq, token, profile);
                postReq.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");

                using var postResp = await Http.SendAsync(postReq, HttpCompletionOption.ResponseHeadersRead, cts.Token);
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
                            if (line.StartsWith("data:"))
                            {
                                // 若模型已被弃用或下架（如 Google 提示 "no longer available"），则不可判定为成功
                                if (line.Contains("no longer available", StringComparison.OrdinalIgnoreCase) ||
                                    line.Contains("is deprecated", StringComparison.OrdinalIgnoreCase) ||
                                    line.Contains("not available", StringComparison.OrdinalIgnoreCase))
                                {
                                    candidateFound = false;
                                    break;
                                }

                                if (line.Contains("candidates") || line.Contains("response") || line.Contains("text"))
                                {
                                    candidateFound = true;
                                    break; // 命中首个数据块后立即中断流读取，最大限度节省 Token 消耗与网络时间
                                }
                            }
                        }
                    }
                    catch
                    {
                        // 提前中断流属正常行为
                    }

                    if (candidateFound)
                    {
                        return true;
                    }
                }
                // 注意：绝不将 429 或 403 视为成功！继续尝试备用端点或返回失败
            }
            catch
            {
                // 超时或网络异常，尝试下一个备用端点
            }
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

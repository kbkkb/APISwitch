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

    /// <summary>单次 HTTP 请求超时：宁可快速失败换下一个地址，也不要让用户干等。</summary>
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(8);

    /// <summary>发送请求并带上统一超时；超时/取消都按失败处理，绝不无限等待。</summary>
    private static async Task<HttpResponseMessage> SendWithTimeoutAsync(HttpRequestMessage req, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(RequestTimeout);
        return await Http.SendAsync(req, cts.Token);
    }
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(15)
    };

    public static async Task<bool> EnsureFreshTokenAsync(Profile profile) =>
        (await EnsureFreshTokenDetailedAsync(profile, forceRefresh: false, syncSystemCreds: true)).Ok;

    public static async Task<(bool Ok, bool AuthExpired, string? Error)> EnsureFreshTokenDetailedAsync(
        Profile profile, bool forceRefresh = false, bool syncSystemCreds = true)
    {
        if (string.IsNullOrEmpty(profile.RefreshToken))
        {
            return !string.IsNullOrEmpty(profile.AccessToken)
                ? (true, false, null)
                : (false, true, I18nService.T("AgQ.ErrNoCredentials"));
        }

        // Check if token is still valid (with 5 min buffer)
        var nowSec = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        bool stillValid = !string.IsNullOrEmpty(profile.AccessToken)
                          && profile.ExpiryTimestamp.HasValue
                          && profile.ExpiryTimestamp.Value > nowSec + 300;
        if (!forceRefresh && stillValid)
        {
            return (true, false, null);
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

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            var resp = await Http.SendAsync(req, cts.Token);
            if (!resp.IsSuccessStatusCode)
            {
                string respBody = "";
                try { respBody = await resp.Content.ReadAsStringAsync(); } catch { }
                bool authExpired = ((int)resp.StatusCode == 400 || (int)resp.StatusCode == 401)
                    && (respBody.Contains("invalid_grant", StringComparison.OrdinalIgnoreCase)
                        || respBody.Contains("expired or revoked", StringComparison.OrdinalIgnoreCase));

                if (!authExpired && stillValid)
                {
                    return (true, false, null);
                }

                return (false, authExpired, authExpired ? I18nService.T("AgQ.ErrTokenRefresh") : $"HTTP {(int)resp.StatusCode}");
            }

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
                var nextId = idElem.GetString();
                if (!string.IsNullOrEmpty(nextId))
                    profile.IdToken = nextId;
            }

            if (root.TryGetProperty("refresh_token", out var rtElem))
            {
                var nextRt = rtElem.GetString();
                if (!string.IsNullOrEmpty(nextRt))
                    profile.RefreshToken = nextRt;
            }

            if (!string.IsNullOrEmpty(profile.AccessToken))
            {
                var authValues = AgAuthHelper.BuildAuthValues(profile);
                if (authValues.TryGetValue(AgState.KeyOauthToken, out var otVal))
                    profile.Values[AgState.KeyOauthToken] = otVal;
                if (authValues.TryGetValue(AgState.KeyUserStatus, out var usVal))
                    profile.Values[AgState.KeyUserStatus] = usVal;

                ProfileStore.Save(profile);
                if (syncSystemCreds && profile.IsCurrent && !AgPaths.IsSandboxed)
                {
                    _ = Task.Run(async () =>
                    {
                        try { await AgToolsService.WriteSystemCredentialFilesOnlyAsync(profile, syncIdeMeta: true); }
                        catch { }
                    });
                }
                return (true, false, null);
            }

            return (false, false, I18nService.T("AgQ.ErrTokenRefresh"));
        }
        catch (Exception ex)
        {
            if (stillValid)
                return (true, false, null);
            return (false, false, ex.Message);
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

                var resp = await SendWithTimeoutAsync(req, CancellationToken.None);
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
        var ideVer = AgPaths.GetInstalledIdeVersion();
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.TryAddWithoutValidation("User-Agent", $"antigravity/hub/{ideVer} (os_type=windows; arch=amd64; aidev_client)");
        req.Headers.TryAddWithoutValidation("x-client-name", "antigravity");
        req.Headers.TryAddWithoutValidation("x-client-version", ideVer);
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
        var (models, _) = await FetchAvailableModelsDetailAsync(profile, token);
        return models.Select(m => m.ModelId).ToList();
    }

    /// <summary>
    /// 在线动态拉取当前账号全部可用模型的详细配额与能力参数（源自 fetchAvailableModels）。
    /// 仅请求 Antigravity 客户端一致的 daily / sandbox 端点，避免回退到 cloudcode-pa 拿到脱离的旧 26 模型 100% 配额。
    /// </summary>
    public static async Task<(List<AgModelQuotaInfo> Models, string? RawJson)> FetchAvailableModelsDetailAsync(Profile profile, string token)
    {
        var modelUrls = new[]
        {
            "https://daily-cloudcode-pa.googleapis.com/v1internal:fetchAvailableModels",
            "https://daily-cloudcode-pa.sandbox.googleapis.com/v1internal:fetchAvailableModels"
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
                        var pickerOrderMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                        if (doc.RootElement.TryGetProperty("agentModelSorts", out var sortsElem) && sortsElem.ValueKind == JsonValueKind.Array)
                        {
                            int ordIdx = 0;
                            foreach (var sortObj in sortsElem.EnumerateArray())
                            {
                                if (sortObj.TryGetProperty("groups", out var grps) && grps.ValueKind == JsonValueKind.Array)
                                {
                                    foreach (var grp in grps.EnumerateArray())
                                    {
                                        if (grp.TryGetProperty("modelIds", out var mIds) && mIds.ValueKind == JsonValueKind.Array)
                                        {
                                            foreach (var mid in mIds.EnumerateArray())
                                            {
                                                var idStr = mid.GetString();
                                                if (!string.IsNullOrWhiteSpace(idStr) && !pickerOrderMap.ContainsKey(idStr))
                                                    pickerOrderMap[idStr] = ordIdx++;
                                            }
                                        }
                                    }
                                }
                            }
                        }

                        var groupMetaMap = new Dictionary<string, (string GroupKey, string GroupName, string ThinkingName)>(StringComparer.OrdinalIgnoreCase);
                        if (doc.RootElement.TryGetProperty("groupedModels", out var gmElem) && gmElem.ValueKind == JsonValueKind.Object)
                        {
                            foreach (var gProp in gmElem.EnumerateObject())
                            {
                                var gKey = gProp.Name;
                                var gDisplay = gProp.Value.TryGetProperty("displayName", out var gdn) ? gdn.GetString() ?? gKey : gKey;
                                if (gProp.Value.TryGetProperty("supportedThinkingLevels", out var lvls) && lvls.ValueKind == JsonValueKind.Array)
                                {
                                    foreach (var lvl in lvls.EnumerateArray())
                                    {
                                        var sfx = lvl.TryGetProperty("thinkingSuffix", out var tsfx) ? tsfx.GetString() : null;
                                        var tdn = lvl.TryGetProperty("thinkingDisplayName", out var tdnEl) ? tdnEl.GetString() ?? "" : "";
                                        if (!string.IsNullOrWhiteSpace(sfx))
                                        {
                                            groupMetaMap[$"{gKey}-{sfx}"] = (gKey, gDisplay, tdn);
                                        }
                                    }
                                }
                            }
                        }

                        var list = new List<AgModelQuotaInfo>();
                        var quotaSummary = new List<string>();
                        bool hasActiveArgon = false;
                        bool hasDisabledArgon = false;
                        bool hasActiveProExclusive = false;
                        bool hasDisabledProExclusive = false;
                        foreach (var prop in modelsElem.EnumerateObject())
                        {
                            var val = prop.Value;
                            bool disabled = val.TryGetProperty("disabled", out var dis) && dis.ValueKind == JsonValueKind.True;
                            if (prop.Name.Contains("argon", StringComparison.OrdinalIgnoreCase))
                            {
                                if (disabled) hasDisabledArgon = true;
                                else hasActiveArgon = true;
                            }
                            if (prop.Name.Contains("argon", StringComparison.OrdinalIgnoreCase) ||
                                prop.Name.Contains("gemini-4-neon", StringComparison.OrdinalIgnoreCase) ||
                                prop.Name.Contains("gemini-4-carbon", StringComparison.OrdinalIgnoreCase))
                            {
                                if (disabled) hasDisabledProExclusive = true;
                                else hasActiveProExclusive = true;
                            }

                            double? remaining = null;
                            string? resetTime = null;
                            if (val.TryGetProperty("quotaInfo", out var qi))
                            {
                                if (qi.TryGetProperty("remainingFraction", out var rf) && rf.TryGetDouble(out var rfd))
                                {
                                    remaining = rfd;
                                    quotaSummary.Add($"{prop.Name}={rfd:0.##}");
                                }
                                if (qi.TryGetProperty("resetTime", out var rt) && rt.ValueKind == JsonValueKind.String)
                                    resetTime = rt.GetString();
                            }

                            int pOrder = pickerOrderMap.TryGetValue(prop.Name, out var ord) ? ord : -1;
                            bool pVisible = pOrder >= 0 && !disabled;
                            groupMetaMap.TryGetValue(prop.Name, out var gm);

                            list.Add(new AgModelQuotaInfo
                            {
                                ModelId = prop.Name,
                                DisplayName = val.TryGetProperty("displayName", out var dn) && !string.IsNullOrWhiteSpace(dn.GetString())
                                    ? dn.GetString()! : prop.Name,
                                RemainingFraction = remaining,
                                ResetTime = resetTime,
                                Recommended = val.TryGetProperty("recommended", out var rec) && rec.ValueKind == JsonValueKind.True,
                                Disabled = disabled,
                                SupportsThinking = val.TryGetProperty("supportsThinking", out var st) && st.ValueKind == JsonValueKind.True,
                                SupportsImages = val.TryGetProperty("supportsImages", out var si) && si.ValueKind == JsonValueKind.True,
                                MaxTokens = val.TryGetProperty("maxTokens", out var mt) && mt.TryGetInt32(out var mti) ? mti : 0,
                                MaxOutputTokens = val.TryGetProperty("maxOutputTokens", out var mot) && mot.TryGetInt32(out var moti) ? moti : 0,
                                IsPickerVisible = pVisible,
                                PickerOrder = pOrder,
                                GroupKey = gm.GroupKey,
                                GroupDisplayName = gm.GroupName,
                                ThinkingDisplayName = gm.ThinkingName,
                            });
                        }
                        if (hasActiveArgon || (profile.IsProTier && hasActiveProExclusive))
                        {
                            profile.IsPaidSubscription = true;
                        }
                        else if ((hasDisabledArgon || hasDisabledProExclusive) && !hasActiveProExclusive && profile.IsProTier && !profile.IsUltra)
                        {
                            profile.IsPaidSubscription = false;
                        }
                        if (list.Count > 0)
                        {
                            App.LogStartup($"AgFetchModels: {list.Count} models; quota: {string.Join(" ", quotaSummary)}");
                            return (list, json);
                        }
                    }
                }
            }
            catch { }
        }
        return (new List<AgModelQuotaInfo>(), null);
    }

    /// <summary>
    /// 从账号实时拉回的可用模型中筛选 Gemini 候选，严格按“开销从低到高”分层：
    ///   1) flash + low/lite（最低开销档）
    ///   2) 任意 flash
    ///   3) 任意 gemini（不含独立配额池的 argon 与 disabled 占位项）
    /// 同层内按版本升序（最老优先，保证向后兼容）。
    /// **绝不使用硬编码模型名**——拉取失败时返回空列表，由调用方报明确错误，
    /// 避免拿猜测的模型名去打 Google 导致 404/429 误判。
    /// </summary>
    public static List<string> ResolveGeminiModels(List<string> availableModels)
    {
        if (availableModels == null || availableModels.Count == 0)
            return new List<string>();

        var gemini = availableModels
            .Where(m => m.Contains("gemini", StringComparison.OrdinalIgnoreCase) &&
                        !m.Contains("argon", StringComparison.OrdinalIgnoreCase) &&
                        !m.Contains("disabled", StringComparison.OrdinalIgnoreCase))
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
    /// 按版本号降序（最新优先）。CLI 激活专用：老模型（如 gemini-2.5）多已被官方下线，
    /// CLI 会直接拒绝，所以必须优先试最新的，而不是最便宜/最老的。
    /// </summary>
    public static List<string> OrderByVersionDescending(List<string> models)
    {
        if (models == null || models.Count == 0) return new List<string>();
        return models
            .OrderByDescending(ExtractModelVersion)
            .ThenBy(m => m, StringComparer.OrdinalIgnoreCase)
            .ToList();
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

    static int ApiModelEffortRank(string id)
    {
        if (id.EndsWith("-low", StringComparison.OrdinalIgnoreCase) ||
            id.Contains("flash-lite", StringComparison.OrdinalIgnoreCase))
            return 0;
        if (!id.EndsWith("-medium", StringComparison.OrdinalIgnoreCase) &&
            !id.EndsWith("-high", StringComparison.OrdinalIgnoreCase) &&
            !id.EndsWith("-xhigh", StringComparison.OrdinalIgnoreCase) &&
            !id.EndsWith("-thinking", StringComparison.OrdinalIgnoreCase))
            return 1;
        if (id.EndsWith("-medium", StringComparison.OrdinalIgnoreCase))
            return 2;
        if (id.EndsWith("-high", StringComparison.OrdinalIgnoreCase) ||
            id.EndsWith("-thinking", StringComparison.OrdinalIgnoreCase))
            return 3;
        if (id.EndsWith("-xhigh", StringComparison.OrdinalIgnoreCase))
            return 4;
        return 5;
    }

    public static List<string> PickCliGeminiCandidates(Profile profile)
    {
        if (profile.ModelQuotas == null || profile.ModelQuotas.Count == 0)
            return new List<string>();

        return profile.ModelQuotas
            .Where(m => !m.Disabled &&
                        !string.IsNullOrWhiteSpace(m.ModelId) &&
                        m.ModelId.Contains("gemini", StringComparison.OrdinalIgnoreCase) &&
                        !m.ModelId.Contains("argon", StringComparison.OrdinalIgnoreCase) &&
                        !m.ModelId.StartsWith("tab_", StringComparison.OrdinalIgnoreCase) &&
                        !m.ModelId.StartsWith("chat_", StringComparison.OrdinalIgnoreCase) &&
                        !m.ModelId.Contains("image", StringComparison.OrdinalIgnoreCase) &&
                        !m.ModelId.Contains("sonar", StringComparison.OrdinalIgnoreCase) &&
                        !m.ModelId.Contains("orchestrator", StringComparison.OrdinalIgnoreCase))
            .OrderBy(m => m.IsPickerVisible ? 0 : 1)
            .ThenBy(m => m.ModelId.Contains("flash", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(m => ApiModelEffortRank(m.ModelId))
            .ThenByDescending(m => ExtractModelVersion(m.ModelId))
            .ThenBy(m => m.ModelId, StringComparer.OrdinalIgnoreCase)
            .Select(m => m.ModelId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static List<string> PickCliThirdPartyCandidates(Profile profile)
    {
        if (profile.ModelQuotas == null || profile.ModelQuotas.Count == 0)
            return new List<string>();

        static int FamilyRank(string id)
        {
            if (id.Contains("sonnet", StringComparison.OrdinalIgnoreCase)) return 0;
            if (id.Contains("gpt", StringComparison.OrdinalIgnoreCase)) return 1;
            if (id.Contains("opus", StringComparison.OrdinalIgnoreCase)) return 2;
            return 3;
        }

        return profile.ModelQuotas
            .Where(m => !m.Disabled &&
                        !string.IsNullOrWhiteSpace(m.ModelId) &&
                        (m.ModelId.Contains("claude", StringComparison.OrdinalIgnoreCase) ||
                         m.ModelId.Contains("gpt", StringComparison.OrdinalIgnoreCase)))
            .OrderBy(m => m.IsPickerVisible ? 0 : 1)
            .ThenBy(m => FamilyRank(m.ModelId))
            .ThenBy(m => ApiModelEffortRank(m.ModelId))
            .ThenByDescending(m => ExtractClaudeVersion(m.ModelId))
            .ThenBy(m => m.ModelId, StringComparer.OrdinalIgnoreCase)
            .Select(m => m.ModelId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// 向 Google 官方端点发送真实轻量消息以激活该账号的限额周期，并自动拉取最新配额
    /// 遵循安全防风控原则：
    /// 1. 动态拉取当前账号可用模型，使用低功耗模型触发限额计时；
    /// 2. 自动跳过已在计时中的配额池，避免重复激活。
    /// </summary>
    public static async Task<(bool ok, long latencyMs, string message, bool rateLimited)> ActivateAccountQuotaAsync(Profile profile, CancellationToken ct = default)
    {
        var r = await ActivateAccountQuotaDetailedAsync(profile, ct, forcePreRefresh: true);
        return (r.ok, r.latencyMs, r.message, r.rateLimited);
    }

    public static async Task<(bool ok, long latencyMs, string message, bool rateLimited, bool skippedAll)> ActivateAccountQuotaDetailedAsync(
        Profile profile, CancellationToken ct = default, bool forcePreRefresh = true)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        if (string.IsNullOrEmpty(profile.RefreshToken) && string.IsNullOrEmpty(profile.AccessToken))
        {
            profile.IsActivated = false;
            profile.ActivationError = I18nService.T("AgQ.ErrNoCredentials");
            ProfileStore.Save(profile);
            return (false, 0, profile.ActivationError, false, false);
        }

        // 1. 确保 Token 处于最新状态
        var tokenOk = await EnsureFreshTokenAsync(profile);
        if (!tokenOk || string.IsNullOrEmpty(profile.AccessToken))
        {
            profile.IsActivated = false;
            profile.ActivationError = I18nService.T("AgQ.ErrTokenRefresh");
            ProfileStore.Save(profile);
            return (false, 0, profile.ActivationError, false, false);
        }

        // 1.2 先确保当前配额池与模型目录是最新的，以便精准跳过已在计时的池
        if (forcePreRefresh || !profile.QuotaUpdatedAt.HasValue || (DateTime.UtcNow - profile.QuotaUpdatedAt.Value).TotalSeconds > 45)
        {
            ct.ThrowIfCancellationRequested();
            await RefreshQuotaAsync(profile);
        }

        var plan = profile.EvaluatePoolActivationNeed();
        if (!plan.NeedsAnyCliCall)
        {
            sw.Stop();
            profile.IsActivated = true;
            profile.ActivationError = null;
            profile.QuotaExhausted = false;
            profile.QuotaExhaustedResetAt = null;
            ProfileStore.Save(profile);
            return (true, sw.ElapsedMilliseconds, I18nService.T("AgQ.AlreadyActiveSkip"), false, true);
        }

        // 1.5 激活依赖本地真实的 Antigravity CLI（agy.exe）。
        // 与人类在终端使用完全一致（自带鉴权 / 项目解析 / 配额管理 / 设备指纹），
        // 风控命中率远低于手搓 HTTP 握手。CLI 缺失时直接返回可操作错误，
        // 由 UI 提示用户安装，绝不静默回退到不可用的合成握手。
        if (!AgCliService.IsAvailable())
        {
            sw.Stop();
            profile.IsActivated = false;
            profile.ActivationError = I18nService.T("AgQ.CliNotFound");
            ProfileStore.Save(profile);
            return (false, sw.ElapsedMilliseconds, profile.ActivationError, false, false);
        }

        ct.ThrowIfCancellationRequested();
        var cliResult = await AgCliService.ActivateAsync(profile, plan, ct);
        if (cliResult.GeminiOk) profile.GeminiActivatedAt = DateTime.UtcNow;
        if (cliResult.ThirdPartyOk) profile.ThirdPartyActivatedAt = DateTime.UtcNow;
        if (cliResult.ArgonOk) profile.ArgonActivatedAt = DateTime.UtcNow;

        // CLI 跑完无论成败都同步一次最新配额，保证徽章与剩余百分比是最新的
        await RefreshQuotaAsync(profile);

        profile.IsActivated = cliResult.Ok;
        profile.ActivationLatencyMs = cliResult.LatencyMs;
        profile.ActivatedAt = cliResult.Ok ? DateTime.UtcNow : profile.ActivatedAt;
        if (cliResult.Ok)
        {
            profile.ActivationError = null;
            profile.QuotaExhausted = false;
            profile.QuotaExhaustedResetAt = null;
        }
        else
        {
            profile.ActivationError = cliResult.Message;
            profile.QuotaExhausted = cliResult.RateLimited;
            if (cliResult.RateLimited)
                profile.QuotaExhaustedResetAt = profile.ConstrainingResetUtc() ?? DateTime.UtcNow.AddHours(5);
        }
        ProfileStore.Save(profile);
        sw.Stop();
        return (cliResult.Ok, cliResult.LatencyMs, cliResult.Message, cliResult.RateLimited, false);
    }

    /// <summary>
    /// 预留：手搓 HTTP 合成握手。实测对 Pro 账号长期 429（设备指纹与配额管理均与
    /// 真实客户端不一致），已停止使用；保留以便后续诊断比对，不参与正常激活流程。
    /// </summary>
    private static async Task<(bool ok, long latencyMs, string message, bool rateLimited)> ActivateViaSyntheticHandshakeAsync(Profile profile)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var token = profile.AccessToken ?? "";
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
                    // 优先用"当前真正用尽的桶"的重置时间；退化到最早的未来重置；最后才盲猜 +5h
                    profile.QuotaExhaustedResetAt = profile.ConstrainingResetUtc()
                        ?? EarliestQuotaReset(profile)
                        ?? DateTime.UtcNow.AddHours(5);
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

    /// <summary>取各配额桶中最小的剩余比例（无数据返回 null）。</summary>
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
        Consider(profile.QuotaArgon5hFraction);
        Consider(profile.QuotaArgonWeeklyFraction);
        return min;
    }

    /// <summary>取配额摘要中最早的未来重置时间（用于 429 倒计时展示，自动忽略满额未开始计时的占位时间）。</summary>
    private static DateTime? EarliestQuotaReset(Profile profile)
    {
        DateTime? earliest = null;
        void Consider(double? fraction, string? raw, TimeSpan window)
        {
            if (string.IsNullOrEmpty(raw)) return;
            if (Profile.IsTimerStandby(fraction, raw, profile.QuotaUpdatedAt, window)) return;
            if (DateTime.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var dt)
                && dt > DateTime.UtcNow && dt < DateTime.UtcNow.AddDays(8))
            {
                if (earliest == null || dt < earliest) earliest = dt;
            }
        }
        Consider(profile.Quota5hFraction, profile.Quota5hResetTime, TimeSpan.FromHours(5));
        Consider(profile.QuotaWeeklyFraction, profile.QuotaWeeklyResetTime, TimeSpan.FromDays(7));
        Consider(profile.Quota3p5hFraction, profile.Quota3p5hResetTime, TimeSpan.FromHours(5));
        Consider(profile.Quota3pWeeklyFraction, profile.Quota3pWeeklyResetTime, TimeSpan.FromDays(7));
        Consider(profile.QuotaArgon5hFraction, profile.QuotaArgon5hResetTime, TimeSpan.FromHours(5));
        Consider(profile.QuotaArgonWeeklyFraction, profile.QuotaArgonWeeklyResetTime, TimeSpan.FromDays(7));
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
        // 与 Antigravity 客户端一致，优先请求 daily 端点；sandbox / 生产端点作为备用。
        // daily 端点可能有独立限流，429 时继续尝试备用 URL，全部 429 才返回 RateLimited。
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
                        parts = new[] { new { text = "只回复1" } }
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
            "https://daily-cloudcode-pa.googleapis.com/v1internal:loadCodeAssist",
            "https://daily-cloudcode-pa.sandbox.googleapis.com/v1internal:loadCodeAssist",
            "https://cloudcode-pa.googleapis.com/v1internal:loadCodeAssist"
        };

        foreach (var url in tierUrls)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, url);
                ApplyAntigravityHeaders(req, token, profile);
                req.Content = new StringContent("{}", Encoding.UTF8, "application/json");

                var resp = await SendWithTimeoutAsync(req, CancellationToken.None);
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
        bool hasArgonGroupInSummary = false;
        string? summaryJson = null;

        // 2. 并行拉取配额汇总（retrieveUserQuotaSummary）与逐模型明细（fetchAvailableModels），
        //    再统一回填状态（完全单线程写 Profile，耗时减半）。
        var summaryTask = FetchQuotaSummaryJsonAsync(profile, token, projectPayload);
        var modelsTask = FetchAvailableModelsDetailAsync(profile, token);
        await Task.WhenAll(summaryTask, modelsTask);

        summaryJson = summaryTask.Result;
        if (!string.IsNullOrEmpty(summaryJson))
        {
            ParseQuotaSummary(profile, summaryJson, out hasArgonGroupInSummary);
            quotaGot = true;
        }

        var (modelQuotas, modelsJson) = modelsTask.Result;
        if (modelQuotas.Count > 0)
        {
            profile.ModelQuotas = modelQuotas;
            quotaGot = ReconcileQuotaWithModels(profile, modelQuotas, quotaGot, hasArgonGroupInSummary);
        }

        if (quotaGot || modelQuotas.Count > 0)
        {
            _ = AgToolsService.SyncQuotaToToolsAsync(profile, summaryJson, modelsJson);
        }

        if (quotaGot)
        {
            profile.QuotaUpdatedAt = DateTime.UtcNow;

            // 配额已恢复：清理 429 遗留的"用尽"标记，避免徽章继续显示过期倒计时。
            // IsActivated=false 只表示"上次激活失败"，配额恢复后失败原因已不存在，回落"待激活"。
            if (profile.QuotaExhausted || profile.QuotaExhaustedResetAt.HasValue)
            {
                var minRemaining = MinRemainingFraction(profile);
                if (minRemaining.HasValue && minRemaining.Value > 0.05)
                {
                    profile.QuotaExhausted = false;
                    profile.QuotaExhaustedResetAt = null;
                    if (profile.IsActivated == false)
                    {
                        profile.IsActivated = null;
                        profile.ActivationError = null;
                    }
                }
            }
            // 用量监控：写入配额快照（供趋势差分展示）
            UsageStorage.RecordQuotaSnapshot(profile.Email ?? "", "gemini-5h", profile.Quota5hFraction ?? 0, ParseResetUtc(profile.Quota5hResetTime));
            UsageStorage.RecordQuotaSnapshot(profile.Email ?? "", "gemini-weekly", profile.QuotaWeeklyFraction ?? 0, ParseResetUtc(profile.QuotaWeeklyResetTime));
            UsageStorage.RecordQuotaSnapshot(profile.Email ?? "", "3p-5h", profile.Quota3p5hFraction ?? 0, ParseResetUtc(profile.Quota3p5hResetTime));
            UsageStorage.RecordQuotaSnapshot(profile.Email ?? "", "3p-weekly", profile.Quota3pWeeklyFraction ?? 0, ParseResetUtc(profile.Quota3pWeeklyResetTime));
            if (Profile.ShowArgonQuotaColumn && profile.QuotaArgon5hFraction.HasValue)
                UsageStorage.RecordQuotaSnapshot(profile.Email ?? "", "gemini-argon-5h", profile.QuotaArgon5hFraction.Value, ParseResetUtc(profile.QuotaArgon5hResetTime));
            if (Profile.ShowArgonQuotaColumn && profile.QuotaArgonWeeklyFraction.HasValue)
                UsageStorage.RecordQuotaSnapshot(profile.Email ?? "", "gemini-argon-weekly", profile.QuotaArgonWeeklyFraction.Value, ParseResetUtc(profile.QuotaArgonWeeklyResetTime));
            ProfileStore.Save(profile);
            return true;
        }

        return false;
    }

    static async Task<string?> FetchQuotaSummaryJsonAsync(Profile profile, string token, string projectPayload)
    {
        var quotaUrls = new[]
        {
            "https://daily-cloudcode-pa.googleapis.com/v1internal:retrieveUserQuotaSummary",
            "https://daily-cloudcode-pa.sandbox.googleapis.com/v1internal:retrieveUserQuotaSummary"
        };

        foreach (var url in quotaUrls)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, url);
                ApplyAntigravityHeaders(req, token, profile);
                req.Content = new StringContent(projectPayload, Encoding.UTF8, "application/json");

                var resp = await SendWithTimeoutAsync(req, CancellationToken.None);
                if (resp.IsSuccessStatusCode)
                {
                    var summaryJson = await resp.Content.ReadAsStringAsync();
                    App.LogStartup($"AgQuota: {profile.Email} url={url}");
                    return summaryJson;
                }

                using var reqEmpty = new HttpRequestMessage(HttpMethod.Post, url);
                ApplyAntigravityHeaders(reqEmpty, token, profile);
                reqEmpty.Content = new StringContent("{}", Encoding.UTF8, "application/json");
                var respEmpty = await SendWithTimeoutAsync(reqEmpty, CancellationToken.None);
                if (respEmpty.IsSuccessStatusCode)
                {
                    var summaryJson = await respEmpty.Content.ReadAsStringAsync();
                    App.LogStartup($"AgQuota: {profile.Email} url={url}");
                    return summaryJson;
                }
            }
            catch { }
        }

        return null;
    }

    static DateTime? ParseResetUtc(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return null;
        if (DateTime.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var dt))
            return dt;
        return null;
    }

    static double? AlignIfMatchingWindow(double? currentFraction, string? currentResetRaw, DateTime? modelResetUtc, double modelRemaining)
    {
        if (!currentFraction.HasValue || !modelResetUtc.HasValue) return currentFraction;
        var bResetUtc = ParseResetUtc(currentResetRaw);
        if (!bResetUtc.HasValue) return currentFraction;
        if (Math.Abs((bResetUtc.Value - modelResetUtc.Value).TotalSeconds) <= 60 && modelRemaining < currentFraction.Value)
            return modelRemaining;
        return currentFraction;
    }

    internal static bool ReconcileQuotaWithModels(Profile profile, List<AgModelQuotaInfo> modelQuotas, bool quotaGot, bool hasArgonGroupFound = false)
    {
        if (modelQuotas == null || modelQuotas.Count == 0) return quotaGot;

        bool hasActiveArgon = modelQuotas.Any(m => !m.Disabled && m.ModelId.Contains("argon", StringComparison.OrdinalIgnoreCase));
        if (!hasActiveArgon && !hasArgonGroupFound)
        {
            profile.QuotaArgon5hFraction = null;
            profile.QuotaArgon5hResetTime = null;
            profile.QuotaArgonWeeklyFraction = null;
            profile.QuotaArgonWeeklyResetTime = null;
        }

        AgModelQuotaInfo? gemFallback = null;
        bool gemFallbackIsWeekly = false;
        AgModelQuotaInfo? argonFallback = null;
        AgModelQuotaInfo? p3Fallback = null;

        foreach (var m in modelQuotas)
        {
            if (m.Disabled || !m.RemainingFraction.HasValue) continue;
            if (m.ModelId.StartsWith("tab_", StringComparison.OrdinalIgnoreCase) ||
                m.ModelId.StartsWith("chat_", StringComparison.OrdinalIgnoreCase))
                continue;

            bool isArgon = m.ModelId.Contains("argon", StringComparison.OrdinalIgnoreCase);
            bool is3p = !isArgon && (m.ModelId.Contains("claude", StringComparison.OrdinalIgnoreCase) ||
                                     m.ModelId.Contains("gpt", StringComparison.OrdinalIgnoreCase));
            bool isGemini = !isArgon && !is3p && m.ModelId.Contains("gemini", StringComparison.OrdinalIgnoreCase);
            if (!isArgon && !is3p && !isGemini) continue;

            double rem = m.RemainingFraction.Value;
            var mResetUtc = ParseResetUtc(m.ResetTime);
            bool isWeeklyReset = mResetUtc.HasValue && (mResetUtc.Value - DateTime.UtcNow).TotalHours > 24;

            if (quotaGot)
            {
                if (isArgon)
                {
                    profile.QuotaArgon5hFraction = AlignIfMatchingWindow(profile.QuotaArgon5hFraction, profile.QuotaArgon5hResetTime, mResetUtc, rem);
                    profile.QuotaArgonWeeklyFraction = AlignIfMatchingWindow(profile.QuotaArgonWeeklyFraction, profile.QuotaArgonWeeklyResetTime, mResetUtc, rem);
                }
                else if (is3p)
                {
                    profile.Quota3p5hFraction = AlignIfMatchingWindow(profile.Quota3p5hFraction, profile.Quota3p5hResetTime, mResetUtc, rem);
                    profile.Quota3pWeeklyFraction = AlignIfMatchingWindow(profile.Quota3pWeeklyFraction, profile.Quota3pWeeklyResetTime, mResetUtc, rem);
                }
                else
                {
                    profile.Quota5hFraction = AlignIfMatchingWindow(profile.Quota5hFraction, profile.Quota5hResetTime, mResetUtc, rem);
                    profile.QuotaWeeklyFraction = AlignIfMatchingWindow(profile.QuotaWeeklyFraction, profile.QuotaWeeklyResetTime, mResetUtc, rem);
                }
            }

            if (isArgon)
            {
                if (argonFallback == null || rem < argonFallback.RemainingFraction!.Value) argonFallback = m;
            }
            else if (is3p)
            {
                if (p3Fallback == null || rem < p3Fallback.RemainingFraction!.Value) p3Fallback = m;
            }
            else
            {
                if (gemFallback == null ||
                    (isWeeklyReset && !gemFallbackIsWeekly) ||
                    (isWeeklyReset == gemFallbackIsWeekly && rem < gemFallback.RemainingFraction!.Value))
                {
                    gemFallback = m;
                    gemFallbackIsWeekly = isWeeklyReset;
                }
            }
        }

        if (!quotaGot)
        {
            if (gemFallback != null)
            {
                profile.QuotaWeeklyFraction = gemFallback.RemainingFraction;
                profile.QuotaWeeklyResetTime = gemFallback.ResetTime;
                quotaGot = true;
            }
            if (argonFallback != null)
            {
                profile.QuotaArgonWeeklyFraction = argonFallback.RemainingFraction;
                profile.QuotaArgonWeeklyResetTime = argonFallback.ResetTime;
                quotaGot = true;
            }
            if (p3Fallback != null)
            {
                profile.Quota3pWeeklyFraction = p3Fallback.RemainingFraction;
                profile.Quota3pWeeklyResetTime = p3Fallback.ResetTime;
                quotaGot = true;
            }
        }
        else if (!profile.QuotaWeeklyFraction.HasValue && gemFallback != null)
        {
            profile.QuotaWeeklyFraction = gemFallback.RemainingFraction;
            profile.QuotaWeeklyResetTime = gemFallback.ResetTime;
        }

        return quotaGot;
    }

    internal static void ParseQuotaSummary(Profile profile, string json)
        => ParseQuotaSummary(profile, json, out _);

    internal static void ParseQuotaSummary(Profile profile, string json, out bool hasArgonGroupFound)
    {
        hasArgonGroupFound = false;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("groups", out var groups)) return;

            bool isFree = !profile.IsProTier;

            // 标记本批响应中实际包含了哪些配额组，只对返回的组做增量/覆盖更新，
            // 绝不因为某些端点（如未全量上线的端点）暂缺某个组而盲目将已知数据抹空成 null。
            bool hasGeminiGroup = false;
            bool has3pGroupFound = false;

            double? newQuota5hFraction = null;
            string? newQuota5hResetTime = null;
            double? newQuotaWeeklyFraction = null;
            string? newQuotaWeeklyResetTime = null;

            double? newQuota3p5hFraction = null;
            string? newQuota3p5hResetTime = null;
            double? newQuota3pWeeklyFraction = null;
            string? newQuota3pWeeklyResetTime = null;

            double? newQuotaArgon5hFraction = null;
            string? newQuotaArgon5hResetTime = null;
            double? newQuotaArgonWeeklyFraction = null;
            string? newQuotaArgonWeeklyResetTime = null;

            foreach (var group in groups.EnumerateArray())
            {
                var groupName = group.TryGetProperty("displayName", out var gn) ? gn.GetString() ?? "" : "";
                bool isArgonGroup = groupName.Contains("Argon", StringComparison.OrdinalIgnoreCase);
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

                    bool isArgon = isArgonGroup || bucketId.Contains("argon", StringComparison.OrdinalIgnoreCase);
                    bool is3p = bucketId.StartsWith("3p", StringComparison.OrdinalIgnoreCase) || is3pGroup;

                    if (isArgon)
                    {
                        hasArgonGroupFound = true;
                        if (is5h)
                        {
                            newQuotaArgon5hFraction = fraction;
                            newQuotaArgon5hResetTime = resetTime;
                        }
                        else if (isWeekly)
                        {
                            newQuotaArgonWeeklyFraction = fraction;
                            newQuotaArgonWeeklyResetTime = resetTime;
                        }
                    }
                    else if (is3p)
                    {
                        has3pGroupFound = true;
                        if (is5h)
                        {
                            newQuota3p5hFraction = fraction;
                            newQuota3p5hResetTime = resetTime;
                        }
                        else if (isWeekly)
                        {
                            newQuota3pWeeklyFraction = fraction;
                            newQuota3pWeeklyResetTime = resetTime;
                        }
                    }
                    else
                    {
                        hasGeminiGroup = true;
                        if (is5h)
                        {
                            newQuota5hFraction = fraction;
                            newQuota5hResetTime = resetTime;
                        }
                        else if (isWeekly)
                        {
                            newQuotaWeeklyFraction = fraction;
                            newQuotaWeeklyResetTime = resetTime;
                        }
                    }
                }
            }

            if (hasGeminiGroup)
            {
                profile.Quota5hFraction = newQuota5hFraction;
                profile.Quota5hResetTime = newQuota5hResetTime;
                profile.QuotaWeeklyFraction = newQuotaWeeklyFraction;
                profile.QuotaWeeklyResetTime = newQuotaWeeklyResetTime;
            }

            if (has3pGroupFound)
            {
                profile.Quota3p5hFraction = newQuota3p5hFraction;
                profile.Quota3p5hResetTime = newQuota3p5hResetTime;
                profile.Quota3pWeeklyFraction = newQuota3pWeeklyFraction;
                profile.Quota3pWeeklyResetTime = newQuota3pWeeklyResetTime;
            }

            if (hasArgonGroupFound)
            {
                profile.QuotaArgon5hFraction = newQuotaArgon5hFraction;
                profile.QuotaArgon5hResetTime = newQuotaArgon5hResetTime;
                profile.QuotaArgonWeeklyFraction = newQuotaArgonWeeklyFraction;
                profile.QuotaArgonWeeklyResetTime = newQuotaArgonWeeklyResetTime;
                profile.IsPaidSubscription = true;
            }
            else if (isFree)
            {
                profile.QuotaArgon5hFraction = null;
                profile.QuotaArgon5hResetTime = null;
                profile.QuotaArgonWeeklyFraction = null;
                profile.QuotaArgonWeeklyResetTime = null;
                profile.IsPaidSubscription = false;
            }
            // 注意：若非免费号且当前响应未返回 Argon 组，绝对严禁主动将 IsPaidSubscription 改为 false！
            // 防止因端点差异或滚动发布导致付费订阅版与体验版在后台轮询中反复横跳跳变。

            // 订阅阶梯双向精准校准：
            // 1) 无 3P、无 5H、无 Argon：保持 Plus（若 ParseTier 已定为 Plus）或校准为 Starter 免费号；
            // 2) 若 ParseTier 已确认为 Plus (g1-plus-tier) 或 Starter，且仅有 weekly 桶（无任何 5H/Argon），
            //    同样保持对应档位并清理 5H/Argon 残留，保留 weekly 配额（Gemini + 3P weekly）；
            // 3) 只有实际返回了 5H 或 Argon 池，或原非 Starter/Plus 且含 3P/5H/Argon 时才认定为 Pro。
            bool isStarterOnlyGemini = !has3pGroupFound && !newQuota5hFraction.HasValue && !hasArgonGroupFound;
            bool currentlyStarter = profile.SubscriptionTier?.Contains("Starter", StringComparison.OrdinalIgnoreCase) == true;
            bool currentlyPlus = profile.SubscriptionTier?.Contains("Plus", StringComparison.OrdinalIgnoreCase) == true;
            if (hasGeminiGroup && isStarterOnlyGemini && !profile.IsUltra)
            {
                var keptTier = currentlyPlus ? "Google AI Plus" : "Starter";
                profile.SubscriptionTier = keptTier;
                profile.Plan = keptTier;
                profile.IsPaidSubscription = false;
                profile.Quota5hFraction = null;
                profile.Quota5hResetTime = null;
                profile.Quota3p5hFraction = null;
                profile.Quota3p5hResetTime = null;
                profile.Quota3pWeeklyFraction = null;
                profile.Quota3pWeeklyResetTime = null;
                profile.QuotaArgon5hFraction = null;
                profile.QuotaArgon5hResetTime = null;
                profile.QuotaArgonWeeklyFraction = null;
                profile.QuotaArgonWeeklyResetTime = null;
            }
            else if ((has3pGroupFound || newQuota5hFraction.HasValue || hasArgonGroupFound) && !profile.IsUltra)
            {
                if (currentlyPlus || currentlyStarter)
                {
                    // 仅当真正出现付费专属的 Argon 组时才允许从 Starter/Plus 升格为 Pro；
                    // Google 现已对免费版（free-tier）开放 3p-5h 限流池，绝不可凭 has5hBucket 误判为 Pro。
                    if (hasArgonGroupFound)
                    {
                        profile.SubscriptionTier = "Google AI Pro";
                        profile.Plan = "Google AI Pro";
                    }
                    else
                    {
                        var keptTier = currentlyPlus ? "Google AI Plus" : "Starter";
                        profile.SubscriptionTier = keptTier;
                        profile.Plan = keptTier;
                        profile.IsPaidSubscription = false;
                        if (!newQuota5hFraction.HasValue)
                        {
                            profile.Quota5hFraction = null;
                            profile.Quota5hResetTime = null;
                        }
                        if (!newQuota3p5hFraction.HasValue)
                        {
                            profile.Quota3p5hFraction = null;
                            profile.Quota3p5hResetTime = null;
                        }
                        profile.QuotaArgon5hFraction = null;
                        profile.QuotaArgon5hResetTime = null;
                        profile.QuotaArgonWeeklyFraction = null;
                        profile.QuotaArgonWeeklyResetTime = null;
                    }
                }
                else if (string.IsNullOrEmpty(profile.SubscriptionTier))
                {
                    // 仅在 loadCodeAssist 失败导致档位完全未知时，若同时有 Gemini 5H 或 Argon 才暂判为 Pro，否则归为 Starter
                    var inferredTier = (newQuota5hFraction.HasValue || hasArgonGroupFound) ? "Google AI Pro" : "Starter";
                    profile.SubscriptionTier = inferredTier;
                    profile.Plan = inferredTier;
                }
            }
        }
        catch { }
    }


    internal static void ParseTier(Profile profile, string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            bool isKnownStarterOrPlus = profile.SubscriptionTier != null &&
                                        (profile.SubscriptionTier.Contains("Starter", StringComparison.OrdinalIgnoreCase) ||
                                         profile.SubscriptionTier.Contains("Plus", StringComparison.OrdinalIgnoreCase));

            // 1. 优先识别 paidTier：Ultra 最高优；其次 g1-plus-tier / Plus 识别为 Google AI Plus；再其次 Starter 免费档
            if (root.TryGetProperty("paidTier", out var paidTier) && paidTier.ValueKind == JsonValueKind.Object)
            {
                var paidId = paidTier.TryGetProperty("id", out var pi) ? pi.GetString() : null;
                var paidName = paidTier.TryGetProperty("name", out var pn) ? pn.GetString() : null;
                var paidDesc = paidTier.TryGetProperty("description", out var pd) ? pd.GetString() : null;

                if ((!string.IsNullOrWhiteSpace(paidName) && paidName.Contains("Ultra", StringComparison.OrdinalIgnoreCase)) ||
                    (!string.IsNullOrWhiteSpace(paidId) && paidId.Contains("ultra", StringComparison.OrdinalIgnoreCase)))
                {
                    var ultraTier = !string.IsNullOrWhiteSpace(paidName) ? paidName : "Google AI Ultra";
                    profile.SubscriptionTier = ultraTier;
                    profile.Plan = ultraTier;
                    return;
                }

                if (string.Equals(paidId, "g1-plus-tier", StringComparison.OrdinalIgnoreCase) ||
                    (paidName?.Contains("Plus", StringComparison.OrdinalIgnoreCase) ?? false))
                {
                    profile.SubscriptionTier = "Google AI Plus";
                    profile.Plan = "Google AI Plus";
                    profile.IsPaidSubscription = false;
                    return;
                }

                if (string.Equals(paidId, "free-tier", StringComparison.OrdinalIgnoreCase) ||
                    (paidName?.Contains("Starter", StringComparison.OrdinalIgnoreCase) ?? false) ||
                    (paidDesc?.Contains("Starter", StringComparison.OrdinalIgnoreCase) ?? false))
                {
                    profile.SubscriptionTier = "Starter";
                    profile.Plan = "Starter";
                    profile.IsPaidSubscription = false;
                    return;
                }

                if (string.Equals(paidId, "g1-pro-tier", StringComparison.OrdinalIgnoreCase))
                {
                    var proTier = !string.IsNullOrWhiteSpace(paidName) ? paidName : "Google AI Pro";
                    profile.SubscriptionTier = proTier;
                    profile.Plan = proTier;
                    return;
                }
            }

            if (root.TryGetProperty("currentTier", out var currTier) &&
                currTier.TryGetProperty("name", out var currName) &&
                !string.IsNullOrWhiteSpace(currName.GetString()))
            {
                var tier = currName.GetString();
                if (tier != null)
                {
                    if (tier.Contains("Plus", StringComparison.OrdinalIgnoreCase))
                    {
                        profile.SubscriptionTier = "Google AI Plus";
                        profile.Plan = "Google AI Plus";
                        return;
                    }
                    if (tier.Contains("Ultra", StringComparison.OrdinalIgnoreCase) ||
                        tier.Contains("Starter", StringComparison.OrdinalIgnoreCase))
                    {
                        profile.SubscriptionTier = tier;
                        profile.Plan = tier;
                        return;
                    }
                }
            }

            // 2. 若已知该账号已被确认为 Starter / Plus，绝不被 paidTier 的推荐升配选项（"Google AI Pro"）覆盖
            if (isKnownStarterOrPlus) return;

            // 3. 初始未确定阶梯时，若 paidTier 存在且非 Starter 则暂赋候选，后续由 ParseQuotaSummary 结合真实配额池最终校准
            if (root.TryGetProperty("paidTier", out var pt) &&
                pt.TryGetProperty("name", out var pn2) &&
                !string.IsNullOrWhiteSpace(pn2.GetString()))
            {
                var tier = pn2.GetString();
                if (string.IsNullOrEmpty(profile.SubscriptionTier))
                {
                    profile.SubscriptionTier = tier;
                    profile.Plan = tier;
                }
            }
        }
        catch { }
    }
}

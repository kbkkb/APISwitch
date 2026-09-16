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

            return !string.IsNullOrEmpty(profile.AccessToken);
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
            "https://cloudcode-pa.googleapis.com/v1internal:loadCodeAssist",
            "https://daily-cloudcode-pa.googleapis.com/v1internal:loadCodeAssist"
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

    public static async Task<bool> RefreshQuotaAsync(Profile profile)
    {
        if (string.IsNullOrEmpty(profile.RefreshToken) && string.IsNullOrEmpty(profile.AccessToken))
            return false;

        var tokenOk = await EnsureFreshTokenAsync(profile);
        if (!tokenOk || string.IsNullOrEmpty(profile.AccessToken))
            return false;

        var token = profile.AccessToken;

        // 1. Query Quota Summary (for Paid/Pro users)
        var quotaUrls = new[]
        {
            "https://cloudcode-pa.googleapis.com/v1internal:retrieveUserQuotaSummary",
            "https://daily-cloudcode-pa.googleapis.com/v1internal:retrieveUserQuotaSummary"
        };

        bool quotaGot = false;
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
                    break;
                }
            }
            catch { }
        }

        // 1.5. Fallback: Query fetchAvailableModels (for Free/Starter accounts where retrieveUserQuotaSummary returns 403)
        if (!quotaGot)
        {
            var modelUrls = new[]
            {
                "https://cloudcode-pa.googleapis.com/v1internal:fetchAvailableModels",
                "https://daily-cloudcode-pa.googleapis.com/v1internal:fetchAvailableModels"
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
                            break;
                        }
                    }
                }
                catch { }
            }
        }

        // 2. Query Subscription Tier (loadCodeAssist)
        var tierUrls = new[]
        {
            "https://cloudcode-pa.googleapis.com/v1internal:loadCodeAssist",
            "https://daily-cloudcode-pa.googleapis.com/v1internal:loadCodeAssist"
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

            foreach (var group in groups.EnumerateArray())
            {
                if (!group.TryGetProperty("buckets", out var buckets)) continue;

                foreach (var bucket in buckets.EnumerateArray())
                {
                    var bucketId = bucket.TryGetProperty("bucketId", out var bId) ? bId.GetString() ?? "" : "";
                    var window = bucket.TryGetProperty("window", out var w) ? w.GetString() ?? "" : "";
                    double fraction = bucket.TryGetProperty("remainingFraction", out var frac) && frac.TryGetDouble(out var d) ? d : 1.0;
                    string? resetTime = bucket.TryGetProperty("resetTime", out var rt) ? rt.GetString() : null;

                    if (bucketId.Contains("5h", StringComparison.OrdinalIgnoreCase) || window.Equals("5h", StringComparison.OrdinalIgnoreCase))
                    {
                        // Priority to gemini-5h
                        if (profile.Quota5hFraction == null || bucketId.StartsWith("gemini", StringComparison.OrdinalIgnoreCase))
                        {
                            profile.Quota5hFraction = fraction;
                            profile.Quota5hResetTime = resetTime;
                        }
                    }
                    else if (bucketId.Contains("weekly", StringComparison.OrdinalIgnoreCase) || window.Equals("weekly", StringComparison.OrdinalIgnoreCase))
                    {
                        if (profile.QuotaWeeklyFraction == null || bucketId.StartsWith("gemini", StringComparison.OrdinalIgnoreCase))
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

    private static bool ParseAvailableModels(Profile profile, string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("models", out var modelsElem)) return false;

            string? defaultModel = root.TryGetProperty("defaultAgentModelId", out var def) ? def.GetString() : null;

            double? weeklyFraction = null;
            string? weeklyResetTime = null;

            // 1. Try defaultAgentModelId first
            if (!string.IsNullOrEmpty(defaultModel) &&
                modelsElem.TryGetProperty(defaultModel, out var defModel) &&
                defModel.TryGetProperty("quotaInfo", out var defQuota))
            {
                if (defQuota.TryGetProperty("remainingFraction", out var rf) && rf.TryGetDouble(out var d))
                    weeklyFraction = d;
                if (defQuota.TryGetProperty("resetTime", out var rt))
                    weeklyResetTime = rt.GetString();
            }

            // 2. Try preferred primary models if default didn't have quota
            if (!weeklyFraction.HasValue)
            {
                string[] preferredModels =
                {
                    "gemini-3.8-flash-high",
                    "gemini-3.7-flash-medium",
                    "gemini-3.7-flash-high",
                    "gemini-pro-agent",
                    "claude-sonnet-4-6",
                    "gemini-3.6-flash-high"
                };

                foreach (var mId in preferredModels)
                {
                    if (modelsElem.TryGetProperty(mId, out var mObj) &&
                        mObj.TryGetProperty("quotaInfo", out var qInfo))
                    {
                        if (qInfo.TryGetProperty("remainingFraction", out var rf) && rf.TryGetDouble(out var d))
                            weeklyFraction = d;
                        if (qInfo.TryGetProperty("resetTime", out var rt))
                            weeklyResetTime = rt.GetString();
                        if (weeklyFraction.HasValue) break;
                    }
                }
            }

            // 3. Fallback to any model with quotaInfo
            if (!weeklyFraction.HasValue)
            {
                foreach (var prop in modelsElem.EnumerateObject())
                {
                    if (prop.Value.TryGetProperty("quotaInfo", out var qInfo))
                    {
                        if (qInfo.TryGetProperty("remainingFraction", out var rf) && rf.TryGetDouble(out var d))
                        {
                            weeklyFraction = d;
                            if (qInfo.TryGetProperty("resetTime", out var rt))
                                weeklyResetTime = rt.GetString();
                            break;
                        }
                    }
                }
            }

            if (weeklyFraction.HasValue)
            {
                // Free/Starter accounts have only weekly quota for agent models, no 5h limit
                profile.QuotaWeeklyFraction = weeklyFraction.Value;
                profile.QuotaWeeklyResetTime = weeklyResetTime;
                profile.Quota5hFraction = null;
                profile.Quota5hResetTime = null;
                return true;
            }
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

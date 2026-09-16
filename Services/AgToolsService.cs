using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using APISwitch.Models;

namespace APISwitch.Services;

public static class AgToolsService
{
    public static string ToolsBaseDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".antigravity_tools");

    public static string AccountsJsonPath => Path.Combine(ToolsBaseDir, "accounts.json");
    public static string AccountsDetailDir => Path.Combine(ToolsBaseDir, "accounts");

    public static string GeminiCredsPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".gemini", "oauth_creds.json");

    public static bool IsInstalled()
    {
        return Directory.Exists(ToolsBaseDir) && File.Exists(AccountsJsonPath);
    }

    public static async Task<List<Profile>> ImportAccountsAsync()
    {
        var imported = new List<Profile>();
        if (!IsInstalled()) return imported;

        try
        {
            var indexJson = await File.ReadAllTextAsync(AccountsJsonPath);
            using var doc = JsonDocument.Parse(indexJson);
            if (!doc.RootElement.TryGetProperty("accounts", out var accountsElem))
                return imported;

            string? currentId = doc.RootElement.TryGetProperty("current_account_id", out var curElem)
                ? curElem.GetString()
                : null;

            foreach (var acc in accountsElem.EnumerateArray())
            {
                var id = acc.TryGetProperty("id", out var idElem) ? idElem.GetString() : null;
                var email = acc.TryGetProperty("email", out var emElem) ? emElem.GetString() : null;
                var name = acc.TryGetProperty("name", out var nmElem) ? nmElem.GetString() : null;

                if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(email)) continue;

                var detailPath = Path.Combine(AccountsDetailDir, $"{id}.json");
                var profile = new Profile
                {
                    Name = !string.IsNullOrWhiteSpace(name) ? name : email,
                    Email = email,
                    CapturedAtUtc = DateTime.UtcNow
                };

                if (File.Exists(detailPath))
                {
                    try
                    {
                        var detailText = await File.ReadAllTextAsync(detailPath);
                        ParseDetailJson(profile, detailText);
                    }
                    catch { }
                }

                if (id == currentId)
                {
                    profile.IsCurrent = true;
                }

                // Check if profile already exists in ProfileStore to preserve existing metadata
                var existing = ProfileStore.Load().FirstOrDefault(p => p.Email.Equals(email, StringComparison.OrdinalIgnoreCase));
                if (existing != null)
                {
                    profile.FilePath = existing.FilePath;
                    if (existing.Values != null && existing.Values.Count > 0)
                    {
                        profile.Values = existing.Values;
                    }
                }

                ProfileStore.Save(profile);
                imported.Add(profile);
            }
        }
        catch { }

        return imported;
    }

    public static void ParseDetailJson(Profile profile, string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("token", out var token))
            {
                if (token.TryGetProperty("access_token", out var at))
                    profile.AccessToken = at.GetString();
                if (token.TryGetProperty("refresh_token", out var rt))
                    profile.RefreshToken = rt.GetString();
                if (token.TryGetProperty("id_token", out var it))
                    profile.IdToken = it.GetString();
                if (token.TryGetProperty("expiry_timestamp", out var exp) && exp.TryGetInt64(out var expTs))
                    profile.ExpiryTimestamp = expTs;
            }

            if (root.TryGetProperty("quota", out var quota))
            {
                if (quota.TryGetProperty("subscription_tier", out var tier))
                {
                    profile.SubscriptionTier = tier.GetString();
                    profile.Plan = profile.SubscriptionTier;
                }

                if (quota.TryGetProperty("last_updated", out var lu) && lu.TryGetInt64(out var luTs))
                {
                    profile.QuotaUpdatedAt = DateTimeOffset.FromUnixTimeSeconds(luTs).UtcDateTime;
                }

                if (quota.TryGetProperty("quota_groups", out var groups))
                {
                    foreach (var group in groups.EnumerateArray())
                    {
                        if (!group.TryGetProperty("buckets", out var buckets)) continue;
                        foreach (var bucket in buckets.EnumerateArray())
                        {
                            var bId = bucket.TryGetProperty("bucket_id", out var bid) ? bid.GetString() ?? "" : "";
                            var win = bucket.TryGetProperty("window", out var w) ? w.GetString() ?? "" : "";
                            double frac = bucket.TryGetProperty("remaining_fraction", out var f) && f.TryGetDouble(out var fd) ? fd : 1.0;
                            string? rTime = bucket.TryGetProperty("reset_time", out var rt) ? rt.GetString() : null;

                            if (bId.Contains("5h", StringComparison.OrdinalIgnoreCase) || win.Equals("5h", StringComparison.OrdinalIgnoreCase))
                            {
                                if (profile.Quota5hFraction == null || bId.StartsWith("gemini", StringComparison.OrdinalIgnoreCase))
                                {
                                    profile.Quota5hFraction = frac;
                                    profile.Quota5hResetTime = rTime;
                                }
                            }
                            else if (bId.Contains("weekly", StringComparison.OrdinalIgnoreCase) || win.Equals("weekly", StringComparison.OrdinalIgnoreCase))
                            {
                                if (profile.QuotaWeeklyFraction == null || bId.StartsWith("gemini", StringComparison.OrdinalIgnoreCase))
                                {
                                    profile.QuotaWeeklyFraction = frac;
                                    profile.QuotaWeeklyResetTime = rTime;
                                }
                            }
                        }
                    }
                }

                if (profile.QuotaWeeklyFraction == null && quota.TryGetProperty("models", out var modelsArr) && modelsArr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var m in modelsArr.EnumerateArray())
                    {
                        var mName = m.TryGetProperty("name", out var mn) ? mn.GetString() ?? "" : "";
                        if (mName.Contains("flash", StringComparison.OrdinalIgnoreCase) || mName.Contains("pro", StringComparison.OrdinalIgnoreCase))
                        {
                            if (m.TryGetProperty("percentage", out var p) && p.TryGetDouble(out var pd))
                            {
                                profile.QuotaWeeklyFraction = pd / 100.0;
                            }
                            if (m.TryGetProperty("reset_time", out var rt))
                            {
                                profile.QuotaWeeklyResetTime = rt.GetString();
                            }
                            if (profile.QuotaWeeklyFraction.HasValue) break;
                        }
                    }
                }
            }
        }
        catch { }
    }

    public static async Task ApplySystemCredentialsAsync(Profile profile)
    {
        // 1. Ensure access token is fresh if possible
        if (!string.IsNullOrEmpty(profile.RefreshToken))
        {
            await AgQuotaService.EnsureFreshTokenAsync(profile);
        }

        // 2. Write ~/.gemini/oauth_creds.json
        try
        {
            var dir = Path.GetDirectoryName(GeminiCredsPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            long expiryMs = profile.ExpiryTimestamp.HasValue
                ? profile.ExpiryTimestamp.Value * 1000
                : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 3600_000;

            var credsObj = new JsonObject
            {
                ["access_token"] = profile.AccessToken ?? "",
                ["refresh_token"] = profile.RefreshToken ?? "",
                ["token_type"] = "Bearer",
                ["expiry_date"] = expiryMs,
                ["id_token"] = profile.IdToken ?? "",
                ["scope"] = "https://www.googleapis.com/auth/userinfo.email openid https://www.googleapis.com/auth/cloud-platform https://www.googleapis.com/auth/userinfo.profile"
            };

            await File.WriteAllTextAsync(GeminiCredsPath, credsObj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }

        // 3. Write Windows Credential Manager: gemini:antigravity
        try
        {
            var expiryIso = profile.ExpiryTimestamp.HasValue
                ? DateTimeOffset.FromUnixTimeSeconds(profile.ExpiryTimestamp.Value).ToLocalTime().ToString("yyyy-MM-ddTHH:mm:ss.ffffffzzz")
                : DateTimeOffset.UtcNow.AddHours(1).ToLocalTime().ToString("yyyy-MM-ddTHH:mm:ss.ffffffzzz");

            var tokenNode = new JsonObject
            {
                ["access_token"] = profile.AccessToken ?? "",
                ["token_type"] = "Bearer",
                ["refresh_token"] = profile.RefreshToken ?? "",
                ["expiry"] = expiryIso
            };

            var credManagerObj = new JsonObject
            {
                ["token"] = tokenNode,
                ["auth_method"] = "consumer",
                ["id_token"] = profile.IdToken ?? ""
            };

            CredentialManager.WriteCredential("gemini:antigravity", "antigravity", credManagerObj.ToJsonString());
        }
        catch { }

        // 4. Update .antigravity_tools/accounts.json current_account_id if applicable
        try
        {
            if (File.Exists(AccountsJsonPath))
            {
                var text = await File.ReadAllTextAsync(AccountsJsonPath);
                var node = JsonNode.Parse(text);
                if (node is JsonObject obj && obj.TryGetPropertyValue("accounts", out var accNode) && accNode is JsonArray arr)
                {
                    foreach (var item in arr)
                    {
                        if (item is JsonObject itemObj &&
                            itemObj.TryGetPropertyValue("email", out var emailVal) &&
                            profile.Email.Equals(emailVal?.ToString(), StringComparison.OrdinalIgnoreCase))
                        {
                            if (itemObj.TryGetPropertyValue("id", out var idVal) && idVal != null)
                            {
                                obj["current_account_id"] = idVal.ToString();
                                await File.WriteAllTextAsync(AccountsJsonPath, obj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                                break;
                            }
                        }
                    }
                }
            }
        }
        catch { }
    }
}

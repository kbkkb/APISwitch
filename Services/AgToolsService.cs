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

                if (profile.Values.Count == 0 && !string.IsNullOrEmpty(profile.AccessToken) && !string.IsNullOrEmpty(profile.RefreshToken))
                {
                    profile.Values = AgAuthHelper.BuildAuthValues(profile);
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

            if (root.TryGetProperty("device_profile", out var dpElem))
            {
                profile.DeviceProfile = new DeviceProfile
                {
                    MachineId = dpElem.TryGetProperty("machine_id", out var mid) ? mid.GetString() : null,
                    MacMachineId = dpElem.TryGetProperty("mac_machine_id", out var mmid) ? mmid.GetString() : null,
                    DevDeviceId = dpElem.TryGetProperty("dev_device_id", out var ddid) ? ddid.GetString() : null,
                    SqmId = dpElem.TryGetProperty("sqm_id", out var sqm) ? sqm.GetString() : null,
                };
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
                        var groupName = group.TryGetProperty("display_name", out var gn) ? gn.GetString() ?? "" : "";
                        bool is3pGroup = groupName.Contains("Claude", StringComparison.OrdinalIgnoreCase) || groupName.Contains("GPT", StringComparison.OrdinalIgnoreCase);

                        if (!group.TryGetProperty("buckets", out var buckets)) continue;
                        foreach (var bucket in buckets.EnumerateArray())
                        {
                            var bId = bucket.TryGetProperty("bucket_id", out var bid) ? bid.GetString() ?? "" : "";
                            var win = bucket.TryGetProperty("window", out var w) ? w.GetString() ?? "" : "";
                            double frac = bucket.TryGetProperty("remaining_fraction", out var f) && f.TryGetDouble(out var fd) ? fd : 1.0;
                            string? rTime = bucket.TryGetProperty("reset_time", out var rt) ? rt.GetString() : null;

                            bool is5h = bId.Contains("5h", StringComparison.OrdinalIgnoreCase) || win.Equals("5h", StringComparison.OrdinalIgnoreCase);
                            bool isWeekly = bId.Contains("weekly", StringComparison.OrdinalIgnoreCase) || win.Equals("weekly", StringComparison.OrdinalIgnoreCase);

                            if (bId.StartsWith("3p", StringComparison.OrdinalIgnoreCase) || is3pGroup)
                            {
                                if (is5h)
                                {
                                    profile.Quota3p5hFraction = frac;
                                    profile.Quota3p5hResetTime = rTime;
                                }
                                else if (isWeekly)
                                {
                                    profile.Quota3pWeeklyFraction = frac;
                                    profile.Quota3pWeeklyResetTime = rTime;
                                }
                            }
                            else
                            {
                                if (is5h)
                                {
                                    profile.Quota5hFraction = frac;
                                    profile.Quota5hResetTime = rTime;
                                }
                                else if (isWeekly)
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

                if (!profile.IsProTier)
                {
                    profile.Quota5hFraction = null;
                    profile.Quota5hResetTime = null;
                    profile.Quota3p5hFraction = null;
                    profile.Quota3p5hResetTime = null;
                    profile.Quota3pWeeklyFraction = null;
                    profile.Quota3pWeeklyResetTime = null;
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

        // 5. Sync virtual device profile with storage.json
        await SyncStorageDeviceProfileAsync(profile);
    }

    public static async Task SyncStorageDeviceProfileAsync(Profile profile)
    {
        try
        {
            var dp = profile.DeviceProfile;
            if (dp == null && Directory.Exists(AccountsDetailDir))
            {
                foreach (var file in Directory.GetFiles(AccountsDetailDir, "*.json"))
                {
                    try
                    {
                        var txt = await File.ReadAllTextAsync(file);
                        using var doc = JsonDocument.Parse(txt);
                        if (doc.RootElement.TryGetProperty("email", out var em) &&
                            profile.Email.Equals(em.GetString(), StringComparison.OrdinalIgnoreCase) &&
                            doc.RootElement.TryGetProperty("device_profile", out var dpElem))
                        {
                            dp = new DeviceProfile
                            {
                                MachineId = dpElem.TryGetProperty("machine_id", out var mid) ? mid.GetString() : null,
                                MacMachineId = dpElem.TryGetProperty("mac_machine_id", out var mmid) ? mmid.GetString() : null,
                                DevDeviceId = dpElem.TryGetProperty("dev_device_id", out var ddid) ? ddid.GetString() : null,
                                SqmId = dpElem.TryGetProperty("sqm_id", out var sqm) ? sqm.GetString() : null,
                            };
                            profile.DeviceProfile = dp;
                            break;
                        }
                    }
                    catch { }
                }
            }

            if (dp == null) return;

            foreach (var userDir in AgPaths.CandidateUserDirs)
            {
                var storageJson = Path.Combine(userDir, "User", "globalStorage", "storage.json");
                if (!File.Exists(storageJson)) continue;

                var text = await File.ReadAllTextAsync(storageJson);
                var node = JsonNode.Parse(text);
                if (node is JsonObject obj)
                {
                    if (!string.IsNullOrEmpty(dp.MachineId)) obj["telemetry.machineId"] = dp.MachineId;
                    if (!string.IsNullOrEmpty(dp.MacMachineId)) obj["telemetry.macMachineId"] = dp.MacMachineId;
                    if (!string.IsNullOrEmpty(dp.DevDeviceId)) obj["telemetry.devDeviceId"] = dp.DevDeviceId;
                    if (!string.IsNullOrEmpty(dp.SqmId)) obj["telemetry.sqmId"] = dp.SqmId;

                    await File.WriteAllTextAsync(storageJson, obj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                }
            }
        }
        catch { }
    }

    /// <summary>
    /// 将从官方拉取到的最新配额信息同步回写到 ~/.antigravity_tools/accounts/*.json，确保与 Antigravity Tools 插件完全一致
    /// </summary>
    public static async Task SyncQuotaToToolsAsync(Profile profile, string? quotaSummaryJson, string? availableModelsJson = null)
    {
        if (!Directory.Exists(AccountsDetailDir)) return;

        try
        {
            string? targetFile = null;
            JsonObject? targetObj = null;

            foreach (var file in Directory.GetFiles(AccountsDetailDir, "*.json"))
            {
                try
                {
                    var text = await File.ReadAllTextAsync(file);
                    var node = JsonNode.Parse(text);
                    if (node is JsonObject obj &&
                        obj.TryGetPropertyValue("email", out var emVal) &&
                        profile.Email.Equals(emVal?.ToString(), StringComparison.OrdinalIgnoreCase))
                    {
                        targetFile = file;
                        targetObj = obj;
                        break;
                    }
                }
                catch { }
            }

            if (targetFile == null || targetObj == null) return;

            if (!targetObj.TryGetPropertyValue("quota", out var quotaNode) || quotaNode is not JsonObject quotaObj)
            {
                quotaObj = new JsonObject();
                targetObj["quota"] = quotaObj;
            }

            quotaObj["last_updated"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (!string.IsNullOrEmpty(profile.SubscriptionTier))
            {
                quotaObj["subscription_tier"] = profile.SubscriptionTier;
            }

            if (!profile.IsProTier)
            {
                // 免费账号：回写时仅写入 Gemini Models 组与 gemini-weekly 桶，绝不写入 5H 或 3P
                var geminiWeeklyBucket = new JsonObject
                {
                    ["bucket_id"] = "gemini-weekly",
                    ["window"] = "weekly",
                    ["remaining_fraction"] = profile.QuotaWeeklyFraction ?? 1.0,
                    ["reset_time"] = profile.QuotaWeeklyResetTime ?? "",
                    ["display_name"] = "Weekly Limit Remaining",
                    ["description"] = "You have used some of your weekly limit, it will fully refresh in 6 days."
                };
                var geminiGroup = new JsonObject
                {
                    ["display_name"] = "Gemini Models",
                    ["description"] = "Models within this group: Gemini Flash, Gemini Pro",
                    ["buckets"] = new JsonArray { geminiWeeklyBucket }
                };
                quotaObj["quota_groups"] = new JsonArray { geminiGroup };
            }
            else if (!string.IsNullOrEmpty(quotaSummaryJson))
            {
                using var doc = JsonDocument.Parse(quotaSummaryJson);
                if (doc.RootElement.TryGetProperty("groups", out var groupsElem))
                {
                    var groupsArr = new JsonArray();
                    foreach (var g in groupsElem.EnumerateArray())
                    {
                        var gObj = new JsonObject
                        {
                            ["display_name"] = g.TryGetProperty("displayName", out var dn) ? dn.GetString() ?? "" : "",
                            ["description"] = g.TryGetProperty("description", out var desc) ? desc.GetString() ?? "" : ""
                        };
                        var bucketsArr = new JsonArray();
                        if (g.TryGetProperty("buckets", out var bElem))
                        {
                            foreach (var b in bElem.EnumerateArray())
                            {
                                var bObj = new JsonObject
                                {
                                    ["bucket_id"] = b.TryGetProperty("bucketId", out var bid) ? bid.GetString() ?? "" : "",
                                    ["window"] = b.TryGetProperty("window", out var w) ? w.GetString() ?? "" : "",
                                    ["remaining_fraction"] = b.TryGetProperty("remainingFraction", out var rf) && rf.TryGetDouble(out var d) ? d : 1.0,
                                    ["reset_time"] = b.TryGetProperty("resetTime", out var rt) ? rt.GetString() : "",
                                    ["display_name"] = b.TryGetProperty("displayName", out var bdn) ? bdn.GetString() ?? "" : "",
                                    ["description"] = b.TryGetProperty("description", out var bdesc) ? bdesc.GetString() ?? "" : ""
                                };
                                bucketsArr.Add(bObj);
                            }
                        }
                        gObj["buckets"] = bucketsArr;
                        groupsArr.Add(gObj);
                    }
                    quotaObj["quota_groups"] = groupsArr;
                }
            }

            if (!string.IsNullOrEmpty(availableModelsJson))
            {
                using var doc = JsonDocument.Parse(availableModelsJson);
                if (doc.RootElement.TryGetProperty("models", out var modelsElem))
                {
                    if (quotaObj.TryGetPropertyValue("models", out var existModels) && existModels is JsonArray mArr)
                    {
                        foreach (var mNode in mArr)
                        {
                            if (mNode is JsonObject mObj && mObj.TryGetPropertyValue("name", out var mNameVal))
                            {
                                var mName = mNameVal?.ToString();
                                if (!string.IsNullOrEmpty(mName) && modelsElem.TryGetProperty(mName, out var mProp))
                                {
                                    if (mProp.TryGetProperty("quotaInfo", out var qInfo))
                                    {
                                        if (qInfo.TryGetProperty("remainingFraction", out var frac) && frac.TryGetDouble(out var fd))
                                            mObj["percentage"] = (int)Math.Round(fd * 100);
                                        if (qInfo.TryGetProperty("resetTime", out var rt))
                                            mObj["reset_time"] = rt.GetString();
                                    }
                                }
                            }
                        }
                    }
                }
            }

            await File.WriteAllTextAsync(targetFile, targetObj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}

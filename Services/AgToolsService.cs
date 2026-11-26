using System.IO;
using System.Text;
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

                var existing = ProfileStore.Load().FirstOrDefault(p => p.Email.Equals(email, StringComparison.OrdinalIgnoreCase));
                if (existing != null)
                {
                    profile.FilePath = existing.FilePath;
                    if (existing.Values != null && existing.Values.Count > 0)
                    {
                        profile.Values = existing.Values;
                    }
                    if (existing.IsPaidSubscription.HasValue && !profile.IsPaidSubscription.HasValue)
                    {
                        profile.IsPaidSubscription = existing.IsPaidSubscription;
                    }
                    if (existing.IsActivated.HasValue)
                    {
                        profile.IsActivated = existing.IsActivated;
                        profile.ActivatedAt = existing.ActivatedAt;
                        profile.ActivationLatencyMs = existing.ActivationLatencyMs;
                        profile.ActivationError = existing.ActivationError;
                    }
                    if (existing.QuotaExhausted)
                    {
                        profile.QuotaExhausted = existing.QuotaExhausted;
                        profile.QuotaExhaustedResetAt = existing.QuotaExhaustedResetAt;
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
                        bool isArgonGroup = groupName.Contains("Argon", StringComparison.OrdinalIgnoreCase);
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
                            bool isArgon = isArgonGroup || bId.Contains("argon", StringComparison.OrdinalIgnoreCase);

                            if (isArgon)
                            {
                                if (is5h)
                                {
                                    profile.QuotaArgon5hFraction = frac;
                                    profile.QuotaArgon5hResetTime = rTime;
                                }
                                else if (isWeekly)
                                {
                                    profile.QuotaArgonWeeklyFraction = frac;
                                    profile.QuotaArgonWeeklyResetTime = rTime;
                                }
                            }
                            else if (bId.StartsWith("3p", StringComparison.OrdinalIgnoreCase) || is3pGroup)
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
                    profile.QuotaArgon5hFraction = null;
                    profile.QuotaArgon5hResetTime = null;
                }
                else if (profile.QuotaArgonWeeklyFraction.HasValue || profile.QuotaArgon5hFraction.HasValue)
                {
                    profile.IsPaidSubscription = true;
                }
            }
        }
        catch { }
    }

    public const string CredTargetName = "gemini:antigravity";
    public const string CredUserName = "antigravity";
    private const string DefaultWriterId = "gWSXHqikCkBTlM3ukNgQF0j1KYZ7_i1CimRQJIFxY0A";

    public sealed record SystemCredSnapshot(
        string? Email,
        string? AccessToken,
        string? RefreshToken,
        string? IdToken,
        long? ExpiryUnixSec,
        string? WriterId);

    public static string BuildCredentialManagerJson(Profile profile, string? existingWriterId = null)
    {
        long nowSec = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        long expSec = profile.ExpiryTimestamp.HasValue && profile.ExpiryTimestamp.Value > nowSec
            ? profile.ExpiryTimestamp.Value
            : nowSec + 3600;
        long expiresIn = Math.Max(60L, expSec - nowSec);
        var expiryIso = DateTimeOffset.FromUnixTimeSeconds(expSec)
            .ToLocalTime()
            .ToString("yyyy-MM-ddTHH:mm:ss.fffffffzzz");

        var tokenNode = new JsonObject
        {
            ["access_token"] = profile.AccessToken ?? "",
            ["token_type"] = "Bearer",
            ["refresh_token"] = profile.RefreshToken ?? "",
            ["expiry"] = expiryIso,
            ["expires_in"] = expiresIn
        };

        var credManagerObj = new JsonObject
        {
            ["token"] = tokenNode,
            ["auth_method"] = "consumer",
            ["id_token_email"] = profile.Email ?? "",
            ["writer_id"] = !string.IsNullOrWhiteSpace(existingWriterId) ? existingWriterId : DefaultWriterId
        };

        if (!string.IsNullOrEmpty(profile.IdToken))
        {
            credManagerObj["id_token"] = profile.IdToken;
        }

        return credManagerObj.ToJsonString();
    }

    public static SystemCredSnapshot? ParseCredentialManagerJson(string? json, string? fallbackEmail = null)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            string? accessToken = null;
            string? refreshToken = null;
            long? expiryUnixSec = null;

            if (root.TryGetProperty("token", out var tokenElem) && tokenElem.ValueKind == JsonValueKind.Object)
            {
                if (tokenElem.TryGetProperty("access_token", out var at) && at.ValueKind == JsonValueKind.String)
                    accessToken = at.GetString();
                if (tokenElem.TryGetProperty("refresh_token", out var rt) && rt.ValueKind == JsonValueKind.String)
                    refreshToken = rt.GetString();
                if (tokenElem.TryGetProperty("expiry", out var exp) && exp.ValueKind == JsonValueKind.String)
                {
                    if (DateTimeOffset.TryParse(exp.GetString(), out var dto))
                        expiryUnixSec = dto.ToUnixTimeSeconds();
                }
                if (!expiryUnixSec.HasValue && tokenElem.TryGetProperty("expires_in", out var expIn) && expIn.TryGetInt64(out var secs) && secs > 0)
                {
                    expiryUnixSec = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + secs;
                }
            }

            if (string.IsNullOrEmpty(accessToken) && string.IsNullOrEmpty(refreshToken))
                return null;

            string? email = root.TryGetProperty("id_token_email", out var em) && em.ValueKind == JsonValueKind.String
                ? em.GetString()
                : null;
            string? idToken = root.TryGetProperty("id_token", out var idt) && idt.ValueKind == JsonValueKind.String
                ? idt.GetString()
                : null;
            string? writerId = root.TryGetProperty("writer_id", out var wid) && wid.ValueKind == JsonValueKind.String
                ? wid.GetString()
                : null;

            if (string.IsNullOrWhiteSpace(email) && !string.IsNullOrEmpty(idToken))
            {
                email = TryDecodeEmailFromJwt(idToken);
            }

            if (string.IsNullOrWhiteSpace(email))
            {
                email = fallbackEmail;
            }

            return new SystemCredSnapshot(email, accessToken, refreshToken, idToken, expiryUnixSec, writerId);
        }
        catch
        {
            return null;
        }
    }

    static string? TryDecodeEmailFromJwt(string jwt)
    {
        try
        {
            var parts = jwt.Split('.');
            if (parts.Length < 2) return null;
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            switch (payload.Length % 4)
            {
                case 2: payload += "=="; break;
                case 3: payload += "="; break;
            }
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(payload));
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("email", out var em) && em.ValueKind == JsonValueKind.String)
                return em.GetString();
        }
        catch { }
        return null;
    }

    public static string? ReadHubLastLoginEmail()
    {
        try
        {
            var path = AgPaths.HubAppStoragePath;
            if (!File.Exists(path)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("agy.onboarding.lastLoginUsername", out var val) &&
                val.ValueKind == JsonValueKind.String)
            {
                var email = val.GetString();
                return string.IsNullOrWhiteSpace(email) ? null : email.Trim();
            }
        }
        catch { }
        return null;
    }

    public static void WriteHubLastLoginEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email)) return;
        try
        {
            var path = AgPaths.HubAppStoragePath;
            var dir = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(dir)) return;
            if (!File.Exists(path) && !Directory.Exists(dir)) return;

            JsonObject obj;
            if (File.Exists(path))
            {
                var parsed = JsonNode.Parse(File.ReadAllText(path));
                obj = parsed as JsonObject ?? new JsonObject();
            }
            else
            {
                obj = new JsonObject();
            }

            obj["agy.onboarding.lastLoginUsername"] = email.Trim();
            var tmpPath = path + ".tmp";
            File.WriteAllText(tmpPath, obj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmpPath, path, overwrite: true);
        }
        catch { }
    }

    public static string? CaptureCurrentActiveCredentialsToStore()
    {
        var hubEmail = ReadHubLastLoginEmail();
        if (AgPaths.IsSandboxed)
            return hubEmail;

        var rawCred = CredentialManager.ReadCredential(CredTargetName);
        var snap = ParseCredentialManagerJson(rawCred, hubEmail);
        if (snap == null || string.IsNullOrWhiteSpace(snap.Email))
            return hubEmail;

        try
        {
            var email = snap.Email.Trim();
            var profiles = ProfileStore.Load();
            var existing = profiles.FirstOrDefault(p =>
                !string.IsNullOrWhiteSpace(p.Email) &&
                p.Email.Equals(email, StringComparison.OrdinalIgnoreCase));

            bool isNew = existing == null;
            var profile = existing ?? new Profile
            {
                Name = email,
                Email = email,
                CapturedAtUtc = DateTime.UtcNow
            };

            bool changed = isNew;
            if (!string.IsNullOrEmpty(snap.RefreshToken) &&
                !string.Equals(profile.RefreshToken, snap.RefreshToken, StringComparison.Ordinal))
            {
                profile.RefreshToken = snap.RefreshToken;
                changed = true;
            }

            bool incomingAccessIsNewer = !string.IsNullOrEmpty(snap.AccessToken) &&
                (!profile.ExpiryTimestamp.HasValue
                 || (snap.ExpiryUnixSec.HasValue && snap.ExpiryUnixSec.Value > profile.ExpiryTimestamp.Value)
                 || string.IsNullOrEmpty(profile.AccessToken)
                 || changed);

            if (incomingAccessIsNewer && !string.Equals(profile.AccessToken, snap.AccessToken, StringComparison.Ordinal))
            {
                profile.AccessToken = snap.AccessToken;
                changed = true;
            }

            if (snap.ExpiryUnixSec.HasValue &&
                (!profile.ExpiryTimestamp.HasValue || snap.ExpiryUnixSec.Value > profile.ExpiryTimestamp.Value || changed))
            {
                profile.ExpiryTimestamp = snap.ExpiryUnixSec.Value;
                changed = true;
            }

            if (!string.IsNullOrEmpty(snap.IdToken) &&
                !string.Equals(profile.IdToken, snap.IdToken, StringComparison.Ordinal))
            {
                profile.IdToken = snap.IdToken;
                changed = true;
            }

            if (changed)
            {
                profile.ActivationError = null;
                profile.CapturedAtUtc = DateTime.UtcNow;
                var authValues = AgAuthHelper.BuildAuthValues(profile);
                if (authValues.TryGetValue(AgState.KeyOauthToken, out var otVal))
                    profile.Values[AgState.KeyOauthToken] = otVal;
                if (authValues.TryGetValue(AgState.KeyUserStatus, out var usVal))
                    profile.Values[AgState.KeyUserStatus] = usVal;
                ProfileStore.Save(profile);
            }

            return email;
        }
        catch
        {
            return snap.Email ?? hubEmail;
        }
    }

    public static async Task ApplySystemCredentialsAsync(Profile profile)
    {
        _ = await TryApplySystemCredentialsAsync(profile, forceTokenRefresh: false, syncIdeMeta: true);
    }

    public static async Task<(bool Ok, bool AuthExpired, string? Error)> TryApplySystemCredentialsAsync(
        Profile profile, bool forceTokenRefresh = false, bool syncIdeMeta = true)
    {
        // 1. Ensure access token is fresh and verified before touching system credentials
        if (!string.IsNullOrEmpty(profile.RefreshToken) || forceTokenRefresh)
        {
            var (ok, authExpired, err) = await AgQuotaService.EnsureFreshTokenDetailedAsync(
                profile, forceRefresh: forceTokenRefresh, syncSystemCreds: false);
            if (!ok || string.IsNullOrEmpty(profile.AccessToken))
            {
                return (false, authExpired, err ?? I18nService.T("AgQ.ErrTokenRefresh"));
            }
        }
        else if (string.IsNullOrEmpty(profile.AccessToken))
        {
            return (false, true, I18nService.T("AgQ.ErrNoCredentials"));
        }

        var written = await WriteSystemCredentialFilesOnlyAsync(profile, syncIdeMeta);
        return written
            ? (true, false, null)
            : (false, false, "Failed to write Windows Credential Manager entry");
    }

    public static async Task<bool> WriteSystemCredentialFilesOnlyAsync(Profile profile, bool syncIdeMeta = true)
    {
        bool credWritten = true;

        // 2. Write ~/.gemini/oauth_creds.json and Windows Credential Manager: gemini:antigravity (skip real host state when sandboxed)
        if (!AgPaths.IsSandboxed)
        {
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
                    ["scope"] = AgAuthFlow.DefaultOAuthScopes
                };

                await File.WriteAllTextAsync(GeminiCredsPath, credsObj.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }

            try
            {
                var existingSnap = ParseCredentialManagerJson(CredentialManager.ReadCredential(CredTargetName));
                var payload = BuildCredentialManagerJson(profile, existingSnap?.WriterId);
                credWritten = CredentialManager.WriteCredential(CredTargetName, CredUserName, payload);
            }
            catch
            {
                credWritten = false;
            }
        }

        if (syncIdeMeta)
        {
            // 3. Sync Hub onboarding lastLoginUsername in app_storage.json
            WriteHubLastLoginEmail(profile.Email);

            // 4. Update .antigravity_tools/accounts.json current_account_id if applicable
            if (!AgPaths.IsSandboxed)
            {
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

            // 5. Sync virtual device profile with storage.json
            await SyncStorageDeviceProfileAsync(profile);
        }

        return credWritten;
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

            if (!string.IsNullOrEmpty(quotaSummaryJson))
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
            else
            {
                // 手动兜底构建配额组
                var groupsArr = new JsonArray();
                var geminiBuckets = new JsonArray
                {
                    new JsonObject
                    {
                        ["bucket_id"] = "gemini-weekly",
                        ["window"] = "weekly",
                        ["remaining_fraction"] = profile.QuotaWeeklyFraction ?? 1.0,
                        ["reset_time"] = profile.QuotaWeeklyResetTime ?? "",
                        ["display_name"] = "Weekly Limit Remaining",
                        ["description"] = "You have used some of your weekly limit, it will fully refresh in 6 days."
                    }
                };
                if (profile.IsProTier && profile.Quota5hFraction.HasValue)
                {
                    geminiBuckets.Add(new JsonObject
                    {
                        ["bucket_id"] = "gemini-5h",
                        ["window"] = "5h",
                        ["remaining_fraction"] = profile.Quota5hFraction.Value,
                        ["reset_time"] = profile.Quota5hResetTime ?? "",
                        ["display_name"] = "Five Hour Limit Remaining"
                    });
                }
                groupsArr.Add(new JsonObject
                {
                    ["display_name"] = "Gemini Models",
                    ["description"] = "Models within this group: Gemini Flash, Gemini Pro",
                    ["buckets"] = geminiBuckets
                });

                if (profile.Quota3pWeeklyFraction.HasValue)
                {
                    var p3Buckets = new JsonArray
                    {
                        new JsonObject
                        {
                            ["bucket_id"] = "3p-weekly",
                            ["window"] = "weekly",
                            ["remaining_fraction"] = profile.Quota3pWeeklyFraction.Value,
                            ["reset_time"] = profile.Quota3pWeeklyResetTime ?? "",
                            ["display_name"] = "Weekly Limit Remaining"
                        }
                    };
                    if (profile.IsProTier && profile.Quota3p5hFraction.HasValue)
                    {
                        p3Buckets.Add(new JsonObject
                        {
                            ["bucket_id"] = "3p-5h",
                            ["window"] = "5h",
                            ["remaining_fraction"] = profile.Quota3p5hFraction.Value,
                            ["reset_time"] = profile.Quota3p5hResetTime ?? "",
                            ["display_name"] = "Five Hour Limit Remaining"
                        });
                    }
                    groupsArr.Add(new JsonObject
                    {
                        ["display_name"] = "Claude and GPT models",
                        ["description"] = "Models within this group: Claude Opus, Claude Sonnet, GPT-OSS",
                        ["buckets"] = p3Buckets
                    });
                }
                quotaObj["quota_groups"] = groupsArr;
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

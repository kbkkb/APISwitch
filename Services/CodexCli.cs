using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using APISwitch.Models;
using Microsoft.Data.Sqlite;
using Tomlyn;
using Tomlyn.Model;

namespace APISwitch.Services;

public static class CodexCli
{
    public static string CodexDir => Path.Combine(ClaudeCli.HomeDir, ".codex");

    public static string ConfigPath => Path.Combine(CodexDir, "config.toml");
    public static string AuthPath => Path.Combine(CodexDir, "auth.json");
    public static string CatalogPath => Path.Combine(CodexDir, "apiswitch-model-catalog.json");
    public const string CatalogFileName = "apiswitch-model-catalog.json";

    static readonly JsonSerializerOptions WriteOpts = new() { WriteIndented = true };

    public static string? CurrentProviderId()
    {
        var text = FileUtil.ReadTextIfExists(ConfigPath);
        if (string.IsNullOrWhiteSpace(text)) return null;
        try
        {
            var model = TomlSerializer.Deserialize<TomlTable>(text);
            if (model == null) return null;

            // If no model_provider is configured, it is official OpenAI
            if (!model.TryGetValue("model_provider", out var mpObj) || mpObj == null)
                return null;

            var mp = mpObj.ToString()?.Trim();
            if (string.IsNullOrEmpty(mp)) return null;

            // 1. Check explicit apiswitch_provider_id recorded during Apply
            if (model.TryGetValue("apiswitch_provider_id", out var apId) && !string.IsNullOrWhiteSpace(apId?.ToString()))
            {
                return apId.ToString()!.Trim();
            }

            // 2. Check model_providers table to match against known providers by BaseUrl
            if (model.TryGetValue("model_providers", out var mpTableObj) && mpTableObj is TomlTable providersTable)
            {
                TomlTable? targetEntry = null;
                if (providersTable.TryGetValue("custom", out var cObj) && cObj is TomlTable cTable)
                    targetEntry = cTable;
                else if (providersTable.TryGetValue(mp, out var mObj) && mObj is TomlTable mTable)
                    targetEntry = mTable;

                if (targetEntry != null && targetEntry.TryGetValue("base_url", out var urlObj))
                {
                    var url = urlObj?.ToString()?.TrimEnd('/');
                    if (!string.IsNullOrEmpty(url))
                    {
                        var all = CliStore.LoadCodex();
                        var matched = all.FirstOrDefault(x => !x.IsOfficial && string.Equals(x.BaseUrl?.TrimEnd('/'), url, StringComparison.OrdinalIgnoreCase));
                        if (matched != null) return matched.Id;
                    }
                }
            }

            // 3. Fallback to model_provider if not "custom"
            if (!string.Equals(mp, "custom", StringComparison.OrdinalIgnoreCase))
            {
                return mp;
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    public static CodexProvider? GetActiveProvider()
    {
        if (LocalProxyServer.ActiveCodexProvider != null)
            return LocalProxyServer.ActiveCodexProvider;
        var currId = CurrentProviderId();
        if (string.IsNullOrEmpty(currId)) return null;
        var all = CliStore.LoadCodex();
        return all.FirstOrDefault(x => string.Equals(x.Id, currId, StringComparison.OrdinalIgnoreCase) ||
                                       string.Equals(x.Name, currId, StringComparison.OrdinalIgnoreCase));
    }

    public static void ReapplyCurrent()
    {
        var p = GetActiveProvider();
        if (p != null)
        {
            Apply(p);
        }
        else
        {
            Apply(new CodexProvider { Name = "官方", IsOfficial = true });
        }
    }

    public static void Apply(CodexProvider p)
    {
        var text = FileUtil.ReadTextIfExists(ConfigPath) ?? "";
        TomlTable model;
        try
        {
            model = TomlSerializer.Deserialize<TomlTable>(text) ?? new TomlTable();
        }
        catch
        {
            // Preserve non-APISwitch settings if the active file is malformed but the automatic backup is valid.
            model = TomlSerializer.Deserialize<TomlTable>(FileUtil.ReadTextIfExists(ConfigPath + ".bak") ?? "") ?? new TomlTable();
        }

        if (p.IsOfficial)
        {
            LocalProxyServer.ActiveCodexProvider = null;
            model.Remove("model_provider");
            model.Remove("apiswitch_provider_id");
            model.Remove("model_catalog_json");
            model.Remove("model");
            model.Remove("model_reasoning_effort");

            // Clean up custom providers from model_providers table so no stale endpoints remain
            if (model.TryGetValue("model_providers", out var mp) && mp is TomlTable providers)
            {
                var keysToRemove = providers.Keys.Where(k =>
                    !k.Equals("amazon-bedrock", StringComparison.OrdinalIgnoreCase) &&
                    !k.Equals("azure", StringComparison.OrdinalIgnoreCase)).ToList();
                foreach (var k in keysToRemove)
                {
                    providers.Remove(k);
                }
                if (providers.Count == 0)
                {
                    model.Remove("model_providers");
                }
            }

            try { if (File.Exists(CatalogPath)) File.Delete(CatalogPath); } catch { }
        }
        else
        {
            LocalProxyServer.ActiveCodexProvider = p;

            model["model_provider"] = "custom";
            model["apiswitch_provider_id"] = p.Id;
            model["disable_response_storage"] = true;

            // 思考强度：per-model 由本地代理按请求模型注入；此处写全局兜底（各模型最高档）
            var effort = ThinkingEffort.ToCodex(ThinkingEffort.MaxOf(
                p.CustomModels?.Select(m => m.ThinkingEffort) ?? Enumerable.Empty<string?>()));
            if (!string.IsNullOrEmpty(effort))
                model["model_reasoning_effort"] = effort;
            else
                model.Remove("model_reasoning_effort");
            var providers = model.TryGetValue("model_providers", out var mp) && mp is TomlTable t
                ? t
                : new TomlTable();

            var token = !string.IsNullOrWhiteSpace(p.ApiKey) ? p.ApiKey : p.BearerToken;

            var effectiveBaseUrl = LocalProxyServer.IsCodexEnabled
                ? LocalProxyServer.ProxyCodexUrl
                : (p.BaseUrl?.TrimEnd('/') ?? "");

            var effectiveWireApi = LocalProxyServer.IsCodexEnabled
                ? "responses"
                : (string.IsNullOrEmpty(p.WireApi) ? "responses" : p.WireApi);

            var entry = new TomlTable
            {
                ["name"] = !string.IsNullOrWhiteSpace(p.Name) ? p.Name : p.Id,
                ["base_url"] = effectiveBaseUrl,
                ["wire_api"] = effectiveWireApi,
                ["requires_openai_auth"] = string.IsNullOrWhiteSpace(token),
            };

            if (!string.IsNullOrWhiteSpace(token))
            {
                entry["experimental_bearer_token"] = token;
            }
            else
            {
                entry["env_key"] = "OPENAI_API_KEY";
            }

            if (p.CustomHeaders != null && p.CustomHeaders.Count > 0)
            {
                var hTable = new TomlTable();
                foreach (var kv in p.CustomHeaders)
                    if (!string.IsNullOrWhiteSpace(kv.Key))
                        hTable[kv.Key.Trim()] = kv.Value ?? "";
                entry["http_headers"] = hTable;
            }

            if (p.ExtraOptions != null && p.ExtraOptions.Count > 0)
            {
                foreach (var kv in p.ExtraOptions)
                {
                    if (string.IsNullOrWhiteSpace(kv.Key)) continue;
                    var val = kv.Value?.Trim() ?? "";
                    if (bool.TryParse(val, out var bVal)) entry[kv.Key.Trim()] = bVal;
                    else if (long.TryParse(val, out var lVal)) entry[kv.Key.Trim()] = lVal;
                    else if (double.TryParse(val, out var dVal)) entry[kv.Key.Trim()] = dVal;
                    else entry[kv.Key.Trim()] = val;
                }
            }

            // CRITICAL: Overwrite ALL existing custom / third-party provider tables with the new entry!
            // This prevents any historical thread/session from communicating with an obsolete provider URL.
            var existingKeys = providers.Keys.ToList();
            foreach (var k in existingKeys)
            {
                if (k.Equals("amazon-bedrock", StringComparison.OrdinalIgnoreCase) ||
                    k.Equals("azure", StringComparison.OrdinalIgnoreCase))
                    continue;
                providers[k] = entry;
            }

            // Always ensure "custom" is set
            providers["custom"] = entry;

            // Also ensure p.Id is mapped to entry if not "custom"
            if (!string.IsNullOrWhiteSpace(p.Id) && !string.Equals(p.Id, "custom", StringComparison.OrdinalIgnoreCase))
            {
                providers[p.Id] = entry;
            }

            // Also ensure any other known providers in CliStore have their entries updated to entry
            var allKnown = CliStore.LoadCodex();
            foreach (var kp in allKnown)
            {
                if (!kp.IsOfficial && !string.IsNullOrWhiteSpace(kp.Id))
                {
                    providers[kp.Id] = entry;
                }
            }

            model["model_providers"] = providers;

            // Generate model catalog for third-party provider so models are visible in Codex UI
            var catalogModels = new JsonArray();
            var addedSlugs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int priority = 100;

            // 1. User configured third-party models
            if (p.CustomModels != null)
            {
                foreach (var m in p.CustomModels)
                {
                    var slug = m.Id?.Trim();
                    if (string.IsNullOrWhiteSpace(slug) || !addedSlugs.Add(slug)) continue;
                    var displayName = !string.IsNullOrWhiteSpace(m.Name) ? m.Name.Trim() : slug;
                    long cw = ParseContextWindow(m.ContextWindow);
                    catalogModels.Add(CreateModelEntry(slug, displayName, priority++, cw));
                }
            }

            // 2. Default model if specified and not yet in catalog
            if (!string.IsNullOrWhiteSpace(p.Model))
            {
                var modelSlug = p.Model.Trim();
                if (addedSlugs.Add(modelSlug))
                {
                    catalogModels.Add(CreateModelEntry(modelSlug, modelSlug, priority++, 1000000));
                }
            }

            // Only include models configured by the user (CustomModels and Model).
            // Do NOT append unconfigured first-party models so they stay hidden in Codex UI.
            if (catalogModels.Count > 0)
            {
                var catalogRoot = new JsonObject
                {
                    ["models"] = catalogModels
                };
                FileUtil.AtomicWriteText(CatalogPath, catalogRoot.ToJsonString(WriteOpts));
                model["model_catalog_json"] = CatalogFileName;
            }
            else
            {
                model.Remove("model_catalog_json");
                try { if (File.Exists(CatalogPath)) File.Delete(CatalogPath); } catch { }
            }
        }

        if (!string.IsNullOrWhiteSpace(p.Model)) model["model"] = p.Model;
        else if (p.IsOfficial) model.Remove("model");

        FileUtil.AtomicWriteText(ConfigPath, TomlSerializer.Serialize(model));
        ApplyAuth(p);

        // Synchronize existing session threads in state_5.sqlite so that all third-party threads point to 'custom'
        if (!p.IsOfficial)
        {
            SyncSessionProvidersToCustom();
        }
    }

    public static void SyncSessionProvidersToCustom()
    {
        try
        {
            var dbPath = Path.Combine(CodexDir, "state_5.sqlite");
            if (!File.Exists(dbPath)) return;
            using var conn = new SqliteConnection($"Data Source={dbPath}");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE threads SET model_provider = 'custom' WHERE model_provider != 'openai'";
            cmd.ExecuteNonQuery();
        }
        catch
        {
            // Best effort, ignore if locked or busy
        }
    }

    public static long ParseContextWindow(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 1000000;
        text = text.Trim();
        try
        {
            if (text.EndsWith("m", StringComparison.OrdinalIgnoreCase))
            {
                var numStr = text[..^1].Trim();
                if (double.TryParse(numStr, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double mVal))
                {
                    return (long)(mVal * 1_000_000);
                }
            }
            else if (text.EndsWith("k", StringComparison.OrdinalIgnoreCase))
            {
                var numStr = text[..^1].Trim();
                if (double.TryParse(numStr, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double kVal))
                {
                    return (long)(kVal * 1_000);
                }
            }
            else if (long.TryParse(text, out long rawVal) && rawVal > 0)
            {
                return rawVal;
            }
        }
        catch { }

        return 1000000;
    }

    static JsonObject CreateModelEntry(string slug, string displayName, int priority, long contextWindow = 1000000, string? description = null)
    {
        if (contextWindow <= 0) contextWindow = 1000000;
        return new JsonObject
        {
            ["slug"] = slug,
            ["display_name"] = displayName,
            ["description"] = description ?? displayName,
            ["priority"] = priority,
            ["context_window"] = contextWindow,
            ["max_context_window"] = contextWindow,
            ["effective_context_window_percent"] = 95,
            ["shell_type"] = "shell_command",
            ["supported_in_api"] = true,
            ["visibility"] = "list",
            ["default_reasoning_level"] = "high",
            ["default_reasoning_summary"] = "none",
            ["support_verbosity"] = false,
            ["supported_reasoning_levels"] = new JsonArray
            {
                new JsonObject { ["effort"] = "none", ["description"] = "Disable Thinking" },
                new JsonObject { ["effort"] = "low", ["description"] = "Fast responses with lighter reasoning" },
                new JsonObject { ["effort"] = "medium", ["description"] = "Balanced reasoning" },
                new JsonObject { ["effort"] = "high", ["description"] = "Deep reasoning" }
            },
            ["supports_image_detail_original"] = false,
            ["supports_parallel_tool_calls"] = false,
            ["supports_reasoning_summaries"] = true,
            ["supports_search_tool"] = false,
            ["truncation_policy"] = new JsonObject
            {
                ["limit"] = 10000,
                ["mode"] = "bytes"
            },
            ["base_instructions"] = "You are Codex, a coding agent. You and the user share the same workspace and collaborate to achieve the user's goals.",
            ["input_modalities"] = new JsonArray { "text", "image" },
            ["experimental_supported_tools"] = new JsonArray(),
            ["additional_speed_tiers"] = new JsonArray(),
            ["availability_nux"] = null,
            ["service_tiers"] = new JsonArray(),
            ["upgrade"] = null
        };
    }

    static void ApplyAuth(CodexProvider p)
    {
        JsonObject root;
        try
        {
            root = JsonNode.Parse(FileUtil.ReadTextIfExists(AuthPath) ?? "{}")?.AsObject() ?? new JsonObject();
        }
        catch
        {
            root = new JsonObject();
        }

        var token = !string.IsNullOrWhiteSpace(p.ApiKey) ? p.ApiKey : p.BearerToken;

        if (p.IsOfficial) root.Remove("OPENAI_API_KEY");
        else if (!string.IsNullOrWhiteSpace(token)) root["OPENAI_API_KEY"] = token;

        FileUtil.AtomicWriteText(AuthPath, root.ToJsonString(WriteOpts));
    }
}


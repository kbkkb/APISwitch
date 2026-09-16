using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using APISwitch.Models;

namespace APISwitch.Services;

public static class OpenCodeCli
{
    public static string ConfigPath => Path.Combine(ClaudeCli.HomeDir, ".config", "opencode", "opencode.json");

    static readonly JsonDocumentOptions DocOpts = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    static readonly JsonSerializerOptions WriteOpts = new() { WriteIndented = true };

    public static JsonObject Load()
    {
        var text = FileUtil.ReadTextIfExists(ConfigPath);
        if (string.IsNullOrWhiteSpace(text)) return new JsonObject();
        try
        {
            return JsonNode.Parse(text, nodeOptions: null, DocOpts)?.AsObject() ?? new JsonObject();
        }
        catch
        {
            return new JsonObject();
        }
    }

    public static string? CurrentModel() => Load()["model"]?.GetValue<string>();

    public static List<string> ProviderIds()
    {
        var providers = Load()["provider"]?.AsObject();
        return providers == null ? new List<string>() : providers.Select(kv => kv.Key).ToList();
    }

    public static List<OpenCodeProvider> LoadProviders()
    {
        var result = new List<OpenCodeProvider>();
        var providers = Load()["provider"]?.AsObject();
        if (providers == null) return result;
        foreach (var kv in providers)
        {
            var node = kv.Value?.AsObject();
            if (node == null) continue;
            var p = new OpenCodeProvider { Id = kv.Key };
            p.Npm = node["npm"]?.GetValue<string>() ?? "@ai-sdk/openai-compatible";
            p.Name = node["name"]?.GetValue<string>();
            var options = node["options"]?.AsObject();
            if (options != null)
            {
                try { p.BaseUrl = options["baseURL"]?.GetValue<string>(); } catch { }
                try { p.ApiKey = options["apiKey"]?.GetValue<string>(); } catch { }
                foreach (var opt in options)
                {
                    if (opt.Key is "baseURL" or "apiKey" or "headers") continue;
                    p.ExtraOptions[opt.Key] = opt.Value?.ToString() ?? "";
                }
                if (options["headers"] is JsonObject hObj)
                {
                    foreach (var h in hObj)
                        p.CustomHeaders[h.Key] = h.Value?.ToString() ?? "";
                }
            }
            var models = node["models"]?.AsObject();
            if (models != null)
            {
                p.ModelsJson = models.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
                foreach (var m in models)
                {
                    var dName = m.Value?["name"]?.GetValue<string>() ?? "";
                    p.CustomModels.Add(new ProviderModelEntry { Id = m.Key, Name = dName });
                }
            }
            result.Add(p);
        }
        return result;
    }

    public static void SaveProvider(OpenCodeProvider p, bool setDefaultModel = false, string? defaultModelId = null)
    {
        var root = Load();
        var providers = root["provider"]?.AsObject() ?? new JsonObject();
        var entry = new JsonObject();
        if (!string.IsNullOrWhiteSpace(p.Name)) entry["name"] = p.Name;
        entry["npm"] = string.IsNullOrWhiteSpace(p.Npm) ? "@ai-sdk/openai-compatible" : p.Npm;
        var options = new JsonObject();
        if (!string.IsNullOrWhiteSpace(p.BaseUrl)) options["baseURL"] = p.BaseUrl;
        if (!string.IsNullOrWhiteSpace(p.ApiKey)) options["apiKey"] = p.ApiKey;

        if (p.ExtraOptions != null)
        {
            foreach (var kv in p.ExtraOptions)
            {
                if (string.IsNullOrWhiteSpace(kv.Key)) continue;
                var val = kv.Value?.Trim() ?? "";
                if (bool.TryParse(val, out var bVal)) options[kv.Key.Trim()] = bVal;
                else if (long.TryParse(val, out var lVal)) options[kv.Key.Trim()] = lVal;
                else if (double.TryParse(val, out var dVal)) options[kv.Key.Trim()] = dVal;
                else options[kv.Key.Trim()] = val;
            }
        }

        if (p.CustomHeaders != null && p.CustomHeaders.Count > 0)
        {
            var hObj = new JsonObject();
            foreach (var h in p.CustomHeaders)
                if (!string.IsNullOrWhiteSpace(h.Key))
                    hObj[h.Key.Trim()] = h.Value ?? "";
            options["headers"] = hObj;
        }

        if (options.Count > 0) entry["options"] = options;

        if (p.CustomModels != null && p.CustomModels.Count > 0)
        {
            var mObj = new JsonObject();
            foreach (var m in p.CustomModels)
            {
                if (string.IsNullOrWhiteSpace(m.Id)) continue;
                mObj[m.Id.Trim()] = new JsonObject { ["name"] = m.Name ?? "" };
            }
            entry["models"] = mObj;
        }
        else if (!string.IsNullOrWhiteSpace(p.ModelsJson))
        {
            try
            {
                if (JsonNode.Parse(p.ModelsJson) is JsonObject models && models.Count > 0)
                    entry["models"] = models;
            }
            catch (InvalidOperationException)
            {
                throw new InvalidOperationException("模型配置必须是合法 JSON 对象，如 {\"model-id\":{}}");
            }
        }
        providers[p.Id] = entry;
        root["provider"] = providers;

        if (setDefaultModel)
            root["model"] = p.Id + "/" + defaultModelId;

        FileUtil.AtomicWriteText(ConfigPath, root.ToJsonString(WriteOpts));
    }

    public static void DeleteProvider(string id)
    {
        var root = Load();
        if (root["provider"]?.AsObject() is { } providers)
        {
            providers.Remove(id);
            root["provider"] = providers;
            FileUtil.AtomicWriteText(ConfigPath, root.ToJsonString(WriteOpts));
        }
    }

    public static void ClearDefaultModel()
    {
        var root = Load();
        if (root.ContainsKey("model"))
        {
            root.Remove("model");
            FileUtil.AtomicWriteText(ConfigPath, root.ToJsonString(WriteOpts));
        }
    }

    public static void SetDefaultModel(string providerId, string modelId)
    {
        var root = Load();
        root["model"] = providerId + "/" + modelId;
        FileUtil.AtomicWriteText(ConfigPath, root.ToJsonString(WriteOpts));
    }
}

public static class PiCli
{
    public static string AuthPath => Path.Combine(ClaudeCli.HomeDir, ".pi", "agent", "auth.json");
    public static string SettingsPath => Path.Combine(ClaudeCli.HomeDir, ".pi", "agent", "settings.json");

    public static bool IsInstalled => Directory.Exists(Path.GetDirectoryName(AuthPath));

    static string ReadOrCreate(string path, string fallback)
    {
        var text = FileUtil.ReadTextIfExists(path);
        return string.IsNullOrWhiteSpace(text) ? fallback : text;
    }

    public static (string? Provider, string? Model) CurrentDefaults()
    {
        try
        {
            var s = JsonNode.Parse(ReadOrCreate(SettingsPath, "{}"))?.AsObject();
            return (Str(s, "defaultProvider"), Str(s, "defaultModel"));
        }
        catch
        {
            return (null, null);
        }
    }

    public static List<string> AuthProviders()
    {
        try
        {
            var a = JsonNode.Parse(ReadOrCreate(AuthPath, "{}"))?.AsObject();
            return a == null ? new List<string>() : a.Select(kv => kv.Key).ToList();
        }
        catch
        {
            return new List<string>();
        }
    }

    public static PiAccount Capture(string name)
    {
        var auth = ReadOrCreate(AuthPath, "{}");
        var settings = ReadOrCreate(SettingsPath, "{}");
        string? prov = null, model = null;
        try
        {
            var s = JsonNode.Parse(settings)?.AsObject();
            prov = Str(s, "defaultProvider");
            model = Str(s, "defaultModel");
        }
        catch { }
        return new PiAccount
        {
            Name = name,
            DefaultProvider = prov ?? "",
            DefaultModel = model ?? "",
            CapturedAtUtc = DateTime.UtcNow,
            AuthJson = auth,
            SettingsJson = settings,
        };
    }

    public static void Restore(PiAccount account)
    {
        FileUtil.AtomicWriteText(AuthPath, account.AuthJson);
        FileUtil.AtomicWriteText(SettingsPath, account.SettingsJson);
    }

    public static string AuthText() => FileUtil.ReadTextIfExists(AuthPath) ?? "{}";

    public static bool MatchesCurrent(PiAccount account) =>
        string.Equals(AuthText(), account.AuthJson, StringComparison.Ordinal);

    static string? Str(JsonObject? obj, string key)
    {
        try { return obj?[key]?.GetValue<string>(); } catch { return null; }
    }
}

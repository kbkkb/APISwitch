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
            var isAnthropic = p.Npm?.Contains("anthropic", StringComparison.OrdinalIgnoreCase) == true;
            foreach (var m in p.CustomModels)
            {
                if (string.IsNullOrWhiteSpace(m.Id)) continue;
                var mNode = new JsonObject { ["name"] = m.Name ?? "" };
                // 思考强度按模型注入（opencode.ai/docs/models：per-model options）
                if (isAnthropic)
                {
                    var budget = ThinkingEffort.ToOpenCodeAnthropicBudget(m.ThinkingEffort);
                    if (budget.HasValue)
                        mNode["options"] = new JsonObject
                        {
                            ["thinking"] = new JsonObject { ["type"] = "enabled", ["budgetTokens"] = budget.Value },
                        };
                }
                else
                {
                    var effort = ThinkingEffort.ToOpenAiReasoningEffort(m.ThinkingEffort);
                    if (!string.IsNullOrEmpty(effort))
                        mNode["options"] = new JsonObject { ["reasoningEffort"] = effort };
                }
                mObj[m.Id.Trim()] = mNode;
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
    public static string ModelsPath => Path.Combine(ClaudeCli.HomeDir, ".pi", "agent", "models.json");
    public static string AuthPath => Path.Combine(ClaudeCli.HomeDir, ".pi", "agent", "auth.json");
    public static string SettingsPath => Path.Combine(ClaudeCli.HomeDir, ".pi", "agent", "settings.json");

    public static bool IsInstalled => Directory.Exists(Path.GetDirectoryName(AuthPath));

    static readonly JsonDocumentOptions DocOpts = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    static readonly JsonSerializerOptions WriteOpts = new() { WriteIndented = true };

    static string ReadOrCreate(string path, string fallback)
    {
        var text = FileUtil.ReadTextIfExists(path);
        return string.IsNullOrWhiteSpace(text) ? fallback : text;
    }

    public static JsonObject LoadModels()
    {
        var text = FileUtil.ReadTextIfExists(ModelsPath);
        if (string.IsNullOrWhiteSpace(text)) return new JsonObject { ["providers"] = new JsonObject() };
        try
        {
            return JsonNode.Parse(text, nodeOptions: null, DocOpts)?.AsObject() ?? new JsonObject { ["providers"] = new JsonObject() };
        }
        catch
        {
            return new JsonObject { ["providers"] = new JsonObject() };
        }
    }

    public static List<string> ProviderIds()
    {
        var list = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var root = LoadModels();
        if (root["providers"]?.AsObject() is { } providers)
        {
            foreach (var kv in providers)
            {
                if (!string.IsNullOrWhiteSpace(kv.Key)) list.Add(kv.Key);
            }
        }
        try
        {
            var authText = FileUtil.ReadTextIfExists(AuthPath);
            if (!string.IsNullOrWhiteSpace(authText))
            {
                if (JsonNode.Parse(authText, nodeOptions: null, DocOpts)?.AsObject() is { } auth)
                {
                    foreach (var kv in auth)
                    {
                        if (!string.IsNullOrWhiteSpace(kv.Key)) list.Add(kv.Key);
                    }
                }
            }
        }
        catch { }
        return list.ToList();
    }

    public static List<PiProvider> LoadProviders()
    {
        var result = new List<PiProvider>();
        var root = LoadModels();
        var providers = root["providers"]?.AsObject();
        if (providers != null)
        {
            foreach (var kv in providers)
            {
                if (kv.Value is not JsonObject node) continue;
                var p = new PiProvider
                {
                    Id = kv.Key,
                    Name = node["name"]?.GetValue<string>(),
                    BaseUrl = node["baseUrl"]?.GetValue<string>(),
                    ApiKey = node["apiKey"]?.GetValue<string>(),
                    Api = node["api"]?.GetValue<string>() ?? "openai-completions",
                };

                if (node["headers"] is JsonObject hObj)
                {
                    foreach (var h in hObj)
                        p.CustomHeaders[h.Key] = h.Value?.ToString() ?? "";
                }

                if (node["models"] is JsonArray mArr)
                {
                    p.ModelsJson = mArr.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
                    foreach (var m in mArr)
                    {
                        if (m is JsonObject mObj)
                        {
                            var mId = mObj["id"]?.GetValue<string>() ?? "";
                            var mName = mObj["name"]?.GetValue<string>() ?? mId;
                            if (!string.IsNullOrEmpty(mId))
                                p.CustomModels.Add(new ProviderModelEntry { Id = mId, Name = mName });
                        }
                    }
                }
                result.Add(p);
            }
        }

        // Also check auth.json for any active provider credentials (e.g. openai, openrouter, custom providers)
        try
        {
            var authText = FileUtil.ReadTextIfExists(AuthPath);
            if (!string.IsNullOrWhiteSpace(authText))
            {
                var authObj = JsonNode.Parse(authText, nodeOptions: null, DocOpts)?.AsObject();
                if (authObj != null)
                {
                    var (defProv, defModel) = CurrentDefaults();
                    foreach (var kv in authObj)
                    {
                        var provId = kv.Key;
                        var existing = result.FirstOrDefault(r => string.Equals(r.Id, provId, StringComparison.OrdinalIgnoreCase));
                        var keyVal = kv.Value?["key"]?.GetValue<string>() ?? "";
                        if (existing != null)
                        {
                            if (string.IsNullOrWhiteSpace(existing.ApiKey) && !string.IsNullOrWhiteSpace(keyVal))
                                existing.ApiKey = keyVal;
                        }
                        else
                        {
                            var p = new PiProvider
                            {
                                Id = provId,
                                Name = provId switch
                                {
                                    "openai" => "OpenAI",
                                    "openrouter" => "OpenRouter",
                                    "anthropic" => "Anthropic",
                                    "google" => "Google Gemini",
                                    _ => provId
                                },
                                BaseUrl = provId switch
                                {
                                    "openrouter" => "https://openrouter.ai/api/v1",
                                    "openai" => "https://api.openai.com/v1",
                                    "anthropic" => "https://api.anthropic.com/v1",
                                    _ => ""
                                },
                                Api = provId switch
                                {
                                    "anthropic" => "anthropic-messages",
                                    "google" => "google-generative-ai",
                                    _ => "openai-completions"
                                },
                                ApiKey = keyVal,
                            };
                            if (string.Equals(defProv, provId, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(defModel))
                            {
                                p.CustomModels.Add(new ProviderModelEntry { Id = defModel, Name = defModel });
                            }
                            result.Add(p);
                        }
                    }
                }
            }
        }
        catch { }

        return result;
    }

    /// <summary>
    /// 将 "1m"/"128k"/纯数字 等上下文窗口写法换算为 token 数值。
    /// 返回 false 表示无法解析（调用方不写该字段）。
    /// </summary>
    public static bool TryParseContextWindow(string? raw, out long tokens)
    {
        tokens = 0;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        var s = raw.Trim().ToLowerInvariant();

        if (s.EndsWith('k') && double.TryParse(s[..^1], out var kv))
        {
            tokens = (long)(kv * 1024);
            return tokens > 0;
        }
        if (s.EndsWith('m') && double.TryParse(s[..^1], out var mv))
        {
            tokens = (long)(mv * 1024 * 1024);
            return tokens > 0;
        }
        if (long.TryParse(s, out var n))
        {
            tokens = n;
            return n > 0;
        }
        return false;
    }

    public static void SaveProvider(PiProvider p, bool setDefaultModel = false, string? defaultModelId = null)
    {        var root = LoadModels();
        var providers = root["providers"]?.AsObject();
        if (providers == null)
        {
            providers = new JsonObject();
            root["providers"] = providers;
        }

        var entry = new JsonObject
        {
            ["name"] = string.IsNullOrWhiteSpace(p.Name) ? p.Id : p.Name,
            ["baseUrl"] = p.BaseUrl ?? "",
            ["api"] = string.IsNullOrWhiteSpace(p.Api) ? "openai-completions" : p.Api,
        };

        if (!string.IsNullOrWhiteSpace(p.ApiKey))
            entry["apiKey"] = p.ApiKey;

        if (p.CustomHeaders != null && p.CustomHeaders.Count > 0)
        {
            var hObj = new JsonObject();
            foreach (var h in p.CustomHeaders)
                if (!string.IsNullOrWhiteSpace(h.Key))
                    hObj[h.Key.Trim()] = h.Value ?? "";
            entry["headers"] = hObj;
        }

        if (p.CustomModels != null && p.CustomModels.Count > 0)
        {
            var mArr = new JsonArray();
            foreach (var m in p.CustomModels)
            {
                if (string.IsNullOrWhiteSpace(m.Id)) continue;
                var mObj = new JsonObject
                {
                    ["id"] = m.Id.Trim(),
                    ["name"] = string.IsNullOrWhiteSpace(m.Name) ? m.Id.Trim() : m.Name.Trim(),
                };
                if (TryParseContextWindow(m.ContextWindow, out var cw))
                    mObj["contextWindow"] = cw;
                // pi 只有在模型上声明 reasoning:true 才会暴露思考/effort 选项（按模型）
                if (ThinkingEffort.ToPi(m.ThinkingEffort) != null)
                    mObj["reasoning"] = true;
                mArr.Add(mObj);
            }
            entry["models"] = mArr;
        }
        else if (!string.IsNullOrWhiteSpace(p.ModelsJson))
        {
            try
            {
                if (JsonNode.Parse(p.ModelsJson) is JsonArray arr && arr.Count > 0)
                    entry["models"] = arr;
                else if (JsonNode.Parse(p.ModelsJson) is JsonObject obj && obj.Count > 0)
                {
                    var mArr = new JsonArray();
                    foreach (var kv in obj)
                    {
                        var dName = kv.Value?["name"]?.GetValue<string>() ?? kv.Key;
                        mArr.Add(new JsonObject { ["id"] = kv.Key, ["name"] = dName });
                    }
                    entry["models"] = mArr;
                }
            }
            catch { }
        }

        providers[p.Id] = entry;
        root["providers"] = providers;
        Directory.CreateDirectory(Path.GetDirectoryName(ModelsPath)!);
        FileUtil.AtomicWriteText(ModelsPath, root.ToJsonString(WriteOpts));

        // Sync apiKey to auth.json if present
        if (!string.IsNullOrWhiteSpace(p.ApiKey))
        {
            try
            {
                var authText = FileUtil.ReadTextIfExists(AuthPath);
                var auth = string.IsNullOrWhiteSpace(authText)
                    ? new JsonObject()
                    : JsonNode.Parse(authText, nodeOptions: null, DocOpts)?.AsObject() ?? new JsonObject();

                auth[p.Id] = new JsonObject
                {
                    ["type"] = "api_key",
                    ["key"] = p.ApiKey,
                };
                Directory.CreateDirectory(Path.GetDirectoryName(AuthPath)!);
                FileUtil.AtomicWriteText(AuthPath, auth.ToJsonString(WriteOpts));
            }
            catch { }
        }

        if (setDefaultModel)
        {
            SetDefaultModel(p.Id, defaultModelId);
        }

        // 按模型写入思考等级（settings.json modelThinkingLevels: "provider/modelId" → level）
        if (p.CustomModels != null && p.CustomModels.Any(m => ThinkingEffort.ToPi(m.ThinkingEffort) != null))
        {
            SetModelThinkingLevels(p.Id, p.CustomModels);
        }
    }

    /// <summary>
    /// 写入 pi 的 per-model 启动思考等级（settings.json modelThinkingLevels，键为 "provider/modelId"）。
    /// 仅覆盖本 provider 的键，其他 provider 的条目保持不动。
    /// </summary>
    public static void SetModelThinkingLevels(string providerId, List<Models.ProviderModelEntry> models)
    {
        try
        {
            var text = FileUtil.ReadTextIfExists(SettingsPath);
            var s = string.IsNullOrWhiteSpace(text) ? new JsonObject() : JsonNode.Parse(text, nodeOptions: null, DocOpts)?.AsObject() ?? new JsonObject();

            var map = s["modelThinkingLevels"]?.AsObject() is { } existing
                ? existing
                : new JsonObject();
            var prefix = providerId + "/";
            var staleKeys = map.Select(kv => kv.Key)
                .Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .ToList();
            foreach (var k in staleKeys)
                map.Remove(k);

            foreach (var m in models)
            {
                var lv = ThinkingEffort.ToPi(m.ThinkingEffort);
                if (lv != null && !string.IsNullOrWhiteSpace(m.Id))
                    map[providerId + "/" + m.Id.Trim()] = lv;
            }

            if (map.Count > 0)
                s["modelThinkingLevels"] = map;
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            FileUtil.AtomicWriteText(SettingsPath, s.ToJsonString(WriteOpts));
        }
        catch { }
    }

    public static void DeleteProvider(string id)
    {
        var root = LoadModels();
        if (root["providers"]?.AsObject() is { } providers)
        {
            providers.Remove(id);
            root["providers"] = providers;
            FileUtil.AtomicWriteText(ModelsPath, root.ToJsonString(WriteOpts));
        }

        try
        {
            var authText = FileUtil.ReadTextIfExists(AuthPath);
            if (!string.IsNullOrWhiteSpace(authText))
            {
                var auth = JsonNode.Parse(authText, nodeOptions: null, DocOpts)?.AsObject();
                if (auth != null && auth.ContainsKey(id))
                {
                    auth.Remove(id);
                    FileUtil.AtomicWriteText(AuthPath, auth.ToJsonString(WriteOpts));
                }
            }
        }
        catch { }

        var (currentProv, _) = CurrentDefaults();
        if (string.Equals(currentProv, id, StringComparison.OrdinalIgnoreCase))
        {
            ClearDefaultModel();
        }
    }

    public static void SetDefaultModel(string providerId, string? modelId)
    {
        var text = FileUtil.ReadTextIfExists(SettingsPath);
        var settings = string.IsNullOrWhiteSpace(text)
            ? new JsonObject()
            : JsonNode.Parse(text, nodeOptions: null, DocOpts)?.AsObject() ?? new JsonObject();

        settings["defaultProvider"] = providerId;
        if (!string.IsNullOrWhiteSpace(modelId))
            settings["defaultModel"] = modelId;

        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        FileUtil.AtomicWriteText(SettingsPath, settings.ToJsonString(WriteOpts));
    }

    public static void ClearDefaultModel()
    {
        var text = FileUtil.ReadTextIfExists(SettingsPath);
        if (string.IsNullOrWhiteSpace(text)) return;
        try
        {
            var settings = JsonNode.Parse(text, nodeOptions: null, DocOpts)?.AsObject();
            if (settings != null)
            {
                settings.Remove("defaultProvider");
                settings.Remove("defaultModel");
                FileUtil.AtomicWriteText(SettingsPath, settings.ToJsonString(WriteOpts));
            }
        }
        catch { }
    }

    /// <summary>写入 Pi 启动默认思考等级（off/minimal/low/medium/high/xhigh/max）。</summary>
    public static void SetDefaultThinking(string level)
    {
        try
        {
            var text = FileUtil.ReadTextIfExists(SettingsPath);
            var s = string.IsNullOrWhiteSpace(text) ? new JsonObject() : JsonNode.Parse(text, nodeOptions: null, DocOpts)?.AsObject() ?? new JsonObject();
            s["defaultThinkingLevel"] = level;
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            FileUtil.AtomicWriteText(SettingsPath, s.ToJsonString(WriteOpts));
        }
        catch { }
    }

    public static void ClearDefaultThinking()
    {
        try
        {
            var text = FileUtil.ReadTextIfExists(SettingsPath);
            if (string.IsNullOrWhiteSpace(text)) return;
            if (JsonNode.Parse(text, nodeOptions: null, DocOpts)?.AsObject() is { } s && s.ContainsKey("defaultThinkingLevel"))
            {
                s.Remove("defaultThinkingLevel");
                FileUtil.AtomicWriteText(SettingsPath, s.ToJsonString(WriteOpts));
            }
        }
        catch { }
    }

    public static string? CurrentThinkingLevel()
    {
        try
        {
            var s = JsonNode.Parse(ReadOrCreate(SettingsPath, "{}"))?.AsObject();
            return Str(s, "defaultThinkingLevel");
        }
        catch { return null; }
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


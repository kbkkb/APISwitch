using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using APISwitch.Models;

namespace APISwitch.Services;

public static class ClaudeCli
{
    public static string HomeDir =>
        Environment.GetEnvironmentVariable("APISWITCH_HOME") is { Length: > 0 } h
            ? h
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public static string SettingsPath => Path.Combine(HomeDir, ".claude", "settings.json");

    static readonly JsonDocumentOptions DocOpts = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    static readonly JsonSerializerOptions WriteOpts = new() { WriteIndented = true };

    public static JsonObject LoadSettings()
    {
        var text = FileUtil.ReadTextIfExists(SettingsPath);
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

    public static string? CurrentBaseUrl()
    {
        try
        {
            return LoadSettings()["env"]?["ANTHROPIC_BASE_URL"]?.GetValue<string>();
        }
        catch
        {
            return null;
        }
    }

    public static string? CurrentProviderName()
    {
        try
        {
            var root = LoadSettings();
            var env = root["env"] as JsonObject;
            if (env == null) return null;

            if (env.TryGetPropertyValue("APISWITCH_PROVIDER_NAME", out var n) && n != null)
            {
                var name = n.GetValue<string>()?.Trim();
                if (!string.IsNullOrEmpty(name)) return name;
            }

            if (env.TryGetPropertyValue("ANTHROPIC_BASE_URL", out var u) && u != null)
            {
                var url = u.GetValue<string>()?.Trim().TrimEnd('/');
                if (!string.IsNullOrEmpty(url))
                {
                    if (url.Contains("/claude-cli", StringComparison.OrdinalIgnoreCase) && LocalProxyServer.ActiveClaudeCliProvider != null)
                    {
                        return LocalProxyServer.ActiveClaudeCliProvider.Name;
                    }

                    var all = CliStore.LoadClaude();
                    var matched = all.FirstOrDefault(x => !x.IsOfficial && string.Equals(x.BaseUrl?.TrimEnd('/'), url, StringComparison.OrdinalIgnoreCase));
                    if (matched != null) return matched.Name;
                }
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    public static ClaudeProvider? GetActiveProvider()
    {
        if (LocalProxyServer.ActiveClaudeCliProvider != null)
            return LocalProxyServer.ActiveClaudeCliProvider;

        var currName = CurrentProviderName();
        if (string.IsNullOrEmpty(currName)) return null;

        var all = CliStore.LoadClaude();
        return all.FirstOrDefault(x => string.Equals(x.Name, currName, StringComparison.OrdinalIgnoreCase) ||
                                       string.Equals(x.Id, currName, StringComparison.OrdinalIgnoreCase));
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
            Apply(new ClaudeProvider { Name = "Anthropic 官方", IsOfficial = true });
        }
    }

    public static void Apply(ClaudeProvider p)
    {
        var root = LoadSettings();
        var env = root["env"] as JsonObject ?? new JsonObject();

        foreach (var key in env.Select(kv => kv.Key).Where(k => k.StartsWith("ANTHROPIC_") || k.StartsWith("APISWITCH_")).ToList())
            env.Remove(key);

        if (p.IsOfficial)
        {
            LocalProxyServer.ActiveClaudeCliProvider = null;
            env.Remove("MAX_THINKING_TOKENS");
        }
        else
        {
            LocalProxyServer.ActiveClaudeCliProvider = p;

            var requiresProxy = p.WireApi == "chat" || LocalProxyServer.IsClaudeCliEnabled;
            if (p.WireApi == "chat" && !LocalProxyServer.IsClaudeCliEnabled)
            {
                LocalProxyServer.SetClaudeCliEnabled(true);
                requiresProxy = true;
            }

            var effectiveBaseUrl = requiresProxy
                ? LocalProxyServer.ProxyClaudeCliUrl
                : p.BaseUrl;

            SetIf(env, "ANTHROPIC_BASE_URL", effectiveBaseUrl);
            SetIf(env, "ANTHROPIC_AUTH_TOKEN", p.AuthToken);

            // 思考强度（Claude 预算制）：per-model 由本地代理按映射模型注入；
            // env 写全局兜底（各档位中的最高预算）：low=4096 / medium=10240 / high=32768
            var effort = ThinkingEffort.MaxOf(p.ModelMappings?.Select(m => m.ThinkingEffort) ?? Enumerable.Empty<string?>());
            var budget = ThinkingEffort.ToClaudeBudgetTokens(effort);
            if (budget.HasValue)
                SetIf(env, "MAX_THINKING_TOKENS", budget.Value.ToString());
            else
                env.Remove("MAX_THINKING_TOKENS");

            if ((p.ModelMappings == null || p.ModelMappings.Count == 0) && p.ExtraEnv != null && p.ExtraEnv.Any(k => k.Key.StartsWith("ANTHROPIC_DEFAULT_")))
            {
                p.ModelMappings = new List<ClaudeModelMapping>();
                var roles = new (string role, string envKey, string nameKey)[] {
                    ("Sonnet", "ANTHROPIC_DEFAULT_SONNET_MODEL", "ANTHROPIC_DEFAULT_SONNET_MODEL_NAME"),
                    ("Opus", "ANTHROPIC_DEFAULT_OPUS_MODEL", "ANTHROPIC_DEFAULT_OPUS_MODEL_NAME"),
                    ("Fable", "ANTHROPIC_DEFAULT_FABLE_MODEL", "ANTHROPIC_DEFAULT_FABLE_MODEL_NAME"),
                    ("Haiku", "ANTHROPIC_DEFAULT_HAIKU_MODEL", "ANTHROPIC_DEFAULT_HAIKU_MODEL_NAME")
                };
                foreach (var (rName, envKey, nameKey) in roles)
                {
                    p.ExtraEnv.TryGetValue(envKey, out var raw);
                    p.ExtraEnv.TryGetValue(nameKey, out var disp);
                    raw ??= p.Model ?? "";
                    bool s1m = raw.EndsWith("[1M]", StringComparison.OrdinalIgnoreCase);
                    var clean = Strip1m(raw);
                    p.ModelMappings.Add(new ClaudeModelMapping
                    {
                        Role = rName,
                        Model = clean,
                        DisplayName = !string.IsNullOrWhiteSpace(disp) ? disp.Trim() : clean,
                        Supports1m = s1m
                    });
                }
            }

            if (p.ModelMappings != null && p.ModelMappings.Count > 0 && p.AccessMode != "direct")
            {
                var fallbackModel = p.ModelMappings.FirstOrDefault(m => !string.IsNullOrWhiteSpace(m.Model))?.Model ?? p.Model;

                string ResolveRole(string role, out string displayName)
                {
                    var m = p.ModelMappings.FirstOrDefault(x => string.Equals(x.Role, role, StringComparison.OrdinalIgnoreCase));
                    var raw = !string.IsNullOrWhiteSpace(m?.Model) ? m.Model.Trim() : (fallbackModel?.Trim() ?? "");
                    var s1m = m?.Supports1m ?? true;
                    var stripped = Strip1m(raw);
                    displayName = !string.IsNullOrWhiteSpace(m?.DisplayName) ? m.DisplayName.Trim() : stripped;
                    if (string.IsNullOrWhiteSpace(stripped)) return "";
                    // 仅当通过本地路由中转时才可追加 [1M]（由本地代理在转发上游前剥离 [1M] 并支持长上下文申报）。
                    // 若未开启本地路由直连第三方上游，第三方上游 API（NewAPI/各厂商）均无 [1M] 模型名渠道，直连传 [1M] 会导致上游直接报 503 model_not_found。
                    var use1m = s1m && requiresProxy;
                    return use1m ? (stripped + "[1M]") : stripped;
                }

                var sonnetVal = ResolveRole("Sonnet", out var sonnetDisp);
                var opusVal = ResolveRole("Opus", out var opusDisp);
                var fableVal = ResolveRole("Fable", out var fableDisp);
                var haikuVal = ResolveRole("Haiku", out var haikuDisp);

                SetIf(env, "ANTHROPIC_DEFAULT_SONNET_MODEL", sonnetVal);
                SetIf(env, "ANTHROPIC_DEFAULT_SONNET_MODEL_NAME", sonnetDisp);
                SetIf(env, "ANTHROPIC_DEFAULT_OPUS_MODEL", opusVal);
                SetIf(env, "ANTHROPIC_DEFAULT_OPUS_MODEL_NAME", opusDisp);
                SetIf(env, "ANTHROPIC_DEFAULT_FABLE_MODEL", fableVal);
                SetIf(env, "ANTHROPIC_DEFAULT_FABLE_MODEL_NAME", fableDisp);
                SetIf(env, "ANTHROPIC_DEFAULT_HAIKU_MODEL", haikuVal);
                SetIf(env, "ANTHROPIC_DEFAULT_HAIKU_MODEL_NAME", haikuDisp);

                SetIf(env, "ANTHROPIC_MODEL", sonnetVal);
                SetIf(env, "ANTHROPIC_SMALL_FAST_MODEL", haikuVal);
                SetIf(env, "CLAUDE_CODE_SUBAGENT_MODEL", haikuVal);
            }
            else
            {
                var cleanModel = requiresProxy ? p.Model : Strip1m(p.Model);
                var cleanSmall = requiresProxy ? p.SmallFastModel : Strip1m(p.SmallFastModel);
                SetIf(env, "ANTHROPIC_MODEL", cleanModel);
                SetIf(env, "ANTHROPIC_SMALL_FAST_MODEL", cleanSmall);
            }

            env["APISWITCH_PROVIDER_NAME"] = p.Name;
            if (!string.IsNullOrEmpty(p.Id)) env["APISWITCH_PROVIDER_ID"] = p.Id;

            if (p.ExtraEnv != null)
            {
                foreach (var kv in p.ExtraEnv)
                {
                    if (string.IsNullOrWhiteSpace(kv.Key)) continue;
                    var key = kv.Key.Trim();
                    if (key.StartsWith("ANTHROPIC_DEFAULT_") || key == "ANTHROPIC_MODEL" || key == "ANTHROPIC_SMALL_FAST_MODEL" || key == "CLAUDE_CODE_SUBAGENT_MODEL")
                        continue;
                    env[key] = kv.Value ?? "";
                }
            }
            if (p.ExtraOptions != null)
                foreach (var kv in p.ExtraOptions)
                    if (!string.IsNullOrWhiteSpace(kv.Key))
                        env[kv.Key.Trim()] = kv.Value ?? "";
            if (p.CustomHeaders != null)
                foreach (var kv in p.CustomHeaders)
                    if (!string.IsNullOrWhiteSpace(kv.Key))
                        env[kv.Key.Trim()] = kv.Value ?? "";
        }

        if (env.Count > 0) root["env"] = env;
        else root.Remove("env");

        FileUtil.AtomicWriteText(SettingsPath, root.ToJsonString(WriteOpts));
    }

    public static string Strip1m(string? model)
    {
        if (string.IsNullOrWhiteSpace(model)) return "";
        model = model.Trim();
        if (model.EndsWith("[1M]", StringComparison.OrdinalIgnoreCase))
            return model.Substring(0, model.Length - 4).Trim();
        return model;
    }

    static void SetIf(JsonObject env, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) env[key] = value;
    }
}

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
        }
        else
        {
            LocalProxyServer.ActiveClaudeCliProvider = p;

            var effectiveBaseUrl = LocalProxyServer.IsClaudeCliEnabled
                ? LocalProxyServer.ProxyClaudeCliUrl
                : p.BaseUrl;

            SetIf(env, "ANTHROPIC_BASE_URL", effectiveBaseUrl);
            SetIf(env, "ANTHROPIC_AUTH_TOKEN", p.AuthToken);
            SetIf(env, "ANTHROPIC_MODEL", p.Model);
            SetIf(env, "ANTHROPIC_SMALL_FAST_MODEL", p.SmallFastModel);

            env["APISWITCH_PROVIDER_NAME"] = p.Name;
            if (!string.IsNullOrEmpty(p.Id)) env["APISWITCH_PROVIDER_ID"] = p.Id;

            foreach (var kv in p.ExtraEnv)
                if (!string.IsNullOrWhiteSpace(kv.Key))
                    env[kv.Key.Trim()] = kv.Value ?? "";
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

    static void SetIf(JsonObject env, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) env[key] = value;
    }
}

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

    public static void Apply(ClaudeProvider p)
    {
        var root = LoadSettings();
        var env = root["env"] as JsonObject ?? new JsonObject();

        foreach (var key in env.Select(kv => kv.Key).Where(k => k.StartsWith("ANTHROPIC_")).ToList())
            env.Remove(key);

        if (!p.IsOfficial)
        {
            SetIf(env, "ANTHROPIC_BASE_URL", p.BaseUrl);
            SetIf(env, "ANTHROPIC_AUTH_TOKEN", p.AuthToken);
            SetIf(env, "ANTHROPIC_MODEL", p.Model);
            SetIf(env, "ANTHROPIC_SMALL_FAST_MODEL", p.SmallFastModel);
            foreach (var kv in p.ExtraEnv)
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

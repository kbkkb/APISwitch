using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using APISwitch.Models;
using Tomlyn;
using Tomlyn.Model;

namespace APISwitch.Services;

public static class CodexCli
{
    public static string CodexDir => Path.Combine(ClaudeCli.HomeDir, ".codex");

    public static string ConfigPath => Path.Combine(CodexDir, "config.toml");
    public static string AuthPath => Path.Combine(CodexDir, "auth.json");

    static readonly JsonSerializerOptions WriteOpts = new() { WriteIndented = true };

    public static string? CurrentProviderId()
    {
        var text = FileUtil.ReadTextIfExists(ConfigPath);
        if (string.IsNullOrWhiteSpace(text)) return null;
        try
        {
            var model = TomlSerializer.Deserialize<TomlTable>(text);
            if (model == null) return null;
            return model.TryGetValue("model_provider", out var v) ? v?.ToString() : null;
        }
        catch
        {
            return null;
        }
    }

    public static void Apply(CodexProvider p)
    {
        var text = FileUtil.ReadTextIfExists(ConfigPath) ?? "";
        TomlTable model;
        try { model = TomlSerializer.Deserialize<TomlTable>(text) ?? new TomlTable(); } catch { model = new TomlTable(); }

        if (p.IsOfficial)
        {
            model.Remove("model_provider");
        }
        else
        {
            model["model_provider"] = p.Id;

            var providers = model.TryGetValue("model_providers", out var mp) && mp is TomlTable t
                ? t
                : new TomlTable();

            var entry = new TomlTable
            {
                ["name"] = p.Name,
                ["base_url"] = p.BaseUrl ?? "",
                ["wire_api"] = string.IsNullOrEmpty(p.WireApi) ? "responses" : p.WireApi,
            };

            if (!string.IsNullOrWhiteSpace(p.BearerToken))
            {
                entry["experimental_bearer_token"] = p.BearerToken;
                entry["requires_openai_auth"] = true;
            }
            else
            {
                entry["env_key"] = "OPENAI_API_KEY";
            }

            providers[p.Id] = entry;
            model["model_providers"] = providers;
        }

        if (!string.IsNullOrWhiteSpace(p.Model)) model["model"] = p.Model;

        FileUtil.AtomicWriteText(ConfigPath, TomlSerializer.Serialize(model));
        ApplyAuth(p);
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

        if (p.IsOfficial) root.Remove("OPENAI_API_KEY");
        else if (!string.IsNullOrWhiteSpace(p.ApiKey)) root["OPENAI_API_KEY"] = p.ApiKey;

        FileUtil.AtomicWriteText(AuthPath, root.ToJsonString(WriteOpts));
    }
}


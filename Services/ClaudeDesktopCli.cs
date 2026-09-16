using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using APISwitch.Models;

namespace APISwitch.Services;

public static class ClaudeDesktopCli
{
    public static string ConfigPath => Path.Combine(AgPaths.AppData, "Claude", "claude_desktop_config.json");

    static readonly string[] GatewayKeys =
    {
        "inferenceProvider", "inferenceGatewayBaseUrl", "inferenceGatewayApiKey",
        "inferenceGatewayAuthScheme", "inferenceModels", "coworkEgressAllowedHosts",
    };

    static readonly JsonDocumentOptions DocOpts = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    static readonly JsonSerializerOptions WriteOpts = new() { WriteIndented = true };

    public static JsonObject LoadConfig()
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

    public static string? CurrentGatewayUrl()
    {
        try
        {
            return LoadConfig()["inferenceGatewayBaseUrl"]?.GetValue<string>();
        }
        catch
        {
            return null;
        }
    }

    public static void Apply(ClaudeProvider p)
    {
        var root = LoadConfig();

        foreach (var key in GatewayKeys)
            root.Remove(key);

        if (!p.IsOfficial)
        {
            if (string.IsNullOrWhiteSpace(p.BaseUrl) || string.IsNullOrWhiteSpace(p.AuthToken))
                throw new InvalidOperationException("Claude 客户端供应商需要 Base URL 和 Auth Token");

            root["inferenceProvider"] = "gateway";
            root["inferenceGatewayBaseUrl"] = p.BaseUrl;
            root["inferenceGatewayApiKey"] = p.AuthToken;
            root["inferenceGatewayAuthScheme"] = "bearer";

            var models = new JsonArray();
            foreach (var id in CollectModelIds(p))
            {
                models.Add(new JsonObject { ["modelId"] = id, ["displayName"] = id });
            }
            if (models.Count == 0)
                models.Add(new JsonObject { ["modelId"] = "claude-sonnet-4-5", ["displayName"] = "claude-sonnet-4-5" });
            root["inferenceModels"] = models;

            if (Uri.TryCreate(p.BaseUrl, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host))
                root["coworkEgressAllowedHosts"] = new JsonArray(uri.Host);
        }

        FileUtil.AtomicWriteText(ConfigPath, root.ToJsonString(WriteOpts));
    }

    static List<string> CollectModelIds(ClaudeProvider p)
    {
        var ids = new List<string>();
        void Add(string? m)
        {
            if (!string.IsNullOrWhiteSpace(m) && !ids.Contains(m)) ids.Add(m);
        }
        Add(p.Model);
        Add(p.SmallFastModel);
        foreach (var kv in p.ExtraEnv)
            if (kv.Key.Contains("_MODEL") && !kv.Key.EndsWith("_MODEL_NAME"))
                Add(kv.Value);
        return ids;
    }
}

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

    public static string? CurrentProviderName()
    {
        try
        {
            var root = LoadConfig();
            if (root.TryGetPropertyValue("apiswitchProviderName", out var n) && n != null)
            {
                var name = n.GetValue<string>()?.Trim();
                if (!string.IsNullOrEmpty(name)) return name;
            }

            var url = root["inferenceGatewayBaseUrl"]?.GetValue<string>()?.Trim().TrimEnd('/');
            if (!string.IsNullOrEmpty(url))
            {
                if (url.Contains("/claude-desktop", StringComparison.OrdinalIgnoreCase) && LocalProxyServer.ActiveClaudeDesktopProvider != null)
                {
                    return LocalProxyServer.ActiveClaudeDesktopProvider.Name;
                }

                var all = CliStore.LoadClaudeDesktop();
                var matched = all.FirstOrDefault(x => !x.IsOfficial && string.Equals(x.BaseUrl?.TrimEnd('/'), url, StringComparison.OrdinalIgnoreCase));
                if (matched != null) return matched.Name;
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
        if (LocalProxyServer.ActiveClaudeDesktopProvider != null)
            return LocalProxyServer.ActiveClaudeDesktopProvider;

        var currName = CurrentProviderName();
        if (string.IsNullOrEmpty(currName)) return null;

        var all = CliStore.LoadClaudeDesktop();
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
            Apply(new ClaudeProvider { Name = "Claude 官方", IsOfficial = true });
        }
    }

    public static void Apply(ClaudeProvider p)
    {
        var root = LoadConfig();

        foreach (var key in GatewayKeys)
            root.Remove(key);
        root.Remove("apiswitchProviderName");
        root.Remove("apiswitchProviderId");

        if (p.IsOfficial)
        {
            LocalProxyServer.ActiveClaudeDesktopProvider = null;
        }
        else
        {
            if (string.IsNullOrWhiteSpace(p.BaseUrl) || string.IsNullOrWhiteSpace(p.AuthToken))
                throw new InvalidOperationException("Claude 客户端供应商需要 Base URL 和 Auth Token");

            LocalProxyServer.ActiveClaudeDesktopProvider = p;

            var effectiveBaseUrl = LocalProxyServer.IsClaudeDesktopEnabled
                ? LocalProxyServer.ProxyClaudeDesktopUrl
                : p.BaseUrl;

            root["inferenceProvider"] = "gateway";
            root["inferenceGatewayBaseUrl"] = effectiveBaseUrl;
            root["inferenceGatewayApiKey"] = p.AuthToken;
            root["inferenceGatewayAuthScheme"] = "bearer";
            root["apiswitchProviderName"] = p.Name;
            if (!string.IsNullOrEmpty(p.Id)) root["apiswitchProviderId"] = p.Id;

            var models = new JsonArray();
            foreach (var id in CollectModelIds(p))
            {
                models.Add(new JsonObject { ["modelId"] = id, ["displayName"] = id });
            }
            if (models.Count == 0)
                models.Add(new JsonObject { ["modelId"] = "claude-sonnet-4-5", ["displayName"] = "claude-sonnet-4-5" });
            root["inferenceModels"] = models;

            if (Uri.TryCreate(effectiveBaseUrl, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host))
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
        if (p.CustomModels != null)
        {
            foreach (var cm in p.CustomModels)
                Add(cm.Id);
        }
        foreach (var kv in p.ExtraEnv)
            if (kv.Key.Contains("_MODEL") && !kv.Key.EndsWith("_MODEL_NAME"))
                Add(kv.Value);
        return ids;
    }
}

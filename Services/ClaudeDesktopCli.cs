using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using APISwitch.Models;

namespace APISwitch.Services;

public static class ClaudeDesktopCli
{
    /// <summary>
    /// 获取当前系统上 Claude 客户端所有可能读写的配置文件路径
    /// 包含：3P 专用路径 (%LOCALAPPDATA%\Claude-3p)、标准 Roaming 路径、MSIX 虚拟化路径等
    /// </summary>
    public static List<string> GetAllCandidateConfigPaths()
    {
        var list = new List<string>();

        // 1. Windows 下 Claude 3P 模式的最核心主路径
        var local3p = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Claude-3p", "claude_desktop_config.json");
        list.Add(local3p);

        // 2. 标准 Roaming 路径 (普通 EXE 安装版)
        var roamingStandard = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Claude", "claude_desktop_config.json");
        list.Add(roamingStandard);

        // 3. Roaming\Claude-3p 路径
        var roaming3p = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Claude-3p", "claude_desktop_config.json");
        list.Add(roaming3p);

        // 4. MSIX / Windows 商店包专用虚拟化路径
        var msixLocalCache = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Packages", "Claude_pzs8sxrjxfjjc", "LocalCache", "Roaming", "Claude", "claude_desktop_config.json");
        if (File.Exists(msixLocalCache) || Directory.Exists(Path.GetDirectoryName(msixLocalCache)!))
        {
            list.Add(msixLocalCache);
        }

        return list;
    }

    /// <summary>
    /// 当前首选的主配置文件路径（优先返回已存在的 3P 路径或标准路径）
    /// </summary>
    public static string ConfigPath
    {
        get
        {
            foreach (var p in GetAllCandidateConfigPaths())
            {
                if (File.Exists(p)) return p;
            }
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Claude-3p", "claude_desktop_config.json");
        }
    }

    public const string ApiSwitchConfigId = "00000000-0000-4000-8000-000000157250";
    public const string CcSwitchLegacyId = "00000000-0000-4000-8000-000000157210";

    public static string ConfigLibraryDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Claude-3p", "configLibrary");

    public static string MetaPath => Path.Combine(ConfigLibraryDir, "_meta.json");

    public static string ApiSwitchConfigFile => Path.Combine(ConfigLibraryDir, $"{ApiSwitchConfigId}.json");

    static readonly string[] GatewayKeys =
    {
        "inferenceProvider", "inferenceGatewayBaseUrl", "inferenceGatewayApiKey",
        "inferenceGatewayAuthScheme", "modelDiscoveryEnabled", "inferenceModels", "coworkEgressAllowedHosts",
    };

    static readonly JsonDocumentOptions DocOpts = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    static readonly JsonSerializerOptions WriteOpts = new() { WriteIndented = true };

    public static JsonObject LoadConfig()
    {
        // 优先读取已存在且包含配置的文件（优先 Local\Claude-3p，其次其它存在路径）
        foreach (var path in GetAllCandidateConfigPaths())
        {
            if (File.Exists(path))
            {
                try
                {
                    var text = FileUtil.ReadTextIfExists(path);
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        var node = JsonNode.Parse(text, nodeOptions: null, DocOpts)?.AsObject();
                        if (node != null) return node;
                    }
                }
                catch { }
            }
        }
        return new JsonObject();
    }

    public static string? CurrentGatewayUrl()
    {
        try
        {
            // 1. 优先读取 Claude 3P managed configLibrary 激活配置
            if (File.Exists(MetaPath))
            {
                var metaText = FileUtil.ReadTextIfExists(MetaPath);
                if (!string.IsNullOrWhiteSpace(metaText))
                {
                    var metaNode = JsonNode.Parse(metaText, nodeOptions: null, DocOpts)?.AsObject();
                    var appliedId = metaNode?["appliedId"]?.GetValue<string>()?.Trim();
                    if (!string.IsNullOrEmpty(appliedId))
                    {
                        var cfgFile = Path.Combine(ConfigLibraryDir, $"{appliedId}.json");
                        if (File.Exists(cfgFile))
                        {
                            var cfgText = FileUtil.ReadTextIfExists(cfgFile);
                            if (!string.IsNullOrWhiteSpace(cfgText))
                            {
                                var cfgNode = JsonNode.Parse(cfgText, nodeOptions: null, DocOpts)?.AsObject();
                                var gwUrl = cfgNode?["inferenceGatewayBaseUrl"]?.GetValue<string>()?.Trim();
                                if (!string.IsNullOrEmpty(gwUrl)) return gwUrl;
                            }
                        }
                    }
                    else
                    {
                        // appliedId 为空字符串代表官方模式
                        return null;
                    }
                }
            }

            // 2. 回退读取标准 claude_desktop_config.json
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
            if (LocalProxyServer.ActiveClaudeDesktopProvider != null)
            {
                return LocalProxyServer.ActiveClaudeDesktopProvider.Name;
            }

            // 1. 优先检查 Claude-3p managed configLibrary
            if (File.Exists(MetaPath))
            {
                var metaText = FileUtil.ReadTextIfExists(MetaPath);
                if (!string.IsNullOrWhiteSpace(metaText))
                {
                    var metaNode = JsonNode.Parse(metaText, nodeOptions: null, DocOpts)?.AsObject();
                    var appliedId = metaNode?["appliedId"]?.GetValue<string>()?.Trim();
                    if (string.IsNullOrEmpty(appliedId))
                    {
                        return null; // 官方模式
                    }

                    // 检查 entries 中的 name
                    if (metaNode?["entries"] is JsonArray entries)
                    {
                        foreach (var e in entries)
                        {
                            if (e is JsonObject eObj && string.Equals(eObj["id"]?.GetValue<string>(), appliedId, StringComparison.OrdinalIgnoreCase))
                            {
                                var eName = eObj["name"]?.GetValue<string>()?.Trim();
                                if (!string.IsNullOrEmpty(eName))
                                {
                                    if (eName.StartsWith("APISwitch - ", StringComparison.OrdinalIgnoreCase))
                                        return eName.Substring(12).Trim();
                                    return eName;
                                }
                            }
                        }
                    }

                    // 检查 cfgFile 的 apiKey / gatewayUrl 匹配供应商
                    var cfgFile = Path.Combine(ConfigLibraryDir, $"{appliedId}.json");
                    if (File.Exists(cfgFile))
                    {
                        var cfgText = FileUtil.ReadTextIfExists(cfgFile);
                        if (!string.IsNullOrWhiteSpace(cfgText))
                        {
                            var cfgNode = JsonNode.Parse(cfgText, nodeOptions: null, DocOpts)?.AsObject();
                            var key = cfgNode?["inferenceGatewayApiKey"]?.GetValue<string>()?.Trim();
                            var url = cfgNode?["inferenceGatewayBaseUrl"]?.GetValue<string>()?.Trim().TrimEnd('/');
                            var all = CliStore.LoadClaudeDesktop();
                            if (!string.IsNullOrEmpty(key))
                            {
                                var matched = all.FirstOrDefault(x => !x.IsOfficial && string.Equals(x.AuthToken?.Trim(), key, StringComparison.Ordinal));
                                if (matched != null) return matched.Name;
                            }
                            if (!string.IsNullOrEmpty(url))
                            {
                                var matched = all.FirstOrDefault(x => !x.IsOfficial && string.Equals(x.BaseUrl?.TrimEnd('/'), url, StringComparison.OrdinalIgnoreCase));
                                if (matched != null) return matched.Name;
                            }
                        }
                    }
                }
            }

            // 2. 回退读取标准 claude_desktop_config.json
            var root = LoadConfig();
            if (root.TryGetPropertyValue("apiswitchProviderName", out var n) && n != null)
            {
                var name = n.GetValue<string>()?.Trim();
                if (!string.IsNullOrEmpty(name)) return name;
            }

            var rootUrl = root["inferenceGatewayBaseUrl"]?.GetValue<string>()?.Trim().TrimEnd('/');
            if (!string.IsNullOrEmpty(rootUrl))
            {
                var all = CliStore.LoadClaudeDesktop();
                var matched = all.FirstOrDefault(x => !x.IsOfficial && string.Equals(x.BaseUrl?.TrimEnd('/'), rootUrl, StringComparison.OrdinalIgnoreCase));
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
            root.Remove("deploymentMode");
            SyncOfficialToConfigLibrary();
        }
        else
        {
            if (string.IsNullOrWhiteSpace(p.BaseUrl) || string.IsNullOrWhiteSpace(p.AuthToken))
                throw new InvalidOperationException("Claude 客户端供应商需要 Base URL 和 Auth Token");

            LocalProxyServer.ActiveClaudeDesktopProvider = p;

            bool isMapping = p.AccessMode != "direct" && p.ModelMappings != null && p.ModelMappings.Count > 0;
            if (isMapping || p.WireApi == "chat")
            {
                if (!LocalProxyServer.IsClaudeDesktopEnabled)
                {
                    LocalProxyServer.SetClaudeDesktopEnabled(true);
                }
            }

            var effectiveBaseUrl = LocalProxyServer.IsClaudeDesktopEnabled
                ? LocalProxyServer.ProxyClaudeDesktopUrl
                : p.BaseUrl;

            root["deploymentMode"] = "3p";
            root["inferenceProvider"] = "gateway";
            root["inferenceGatewayBaseUrl"] = effectiveBaseUrl;
            root["inferenceGatewayApiKey"] = p.AuthToken;
            root["inferenceGatewayAuthScheme"] = "bearer";
            root["modelDiscoveryEnabled"] = false;
            root["apiswitchProviderName"] = p.Name;
            if (!string.IsNullOrEmpty(p.Id)) root["apiswitchProviderId"] = p.Id;

            var models = new JsonArray();
            (string role, string defaultModel, string defaultName)[] roles =
            {
                ("Sonnet", "claude-sonnet-5", "Sonnet"),
                ("Opus", "claude-opus-5", "Opus"),
                ("Fable", "claude-fable-5", "Fable"),
                ("Haiku", "claude-haiku-4-5", "Haiku")
            };

            if (isMapping)
            {
                foreach (var (rName, defModel, defDisplay) in roles)
                {
                    var m = p.ModelMappings?.FirstOrDefault(x => string.Equals(x.Role, rName, StringComparison.OrdinalIgnoreCase));
                    var displayName = !string.IsNullOrWhiteSpace(m?.DisplayName) ? m.DisplayName.Trim() : (!string.IsNullOrWhiteSpace(m?.Model) ? m.Model.Trim() : defDisplay);
                    var cleanDisplay = ClaudeCli.Strip1m(displayName);
                    bool supports1m = m?.Supports1m == true;

                    models.Add(new JsonObject
                    {
                        ["name"] = defModel,
                        ["modelId"] = defModel,
                        ["labelOverride"] = cleanDisplay,
                        ["displayName"] = cleanDisplay,
                        ["supports1m"] = supports1m
                    });
                }
            }
            else
            {
                foreach (var id in CollectModelIds(p))
                {
                    models.Add(new JsonObject
                    {
                        ["name"] = id,
                        ["modelId"] = id,
                        ["labelOverride"] = id,
                        ["displayName"] = id
                    });
                }
                if (models.Count == 0)
                {
                    models.Add(new JsonObject
                    {
                        ["name"] = "claude-sonnet-4-5",
                        ["modelId"] = "claude-sonnet-4-5",
                        ["labelOverride"] = "claude-sonnet-4-5",
                        ["displayName"] = "claude-sonnet-4-5"
                    });
                }
            }
            root["inferenceModels"] = models;

            var allowedHosts = new JsonArray("*");
            root["coworkEgressAllowedHosts"] = allowedHosts;

            // 核心：同步写入 Claude 3P 官方托管库 (%LOCALAPPDATA%\Claude-3p\configLibrary)
            SyncProviderToConfigLibrary(p, effectiveBaseUrl, models);
        }

        // 多播原子写入所有候选路径（优先主路径，并同步写入已存在的目录）
        var targetPaths = GetAllCandidateConfigPaths();
        var jsonText = root.ToJsonString(WriteOpts);
        foreach (var path in targetPaths)
        {
            try
            {
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir))
                {
                    if (!Directory.Exists(dir))
                    {
                        // 仅为前两个核心路径自动创建目录
                        if (path.Contains("Claude-3p") || path.EndsWith("Claude\\claude_desktop_config.json"))
                        {
                            Directory.CreateDirectory(dir);
                        }
                        else
                        {
                            continue;
                        }
                    }
                    FileUtil.AtomicWriteText(path, jsonText);
                }
            }
            catch { }
        }
    }

    private static void SyncOfficialToConfigLibrary()
    {
        try
        {
            if (Directory.Exists(ConfigLibraryDir))
            {
                if (File.Exists(ApiSwitchConfigFile))
                {
                    try { File.Delete(ApiSwitchConfigFile); } catch { }
                }
                var legacyCc = Path.Combine(ConfigLibraryDir, $"{CcSwitchLegacyId}.json");
                if (File.Exists(legacyCc))
                {
                    try { File.Delete(legacyCc); } catch { }
                }

                if (File.Exists(MetaPath))
                {
                    var metaObj = new JsonObject
                    {
                        ["appliedId"] = "",
                        ["entries"] = new JsonArray()
                    };
                    FileUtil.AtomicWriteText(MetaPath, metaObj.ToJsonString(WriteOpts));
                }
            }
        }
        catch { }
    }

    private static void SyncProviderToConfigLibrary(ClaudeProvider p, string effectiveBaseUrl, JsonArray models)
    {
        try
        {
            Directory.CreateDirectory(ConfigLibraryDir);

            // 1. 清理旧残留的 CC Switch 配置文件
            var legacyCc = Path.Combine(ConfigLibraryDir, $"{CcSwitchLegacyId}.json");
            if (File.Exists(legacyCc))
            {
                try { File.Delete(legacyCc); } catch { }
            }

            // 2. 构造 3P Managed Config 对象 (00000000-0000-4000-8000-000000157250.json)
            var configLibraryModels = new JsonArray();
            (string role, string defaultModel, string defaultName)[] roles =
            {
                ("Sonnet", "claude-sonnet-5", "Sonnet"),
                ("Opus", "claude-opus-5", "Opus"),
                ("Fable", "claude-fable-5", "Fable"),
                ("Haiku", "claude-haiku-4-5", "Haiku")
            };

            foreach (var (rName, defModel, defDisplay) in roles)
            {
                var m = p.ModelMappings?.FirstOrDefault(x => string.Equals(x.Role, rName, StringComparison.OrdinalIgnoreCase));
                var displayName = !string.IsNullOrWhiteSpace(m?.DisplayName) ? m.DisplayName.Trim() : (!string.IsNullOrWhiteSpace(m?.Model) ? m.Model.Trim() : defDisplay);
                var cleanDisplay = ClaudeCli.Strip1m(displayName);
                bool supports1m = m?.Supports1m == true;

                configLibraryModels.Add(new JsonObject
                {
                    ["name"] = defModel,
                    ["labelOverride"] = cleanDisplay,
                    ["supports1m"] = supports1m
                });
            }

            var configObj = new JsonObject
            {
                ["coworkEgressAllowedHosts"] = new JsonArray("*"),
                ["disableDeploymentModeChooser"] = true,
                ["inferenceGatewayApiKey"] = p.AuthToken ?? "",
                ["inferenceGatewayAuthScheme"] = "bearer",
                ["inferenceGatewayBaseUrl"] = effectiveBaseUrl,
                ["inferenceModels"] = configLibraryModels,
                ["inferenceProvider"] = "gateway"
            };

            FileUtil.AtomicWriteText(ApiSwitchConfigFile, configObj.ToJsonString(WriteOpts));

            // 3. 构造或更新 _meta.json
            var metaObj = new JsonObject
            {
                ["appliedId"] = ApiSwitchConfigId,
                ["entries"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["id"] = ApiSwitchConfigId,
                        ["name"] = "APISwitch - " + p.Name
                    }
                }
            };

            FileUtil.AtomicWriteText(MetaPath, metaObj.ToJsonString(WriteOpts));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ClaudeDesktopCli] SyncProviderToConfigLibrary error: {ex.Message}");
        }
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

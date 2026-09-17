using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using APISwitch.Models;
using Microsoft.Data.Sqlite;
using Tomlyn;
using Tomlyn.Model;

namespace APISwitch.Services;

public class CcImportItem
{
    public string AppType { get; init; } = "";
    public string Name { get; init; } = "";
    public bool IsCurrent { get; init; }
    public bool IsOfficial { get; init; }
    public string Summary { get; init; } = "";
    public bool Selected { get; set; }
    public ClaudeProvider? Claude { get; init; }
    public CodexProvider? Codex { get; init; }
    public OpenCodeProvider? OpenCode { get; init; }
    public PiProvider? Pi { get; init; }

    public string TypeLabel => AppType switch
    {
        "claude" => "Claude CLI",
        "claude-desktop" => "Claude 客户端",
        "codex" => "Codex",
        "opencode" => "OpenCode",
        "pi" => "Pi",
        _ => AppType,
    };

    public string CurrentLabel => IsCurrent ? (AppType is "opencode" or "pi" ? "● cc 已在配置池" : "● cc 当前") : "";
}

public static class CcSwitchImport
{
    public static string DbPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cc-switch", "cc-switch.db");

    public static bool IsAvailable => File.Exists(DbPath);

    public static List<CcImportItem> Load()
    {
        var items = new List<CcImportItem>();
        if (!IsAvailable) return items;

        var connStr = new SqliteConnectionStringBuilder
        {
            DataSource = DbPath,
            Mode = SqliteOpenMode.ReadOnly,
            DefaultTimeout = 5,
        }.ToString();

        using var conn = new SqliteConnection(connStr);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT app_type, name, is_current, settings_config, id, website_url, notes, meta FROM providers WHERE app_type IN ('claude','claude-desktop','codex','opencode','pi') ORDER BY app_type, sort_index";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var appType = reader.GetString(0);
            var name = reader.GetString(1);
            var isCurrent = reader.GetInt64(2) != 0;
            var cfgText = reader.IsDBNull(3) ? null : reader.GetString(3);
            var id = reader.IsDBNull(4) ? null : reader.GetString(4);
            var websiteUrl = reader.IsDBNull(5) ? null : reader.GetString(5);
            var notes = reader.IsDBNull(6) ? null : reader.GetString(6);
            var metaText = reader.IsDBNull(7) ? null : reader.GetString(7);
            var item = Parse(appType, name, isCurrent, cfgText, id, websiteUrl, notes, metaText);
            if (item != null) items.Add(item);
        }
        return items;
    }

    static CcImportItem? Parse(string appType, string name, bool isCurrent, string? cfgText, string? id, string? websiteUrl, string? notes, string? metaText)
    {
        try
        {
            if (appType is "claude" or "claude-desktop")
            {
                var cfg = JsonNode.Parse(cfgText ?? "{}")?.AsObject();
                var env = cfg?["env"]?.AsObject();
                var official = env == null || env.Count == 0;
                var p = new ClaudeProvider
                {
                    Id = !string.IsNullOrWhiteSpace(id) ? id : name,
                    Name = name,
                    Notes = notes,
                    WebsiteUrl = websiteUrl,
                    IsOfficial = official,
                    BaseUrl = Str(env, "ANTHROPIC_BASE_URL"),
                    AuthToken = Str(env, "ANTHROPIC_AUTH_TOKEN") ?? Str(env, "ANTHROPIC_API_KEY"),
                    Model = Str(env, "ANTHROPIC_MODEL"),
                    SmallFastModel = Str(env, "ANTHROPIC_SMALL_FAST_MODEL"),
                };
                var meta = JsonNode.Parse(metaText ?? "{}")?.AsObject();
                var apiFormat = meta?["apiFormat"]?.GetValue<string>();
                p.WireApi = (apiFormat == "openai" || apiFormat == "chat") ? "chat" : "anthropic";

                if (appType == "claude-desktop")
                {
                    if (meta?["claudeDesktopModelRoutes"] is JsonObject routes)
                    {
                        p.AccessMode = "mapping";
                        var roles = new (string role, string key)[] {
                            ("Sonnet", "claude-sonnet-5"),
                            ("Opus", "claude-opus-5"),
                            ("Fable", "claude-fable-5"),
                            ("Haiku", "claude-haiku-4-5")
                        };

                        foreach (var (rName, rKey) in roles)
                        {
                            JsonObject? rObj = routes[rKey]?.AsObject();
                            if (rObj == null)
                            {
                                foreach (var kv in routes)
                                {
                                    if (kv.Key.Contains(rName, StringComparison.OrdinalIgnoreCase))
                                    {
                                        rObj = kv.Value?.AsObject();
                                        break;
                                    }
                                }
                            }

                            var rModel = rObj?["model"]?.GetValue<string>() ?? "";
                            var rLabel = rObj?["labelOverride"]?.GetValue<string>() ?? rModel;
                            var r1m = rObj?["supports1m"]?.GetValue<bool>() ?? true;

                            p.ModelMappings.Add(new ClaudeModelMapping
                            {
                                Role = rName,
                                Model = ClaudeCli.Strip1m(rModel),
                                DisplayName = ClaudeCli.Strip1m(rLabel),
                                Supports1m = r1m
                            });
                        }
                    }
                }
                else if (appType == "claude")
                {
                    p.AccessMode = "mapping";
                    var roles = new (string role, string envKey, string nameKey)[] {
                        ("Sonnet", "ANTHROPIC_DEFAULT_SONNET_MODEL", "ANTHROPIC_DEFAULT_SONNET_MODEL_NAME"),
                        ("Opus", "ANTHROPIC_DEFAULT_OPUS_MODEL", "ANTHROPIC_DEFAULT_OPUS_MODEL_NAME"),
                        ("Fable", "ANTHROPIC_DEFAULT_FABLE_MODEL", "ANTHROPIC_DEFAULT_FABLE_MODEL_NAME"),
                        ("Haiku", "ANTHROPIC_DEFAULT_HAIKU_MODEL", "ANTHROPIC_DEFAULT_HAIKU_MODEL_NAME")
                    };

                    foreach (var (rName, envKey, nameKey) in roles)
                    {
                        var raw = Str(env, envKey) ?? p.Model ?? "";
                        var disp = Str(env, nameKey);
                        bool s1m = raw.EndsWith("[1M]", StringComparison.OrdinalIgnoreCase);
                        var clean = ClaudeCli.Strip1m(raw);
                        p.ModelMappings.Add(new ClaudeModelMapping
                        {
                            Role = rName,
                            Model = clean,
                            DisplayName = !string.IsNullOrWhiteSpace(disp) ? disp : clean,
                            Supports1m = s1m || string.IsNullOrEmpty(raw)
                        });
                    }
                }

                if (env != null)
                {
                    foreach (var kv in env)
                    {
                        if (kv.Key is "ANTHROPIC_BASE_URL" or "ANTHROPIC_AUTH_TOKEN" or "ANTHROPIC_API_KEY"
                            or "ANTHROPIC_MODEL" or "ANTHROPIC_SMALL_FAST_MODEL") continue;
                        var v = kv.Value?.GetValue<string>();
                        if (v != null)
                        {
                            p.ExtraEnv[kv.Key] = v;
                            p.ExtraOptions[kv.Key] = v;
                        }
                    }
                }
                return new CcImportItem
                {
                    AppType = appType,
                    Name = name,
                    IsCurrent = isCurrent,
                    IsOfficial = official,
                    Summary = official ? "官方" : p.BaseUrl ?? "—",
                    Claude = p,
                };
            }

            if (appType == "codex")
            {
                var cfg = JsonNode.Parse(cfgText ?? "{}")?.AsObject();
                var tomlText = cfg?["config"]?.GetValue<string>();
                var p = new CodexProvider
                {
                    Id = !string.IsNullOrWhiteSpace(id) ? id : name,
                    Name = name,
                    Notes = notes,
                    WebsiteUrl = websiteUrl,
                };

                string? providerId = null;
                string? catalogRelPath = null;
                if (!string.IsNullOrWhiteSpace(tomlText))
                {
                    var toml = TomlSerializer.Deserialize<TomlTable>(tomlText);
                    if (toml != null)
                    {
                        if (toml.TryGetValue("model_provider", out var mp)) providerId = mp?.ToString();
                        if (toml.TryGetValue("model", out var m)) p.Model = m?.ToString();
                        if (toml.TryGetValue("model_catalog_json", out var mcj)) catalogRelPath = mcj?.ToString();
                        if (providerId != null &&
                            toml.TryGetValue("model_providers", out var providers) && providers is TomlTable pt &&
                            pt.TryGetValue(providerId, out var entryObj) && entryObj is TomlTable entry)
                        {
                            if (entry.TryGetValue("base_url", out var bu)) p.BaseUrl = bu?.ToString();
                            if (entry.TryGetValue("wire_api", out var wa)) p.WireApi = wa?.ToString() ?? "responses";
                            if (entry.TryGetValue("experimental_bearer_token", out var bt)) p.BearerToken = bt?.ToString();
                        }
                    }
                }

                if (cfg?["modelCatalog"]?["models"] is JsonArray mcArr)
                {
                    foreach (var item in mcArr)
                    {
                        var mId = item?["model"]?.GetValue<string>() ?? item?["slug"]?.GetValue<string>();
                        var dName = item?["displayName"]?.GetValue<string>() ?? item?["display_name"]?.GetValue<string>() ?? mId;
                        var cwStr = item?["context_window"]?.ToString() ?? item?["contextWindow"]?.ToString();
                        var cw = !string.IsNullOrWhiteSpace(cwStr) ? cwStr : "1m";
                        if (!string.IsNullOrWhiteSpace(mId) && !p.CustomModels.Any(x => string.Equals(x.Id, mId, StringComparison.OrdinalIgnoreCase)))
                        {
                            p.CustomModels.Add(new ProviderModelEntry { Id = mId.Trim(), Name = dName?.Trim() ?? mId.Trim(), ContextWindow = cw });
                        }
                    }
                }

                if (p.CustomModels.Count == 0 && !string.IsNullOrWhiteSpace(catalogRelPath))
                {
                    try
                    {
                        var catFull = Path.IsPathRooted(catalogRelPath)
                            ? catalogRelPath
                            : Path.Combine(CodexCli.CodexDir, catalogRelPath);
                        if (File.Exists(catFull))
                        {
                            var catJson = JsonNode.Parse(File.ReadAllText(catFull))?.AsObject();
                            if (catJson?["models"] is JsonArray arr)
                            {
                                foreach (var item in arr)
                                {
                                    var mId = item?["slug"]?.GetValue<string>() ?? item?["model"]?.GetValue<string>();
                                    var dName = item?["display_name"]?.GetValue<string>() ?? item?["displayName"]?.GetValue<string>() ?? mId;
                                    var cwStr = item?["context_window"]?.ToString() ?? item?["contextWindow"]?.ToString();
                                    var cw = !string.IsNullOrWhiteSpace(cwStr) ? cwStr : "1m";
                                    if (!string.IsNullOrWhiteSpace(mId) && !p.CustomModels.Any(x => string.Equals(x.Id, mId, StringComparison.OrdinalIgnoreCase)))
                                    {
                                        p.CustomModels.Add(new ProviderModelEntry { Id = mId.Trim(), Name = dName?.Trim() ?? mId.Trim(), ContextWindow = cw });
                                    }
                                }
                            }
                        }
                    }
                    catch { }
                }


                var metaNode = JsonNode.Parse(metaText ?? "{}")?.AsObject();
                var metaFormat = metaNode?["apiFormat"]?.GetValue<string>();
                if (metaFormat == "openai_chat") p.WireApi = "chat";
                else if (metaFormat == "anthropic") p.WireApi = "anthropic";

                var authNode = cfg?["auth"];
                if (authNode != null)
                {
                    var authText = authNode is JsonValue ? authNode.GetValue<string>() : authNode.ToJsonString();
                    try
                    {
                        var auth = JsonNode.Parse(authText)?.AsObject();
                        p.ApiKey = Str(auth, "OPENAI_API_KEY");
                    }
                    catch { }
                }

                p.IsOfficial = string.IsNullOrEmpty(providerId) && string.IsNullOrEmpty(p.BaseUrl);
                return new CcImportItem
                {
                    AppType = appType,
                    Name = name,
                    IsCurrent = isCurrent,
                    IsOfficial = p.IsOfficial,
                    Summary = p.IsOfficial ? "官方" : p.BaseUrl ?? "—",
                    Codex = p,
                };
            }
            if (appType == "opencode")
            {
                var cfg = JsonNode.Parse(cfgText ?? "{}")?.AsObject();
                if (cfg == null) return null;
                var p = new OpenCodeProvider
                {
                    Id = !string.IsNullOrWhiteSpace(id) ? id : name,
                    Name = name,
                    Notes = notes,
                    WebsiteUrl = websiteUrl,
                    Npm = Str(cfg, "npm") ?? "@ai-sdk/openai-compatible",
                };
                var options = cfg["options"]?.AsObject();
                if (options != null)
                {
                    try { p.BaseUrl = Str(options, "baseURL"); } catch { }
                    try { p.ApiKey = Str(options, "apiKey"); } catch { }
                    foreach (var kv in options)
                    {
                        if (kv.Key is "baseURL" or "apiKey" or "headers") continue;
                        p.ExtraOptions[kv.Key] = kv.Value?.ToString() ?? "";
                    }
                    if (options["headers"] is JsonObject hObj)
                    {
                        foreach (var kv in hObj)
                            p.CustomHeaders[kv.Key] = kv.Value?.ToString() ?? "";
                    }
                }
                var models = cfg["models"]?.AsObject();
                if (models != null)
                {
                    p.ModelsJson = models.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
                    foreach (var kv in models)
                    {
                        var dName = kv.Value?["name"]?.GetValue<string>() ?? "";
                        p.CustomModels.Add(new ProviderModelEntry { Id = kv.Key, Name = dName });
                    }
                }
                bool inPool = isCurrent;
                try
                {
                    var metaObj = JsonNode.Parse(metaText ?? "{}")?.AsObject();
                    if (metaObj != null && metaObj["liveConfigManaged"] != null)
                        inPool = metaObj["liveConfigManaged"]?.GetValue<bool>() ?? isCurrent;
                }
                catch { }

                return new CcImportItem
                {
                    AppType = appType,
                    Name = name,
                    IsCurrent = inPool,
                    Summary = p.BaseUrl ?? "—",
                    OpenCode = p,
                };
            }
            if (appType == "pi")
            {
                var cfg = JsonNode.Parse(cfgText ?? "{}")?.AsObject();
                if (cfg == null) return null;
                var p = new PiProvider
                {
                    Id = !string.IsNullOrWhiteSpace(id) ? id : name,
                    Name = name,
                    Notes = notes,
                    WebsiteUrl = websiteUrl,
                    BaseUrl = Str(cfg, "baseUrl"),
                    ApiKey = Str(cfg, "apiKey"),
                    Api = Str(cfg, "api") ?? "openai-completions",
                };
                if (cfg["headers"] is JsonObject hObj)
                {
                    foreach (var kv in hObj)
                        p.CustomHeaders[kv.Key] = kv.Value?.ToString() ?? "";
                }
                if (cfg["models"] is JsonArray mArr)
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
                bool inPool = isCurrent;
                try
                {
                    var metaObj = JsonNode.Parse(metaText ?? "{}")?.AsObject();
                    if (metaObj != null && metaObj["liveConfigManaged"] != null)
                        inPool = metaObj["liveConfigManaged"]?.GetValue<bool>() ?? isCurrent;
                }
                catch { }

                return new CcImportItem
                {
                    AppType = appType,
                    Name = name,
                    IsCurrent = inPool,
                    Summary = p.BaseUrl ?? "—",
                    Pi = p,
                };
            }
        }
        catch { }
        return null;
    }

    static string? Str(JsonObject? obj, string key)
    {
        try { return obj?[key]?.GetValue<string>(); } catch { return null; }
    }

    public static (int Claude, int Desktop, int Codex, int Oc, int Pi) Import(IEnumerable<CcImportItem> selected)
    {
        var claude = CliStore.LoadClaude();
        var desktop = CliStore.LoadClaudeDesktop();
        var codex = CliStore.LoadCodex();
        var opencode = CliStore.LoadOpencode();
        var pi = CliStore.LoadPiProviders();
        int nc = 0, nd = 0, nx = 0, no = 0, np = 0;

        foreach (var item in selected)
        {
            if (item.AppType is "claude" or "claude-desktop" && item.Claude != null)
            {
                var list = item.AppType == "claude" ? claude : desktop;
                if (item.IsOfficial && list.Any(x => x.IsOfficial)) continue;
                var idx = list.FindIndex(x => x.Name == item.Claude.Name);
                if (idx >= 0) list[idx] = item.Claude;
                else list.Add(item.Claude);
                if (item.AppType == "claude") nc++; else nd++;
            }
            else if (item.AppType == "codex" && item.Codex != null)
            {
                if (item.IsOfficial && codex.Any(x => x.IsOfficial)) continue;
                var idx = codex.FindIndex(x => x.Name == item.Codex.Name || x.Id == item.Codex.Id);
                if (idx >= 0) codex[idx] = item.Codex;
                else codex.Add(item.Codex);
                nx++;
            }
            else if (item.AppType == "opencode" && item.OpenCode != null)
            {
                var idx = opencode.FindIndex(x => x.Id == item.OpenCode.Id || x.Name == item.OpenCode.Name);
                if (idx >= 0) opencode[idx] = item.OpenCode;
                else opencode.Add(item.OpenCode);
                if (item.IsCurrent)
                {
                    OpenCodeCli.SaveProvider(item.OpenCode);
                }
                no++;
            }
            else if (item.AppType == "pi" && item.Pi != null)
            {
                var idx = pi.FindIndex(x => x.Id == item.Pi.Id || x.Name == item.Pi.Name);
                if (idx >= 0) pi[idx] = item.Pi;
                else pi.Add(item.Pi);
                if (item.IsCurrent)
                {
                    PiCli.SaveProvider(item.Pi);
                }
                np++;
            }
        }

        CliStore.SaveClaude(claude);
        CliStore.SaveClaudeDesktop(desktop);
        CliStore.SaveCodex(codex);
        CliStore.SaveOpencode(opencode);
        CliStore.SavePiProviders(pi);
        return (nc, nd, nx, no, np);
    }
}



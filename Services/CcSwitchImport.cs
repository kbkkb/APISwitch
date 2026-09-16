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

    public string TypeLabel => AppType switch
    {
        "claude" => "Claude CLI",
        "claude-desktop" => "Claude 客户端",
        "codex" => "Codex",
        "opencode" => "OpenCode",
        _ => AppType,
    };

    public string CurrentLabel => IsCurrent ? "● cc 当前" : "";
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
        cmd.CommandText = "SELECT app_type, name, is_current, settings_config FROM providers WHERE app_type IN ('claude','claude-desktop','codex','opencode') ORDER BY app_type, sort_index";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var appType = reader.GetString(0);
            var name = reader.GetString(1);
            var isCurrent = reader.GetInt64(2) != 0;
            var cfgText = reader.IsDBNull(3) ? null : reader.GetString(3);
            var item = Parse(appType, name, isCurrent, cfgText);
            if (item != null) items.Add(item);
        }
        return items;
    }

    static CcImportItem? Parse(string appType, string name, bool isCurrent, string? cfgText)
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
                    Name = name,
                    IsOfficial = official,
                    BaseUrl = Str(env, "ANTHROPIC_BASE_URL"),
                    AuthToken = Str(env, "ANTHROPIC_AUTH_TOKEN") ?? Str(env, "ANTHROPIC_API_KEY"),
                    Model = Str(env, "ANTHROPIC_MODEL"),
                    SmallFastModel = Str(env, "ANTHROPIC_SMALL_FAST_MODEL"),
                };
                if (env != null)
                {
                    foreach (var kv in env)
                    {
                        if (kv.Key is "ANTHROPIC_BASE_URL" or "ANTHROPIC_AUTH_TOKEN" or "ANTHROPIC_API_KEY"
                            or "ANTHROPIC_MODEL" or "ANTHROPIC_SMALL_FAST_MODEL") continue;
                        var v = kv.Value?.GetValue<string>();
                        if (v != null) p.ExtraEnv[kv.Key] = v;
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
                var p = new CodexProvider { Name = name };

                string? providerId = null;
                if (!string.IsNullOrWhiteSpace(tomlText))
                {
                    var toml = TomlSerializer.Deserialize<TomlTable>(tomlText);
                    if (toml != null)
                    {
                        if (toml.TryGetValue("model_provider", out var mp)) providerId = mp?.ToString();
                        if (toml.TryGetValue("model", out var m)) p.Model = m?.ToString();
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
                    Id = name,
                    Name = name,
                    Npm = Str(cfg, "npm") ?? "@ai-sdk/openai-compatible",
                };
                var options = cfg["options"]?.AsObject();
                if (options != null)
                {
                    try { p.BaseUrl = Str(options, "baseURL"); } catch { }
                    try { p.ApiKey = Str(options, "apiKey"); } catch { }
                }
                var models = cfg["models"]?.AsObject();
                if (models != null)
                    p.ModelsJson = models.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
                return new CcImportItem
                {
                    AppType = appType,
                    Name = name,
                    IsCurrent = isCurrent,
                    Summary = p.BaseUrl ?? "—",
                    OpenCode = p,
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

    public static (int Claude, int Desktop, int Codex, int Oc) Import(IEnumerable<CcImportItem> selected)
    {
        var claude = CliStore.LoadClaude();
        var desktop = CliStore.LoadClaudeDesktop();
        var codex = CliStore.LoadCodex();
        int nc = 0, nd = 0, nx = 0, no = 0;

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
                OpenCodeCli.SaveProvider(item.OpenCode);
                no++;
            }
        }

        CliStore.SaveClaude(claude);
        CliStore.SaveClaudeDesktop(desktop);
        CliStore.SaveCodex(codex);
        return (nc, nd, nx, no);
    }
}


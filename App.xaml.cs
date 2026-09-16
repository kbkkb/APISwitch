using System.IO;
using System.Text;
using System.Windows;
using APISwitch.Services;

namespace APISwitch;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        if (e.Args.Contains("--probe"))
        {
            RunProbe();
            Shutdown();
            return;
        }
        if (e.Args.Contains("--selftest"))
        {
            RunSelfTest();
            Shutdown();
            return;
        }
        base.OnStartup(e);
    }

    static void RunSelfTest()
    {
        var sb = new StringBuilder();
        var sandbox = Path.Combine(Path.GetTempPath(), "apiswitch-selftest-home");
        if (Directory.Exists(sandbox)) Directory.Delete(sandbox, recursive: true);
        Environment.SetEnvironmentVariable("APISWITCH_HOME", sandbox);
        var home = Services.ClaudeCli.HomeDir;
        sb.AppendLine("home=" + home);

        Directory.CreateDirectory(Path.Combine(home, ".claude"));
        File.WriteAllText(Path.Combine(home, ".claude", "settings.json"),
            "{\"permissions\":{\"allow\":[\"Bash(ls)\"]},\"hooks\":{\"PreToolUse\":[]},\"env\":{\"ANTHROPIC_BASE_URL\":\"http://old\",\"OTHER_ENV\":\"keep-me\"}}");
        Directory.CreateDirectory(Path.Combine(home, ".codex"));
        File.WriteAllText(Path.Combine(home, ".codex", "config.toml"),
            "# my comment\nmodel = \"gpt-old\"\nnotify = [\"a\", \"b\"]\n\n[desktop]\nfollowUpQueueMode = \"queue\"\n");
        File.WriteAllText(Path.Combine(home, ".codex", "auth.json"), "{\"tokens\":{\"old\":\"login\"}}");

        var relay = new Models.ClaudeProvider
        {
            Name = "TestRelay",
            BaseUrl = "https://relay.test/v1",
            AuthToken = "sk-test",
            Model = "claude-x",
            ExtraEnv = new Dictionary<string, string> { ["ANTHROPIC_DEFAULT_SONNET_MODEL"] = "sonnet-x" },
        };
        Services.ClaudeCli.Apply(relay);
        sb.AppendLine("claude_current=" + Services.ClaudeCli.CurrentBaseUrl());
        sb.AppendLine("claude_settings=" + File.ReadAllText(Services.ClaudeCli.SettingsPath));

        Services.ClaudeCli.Apply(new Models.ClaudeProvider { Name = "官方", IsOfficial = true });
        sb.AppendLine("claude_after_official=" + (Services.ClaudeCli.CurrentBaseUrl() ?? "official"));
        sb.AppendLine("claude_settings_official=" + File.ReadAllText(Services.ClaudeCli.SettingsPath));

        var codexRelay = new Models.CodexProvider
        {
            Name = "Test Relay",
            BaseUrl = "https://relay.test/v1",
            WireApi = "responses",
            ApiKey = "sk-test",
            Model = "gpt-test",
        };
        Services.CodexCli.Apply(codexRelay);
        sb.AppendLine("codex_current=" + Services.CodexCli.CurrentProviderId());
        sb.AppendLine("codex_config=" + File.ReadAllText(Services.CodexCli.ConfigPath));
        sb.AppendLine("codex_auth=" + File.ReadAllText(Services.CodexCli.AuthPath));

        Services.CodexCli.Apply(new Models.CodexProvider { Name = "官方", IsOfficial = true });
        sb.AppendLine("codex_after_official=" + (Services.CodexCli.CurrentProviderId() ?? "official"));
        sb.AppendLine("codex_auth_official=" + File.ReadAllText(Services.CodexCli.AuthPath));

        var appdata = Path.Combine(sandbox, "appdata");
        Environment.SetEnvironmentVariable("APISWITCH_APPDATA", appdata);
        Directory.CreateDirectory(Path.Combine(appdata, "Claude"));
        File.WriteAllText(Path.Combine(appdata, "Claude", "claude_desktop_config.json"),
            "{\"mcpServers\":{\"unityMCP\":{\"command\":\"uvx\"}}}");

        var desktopRelay = new Models.ClaudeProvider
        {
            Name = "TestRelay",
            BaseUrl = "https://relay.test",
            AuthToken = "sk-test",
            Model = "claude-sonnet-4-5",
            SmallFastModel = "claude-haiku-4-5",
        };
        Services.ClaudeDesktopCli.Apply(desktopRelay);
        sb.AppendLine("desktop_current=" + Services.ClaudeDesktopCli.CurrentGatewayUrl());
        sb.AppendLine("desktop_config=" + File.ReadAllText(Services.ClaudeDesktopCli.ConfigPath));
        Services.ClaudeDesktopCli.Apply(new Models.ClaudeProvider { Name = "官方", IsOfficial = true });
        sb.AppendLine("desktop_after_official=" + (Services.ClaudeDesktopCli.CurrentGatewayUrl() ?? "official"));
        sb.AppendLine("desktop_config_official=" + File.ReadAllText(Services.ClaudeDesktopCli.ConfigPath));

        var ocPath = Services.OpenCodeCli.ConfigPath;
        Directory.CreateDirectory(Path.GetDirectoryName(ocPath)!);
        File.WriteAllText(ocPath, "{\"$schema\":\"x\",\"mcp\":{\"a\":1},\"plugin\":[\"p1\"],\"provider\":{\"old\":{\"npm\":\"@ai-sdk/openai-compatible\",\"options\":{\"baseURL\":\"http://o\"}}}}");
        var ocP = new Models.OpenCodeProvider
        {
            Id = "test-oc",
            Npm = "@ai-sdk/openai-compatible",
            BaseUrl = "https://oc.test/v1",
            ApiKey = "sk-test",
            ModelsJson = "{\"model-a\":{}}",
        };
        Services.OpenCodeCli.SaveProvider(ocP, setDefaultModel: true, defaultModelId: "model-a");
        sb.AppendLine("oc_current=" + Services.OpenCodeCli.CurrentModel());
        sb.AppendLine("oc_providers=" + string.Join(",", Services.OpenCodeCli.ProviderIds()));
        sb.AppendLine("oc_config=" + File.ReadAllText(ocPath).Replace('\n', ' '));
        Services.OpenCodeCli.DeleteProvider("test-oc");
        sb.AppendLine("oc_after_delete=" + string.Join(",", Services.OpenCodeCli.ProviderIds()));

        Services.PiCli.AuthPath.GetHashCode();
        var piAuth = Path.Combine(sandbox, ".pi", "agent");
        Environment.SetEnvironmentVariable("APISWITCH_HOME", sandbox);
        Directory.CreateDirectory(piAuth);
        File.WriteAllText(Path.Combine(piAuth, "auth.json"), "{\"openai\":{\"type\":\"api_key\"}}");
        File.WriteAllText(Path.Combine(piAuth, "settings.json"), "{\"defaultProvider\":\"openai\",\"defaultModel\":\"gpt-x\",\"theme\":\"dark\"}");
        var acc = Services.PiCli.Capture("snap-1");
        sb.AppendLine("pi_capture=" + acc.DefaultProvider + "/" + acc.DefaultModel);
        File.WriteAllText(Path.Combine(piAuth, "auth.json"), "{\"openrouter\":{\"type\":\"api_key\"}}");
        File.WriteAllText(Path.Combine(piAuth, "settings.json"), "{\"defaultProvider\":\"openrouter\",\"defaultModel\":\"r1\"}");
        sb.AppendLine("pi_changed_current=" + Services.PiCli.MatchesCurrent(acc));
        Services.PiCli.Restore(acc);
        sb.AppendLine("pi_after_restore=" + Services.PiCli.CurrentDefaults().Provider + "/" + Services.PiCli.CurrentDefaults().Model);
        sb.AppendLine("pi_after_restore_current=" + Services.PiCli.MatchesCurrent(acc));

        File.WriteAllText(Path.Combine(Path.GetTempPath(), "apiswitch-selftest.txt"), sb.ToString());
    }

    static void RunProbe()
    {
        var sb = new StringBuilder();
        var db = AgPaths.FindStateDb();
        sb.AppendLine("db=" + (db ?? "null"));
        sb.AppendLine("exe=" + (AgPaths.FindIdeExecutable() ?? "null"));
        sb.AppendLine("running=" + AgProcess.IsRunning());
        if (db != null)
        {
            try
            {
                var auth = AgDb.ReadAuth(db);
                foreach (var kv in auth) sb.AppendLine($"key={kv.Key} len={kv.Value.Length}");
                auth.TryGetValue(AgState.KeyUserStatus, out var us);
                auth.TryGetValue(AgState.KeyOauthToken, out var ot);
                var email = AgState.ExtractEmail(us);
                sb.AppendLine("email=" + (email ?? "null"));
                sb.AppendLine("plan=" + (AgState.ExtractPlan(us) ?? "null"));
                sb.AppendLine("authState=" + (AgState.ExtractAuthState(ot, email) ?? "null"));
            }
            catch (Exception ex)
            {
                sb.AppendLine("error=" + ex.Message);
            }
        }
        foreach (var p in ProfileStore.Load())
            sb.AppendLine($"profile={p.Email} plan={p.Plan} at={p.CapturedAtUtc:o} keys={p.Values.Count}");
        try
        {
            sb.AppendLine("claude_settings=" + ClaudeCli.SettingsPath);
            sb.AppendLine("claude_current=" + (ClaudeCli.CurrentBaseUrl() ?? "official"));
            sb.AppendLine("codex_current=" + (CodexCli.CurrentProviderId() ?? "official"));
            sb.AppendLine("desktop_config=" + ClaudeDesktopCli.ConfigPath);
            sb.AppendLine("desktop_current=" + (ClaudeDesktopCli.CurrentGatewayUrl() ?? "official"));
            sb.AppendLine("opencode_config=" + OpenCodeCli.ConfigPath);
            sb.AppendLine("opencode_current=" + (OpenCodeCli.CurrentModel() ?? "unset"));
            sb.AppendLine("opencode_providers_live=" + OpenCodeCli.ProviderIds().Count);
            if (PiCli.IsInstalled)
            {
                var (pp, pm) = PiCli.CurrentDefaults();
                sb.AppendLine("pi_defaults=" + (pp ?? "-") + " / " + (pm ?? "-"));
                sb.AppendLine("pi_auth_providers=" + PiCli.AuthProviders().Count);
            }
            else sb.AppendLine("pi=not-installed");
            sb.AppendLine("pi_snapshots=" + CliStore.LoadPi().Count);
            sb.AppendLine("claude_providers=" + CliStore.LoadClaude().Count);
            sb.AppendLine("desktop_providers=" + CliStore.LoadClaudeDesktop().Count);
            sb.AppendLine("codex_providers=" + CliStore.LoadCodex().Count);
            if (CcSwitchImport.IsAvailable)
            {
                var items = CcSwitchImport.Load();
                sb.AppendLine("cc_import_items=" + items.Count);
                foreach (var g in items.GroupBy(i => i.AppType))
                    sb.AppendLine($"  cc[{g.Key}]={g.Count()} e.g. {g.First().Name} -> {g.First().Summary} official={g.First().IsOfficial}");
            }
            else sb.AppendLine("cc_import_items=db-missing");
        }
        catch (Exception ex)
        {
            sb.AppendLine("cli_error=" + ex.Message);
        }
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "apiswitch-probe.txt"), sb.ToString());
    }
}

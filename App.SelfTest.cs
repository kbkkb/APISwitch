using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using APISwitch.Services;

namespace APISwitch;

public partial class App
{
    static async Task RunSelfTest()
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

        // Pi 默认模型 + contextWindow 写入链路断言
        try
        {
            Services.PiCli.SaveProvider(new Models.PiProvider
            {
                Id = "test-pi",
                Name = "Test Pi",
                BaseUrl = "https://pi.test/v1",
                ApiKey = "sk-pi",
                Api = "openai-completions",
                CustomModels = new List<Models.ProviderModelEntry>
                {
                    new() { Id = "m1", Name = "M1", ContextWindow = "1m" },
                    new() { Id = "m2", Name = "M2", ContextWindow = "128k" },
                },
                DefaultModel = "m1",
            }, setDefaultModel: true, defaultModelId: "m1");

            var piModelsJson = File.ReadAllText(Services.PiCli.ModelsPath);
            sb.AppendLine("pi_save_has_default=" + piModelsJson.Contains("test-pi"));
            sb.AppendLine("pi_models_ctx_1m=" + piModelsJson.Contains("1048576"));
            sb.AppendLine("pi_models_ctx_128k=" + piModelsJson.Contains("131072"));

            var (piProv, piModel) = Services.PiCli.CurrentDefaults();
            sb.AppendLine("pi_default_model=" + piProv + "/" + piModel);

            // OpenCode 默认模型写入链路断言
            Services.OpenCodeCli.SaveProvider(new Models.OpenCodeProvider
            {
                Id = "test-oc",
                Name = "Test OC",
                Npm = "@ai-sdk/openai-compatible",
                BaseUrl = "https://oc.test/v1",
                ApiKey = "sk-oc",
                DefaultModel = "model-a",
            }, setDefaultModel: true, defaultModelId: "model-a");

            var ocCur = Services.OpenCodeCli.CurrentModel();
            sb.AppendLine("oc_default_model=" + (ocCur ?? "unset"));
        }
        catch (Exception ex)
        {
            sb.AppendLine("pi_oc_default_model_err=" + ex.Message);
        }

        try
        {
            var mw = new MainWindow { Visibility = Visibility.Hidden };
            for (int i = 0; i < mw.Tabs.Items.Count; i++)
            {
                mw.Tabs.SelectedIndex = i;
                mw.UpdateLayout();
            }
            mw.Close();
            sb.AppendLine("ui_tabs_render=ok");
        }
        catch (Exception ex)
        {
            sb.AppendLine("ui_tabs_render_err=" + ex);
        }

        // 用量监控窗口：覆盖 5 个视图页签、筛选条、图表模式切换与 Antigravity 专属额度视角
        try
        {
            var umw = new Dialogs.UsageMonitorWindow { Visibility = Visibility.Hidden };
            umw.UpdateLayout();
            umw.TabAgentsRadio.IsChecked = true;
            umw.UpdateLayout();
            umw.TabModelsRadio.IsChecked = true;
            umw.UpdateLayout();
            umw.TabCallsRadio.IsChecked = true;
            umw.UpdateLayout();
            umw.ChartModeHeatmapRadio.IsChecked = true;
            umw.UpdateLayout();
            umw.SearchBox.Text = "claude";
            umw.UpdateLayout();
            umw.SearchBox.Text = "";
            // 极端悬殊日用量回归探测（避免高/低谷差距 > 1000 万倍时 Math.Clamp min>max 崩溃）
            umw.TestRenderExtremeTrend(new List<Services.UsageStorage.DailyTotal>
            {
                new("2026-11-14", 500_000_000, 1_000_000, 200),
                new("2026-11-15", 0, 15, 1),
                new("2026-11-16", 10, 5, 1),
            });
            umw.ChartModeBarRadio.IsChecked = true;
            umw.TestRenderExtremeTrend(new List<Services.UsageStorage.DailyTotal>
            {
                new("2026-11-14", 500_000_000, 1_000_000, 200),
                new("2026-11-15", 0, 15, 1),
                new("2026-11-16", 10, 5, 1),
            });
            umw.TabProvidersRadio.IsChecked = true;
            var pills = umw.ClientFilterList.Items.OfType<Dialogs.UsageMonitorWindow.ClientFilterItemVM>().ToList();
            if (pills.Count != 11 || !pills[0].IsSelected || pills.Count(p => p.IsSelected) != 1)
                throw new Exception($"ClientFilterList initial state mismatch: count={pills.Count}, first={pills.FirstOrDefault()?.IsSelected}");
            umw.SelectAgentFilter(Services.UsageStorage.AgentCodex);
            if (!pills.Any(p => p.Agent == Services.UsageStorage.AgentCodex && p.IsSelected) || pills[0].IsSelected)
                throw new Exception("ClientFilterList failed to select single agent");
            umw.SelectAgentFilter(null);
            if (!pills[0].IsSelected || pills.Count(p => p.IsSelected) != 1)
                throw new Exception("ClientFilterList failed to restore All");
            umw.UpdateLayout();
            umw.Close();
            sb.AppendLine("ui_usage_monitor_render=ok");
        }
        catch (Exception ex)
        {
            sb.AppendLine("ui_usage_monitor_render_err=" + ex);
        }
        // ===== 思考强度（模型级）写入断言 =====
        try
        {
            // 1) Codex：全局兜底 = 各模型最高档（per-model 由代理按请求模型注入）
            Services.CodexCli.Apply(new Models.CodexProvider
            {
                Id = "test-codex-think",
                Name = "Test Codex Think",
                BaseUrl = "https://codex.test/v1",
                WireApi = "responses",
                ApiKey = "sk-cx",
                Model = "gpt-5",
                CustomModels = new List<Models.ProviderModelEntry>
                {
                    new() { Id = "gpt-5", Name = "GPT-5", ThinkingEffort = "low" },
                    new() { Id = "gpt-5-mini", Name = "GPT-5 mini", ThinkingEffort = "high" },
                },
            });
            var codexCfg = File.ReadAllText(Services.CodexCli.ConfigPath);
            sb.AppendLine("think_codex_written=" + codexCfg.Contains("model_reasoning_effort = \"high\""));
            Services.CodexCli.Apply(new Models.CodexProvider { Name = "官方", IsOfficial = true });
            sb.AppendLine("think_codex_cleared=" + !File.ReadAllText(Services.CodexCli.ConfigPath).Contains("model_reasoning_effort"));

            // 2) Claude CLI：env 兜底 = 映射表最高预算（low=4096 / high=32768）
            Services.ClaudeCli.Apply(new Models.ClaudeProvider
            {
                Name = "TestClaudeThink",
                BaseUrl = "https://claude.test",
                AuthToken = "sk-t",
                WireApi = "anthropic",
                AccessMode = "mapping",
                ModelMappings = new List<Models.ClaudeModelMapping>
                {
                    new() { Role = "Sonnet", Model = "claude-sonnet-x", ThinkingEffort = "low" },
                    new() { Role = "Opus", Model = "claude-opus-x", ThinkingEffort = "high" },
                },
            });
            var claudeSettings = File.ReadAllText(Services.ClaudeCli.SettingsPath);
            sb.AppendLine("think_claude_written=" + claudeSettings.Contains("\"MAX_THINKING_TOKENS\": \"32768\""));
            Services.ClaudeCli.Apply(new Models.ClaudeProvider { Name = "官方", IsOfficial = true });
            sb.AppendLine("think_claude_cleared=" + !File.ReadAllText(Services.ClaudeCli.SettingsPath).Contains("MAX_THINKING_TOKENS"));

            // 3) Pi：models[] 按模型 reasoning + settings modelThinkingLevels
            Services.PiCli.SaveProvider(new Models.PiProvider
            {
                Id = "test-pi-think",
                Name = "Test Pi Think",
                BaseUrl = "https://pi.test/v1",
                ApiKey = "sk-pi",
                CustomModels = new List<Models.ProviderModelEntry>
                {
                    new() { Id = "m1", Name = "M1", ContextWindow = "1m", ThinkingEffort = "medium" },
                    new() { Id = "m2", Name = "M2", ContextWindow = "1m" },
                },
            });
            var piModels = File.ReadAllText(Services.PiCli.ModelsPath);
            sb.AppendLine("think_pi_reasoning=" + piModels.Contains("\"reasoning\": true"));
            var piSettings = File.ReadAllText(Services.PiCli.SettingsPath);
            sb.AppendLine("think_pi_level_map=" + piSettings.Contains("\"test-pi-think/m1\": \"medium\""));

            // 4) OpenCode：per-model options（仅配置了的模型带 options）
            Services.OpenCodeCli.SaveProvider(new Models.OpenCodeProvider
            {
                Id = "test-oc-think",
                Name = "Test OC Think",
                Npm = "@ai-sdk/openai-compatible",
                BaseUrl = "https://oc.test/v1",
                ApiKey = "sk-oc",
                CustomModels = new List<Models.ProviderModelEntry>
                {
                    new() { Id = "m1", Name = "M1", ThinkingEffort = "high" },
                    new() { Id = "m2", Name = "M2" },
                },
            });
            var ocCfg = File.ReadAllText(Services.OpenCodeCli.ConfigPath);
            sb.AppendLine("think_oc_reasoning_effort=" + ocCfg.Contains("\"reasoningEffort\": \"high\""));
            sb.AppendLine("think_oc_no_effort_model_clean=" + (ocCfg.Split("\"m2\"").Length <= 2));

            Services.OpenCodeCli.SaveProvider(new Models.OpenCodeProvider
            {
                Id = "test-oc-think-anth",
                Name = "Test OC Think Anth",
                Npm = "@ai-sdk/anthropic",
                BaseUrl = "https://oc2.test/v1",
                ApiKey = "sk-oc2",
                CustomModels = new List<Models.ProviderModelEntry>
                {
                    new() { Id = "m1", Name = "M1", ThinkingEffort = "low" },
                },
            });
            var ocCfg2 = File.ReadAllText(Services.OpenCodeCli.ConfigPath);
            sb.AppendLine("think_oc_thinking_budget=" + ocCfg2.Contains("\"budgetTokens\": 4096"));
        }
        catch (Exception ex)
        {
            sb.AppendLine("think_channels_err=" + ex.Message);
        }
        // ===== Antigravity 激活引擎断言 =====
        try
        {
            // 1) Claude 候选模型动态筛选：sonnet 优先 + 版本升序（最老优先）
            var claudePicked = Services.AgQuotaService.ResolveClaudeModels(new List<string>
            {
                "gemini-2.5-flash", "claude-opus-4-6", "claude-sonnet-4-6", "claude-sonnet-4-5", "gpt-5"
            });
            sb.AppendLine("ag_claude_pick=" + string.Join(",", claudePicked));

            // 1b) Gemini 候选：无 low/lite 后缀的新模型（如 gemini-3.5-flash）也必须被选中；
            //     有空列表时必须返回空（绝不硬编码猜测模型名）
            var geminiPicked = Services.AgQuotaService.ResolveGeminiModels(new List<string>
            {
                "gemini-3.5-pro", "gemini-3.5-flash", "gemini-2.5-pro", "claude-sonnet-4-6"
            });
            sb.AppendLine("ag_gemini_pick=" + string.Join(",", geminiPicked));
            sb.AppendLine("ag_gemini_empty=" + Services.AgQuotaService.ResolveGeminiModels(new List<string>()).Count);
            sb.AppendLine("ag_claude_empty=" + Services.AgQuotaService.ResolveClaudeModels(new List<string>()).Count);

            // 2) SSE 候选判定：合法 candidates → true；含 text 字段的错误 JSON → false
            sb.AppendLine("ag_sse_valid=" + Services.AgQuotaService.IsValidCandidateData(
                "{\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"hi\"}]}}]}"));
            sb.AppendLine("ag_sse_error_json=" + Services.AgQuotaService.IsValidCandidateData(
                "{\"error\":{\"message\":\"text field mentioned but failed\"}}"));

            // 3) 三态徽章文本：已激活 / 用尽倒计时 / 待激活
            var pAct = new Models.Profile { IsActivated = true, ActivationLatencyMs = 850 };
            sb.AppendLine("ag_status_activated=" + pAct.ActivationStatusText);
            var pExh = new Models.Profile { IsActivated = false, QuotaExhaustedResetAt = DateTime.UtcNow.AddMinutes(95) };
            sb.AppendLine("ag_status_exhausted=" + pExh.ActivationStatusText);
            var pNone = new Models.Profile();
            sb.AppendLine("ag_status_pending=" + pNone.ActivationStatusText);

            // 3b) QuotaExhausted 标志语义：未来重置→倒计时；已过重置→待激活（非“失败”）
            var pExhFuture = new Models.Profile { IsActivated = false, QuotaExhausted = true, QuotaExhaustedResetAt = DateTime.UtcNow.AddHours(2) };
            sb.AppendLine("ag_exhausted_future=" + pExhFuture.ActivationStatusText);
            var pExhPast = new Models.Profile { IsActivated = false, QuotaExhausted = true, QuotaExhaustedResetAt = DateTime.UtcNow.AddHours(-1) };
            sb.AppendLine("ag_exhausted_past=" + pExhPast.ActivationStatusText);
            var pRealFail = new Models.Profile { IsActivated = false, ActivationError = "token" };
            sb.AppendLine("ag_real_fail=" + pRealFail.ActivationStatusText);

            // 3c) 关键回归：配额已恢复但 429 遗留标记未清时，不得再显示倒计时
            var pStale = new Models.Profile
            {
                IsActivated = false,
                QuotaExhausted = true,
                QuotaExhaustedResetAt = DateTime.UtcNow.AddHours(3),
                Quota5hFraction = 0.85,
                QuotaWeeklyFraction = 0.62,
                Quota5hResetTime = DateTime.UtcNow.AddHours(2).ToString("yyyy-MM-ddTHH:mm:ssZ"),
            };
            sb.AppendLine("ag_stale_no_countdown=" + pStale.ActivationStatusText);
            // 3d) 仍处用尽且重置时间在未来 → 显示倒计时（含天级格式）
            var pTrueExh = new Models.Profile
            {
                IsActivated = false,
                Quota5hFraction = 0.01,
                QuotaWeeklyFraction = 0.004,
                Quota5hResetTime = DateTime.UtcNow.AddHours(2).ToString("yyyy-MM-ddTHH:mm:ssZ"),
                QuotaWeeklyResetTime = DateTime.UtcNow.AddDays(5).AddHours(22).ToString("yyyy-MM-ddTHH:mm:ssZ"),
            };
            sb.AppendLine("ag_true_exhausted=" + pTrueExh.ActivationStatusText);
            // 4) 批量激活按钮可见（曾误置 Collapsed 导致功能不可达）
            var mw2 = new MainWindow { Visibility = Visibility.Hidden };
            var batchBtn = mw2.FindName("BatchActivateBtn") as System.Windows.Controls.Control;
            sb.AppendLine("ag_batch_btn=" + (batchBtn != null && batchBtn.Visibility == Visibility.Visible ? "visible" : "hidden"));
            mw2.Close();
            // 4b) 最小化回归：点标题栏"-"必须最小化到任务栏（任务栏按钮可见、状态为 Minimized），
            //     而不是收进系统托盘。收托盘只属于"关闭窗口"路径。
            try
            {
                var minWin = new MainWindow { Visibility = Visibility.Hidden };
                minWin.MinimizeToTaskbar();
                sb.AppendLine("min_state=" + (minWin.WindowState == WindowState.Minimized ? "minimized" : "normal"));
                sb.AppendLine("min_taskbar=" + (minWin.ShowInTaskbar ? "shown" : "hidden"));
                sb.AppendLine("min_visible=" + (minWin.IsVisible ? "visible" : "hidden"));
                // 4c) 关闭到托盘回归：窗口与任务栏按钮都必须消失
                minWin.MinimizeToTray();
                sb.AppendLine("tray_min_taskbar=" + (minWin.ShowInTaskbar ? "shown" : "hidden"));
                sb.AppendLine("tray_min_visible=" + (minWin.IsVisible ? "visible" : "hidden"));
                sb.AppendLine("tray_min_state=" + (minWin.WindowState == WindowState.Minimized ? "minimized" : "normal"));
                minWin.Close();
            }
            catch (Exception ex)
            {
                sb.AppendLine("tray_probe_err=" + ex.Message);
            }
            // 4d) CLI 失败分类回归（用户截图中的真实报因）：
            //     exit=0 的 JSON 里带 error 必须判失败；"模型不被接受"与"参数用法错误"都要识别出来，
            //     否则不会降级参数、也不会换下一个可用模型。
            try
            {
                var json = "{\"conversation_id\":\"\",\"status\":\"ERROR\",\"response\":\"\",\"error\":\"invalid model selection (--model\\\"gemini-2.5-flash-lite\\\" --effort \\\"low\\\"): --effort is not supported\"}";
                var err = Services.AgCliService.ExtractJsonError(json);
                sb.AppendLine("cli_json_error=" + (err ?? "null"));
                sb.AppendLine("cli_is_model=" + Services.AgCliService.IsModelError(json));
                sb.AppendLine("cli_is_usage=" + Services.AgCliService.IsUsageError(0, json));
                // 成功输出不得被误判为错误
                sb.AppendLine("cli_json_ok_ignored=" + (Services.AgCliService.ExtractJsonError("{\"status\":\"OK\",\"response\":\"hi\"}") == null));
                // 配额类错误要能被识别（不该去换模型/降级参数）
                var quota = "{\"status\":\"ERROR\",\"error\":\"429 RESOURCE_EXHAUSTED: quota exceeded\"}";
                sb.AppendLine("cli_quota_error=" + (Services.AgCliService.ExtractJsonError(quota) ?? "null"));
            }
            catch (Exception ex)
            {
                sb.AppendLine("cli_probe_err=" + ex.Message);
            }
            // 4e) 模型排序回归：CLI 激活必须"最新优先"，
            //     老模型（gemini-2.5 等）多已被官方下线，先试它只会白白卡很久再失败。
            try
            {
                var ordered = Services.AgQuotaService.OrderByVersionDescending(new List<string>
                {
                    "gemini-2.5-flash-lite", "gemini-3.8-flash-low", "gemini-3-pro", "gemini-2.5-pro",
                });
                sb.AppendLine("cli_model_order=" + string.Join(",", ordered));
            }
            catch (Exception ex)
            {
                sb.AppendLine("cli_order_err=" + ex.Message);
            }
            // 4f) CLI 自报模型解析 + 最便宜挑选（用真实 CLI 输出做回归）：
            //     CLI 的模型名是带思考档位的显示名，和 Google API 的命名不是一套，
            // // 必须按 CLI 自报的列表选，否则永远 "model not recognized"。
            try
            {
                var raw = "error: invalid model selection (--model \"__x__\" --effort \"\"): model __x__ is not recognized as a known model or custom model in settings\n" +
                           "Available models:\n" +
                           "  Gemini 3.8 Flash (High)\n  Gemini 3.8 Flash (Medium)\n  Gemini 3.8 Flash (Low)\n" +
                           "  Gemini 3.7 Flash (High)\n  Gemini 3.7 Flash (Medium)\n  Gemini 3.7 Flash (Low)\n" +
                           "  Gemini 3.6 Flash (High)\n  Gemini 3.6 Flash (Medium)\n  Gemini 3.6 Flash (Low)\n" +
                           "  Gemini 3.1 Pro (High)\n  Gemini 3.1 Pro (Low)\n" +
                           "  Claude Sonnet 4.6 (Thinking)\n  Claude Opus 4.6 (Thinking)\n  GPT-OSS 120B (Medium)";
                var parsed = Services.AgCliService.ParseAvailableModels(raw);
                sb.AppendLine("cli_parsed_count=" + parsed.Count);
                sb.AppendLine("cli_parsed_first=" + (parsed.Count > 0 ? parsed[0] : "none"));
                var gem = Services.AgCliService.PickCheapest(parsed, geminiPool: true);
                var tp = Services.AgCliService.PickCheapest(parsed, geminiPool: false);
                sb.AppendLine("cli_pick_gemini=" + (gem.Count > 0 ? gem[0] : "none"));
                sb.AppendLine("cli_pick_thirdparty=" + (tp.Count > 0 ? tp[0] : "none"));
                // 没有该章节时不得凭空造模型
                sb.AppendLine("cli_parse_empty_ok=" + (Services.AgCliService.ParseAvailableModels("some other error").Count == 0));
            }
            catch (Exception ex)
            {
                sb.AppendLine("cli_parse_err=" + ex.Message);
            }
            // 4g) 忙碌遮罩不得禁用整个窗口：
            //     窗口一旦 IsEnabled=false，点击它只会激活窗口而不派发点击，
            //     且部分自定义按钮样式没有禁用态视觉反馈，界面看着正常却点不动。
            try
            {
                var busyWin = new MainWindow { Visibility = Visibility.Hidden };
                busyWin.SetBusy("selftest");
                sb.AppendLine("busy_window_enabled=" + (busyWin.IsEnabled ? "yes" : "no"));
                sb.AppendLine("busy_overlay=" + (busyWin.BusyOverlay.Visibility == Visibility.Visible ? "visible" : "hidden"));
                busyWin.ClearBusy();
                sb.AppendLine("busy_after_clear=" + (busyWin.BusyOverlay.Visibility == Visibility.Visible ? "visible" : "hidden"));
                busyWin.Close();
            }
            catch (Exception ex)
            {
                sb.AppendLine("busy_probe_err=" + ex.Message);
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine("ag_activation_err=" + ex.Message);
        }
        // ProviderDialog 各模式的默认模型区可见性断言（防回归）
        try
        {
            foreach (var mode in new Dialogs.ProviderDialogMode[]
            {
                Dialogs.ProviderDialogMode.Pi,
                Dialogs.ProviderDialogMode.OpenCode,
                Dialogs.ProviderDialogMode.Codex,
                Dialogs.ProviderDialogMode.Claude,
            })
            {
                var dlg = new Dialogs.ProviderDialog(mode, null);
                var panel = dlg.FindName("DirectModelPanel");
                var combo = dlg.FindName("ModelCombo");
                var vis = (panel is System.Windows.UIElement el && el.Visibility == Visibility.Visible) ? "visible" : "hidden";
                sb.AppendLine("pd_" + mode + "_default_model_panel=" + vis);
                sb.AppendLine("pd_" + mode + "_model_combo=" + (combo != null ? "yes" : "no"));
                dlg.Close();
            }
            // 4h) 批量激活取消按钮：默认隐藏（不占工具栏），点击后由代码显出
            try
            {
                var mwC = new MainWindow { Visibility = Visibility.Hidden };
                var cancelBtn = mwC.FindName("BusyCancelBtn") as System.Windows.Controls.Control;
                sb.AppendLine("batch_cancel_exists=" + (cancelBtn != null));
                sb.AppendLine("batch_cancel_hidden_by_default=" + (cancelBtn != null && cancelBtn.Visibility != Visibility.Visible));
                // 关键回归：取消按钮必须位于忙碌遮罩内部。遮罩盖住整个窗口，
                // 放在工具栏上会被遮罩挡住点不到（用户实际遇到的问题）。
                var inOverlay = false;
                for (System.Windows.DependencyObject? d = cancelBtn; d != null; d = System.Windows.Media.VisualTreeHelper.GetParent(d))
                {
                    if (ReferenceEquals(d, mwC.BusyOverlay)) { inOverlay = true; break; }
                }
                sb.AppendLine("batch_cancel_inside_overlay=" + inOverlay);
                mwC.Close();
            }
            catch (Exception ex)
            {
                sb.AppendLine("batch_cancel_err=" + ex.Message);
            }

            sb.AppendLine("pd_dialog_probe=ok");

            // Bilingual release notes probe
            try
            {
                var testBody = "## 简体中文\r\n- 中文日志条目\r\n## English\r\n- English log entry";
                var (zhParsed, enParsed) = ReleaseNotesHelper.ParseBilingual(testBody);
                var zhOk = zhParsed.Contains("中文日志条目");
                var enOk = enParsed.Contains("English log entry");

                var localZh = ReleaseNotesHelper.GetLocalReleaseNotes("0.3.2", "zh-CN");
                var localEn = ReleaseNotesHelper.GetLocalReleaseNotes("0.3.2", "en-US");
                var localZhOk = !string.IsNullOrEmpty(localZh);
                var localEnOk = !string.IsNullOrEmpty(localEn);

                var uInfo = new UpdateInfo
                {
                    ReleaseNotesZh = zhParsed,
                    ReleaseNotesEn = enParsed
                };
                var uZh = uInfo.GetLocalizedReleaseNotes("zh-CN");
                var uEn = uInfo.GetLocalizedReleaseNotes("en-US");

                var commentBody = "## 简体中文\r\n<!-- lang:zh -->\r\n中文更新条目\r\n<!-- /lang:zh -->\r\n## English\r\n<!-- lang:en -->\r\nEnglish update item\r\n<!-- /lang:en -->";
                var (cZh, cEn) = ReleaseNotesHelper.ParseBilingual(commentBody, "9.9.9");
                var commentTagsOk = cZh.Contains("中文更新条目") && !cZh.Contains("English update") && cEn.Contains("English update item");

                var navEnglishOnly = "[English](RELEASE_NOTES.md) | [简体中文](RELEASE_NOTES_zh.md)\r\n# APISwitch Release Notes\r\n## APISwitch v9.9.9 Release Notes\r\n• Pure English entry";
                var (nZh, nEn) = ReleaseNotesHelper.ParseBilingual(navEnglishOnly, "9.9.9");
                var navBypassOk = string.IsNullOrEmpty(nZh) && nEn.Contains("Pure English entry");

                sb.AppendLine($"bilingual_probe=ok;zhParsed={zhOk};enParsed={enOk};localZh={localZhOk};localEn={localEnOk};uZh={!string.IsNullOrEmpty(uZh)};uEn={!string.IsNullOrEmpty(uEn)};commentTagsOk={commentTagsOk};navBypassOk={navBypassOk}");
            }
            catch (Exception ex)
            {
                sb.AppendLine("bilingual_probe_err=" + ex.Message);
            }
            // Anti-flapping and quota merging regression probe
            try
            {
                var pPaid = new Models.Profile
                {
                    Email = "test-paid@example.com",
                    SubscriptionTier = "Google AI Pro",
                    Plan = "Google AI Pro",
                    IsPaidSubscription = true,
                    QuotaWeeklyFraction = 0.8,
                    QuotaArgonWeeklyFraction = 0.45
                };

                // 1) 2-group JSON (Gemini + 3P, no Argon) should NOT demote IsPaidSubscription or wipe Argon quota
                var json2Group = "{\"groups\":[{\"displayName\":\"Gemini Models\",\"buckets\":[{\"bucketId\":\"gemini-weekly\",\"window\":\"weekly\",\"remainingFraction\":0.75}]},{\"displayName\":\"Claude and GPT models\",\"buckets\":[{\"bucketId\":\"3p-weekly\",\"window\":\"weekly\",\"remainingFraction\":0.99}]}]}";
                AgQuotaService.ParseQuotaSummary(pPaid, json2Group);

                var demotePrevented = pPaid.IsPaidSubscription == true &&
                                      pPaid.QuotaArgonWeeklyFraction == 0.45 &&
                                      pPaid.QuotaWeeklyFraction == 0.75 &&
                                      pPaid.IsPaidPro && !pPaid.IsPromoPro;

                // 2) 3-group JSON (includes Argon) should update Argon and maintain Paid Pro
                var json3Group = "{\"groups\":[{\"displayName\":\"Gemini 4 Argon\",\"buckets\":[{\"bucketId\":\"gemini-argon-wtu-weekly\",\"window\":\"weekly\",\"remainingFraction\":0.40}]},{\"displayName\":\"Gemini Models\",\"buckets\":[{\"bucketId\":\"gemini-weekly\",\"window\":\"weekly\",\"remainingFraction\":0.70}]}]}";
                AgQuotaService.ParseQuotaSummary(pPaid, json3Group);

                var argonUpdated = pPaid.IsPaidSubscription == true &&
                                   pPaid.QuotaArgonWeeklyFraction == 0.40 &&
                                   pPaid.QuotaWeeklyFraction == 0.70 &&
                                   pPaid.IsPaidPro;

                // 3) Promo Pro profile without Argon should remain IsPromoPro
                var pPromo = new Models.Profile
                {
                    Email = "test-promo@example.com",
                    SubscriptionTier = "Google AI Pro",
                    Plan = "Google AI Pro",
                    IsPaidSubscription = false
                };
                AgQuotaService.ParseQuotaSummary(pPromo, json2Group);
                var promoOk = pPromo.IsPromoPro && !pPromo.IsPaidPro;

                // 4) Starter 纯免费账号：仅有常规 Gemini 周配额，无任何 3P、5H、Argon
                // 即使 loadCodeAssist 曾被 paidTier 赋为 Google AI Pro，在此必须精准校准为 Starter 免费版，杜绝被误标为 Pro 体验版
                var pStarter = new Models.Profile
                {
                    Email = "test-starter@example.com",
                    SubscriptionTier = "Google AI Pro",
                    Plan = "Google AI Pro"
                };
                var jsonStarter = "{\"groups\":[{\"displayName\":\"Gemini Models\",\"buckets\":[{\"bucketId\":\"gemini-weekly\",\"window\":\"weekly\",\"remainingFraction\":0.60}]}]}";
                AgQuotaService.ParseQuotaSummary(pStarter, jsonStarter);
                var starterOk = !pStarter.IsProTier &&
                                !pStarter.IsPromoPro &&
                                !pStarter.IsPaidPro &&
                                pStarter.SubscriptionTier == "Starter" &&
                                pStarter.TierDisplay == I18nService.T("Ag.TierStarterFree");

                // 5) Google AI Plus 账号：loadCodeAssist 返回 g1-plus-tier / "Antigravity Starter Quota"，
                // 且 retrieveUserQuotaSummary 同时返回 gemini-weekly + 3p-weekly（无 5h / 无 Argon），曾误升 Pro 或归为 Starter，并残留 Argon 0%
                var pPlus3p = new Models.Profile
                {
                    Email = "test-plus-3p@example.com",
                    SubscriptionTier = "Google AI Pro",
                    Plan = "Google AI Pro",
                    QuotaArgonWeeklyFraction = 0.0,
                    Quota5hFraction = 1.0
                };
                AgQuotaService.ParseTier(pPlus3p, "{\"paidTier\":{\"id\":\"g1-plus-tier\",\"name\":\"Antigravity Starter Quota\",\"description\":\"Antigravity Starter Quota\"}}");
                var jsonStarterWith3p = "{\"groups\":[{\"displayName\":\"Gemini Models\",\"buckets\":[{\"bucketId\":\"gemini-weekly\",\"window\":\"weekly\",\"remainingFraction\":1.0}]},{\"displayName\":\"Claude and GPT models\",\"buckets\":[{\"bucketId\":\"3p-weekly\",\"window\":\"weekly\",\"remainingFraction\":0.95}]}]}";
                AgQuotaService.ParseQuotaSummary(pPlus3p, jsonStarterWith3p, out var starterHasArgon);
                AgQuotaService.ReconcileQuotaWithModels(pPlus3p, new List<Models.AgModelQuotaInfo>
                {
                    new() { ModelId = "gemini-4-argon-disabled", Disabled = true }
                }, quotaGot: true, hasArgonGroupFound: starterHasArgon);
                var plus3pOk = pPlus3p.SubscriptionTier == "Google AI Plus" &&
                               pPlus3p.IsPlus &&
                               !pPlus3p.IsProTier &&
                               !pPlus3p.IsPaidPro &&
                               pPlus3p.TierDisplay == I18nService.T("Ag.TierPlus") &&
                               !pPlus3p.QuotaArgonWeeklyFraction.HasValue &&
                               !pPlus3p.Quota5hFraction.HasValue &&
                               Math.Abs((pPlus3p.Quota3pWeeklyFraction ?? 0) - 0.95) < 1e-6;

                // 6) 同 ResetTime 窗口下，fetchAvailableModels 返回更低的实时剩余百分比时应主动校准汇总桶
                var pRec = new Models.Profile
                {
                    Email = "test-reconcile@example.com",
                    SubscriptionTier = "Google AI Pro",
                    QuotaArgonWeeklyFraction = 0.99,
                    QuotaArgonWeeklyResetTime = "2026-11-30T01:40:12Z",
                    Quota3pWeeklyFraction = 0.93,
                    Quota3pWeeklyResetTime = "2026-11-25T01:34:28Z"
                };
                AgQuotaService.ReconcileQuotaWithModels(pRec, new List<Models.AgModelQuotaInfo>
                {
                    new() { ModelId = "gemini-4-argon-medium", RemainingFraction = 0.918, ResetTime = "2026-11-30T01:40:12Z" },
                    new() { ModelId = "claude-opus-5-5-medium", RemainingFraction = 1.0, ResetTime = "2026-11-23T10:54:20Z" }
                }, quotaGot: true, hasArgonGroupFound: true);
                var reconcileOk = Math.Abs((pRec.QuotaArgonWeeklyFraction ?? 0) - 0.918) < 1e-6 &&
                                  Math.Abs((pRec.Quota3pWeeklyFraction ?? 0) - 0.93) < 1e-6;

                // 7) 满额且尚未开始计时的占位 resetTime（5H=+300.5m / Weekly=+10080.5m）应显示「未开始计时」而非剩余倒计时
                var nowUtc = DateTime.UtcNow;
                var pStandby = new Models.Profile
                {
                    Email = "test-standby@example.com",
                    SubscriptionTier = "Google AI Pro",
                    IsPaidSubscription = true,
                    QuotaUpdatedAt = nowUtc,
                    Quota5hFraction = 1.0,
                    Quota5hResetTime = nowUtc.AddMinutes(300.5).ToString("O"),
                    QuotaWeeklyFraction = 1.0,
                    QuotaWeeklyResetTime = nowUtc.AddMinutes(10080.5).ToString("O"),
                    Quota3p5hFraction = 0.96,
                    Quota3p5hResetTime = nowUtc.AddMinutes(279).ToString("O"),
                    Quota3pWeeklyFraction = 0.01,
                    Quota3pWeeklyResetTime = nowUtc.AddHours(20).ToString("O")
                };
                var expectedIdle = I18nService.T("Ag.TimerNotStarted");
                var expectedExhaustedSample = I18nService.F("Ag.TimerExhaustedTipFmt", "SPLIT_TOKEN");
                var exhaustedPrefix = expectedExhaustedSample.Split("SPLIT_TOKEN")[0];
                var standbyTimerOk = pStandby.Quota5hCountdown == expectedIdle &&
                                     pStandby.QuotaWeeklyCountdown == expectedIdle &&
                                     pStandby.Quota5hCountdownFg.IsFrozen &&
                                     pStandby.Quota5hCountdownBg.IsFrozen &&
                                     pStandby.Quota3p5hCountdown.Contains("4") &&
                                     !string.IsNullOrEmpty(pStandby.Quota3p5hCountdownTooltip) &&
                                     (pStandby.Quota3pWeeklyCountdownTooltip?.Contains(exhaustedPrefix) == true);

                // 8) 免费新号（free-tier）：loadCodeAssist 返回 free-tier/Antigravity Starter Quota，
                // 即使 retrieveUserQuotaSummary 返回 3p-5h + 3p-weekly（无 Gemini 组，由 fetchAvailableModels 补全 Gemini 周配额），
                // 也必须保持为 Starter 免费版，绝不可因 3p-5h 误判为 Google AI Pro 体验版
                var pFree3p5h = new Models.Profile
                {
                    Email = "test-free-3p5h@example.com",
                    SubscriptionTier = "Google AI Pro",
                    Plan = "Google AI Pro",
                    IsPaidSubscription = false
                };
                AgQuotaService.ParseTier(pFree3p5h, "{\"currentTier\":{\"id\":\"free-tier\",\"name\":\"Antigravity\"},\"paidTier\":{\"id\":\"free-tier\",\"name\":\"Antigravity Starter Quota\",\"description\":\"Antigravity Starter Quota\"}}");
                var jsonFree3p5h = "{\"groups\":[{\"displayName\":\"Claude and GPT models\",\"buckets\":[{\"bucketId\":\"3p-weekly\",\"window\":\"weekly\",\"remainingFraction\":1.0},{\"bucketId\":\"3p-5h\",\"window\":\"5h\",\"remainingFraction\":1.0}]}]}";
                AgQuotaService.ParseQuotaSummary(pFree3p5h, jsonFree3p5h, out var freeHasArgon);
                AgQuotaService.ReconcileQuotaWithModels(pFree3p5h, new List<Models.AgModelQuotaInfo>
                {
                    new() { ModelId = "gemini-4-hydrogen-medium", RemainingFraction = 1.0, ResetTime = nowUtc.AddDays(7).ToString("O") },
                    new() { ModelId = "gemini-4-argon-disabled", Disabled = true }
                }, quotaGot: true, hasArgonGroupFound: freeHasArgon);
                pFree3p5h.QuotaUpdatedAt = nowUtc;
                var freeWith3p5hOk = pFree3p5h.SubscriptionTier == "Starter" &&
                                     !pFree3p5h.IsProTier &&
                                     !pFree3p5h.IsPromoPro &&
                                     !pFree3p5h.IsPaidPro &&
                                     !pFree3p5h.IsPlus &&
                                     pFree3p5h.TierDisplay == I18nService.T("Ag.TierStarterFree") &&
                                     pFree3p5h.QuotaWeeklyFraction == 1.0 &&
                                     pFree3p5h.Quota3p5hPercentText == "100%" &&
                                     pFree3p5h.Quota3p5hCountdown == expectedIdle &&
                                     !pFree3p5h.Quota5hFraction.HasValue &&
                                     pFree3p5h.Quota5hCountdown == I18nService.T("Ag.No5hLimit");

                sb.AppendLine($"ag_tier_flapping_probe=ok;demotePrevented={demotePrevented};argonUpdated={argonUpdated};promoOk={promoOk};starterOk={starterOk};plus3pOk={plus3pOk};reconcileOk={reconcileOk};standbyTimerOk={standbyTimerOk};freeWith3p5hOk={freeWith3p5hOk}");
            }
            catch (Exception ex)
            {
                sb.AppendLine("ag_tier_flapping_probe_err=" + ex.Message);
            }

            // Claude 安全关闭与精准进程识别探测
            try
            {
                var desktopMsixOk = ClaudeProcess.IsClaudeDesktopPath(@"C:\Program Files\WindowsApps\Claude_2.42304.2.0_x64__pzs8sxrjxfjjc\Claude.exe");
                var desktopWin32Ok = ClaudeProcess.IsClaudeDesktopPath(@"C:\Users\test\AppData\Local\AnthropicClaude\Claude.exe");
                var cliNotDesktop = !ClaudeProcess.IsClaudeDesktopPath(@"C:\Users\test\.local\bin\claude.exe");

                var cliExeOk = ClaudeCliProcess.IsClaudeCliRootProcess(new ClaudeCliProcess.ProcessItem
                {
                    Name = "claude.exe",
                    ExePath = @"C:\Users\test\.local\bin\claude.exe",
                    CommandLine = "claude"
                });
                var desktopNotCli = !ClaudeCliProcess.IsClaudeCliRootProcess(new ClaudeCliProcess.ProcessItem
                {
                    Name = "claude.exe",
                    ExePath = @"C:\Program Files\WindowsApps\Claude_2.42304.2.0_x64__pzs8sxrjxfjjc\Claude.exe",
                    CommandLine = "claude.exe"
                });
                var cliNodeOk = ClaudeCliProcess.IsClaudeCliRootProcess(new ClaudeCliProcess.ProcessItem
                {
                    Name = "node.exe",
                    ExePath = @"C:\Program Files\nodejs\node.exe",
                    CommandLine = "node C:\\Users\\test\\node_modules\\@anthropic-ai\\claude-code\\bin\\claude.js"
                });
                var regularNodeNotCli = !ClaudeCliProcess.IsClaudeCliRootProcess(new ClaudeCliProcess.ProcessItem
                {
                    Name = "node.exe",
                    ExePath = @"C:\Program Files\nodejs\node.exe",
                    CommandLine = "node server.js"
                });

                var i18nCliBtn = I18nService.T("Claude.StopProcessBtn");
                var i18nDesktopBtn = I18nService.T("Desktop.StopProcessBtn");
                var i18nOk = !string.IsNullOrWhiteSpace(i18nCliBtn) && !string.IsNullOrWhiteSpace(i18nDesktopBtn);

                var snapshot = ClaudeCliProcess.GetProcessSnapshot();
                var snapshotOk = snapshot != null;

                sb.AppendLine($"claude_safe_stop_probe=ok;desktopPathOk={desktopMsixOk && desktopWin32Ok && cliNotDesktop};cliMatchOk={cliExeOk && desktopNotCli};cliNodeOk={cliNodeOk && regularNodeNotCli};i18nOk={i18nOk};snapshotOk={snapshotOk}");
            }
            catch (Exception ex)
            {
                sb.AppendLine("claude_safe_stop_probe_err=" + ex.Message);
            }

            // 用量统计精准校准与去重探针（完全在沙盒 APISWITCH_HOME + APISWITCH_APPDATA 内运行，零污染真实库）
            var prevAppData = Environment.GetEnvironmentVariable("APISWITCH_APPDATA");
            try
            {
                var usageAppData = Path.Combine(sandbox, "appdata");
                Directory.CreateDirectory(usageAppData);
                Environment.SetEnvironmentVariable("APISWITCH_APPDATA", usageAppData);
                UsageScanner.Reset();

                // 1) source_id 幂等合并：代理先记 + 扫描后补，合并为 1 条并保留高优字段
                var now = DateTime.Now;
                UsageStorage.RecordUsage(UsageStorage.AgentCodex, "relay-1", "MyRelay", "gpt-test",
                    100, 20, now, cacheReadTokens: 0, cacheWriteTokens: 0, durationMs: 2000, ttftMs: 350, sourceId: "codex:resp_merge_1");
                UsageStorage.RecordUsage(UsageStorage.AgentCodex, "custom", "Codex", "",
                    120, 30, now, cacheReadTokens: 80, cacheWriteTokens: 0, durationMs: 2100, ttftMs: 1800, sourceId: "codex:resp_merge_1");

                // 2) Claude Code 多内容块 + 跨文件去重 + 缓存计入 + stats-cache 补历史 + 忽略 synthetic
                var claudeProj = Path.Combine(home, ".claude", "projects", "proj-a");
                Directory.CreateDirectory(claudeProj);
                var tsStr = now.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
                var claudeLines = string.Join("\n", new[]
                {
                    "{\"type\":\"assistant\",\"timestamp\":\"" + tsStr + "\",\"message\":{\"id\":\"msg_blk_1\",\"model\":\"claude-opus-5\",\"usage\":{\"input_tokens\":26,\"cache_read_input_tokens\":98000,\"cache_creation_input_tokens\":2000,\"output_tokens\":100,\"inference_geo\":\"global\"}}}",
                    "{\"type\":\"assistant\",\"timestamp\":\"" + tsStr + "\",\"message\":{\"id\":\"msg_blk_1\",\"model\":\"claude-opus-5\",\"usage\":{\"input_tokens\":26,\"cache_read_input_tokens\":98000,\"cache_creation_input_tokens\":2000,\"output_tokens\":500,\"inference_geo\":\"global\"}}}",
                    "{\"type\":\"assistant\",\"timestamp\":\"" + tsStr + "\",\"message\":{\"id\":\"msg_blk_1\",\"model\":\"claude-opus-5\",\"usage\":{\"input_tokens\":26,\"cache_read_input_tokens\":98000,\"cache_creation_input_tokens\":2000,\"output_tokens\":500,\"inference_geo\":\"global\"}}}",
                    "{\"type\":\"assistant\",\"timestamp\":\"" + tsStr + "\",\"message\":{\"id\":\"msg_syn\",\"model\":\"<synthetic>\",\"usage\":{\"input_tokens\":10,\"output_tokens\":10}}}"
                });
                File.WriteAllText(Path.Combine(claudeProj, "sess1.jsonl"), claudeLines);
                File.WriteAllText(Path.Combine(claudeProj, "sess2.jsonl"), claudeLines);

                var todayStr = now.ToString("yyyy-MM-dd");
                File.WriteAllText(Path.Combine(home, ".claude", "stats-cache.json"),
                    "{\"version\":4,\"dailyModelTokens\":[" +
                    "{\"date\":\"" + todayStr + "\",\"tokensByModel\":{\"claude-opus-5\":999999}}," +
                    "{\"date\":\"2026-06-15\",\"tokensByModel\":{\"claude-opus-4-7\":1000}}" +
                    "],\"modelUsage\":{\"claude-opus-4-7\":{\"inputTokens\":100,\"outputTokens\":900,\"cacheReadInputTokens\":9000,\"cacheCreationInputTokens\":1000}}}");

                // 3) Pi 缓存总输入对齐 totalTokens - outTok
                var piDir = Path.Combine(home, ".pi", "agent", "sessions", "p1");
                Directory.CreateDirectory(piDir);
                File.WriteAllText(Path.Combine(piDir, "s1.jsonl"),
                    "{\"type\":\"message\",\"id\":\"pi_1\",\"timestamp\":\"" + tsStr + "\",\"message\":{\"role\":\"assistant\",\"model\":\"kimi-k3\",\"provider\":\"StepFun\",\"usage\":{\"input\":1000,\"output\":200,\"reasoning\":50,\"cacheRead\":4000,\"cacheWrite\":500,\"totalTokens\":5700}}}");

                // 4) OpenCode 缓存 + 推理输出 + 历史建库
                var ocDir = Path.Combine(home, ".local", "share", "opencode");
                Directory.CreateDirectory(ocDir);
                var ocDbPath = Path.Combine(ocDir, "opencode.db");
                using (var ocConn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={ocDbPath}"))
                {
                    ocConn.Open();
                    using var c = ocConn.CreateCommand();
                    c.CommandText = "CREATE TABLE message (id TEXT PRIMARY KEY, session_id TEXT, time_created INTEGER, data TEXT);" +
                        "INSERT INTO message VALUES ('oc_1', 'ses1', " + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + ", " +
                        "'{\"role\":\"assistant\",\"modelID\":\"ds-v4\",\"providerID\":\"opencode\",\"tokens\":{\"total\":10140,\"input\":800,\"output\":100,\"reasoning\":40,\"cache\":{\"read\":9000,\"write\":200}}}');";
                    c.ExecuteNonQuery();
                }

                // 5) Codex archived .jsonl.zst 解压 + session_meta / turn_context 解析
                var codexArch = Path.Combine(home, ".codex", "archived_sessions");
                Directory.CreateDirectory(codexArch);
                var codexJsonl = string.Join("\n", new[]
                {
                    "{\"timestamp\":\"" + tsStr + "\",\"type\":\"session_meta\",\"payload\":{\"id\":\"thr_1\",\"model_provider\":\"myprov\"}}",
                    "{\"timestamp\":\"" + tsStr + "\",\"type\":\"turn_context\",\"payload\":{\"model\":\"gpt-6-test\"}}",
                    "{\"timestamp\":\"" + tsStr + "\",\"type\":\"token_usage_record\",\"payload\":{\"response_id\":\"resp_zst_1\",\"usage\":{\"input_tokens\":5000,\"cached_input_tokens\":4000,\"cache_write_input_tokens\":0,\"output_tokens\":150}}}"
                });
                using (var fsZ = File.Create(Path.Combine(codexArch, "rollout-arch.jsonl.zst")))
                using (var csZ = new ZstdSharp.CompressionStream(fsZ))
                using (var swZ = new StreamWriter(csZ))
                {
                    swZ.Write(codexJsonl);
                }

                // 6) Gemini CLI .jsonl 同 ID 去重
                var gemDir = Path.Combine(home, ".gemini", "tmp", "p1", "chats");
                Directory.CreateDirectory(gemDir);
                var gemLines = string.Join("\n", new[]
                {
                    "{\"id\":\"gem_1\",\"timestamp\":\"" + tsStr + "\",\"type\":\"gemini\",\"model\":\"gemini-3-flash\",\"tokens\":{\"input\":12000,\"output\":80,\"cached\":8000,\"thoughts\":20,\"total\":12100}}",
                    "{\"id\":\"gem_1\",\"timestamp\":\"" + tsStr + "\",\"type\":\"gemini\",\"model\":\"gemini-3-flash\",\"tokens\":{\"input\":12000,\"output\":80,\"cached\":8000,\"thoughts\":20,\"total\":12100}}"
                });
                File.WriteAllText(Path.Combine(gemDir, "session-2026-11-16T10-00-gem1.jsonl"), gemLines);

                // 7) DSH 新版 session.v4.jsonl.zstd（含 assistant/message + assistant/attempt）与 session_projcache/sessions/*.json
                var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var dshSessDir = Path.Combine(home, ".dsh", "sessions", "--proj--", "session-dsh-v4");
                Directory.CreateDirectory(dshSessDir);
                var dshProjSessionsDir = Path.Combine(home, ".dsh", "storages", "session_projcache", "sessions");
                Directory.CreateDirectory(dshProjSessionsDir);
                var dshLines = string.Join("\n", new[]
                {
                    "{\"type\":\"session\",\"version\":4,\"id\":\"session-dsh-v4\",\"createdAt\":" + (nowMs - 5000) + "}",
                    "{\"type\":\"step/start\",\"seq\":1,\"time\":" + (nowMs - 4000) + ",\"data\":{\"turn\":1,\"step\":1}}",
                    "{\"type\":\"assistant/message\",\"seq\":2,\"time\":" + (nowMs - 1000) + ",\"data\":{\"turn\":1,\"step\":1,\"usage\":{\"inputTokens\":1000,\"outputTokens\":200,\"cacheReadTokens\":3000},\"stream\":[{\"type\":\"chunk\",\"time\":" + (nowMs - 3000) + ",\"chunk\":{\"type\":\"block-start\",\"index\":0}},{\"type\":\"chunk\",\"time\":" + (nowMs - 1000) + ",\"chunk\":{\"type\":\"finish\",\"replayState\":{\"response\":{\"provider\":\"tianyou\",\"model\":\"deepseek-v4.1-flash\"}}}}]}}",
                    "{\"type\":\"assistant/attempt\",\"seq\":3,\"time\":" + (nowMs - 500) + ",\"data\":{\"turn\":1,\"step\":2,\"stream\":[{\"type\":\"chunk\",\"time\":" + (nowMs - 500) + ",\"chunk\":{\"type\":\"usage\",\"usage\":{\"inputTokens\":500,\"outputTokens\":50}}}]}}"
                });
                using (var fsD = File.Create(Path.Combine(dshSessDir, "session.v4.jsonl.zstd")))
                using (var csD = new ZstdSharp.CompressionStream(fsD))
                using (var swD = new StreamWriter(csD))
                {
                    swD.Write(dshLines);
                }
                // 同会话即使同时存在 session_projcache/sessions/*.json 也不得重复计数
                File.WriteAllText(Path.Combine(dshProjSessionsDir, "session-dsh-v4.json"),
                    "{\"version\":7,\"record\":{\"rows\":{\"modelSelection\":{\"val\":{\"lastUsed\":{\"provider\":\"tianyou\",\"model\":\"deepseek-v4.1-flash\"}}},\"sessionListMetadata\":{\"val\":{\"lastPromptAt\":" + nowMs + "}},\"tokenUsage\":{\"val\":{\"totals\":{\"uncachedInputTokens\":1500,\"outputTokens\":250,\"cacheReadTokens\":3000,\"cacheWriteTokens\":0},\"last\":{\"turn\":1,\"step\":2}}}}}}");
                // 仅存在于 session_projcache/sessions/*.json 的会话应由兜底通道补采
                File.WriteAllText(Path.Combine(dshProjSessionsDir, "session-dsh-fallback.json"),
                    "{\"version\":7,\"record\":{\"rows\":{\"modelSelection\":{\"val\":{\"lastUsed\":{\"provider\":\"liwan\",\"model\":\"glm-test\"}}},\"sessionListMetadata\":{\"val\":{\"lastPromptAt\":" + nowMs + "}},\"tokenUsage\":{\"val\":{\"totals\":{\"uncachedInputTokens\":400,\"outputTokens\":60,\"cacheReadTokens\":600,\"cacheWriteTokens\":0},\"last\":{\"turn\":2,\"step\":1}}}}}}");

                // 8) Antigravity brain/transcript.jsonl + transcript_full.jsonl 去重 + 台账模型映射/时间线夹逼补齐 + quota_snapshots 区间查询
                var agLogsDir = Path.Combine(home, ".gemini", "antigravity", "brain", "conv-ag-1", ".system_generated", "logs");
                Directory.CreateDirectory(agLogsDir);
                var tsAg0 = now.AddMinutes(-8).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ");
                var tsAgMid = now.AddMinutes(-5).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ");
                var tsAgMidDone = now.AddMinutes(-4).AddSeconds(-48).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ");
                var tsAg1 = now.AddMinutes(-2).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ");
                var tsAg1Next = now.AddMinutes(-1).AddSeconds(-52).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ");
                var agLines = string.Join("\n", new[]
                {
                    "{\"step_index\":1,\"type\":\"USER_INPUT\",\"status\":\"DONE\",\"created_at\":\"" + tsAg0 + "\"}",
                    "{\"step_index\":2,\"type\":\"PLANNER_RESPONSE\",\"status\":\"DONE\",\"created_at\":\"" + tsAg0 + "\",\"input_tokens\":2000,\"cache_read_tokens\":8000,\"output_tokens\":300}",
                    "{\"step_index\":3,\"type\":\"CHECKPOINT\",\"status\":\"DONE\",\"created_at\":\"" + tsAgMid + "\",\"input_tokens\":100,\"cache_read_tokens\":400,\"output_tokens\":50}",
                    "{\"step_index\":4,\"type\":\"PLANNER_RESPONSE\",\"status\":\"DONE\",\"created_at\":\"" + tsAg1 + "\",\"input_tokens\":500,\"cache_read_tokens\":1000,\"output_tokens\":120}",
                    "{\"step_index\":5,\"type\":\"GENERIC\",\"status\":\"DONE\",\"created_at\":\"" + tsAg1Next + "\"}"
                }) + "\n";
                File.WriteAllText(Path.Combine(agLogsDir, "transcript.jsonl"), agLines);
                File.WriteAllText(Path.Combine(agLogsDir, "transcript_full.jsonl"), agLines);
                UsageStorage.RecordQuotaSnapshot("ag@test.com", "gemini-5h", 0.72, now.AddHours(4));
                UsageStorage.RecordQuotaSnapshot("ag@test.com", "gemini-weekly", 0.91, now.AddDays(5));

                // 先跑一次 ScanAll()（此时无 pricing_ledger_v1.json，Antigravity 记录暂为空模型），
                // 随后写入 pricing_ledger_v1.json（有意只含 step2 与 step4，漏掉 step3）并跑第二次 ScanAll() 验证：
                // 1) 已有记录仅原地补齐模型（总请求数/Token 不重复累加）；
                // 2) 前后同模型时间线夹逼把漏记的 step3 也正确补齐为 gemini-4-hydrogen。
                UsageScanner.ScanAll();
                var ledgerJson = "{" +
                    "\"version\":\"1.0.0\"," +
                    "\"calls\":{" +
                    "\"c1\":{\"callId\":\"c1\",\"cascadeId\":\"conv-ag-1\",\"model\":\"Gemini 4 Hydrogen (XHigh)\",\"modelKey\":null,\"inputTokens\":2000,\"cacheReadTokens\":8000,\"outputTokens\":300,\"timestamp\":" + (nowMs - 480000) + "}," +
                    "\"c2\":{\"callId\":\"c2\",\"cascadeId\":\"conv-ag-1\",\"model\":\"Gemini 4 Hydrogen (Medium)\",\"modelKey\":null,\"inputTokens\":500,\"cacheReadTokens\":1000,\"outputTokens\":120,\"timestamp\":" + (nowMs - 120000) + "}" +
                    "}}";
                File.WriteAllText(Path.Combine(home, ".gemini", "antigravity", "pricing_ledger_v1.json"), ledgerJson);
                UsageScanner.ScanAll();
                // 8b) Antigravity conversations/<cid>.db（f21 显示名 / f20.model_enum / 跳过超大 f3）异步补齐验证
                var agDbConvLogs = Path.Combine(home, ".gemini", "antigravity", "brain", "conv-ag-db", ".system_generated", "logs");
                Directory.CreateDirectory(agDbConvLogs);
                File.WriteAllText(Path.Combine(agDbConvLogs, "transcript.jsonl"), string.Join("\n", new[]
                {
                    "{\"step_index\":2,\"type\":\"PLANNER_RESPONSE\",\"status\":\"DONE\",\"created_at\":\"" + tsAg0 + "\",\"input_tokens\":600,\"cache_read_tokens\":1400,\"output_tokens\":80}",
                    "{\"step_index\":4,\"type\":\"PLANNER_RESPONSE\",\"status\":\"DONE\",\"created_at\":\"" + tsAg1 + "\",\"input_tokens\":700,\"cache_read_tokens\":2300,\"output_tokens\":90}"
                }) + "\n");
                // 先扫一次（此时 conv-ag-db.db 尚未落盘，两条暂时为空模型）
                UsageScanner.ScanAll();

                byte[] PbFieldVarint(int fn, ulong v)
                {
                    using var ms = new MemoryStream();
                    void WriteVar(ulong x) { while (x >= 0x80) { ms.WriteByte((byte)((x & 0x7F) | 0x80)); x >>= 7; } ms.WriteByte((byte)x); }
                    WriteVar((ulong)((fn << 3) | 0));
                    WriteVar(v);
                    return ms.ToArray();
                }
                byte[] PbFieldBytes(int fn, byte[] payload)
                {
                    using var ms = new MemoryStream();
                    void WriteVar(ulong x) { while (x >= 0x80) { ms.WriteByte((byte)((x & 0x7F) | 0x80)); x >>= 7; } ms.WriteByte((byte)x); }
                    WriteVar((ulong)((fn << 3) | 2));
                    WriteVar((ulong)payload.Length);
                    ms.Write(payload, 0, payload.Length);
                    return ms.ToArray();
                }
                byte[] PbConcat(params byte[][] parts)
                {
                    using var ms = new MemoryStream();
                    foreach (var p in parts) ms.Write(p, 0, p.Length);
                    return ms.ToArray();
                }

                var agConvsDir = Path.Combine(home, ".gemini", "antigravity", "conversations");
                Directory.CreateDirectory(agConvsDir);
                var agDbPath = Path.Combine(agConvsDir, "conv-ag-db.db");
                using (var agConn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={agDbPath};Pooling=False"))
                {
                    agConn.Open();
                    using var c = agConn.CreateCommand();
                    c.CommandText = @"
PRAGMA journal_mode=WAL;
CREATE TABLE steps (idx integer PRIMARY KEY, step_type integer NOT NULL DEFAULT 0, metadata blob);
CREATE TABLE gen_metadata (idx integer PRIMARY KEY, data blob, size integer NOT NULL DEFAULT 0);";
                    c.ExecuteNonQuery();

                    void InsertStep(long sIdx, long gIdx)
                    {
                        var meta = PbFieldBytes(20, PbConcat(PbFieldVarint(2, (ulong)sIdx), PbFieldVarint(3, (ulong)gIdx)));
                        using var ic = agConn.CreateCommand();
                        ic.CommandText = "INSERT INTO steps(idx, step_type, metadata) VALUES ($i, 15, $m);";
                        ic.Parameters.AddWithValue("$i", sIdx);
                        ic.Parameters.AddWithValue("$m", meta);
                        ic.ExecuteNonQuery();
                    }
                    void InsertGen(long gIdx, string? enumId, string? f21, string? f19, int bigPayloadBytes)
                    {
                        var f1Parts = new List<byte[]>();
                        if (!string.IsNullOrEmpty(f19)) f1Parts.Add(PbFieldBytes(19, System.Text.Encoding.UTF8.GetBytes(f19)));
                        if (!string.IsNullOrEmpty(enumId))
                            f1Parts.Add(PbFieldBytes(20, PbConcat(
                                PbFieldBytes(1, "model_enum"u8.ToArray()),
                                PbFieldBytes(2, System.Text.Encoding.UTF8.GetBytes(enumId)))));
                        if (!string.IsNullOrEmpty(f21)) f1Parts.Add(PbFieldBytes(21, System.Text.Encoding.UTF8.GetBytes(f21)));
                        var topParts = new List<byte[]>();
                        if (bigPayloadBytes > 0) topParts.Add(PbFieldBytes(3, new byte[bigPayloadBytes]));
                        topParts.Add(PbFieldBytes(1, PbConcat(f1Parts.ToArray())));
                        var data = PbConcat(topParts.ToArray());
                        using var ig = agConn.CreateCommand();
                        ig.CommandText = "INSERT INTO gen_metadata(idx, data, size) VALUES ($i, $d, $sz);";
                        ig.Parameters.AddWithValue("$i", gIdx);
                        ig.Parameters.AddWithValue("$d", data);
                        ig.Parameters.AddWithValue("$sz", data.Length);
                        ig.ExecuteNonQuery();
                    }
                    InsertStep(2, 101);
                    InsertStep(4, 102);
                    // 101 有超大 f3 且 f19 为混淆 worker 串，必须优先按 f21 解析出 gemini-4-argon
                    InsertGen(101, "MODEL_PLACEHOLDER_M668", "Orchestrator (Argon)", "gemini-flash-worker-agy", 8192);
                    // 102 无 f21 也无 f19，仅靠 model_enum 解析出 gemini-3.8-flash
                    InsertGen(102, "MODEL_PLACEHOLDER_M318", null, null, 0);
                }

                // 重新写入刚才预埋的代理+扫描混合记录，同时驱动 conv-ag-db.db 事后延迟补齐
                UsageStorage.RecordUsage(UsageStorage.AgentCodex, "relay-1", "MyRelay", "gpt-test",
                    100, 20, now, cacheReadTokens: 0, cacheWriteTokens: 0, durationMs: 2000, ttftMs: 350, sourceId: "codex:resp_merge_1");
                UsageStorage.RecordUsage(UsageStorage.AgentCodex, "custom", "Codex", "",
                    120, 30, now, cacheReadTokens: 80, cacheWriteTokens: 0, durationMs: 2100, ttftMs: 1800, sourceId: "codex:resp_merge_1");
                UsageScanner.ScanAll();

                var allFrom = 0L;
                var allTo = DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeSeconds();
                var byAgent = UsageStorage.GetAgentTotals(allFrom, allTo).ToDictionary(x => x.Agent, x => x);

                bool codexOk = byAgent.TryGetValue(UsageStorage.AgentCodex, out var agCodex)
                    && agCodex.Requests == 2 && agCodex.InTokens == 5120 && agCodex.OutTokens == 180;
                bool claudeOk = byAgent.TryGetValue(UsageStorage.AgentClaudeCli, out var agClaude)
                    && agClaude.Requests == 2 && agClaude.InTokens == (100026 + 10100) && agClaude.OutTokens == (500 + 900);
                bool piOk = byAgent.TryGetValue(UsageStorage.AgentPi, out var agPi)
                    && agPi.Requests == 1 && agPi.InTokens == 5500 && agPi.OutTokens == 200;
                bool ocOk = byAgent.TryGetValue(UsageStorage.AgentOpenCode, out var agOc)
                    && agOc.Requests == 1 && agOc.InTokens == 10000 && agOc.OutTokens == 140;
                bool gemOk = byAgent.TryGetValue(UsageStorage.AgentGeminiCli, out var agGem)
                    && agGem.Requests == 1 && agGem.InTokens == 12000 && agGem.OutTokens == 100;
                bool dshOk = byAgent.TryGetValue(UsageStorage.AgentDsh, out var agDsh)
                    && agDsh.Requests == 3 && agDsh.InTokens == (4000 + 500 + 1000) && agDsh.OutTokens == 310;
                var qSnaps = UsageStorage.GetQuotaSnapshotsRange(allFrom, allTo);
                var agRecentList = UsageStorage.GetRecentEvents(allFrom, allTo, 50)
                    .Where(e => e.Agent == UsageStorage.AgentAntigravity)
                    .ToList();
                var agLedgerList = agRecentList.Where(e => e.Model == "gemini-4-hydrogen").ToList();
                var agStep4 = agLedgerList.FirstOrDefault(e => e.OutTokens == 120);
                var agStep2 = agLedgerList.FirstOrDefault(e => e.OutTokens == 300);
                bool agOk = byAgent.TryGetValue(UsageStorage.AgentAntigravity, out var agAg)
                    && agAg.Requests == 5 && agAg.InTokens == 17000 && agAg.OutTokens == 640
                    && agLedgerList.Count == 3
                    && agStep2 != null && agStep2.TtftMs > 0
                    && agStep4 != null && agStep4.DurationMs == 8000
                    && qSnaps.Count(q => q.AccountEmail == "ag@test.com") == 2;
                bool agDbOk = agRecentList.Count == 5
                    && agRecentList.Any(e => e.Model == "gemini-4-argon" && e.InTokens == 2000 && e.OutTokens == 80)
                    && agRecentList.Any(e => e.Model == "gemini-3.8-flash" && e.InTokens == 3000 && e.OutTokens == 90);

                var evMerge = UsageStorage.GetRecentEvents(allFrom, allTo, 50)
                    .FirstOrDefault(e => e.Agent == UsageStorage.AgentCodex && e.Model == "gpt-test");
                bool mergeOk = evMerge != null && evMerge.ProviderId == "relay-1" && evMerge.TtftMs == 350;

                // StepCode message.model + 跨重启增量 Seek 自愈回归验证
                var stepDir = Path.Combine(home, ".stepcode", "agent", "sessions", "demo");
                Directory.CreateDirectory(stepDir);
                var stepFile = Path.Combine(stepDir, "2026-11-26_test.jsonl");
                File.WriteAllText(stepFile,
                    "{\"type\":\"session\",\"id\":\"s1\",\"timestamp\":\"2026-11-26T01:00:00Z\"}\n" +
                    "{\"type\":\"model_change\",\"id\":\"m1\",\"modelId\":\"step-5-preview\",\"provider\":\"step\"}\n" +
                    "{\"type\":\"message\",\"id\":\"msg-1\",\"timestamp\":\"2026-11-26T01:00:02Z\",\"message\":{\"role\":\"assistant\",\"provider\":\"step\",\"model\":\"step-5-preview\",\"usage\":{\"input\":100,\"output\":20,\"cacheRead\":50,\"cacheWrite\":0,\"totalTokens\":170}}}\n");
                UsageScanner.ScanAll();
                File.AppendAllText(stepFile,
                    "{\"type\":\"message\",\"id\":\"msg-2\",\"timestamp\":\"2026-11-26T01:00:05Z\",\"message\":{\"role\":\"assistant\",\"provider\":\"step\",\"model\":\"step-5-preview\",\"usage\":{\"input\":120,\"output\":30,\"cacheRead\":80,\"cacheWrite\":0,\"totalTokens\":230}}}\n");
                UsageScanner.ScanAll();
                var stepEvs = UsageStorage.GetRecentEvents(allFrom, allTo, 50)
                    .Where(e => e.Agent == UsageStorage.AgentStepCode).ToList();
                bool stepModelOk = stepEvs.Count == 2 && stepEvs.All(e => e.Model == "step-5-preview");

                // Antigravity 可用模型下拉目录过滤、思考强度合并与刷新保持断言
                var pCatalog = new Models.Profile
                {
                    Email = "catalog@example.com",
                    ModelQuotas = new List<Models.AgModelQuotaInfo>
                    {
                        new()
                        {
                            ModelId = "gemini-4-neon-low",
                            IsPickerVisible = true,
                            PickerOrder = 0,
                            GroupKey = "gemini-4-neon",
                            GroupDisplayName = "Gemini 4 Neon",
                            ThinkingDisplayName = "Low",
                            SupportsThinking = true,
                            MaxTokens = 1048576
                        },
                        new()
                        {
                            ModelId = "gemini-4-neon-xhigh",
                            IsPickerVisible = true,
                            PickerOrder = 3,
                            GroupKey = "gemini-4-neon",
                            GroupDisplayName = "Gemini 4 Neon",
                            ThinkingDisplayName = "XHigh",
                            SupportsThinking = true,
                            MaxTokens = 1048576
                        },
                        new()
                        {
                            ModelId = "claude-opus-5-5-high",
                            IsPickerVisible = true,
                            PickerOrder = 4,
                            GroupKey = "claude-opus-5-5",
                            GroupDisplayName = "Claude Opus 5.5",
                            ThinkingDisplayName = "High",
                            SupportsThinking = true,
                            MaxTokens = 200000
                        },
                        new() { ModelId = "gemini-3.5-flash-lite", IsPickerVisible = false },
                        new() { ModelId = "gemini-4-argon-disabled", IsPickerVisible = true, PickerOrder = 5, Disabled = true }
                    }
                };
                pCatalog.IsModelsExpanded = true;
                var pCatalogRefreshed = new Models.Profile
                {
                    Email = "catalog@example.com",
                    ModelQuotas = pCatalog.ModelQuotas
                };
                pCatalog.UpdateFrom(pCatalogRefreshed);
                bool modelDrawerOk = pCatalog.AvailableModelCount == 2 &&
                                     pCatalog.MergedAvailableModels.Count == 2 &&
                                     pCatalog.MergedAvailableModels[0].DisplayTitle == "Gemini 4 Neon" &&
                                     pCatalog.MergedAvailableModels[0].LevelsBadge == "Low–XHigh" &&
                                     pCatalog.MergedAvailableModels[1].DisplayTitle == "Claude Opus 5.5" &&
                                     pCatalog.ModelsDrawerVisibility == Visibility.Visible &&
                                     pCatalog.IsModelsExpanded;

                // 悬浮窗 Antigravity 单端今日用量、速率与模型查询断言（隔离其他客户端记录）
                var wBefore = UsageStorage.GetAntigravityWidgetStats(DateTime.Now);
                var tsWidget = DateTime.Now.AddSeconds(2);
                UsageStorage.RecordUsage(UsageStorage.AgentAntigravity, "", "Antigravity", "gemini-4-neon", 2400, 600, tsWidget,
                    cacheReadTokens: 1800, cacheWriteTokens: 0, durationMs: 6000, ttftMs: 950, sourceId: "selftest:ag:widget:1");
                UsageStorage.RecordUsage(UsageStorage.AgentCodex, "openai", "OpenAI", "gpt-5.4", 9000, 1000, tsWidget,
                    cacheReadTokens: 0, cacheWriteTokens: 0, durationMs: 5000, ttftMs: 400, sourceId: "selftest:codex:widget:1");
                UsageScanner.ScanAntigravityQuick();
                var wStats = UsageStorage.GetAntigravityWidgetStats(tsWidget);
                AppSettingsService.Current.FloatingWidgetCapsuleLeft = 1234;
                AppSettingsService.Current.FloatingWidgetCapsuleTop = 888;
                AppSettingsService.Save();
                var reloadedSettings = AppSettingsService.Load();
                bool widgetStatsOk = (wStats.TodayRequests - wBefore.TodayRequests) == 1 &&
                                     (wStats.TodayTotalTokens - wBefore.TodayTotalTokens) == 3000 &&
                                     Math.Abs(wStats.LastSpeedTokPerSec - 100.0) < 0.5 &&
                                     wStats.LastTtftMs == 950 &&
                                     wStats.LastModel == "gemini-4-neon" &&
                                     reloadedSettings.FloatingWidgetCapsuleLeft == 1234 &&
                                     reloadedSettings.FloatingWidgetCapsuleTop == 888;

                sb.AppendLine($"usage_accuracy_probe=ok;codexOk={codexOk};claudeOk={claudeOk};piOk={piOk};ocOk={ocOk};gemOk={gemOk};dshOk={dshOk};agOk={agOk};agDbOk={agDbOk};mergeOk={mergeOk};stepModelOk={stepModelOk};modelDrawerOk={modelDrawerOk};widgetStatsOk={widgetStatsOk}");
            }
            catch (Exception ex)
            {
                sb.AppendLine("usage_accuracy_probe_err=" + ex.Message);
            }
            finally
            {
                UsageScanner.Reset();
                Environment.SetEnvironmentVariable("APISWITCH_APPDATA", prevAppData);
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine("pd_dialog_probe_err=" + ex.Message);
        }

        File.WriteAllText(Path.Combine(Path.GetTempPath(), "apiswitch-selftest.txt"), sb.ToString());
    }

    /// <summary>诊断模式：对指定邮箱的存档账号执行一次真实激活，输出可用模型与结果。</summary>
    static void RunActivate(string email)
    {
        // 在后台线程执行，避免阻塞 STA 调度线程导致异步续延死锁
        var sb = new StringBuilder();
        try
        {
            System.Threading.Tasks.Task.Run(() =>
            {
                var inner = new StringBuilder();
                try
                {
                    var profiles = ProfileStore.Load();
                    var p = profiles.FirstOrDefault(x => string.Equals(x.Email, email, StringComparison.OrdinalIgnoreCase));
                    if (p == null)
                    {
                        inner.AppendLine("profile-not-found");
                    }
                    else
                    {
                        inner.AppendLine("email=" + p.Email);
                        var tokenOk = AgQuotaService.EnsureFreshTokenAsync(p).GetAwaiter().GetResult();
                        inner.AppendLine("token_ok=" + tokenOk);
                        if (tokenOk && !string.IsNullOrEmpty(p.AccessToken))
                        {
                            var models = AgQuotaService.FetchAvailableModelNamesAsync(p, p.AccessToken).GetAwaiter().GetResult();
                            inner.AppendLine("available_models_count=" + models.Count);
                            inner.AppendLine("available_models=" + string.Join(", ", models));
                            inner.AppendLine("gemini_candidates=" + string.Join(", ", AgQuotaService.ResolveGeminiModels(models)));
                            inner.AppendLine("claude_candidates=" + string.Join(", ", AgQuotaService.ResolveClaudeModels(models)));
                        }
                        var (ok, latency, msg, rateLimited) = AgQuotaService.ActivateAccountQuotaAsync(p).GetAwaiter().GetResult();
                        inner.AppendLine("activate_ok=" + ok);
                        inner.AppendLine("activate_rate_limited=" + rateLimited);
                        inner.AppendLine("activate_latency_ms=" + latency);
                        inner.AppendLine("activate_message=" + msg);
                        inner.AppendLine("exhausted_reset_utc=" + (p.QuotaExhaustedResetAt?.ToString("O") ?? "-"));
                    }
                }
                catch (Exception ex)
                {
                    inner.AppendLine("activate_err=" + ex);
                }
                sb.Append(inner);
            }).Wait(TimeSpan.FromMinutes(3));
        }
        catch (Exception ex)
        {
            sb.AppendLine("activate_outer_err=" + ex.Message);
        }
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "apiswitch-activate.txt"), sb.ToString());
    }

    /// <summary>
    /// 回填历史用量数据：备份 usage.db → 清空事件表 → 全量重扫本地会话日志。
    /// 用于缓存/耗时/首字指标上线后补齐老数据——增量扫描按字节偏移推进，
    /// 不会重读已消费的文件，老行里的新字段会永远是 0。
    /// 报告写入 %TEMP%\apiswitch-backfill.txt。
    /// </summary>
    static void RunBackfillUsage()
    {
        var sb = new StringBuilder();
        try
        {
            sb.AppendLine($"start={DateTime.Now:O}");
            var result = UsageScanner.BackfillAll();
            var (total, withCache, withTiming) = UsageStorage.CountPerfRows();
            sb.AppendLine($"backup={result.BackupPath ?? "(none)"}");
            sb.AppendLine($"written={result.Written}");
            sb.AppendLine($"total_rows={total}");
            sb.AppendLine($"rows_with_cache={withCache}");
            sb.AppendLine($"rows_with_timing={withTiming}");
        }
        catch (Exception ex)
        {
            sb.AppendLine("error=" + ex);
        }
        var outPath = Path.Combine(Path.GetTempPath(), "apiswitch-backfill.txt");
        File.WriteAllText(outPath, sb.ToString());
        try { Console.WriteLine(sb.ToString()); } catch { }
    }

    static void RunProbe()
    {        var sb = new StringBuilder();
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

using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using APISwitch.Services;

namespace APISwitch;

public partial class App : System.Windows.Application
{
    private static Mutex? _singleInstanceMutex;
    private static EventWaitHandle? _wakeupEvent;
    private static RegisteredWaitHandle? _registeredWait;

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int dwProcessId);
    private const int ASFW_ANY = -1;

    public App()
    {
        DispatcherUnhandledException += (s, args) =>
        {
            try
            {
                var logDir = Path.Combine(AgPaths.AppData, "APISwitch");
                Directory.CreateDirectory(logDir);
                File.AppendAllText(Path.Combine(logDir, "crash.log"), $"[{DateTime.Now:O}] DispatcherUnhandledException: {args.Exception}\n");
                MessageBox.Show($"程序遇到未处理的异常：\n{args.Exception.Message}\n\n详细信息已写入日志：{Path.Combine(logDir, "crash.log")}", "APISwitch 错误", MessageBoxButton.OK, MessageBoxImage.Error);
                args.Handled = true;
            }
            catch { }
        };
        AppDomain.CurrentDomain.UnhandledException += (s, args) =>
        {
            try
            {
                var logDir = Path.Combine(AgPaths.AppData, "APISwitch");
                Directory.CreateDirectory(logDir);
                File.AppendAllText(Path.Combine(logDir, "crash.log"), $"[{DateTime.Now:O}] AppDomain UnhandledException: {args.ExceptionObject}\n");
            }
            catch { }
        };
    }

    static void OnComboBoxPreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        if (sender is System.Windows.Controls.ComboBox cb && !cb.IsDropDownOpen)
        {
            // 未展开下拉时：不切换选中项，让事件冒泡给外层 ScrollViewer 滚动页面
            e.Handled = true;
            var parent = System.Windows.Media.VisualTreeHelper.GetParent(cb) as UIElement;
            parent?.RaiseEvent(new System.Windows.Input.MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
            {
                RoutedEvent = UIElement.MouseWheelEvent,
                Source = sender,
            });
        }
    }
    public static void LogStartup(string msg)
    {
        try
        {
            var logDir = Path.Combine(AgPaths.AppData, "APISwitch");
            Directory.CreateDirectory(logDir);
            File.AppendAllText(Path.Combine(logDir, "startup.log"), $"[{DateTime.Now:O}] [PID:{Environment.ProcessId}] {msg}\n");
        }
        catch { }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        LogStartup($"OnStartup start, args=[{string.Join(" ", e.Args)}]");
        // 语言字典需在任何 UI / 诊断路径之前就绪（selftest / render-test 同样需要本地化文本）
        I18nService.Initialize();
        LogStartup($"I18n initialized, language={I18nService.ResolvedLanguage}.");
        if (e.Args.Contains("--probe"))
        {
            RunProbe();
            Shutdown();
            return;
        }
        if (e.Args.Length >= 2 && e.Args[0] == "--activate")
        {
            RunActivate(e.Args[1]);
            Shutdown();
            return;
        }
        if (e.Args.Contains("--selftest"))
        {
            RunSelfTest();
            Shutdown();
            return;
        }
        if (e.Args.Length >= 2 && e.Args[0] == "--render-test")
        {
            var outputPath = e.Args[1];
            try
            {
                LocalProxyServer.Initialize();
                var win = new MainWindow();
                win.Show();
                win.UpdateLayout();
                var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(
                    (int)win.ActualWidth, (int)win.ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                rtb.Render(win);
                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
                using (var fs = File.Open(outputPath, FileMode.Create, FileAccess.Write))
                {
                    encoder.Save(fs);
                }
                win.Close();
            }
            catch (Exception ex)
            {
                File.WriteAllText(outputPath + ".err", ex.ToString());
            }
            Shutdown();
            return;
        }

        if (e.Args.Length >= 2 && e.Args[0] == "--apply-desktop")
        {
            var targetName = e.Args[1];
            var all = CliStore.LoadClaudeDesktop();
            var target = all.FirstOrDefault(x => string.Equals(x.Name, targetName, StringComparison.OrdinalIgnoreCase) || string.Equals(x.Id, targetName, StringComparison.OrdinalIgnoreCase));
            if (target != null)
            {
                LocalProxyServer.SetClaudeDesktopEnabled(true);
                ClaudeDesktopCli.Apply(target);
            }
            Shutdown();
            return;
        }

        const string mutexName = "APISwitch_Desktop_App_Mutex_6723c035";
        const string eventName = "APISwitch_Desktop_Wakeup_Event_6723c035";

        bool hasHandle;
        try
        {
            _singleInstanceMutex = new Mutex(false, mutexName);
            hasHandle = _singleInstanceMutex.WaitOne(0, false);
        }
        catch (AbandonedMutexException)
        {
            hasHandle = true;
        }
        catch
        {
            hasHandle = false;
        }

        if (!hasHandle)
        {
            LogStartup("Another instance is already running. Signaling wakeup event and exiting.");
            try
            {
                AllowSetForegroundWindow(ASFW_ANY);
                using var wakeHandle = EventWaitHandle.OpenExisting(eventName);
                wakeHandle.Set();
            }
            catch { }

            Shutdown();
            return;
        }

        LogStartup("Single instance mutex acquired.");

        try
        {
            _wakeupEvent = new EventWaitHandle(false, EventResetMode.AutoReset, eventName);
            _registeredWait = ThreadPool.RegisterWaitForSingleObject(_wakeupEvent, (state, timedOut) =>
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        if (MainWindow is MainWindow mw)
                        {
                            mw.RestoreAndActivate();
                        }
                    }
                    catch { }
                }));
            }, null, -1, false);
        }
        catch { }

        LocalProxyServer.Initialize();
        LogStartup("LocalProxyServer initialized.");

        // 修复：ComboBox 聚焦（未展开下拉）时滚轮会误切换选中项。
        // 在隧道阶段拦截（Combo 自身的 OnMouseWheel 在冒泡阶段），未展开时把滚轮
        // 重新以冒泡事件抛回，让外层 ScrollViewer 正常滚动页面。
        EventManager.RegisterClassHandler(typeof(System.Windows.Controls.ComboBox),
            UIElement.PreviewMouseWheelEvent,
            new System.Windows.Input.MouseWheelEventHandler(OnComboBoxPreviewMouseWheel));
        base.OnStartup(e);
        base.OnStartup(e);

        LogStartup("Creating MainWindow...");
        var mainWindow = new MainWindow();
        MainWindow = mainWindow;
        LogStartup("MainWindow instantiated. Calling Show()...");
        mainWindow.Show();
        LogStartup("mainWindow.Show() finished.");
    }

    protected override void OnExit(ExitEventArgs e)
    {
        LogStartup($"OnExit called with ExitCode: {e.ApplicationExitCode}");
        try
        {
            _registeredWait?.Unregister(null);
            _registeredWait = null;
        }
        catch { }

        try
        {
            if (_wakeupEvent != null)
            {
                _wakeupEvent.Dispose();
                _wakeupEvent = null;
            }
        }
        catch { }

        try
        {
            if (_singleInstanceMutex != null)
            {
                _singleInstanceMutex.ReleaseMutex();
                _singleInstanceMutex.Dispose();
                _singleInstanceMutex = null;
            }
        }
        catch { }

        try
        {
            LocalProxyServer.Stop(restoreDirectConfig: false);
        }
        catch { }
        base.OnExit(e);
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
            // 4) 批量激活按钮可见（曾误置 Collapsed 导致功能不可达）
            var mw2 = new MainWindow { Visibility = Visibility.Hidden };
            var batchBtn = mw2.FindName("BatchActivateBtn") as System.Windows.Controls.Control;
            sb.AppendLine("ag_batch_btn=" + (batchBtn != null && batchBtn.Visibility == Visibility.Visible ? "visible" : "hidden"));
            mw2.Close();
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
            sb.AppendLine("pd_dialog_probe=ok");
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

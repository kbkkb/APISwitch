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
    static readonly object _logLock = new();

    public static void LogStartup(string msg)
    {
        // 后台配额任务与 UI 线程都会写日志，轮转涉及"读-截断-写"，
        // 必须串行化，否则并发轮转会互相覆盖导致日志错乱
        lock (_logLock)
        {
            try
            {
                var logDir = Path.Combine(AgPaths.AppData, "APISwitch");
                Directory.CreateDirectory(logDir);
                var logPath = Path.Combine(logDir, "startup.log");

                // 简单轮转：超过 1MB 时保留尾部 256KB，避免日志无限增长
                try
                {
                    var fi = new FileInfo(logPath);
                    if (fi.Exists && fi.Length > 1_048_576)
                    {
                        const int keep = 262_144;
                        var tail = new byte[keep];
                        using (var fs = new FileStream(logPath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
                        {
                            fs.Seek(-keep, SeekOrigin.End);
                            int read = 0;
                            while (read < keep)
                            {
                                int n = fs.Read(tail, read, keep - read);
                                if (n <= 0) break;
                                read += n;
                            }
                            fs.SetLength(0);
                            fs.Position = 0;
                            fs.Write(tail, 0, read);
                        }
                    }
                }
                catch { }

                File.AppendAllText(logPath, $"[{DateTime.Now:O}] [PID:{Environment.ProcessId}] {msg}\n");
            }
            catch { }
        }
    }

    protected override async void OnStartup(StartupEventArgs e)
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
            await RunSelfTest();
            Shutdown();
            return;
        }
        if (e.Args.Contains("--backfill-usage"))
        {
            RunBackfillUsage();
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
                if (e.Args.Length >= 3)
                {
                    var tag = e.Args[2];
                    foreach (System.Windows.Controls.TabItem item in win.Tabs.Items)
                    {
                        if (string.Equals(item.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase))
                        {
                            win.Tabs.SelectedItem = item;
                            break;
                        }
                    }
                }
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

        if (e.Args.Length >= 2 && e.Args[0] == "--render-usage")
        {
            var outputPath = e.Args[1];
            int days = e.Args.Length >= 3 && int.TryParse(e.Args[2], out var d) ? d : 180;
            string tab = e.Args.Length >= 4 ? e.Args[3].ToLowerInvariant() : "providers";
            string agent = e.Args.Length >= 5 ? e.Args[4] : "";
            try
            {
                var win = new Dialogs.UsageMonitorWindow();
                win.Show();
                win.UpdateLayout();
                if (days == 365) win.Quick365DRadio.IsChecked = true;
                else if (days == 180) win.Quick180DRadio.IsChecked = true;
                else if (days == 30) win.Quick30DRadio.IsChecked = true;
                else if (days == 7) win.Quick7DRadio.IsChecked = true;
                else if (days == 1) win.Quick1DRadio.IsChecked = true;
                if (tab == "agents") win.TabAgentsRadio.IsChecked = true;
                else if (tab == "models") win.TabModelsRadio.IsChecked = true;
                else if (tab == "calls") win.TabCallsRadio.IsChecked = true;
                if (!string.IsNullOrWhiteSpace(agent)) win.SelectAgentFilter(agent);
                await Task.Delay(300);
                await win.InitialScanTask;
                await Task.Delay(400);
                win.UpdateLayout();
                var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(
                    (int)Math.Max(win.ActualWidth, 100), (int)Math.Max(win.ActualHeight, 100), 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
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

}

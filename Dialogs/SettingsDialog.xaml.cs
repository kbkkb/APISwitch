using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MediaColor = System.Windows.Media.Color;
using MediaBrush = System.Windows.Media.Brush;
using WpfPoint = System.Windows.Point;
using WpfSaveFileDialog = Microsoft.Win32.SaveFileDialog;
using WpfOpenFileDialog = Microsoft.Win32.OpenFileDialog;
using APISwitch.Services;

namespace APISwitch.Dialogs;

public partial class SettingsDialog : Window
{
    private readonly MainWindow _mainWindow;
    private readonly string _themeTag;

    public SettingsDialog(string themeTag, MainWindow mainWindow)
    {
        InitializeComponent();
        _themeTag = themeTag;
        _mainWindow = mainWindow;

        ApplySettingsTheme(themeTag);
        InitSettingsValues();
    }

    private void ApplySettingsTheme(string tag)
    {
        MediaColor primaryColor;
        MediaColor gradientEndColor;
        MediaColor hoverColor;
        MediaColor titleBarBgColor;
        MediaColor titleBarBorderColor;
        MediaColor sidebarBorderColor;
        MediaColor windowBgColor;
        MediaColor activeBgColor;
        MediaColor activeBorderColor;

        string badgeName;

        switch (tag)
        {
            case "codex":
                primaryColor = MediaColor.FromRgb(0x10, 0xA3, 0x7F);
                gradientEndColor = MediaColor.FromRgb(0x05, 0x96, 0x69);
                hoverColor = MediaColor.FromRgb(0x04, 0x78, 0x57);
                activeBgColor = MediaColor.FromRgb(0xC4, 0xF3, 0xDE);
                activeBorderColor = MediaColor.FromRgb(0x34, 0xD3, 0x99);
                titleBarBgColor = MediaColor.FromRgb(0xD3, 0xEF, 0xE3);
                titleBarBorderColor = MediaColor.FromRgb(0xB9, 0xE5, 0xD2);
                sidebarBorderColor = MediaColor.FromRgb(0xBF, 0xE7, 0xD6);
                windowBgColor = MediaColor.FromRgb(0xDC, 0xF4, 0xEB);
                badgeName = "Codex 风格";
                break;

            case "claude":
            case "desktop":
                primaryColor = MediaColor.FromRgb(0xD9, 0x77, 0x06);
                gradientEndColor = MediaColor.FromRgb(0xEA, 0x58, 0x0C);
                hoverColor = MediaColor.FromRgb(0xB4, 0x53, 0x09);
                activeBgColor = MediaColor.FromRgb(0xFC, 0xE3, 0xCB);
                activeBorderColor = MediaColor.FromRgb(0xFB, 0x92, 0x3C);
                titleBarBgColor = MediaColor.FromRgb(0xF0, 0xDF, 0xCD);
                titleBarBorderColor = MediaColor.FromRgb(0xE4, 0xCD, 0xAF);
                sidebarBorderColor = MediaColor.FromRgb(0xE8, 0xD4, 0xBE);
                windowBgColor = MediaColor.FromRgb(0xF7, 0xE8, 0xD8);
                badgeName = tag == "desktop" ? "Claude Desktop 风格" : "Claude CLI 风格";
                break;

            case "opencode":
                primaryColor = MediaColor.FromRgb(0x02, 0x84, 0xC7);
                gradientEndColor = MediaColor.FromRgb(0x0E, 0xA5, 0xE9);
                hoverColor = MediaColor.FromRgb(0x03, 0x69, 0xA1);
                activeBgColor = MediaColor.FromRgb(0xBD, 0xE3, 0xFB);
                activeBorderColor = MediaColor.FromRgb(0x38, 0xBD, 0xF8);
                titleBarBgColor = MediaColor.FromRgb(0xCC, 0xE6, 0xFA);
                titleBarBorderColor = MediaColor.FromRgb(0xB0, 0xD7, 0xF6);
                sidebarBorderColor = MediaColor.FromRgb(0xB9, 0xDC, 0xF7);
                windowBgColor = MediaColor.FromRgb(0xD8, 0xED, 0xFC);
                badgeName = "OpenCode 风格";
                break;

            case "pi":
                primaryColor = MediaColor.FromRgb(0x7C, 0x3A, 0xED);
                gradientEndColor = MediaColor.FromRgb(0x93, 0x33, 0xEA);
                hoverColor = MediaColor.FromRgb(0x6D, 0x28, 0xD9);
                activeBgColor = MediaColor.FromRgb(0xD9, 0xC8, 0xFB);
                activeBorderColor = MediaColor.FromRgb(0xA7, 0x8B, 0xFA);
                titleBarBgColor = MediaColor.FromRgb(0xDE, 0xD3, 0xFA);
                titleBarBorderColor = MediaColor.FromRgb(0xC8, 0xB6, 0xF5);
                sidebarBorderColor = MediaColor.FromRgb(0xD0, 0xC1, 0xF7);
                windowBgColor = MediaColor.FromRgb(0xE7, 0xDC, 0xFD);
                badgeName = "Pi 风格";
                break;

            case "antigravity":
            default:
                primaryColor = MediaColor.FromRgb(0x4F, 0x46, 0xE5);
                gradientEndColor = MediaColor.FromRgb(0x63, 0x66, 0xF1);
                hoverColor = MediaColor.FromRgb(0x43, 0x38, 0xCA);
                activeBgColor = MediaColor.FromRgb(0xDB, 0xE5, 0xFE);
                activeBorderColor = MediaColor.FromRgb(0x81, 0x8C, 0xF8);
                titleBarBgColor = MediaColor.FromRgb(0xE0, 0xE7, 0xF8);
                titleBarBorderColor = MediaColor.FromRgb(0xCB, 0xD7, 0xEE);
                sidebarBorderColor = MediaColor.FromRgb(0xCF, 0xDB, 0xEE);
                windowBgColor = MediaColor.FromRgb(0xE8, 0xEE, 0xFB);
                badgeName = "Antigravity 风格";
                break;
        }

        Resources["WindowBgBrush"] = new SolidColorBrush(windowBgColor);
        Resources["TitleBarBgBrush"] = new SolidColorBrush(titleBarBgColor);
        Resources["TitleBarBorderBrush"] = new SolidColorBrush(titleBarBorderColor);
        Resources["SidebarBorderBrush"] = new SolidColorBrush(sidebarBorderColor);
        Resources["TabActiveBgBrush"] = new SolidColorBrush(activeBgColor);
        Resources["TabActiveBorderBrush"] = new SolidColorBrush(activeBorderColor);
        Resources["AccentBrush"] = new SolidColorBrush(primaryColor);
        Resources["AccentHoverBrush"] = new SolidColorBrush(hoverColor);

        var grad = new LinearGradientBrush { StartPoint = new WpfPoint(0, 0), EndPoint = new WpfPoint(1, 0) };
        grad.GradientStops.Add(new GradientStop(primaryColor, 0));
        grad.GradientStops.Add(new GradientStop(gradientEndColor, 1));
        Resources["AccentGradient"] = grad;
    }

    private void InitSettingsValues()
    {
        AutoStartCheckBox.IsChecked = AppSettingsService.IsAutoStartEnabled();

        var settings = AppSettingsService.Current;
        CloseMinimizeRadio.IsChecked = settings.MinimizeToTrayOnClose;
        CloseExitRadio.IsChecked = !settings.MinimizeToTrayOnClose;
        AutoCheckUpdateCheckBox.IsChecked = settings.AutoCheckUpdate;

        var ver = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        if (ver != null && AboutVersionText != null)
        {
            AboutVersionText.Text = $"v{ver.Major}.{ver.Minor}.{ver.Build}";
        }

        if (ProxyHostTextBox != null)
            ProxyHostTextBox.Text = LocalProxyServer.Host;
        if (ProxyPortTextBox != null)
            ProxyPortTextBox.Text = LocalProxyServer.Port.ToString();

        UpdateProxyEndpointsUI();

        if (CodexProxyStatusText != null)
            CodexProxyStatusText.Text = LocalProxyServer.IsCodexEnabled ? "已就绪" : "未开启";
        if (ClaudeCliProxyStatusText != null)
            ClaudeCliProxyStatusText.Text = LocalProxyServer.IsClaudeCliEnabled ? "已就绪" : "未开启";
        if (DesktopProxyStatusText != null)
            DesktopProxyStatusText.Text = LocalProxyServer.IsClaudeDesktopEnabled ? "已就绪" : "未开启";
    }

    private void UpdateProxyEndpointsUI()
    {
        if (ProxyHeadingText != null)
            ProxyHeadingText.Text = $"服务状态：运行中 ({LocalProxyServer.Host}:{LocalProxyServer.Port})";
        if (ProxyCodexUrlText != null)
            ProxyCodexUrlText.Text = LocalProxyServer.ProxyCodexUrl;
        if (ProxyClaudeCliUrlText != null)
            ProxyClaudeCliUrlText.Text = LocalProxyServer.ProxyClaudeCliUrl;
        if (ProxyDesktopUrlText != null)
            ProxyDesktopUrlText.Text = LocalProxyServer.ProxyClaudeDesktopUrl;
    }

    private void Header_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void OnClose(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void OnNavChanged(object sender, RoutedEventArgs e)
    {
        if (PageSync == null || PageGeneral == null || PageProxy == null || PageAbout == null) return;

        var tag = (sender as FrameworkElement)?.Tag?.ToString() ?? "sync";
        PageSync.Visibility = tag == "sync" ? Visibility.Visible : Visibility.Collapsed;
        PageGeneral.Visibility = tag == "general" ? Visibility.Visible : Visibility.Collapsed;
        PageProxy.Visibility = tag == "proxy" ? Visibility.Visible : Visibility.Collapsed;
        PageAbout.Visibility = tag == "about" ? Visibility.Visible : Visibility.Collapsed;
    }

    // ==================== Data Sync Handlers ====================

    private void OnExportAllConfigClick(object sender, RoutedEventArgs e)
    {
        var sfd = new WpfSaveFileDialog
        {
            Title = "导出所有配置备份",
            Filter = "JSON 备份文件 (*.json)|*.json",
            FileName = $"APISwitch_Backup_{DateTime.Now:yyyyMMdd_HHmmss}.json"
        };

        if (sfd.ShowDialog(this) == true)
        {
            try
            {
                var ver = AboutVersionText?.Text ?? "v0.1.1";
                ConfigSyncService.ExportToFile(sfd.FileName, ver);
                MessageBox.Show(
                    "所有配置已成功导出！\n\n您可以将此文件保存在网盘或移动设备中，在其他电脑上直接通过「导入配置」即可无缝同步。",
                    "导出成功",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"导出备份失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private void OnImportAllConfigClick(object sender, RoutedEventArgs e)
    {
        var ofd = new WpfOpenFileDialog
        {
            Title = "选择 APISwitch 备份文件",
            Filter = "JSON 备份文件 (*.json)|*.json|所有文件 (*.*)|*.*"
        };

        if (ofd.ShowDialog(this) == true)
        {
            try
            {
                var bundle = ConfigSyncService.ReadBackupFile(ofd.FileName);
                bool overwrite = ImportOverwriteRadio.IsChecked == true;

                string confirmMsg = overwrite
                    ? "【警告】完全覆盖模式将使用备份中的配置彻底清空并替换当前的供应商列表与账号数据。\n\n是否确认覆盖导入？"
                    : $"准备从备份文件导入配置：\n\n• 备份时间：{bundle.ExportedAt:yyyy-MM-dd HH:mm:ss}\n• 来源设备：{bundle.DeviceName}\n• 导入模式：合并追加（保留现有供应商）\n\n是否开始导入？";

                if (MessageBox.Show(confirmMsg, "导入确认", MessageBoxButton.OKCancel, overwrite ? MessageBoxImage.Warning : MessageBoxImage.Question) != MessageBoxResult.OK)
                {
                    return;
                }

                var res = ConfigSyncService.ImportBackupBundle(bundle, overwrite);
                _mainWindow.Dispatcher.Invoke(() =>
                {
                    _mainWindow.RefreshAll();
                });

                MessageBox.Show(
                    $"配置导入成功！共处理 {res.TotalCount} 项配置：\n\n" +
                    $"• Claude CLI 供应商: {res.ClaudeCliCount} 个\n" +
                    $"• Claude 客户端供应商: {res.ClaudeDesktopCount} 个\n" +
                    $"• Codex 供应商: {res.CodexCount} 个\n" +
                    $"• OpenCode 供应商: {res.OpenCodeCount} 个\n" +
                    $"• Pi 供应商: {res.PiCount} 个\n" +
                    $"• Antigravity 账号: {res.ProfilesCount} 个",
                    "导入成功",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"导入备份失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private void OnImportCcSwitchClick(object sender, RoutedEventArgs e)
    {
        Close();
        _mainWindow.OnImportCc(sender, e);
    }

    private void OnOpenDataFolderClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var dir = Path.Combine(AgPaths.AppData, "APISwitch");
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"打开目录失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ==================== General Settings Handlers ====================

    private void AutoStartCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        AppSettingsService.SetAutoStart(AutoStartCheckBox.IsChecked == true);
    }

    private void CloseActionRadio_Changed(object sender, RoutedEventArgs e)
    {
        AppSettingsService.Current.MinimizeToTrayOnClose = CloseMinimizeRadio.IsChecked == true;
        AppSettingsService.Save();
    }

    private void AutoCheckUpdateCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        AppSettingsService.Current.AutoCheckUpdate = AutoCheckUpdateCheckBox.IsChecked == true;
        AppSettingsService.Save();
    }

    private System.Windows.Threading.DispatcherTimer? _toastTimer;

    public void ShowToast(string msg, bool isError = false, bool isInfo = false)
    {
        Dispatcher.Invoke(() =>
        {
            _toastTimer?.Stop();
            ToastText.Text = msg;
            if (isInfo)
            {
                ToastIcon.Data = (Geometry)FindResource("IconRefresh");
                ToastIcon.Fill = (MediaBrush)FindResource("AccentBrush");
                ToastBanner.BorderBrush = (MediaBrush)FindResource("AccentBrush");
            }
            else if (isError)
            {
                ToastIcon.Data = (Geometry)FindResource("IconClose");
                ToastIcon.Fill = (MediaBrush)FindResource("DangerBrush");
                ToastBanner.BorderBrush = (MediaBrush)FindResource("DangerBrush");
            }
            else
            {
                ToastIcon.Data = (Geometry)FindResource("IconCheck");
                ToastIcon.Fill = (MediaBrush)FindResource("GreenBrush");
                ToastBanner.BorderBrush = (MediaBrush)FindResource("CardBorderBrush");
            }

            ToastBanner.Visibility = Visibility.Visible;
            var anim = new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180));
            ToastBanner.BeginAnimation(OpacityProperty, anim);

            _toastTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(isInfo ? 5.0 : 2.8) };
            _toastTimer.Tick += (_, _) =>
            {
                _toastTimer.Stop();
                var fadeOut = new System.Windows.Media.Animation.DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(200));
                fadeOut.Completed += (_, _) => ToastBanner.Visibility = Visibility.Collapsed;
                ToastBanner.BeginAnimation(OpacityProperty, fadeOut);
            };
            _toastTimer.Start();
        });
    }

    private async void OnManualCheckUpdateClick(object sender, RoutedEventArgs e)
    {
        ShowToast("正在检查最新版本…", isInfo: true);
        try
        {
            var info = await UpdateService.CheckForUpdatesAsync();

            if (info == null)
            {
                ShowToast("检查更新失败，请确认网络连接或稍后重试", isError: true);
                return;
            }

            _mainWindow.ApplyUpdateInfo(info);

            if (info.HasUpdate)
            {
                ShowToast($"⚡ 发现新版本 {info.LatestVersion}！");
                new UpdateDialog(info) { Owner = this }.ShowDialog();
            }
            else
            {
                ShowToast($"当前已是最新版本 ({info.CurrentVersion})");
            }
        }
        catch (Exception ex)
        {
            ShowToast("检查更新异常：" + ex.Message, isError: true);
        }
    }

    // ==================== Local Proxy Handlers ====================

    private void OnSaveProxyAddressClick(object sender, RoutedEventArgs e)
    {
        var host = ProxyHostTextBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(host)) host = "127.0.0.1";

        if (!int.TryParse(ProxyPortTextBox.Text?.Trim(), out int port) || port < 1024 || port > 65535)
        {
            ShowToast("请输入合法的端口号（1024 - 65535）", isError: true);
            return;
        }

        var (success, msg) = LocalProxyServer.UpdateAddress(host, port);
        UpdateProxyEndpointsUI();
        ShowToast(msg, isError: !success);
    }

    private void OnResetProxyAddressClick(object sender, RoutedEventArgs e)
    {
        ProxyHostTextBox.Text = "127.0.0.1";
        ProxyPortTextBox.Text = "15725";
        var (success, msg) = LocalProxyServer.UpdateAddress("127.0.0.1", 15725);
        UpdateProxyEndpointsUI();
        ShowToast(success ? "已恢复默认路由服务配置 (127.0.0.1:15725)" : msg, isError: !success);
    }

    private void OnCopyCodexUrlClick(object sender, RoutedEventArgs e) =>
        CopyTextToClipboard(LocalProxyServer.ProxyCodexUrl, "Codex 路由端点");

    private void OnCopyClaudeCliUrlClick(object sender, RoutedEventArgs e) =>
        CopyTextToClipboard(LocalProxyServer.ProxyClaudeCliUrl, "Claude CLI 路由端点");

    private void OnCopyDesktopUrlClick(object sender, RoutedEventArgs e) =>
        CopyTextToClipboard(LocalProxyServer.ProxyClaudeDesktopUrl, "Claude Desktop 路由端点");

    private void CopyTextToClipboard(string text, string label)
    {
        try
        {
            System.Windows.Clipboard.SetText(text);
            ShowToast($"已复制 {label} 到剪贴板");
        }
        catch (Exception ex)
        {
            ShowToast($"复制失败：{ex.Message}", isError: true);
        }
    }

    private void OnRestartProxyClick(object sender, RoutedEventArgs e)
    {
        try
        {
            LocalProxyServer.Stop(restoreDirectConfig: false);
            LocalProxyServer.Start();
            UpdateProxyEndpointsUI();
            ShowToast($"路由服务已重启 ({LocalProxyServer.Host}:{LocalProxyServer.Port})");
        }
        catch (Exception ex)
        {
            ShowToast($"重启服务异常：{ex.Message}", isError: true);
        }
    }

    private void OnOpenGitHubClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("https://github.com/kbkkb/APISwitch") { UseShellExecute = true });
        }
        catch { }
    }

    private void OnOpenBugFeedbackClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("https://github.com/kbkkb/APISwitch/issues") { UseShellExecute = true });
        }
        catch { }
    }
}

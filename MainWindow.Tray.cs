using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MediaColor = System.Windows.Media.Color;
using WpfPoint = System.Windows.Point;
using APISwitch.Dialogs;
using APISwitch.Models;
using APISwitch.Services;

namespace APISwitch;

public partial class MainWindow
{
    System.Windows.Forms.ContextMenuStrip CreateAppTrayMenu() =>
        ModernTrayMenu.Create(
            onShow: RestoreFromTray,
            onRefresh: () => Dispatcher.Invoke(() => _ = RefreshAllAgQuotasAsync(silent: false)),
            onLaunchIde: () => Dispatcher.Invoke(() =>
            {
                try { AgProcess.StartIde(); }
                catch (Exception ex) { ShowToast(I18nService.F("Common.LaunchIdeFailFmt", ex.Message), isError: true); }
            }),
            onExit: ExitApp,
            version: AppVersionText?.Text ?? "v0.1.1",
            getAccounts: BuildTrayAccountsSnapshot,
            onSwitchAccount: p => Dispatcher.Invoke(() => _ = SwitchToProfile(p)),
            onOpenUsage: () => Dispatcher.Invoke(() => OnOpenUsageMonitor(this, new RoutedEventArgs())),
            onToggleWidget: () => Dispatcher.Invoke(ToggleFloatingWidget),
            isWidgetEnabled: () => AppSettingsService.Current.FloatingWidgetEnabled
        );

    IReadOnlyList<ModernTrayMenu.TrayAccountEntry> BuildTrayAccountsSnapshot()
    {
        var list = new List<ModernTrayMenu.TrayAccountEntry>(_profiles.Count);
        foreach (var p in _profiles)
        {
            var email = !string.IsNullOrEmpty(p.Email) ? p.Email : p.Name;
            string gem = (p.Quota5hFraction.HasValue && p.Quota5hFraction.Value < 0.9999)
                ? p.Quota5hPercentText
                : p.QuotaWeeklyPercentText;
            string c3p = (p.Quota3p5hFraction.HasValue && p.Quota3p5hFraction.Value < 0.9999)
                ? p.Quota3p5hPercentText
                : p.Quota3pWeeklyPercentText;
            string shortTier = p.IsUltra ? "Ultra" : (p.IsPaidPro ? "Pro" : (p.IsPromoPro ? "Exp" : (p.IsPlus ? "Plus" : "Free")));
            list.Add(new ModernTrayMenu.TrayAccountEntry(p, email, shortTier, gem, c3p, p.IsCurrent));
        }
        return list;
    }

    void RebuildTrayMenu()
    {
        try
        {
            Dispatcher.Invoke(() =>
            {
                if (_notifyIcon != null && _notifyIcon.ContextMenuStrip is IDisposable old)
                {
                    _notifyIcon.ContextMenuStrip = null;
                    old.Dispose();
                }
                if (_notifyIcon != null)
                {
                    _notifyIcon.ContextMenuStrip = CreateAppTrayMenu();
                }
            });
        }
        catch { }
    }

    void InitTrayIcon()
    {
        try
        {
            _notifyIcon = new System.Windows.Forms.NotifyIcon
            {
                Text = "APISwitch",
                Visible = true
            };

            var exePath = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exePath) && File.Exists(exePath))
            {
                _notifyIcon.Icon = System.Drawing.Icon.ExtractAssociatedIcon(exePath);
            }
            if (_notifyIcon.Icon == null)
            {
                try
                {
                    var streamInfo = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico"));
                    if (streamInfo?.Stream != null)
                        _notifyIcon.Icon = new System.Drawing.Icon(streamInfo.Stream);
                }
                catch { }
            }
            if (_notifyIcon.Icon == null)
            {
                var icoPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "app.ico");
                if (File.Exists(icoPath))
                    _notifyIcon.Icon = new System.Drawing.Icon(icoPath);
                else
                    _notifyIcon.Icon = System.Drawing.SystemIcons.Application;
            }

            _notifyIcon.MouseClick += (_, e) =>
            {
                if (e.Button != System.Windows.Forms.MouseButtons.Left) return;
                // 左键 = 唤出/前置主窗口（窗口被最小化到任务栏或收进托盘时都能一键找回）。
                Dispatcher.Invoke(() => RestoreAndActivate());
            };

            _notifyIcon.ContextMenuStrip = CreateAppTrayMenu();
        }
        catch { }
    }

    public void RestoreAndActivate()
    {
        // 先把窗口状态归一化，再亮出任务栏按钮：避免"以最小化状态显示"——
        // 那种情况下屏幕上不会出现窗口，只在任务栏留一个最小化按钮。
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }
        if (!IsVisible)
        {
            Show();
        }
        ShowInTaskbar = true;
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
        OnForegroundEntered();
    }

    /// <summary>
    /// 最小化到任务栏：任务栏上必须留下这个任务的按钮（标准 Windows 行为）。
    /// 与"收进系统托盘"是两件事——最小化绝不 Hide()。
    /// </summary>
    public void MinimizeToTaskbar()
    {
        ShowInTaskbar = true;
        WindowState = WindowState.Minimized;
    }

    /// <summary>
    /// 收进系统托盘（仅"关闭窗口"时使用）：窗口和任务栏按钮都消失，只留托盘图标。
    /// </summary>
    public void MinimizeToTray()
    {
        _trayTransition = true;
        try
        {
            // 若此刻已被最小化，先恢复成 Normal：Windows 对"最小化中的窗口"执行
            // ShowInTaskbar=false 时，任务栏上那个最小化按钮不一定会被清掉。
            if (WindowState == WindowState.Minimized)
            {
                WindowState = WindowState.Normal;
            }
            ShowInTaskbar = false;
            Hide();
        }
        finally
        {
            _trayTransition = false;
        }
        if (!AppSettingsService.Current.FloatingWidgetEnabled)
            _agQuotaTimer?.Stop();
    }
    void RestoreFromTray() => RestoreAndActivate();

    void ExitApp()
    {
        _isExiting = true;
        DisposeFloatingWidget();
        if (_notifyIcon != null)
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _notifyIcon = null;
        }
        System.Windows.Application.Current.Shutdown();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_isExiting)
        {
            if (AppSettingsService.Current.MinimizeToTrayOnClose)
            {
                e.Cancel = true;
                MinimizeToTray();
                return;
            }
            else
            {
                ExitApp();
                return;
            }
        }
        base.OnClosing(e);
    }

    void OnMinWindow(object sender, RoutedEventArgs e) => MinimizeToTaskbar();

    void OnMaxWindow(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    void OnCloseWindow(object sender, RoutedEventArgs e)
    {
        if (AppSettingsService.Current.MinimizeToTrayOnClose)
        {
            MinimizeToTray();
        }
        else
        {
            ExitApp();
        }
    }

}

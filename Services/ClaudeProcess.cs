using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace APISwitch.Services;

public static class ClaudeProcess
{
    private const string ClaudeAppxId = "Claude_pzs8sxrjxfjjc!Claude";

    /// <summary>
    /// 判断进程是否属于 Claude 客户端
    /// </summary>
    public static bool IsClaudeProcess(Process p)
    {
        try
        {
            var name = p.ProcessName.ToLowerInvariant();
            if (name == "claude")
            {
                return true;
            }
        }
        catch { }
        return false;
    }

    /// <summary>
    /// 获取所有当前正在运行的 Claude 桌面客户端进程
    /// </summary>
    public static Process[] GetClaudeProcesses()
    {
        var list = new List<Process>();
        foreach (var p in Process.GetProcesses())
        {
            if (IsClaudeProcess(p))
            {
                list.Add(p);
            }
        }
        return list.ToArray();
    }

    public static bool IsRunning() => GetClaudeProcesses().Length > 0;

    /// <summary>
    /// 寻找可启动的 Claude 桌面客户端路径或快捷方式
    /// </summary>
    public static string? FindClaudeExecutableOrShortcut()
    {
        // 1. 检查快捷方式（桌面与开始菜单）
        var shortcutPaths = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Claude.lnk"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), "Claude.lnk"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs", "Claude.lnk"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), "Programs", "Claude.lnk"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs", "Anthropic", "Claude.lnk"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), "Programs", "Anthropic", "Claude.lnk"),
        };
        foreach (var s in shortcutPaths)
        {
            if (File.Exists(s)) return s;
        }

        // 2. 检查常见安装可执行文件路径
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AnthropicClaude", "Claude.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Claude", "Claude.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Claude", "Claude.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Claude", "Claude.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Claude", "Claude.exe"),
        };
        foreach (var c in candidates)
        {
            if (File.Exists(c)) return c;
        }

        // 3. 检查注册表卸载信息
        try
        {
            var registryPaths = new[]
            {
                @"Software\Microsoft\Windows\CurrentVersion\Uninstall",
                @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
            };

            foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
            {
                foreach (var regPath in registryPaths)
                {
                    using var key = hive.OpenSubKey(regPath);
                    if (key == null) continue;
                    foreach (var subName in key.GetSubKeyNames())
                    {
                        using var appKey = key.OpenSubKey(subName);
                        if (appKey == null) continue;
                        var disp = appKey.GetValue("DisplayName")?.ToString() ?? "";
                        if (disp.IndexOf("Claude", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            var loc = appKey.GetValue("InstallLocation")?.ToString();
                            if (!string.IsNullOrEmpty(loc))
                            {
                                var cand = Path.Combine(loc, "Claude.exe");
                                if (File.Exists(cand)) return cand;
                            }
                            var icon = appKey.GetValue("DisplayIcon")?.ToString();
                            if (!string.IsNullOrEmpty(icon))
                            {
                                var cleanIcon = icon.Trim('\"', ' ');
                                if (cleanIcon.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(cleanIcon))
                                    return cleanIcon;
                            }
                        }
                    }
                }
            }
        }
        catch { }

        return null;
    }

    /// <summary>
    /// 彻底终止所有 Claude 客户端进程
    /// </summary>
    public static async Task<bool> StopClaudeAsync()
    {
        var procs = GetClaudeProcesses();
        if (procs.Length == 0) return true;

        // 1. 先尝试通过 Process.Kill 终止
        foreach (var p in procs)
        {
            try { p.Kill(entireProcessTree: true); } catch { }
        }

        // 2. 强力调用 Windows taskkill 彻底杀死
        try
        {
            var psi = new ProcessStartInfo("taskkill", "/F /T /IM claude.exe")
            {
                CreateNoWindow = true,
                UseShellExecute = false
            };
            Process.Start(psi)?.WaitForExit(1500);
        }
        catch { }

        // 3. 等待进程完全退出（最多 3 秒）
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (DateTime.UtcNow < deadline)
        {
            if (GetClaudeProcesses().Length == 0) break;
            await Task.Delay(100);
        }

        foreach (var p in procs)
        {
            try { p.Dispose(); } catch { }
        }
        await Task.Delay(500);

        return GetClaudeProcesses().Length == 0;
    }

    /// <summary>
    /// 启动 Claude 桌面客户端
    /// </summary>
    public static void StartClaude()
    {
        // 1. 优先使用桌面快捷方式或已知路径
        var target = FindClaudeExecutableOrShortcut();
        if (!string.IsNullOrEmpty(target) && File.Exists(target))
        {
            var psi = new ProcessStartInfo(target)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(target) ?? ""
            };
            Process.Start(psi);
            return;
        }

        // 2. 尝试通过 Windows AppsFolder AUMID 启动（适用于 WindowsApps / MSIX 安装）
        try
        {
            var psi = new ProcessStartInfo($@"shell:AppsFolder\{ClaudeAppxId}")
            {
                UseShellExecute = true
            };
            Process.Start(psi);
            return;
        }
        catch { }

        // 3. 尝试协议启动 claude://
        try
        {
            Process.Start(new ProcessStartInfo("claude://") { UseShellExecute = true });
            return;
        }
        catch { }

        throw new FileNotFoundException("未检测到可启动的 Claude 桌面客户端或快捷方式。");
    }

    /// <summary>
    /// 彻底重启 Claude 桌面客户端
    /// </summary>
    public static async Task RestartClaudeAsync()
    {
        await StopClaudeAsync();
        StartClaude();
    }
}

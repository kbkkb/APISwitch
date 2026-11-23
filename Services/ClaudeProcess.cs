using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace APISwitch.Services;

public static class ClaudeProcess
{
    private const string ClaudeAppxId = "Claude_pzs8sxrjxfjjc!Claude";

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(IntPtr hProcess, int dwFlags, [Out] StringBuilder lpExeName, ref int lpdwSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(int processAccess, bool bInheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    /// <summary>
    /// 安全高效获取进程可执行文件完整路径（免除 32/64 位及部分权限异常）
    /// </summary>
    public static string? GetProcessPath(Process p)
    {
        try
        {
            return p.MainModule?.FileName;
        }
        catch
        {
            try
            {
                const int PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
                var handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, p.Id);
                if (handle != IntPtr.Zero)
                {
                    try
                    {
                        var sb = new StringBuilder(1024);
                        int size = sb.Capacity;
                        if (QueryFullProcessImageName(handle, 0, sb, ref size))
                            return sb.ToString();
                    }
                    finally
                    {
                        CloseHandle(handle);
                    }
                }
            }
            catch { }
        }
        return null;
    }

    /// <summary>
    /// 判断给定路径是否属于 Claude 桌面客户端安装目录
    /// </summary>
    public static bool IsClaudeDesktopPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var p = path.ToLowerInvariant();
        if (p.Contains("windowsapps") && p.Contains("claude")) return true;
        if (p.Contains("anthropicclaude")) return true;
        if (p.Contains(@"programs\claude\claude.exe") || p.Contains(@"program files\claude\claude.exe")) return true;
        return false;
    }

    /// <summary>
    /// 判断进程是否属于 Claude 桌面客户端（严格排除 Claude Code CLI）
    /// </summary>
    public static bool IsClaudeProcess(Process p)
    {
        try
        {
            var name = p.ProcessName.ToLowerInvariant();
            if (name != "claude") return false;

            var path = GetProcessPath(p);
            if (!string.IsNullOrEmpty(path))
            {
                if (IsClaudeDesktopPath(path)) return true;

                // 排除明确属于 CLI 的路径特征
                if (path.Contains(".local", StringComparison.OrdinalIgnoreCase) ||
                    path.Contains("claude-code", StringComparison.OrdinalIgnoreCase) ||
                    path.Contains(@"npm\node_modules", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            try
            {
                var desc = p.MainModule?.FileVersionInfo.FileDescription ?? "";
                var prod = p.MainModule?.FileVersionInfo.ProductName ?? "";
                if (desc.Equals("Claude Code", StringComparison.OrdinalIgnoreCase) || prod.Equals("Claude Code", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
                if (desc.IndexOf("Claude", StringComparison.OrdinalIgnoreCase) >= 0 || prod.IndexOf("Claude", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }
            catch { }

            return true;
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
    /// 获取当前所有正在运行的 Claude 桌面客户端及其 MCP/派生子进程的 PID 列表
    /// </summary>
    public static List<int> GetClaudeDesktopProcessIds()
    {
        var snapshot = ClaudeCliProcess.GetProcessSnapshot();
        var rootPids = new HashSet<int>();

        foreach (var proc in snapshot)
        {
            var name = proc.Name.ToLowerInvariant();
            if (name == "claude.exe" || name == "claude")
            {
                if (IsClaudeDesktopPath(proc.ExePath))
                {
                    rootPids.Add(proc.Pid);
                }
                else if (!proc.ExePath.Contains(".local", StringComparison.OrdinalIgnoreCase) &&
                         !proc.ExePath.Contains("claude-code", StringComparison.OrdinalIgnoreCase) &&
                         !proc.CommandLine.Contains("claude-code", StringComparison.OrdinalIgnoreCase))
                {
                    rootPids.Add(proc.Pid);
                }
            }
        }

        if (rootPids.Count == 0) return new List<int>();

        // 级联寻找所有由桌面客户端派生的子进程（包括 MCP 服务器、辅助子进程）
        var allPids = new HashSet<int>(rootPids);
        bool expanded;
        do
        {
            expanded = false;
            foreach (var proc in snapshot)
            {
                if (allPids.Contains(proc.ParentPid) && allPids.Add(proc.Pid))
                {
                    expanded = true;
                }
            }
        } while (expanded);

        return allPids.ToList();
    }

    /// <summary>
    /// 彻底安全终止所有 Claude 桌面客户端及其 MCP/派生子进程
    /// </summary>
    /// <returns>(是否成功, 终止的进程数)</returns>
    public static async Task<(bool Ok, int Count)> StopClaudeDesktopAsync()
    {
        var pids = GetClaudeDesktopProcessIds();
        if (pids.Count == 0) return (true, 0);

        int killed = 0;

        // 1. 先通过 Process.Kill 终止
        foreach (var pid in pids)
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                p.Kill(entireProcessTree: true);
                killed++;
            }
            catch { }
        }

        // 2. 强力调用 Windows taskkill 针对 PID 逐个树杀
        foreach (var pid in pids)
        {
            try
            {
                var psi = new ProcessStartInfo("taskkill", $"/F /T /PID {pid}")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false
                };
                Process.Start(psi)?.WaitForExit(1000);
            }
            catch { }
        }

        // 3. 针对桌面主可执行名 claude.exe 再做一次系统级兜底广播
        try
        {
            var psi = new ProcessStartInfo("taskkill", "/F /T /IM claude.exe")
            {
                CreateNoWindow = true,
                UseShellExecute = false
            };
            Process.Start(psi)?.WaitForExit(1000);
        }
        catch { }

        // 4. 等待进程完全退出（最多 3 秒）
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (DateTime.UtcNow < deadline)
        {
            if (GetClaudeProcesses().Length == 0) break;
            await Task.Delay(100);
        }

        return (GetClaudeProcesses().Length == 0, killed > 0 ? killed : pids.Count);
    }

    /// <summary>
    /// 彻底终止所有 Claude 客户端进程（保持向后兼容）
    /// </summary>
    public static async Task<bool> StopClaudeAsync()
    {
        var res = await StopClaudeDesktopAsync();
        return res.Ok;
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
     /// 彻底重启 Claude 桌面客户端。
     /// 走通用重启流程（优雅关闭 → 强杀残留 → 等待释放 → 启动并验证新进程），
     /// 返回真实结果，不再一味假定成功。
     /// </summary>
     public static Task<AppProcessRestart.Result> RestartClaudeAsync() =>
         AppProcessRestart.RestartAsync("Claude", GetClaudeProcesses, StartClaude, Log);
 
     static void Log(string msg) => App.LogStartup("ClaudeRestart: " + msg);
 }

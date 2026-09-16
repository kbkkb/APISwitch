using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace APISwitch.Services;

public static class CodexProcess
{
    private const string CodexAppxId = "OpenAI.Codex_2p2nqsd0c76g0!App";

    /// <summary>
    /// 判断进程是否属于 Codex（包括官方客户端 ChatGPT.exe、后端 codex.exe、cua、以及社区启动器 Codex++）
    /// </summary>
    public static bool IsCodexProcess(Process p)
    {
        try
        {
            var name = p.ProcessName.ToLowerInvariant();
            if (name.StartsWith("codex", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (name == "chatgpt")
            {
                try
                {
                    var path = p.MainModule?.FileName;
                    if (!string.IsNullOrEmpty(path) && path.Contains("OpenAI.Codex", StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
                catch { }

                try
                {
                    if (p.MainWindowTitle.IndexOf("codex", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return true;
                    }
                }
                catch { }
            }
        }
        catch { }
        return false;
    }

    /// <summary>
    /// 获取所有当前正在运行的 Codex 相关进程
    /// </summary>
    public static Process[] GetCodexProcesses()
    {
        var list = new List<Process>();
        foreach (var p in Process.GetProcesses())
        {
            if (IsCodexProcess(p))
            {
                list.Add(p);
            }
        }
        return list.ToArray();
    }

    public static bool IsRunning() => GetCodexProcesses().Length > 0;

    public static string? FindCodexExecutable()
    {
        // 1. 已知可执行文件路径（优先社区 Codex++ 启动器，因其负责代理注入与启动）
        var candidates = new[]
        {
            @"D:\Codex++\codex-plus-plus.exe",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Codex++", "codex-plus-plus.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Codex++", "codex-plus-plus.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Codex", "Codex.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenAI", "Codex", "Codex.exe"),
        };

        foreach (var c in candidates)
        {
            if (File.Exists(c)) return c;
        }

        // 2. 检查桌面快捷方式
        try
        {
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            var cppLnk = Path.Combine(desktop, "Codex++.lnk");
            if (File.Exists(cppLnk)) return cppLnk;

            var codexLnk = Path.Combine(desktop, "Codex.lnk");
            if (File.Exists(codexLnk)) return codexLnk;
        }
        catch { }

        // 3. 检查注册表卸载项
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
                        if (disp.IndexOf("codex", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            var loc = appKey.GetValue("InstallLocation")?.ToString();
                            if (!string.IsNullOrEmpty(loc))
                            {
                                var cand1 = Path.Combine(loc, "codex-plus-plus.exe");
                                if (File.Exists(cand1)) return cand1;
                                var cand2 = Path.Combine(loc, "Codex.exe");
                                if (File.Exists(cand2)) return cand2;
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
    /// 彻底终止所有 Codex 进程（包括官方 ChatGPT.exe 窗口进程、后端 codex.exe、cua 及相关工具）
    /// </summary>
    public static async Task<bool> StopCodexAsync()
    {
        var procs = GetCodexProcesses();
        if (procs.Length == 0) return true;

        var chatGptPids = procs
            .Where(p => p.ProcessName.Equals("ChatGPT", StringComparison.OrdinalIgnoreCase))
            .Select(p => p.Id)
            .ToList();

        // 1. 先尝试直接通过 Process.Kill 终止进程树
        foreach (var p in procs)
        {
            try { p.Kill(entireProcessTree: true); } catch { }
        }

        // 2. 强力调用 Windows 原生 taskkill 确保彻底杀死
        try
        {
            var psi = new ProcessStartInfo("taskkill", "/F /T /IM codex.exe /IM codex-computer-use-swift.exe /IM codex-plus-plus.exe /IM codex-plus-plus-manager.exe")
            {
                CreateNoWindow = true,
                UseShellExecute = false
            };
            Process.Start(psi)?.WaitForExit(1500);
        }
        catch { }

        if (chatGptPids.Count > 0)
        {
            var pidArgs = string.Join(" ", chatGptPids.Select(id => $"/PID {id}"));
            try
            {
                var psi = new ProcessStartInfo("taskkill", $"/F /T {pidArgs}")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false
                };
                Process.Start(psi)?.WaitForExit(1500);
            }
            catch { }
        }

        // 3. 循环检测直到所有相关进程完全退出（最多 3 秒）
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (DateTime.UtcNow < deadline)
        {
            if (GetCodexProcesses().Length == 0) break;
            await Task.Delay(100);
        }

        // 4. 清理句柄并等待 500ms，确保操作系统释放网络端口、命名管道和配置锁
        foreach (var p in procs)
        {
            try { p.Dispose(); } catch { }
        }
        await Task.Delay(500);

        return GetCodexProcesses().Length == 0;
    }

    /// <summary>
    /// 启动 Codex 客户端
    /// </summary>
    public static void StartCodex()
    {
        // 1. 优先使用检测到的可执行文件或桌面快捷方式
        var target = FindCodexExecutable();
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

        // 2. 尝试启动 Windows 官方商店应用 (OpenAI.Codex)
        try
        {
            var psi = new ProcessStartInfo("explorer.exe", $@"shell:AppsFolder\{CodexAppxId}")
            {
                UseShellExecute = false,
                CreateNoWindow = true
            };
            Process.Start(psi);
            return;
        }
        catch { }

        // 3. 尝试协议启动 codex://
        try
        {
            Process.Start(new ProcessStartInfo("codex://") { UseShellExecute = true });
            return;
        }
        catch { }

        throw new FileNotFoundException("未检测到可启动的 Codex 客户端或快捷方式。");
    }

    /// <summary>
    /// 彻底重启 Codex 客户端
    /// </summary>
    public static async Task RestartCodexAsync()
    {
        await StopCodexAsync();
        StartCodex();
    }
}

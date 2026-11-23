using System.Diagnostics;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;

namespace APISwitch.Services;

public static class ClaudeCliProcess
{
    public class ProcessItem
    {
        public int Pid { get; set; }
        public int ParentPid { get; set; }
        public string Name { get; set; } = "";
        public string ExePath { get; set; } = "";
        public string CommandLine { get; set; } = "";
    }

    /// <summary>
    /// 获取当前系统所有进程的快照（PID、父PID、名称、路径、命令行）
    /// 优先使用 WMI 高效检索，若 WMI 受限或服务异常则平滑回退至 Process.GetProcesses()
    /// </summary>
    public static List<ProcessItem> GetProcessSnapshot()
    {
        var list = new List<ProcessItem>();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT ProcessId, ParentProcessId, Name, ExecutablePath, CommandLine FROM Win32_Process");
            using var collection = searcher.Get();
            foreach (ManagementObject obj in collection)
            {
                var pid = Convert.ToInt32(obj["ProcessId"] ?? 0);
                var parentPid = Convert.ToInt32(obj["ParentProcessId"] ?? 0);
                var name = obj["Name"]?.ToString() ?? "";
                var exePath = obj["ExecutablePath"]?.ToString() ?? "";
                var cmdLine = obj["CommandLine"]?.ToString() ?? "";
                list.Add(new ProcessItem
                {
                    Pid = pid,
                    ParentPid = parentPid,
                    Name = name,
                    ExePath = exePath,
                    CommandLine = cmdLine
                });
            }
            if (list.Count > 0) return list;
        }
        catch { }

        // Fallback: 如果 WMI 不可用，通过 Process.GetProcesses() 兜底
        try
        {
            foreach (var p in Process.GetProcesses())
            {
                string path = "";
                try { path = p.MainModule?.FileName ?? ""; } catch { }
                list.Add(new ProcessItem
                {
                    Pid = p.Id,
                    ParentPid = 0,
                    Name = p.ProcessName + ".exe",
                    ExePath = path,
                    CommandLine = ""
                });
            }
        }
        catch { }

        return list;
    }

    /// <summary>
    /// 判断单个进程是否属于 Claude Code (CLI) 根进程
    /// </summary>
    public static bool IsClaudeCliRootProcess(ProcessItem item)
    {
        var name = item.Name.ToLowerInvariant();
        var exe = item.ExePath.ToLowerInvariant();
        var cmd = item.CommandLine.ToLowerInvariant();

        // 1. 如果进程名是 claude.exe 或 claude
        if (name == "claude.exe" || name == "claude")
        {
            // 严防误杀：如果是 Claude Desktop 客户端，排除
            if (ClaudeProcess.IsClaudeDesktopPath(item.ExePath))
            {
                return false;
            }

            // 匹配 CLI 特征
            if (exe.Contains(@".local\bin") ||
                exe.Contains("claude-code") ||
                exe.Contains("npm") ||
                cmd.Contains("claude-code") ||
                cmd.Contains("@anthropic-ai"))
            {
                return true;
            }

            // 若非明确 Desktop 路径，通常为 CLI 可执行文件
            return true;
        }

        // 2. 如果是 node.exe 或 bun.exe 启动的 claude-code CLI 实例
        if (name == "node.exe" || name == "node" || name == "bun.exe" || name == "bun")
        {
            if (cmd.Contains("@anthropic-ai/claude-code") ||
                cmd.Contains("claude-code") ||
                cmd.Contains("claude.js") ||
                cmd.Contains(@"\bin\claude") ||
                cmd.Contains("/bin/claude") ||
                cmd.Contains(@"npm\claude"))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 获取当前所有正在运行的 Claude Code (CLI) 及其派生子进程的 PID 列表
    /// </summary>
    public static List<int> GetClaudeCliProcessIds()
    {
        var snapshot = GetProcessSnapshot();
        var rootPids = new HashSet<int>();

        foreach (var proc in snapshot)
        {
            if (IsClaudeCliRootProcess(proc))
            {
                rootPids.Add(proc.Pid);
            }
        }

        if (rootPids.Count == 0) return new List<int>();

        // 级联寻找所有派生的子进程（包括 CLI 工具唤起的 bash、dev server、工作进程等）
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

    public static bool IsRunning() => GetClaudeCliProcessIds().Count > 0;

    /// <summary>
    /// 彻底安全终止所有 Claude Code (CLI) 及其派生子进程
    /// </summary>
    /// <returns>(是否成功, 终止的进程数)</returns>
    public static async Task<(bool Ok, int Count)> StopClaudeCliAsync()
    {
        var pids = GetClaudeCliProcessIds();
        if (pids.Count == 0) return (true, 0);

        int killed = 0;

        // 1. 先通过 Process.Kill(entireProcessTree: true) 逐个级联清理
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

        // 2. 强力调用 Windows taskkill 兜底确保释放锁与子进程
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

        // 3. 等待进程完全退出（最多 3 秒）
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (DateTime.UtcNow < deadline)
        {
            var remaining = GetClaudeCliProcessIds();
            if (remaining.Count == 0) break;
            await Task.Delay(100);
        }

        var finalRemaining = GetClaudeCliProcessIds();
        return (finalRemaining.Count == 0, killed > 0 ? killed : pids.Count);
    }
}

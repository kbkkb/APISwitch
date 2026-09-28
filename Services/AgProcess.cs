using System.Diagnostics;
using System.IO;

namespace APISwitch.Services;

public static class AgProcess
{
    public static Process[] GetIdeProcesses()
    {
        var exe = AgPaths.FindIdeExecutable();
        var dir = exe != null ? Path.GetDirectoryName(exe) : null;
        var all = Process.GetProcesses();
        var result = new List<Process>();
        foreach (var p in all)
        {
            if (!p.ProcessName.StartsWith("Antigravity", StringComparison.OrdinalIgnoreCase))
            {
                p.Dispose();
                continue;
            }
            if (dir == null)
            {
                result.Add(p);
                continue;
            }
            var path = SafePath(p);
            if (path != null && path.StartsWith(dir, StringComparison.OrdinalIgnoreCase))
                result.Add(p);
            else
                p.Dispose();
        }
        return result.ToArray();
    }

    public static bool IsRunning() => GetIdeProcesses().Length > 0;

    static string? SafePath(Process p)
    {
        try { return p.MainModule?.FileName; } catch { return null; }
    }

    public static async Task<bool> StopIdeAsync()
    {
        var procs = GetIdeProcesses();
        if (procs.Length == 0) return true;

        foreach (var p in procs)
        {
            try { p.CloseMainWindow(); } catch { }
        }

        var deadline = DateTime.UtcNow.AddSeconds(6);
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(300);
            var poll = GetIdeProcesses();
            bool anyAlive = poll.Any(p => !p.HasExited);
            foreach (var p in poll) p.Dispose();
            if (!anyAlive) break;
        }

        var kill = GetIdeProcesses();
        foreach (var p in kill)
        {
            try { p.Kill(entireProcessTree: true); } catch { }
        }
        foreach (var p in kill) p.Dispose();

        await Task.Delay(400);
        foreach (var p in procs) p.Dispose();
        var final = GetIdeProcesses();
        bool ok = final.Length == 0;
        foreach (var p in final) p.Dispose();
        return ok;
    }

    public static void StartIde()
    {
        var exe = AgPaths.FindIdeExecutable();
        if (exe == null) throw new FileNotFoundException("找不到 Antigravity.exe");
        Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
    }
}

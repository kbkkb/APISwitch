using System.Diagnostics;
using System.IO;

namespace APISwitch.Services;

public static class AgProcess
{
    public static Process[] GetIdeProcesses()
    {
        var exe = AgPaths.FindIdeExecutable();
        var dir = exe != null ? Path.GetDirectoryName(exe) : null;
        return Process.GetProcesses()
            .Where(p => p.ProcessName.StartsWith("Antigravity", StringComparison.OrdinalIgnoreCase))
            .Where(p =>
            {
                if (dir == null) return true;
                var path = SafePath(p);
                return path != null && path.StartsWith(dir, StringComparison.OrdinalIgnoreCase);
            })
            .ToArray();
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
            if (!GetIdeProcesses().Any(p => !p.HasExited)) break;
        }

        foreach (var p in GetIdeProcesses())
        {
            try { p.Kill(entireProcessTree: true); } catch { }
        }

        await Task.Delay(400);
        foreach (var p in procs) p.Dispose();
        return GetIdeProcesses().Length == 0;
    }

    public static void StartIde()
    {
        var exe = AgPaths.FindIdeExecutable();
        if (exe == null) throw new FileNotFoundException("找不到 Antigravity.exe");
        Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
    }
}

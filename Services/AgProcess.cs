using System.Diagnostics;
using System.IO;

namespace APISwitch.Services;

/// <summary>
/// Antigravity IDE（VSCode/Electron 分支）的进程控制。
///
/// 白屏根因：Electron 应用收到 CloseMainWindow 后往往不会立刻退出
/// （可能弹出确认框、或只是关窗口而进程常驻），于是老实现几乎必然走到
/// Kill(entireProcessTree)，但之后只等 400ms 就开始写 state.vscdb 并启动新实例。
/// 此刻旧进程（GPU / 渲染 / 扩展宿主 / crashpad）还活着并握着 SQLite 库锁与单例锁，
/// 新实例要么读到半提交状态、要么被单例锁挡掉，workbench 起不来就是白屏。
/// 用户"手动重启没问题"是因为自然关闭会等好几秒，锁全部释放。
/// </summary>
public static class AgProcess
{
    /// <summary>优雅关闭的最长等待时间。</summary>
    private static readonly TimeSpan GracefulTimeout = TimeSpan.FromSeconds(12);

    /// <summary>强杀后等待进程树彻底消失的最长时间。</summary>
    private static readonly TimeSpan KillTimeout = TimeSpan.FromSeconds(10);

    /// <summary>SQLite 状态库释放的最短沉淀时间（覆盖文件锁/句柄回收）。</summary>
    private static readonly TimeSpan MinSettle = TimeSpan.FromSeconds(2);

    /// <summary>等待 -wal/-shm 消失的最长时间。</summary>
    private static readonly TimeSpan WalWait = TimeSpan.FromSeconds(6);

    /// <summary>等待新 IDE 出现主窗口的最长时间（Electron 冷启动可能较慢）。</summary>
    private static readonly TimeSpan WindowWait = TimeSpan.FromSeconds(60);

    public static Process[] GetIdeProcesses()
    {
        var all = Process.GetProcesses();
        var result = new List<Process>();
        foreach (var p in all)
        {
            if (!p.ProcessName.StartsWith("Antigravity", StringComparison.OrdinalIgnoreCase))
            {
                p.Dispose();
                continue;
            }

            // 只按进程名匹配，不按可执行文件目录过滤。
            // 原因：机器上可能同时存在多个 Antigravity 安装副本
            // （实测有两份完全相同的：Programs\antigravity 与 Programs\Antigravity），
            // 且快捷方式可能指向其中任意一个。若按 FindIdeExecutable() 返回的那一份
            // 目录过滤，从另一份启动的进程会被漏掉 → APISwitch 误判"IDE 没在运行" →
            // 不关闭就直接启动新实例 → 撞单例锁 → 白屏。"Antigravity" 这个进程名
            // 足够独特，按名匹配既安全又不会漏。
            result.Add(p);
        }
        return result.ToArray();
    }
    public static bool IsRunning() => GetIdeProcesses().Length > 0;

    /// <summary>
    /// 彻底关闭 IDE，并确保 SQLite 状态库的锁已释放。
    /// 返回 true 表示所有相关进程都已退出。
    /// </summary>
    public static async Task<bool> StopIdeAsync(Action<string>? log = null)
    {
        var initial = GetIdeProcesses();
        if (initial.Length == 0) return true;

        // 1. 优雅关闭：只对有窗口的进程发关闭消息（其余是渲染/GPU 子进程，发了也无意义）
        foreach (var p in initial)
        {
            try
            {
                if (p.MainWindowHandle != IntPtr.Zero)
                    p.CloseMainWindow();
            }
            catch { }
        }
        DisposeAll(initial);

        var exited = await WaitAllGoneAsync(GracefulTimeout);
        log?.Invoke($"ag-stop: graceful close, allGone={exited}");

        // 2. 仍有残留才强杀进程树
        if (!exited)
        {
            var kill = GetIdeProcesses();
            foreach (var p in kill)
            {
                try { p.Kill(entireProcessTree: true); } catch { }
            }
            DisposeAll(kill);
            exited = await WaitAllGoneAsync(KillTimeout);
            log?.Invoke($"ag-stop: force kill, allGone={exited}");
        }

        // 3. 等状态库锁真正释放：最短沉淀 + 等 -wal/-shm 消失
        await WaitStateDbSettledAsync(log);

        var final = GetIdeProcesses();
        var ok = final.Length == 0;
        DisposeAll(final);
        return ok;
    }

    /// <summary>启动 IDE，并验证新实例真的出现了主窗口（而不是被单例锁挡掉后静默退出）。</summary>
    public static async Task<bool> StartIdeAndWaitAsync(Action<string>? log = null)
    {
        Process.Start(new ProcessStartInfo(AgPaths.FindIdeExecutable() ?? throw new FileNotFoundException("找不到 Antigravity.exe"))
        {
            UseShellExecute = true,
        });

        var deadline = DateTime.UtcNow + WindowWait;
        int lastCount = 0;
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(500);

            var procs = GetIdeProcesses();
            int alive = 0;
            bool hasWindow = false;
            foreach (var p in procs)
            {
                try
                {
                    if (p.HasExited) continue;
                    alive++;
                    if (p.MainWindowHandle != IntPtr.Zero) hasWindow = true;
                }
                catch { }
            }
            DisposeAll(procs);

            if (alive > 0 && hasWindow)
            {
                log?.Invoke($"ag-start: main window appeared (alive={alive})");
                return true;
            }

            // 进程起来了但窗口还没出来，继续等；日志只记一次变化，避免刷屏
            if (alive != lastCount)
            {
                lastCount = alive;
                log?.Invoke($"ag-start: alive={alive}, waiting for main window");
            }
        }

        log?.Invoke("ag-start: TIMEOUT waiting for main window");
        return false;
    }

    public static void StartIde()
    {
        var exe = AgPaths.FindIdeExecutable();
        if (exe == null) throw new FileNotFoundException("找不到 Antigravity.exe");
        Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
    }

    /// <summary>完整重启：关闭（含锁释放）→ 启动 → 验证窗口出现。</summary>
    public static async Task<bool> RestartIdeAsync(Action<string>? log = null)
    {
        var stopped = await StopIdeAsync(log);
        if (!stopped)
        {
            log?.Invoke("ag-restart: IDE did not fully exit, aborting");
            return false;
        }
        return await StartIdeAndWaitAsync(log);
    }

    /// <summary>轮询直到所有 Antigravity 进程退出。</summary>
    static async Task<bool> WaitAllGoneAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var current = GetIdeProcesses();
            bool anyAlive = false;
            foreach (var p in current)
            {
                try { if (!p.HasExited) anyAlive = true; } catch { }
            }
            DisposeAll(current);
            if (!anyAlive) return true;
            await Task.Delay(200);
        }
        return false;
    }

    /// <summary>
    /// 等待 SQLite 状态库释放。最短沉淀 MinSettle；
    /// 若 -wal/-shm 仍在（说明库还没干净关闭），最多再等 WalWait。
    /// </summary>
    static async Task WaitStateDbSettledAsync(Action<string>? log)
    {
        await Task.Delay(MinSettle);

        var db = AgPaths.FindStateDb();
        if (db == null) return;

        var deadline = DateTime.UtcNow + WalWait;
        while (DateTime.UtcNow < deadline)
        {
            bool hot = File.Exists(db + "-wal") || File.Exists(db + "-shm");
            if (!hot)
            {
                log?.Invoke("ag-stop: state db clean (no -wal/-shm)");
                return;
            }
            await Task.Delay(300);
        }
        log?.Invoke("ag-stop: state db still has -wal/-shm, proceeding (SQLite will recover)");
    }

    static void DisposeAll(Process[] procs)
    {
        foreach (var p in procs)
        {
            try { p.Dispose(); } catch { }
        }
    }
}

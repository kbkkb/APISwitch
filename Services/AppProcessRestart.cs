using System.Diagnostics;
using System.Threading.Tasks;

namespace APISwitch.Services;

/// <summary>
/// 桌面客户端（Codex / Claude 等）的通用重启流程。
///
/// 背景：这两个客户端都是 MSIX 打包的 Electron 应用（装在 WindowsApps 下，
/// 一个应用有 9+ 个同名进程：主进程 / 渲染进程 / GPU / 工具进程）。
/// 直接 taskkill 强杀会留下单例锁与 MSIX 激活状态，新实例启动后静默退出，
/// 表现为"点了重启、提示成功，但客户端根本没起来"。
///
/// 正确流程（与能正常工作的 Antigravity 重启一致）：
///   1. 优雅关闭：只对有窗口的进程发关闭消息，让应用自己走退出路径；
///   2. 等它自己退出；仍有残留才强制杀进程树；
///   3. 留足时间让系统释放单例锁 / 命名管道 / MSIX 激活（500ms 不够，实测要 1s 以上）；
///   4. 启动，然后**验证新进程真的出现**，没出现就重试一次；
///   5. 返回真实结果，由调用方决定提示成功还是失败——绝不一味显示"已重启"。
/// </summary>
public static class AppProcessRestart
{
    /// <summary>重启结果。Ok=false 时 Message 说明原因。</summary>
    public sealed record Result(bool Ok, string Reason);

    /// <summary>
    /// 执行完整重启。
    /// </summary>
    /// <param name="displayName">界面显示名（如 "Claude"）</param>
     /// <param name="getProcesses">
     /// 枚举该客户端全部相关进程。契约：每次调用必须返回"新建"的 Process 对象
     /// （例如 Process.GetProcessesByName 的结果），本方法会释放它枚举到的对象。
     /// 不要传入你自己长期持有、后续还要使用的 Process 对象——那会被本方法释放掉。
     /// </param>
    /// <param name="start">启动动作</param>
    /// <param name="log">日志回调，可为 null</param>
    public static async Task<Result> RestartAsync(
        string displayName,
        Func<Process[]> getProcesses,
        Action start,
        Action<string>? log = null)
    {
        // ---------- 1. 优雅关闭 ----------
        // 只对有窗口的进程发关闭消息。无窗口的是渲染/GPU 子进程，
        // 对它们发 CloseMainWindow 无效，交给后面的进程树强杀。
        var initial = getProcesses();
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

        // ---------- 2. 等待自行退出 ----------
        var gone = await WaitUntilGoneAsync(getProcesses, TimeSpan.FromSeconds(6));
        log?.Invoke($"{displayName}: graceful close done, allGone={gone}");

        // ---------- 3. 仍有残留才强杀 ----------
        if (!gone)
        {
            var remaining = getProcesses();
            foreach (var p in remaining)
            {
                try { p.Kill(entireProcessTree: true); } catch { }
            }
            DisposeAll(remaining);
            gone = await WaitUntilGoneAsync(getProcesses, TimeSpan.FromSeconds(5));
            log?.Invoke($"{displayName}: force kill done, allGone={gone}");
        }

        // ---------- 4. 让系统释放单例锁 / MSIX 激活 ----------
        // 关键：500ms 对 Electron + MSIX 经常不够，这是"有时起不来"的主因。
        await Task.Delay(TimeSpan.FromSeconds(1));

        // ---------- 5. 启动并验证新进程真的出现 ----------
         var existing = CollectPids(getProcesses());

        for (int attempt = 1; attempt <= 2; attempt++)
        {
            try
            {
                start();
            }
            catch (Exception ex)
            {
                log?.Invoke($"{displayName}: start attempt {attempt} threw: {ex.Message}");
                if (attempt == 2) return new Result(false, ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(1));
                continue;
            }

            var newPid = await WaitForNewProcessAsync(getProcesses, existing, TimeSpan.FromSeconds(12));
            log?.Invoke($"{displayName}: start attempt {attempt}, newProcess={(newPid?.ToString() ?? "none")}");

            if (newPid != null)
                return new Result(true, "");

            // 没起来：可能启动器本身异步，再给它一次机会
            if (attempt == 1)
                await Task.Delay(TimeSpan.FromSeconds(1));
        }

        return new Result(false, "no-new-process");
    }

    /// <summary>轮询直到相关进程全部退出。</summary>
    static async Task<bool> WaitUntilGoneAsync(Func<Process[]> getProcesses, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var current = getProcesses();
            bool anyAlive = false;
            foreach (var p in current)
            {
                try { if (!p.HasExited) anyAlive = true; } catch { }
            }
            DisposeAll(current);
            if (!anyAlive) return true;
            await Task.Delay(150);
        }
        return false;
    }

    /// <summary>收集当前进程 PID 集合（用于判断"是否出现了新进程"）。</summary>
    static HashSet<int> CollectPids(Process[] procs)
    {
        var set = new HashSet<int>();
        foreach (var p in procs)
        {
            try { if (!p.HasExited) set.Add(p.Id); } catch { }
            try { p.Dispose(); } catch { }
        }
        return set;
    }

    /// <summary>轮询直到出现一个 PID 不在 existing 里的新进程。</summary>
    static async Task<int?> WaitForNewProcessAsync(Func<Process[]> getProcesses, HashSet<int> existing, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var current = getProcesses();
            foreach (var p in current)
            {
                int id;
                try
                {
                    if (p.HasExited) { p.Dispose(); continue; }
                    id = p.Id;
                }
                catch { p.Dispose(); continue; }

                if (!existing.Contains(id))
                {
                    DisposeAll(current);
                    return id;
                }
            }
            DisposeAll(current);
            await Task.Delay(200);
        }
        return null;
    }

    static void DisposeAll(Process[] procs)
    {
        foreach (var p in procs)
        {
            try { p.Dispose(); } catch { }
        }
    }
}

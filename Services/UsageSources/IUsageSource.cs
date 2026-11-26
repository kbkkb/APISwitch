using System;

namespace APISwitch.Services;

/// <summary>
/// 用量数据源适配器。每个 CLI agent（StepCode / zcode / dsh …）实现一个适配器，
/// UsageScanner 只面向接口调度，新增 agent 不需要改扫描主逻辑。
///
/// 设计约束：
/// 1. 数据源不存在时 Scan() 必须返回 0，不得抛异常——用户机器上大部分 agent
///    根本没安装，逐个探测目录是常态，抛异常会把整轮扫描炸掉。
/// 2. 单条记录解析失败只丢掉那一条，不影响同一批里的其他记录。
/// 3. 增量水位由 <see cref="UsageScanContext"/> 统一托管，适配器不自己存状态。
/// </summary>
public interface IUsageSource
{
    /// <summary>入库用的 agent 标识（对应 UsageStorage.Agent* 常量）。</summary>
    string AgentId { get; }

    /// <summary>执行一次增量扫描，返回新增入库的记录条数。</summary>
    int Scan(UsageScanContext ctx);
}

/// <summary>
/// 适配器基类：包一层 try-catch 作为最后防线，并提供水位读写与记录用量
/// 的统一出口，避免每个适配器各写一遍偏移操作和 RecordUsage 参数。
///
/// 上下文是按次扫描传入的（而非构造时捕获），因为 UsageScanner 在回填/重置时
/// 会整体替换偏移字典——捕获引用会拿到 stale 字典，导致回填后增量水位错乱。
/// </summary>
public abstract class UsageSourceBase : IUsageSource
{
    protected UsageScanContext _ctx = null!;

    public abstract string AgentId { get; }

    public int Scan(UsageScanContext ctx)
    {
        _ctx = ctx;
        try
        {
            return ScanCore();
        }
        catch
        {
            // 兜底：任何未预期的异常都不能冒泡到 ScanAll，否则会中断其余 agent 的扫描
            return 0;
        }
    }

    protected abstract int ScanCore();

    /// <summary>读取指定键的增量水位（不存在返回 0）。</summary>
    protected long GetWatermark(string key) => _ctx.GetOffset(key);

    /// <summary>推进指定键的增量水位。</summary>
    protected void SetWatermark(string key, long value) => _ctx.SetOffset(key, value);

    /// <summary>按文件路径读取字节偏移水位（JSONL 类数据源用）。</summary>
    protected long GetFileOffset(string file) => _ctx.GetOffset("file:" + file);

    /// <summary>按文件路径推进字节偏移水位。</summary>
    protected void SetFileOffset(string file, long pos) => _ctx.SetOffset("file:" + file, pos);

    /// <summary>读取指定文件上次持久化的当前模型名（防止增量 Seek 跳过文件头丢失模型）。</summary>
    protected string GetFileModel(string file) => _ctx.GetModel(file);

    /// <summary>持久化指定文件的当前模型名。</summary>
    protected void SetFileModel(string file, string model) => _ctx.SetModel(file, model);

    /// <summary>记录一条用量。</summary>
    protected void Record(string model, long inTokens, long outTokens, DateTime ts,
        long cacheRead = 0, long cacheWrite = 0, long durationMs = 0, long ttftMs = 0,
        string providerId = "", string providerName = "", string sourceId = "")
        => UsageStorage.RecordUsage(AgentId, providerId, providerName, model,
            inTokens, outTokens, ts, cacheRead, cacheWrite, durationMs, ttftMs, sourceId);

    /// <summary>批量记录一组用量（使用单事务批处理）。返回是否成功提交。</summary>
    protected bool RecordBatch(IEnumerable<UsageStorage.UsageRecord> records)
        => UsageStorage.RecordBatch(records);
}

/// <summary>
/// 扫描上下文：把 UsageScanner 的偏移水位与锁透传给适配器。
/// 每次 ScanAll() 新建一个实例，保证拿到的是当期字典而非被替换后的 stale 引用。
/// </summary>
public sealed class UsageScanContext
{
    private readonly Dictionary<string, long> _offsets;
    private readonly Dictionary<string, string>? _fileModels;
    private readonly object _lock;

    internal UsageScanContext(Dictionary<string, long> offsets, object scanLock, Dictionary<string, string>? fileModels = null)
    {
        _offsets = offsets;
        _lock = scanLock;
        _fileModels = fileModels;
    }

    internal long GetOffset(string key)
    {
        lock (_lock)
        {
            return _offsets.TryGetValue(key, out var v) ? v : 0;
        }
    }

    internal void SetOffset(string key, long value)
    {
        lock (_lock)
        {
            _offsets[key] = value;
        }
    }

    internal string GetModel(string file)
    {
        if (_fileModels == null) return "";
        lock (_lock)
        {
            return _fileModels.TryGetValue(file, out var m) ? m : "";
        }
    }

    internal void SetModel(string file, string model)
    {
        if (_fileModels == null || string.IsNullOrEmpty(model)) return;
        lock (_lock)
        {
            _fileModels[file] = model;
        }
    }
}

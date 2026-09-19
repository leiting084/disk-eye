namespace DiskEye.Etw;

/// <summary>DrainWrites 产出的一条 1 秒聚合写入：某 pid 对某路径的累计字节。</summary>
internal readonly record struct WrSample(int Pid, string DosPath, long Bytes);

/// <summary>
/// 子进程侧 FileObject 句柄状态机（纯逻辑，无 TraceEvent/IO 依赖，可单测）。
///
/// 内核语义（2026-09-15 实测）：FileIo/Create(ID12) 为每次 open 分配唯一 FileObject，
/// 它在内核里唯一对应"某进程对某文件的一次打开"，故 FileObject→pid/路径在句柄存活期恒定，
/// FileIo/Write(ID16) 凭 FileObject 即可反查写者——不受任何秒级 TTL 限制，这是长句柄归因的解。
///
/// 路径→最后属主的 LRU 归主进程 AttributionEngine（它能同时看到 FSW 生命周期）；
/// 本类只负责活跃句柄表与写字节聚合。
///
/// 线程模型：ETW 回调线程调 On*，1 秒定时器线程调 DrainWrites，全部走同一把锁，临界区微秒级。
/// </summary>
internal sealed class HandleStateTable
{
    private sealed class Handle
    {
        public required ulong Obj;
        public required int Pid;
        public required string DosPath;
        public required bool IsDir;
        public long WriteBytes;
        public bool Dirty;
    }

    private readonly Dictionary<ulong, Handle> _active = new();
    private readonly object _gate = new();

    /// <summary>FileIo/Create：登记/刷新一次打开。目录句柄也登记（供主进程过滤目录噪音）。</summary>
    public void OnCreate(ulong fileObject, int pid, string dosPath, bool isDir)
    {
        if (fileObject == 0 || pid <= 0 || string.IsNullOrEmpty(dosPath)) return;
        lock (_gate)
        {
            _active[fileObject] = new Handle
            {
                Obj = fileObject,
                Pid = pid,
                DosPath = dosPath,
                IsDir = isDir,
            };
        }
    }

    /// <summary>FileIo/Write：按句柄累加字节并标脏。FileObject 未知（漏 Create）返回 false。</summary>
    public bool OnWrite(ulong fileObject, uint bytes)
    {
        if (fileObject == 0 || bytes == 0) return false;
        lock (_gate)
        {
            if (!_active.TryGetValue(fileObject, out var h)) return false;
            h.WriteBytes += bytes;
            h.Dirty = true;
            return true;
        }
    }

    /// <summary>
    /// 1 秒聚合：取走所有脏句柄的累计字节并清零，返回 (pid, 路径, 字节) 列表。
    /// 二次调用（无新写入）返回空列表。
    /// </summary>
    public List<WrSample> DrainWrites()
    {
        var list = new List<WrSample>();
        lock (_gate)
        {
            foreach (var h in _active.Values)
            {
                if (!h.Dirty || h.WriteBytes <= 0) continue;
                list.Add(new WrSample(h.Pid, h.DosPath, h.WriteBytes));
                h.WriteBytes = 0;
                h.Dirty = false;
            }
        }
        return list;
    }

    /// <summary>FileIo/Cleanup：移除句柄，返回 (pid,路径,是否目录,残留写字节)。
    /// 短命句柄（1 秒聚合窗内写+关）的残留字节必须冲刷，否则丢字节。</summary>
    public (int Pid, string DosPath, bool IsDir, long ResidualBytes)? OnCleanup(ulong fileObject) => Remove(fileObject);

    /// <summary>FileIo/Close：兜底移除（Cleanup 通常先到，残留一般为 0）。</summary>
    public (int Pid, string DosPath, bool IsDir, long ResidualBytes)? OnClose(ulong fileObject) => Remove(fileObject);

    private (int, string, bool, long)? Remove(ulong fileObject)
    {
        if (fileObject == 0) return null;
        lock (_gate)
        {
            if (!_active.Remove(fileObject, out var h)) return null;
            long residual = h.Dirty ? h.WriteBytes : 0;
            return (h.Pid, h.DosPath, h.IsDir, residual);
        }
    }

    /// <summary>FileIo/Delete(ID18) / Rename(ID19)：凭 FileObject 反查 (pid, 路径)。句柄已关返回 null。</summary>
    public (int Pid, string DosPath, bool IsDir)? Lookup(ulong fileObject)
    {
        lock (_gate)
        {
            if (!_active.TryGetValue(fileObject, out var h)) return null;
            return (h.Pid, h.DosPath, h.IsDir);
        }
    }

    /// <summary>当前活跃句柄数（诊断/ST 帧用）。</summary>
    public int ActiveCount { get { lock (_gate) return _active.Count; } }
}

using DiskEye.Etw;
using DiskEye.Models;

namespace DiskEye.Monitoring;

/// <summary>归因结果类别（同时决定 events.source 的取值）。</summary>
public enum AttrResultKind
{
    /// <summary>命中 FileIo/Write 的最近写者（最强信号）。</summary>
    Writer,
    /// <summary>命中活跃打开者。</summary>
    Opener,
    /// <summary>句柄已关，命中 30 分钟路径→最后属主 LRU。</summary>
    Recent,
    /// <summary>命中 FileIo/Delete 删除帧。</summary>
    DeletedFrame,
    /// <summary>改名事件，属主取自旧路径。</summary>
    Renamed,
    /// <summary>暂未取得属主（≤1 秒等待后仍可能回填）。</summary>
    Pending,
    /// <summary>目标是目录，调用方应丢弃不入库。</summary>
    Dropped,
}

public sealed record AttrResult(AttrResultKind Kind, int Pid, string Name, string ExePath, string? SourceOverride = null)
{
    /// <summary>写入 events.source 的稳定标识。</summary>
    public string Source => SourceOverride ?? Kind switch
    {
        AttrResultKind.Writer => "v2-writer",
        AttrResultKind.Opener => "v2-opener",
        AttrResultKind.Recent => "v2-recent",
        AttrResultKind.DeletedFrame => "v2-delete",
        AttrResultKind.Renamed => "v2-rename",
        _ => "pending",
    };

    public static readonly AttrResult PendingResult = new(AttrResultKind.Pending, 0, "unknown", "");
    public static readonly AttrResult DroppedResult = new(AttrResultKind.Dropped, 0, "unknown", "");
}

/// <summary>pending 行在属主晚到时回填：行 id + 归因结果。</summary>
public sealed record PendingBackfill(long RowId, AttrResult Result);

/// <summary>按需把 pid 解析为名字（生产侧用进程缓存/Process API；测试可注入桩）。</summary>
public interface INameResolver
{
    (string Name, string ExePath) Resolve(int pid);
}

/// <summary>
/// 主进程归因引擎（纯逻辑，不碰 SQLite/ETW/WinForms）。
/// 吃 ETW 帧维护"路径→属主"状态，FSW 事件来时给出 pid+名字。
/// v2 = FileObject 状态机（写者权威 + 活跃打开者 + 30 分钟 recent + 删除帧 + 改名迁移 + 目录集）；
/// v1 = 仅打开帧 4 秒 TTL 先到先得（复刻 v0.8.1，供 config 回退）。
/// </summary>
public sealed class AttributionEngine
{
    public event Action<WriteSample>? OnWriteBytes;
    public event Action<List<PendingBackfill>>? OnBackfill;

    private sealed class Owner
    {
        public int OpenerPid;
        public int WriterPid;
        public string OpenerName = "";
        public string WriterName = "";
    }

    private readonly bool _v2;
    private readonly Func<long> _now;
    private readonly INameResolver? _names;

    // v2 状态
    private readonly Dictionary<string, Owner> _active = new();
    private readonly KeyLru<string, (int Pid, string Name)> _recent;
    private readonly Dictionary<string, (int Pid, long ExpireMs)> _deleteClaims = new();
    private readonly Dictionary<string, long> _dirs = new();            // 目录键 → 过期时刻
    private readonly Dictionary<int, (string Name, string Exe, long ExpireMs)> _pidNames = new();
    // V0.9.9: pid→exe 档案。进程启动必然先打开自己的 exe 映像——OP 帧路径以 .exe 结尾时
    // 记录（pid, exe 路径）。短命进程退出后 GetProcessById 失败，仍可从这里还原名字/路径。
    private readonly Dictionary<int, (string Exe, long ExpireMs)> _pidExe = new();
    private readonly Dictionary<string, List<(long RowId, long ExpireMs)>> _pending = new();

    // v1 状态（复刻 v0.8.1）
    private readonly Dictionary<string, (int Pid, long ExpireMs)> _v1 = new();

    private readonly long _recentTtlMs;
    private readonly long _recentCap;
    private readonly long _nameTtlMs = 20 * 60_000;
    private readonly long _claimTtlMs = 10_000;
    private readonly long _dirTtlMs = 60 * 60_000;
    private readonly long _pendingTtlMs = 30_000;   // V0.9.1: 10s→30s，覆盖风暴时帧消费排队
    private readonly int _pendingCapRows = 100_000;
    private int _pendingRows;

    public AttributionEngine(bool v2 = true, INameResolver? nameResolver = null, Func<long>? now = null,
        long recentTtlMs = 30 * 60_000, int recentCapacity = 250_000, int dirCapacity = 50_000)
    {
        _v2 = v2;
        _names = nameResolver;
        _now = now ?? (() => Environment.TickCount64);
        _recentTtlMs = recentTtlMs;
        _recentCap = recentCapacity;
        _recent = new KeyLru<string, (int, string)>(recentCapacity);
        _dirCapacity = dirCapacity;
    }

    private readonly int _dirCapacity;
    private long Now => _now();

    private static string Key(string path) => (path ?? "").TrimEnd('\\').ToLowerInvariant();

    // —— 帧入口 ——
    public void ApplyFrame(Frame f)
    {
        switch (f.Kind)
        {
            case FrameKind.Name:
                if (f.Pid > 0 && !string.IsNullOrEmpty(f.Name))
                    lock (_gate) _pidNames[f.Pid] = (f.Name, f.ExePath, Now + _nameTtlMs);
                break;
            case FrameKind.Open when !_v2:
                lock (_gate)
                {
                    PruneV1();
                    _v1[Key(f.Path)] = (f.Pid, Now + 4_000);
                }
                break;
            case FrameKind.Open:
                ApplyOpen(f);
                break;
            case FrameKind.Write when _v2:
                ApplyWrite(f);
                break;
            case FrameKind.Cleanup when _v2:
                ApplyCleanup(f);
                break;
            case FrameKind.Delete when _v2:
                ApplyDelete(f);
                break;
            case FrameKind.Rename when _v2:
                ApplyRenameHint(f);
                break;
        }
    }

    private readonly object _gate = new();

    private void ApplyOpen(Frame f)
    {
        var k = Key(f.Path);
        if (f.IsDir)
        {
            lock (_gate)
            {
                if (_dirs.Count >= _dirCapacity && !_dirs.ContainsKey(k))
                    _dirs.Remove(_dirs.Keys.First());
                _dirs[k] = Now + _dirTtlMs;
                if (!string.IsNullOrEmpty(f.Name)) _pidNames[f.Pid] = (f.Name, "", Now + _nameTtlMs);
            }
            return;
        }
        List<PendingBackfill>? backfill = null;
        lock (_gate)
        {
            // V0.9.9: 进程启动必先打开自己 exe——OP 帧路径以 .exe 结尾时建 pid→exe 档案
            if (f.Path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                _pidExe[f.Pid] = (f.Path, Now + _nameTtlMs);

            if (!_active.TryGetValue(k, out var o))
            {
                o = new Owner();
                _active[k] = o;
            }
            if (o.OpenerPid == 0) { o.OpenerPid = f.Pid; o.OpenerName = f.Name; }
            if (!string.IsNullOrEmpty(f.Name)) _pidNames[f.Pid] = (f.Name, "", Now + _nameTtlMs);
            // 打开帧首次让该路径可归因 → 回填等待中的 pending 行（归打开者）
            backfill = DrainPending(k, new AttrResult(AttrResultKind.Opener, f.Pid, NameOf(f.Pid, f.Name), ""));
        }
        Raise(backfill);
    }

    private void ApplyWrite(Frame f)
    {
        var k = Key(f.Path);
        WriteSample? sample = null;
        List<PendingBackfill>? backfill = null;
        lock (_gate)
        {
            if (!_active.TryGetValue(k, out var o))
            {
                o = new Owner { OpenerPid = f.Pid };
                _active[k] = o;
            }
            o.WriterPid = f.Pid;
            o.WriterName = f.Name;
            if (!string.IsNullOrEmpty(f.Name)) _pidNames[f.Pid] = (f.Name, "", Now + _nameTtlMs);
            _recent.Add(k, (f.Pid, NameOf(f.Pid, f.Name)), Now + _recentTtlMs);

            var (name, exe) = NameOfExe(f.Pid, f.Name);
            sample = new WriteSample(DateTime.Now, f.Pid, name, exe, f.Path, FsPaths.GetFolder(f.Path), f.Bytes);
            backfill = DrainPending(k, new AttrResult(AttrResultKind.Writer, f.Pid, name, exe));
        }
        if (sample != null && sample.Bytes > 0) OnWriteBytes?.Invoke(sample);
        Raise(backfill);
    }

    private void ApplyCleanup(Frame f)
    {
        var k = Key(f.Path);
        List<PendingBackfill>? backfill = null;
        lock (_gate)
        {
            if (_active.TryGetValue(k, out var o))
            {
                int pid = o.WriterPid != 0 ? o.WriterPid : o.OpenerPid;
                if (pid > 0)
                {
                    var name = o.WriterPid != 0 ? NameOf(pid, o.WriterName) : NameOf(pid, o.OpenerName);
                    _recent.Add(k, (pid, name), Now + _recentTtlMs);
                    backfill = DrainPending(k,
                        new AttrResult(o.WriterPid != 0 ? AttrResultKind.Writer : AttrResultKind.Opener, pid, name, ""));
                }
                _active.Remove(k);
            }
        }
        Raise(backfill);
    }

    private void ApplyDelete(Frame f)
    {
        var k = Key(f.Path);
        List<PendingBackfill>? backfill = null;
        lock (_gate)
        {
            var (name, _) = NameOfExe(f.Pid, f.Name);
            _deleteClaims[k] = (f.Pid, Now + _claimTtlMs);
            _recent.Add(k, (f.Pid, name), Now + _recentTtlMs);
            backfill = DrainPending(k, new AttrResult(AttrResultKind.DeletedFrame, f.Pid, name, ""));
        }
        Raise(backfill);
    }

    /// <summary>ID19 只给操作者+旧路径；真正的旧→新迁移在 FSW Renamed（携带新旧路径）时做。</summary>
    private void ApplyRenameHint(Frame f)
    {
        var k = Key(f.OldPath);
        lock (_gate)
        {
            var (name, _) = NameOfExe(f.Pid, f.Name);
            _recent.TryGet(k, Now, out var prev);
            _recent.Add(k, (f.Pid, string.IsNullOrEmpty(prev.Name) ? name : prev.Name), Now + _claimTtlMs);
        }
    }

    // —— FSW 事件归因 ——
    public AttrResult Resolve(FileSystemWatcherMonitor.RawEvent raw)
    {
        if (!_v2) return ResolveV1(raw.FullPath);

        var k = Key(raw.FullPath);
        lock (_gate)
        {
            Prune();

            // 改名：属主取旧路径，并把归属迁移到新路径
            if (raw.EventType == "Renamed" && !string.IsNullOrEmpty(raw.OldFullPath))
            {
                var oldK = Key(raw.OldFullPath);
                var r = OwnerOf(oldK);
                if (r is { } rr)
                {
                    MigrateOwner(oldK, k);
                    return new AttrResult(AttrResultKind.Renamed, rr.Pid, rr.Name, "");
                }
                return AttrResult.PendingResult;
            }

            // 目录属性噪音：目录集合命中 → 丢弃
            if (_dirs.TryGetValue(k, out var dirExp) && dirExp > Now)
                return AttrResult.DroppedResult;

            if (raw.EventType == "Deleted")
            {
                if (_deleteClaims.TryGetValue(k, out var dc) && dc.ExpireMs > Now)
                {
                    var (n, e) = NameOfExe(dc.Pid, "");
                    return new AttrResult(AttrResultKind.DeletedFrame, dc.Pid, n, e);
                }
            }

            var own = OwnerOf(k);
            if (own is { } o) return o;
            return AttrResult.PendingResult;
        }
    }

    /// <summary>找不到属主、已以 pending 入库后注册行 id；属主帧晚到时触发 OnBackfill。</summary>
    public void RegisterPending(string path, long rowId)
    {
        var k = Key(path);
        lock (_gate)
        {
            if (_pendingRows >= _pendingCapRows) return;  // 溢出：保持 unknown，不再等
            if (!_pending.TryGetValue(k, out var list)) { list = new(); _pending[k] = list; }
            list.Add((rowId, Now + _pendingTtlMs));
            _pendingRows++;
        }
    }

    private List<PendingBackfill>? DrainPending(string k, AttrResult result)
    {
        if (!_pending.Remove(k, out var list)) return null;
        var now = Now;
        var alive = list.Where(x => x.ExpireMs > now).ToList();
        _pendingRows -= list.Count;
        if (alive.Count == 0) return null;
        _pendingRows += alive.Count;
        return alive.Select(x => new PendingBackfill(x.RowId, result)).ToList();
    }

    private void Raise(List<PendingBackfill>? list)
    {
        if (list is { Count: > 0 }) OnBackfill?.Invoke(list);
    }

    /// <summary>返回某路径当前最佳属主（Writer &gt; Opener &gt; Recent），无则 null。调用方持锁。</summary>
    private AttrResult? OwnerOf(string k)
    {
        if (_active.TryGetValue(k, out var o))
        {
            if (o.WriterPid > 0)
            {
                var (n, e) = NameOfExe(o.WriterPid, o.WriterName);
                return new AttrResult(AttrResultKind.Writer, o.WriterPid, n, e);
            }
            if (o.OpenerPid > 0)
            {
                var (n, e) = NameOfExe(o.OpenerPid, o.OpenerName);
                return new AttrResult(AttrResultKind.Opener, o.OpenerPid, n, e);
            }
        }
        if (_recent.TryGet(k, Now, out var r))
        {
            var (n, e) = NameOfExe(r.Pid, r.Name);
            return new AttrResult(AttrResultKind.Recent, r.Pid, n, e);
        }
        return null;
    }

    private void MigrateOwner(string oldK, string newK)
    {
        if (_active.TryGetValue(oldK, out var o))
        {
            _active[newK] = o;
            _active.Remove(oldK);
        }
        if (_recent.TryGet(oldK, Now, out var r))
        {
            _recent.Add(newK, r, Now + _recentTtlMs);
            _recent.Remove(oldK);
        }
        _dirs.Remove(oldK, out _);
    }

    private (string Name, string ExePath) NameOfExe(int pid, string hint)
    {
        if (_pidNames.TryGetValue(pid, out var p) && p.ExpireMs > Now && !string.IsNullOrEmpty(p.Name))
            return (p.Name, p.Exe);
        // V0.9.9: pid→exe 档案兜底（进程已退出也能给出名字+完整路径）
        if (_pidExe.TryGetValue(pid, out var pe) && pe.ExpireMs > Now)
        {
            var exe = pe.Exe;
            return (Path.GetFileName(exe), exe);
        }
        // hint 为空或只是占位 "unknown" 时，实时解析（进程此刻可能仍存活）
        if (string.IsNullOrEmpty(hint) || hint == "unknown")
        {
            var lr = _names?.Resolve(pid);
            if (lr is { Name.Length: > 0 } l && l.Name != "unknown") return (l.Name, l.ExePath);
        }
        return (string.IsNullOrEmpty(hint) ? "unknown" : hint, "");
    }

    private string NameOf(int pid, string hint) => NameOfExe(pid, hint).Name;

    private void Prune()
    {
        var now = Now;
        foreach (var k in _dirs.Keys.Where(k => _dirs[k] <= now).ToList()) _dirs.Remove(k);
        foreach (var k in _deleteClaims.Keys.Where(k => _deleteClaims[k].ExpireMs <= now).ToList())
            _deleteClaims.Remove(k);
        foreach (var p in _pidNames.Keys.Where(p => _pidNames[p].ExpireMs <= now).ToList())
            _pidNames.Remove(p);
        foreach (var pid in _pidExe.Keys.Where(pid => _pidExe[pid].ExpireMs <= now).ToList())
            _pidExe.Remove(pid);
        foreach (var k in _pending.Keys.ToList())
        {
            var kept = _pending[k].Where(x => x.ExpireMs > now).ToList();
            _pendingRows -= _pending[k].Count - kept.Count;
            if (kept.Count == 0) _pending.Remove(k);
            else _pending[k] = kept;
        }
        _recent.RemoveExpired(now);
    }

    // —— v1（复刻 v0.8.1：仅打开帧、4 秒 TTL、先到先得、无目录/写字节/recent）——
    private AttrResult ResolveV1(string path)
    {
        var k = Key(path);
        lock (_gate)
        {
            PruneV1();
            if (_v1.TryGetValue(k, out var v) && v.ExpireMs > Now)
            {
                var (n, e) = NameOfExe(v.Pid, "");
                return new AttrResult(AttrResultKind.Opener, v.Pid, n, e, SourceOverride: "v1");
            }
            return AttrResult.PendingResult;
        }
    }

    private void PruneV1()
    {
        var now = Now;
        foreach (var k in _v1.Keys.Where(k => _v1[k].ExpireMs <= now).ToList()) _v1.Remove(k);
    }

    /// <summary>是否被识别为目录（供测试/诊断）。</summary>
    internal bool IsDirectory(string path)
    {
        var k = Key(path);
        lock (_gate) return _dirs.TryGetValue(k, out var e) && e > Now;
    }
}

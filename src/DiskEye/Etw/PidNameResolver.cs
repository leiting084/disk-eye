using System.Collections.Concurrent;
using System.Diagnostics;

namespace DiskEye.Etw;

/// <summary>
/// pid → (进程名, exe 路径) 的异步解析器。
/// ETW 回调线程只 EnqueuePid（去重，非阻塞）；专用后台线程串行做 Process.GetProcessById，
/// 解析结果经回调交回（worker 用来发 NM 帧）。进程已退出直接跳过，不重试（短命进程）。
/// 启动时不枚举全量进程（风暴期昂贵），名字按需解析。
/// </summary>
internal sealed class PidNameResolver : IDisposable
{
    private readonly BlockingCollection<int> _queue = new(new ConcurrentQueue<int>());
    private readonly HashSet<int> _pending = new();
    private readonly object _pendingGate = new();
    private readonly Action<int, string, string> _onResolved;
    private readonly Thread _thread;
    private volatile bool _running;

    public PidNameResolver(Action<int, string, string> onResolved)
    {
        _onResolved = onResolved;
        _thread = new Thread(Loop) { IsBackground = true, Name = "DiskEye-PidName" };
    }

    public void Start()
    {
        if (_running) return;
        _running = true;
        _thread.Start();
    }

    /// <summary>非阻塞登记一个待解析 pid（去重）。</summary>
    public void EnqueuePid(int pid)
    {
        if (pid <= 0) return;
        lock (_pendingGate)
        {
            if (!_pending.Add(pid)) return;
        }
        try { _queue.TryAdd(pid, 0); } catch { /* CompleteAdding 后忽略 */ }
    }

    private void Loop()
    {
        foreach (var pid in _queue.GetConsumingEnumerable())
        {
            lock (_pendingGate) _pending.Remove(pid);
            if (!_running) continue;
            try
            {
                using var p = Process.GetProcessById(pid);
                var name = p.ProcessName + ".exe";
                string exe = "";
                try { exe = p.MainModule?.FileName ?? ""; } catch { /* 32/64、权限不足时拿不到 */ }
                _onResolved(pid, name.Replace('\t', ' '), exe);
            }
            catch
            {
                // 进程已退出（短命进程）——跳过不重试；pid 仍随帧到达，主进程还有自己的兜底
            }
        }
    }

    public void Dispose()
    {
        _running = false;
        try { _queue.CompleteAdding(); } catch { }
    }
}

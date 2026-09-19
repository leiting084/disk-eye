using System.Collections.Concurrent;

namespace DiskEye.Etw;

/// <summary>
/// 有界非阻塞帧输出：ETW 回调线程 Enqueue 永不阻塞（v0.7 内核态阻塞事故红线）。
/// 专用写线程串行写 StreamWriter；队列超容量时丢弃最旧之外的新帧并计数（ST 帧上报）。
/// </summary>
internal sealed class BoundedFrameWriter : IDisposable
{
    private readonly TextWriter _output;
    private readonly int _capacity;
    private readonly ConcurrentQueue<string> _queue = new();
    private readonly object _pulseGate = new();
    private Thread? _thread;
    private volatile bool _running;
    private int _queued;

    public long DroppedCount => Interlocked.Read(ref _dropped);
    private long _dropped;

    public int QueueDepth => Volatile.Read(ref _queued);

    public BoundedFrameWriter(TextWriter output, int capacity = 500_000, bool autoStart = true)
    {
        _output = output;
        _capacity = capacity;
        if (autoStart) Start();
    }

    public void Start()
    {
        if (_running) return;
        _running = true;
        _thread = new Thread(WriteLoop) { IsBackground = true, Name = "DiskEye-FrameWriter" };
        _thread.Start();
    }

    /// <summary>非阻塞入队。满则丢弃并累加 DroppedCount，绝不阻塞调用线程（ETW 回调）。</summary>
    public void Enqueue(string line)
    {
        int n = Interlocked.Increment(ref _queued);
        if (n > _capacity)
        {
            Interlocked.Decrement(ref _queued);
            Interlocked.Increment(ref _dropped);
            return;
        }
        _queue.Enqueue(line);
        lock (_pulseGate) Monitor.PulseAll(_pulseGate);
    }

    private void WriteLoop()
    {
        while (_running || !_queue.IsEmpty)
        {
            try
            {
                if (_queue.TryDequeue(out var line))
                {
                    Interlocked.Decrement(ref _queued);
                    _output.WriteLine(line);
                    continue;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[FrameWriter] write error: {ex.Message}");
            }
            lock (_pulseGate)
            {
                if (_running && _queue.IsEmpty)
                    Monitor.Wait(_pulseGate, 100);
            }
        }
        try { _output.Flush(); } catch { }
    }

    public void Dispose()
    {
        _running = false;
        lock (_pulseGate) Monitor.PulseAll(_pulseGate);
        try { _thread?.Join(3000); } catch { }
        // 兜底：join 超时也同步 flush 剩余（进程退出场景）
        try { while (_queue.TryDequeue(out var l)) _output.WriteLine(l); _output.Flush(); } catch { }
    }
}

using System.Collections.Concurrent;
using DiskEye.Models;
using DiskEye.Storage;

namespace DiskEye.Monitoring;

/// <summary>
/// 把 FileSystemWatcher 收到的事件每 100ms 批量入库。V0.9：
/// - 归因走 AttributionEngine（FileObject 状态机），未命中短等 1 秒，超时以 pending 入库并注册回填；
/// - 字节总量只来自 ETW write_bytes（真实写入），不再累加 FileInfo 快照；
/// - 目录属性事件丢弃；ETW 不可用时降级 EtwProcessResolver 启发式，字节暂停；
/// - 每小时落一次 v1/v2 A-B 计数。
/// </summary>
public sealed class EventAggregator : IDisposable
{
    private readonly EtwProcessResolver _heuristic;
    private readonly EventStore _store;
    private readonly EtwFrameClient? _client;
    private readonly AttributionEngine _engine;
    private readonly V1AttributionShadow _shadow;
    private readonly int _intervalMs;

    private readonly ConcurrentQueue<PendingEvent> _queue = new();
    private readonly ConcurrentQueue<WriteSample> _writeQueue = new();
    private readonly ConcurrentQueue<PendingBackfill> _backfillQueue = new();
    private CancellationTokenSource? _cts;
    private Task? _pumpTask;
    private CancellationTokenSource? _surgeCts;
    private Task? _surgeTask;
    private long _totalBytesToday;
    private string _abHour = "";

    /// <summary>ETW 帧到达晚于 FSW 的最长等待（毫秒），超时先 pending 入库等回填。</summary>
    private const int EtwWaitMs = 1000;

    private readonly record struct PendingEvent(FileSystemWatcherMonitor.RawEvent Raw, long ArrivalTicks);

    public event Action<SurgeAlert>? OnSurgeDetected;

    public sealed record SurgeAlert(string Category, string Key, long Bytes, long EventCount, TimeSpan Window);

    public long ProcessSurgeThresholdBytes { get; set; } = 100L * 1024 * 1024;
    public long FolderSurgeThresholdBytes { get; set; } = 500L * 1024 * 1024;
    public TimeSpan SurgeWindow { get; set; } = TimeSpan.FromMinutes(1);
    public TimeSpan SurgeCooldown { get; set; } = TimeSpan.FromMinutes(30);
    private readonly ConcurrentDictionary<string, DateTime> _surgeCooldown = new();

    public event Action<long>? OnTotalBytesChanged;

    public EventAggregator(EtwProcessResolver heuristic, EventStore store,
        AttributionEngine engine, V1AttributionShadow shadow, EtwFrameClient? client, int intervalMs = 100)
    {
        _heuristic = heuristic;
        _store = store;
        _engine = engine;
        _shadow = shadow;
        _client = client;
        _intervalMs = intervalMs;

        // ETW 写样本 → 字节入库 + 今日累计
        _engine.OnWriteBytes += s => _writeQueue.Enqueue(s);

        // pending 行的属主晚到 → 批量回填
        _engine.OnBackfill += OnBackfill;

        if (_client != null)
        {
            _client.OnFrame += f =>
            {
                _engine.ApplyFrame(f);
                _shadow.ApplyFrame(f);
            };
        }
    }

    private long _v2Backfill;

    /// <summary>
    /// 该回调运行在 ETW 读帧线程上——V0.9 曾在此直接写 SQLite，风暴时帧消费被 DB I/O 拖慢，
    /// 帧晚于 pending 窗口导致大量永久 unknown。V0.9.1 改为只入队，由 pump 线程批量执行。
    /// </summary>
    private void OnBackfill(List<PendingBackfill> items)
    {
        foreach (var i in items) _backfillQueue.Enqueue(i);
    }

    private void FlushBackfills()
    {
        if (_backfillQueue.IsEmpty) return;
        var pending = new List<PendingBackfill>();
        while (_backfillQueue.TryDequeue(out var b))
        {
            pending.Add(b);
            if (pending.Count >= 2000) break;
        }
        try
        {
            foreach (var g in pending.GroupBy(i => i.Result))
            {
                var r = g.Key;
                var ids = g.Select(i => i.RowId).Distinct().ToList();
                int n = _store.BackfillRows(ids, r.Pid, r.Name, r.ExePath, "v2-backfill");
                Interlocked.Add(ref _v2Backfill, n);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Aggregator] 回填失败: {ex.Message}");
        }
    }

    /// <summary>ETW 精确归因是否在线（否则降级启发式，字节暂停）。</summary>
    public bool IsPrecise => _client?.IsActive ?? false;

    /// <summary>
    /// V0.9.7: 允许入账的盘符（大写）。ETW 是全局 provider，所有盘的 Write 都会来；
    /// 不按监控盘过滤的话"没选 C 也有 C 盘数据"（用户实测反馈）。
    /// 空集合 = 不过滤（向后兼容）。
    /// </summary>
    public HashSet<string> AllowedDrives { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public void Enqueue(FileSystemWatcherMonitor.RawEvent raw)
        => _queue.Enqueue(new PendingEvent(raw, DateTime.UtcNow.Ticks));

    public void Start()
    {
        if (_pumpTask != null) return;
        _cts = new CancellationTokenSource();
        _pumpTask = Task.Run(() => PumpLoop(_cts.Token));
        _surgeCts = new CancellationTokenSource();
        _surgeTask = Task.Run(() => SurgeDetectLoop(_surgeCts.Token));
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { }
        try { _surgeCts?.Cancel(); } catch { }
        try { _pumpTask?.Wait(2000); } catch { }
        try { _surgeTask?.Wait(2000); } catch { }
        FlushOnce();
        FlushWrites();
        FlushBackfills();
        FlushAb(force: true);
        _pumpTask = null;
        _surgeTask = null;
        _cts?.Dispose();
        _surgeCts?.Dispose();
        _cts = null;
        _surgeCts = null;
    }

    private async Task PumpLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(_intervalMs, ct); } catch { break; }
            FlushOnce();
            FlushWrites();
            FlushBackfills();
            FlushAb(force: false);
            MaybeBackfillNames();
        }
    }

    private void FlushOnce()
    {
        if (_queue.IsEmpty) return;

        var batch = new List<FileEvent>();
        var pendingPaths = new List<int>();   // batch 中属于 pending 的下标
        var nowTicks = DateTime.UtcNow.Ticks;
        List<PendingEvent>? waitMore = null;

        while (_queue.TryDequeue(out var p))
        {
            var raw = p.Raw;
            var result = _engine.Resolve(raw);

            if (result.Kind == AttrResultKind.Dropped)
            {
                Count(AbKey.DroppedDirs);
                continue;  // 目录属性噪音，不入库
            }

            int pid;
            string name;
            string exePath;
            string source;

            if (result.Kind == AttrResultKind.Pending)
            {
                bool etwPrecise = IsPrecise;
                bool withinWait = (nowTicks - p.ArrivalTicks) / 10_000 < EtwWaitMs;
                if (etwPrecise && withinWait)
                {
                    (waitMore ??= new List<PendingEvent>()).Add(p);
                    continue;
                }

                if (!etwPrecise)
                {
                    // 降级：启发式
                    int hp = _heuristic.Resolve(raw.FullPath);
                    if (hp > 0)
                    {
                        var h = _heuristic.GetProcessInfo(hp);
                        pid = hp; name = h.Name; exePath = h.Path; source = "heuristic";
                        Count(AbKey.Heuristic);
                        goto Add;
                    }
                }

                // 超时仍未知 → pending 入库，等 10 秒内的回填
                pid = 0; name = "unknown"; exePath = ""; source = "pending";
                Count(AbKey.Unknown);
                pendingPaths.Add(batch.Count);
            }
            else
            {
                pid = result.Pid; name = result.Name; exePath = result.ExePath; source = result.Source;
                Count(result.Kind switch
                {
                    AttrResultKind.Writer => AbKey.V2Writer,
                    AttrResultKind.Opener => AbKey.V2Opener,
                    AttrResultKind.Recent => AbKey.V2Recent,
                    AttrResultKind.DeletedFrame => AbKey.V2Delete,
                    AttrResultKind.Renamed => AbKey.V2Rename,
                    _ => AbKey.Unknown,
                });
            }

            Add:
            // A/B 影子计数（v0.8.1 是否能具名）
            Count(AbKey.Fsw);
            if (_shadow.IsNamed(raw.FullPath)) Count(AbKey.V1Named);
            if (pid > 0 && name != "unknown") Count(AbKey.V2Named);

            batch.Add(new FileEvent(
                Id: 0,
                Timestamp: raw.Timestamp,
                DriveLetter: raw.DriveLetter,
                FullPath: raw.FullPath,
                EventType: raw.EventType,
                SizeBytes: raw.SizeBytes,
                ProcessId: pid,
                ProcessName: name,
                ProcessPath: exePath,
                Source: source));
        }

        if (waitMore != null)
            foreach (var p in waitMore) _queue.Enqueue(p);

        if (batch.Count > 0)
        {
            try
            {
                var ids = _store.InsertBatch(batch);
                foreach (var idx in pendingPaths)
                    if (idx < ids.Count) _engine.RegisterPending(batch[idx].FullPath, ids[idx]);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Aggregator] 写库失败: {ex.Message}");
            }
        }

        try { _store.MaybePeriodicCheckpoint(); } catch { }
    }

    private void FlushWrites()
    {
        if (_writeQueue.IsEmpty) return;
        var samples = new List<WriteSample>();
        long added = 0;
        while (_writeQueue.TryDequeue(out var s))
        {
            // V0.9.7: 按监控盘过滤（ETW 全局，未过滤会记录未选盘的数据）
            if (AllowedDrives.Count > 0)
            {
                // root="D:\" → "D:" → "D"（AllowedDrives 存无冒号大写字母）
                var drive = Path.GetPathRoot(s.FullPath) is { Length: > 1 } root
                    ? root.TrimEnd('\\', '/').TrimEnd(':')
                    : "";
                if (!AllowedDrives.Contains(drive)) continue;
            }
            samples.Add(s);
            if (s.Timestamp.Date == DateTime.Today) added += s.Bytes;
        }
        try
        {
            _store.InsertWriteBatch(samples);
            if (added > 0)
            {
                Interlocked.Add(ref _totalBytesToday, added);
                OnTotalBytesChanged?.Invoke(_totalBytesToday);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Aggregator] write_bytes 入库失败: {ex.Message}");
        }
    }

    private DateTime _lastNameBackfill = DateTime.MinValue;
    private int _nameBackfillRunning;

    /// <summary>每 60 秒在后台用"同 pid 已知名字"补全 unknown（风暴后自愈，不阻塞 pump/UI）。</summary>
    private void MaybeBackfillNames()
    {
        if (Interlocked.CompareExchange(ref _nameBackfillRunning, 1, 0) != 0) return;
        var now = DateTime.Now;
        if (now - _lastNameBackfill < TimeSpan.FromSeconds(60))
        {
            _nameBackfillRunning = 0;
            return;
        }
        _lastNameBackfill = now;
        Task.Run(() =>
        {
            try { _store.BackfillNamesByPid(); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[Aggregator] 补名失败: {ex.Message}"); }
            finally { _nameBackfillRunning = 0; }
        });
    }

    // —— A/B 小时计数 ——
    private enum AbKey { Fsw, V1Named, V2Named, V2Writer, V2Opener, V2Recent, V2Delete, V2Rename, Heuristic, Unknown, DroppedDirs }
    private readonly long[] _ab = new long[11];
    private readonly object _abGate = new();
    private void Count(AbKey k) { lock (_abGate) _ab[(int)k]++; }

    private void FlushAb(bool force)
    {
        var hour = DateTime.Now.ToString("yyyy-MM-dd HH:00");
        long[]? snapshot = null;
        string key = "";
        lock (_abGate)
        {
            if (_abHour == "") _abHour = hour;
            if (force)
            {
                if (_ab.All(x => x == 0)) return;
                snapshot = (long[])_ab.Clone();
                key = _abHour;
            }
            else if (hour != _abHour)
            {
                snapshot = (long[])_ab.Clone();
                key = _abHour;          // snapshot 属于刚结束的旧小时桶
                Array.Clear(_ab);
                _abHour = hour;
            }
        }
        if (snapshot == null) return;
        try
        {
            long lost = _client?.LastHealth?.EventsLost ?? 0;
            int restarts = _client?.RestartCount ?? 0;
            _store.UpsertAbHourly(new EventStore.AbHourlyStats(
                key,
                FswEvents: snapshot[(int)AbKey.Fsw], V1Named: snapshot[(int)AbKey.V1Named],
                V2Named: snapshot[(int)AbKey.V2Named], V2Writer: snapshot[(int)AbKey.V2Writer],
                V2Opener: snapshot[(int)AbKey.V2Opener], V2Recent: snapshot[(int)AbKey.V2Recent],
                V2Backfill: Interlocked.Exchange(ref _v2Backfill, 0),
                Heuristic: snapshot[(int)AbKey.Heuristic], Unknown: snapshot[(int)AbKey.Unknown],
                DroppedDirs: snapshot[(int)AbKey.DroppedDirs],
                EtwEventsLost: lost, ChildRestarts: restarts));
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[Aggregator] A/B 落库失败: {ex.Message}"); }
    }

    // —— 异常增长（字节口径已切 write_bytes）——
    private async Task SurgeDetectLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var interval = Math.Max(5, Math.Min(600, SurgeWindow.TotalSeconds));
            try { await Task.Delay(TimeSpan.FromSeconds(interval), ct); } catch { break; }
            try { CheckSurge(); } catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Surge] 检测异常: {ex.Message}");
            }
        }
    }

    private void CheckSurge()
    {
        var since = DateTime.Now - SurgeWindow;
        foreach (var s in _store.QueryProcessBytes(since))
        {
            if (s.TotalBytes >= ProcessSurgeThresholdBytes
                && TryFireAlert("Process", s.ProcessName, s.TotalBytes, s.EventCount, SurgeWindow))
                return;
        }
        foreach (var s in _store.QueryFolderBytes(since, 20))
        {
            if (s.TotalBytes >= FolderSurgeThresholdBytes
                && TryFireAlert("Folder", s.Folder, s.TotalBytes, s.EventCount, SurgeWindow))
                return;
        }
    }

    private bool TryFireAlert(string category, string key, long bytes, long eventCount, TimeSpan window)
    {
        var compositeKey = category + "|" + key;
        if (_surgeCooldown.TryGetValue(compositeKey, out var last) && DateTime.Now - last < SurgeCooldown)
            return false;
        _surgeCooldown[compositeKey] = DateTime.Now;
        foreach (var kv in _surgeCooldown)
            if (DateTime.Now - kv.Value > SurgeCooldown * 2)
                _surgeCooldown.TryRemove(kv.Key, out _);
        OnSurgeDetected?.Invoke(new SurgeAlert(category, key, bytes, eventCount, window));
        return true;
    }

    public void InitTodayBytes(long todayBytes) => Interlocked.Exchange(ref _totalBytesToday, todayBytes);
    public long TotalBytesToday => Interlocked.Read(ref _totalBytesToday);
    public void Dispose() => Stop();
}

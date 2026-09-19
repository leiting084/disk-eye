using DiskEye.Config;
using DiskEye.Models;
using DiskEye.Storage;

namespace DiskEye.Monitoring;

/// <summary>
/// 监控服务总控。负责：
/// - 启动/停止所有组件
/// - 根据配置增减监控盘符
/// - 启动时执行一次归档（&gt;30 天的移到 archive 表）
/// </summary>
public sealed class MonitorService : IDisposable
{
    public EtwProcessResolver Etw { get; private set; } = null!;
    public EtwFrameClient? FrameClient { get; private set; }
    public AttributionEngine Engine { get; private set; } = null!;
    public EventStore Store { get; private set; } = null!;
    public EventAggregator Aggregator { get; private set; } = null!;

    private readonly Dictionary<string, FileSystemWatcherMonitor> _monitors = new();
    private AppConfig _config = new();
    private bool _started;

    public AppConfig CurrentConfig => _config;

    public void Start()
    {
        if (_started) return;
        _started = true;

        _config = AppConfigStore.Load();
        var dbPath = Path.Combine(AppConfigStore.ConfigDir, "events.db");
        Store = new EventStore(dbPath);
        Etw = new EtwProcessResolver();

        // V0.9: ETW 子进程帧客户端（Start 立即返回；不就绪时引擎走启发式降级）
        FrameClient = _config.EnableEtw ? new EtwFrameClient() : null;
        Engine = new AttributionEngine(
            v2: _config.AttributionEngineV2,
            nameResolver: new ProcessCacheNameResolver(Etw));
        var shadow = new V1AttributionShadow();
        Aggregator = new EventAggregator(Etw, Store, Engine, shadow, FrameClient,
            _config.AggregationIntervalMs);

        // V3: 同步路径过滤到 PathFilterHolder（FileSystemWatcherMonitor 用）
        PathFilterHolder.Current = new PathFilter
        {
            IgnorePrefixes = new List<string>(_config.IgnorePrefixes),
            IncludePrefixes = new List<string>(_config.IncludePrefixes),
        };

        Etw.Start();
        Aggregator.Start();
        // V0.9.7: ETW 字节按监控盘入账（"没选 C 也有 C 盘"的修复）
        SyncAllowedDrives();
        // V0.9.8: 清掉盘符过滤上线前的非监控盘历史字节（重灾区 C:\Temp 残留）
        try { Store.PurgeWritesOutsideDrives(Aggregator.AllowedDrives); } catch { }
        // 子进程最后启动：就绪帧到达即喂给已工作的引擎/聚合器
        FrameClient?.Start();

        // V5.1: 同步异常增长阈值到 Aggregator
        Aggregator.ProcessSurgeThresholdBytes = _config.ProcessSurgeThresholdBytes;
        Aggregator.FolderSurgeThresholdBytes = _config.FolderSurgeThresholdBytes;
        Aggregator.SurgeWindow = TimeSpan.FromSeconds(_config.SurgeWindowSeconds);
        Aggregator.SurgeCooldown = TimeSpan.FromMinutes(_config.SurgeCooldownMinutes);

        // V0.9.15: 启动计时日志
        var sw = System.Diagnostics.Stopwatch.StartNew();
        // 启动时初始化今日基线（V0.9：真实写入字节口径）
        Aggregator.InitTodayBytes(Store.QueryTodayWriteBytes());
        System.Diagnostics.Debug.WriteLine($"[Monitor] InitTodayBytes 耗时 {sw.ElapsedMilliseconds}ms");

        sw.Restart();
        // 启动时归档一次
        try { Store.Archive(_config.ArchiveAfterDays); } catch { /* 启动归档失败不致命 */ }
        System.Diagnostics.Debug.WriteLine($"[Monitor] Archive 耗时 {sw.ElapsedMilliseconds}ms");

        // 启动已配置的盘符监控
        foreach (var d in _config.MonitoredDrives)
        {
            TryAddDrive(d);
        }
    }

    public void Stop()
    {
        if (!_started) return;
        _started = false;
        foreach (var m in _monitors.Values) m.Dispose();
        _monitors.Clear();
        Aggregator?.Dispose();
        Etw?.Dispose();
        FrameClient?.Dispose();
        Store?.Dispose();
    }

    /// <summary>运行时增减监控盘符（设置界面用）。</summary>
    public void TryAddDrive(string letter)
    {
        letter = letter.ToUpperInvariant();
        if (_monitors.ContainsKey(letter)) return;
        try
        {
            var m = new FileSystemWatcherMonitor(letter, Aggregator.Enqueue);
            m.Start();
            _monitors[letter] = m;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Monitor] 添加盘符 {letter} 失败: {ex.Message}");
        }
    }

    public void RemoveDrive(string letter)
    {
        letter = letter.ToUpperInvariant();
        if (_monitors.TryGetValue(letter, out var m))
        {
            m.Dispose();
            _monitors.Remove(letter);
        }
    }

    public IReadOnlyCollection<string> ActiveDrives => _monitors.Keys.ToList();

    public void UpdateConfig(Action<AppConfig> mutator)
    {
        mutator(_config);
        AppConfigStore.Save(_config);
        SyncAllowedDrives();  // V0.9.7: 运行时改监控盘后立即生效
        // V0.9.11: 切走盘符时清其历史字节——榜单立即反映切换（否则旧数据挂着"切了没变"）
        try { Store.PurgeWritesOutsideDrives(Aggregator.AllowedDrives); } catch { }
    }

    private void SyncAllowedDrives()
        => Aggregator.AllowedDrives = new HashSet<string>(
            _config.MonitoredDrives.Select(d => d.ToUpperInvariant()));

    public void Dispose() => Stop();
}
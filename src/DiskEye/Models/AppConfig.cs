namespace DiskEye.Models;

/// <summary>
/// 应用配置。序列化到 %LOCALAPPDATA%\DiskEye\config.json。
/// </summary>
public sealed class AppConfig
{
    /// <summary>要监控的盘符集合，如 ["C","D","E"]。空=首次运行引导。</summary>
    public HashSet<string> MonitoredDrives { get; set; } = new();

    /// <summary>是否开机自启动。</summary>
    public bool AutoStart { get; set; } = true;

    /// <summary>事件聚合批量间隔（毫秒）。</summary>
    public int AggregationIntervalMs { get; set; } = 100;

    /// <summary>归档阈值（天），超过该天数的事件移到 archive 表。</summary>
    public int ArchiveAfterDays { get; set; } = 30;

    /// <summary>V3: 黑名单路径前缀列表（命中即忽略，例如 "C:\\Users\\xxx\\Downloads\\tmp"）。</summary>
    public List<string> IgnorePrefixes { get; set; } = new();

    /// <summary>V3: 白名单路径前缀列表（空=不限；非空则仅监控这些前缀下的事件）。</summary>
    public List<string> IncludePrefixes { get; set; } = new();

    /// <summary>V5.1: 进程异常增长阈值（字节/窗口期）。默认 100MB。</summary>
    public long ProcessSurgeThresholdBytes { get; set; } = 100L * 1024 * 1024;

    /// <summary>V5.1: 文件夹异常增长阈值（字节/窗口期）。默认 500MB。</summary>
    public long FolderSurgeThresholdBytes { get; set; } = 500L * 1024 * 1024;

    /// <summary>V5.1: 异常增长检测窗口（秒）。默认 60 秒。</summary>
    public int SurgeWindowSeconds { get; set; } = 60;

    /// <summary>V5.1: 异常增长弹窗冷却（分钟）。默认 30 分钟。</summary>
    public int SurgeCooldownMinutes { get; set; } = 30;

    /// <summary>V7.4: 开机自启延迟（秒）。默认 30 秒，避免开机时和系统 IO 抢资源。</summary>
    public int StartupDelaySeconds { get; set; } = 30;

    /// <summary>V0.8: 是否启用 ETW 子进程精确反查（false = 只用 Process 启发式）。
    /// ETW 在子进程里跑，出问题只影响精确度；若用户机器上仍不稳定可改 config.json 关掉。</summary>
    public bool EnableEtw { get; set; } = true;

    /// <summary>V0.9.13: 界面语言 zh / en（切换后重启生效）。</summary>
    public string Language { get; set; } = "zh";

    /// <summary>V0.9: 归因引擎。true=FileObject 状态机（writer 权威+长句柄+真实字节）；
    /// false=v1 兼容（仅打开帧 4 秒 TTL，复刻 v0.8.1 行为），用于回退。</summary>
    public bool AttributionEngineV2 { get; set; } = true;

    /// <summary>主窗口最近一次位置。</summary>
    public int? WindowX { get; set; }
    public int? WindowY { get; set; }
    public int? WindowWidth { get; set; }
    public int? WindowHeight { get; set; }
}
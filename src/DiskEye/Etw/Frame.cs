namespace DiskEye.Etw;

/// <summary>主从 stdout 帧类型（V1 协议）。</summary>
public enum FrameKind
{
    /// <summary>无法识别但结构完整的帧（前向兼容：调用方计数忽略）。</summary>
    Unknown,
    Ready,
    Heartbeat,
    Error,
    /// <summary>FileIo/Create：pid 打开 path。</summary>
    Open,
    /// <summary>1 秒聚合写入：pid 对 path 写了 Bytes。</summary>
    Write,
    /// <summary>FileIo/Cleanup：pid 对 path 的句柄关闭。</summary>
    Cleanup,
    /// <summary>FileIo/Delete（SetInfo disposition=1）。</summary>
    Delete,
    /// <summary>FileIo/Rename（SetInfo）；Path=旧路径，新路径由 FSW 提供。</summary>
    Rename,
    /// <summary>pid → name/exePath 名字解析结果。</summary>
    Name,
    /// <summary>5 秒健康统计。</summary>
    Stats,
}

/// <summary>一帧主从消息。未使用字段保持默认值，字符串永不为 null（空串）。</summary>
public sealed record Frame(
    FrameKind Kind,
    int Pid = 0,
    long Bytes = 0,
    bool IsDir = false,
    string Path = "",
    string OldPath = "",
    string Name = "",
    string ExePath = "",
    string Message = "",
    FrameHealth? Health = null);

/// <summary>ST 帧：子进程自报健康度（主进程据此告警/降级展示）。</summary>
public sealed record FrameHealth(
    long EventsLost,
    long DroppedFrames,
    int QueueDepth,
    long CountCreate,
    long CountWrite,
    long CountCleanup,
    long CountDelete,
    long CountRename);

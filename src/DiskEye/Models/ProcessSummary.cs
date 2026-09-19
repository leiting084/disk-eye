namespace DiskEye.Models;

/// <summary>
/// 「凶手指控榜」一行：按进程聚合后的统计。
/// </summary>
public sealed record ProcessSummary(
    string ProcessName,
    string ProcessPath,
    long EventCount,
    long TotalBytes,
    DateTime LastActivity
);
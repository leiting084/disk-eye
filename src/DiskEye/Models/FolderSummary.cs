namespace DiskEye.Models;

/// <summary>
/// 「文件夹重灾区」一行：按文件夹聚合的今日统计。
/// 注意：folder 是完整目录路径（C:\Users\xxx\AppData\Local\Temp\）。
/// </summary>
public sealed record FolderSummary(
    string Folder,
    long EventCount,
    long TotalBytes,
    DateTime LastActivity
);

/// <summary>
/// V5.4 进程树节点：单进程的今日事件统计 + 父进程信息。
/// </summary>
public sealed record ProcessTreeNode(
    int Pid,
    int? ParentPid,
    string ParentName,         // 父进程名（拉不到显示 "—"）
    string ProcessName,
    string ProcessPath,
    long EventCount,
    long TotalBytes,
    DateTime LastActivity
);
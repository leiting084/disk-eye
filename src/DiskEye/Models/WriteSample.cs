namespace DiskEye.Models;

/// <summary>
/// 一次 1 秒聚合的真实写入：某 pid 对某路径写入的增量字节（来自 ETW FileIo/Write）。
/// 与 events 表的"文件当前大小快照"严格区分——这是榜单/今日写入的权威口径。
/// </summary>
public sealed record WriteSample(
    DateTime Timestamp,
    int Pid,
    string ProcessName,
    string ProcessPath,
    string FullPath,
    string Folder,
    long Bytes);

namespace DiskEye.Models;

/// <summary>
/// 一条文件变化事件。SQLite events 表的一行。
/// </summary>
public sealed record FileEvent(
    long Id,
    DateTime Timestamp,
    string DriveLetter,      //  C / D / E
    string FullPath,         //  C:\foo\bar.txt
    string EventType,        //  Created / Modified / Deleted / Renamed
    long SizeBytes,          //  文件当前大小快照（V0.9 起不参与字节总量）；Deleted=0
    int ProcessId,           //  触发该变化的进程 PID（0=未知）
    string ProcessName,      //  进程名（unknown=未知）
    string ProcessPath,      //  进程可执行文件路径
    string Source = ""       //  V0.9 归因来源：v2-writer/v2-opener/v2-recent/v2-delete/v2-rename/v2-backfill/heuristic/pending/v1
);
namespace DiskEye.Etw;

/// <summary>
/// Kernel-File ETW 常量。所有取值 2026-09-15 由 experiments/etw-probe 在本机实测确认
/// （两轮 20 万事件/秒背景流量，EventsLost=0）。禁止凭记忆修改——改前先用探针复测。
/// </summary>
internal static class KernelFileConstants
{
    /// <summary>Microsoft-Windows-Kernel-File provider GUID（v0.8 实测正确值；v0.7 的 bdd372b7… 是错的）。</summary>
    public static readonly Guid KernelFileProvider = new("{EDD08927-9CC4-4E65-B970-C2560FB5C289}");

    /// <summary>Microsoft-Windows-Kernel-Process provider GUID（进程启动/停止，用于 pid→name 权威表）。</summary>
    public static readonly Guid KernelProcessProvider = new("{22FB2CD6-0E7B-422B-A0C7-2FAD1FD0E716}");

    /// <summary>KERNEL_FILE_KEYWORD_FILEIO。0x10 是 FILENAME（FlushBuffers/Name 类，不含路径，不消费）。</summary>
    public const ulong FileIoKeyword = 0x20;

    // —— FileIO 事件 ID（实测版本 ver=1；Query ID=24 ver=0 无路径不消费）——
    public const int EventIdCreate = 12;   // 每次打开/创建（含只读 open），载荷 offset32 起 Unicode 路径
    public const int EventIdCleanup = 13;  // 最后一个句柄关闭
    public const int EventIdClose = 14;    // 关闭兜底
    public const int EventIdWrite = 16;    // 写完成；offset28 = 本次写字节数(uint32)
    public const int EventIdDelete = 18;   // SetInfo 删除；offset20 disposition==1
    public const int EventIdRename = 19;   // SetInfo 改名；FileObject 反查旧路径（FSW 给新路径）

    // —— FileIo/Create(Name) 载荷布局（实测 hex dump）——
    // 0 IrpPtr(8)  8 FileObject(8)  16 ThreadId(4)  20 CreateOptions(4)
    // 24 FileAttributes(4)  28 ShareAccess(2)+...  32 Unicode 路径(\Device\… 形式)
    public const int OffsetFileObject = 8;
    public const int OffsetCreateOptions = 20;
    public const int OffsetPath = 32;

    // —— FileIo/Write(ID16)/Read(ID17) 载荷布局（实测，与 Create 不同！）——
    // 0 保留(8,实测为0)  8 IrpPtr(8，等于 Create 的 off0)  16 FileObject(8，等于 Create 的 off8)
    // 24 ThreadId(8)  32 杂项(uint32)  36 字节数(uint32)  40 …
    public const int OffsetWriteFileObject = 16;

    // —— FileIo/Write(ID16) 载荷布局（2026-09-15 逐字节实测，见 etw-probe 样本）——
    // 0 IrpPtr(8)  8 FileObject(8)  16 ThreadId(8)  24 保留(8)  32 杂项(uint32)  36 字节数(uint32)  40 …
    // 实测：12345→39300000、100000→A0860100、1000000→40420F00、777→09030000，均在 offset36
    public const int OffsetWriteSize = 36;

    // —— FileIo/SetInfo（Delete=18/Rename=19）载荷布局（实测 len=40）——
    // 0 IrpPtr(8)  8 FileObject(8)  16 ThreadId(8)  24 disposition/参数(uint32)  32/36 杂项
    // 删除时 offset24 = 1（实测）；改名时为 0
    public const int OffsetSetInfoParam = 24;
    public const uint DeleteDisposition = 1;

    /// <summary>CreateOptions 的 FILE_DIRECTORY_FILE 位（bit0）。命中=目录句柄。</summary>
    public const uint FileDirectoryFile = 0x1;
}

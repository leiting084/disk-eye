using System.Text;

namespace DiskEye.Etw;

/// <summary>一次打开（ID12）解析结果：FileObject、DOS 路径、是否目录。</summary>
internal readonly record struct CreateInfo(ulong FileObject, string DosPath, bool IsDirectory);

/// <summary>
/// Kernel-File 事件载荷的纯函数解析（无 TraceEvent 依赖，可单测）。
/// 所有偏移/布局 2026-09-15 由 experiments/etw-probe 逐字节实测，真实样本固化在
/// DiskEye.Tests 的 CapturedPayloads；禁止凭记忆修改，改动前先用探针复测。
/// 任何越界/异常都返回 false/null——调用方只丢事件，不能崩。
/// </summary>
internal static class KernelFileParser
{
    public static bool TryGetFileObject(byte[] data, out ulong fileObject)
    {
        fileObject = 0;
        if (data == null || data.Length < KernelFileConstants.OffsetFileObject + 8) return false;
        fileObject = BitConverter.ToUInt64(data, KernelFileConstants.OffsetFileObject);
        return fileObject != 0;
    }

    /// <summary>
    /// FileIo/Write(ID16)/Read(ID17) 的 FileObject 在 offset16（实测，比 Create 多 8 字节头）。
    /// 交叉验证：write 的 off16 == 同文件 create 的 off8。
    /// </summary>
    public static bool TryGetWriteFileObject(byte[] data, out ulong fileObject)
    {
        fileObject = 0;
        if (data == null || data.Length < KernelFileConstants.OffsetWriteFileObject + 8) return false;
        fileObject = BitConverter.ToUInt64(data, KernelFileConstants.OffsetWriteFileObject);
        return fileObject != 0;
    }

    /// <summary>解析 ID12 Create/Name：FileObject、目录位、NT 路径→DOS 路径。非本地盘返回 false。</summary>
    public static bool TryGetCreate(byte[] data, IReadOnlyList<(string Device, string Drive)> deviceMap, out CreateInfo info)
    {
        info = default;
        if (data == null || data.Length <= KernelFileConstants.OffsetPath + 2) return false;
        if (!TryGetFileObject(data, out var fo)) return false;

        bool optionDir = false;
        if (data.Length >= KernelFileConstants.OffsetCreateOptions + 4)
        {
            uint options = BitConverter.ToUInt32(data, KernelFileConstants.OffsetCreateOptions);
            optionDir = (options & KernelFileConstants.FileDirectoryFile) != 0;
        }

        string raw;
        try
        {
            raw = Encoding.Unicode.GetString(data, KernelFileConstants.OffsetPath,
                data.Length - KernelFileConstants.OffsetPath).TrimEnd('\0');
        }
        catch { return false; }
        if (raw.Length < 4 || raw[0] != '\\') return false;

        // 目录判定（实测）：CreateOptions 的 FILE_DIRECTORY_FILE 位不可靠——普通打开/建目录时为 0，
        // 只有按目录访问权限枚举时才置 1；但目录的 NT 路径恒以 '\' 结尾，文件不会。两者取或。
        bool isDir = optionDir || raw[^1] == '\\';

        string? dos = MapToDos(raw, deviceMap);
        if (dos == null) return false;  // 非本地盘（网络盘等）

        info = new CreateInfo(fo, dos, isDir);
        return true;
    }

    /// <summary>解析 ID16 Write 的本次字节数（uint32 @ offset36）。</summary>
    public static bool TryGetWriteSize(byte[] data, out uint bytes)
    {
        bytes = 0;
        if (data == null || data.Length < KernelFileConstants.OffsetWriteSize + 4) return false;
        bytes = BitConverter.ToUInt32(data, KernelFileConstants.OffsetWriteSize);
        return bytes > 0;
    }

    /// <summary>ID18 SetInfo 是否为删除（disposition @ offset24 == 1）。</summary>
    public static bool IsDeleteDisposition(byte[] data)
    {
        if (data == null || data.Length < KernelFileConstants.OffsetSetInfoParam + 4) return false;
        return BitConverter.ToUInt32(data, KernelFileConstants.OffsetSetInfoParam)
               == KernelFileConstants.DeleteDisposition;
    }

    /// <summary>NT 设备路径（\Device\HarddiskVolumeX\…）→ DOS（C:\…）。deviceMap 已按设备名长度降序。</summary>
    internal static string? MapToDos(string rawNtPath, IReadOnlyList<(string Device, string Drive)> deviceMap)
    {
        foreach (var (device, drive) in deviceMap)
        {
            if (rawNtPath.StartsWith(device, StringComparison.OrdinalIgnoreCase))
            {
                return drive + rawNtPath[device.Length..];
            }
        }
        return null;
    }
}

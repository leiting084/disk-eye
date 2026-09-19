using DiskEye.Etw;

namespace DiskEye.Tests;

public class KernelFileParserTests
{
    // 探针采集机：\Device\HarddiskVolume4 = C:
    private static readonly (string, string)[] DeviceMap =
        { (@"\Device\HarddiskVolume4", "C:") };

    [Fact]
    public void WriteSize_12345_FromCapturedPayload()
    {
        Assert.True(KernelFileParser.TryGetWriteSize(CapturedPayloads.Bytes(CapturedPayloads.Write12345Hex), out var n));
        Assert.Equal(12345u, n);
    }

    [Fact]
    public void WriteFileObject_AtOffset16_MatchesCreateFileObject_AtOffset8()
    {
        // 实测关键布局：Write 的 FileObject 在 off16，等于同文件 Create 的 off8
        var create = CapturedPayloads.Bytes(CapturedPayloads.CreateFileHex);
        var write = CapturedPayloads.Bytes(CapturedPayloads.Write12345Hex);
        KernelFileParser.TryGetFileObject(create, out var foCreate);
        KernelFileParser.TryGetWriteFileObject(write, out var foWrite);
        Assert.Equal(foCreate, foWrite);
        // off8 在 Write 里是 IrpPtr（=Create 的 off0），不是 FileObject
        KernelFileParser.TryGetFileObject(write, out var foWriteWrongOffset);
        Assert.NotEqual(foCreate, foWriteWrongOffset);
    }

    [Fact]
    public void WriteSize_100000_FromCapturedPayload()
    {
        Assert.True(KernelFileParser.TryGetWriteSize(CapturedPayloads.Bytes(CapturedPayloads.Write100000Hex), out var n));
        Assert.Equal(100000u, n);
    }

    [Fact]
    public void Create_File_ParsesPathAndFileObject_NotDirectory()
    {
        var ok = KernelFileParser.TryGetCreate(CapturedPayloads.Bytes(CapturedPayloads.CreateFileHex), DeviceMap, out var info);
        Assert.True(ok);
        Assert.False(info.IsDirectory);
        Assert.Equal(@"C:\Users\Dao\AppData\Local\Temp\etwprobe_io2\w0.bin", info.DosPath);
        Assert.NotEqual(0UL, info.FileObject);
    }

    [Fact]
    public void Create_Directory_DetectsDirectoryBit()
    {
        var ok = KernelFileParser.TryGetCreate(CapturedPayloads.Bytes(CapturedPayloads.CreateDirHex), DeviceMap, out var info);
        Assert.True(ok);
        Assert.True(info.IsDirectory);
        Assert.Equal(@"C:\Users\Dao\AppData\Local\Temp\etwprobe_io2\", info.DosPath);
    }

    [Fact]
    public void Delete_Disposition_Detected()
    {
        Assert.True(KernelFileParser.IsDeleteDisposition(CapturedPayloads.Bytes(CapturedPayloads.DeleteHex)));
    }

    [Fact]
    public void SetInfo_WithZeroDisposition_IsNotDelete()
    {
        var data = (byte[])CapturedPayloads.Bytes(CapturedPayloads.DeleteHex).Clone();
        // offset24 disposition 改 0（模拟 Rename ID19）
        data[24] = 0;
        Assert.False(KernelFileParser.IsDeleteDisposition(data));
    }

    [Fact]
    public void NonLocalDevice_ReturnsFalse()
    {
        var ok = KernelFileParser.TryGetCreate(
            CapturedPayloads.Bytes(CapturedPayloads.CreateFileHex),
            new[] { (@"\Device\Mup", "X:") }, out _);
        Assert.False(ok);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(8)]
    public void TruncatedPayloads_FileObjectFalse(int len)
    {
        var cut = CapturedPayloads.Bytes(CapturedPayloads.Write12345Hex)[..len];
        Assert.False(KernelFileParser.TryGetFileObject(cut, out _));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(32)]
    [InlineData(39)]  // size 在 [36,40)，差 1 字节
    public void TruncatedPayloads_WriteSizeFalse(int len)
    {
        var cut = CapturedPayloads.Bytes(CapturedPayloads.Write12345Hex)[..len];
        Assert.False(KernelFileParser.TryGetWriteSize(cut, out _));
    }

    [Fact]
    public void NullPayloads_DoNotThrow()
    {
        Assert.False(KernelFileParser.TryGetWriteSize(null!, out _));
        Assert.False(KernelFileParser.TryGetCreate(null!, DeviceMap, out _));
        Assert.False(KernelFileParser.IsDeleteDisposition(null!));
    }

    [Fact]
    public void MapToDos_LongestDevicePrefixWins()
    {
        var map = new (string, string)[]
        {
            (@"\Device\HarddiskVolume10", "J:"),
            (@"\Device\HarddiskVolume1", "D:"),
        };
        // 长前缀优先，避免 Volume1 抢占 Volume10
        Assert.Equal(@"J:\f", KernelFileParser.MapToDos(@"\Device\HarddiskVolume10\f", map));
        Assert.Equal(@"D:\f", KernelFileParser.MapToDos(@"\Device\HarddiskVolume1\f", map));
    }
}

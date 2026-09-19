using DiskEye.Etw;

namespace DiskEye.Tests;

public class FrameProtocolTests
{
    [Fact]
    public void Open_RoundTrip()
    {
        var line = FrameProtocol.Open(1234, "node.exe", false, @"D:\proj\a.txt");
        Assert.True(FrameProtocol.TryParse(line, out var f));
        Assert.Equal(FrameKind.Open, f.Kind);
        Assert.Equal(1234, f.Pid);
        Assert.Equal("node.exe", f.Name);
        Assert.False(f.IsDir);
        Assert.Equal(@"D:\proj\a.txt", f.Path);
    }

    [Fact]
    public void Open_DirectoryFlag_Preserved()
    {
        Assert.True(FrameProtocol.TryParse(FrameProtocol.Open(5, "x.exe", true, @"D:\tmp"), out var f));
        Assert.True(f.IsDir);
    }

    [Fact]
    public void Write_Bytes_Parsed()
    {
        Assert.True(FrameProtocol.TryParse(FrameProtocol.Write(7, 1_000_000, @"D:\f"), out var f));
        Assert.Equal(FrameKind.Write, f.Kind);
        Assert.Equal(1_000_000, f.Bytes);
    }

    [Fact]
    public void Cleanup_Delete_UsePath_Rename_UsesOldPath()
    {
        Assert.True(FrameProtocol.TryParse(FrameProtocol.Cleanup(1, @"D:\a"), out var cl));
        Assert.Equal(FrameKind.Cleanup, cl.Kind);
        Assert.Equal(@"D:\a", cl.Path);

        Assert.True(FrameProtocol.TryParse(FrameProtocol.Delete(2, @"D:\b"), out var dl));
        Assert.Equal(FrameKind.Delete, dl.Kind);
        Assert.Equal(@"D:\b", dl.Path);

        Assert.True(FrameProtocol.TryParse(FrameProtocol.Rename(3, @"D:\old"), out var rn));
        Assert.Equal(FrameKind.Rename, rn.Kind);
        Assert.Equal(@"D:\old", rn.OldPath);
        Assert.Equal("", rn.Path);
    }

    [Fact]
    public void Name_RoundTrip()
    {
        Assert.True(FrameProtocol.TryParse(FrameProtocol.Name(9, "pg.exe", @"C:\pg\pg.exe"), out var f));
        Assert.Equal("pg.exe", f.Name);
        Assert.Equal(@"C:\pg\pg.exe", f.ExePath);
    }

    [Fact]
    public void Stats_RoundTrip()
    {
        var h = new FrameHealth(10, 3, 42, 100, 50, 20, 2, 1);
        Assert.True(FrameProtocol.TryParse(FrameProtocol.Stats(h), out var f));
        Assert.Equal(FrameKind.Stats, f.Kind);
        Assert.NotNull(f.Health);
        Assert.Equal(10, f.Health!.EventsLost);
        Assert.Equal(3, f.Health.DroppedFrames);
        Assert.Equal(42, f.Health.QueueDepth);
    }

    [Fact]
    public void Ready_Heartbeat()
    {
        Assert.True(FrameProtocol.TryParse("READY", out var r));
        Assert.Equal(FrameKind.Ready, r.Kind);
        Assert.True(FrameProtocol.TryParse("HB", out var h));
        Assert.Equal(FrameKind.Heartbeat, h.Kind);
    }

    [Theory]
    [InlineData("OP abc")]                      // 有类型但结构残缺
    [InlineData("OP 0\tnode\t0\tD:\\a")]         // pid 0
    [InlineData("OP -1\tnode\t0\tD:\\a")]
    [InlineData("WR 5\tnotnum\tD:\\a")]
    [InlineData("WR 5\t100\t")]                   // 空路径
    [InlineData("NM 5\t\tD:\\exe")]              // 空名字
    [InlineData("CL 5")]                          // 缺路径
    [InlineData("")]
    [InlineData("   ")]
    public void Malformed_ReturnsFalse(string line)
    {
        Assert.False(FrameProtocol.TryParse(line, out _));
    }

    [Fact]
    public void UnknownKind_ReturnsTrue_AsUnknown()
    {
        // 新版本下发的未知帧：结构完整，调用方安全忽略
        Assert.True(FrameProtocol.TryParse("ZZ some future payload", out var f));
        Assert.Equal(FrameKind.Unknown, f.Kind);
    }

    [Fact]
    public void Sanitize_TabsAndNewlines_Replaced()
    {
        var evil = "a\tb\rc\nd";
        var line = FrameProtocol.Open(1, evil, false, evil);
        // 消毒后 OP 帧恰好 3 个分隔符（4 段）
        Assert.Equal(4, line.Split('\t').Length);
        Assert.True(FrameProtocol.TryParse(line, out var f));
        Assert.Equal("a b c d", f.Name);
        Assert.Equal("a b c d", f.Path);
    }
}

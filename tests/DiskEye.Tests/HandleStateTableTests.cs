using DiskEye.Etw;

namespace DiskEye.Tests;

public class HandleStateTableTests
{
    private const ulong FO1 = 0x1000;
    private const ulong FO2 = 0x2000;

    [Fact]
    public void Write_Aggregates_PerFileObject_UntilDrain()
    {
        var t = new HandleStateTable();
        t.OnCreate(FO1, 100, @"D:\a.bin", false);
        Assert.True(t.OnWrite(FO1, 12345));
        Assert.True(t.OnWrite(FO1, 777));

        var drain = t.DrainWrites();
        Assert.Single(drain);
        Assert.Equal((100, @"D:\a.bin", 12345 + 777L), (drain[0].Pid, drain[0].DosPath, drain[0].Bytes));

        // 二次 Drain：无新写入 → 空
        Assert.Empty(t.DrainWrites());
    }

    [Fact]
    public void MultipleFileObjects_SamePath_AreTrackedIndependently()
    {
        var t = new HandleStateTable();
        t.OnCreate(FO1, 1, @"D:\shared.log", false);
        t.OnCreate(FO2, 2, @"D:\shared.log", false);
        t.OnWrite(FO1, 100);
        t.OnWrite(FO2, 200);

        var drain = t.DrainWrites();
        Assert.Equal(2, drain.Count);
        Assert.Equal(300, drain.Sum(s => s.Bytes));
        Assert.Contains(drain, s => s.Pid == 1 && s.Bytes == 100);
        Assert.Contains(drain, s => s.Pid == 2 && s.Bytes == 200);
    }

    [Fact]
    public void Write_ForUnknownFileObject_ReturnsFalse()
    {
        var t = new HandleStateTable();
        Assert.False(t.OnWrite(FO1, 10));
    }

    [Fact]
    public void ZeroWrite_IsIgnored()
    {
        var t = new HandleStateTable();
        t.OnCreate(FO1, 1, @"D:\a", false);
        Assert.False(t.OnWrite(FO1, 0));
        Assert.Empty(t.DrainWrites());
    }

    [Fact]
    public void Cleanup_RemovesHandle_AndReturnsOwner()
    {
        var t = new HandleStateTable();
        t.OnCreate(FO1, 55, @"D:\x.tmp", false);
        var removed = t.OnCleanup(FO1);
        Assert.NotNull(removed);
        Assert.Equal((55, @"D:\x.tmp", false, 0L), removed!.Value);

        // 关闭后再来的写/查都失败（句柄已不存在）
        Assert.False(t.OnWrite(FO1, 10));
        Assert.Null(t.Lookup(FO1));
        Assert.Null(t.OnCleanup(FO1));
    }

    [Fact]
    public void ShortLivedHandle_Cleanup_ReturnsResidualWriteBytes()
    {
        // 1 秒聚合窗内写+关：Cleanup 必须带出残留字节，不能丢
        var t = new HandleStateTable();
        t.OnCreate(FO1, 9, @"D:\quick.tmp", false);
        t.OnWrite(FO1, 12345);
        // 未到 Drain 周期就 Cleanup
        var removed = t.OnCleanup(FO1)!.Value;
        Assert.Equal(12345, removed.ResidualBytes);
        Assert.Empty(t.DrainWrites());  // 已随 Cleanup 冲刷
    }

    [Fact]
    public void Lookup_Delete_Rename_Survives_UntilCleanup()
    {
        var t = new HandleStateTable();
        t.OnCreate(FO1, 7, @"D:\old.txt", false);
        var hit = t.Lookup(FO1);
        Assert.Equal(7, hit!.Value.Pid);
        Assert.Equal(@"D:\old.txt", hit.Value.DosPath);
    }

    [Fact]
    public void Directory_Create_Flag_Retained()
    {
        var t = new HandleStateTable();
        t.OnCreate(FO1, 9, @"D:\tmp", true);
        Assert.True(t.Lookup(FO1)!.Value.IsDir);
        Assert.True(t.OnCleanup(FO1)!.Value.IsDir);
    }

    [Fact]
    public void InvalidArgs_AreIgnored()
    {
        var t = new HandleStateTable();
        t.OnCreate(0, 1, "x", false);
        t.OnCreate(FO1, 0, "x", false);
        t.OnCreate(FO1, 1, "", false);
        Assert.Equal(0, t.ActiveCount);
    }

    [Fact]
    public void Recreate_SameFileObject_ResetsAggregation()
    {
        var t = new HandleStateTable();
        t.OnCreate(FO1, 1, @"D:\a", false);
        t.OnWrite(FO1, 500);
        t.DrainWrites();
        // 同一 FO 重新 Create（内核一般不复用，但逻辑上刷新）
        t.OnCreate(FO1, 1, @"D:\a", false);
        Assert.Empty(t.DrainWrites());
    }
}

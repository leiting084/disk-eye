using DiskEye.Etw;
using DiskEye.Models;
using DiskEye.Monitoring;

namespace DiskEye.Tests;

public class AttributionEngineTests
{
    private sealed class FakeNames : INameResolver
    {
        public (string Name, string ExePath) Resolve(int pid) => ($"pid{pid}.exe", $@"C:\bin\pid{pid}.exe");
    }

    private sealed class Clock
    {
        public long T;
        public long Now() => T;
        public void Advance(long ms) => T += ms;
    }

    private static FileSystemWatcherMonitor.RawEvent Ev(string type, string path, string? old = null)
        => new(DateTime.Now, "D", path, type, 0, old);

    private static Frame Open(int pid, string path, bool isDir = false, string name = "")
        => new(FrameKind.Open, Pid: pid, Path: path, IsDir: isDir, Name: name);

    private static Frame Write(int pid, long bytes, string path, string name = "")
        => new(FrameKind.Write, Pid: pid, Bytes: bytes, Path: path, Name: name);

    private AttributionEngine NewEngine(Clock c, bool v2 = true)
        => new(v2, new FakeNames(), c.Now);

    [Fact]
    public void LongLivedHandle_Opener_BeyondOld4sTtl()
    {
        var c = new Clock();
        var e = NewEngine(c);
        e.ApplyFrame(Open(100, @"D:\db.sqlite"));
        c.Advance(35_000);  // 跨过 v0.8.1 的 4 秒 TTL
        var r = e.Resolve(Ev("Modified", @"D:\db.sqlite"));
        Assert.Equal(AttrResultKind.Opener, r.Kind);
        Assert.Equal(100, r.Pid);
        Assert.Equal("pid100.exe", r.Name);
    }

    [Fact]
    public void Writer_Beats_Opener_And_ReaderNeverWins()
    {
        var c = new Clock();
        var e = NewEngine(c);
        // pid 10 只是打开（杀软式扫描），pid 11 真正写入
        e.ApplyFrame(Open(10, @"D:\f.bin"));
        e.ApplyFrame(Write(11, 500, @"D:\f.bin"));
        var r = e.Resolve(Ev("Modified", @"D:\f.bin"));
        Assert.Equal(AttrResultKind.Writer, r.Kind);
        Assert.Equal(11, r.Pid);
    }

    [Fact]
    public void Cleanup_MovesOwner_ToRecent_ForTrailingWrites()
    {
        var c = new Clock();
        var e = NewEngine(c);
        e.ApplyFrame(Open(20, @"D:\a.log"));
        e.ApplyFrame(new Frame(FrameKind.Cleanup, Pid: 20, Path: @"D:\a.log"));
        var r = e.Resolve(Ev("Modified", @"D:\a.log"));
        Assert.Equal(AttrResultKind.Recent, r.Kind);
        Assert.Equal(20, r.Pid);
    }

    [Fact]
    public void Recent_Expires_AfterTtl()
    {
        var c = new Clock();
        var e = new AttributionEngine(true, new FakeNames(), c.Now, recentTtlMs: 30_000);
        e.ApplyFrame(Open(20, @"D:\a.log"));
        e.ApplyFrame(new Frame(FrameKind.Cleanup, Pid: 20, Path: @"D:\a.log"));
        c.Advance(31_000);
        Assert.Equal(AttrResultKind.Pending, e.Resolve(Ev("Modified", @"D:\a.log")).Kind);
    }

    [Fact]
    public void DirectoryEvents_AreDropped_ButFilesInside_Kept()
    {
        var c = new Clock();
        var e = NewEngine(c);
        e.ApplyFrame(Open(30, @"D:\tmp", isDir: true));
        Assert.Equal(AttrResultKind.Dropped, e.Resolve(Ev("Modified", @"D:\tmp")).Kind);
        // 目录里的文件正常归因
        e.ApplyFrame(Open(31, @"D:\tmp\f.tmp"));
        Assert.Equal(AttrResultKind.Opener, e.Resolve(Ev("Created", @"D:\tmp\f.tmp")).Kind);
    }

    [Fact]
    public void DeleteFrame_AttributesDeletion()
    {
        var c = new Clock();
        var e = NewEngine(c);
        e.ApplyFrame(new Frame(FrameKind.Delete, Pid: 40, Path: @"D:\gone.tmp"));
        var r = e.Resolve(Ev("Deleted", @"D:\gone.tmp"));
        Assert.Equal(AttrResultKind.DeletedFrame, r.Kind);
        Assert.Equal(40, r.Pid);
    }

    [Fact]
    public void DeleteClaim_Expires_ButRecent_StillAttributes()
    {
        var c = new Clock();
        var e = NewEngine(c);
        e.ApplyFrame(new Frame(FrameKind.Delete, Pid: 40, Path: @"D:\gone.tmp"));
        c.Advance(11_000);
        // 删除帧（10s）过期后，删除事件仍由 recent（30min）兜底归因
        Assert.Equal(AttrResultKind.Recent, e.Resolve(Ev("Deleted", @"D:\gone.tmp")).Kind);
        Assert.Equal(40, e.Resolve(Ev("Deleted", @"D:\gone.tmp")).Pid);
    }

    [Fact]
    public void Rename_MigratesOwner_ThenWritesFollowNewPath()
    {
        var c = new Clock();
        var e = NewEngine(c);
        e.ApplyFrame(Open(50, @"D:\old.txt"));
        e.ApplyFrame(Write(50, 10, @"D:\old.txt"));

        var r = e.Resolve(Ev("Renamed", @"D:\new.txt", @"D:\old.txt"));
        Assert.Equal(AttrResultKind.Renamed, r.Kind);
        Assert.Equal(50, r.Pid);

        // 迁移后新路径的写入归同一 pid；旧路径不再可归因
        Assert.Equal(AttrResultKind.Writer, e.Resolve(Ev("Modified", @"D:\new.txt")).Kind);
    }

    [Fact]
    public void UnknownPath_IsPending()
    {
        var e = NewEngine(new Clock());
        Assert.Equal(AttrResultKind.Pending, e.Resolve(Ev("Modified", @"D:\never")).Kind);
    }

    [Fact]
    public void PendingRow_Backfills_WhenOwnerFrameArrives()
    {
        var c = new Clock();
        var e = NewEngine(c);
        // 先来 FSW 事件但还没 ETW 帧 → pending 入库
        Assert.Equal(AttrResultKind.Pending, e.Resolve(Ev("Created", @"D:\late.bin")).Kind);
        e.RegisterPending(@"D:\late.bin", rowId: 1001);

        List<PendingBackfill>? got = null;
        e.OnBackfill += b => got = b;
        e.ApplyFrame(Open(60, @"D:\late.bin", name: "pid60.exe"));

        Assert.NotNull(got);
        Assert.Single(got!);
        Assert.Equal(1001, got![0].RowId);
        Assert.Equal(60, got[0].Result.Pid);
    }

    [Fact]
    public void PendingRow_NotBackfilled_After10s()
    {
        var c = new Clock();
        var e = NewEngine(c);
        e.RegisterPending(@"D:\late.bin", 1);
        c.Advance(31_000);  // V0.9.1: pending 回填窗口 30 秒
        List<PendingBackfill>? got = null;
        e.OnBackfill += b => got = b;
        e.ApplyFrame(Open(60, @"D:\late.bin"));
        Assert.Null(got);  // 超时不回填
    }

    [Fact]
    public void WriteFrame_RaisesWriteSample_WithRealBytes()
    {
        var e = NewEngine(new Clock());
        WriteSample? sample = null;
        e.OnWriteBytes += s => sample = s;
        e.ApplyFrame(Write(70, 12345, @"D:\sub\w.bin", "pid70.exe"));
        Assert.NotNull(sample);
        Assert.Equal(12345, sample!.Bytes);
        Assert.Equal(70, sample.Pid);
        Assert.Equal(@"D:\sub\", sample.Folder);
    }

    [Fact]
    public void V1Mode_Uses4sTtl_AndNoDirectoryFilter()
    {
        var c = new Clock();
        var e = NewEngine(c, v2: false);
        e.ApplyFrame(Open(80, @"D:\x"));
        Assert.Equal(AttrResultKind.Opener, e.Resolve(Ev("Modified", @"D:\x")).Kind);
        Assert.Equal("v1", e.Resolve(Ev("Modified", @"D:\x")).Source);

        c.Advance(5_000);
        Assert.Equal(AttrResultKind.Pending, e.Resolve(Ev("Modified", @"D:\x")).Kind);

        // v1 不过滤目录
        e.ApplyFrame(Open(81, @"D:\dir", isDir: true));
        Assert.Equal(AttrResultKind.Opener, e.Resolve(Ev("Modified", @"D:\dir")).Kind);
    }

    [Fact]
    public void NameFrame_FillsProcessName()
    {
        var e = NewEngine(new Clock());
        e.ApplyFrame(new Frame(FrameKind.Name, Pid: 90, Name: "svc.exe", ExePath: @"C:\svc\svc.exe"));
        e.ApplyFrame(Open(90, @"D:\z"));
        var r = e.Resolve(Ev("Modified", @"D:\z"));
        Assert.Equal("svc.exe", r.Name);
    }

    [Fact]
    public void CaseAndTrailingSlash_AreNormalized()
    {
        var e = NewEngine(new Clock());
        e.ApplyFrame(Open(1, @"D:\Data\file"));
        Assert.Equal(AttrResultKind.Opener, e.Resolve(Ev("Modified", @"d:\DATA\file\")).Kind);
    }
}

using DiskEye.Models;
using DiskEye.Storage;

namespace DiskEye.Tests;

/// <summary>
/// V0.9.1 回归：write_bytes 里有、今天 events 表没有的进程（典型：chrome 等只写字节）
/// 曾导致 QueryProcessUnified/QueryFolderUnified 解引用 null 抛 NRE，整个榜单静默失败。
/// </summary>
public class UnifiedQueryTests : IDisposable
{
    private readonly string _dir;
    private readonly EventStore _store;
    private static readonly DateTime Now = new(2026, 9, 16, 10, 0, 0, DateTimeKind.Local);

    public UnifiedQueryTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "diskeye_ut_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _store = new EventStore(Path.Combine(_dir, "events.db"));
    }

    [Fact]
    public void Process_PresentOnlyInWriteBytes_DoesNotThrow_AndAppears()
    {
        // events 里只有 app1
        _store.InsertBatch(new[]
        {
            new FileEvent(0, Now, "D", @"D:\a\app1\f.txt", "Modified", 10, 1, "app1.exe", @"D:\a\app1\app1.exe"),
        });
        // write_bytes 里 app1 + chrome（chrome 在 events 今天没有行）
        _store.InsertWriteBatch(new[]
        {
            new WriteSample(Now, 1, "app1.exe", @"D:\a\app1\app1.exe", @"D:\a\app1\f.txt", @"D:\a\app1\", 100),
            new WriteSample(Now, 2, "chrome.exe", @"C:\chrome\chrome.exe", @"D:\cache\x", @"D:\cache\", 50_000_000),
        });

        List<EventStore.UnifiedRow> rows;
        var ex = Record.Exception(() => rows = _store.QueryProcessUnified(Now.Date));
        Assert.Null(ex);
        rows = _store.QueryProcessUnified(Now.Date);

        Assert.Contains(rows, r => r.Name == "chrome.exe" && r.WriteBytes == 50_000_000 && r.EventCount == 0);
        Assert.Contains(rows, r => r.Name == "app1.exe" && r.WriteBytes == 100 && r.EventCount == 1);
        // chrome 字节最多排第一
        Assert.Equal("chrome.exe", rows[0].Name);
    }

    [Fact]
    public void Folder_PresentOnlyInWriteBytes_DoesNotThrow()
    {
        _store.InsertWriteBatch(new[]
        {
            new WriteSample(Now, 3, "x.exe", "", @"D:\onlywrite\f.bin", @"D:\onlywrite\", 777),
        });
        List<EventStore.UnifiedRow> rows;
        var ex = Record.Exception(() => rows = _store.QueryFolderUnified(Now.Date));
        Assert.Null(ex);
        rows = _store.QueryFolderUnified(Now.Date);
        Assert.Contains(rows, r => r.Name == @"D:\onlywrite\" && r.WriteBytes == 777);
    }

    public void Dispose()
    {
        _store.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
    }
}

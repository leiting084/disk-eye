using System.IO;
using System.Linq;
using System.Text;
using DiskEye.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace DiskEye.Tests;

/// <summary>
/// V0.9.17 启动自愈的端到端测试：在临时库上造脏数据 → 跑迁移 → 断言"该修的修了、
/// 不该动的没动、再跑一次不重复改"。
///
/// 脏数据一律用 CP936 现场构造（见 Corrupt），不硬编码乱码串：污染源就是 CP936 解码器，
/// 用它生成样本才是真实形态。
/// </summary>
public class MojibakeMigrationTests : IDisposable
{
    private readonly string _dir;
    private readonly string _dbPath;

    public MojibakeMigrationTests()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        _dir = Path.Combine(Path.GetTempPath(), "diskeye-mojibake-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _dbPath = Path.Combine(_dir, "events.db");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    /// <summary>把正常文本"污染"成当年落库的形态：UTF-8 字节被宽松 CP936 解码。</summary>
    private static string Corrupt(string text)
    {
        var loose = Encoding.GetEncoding(936);   // 宽松 = 与 Console.OutputEncoding 当年的行为一致
        return loose.GetString(new UTF8Encoding(false, true).GetBytes(text));
    }

    private const string GoodPath = "D:\\tools\\0.mytools\\DiskEye 磁盘监控\\etw_child.log";
    private const string GoodFolder = "D:\\tools\\0.mytools\\DiskEye 磁盘监控\\";
    private const string GoodExe = "D:\\tools\\0.mytools\\DiskEye 磁盘监控\\DiskEye.exe";
    private const string HealthyChinesePath = "D:\\个人桌面\\桌面\\当前工作\\2025-9-20.txt";
    private const string HealthyChineseFolder = "D:\\个人桌面\\桌面\\当前工作\\";
    private const string AsciiPath = "D:\\application\\apache\\logs\\access.log";

    [Fact]
    public void RepairsMojibakeRows_LeavesHealthyRowsUntouched_AndIsIdempotent()
    {
        using (var store = new EventStore(_dbPath, scheduleStartupRepair: false))
        {
            InsertEvent(Corrupt(GoodPath), Corrupt(GoodFolder), "DiskEye.exe", Corrupt(GoodExe));
            InsertEvent(HealthyChinesePath, HealthyChineseFolder, "Notepad.exe", "C:\\Windows\\notepad.exe");
            InsertEvent(AsciiPath, "D:\\application\\apache\\logs\\", "httpd.exe", "D:\\application\\apache\\bin\\httpd.exe");

            int repaired = store.RepairLegacyMojibake();
            Assert.Equal(3, repaired);   // 只有那条脏行的 path/folder/process_path（process_name 是纯 ASCII）
        }

        var rows = ReadEvents();
        Assert.Equal(3, rows.Count);

        var fixedRow = rows.Single(r => r.ProcessName == "DiskEye.exe");
        Assert.Equal(GoodPath, fixedRow.Path);
        Assert.Equal(GoodFolder, fixedRow.Folder);
        Assert.Equal(GoodExe, fixedRow.ProcessPath);

        // 健康数据必须原样 —— 这是自愈最危险的失败模式
        var healthy = rows.Single(r => r.ProcessName == "Notepad.exe");
        Assert.Equal(HealthyChinesePath, healthy.Path);
        Assert.Equal(HealthyChineseFolder, healthy.Folder);

        var ascii = rows.Single(r => r.ProcessName == "httpd.exe");
        Assert.Equal(AsciiPath, ascii.Path);

        // 幂等：再跑一次必须零改动，否则每次启动都在改数据
        using (var store = new EventStore(_dbPath, scheduleStartupRepair: false))
        {
            Assert.Equal(0, store.RepairLegacyMojibake());
        }

        // 回滚脚本要落盘 —— 升级出问题时用户能自己退回去
        var rollback = Directory.GetFiles(_dir, "mojibake-rollback-*.sql");
        Assert.Single(rollback);
        var sql = File.ReadAllText(rollback[0]);
        Assert.Contains("UPDATE \"events\"", sql);
        Assert.Contains(Corrupt(GoodPath), sql);   // 存的是修改前的值（乱码），才能回滚
    }

    [Fact]
    public void MarksUserVersion_SoLaterStartsSkipTheScan()
    {
        using (var store = new EventStore(_dbPath, scheduleStartupRepair: false))
        {
            InsertEvent(Corrupt(GoodPath), Corrupt(GoodFolder), "DiskEye.exe", Corrupt(GoodExe));
            Assert.True(store.RepairLegacyMojibake() > 0);
        }

        // 标记写进去了吗
        using (var conn = new SqliteConnection($"Data Source={_dbPath}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA user_version;";
            Assert.Equal(1L, System.Convert.ToInt64(cmd.ExecuteScalar()));
        }

        // 用公开构造函数（会自动挂后台自愈）再开一次：user_version 已标记 → 不再扫
        using (var store = new EventStore(_dbPath))
        {
            Assert.Equal(0, store.RepairLegacyMojibake());
        }
    }

    private void InsertEvent(string path, string folder, string processName, string processPath)
    {
        using var conn = new SqliteConnection($"Data Source={_dbPath};Mode=ReadWrite;Cache=Shared");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO events (ts, drive, path, folder, event_type, size, pid, process_name, process_path, source)
            VALUES ($ts, 'D', $path, $folder, 'Modified', 1024, 42, $pname, $ppath, 'v2-recent')";
        cmd.Parameters.AddWithValue("$ts", "2026-09-27T04:00:00.0000000+08:00");
        cmd.Parameters.AddWithValue("$path", path);
        cmd.Parameters.AddWithValue("$folder", folder);
        cmd.Parameters.AddWithValue("$pname", processName);
        cmd.Parameters.AddWithValue("$ppath", processPath);
        cmd.ExecuteNonQuery();
    }

    private List<(string Path, string Folder, string ProcessName, string ProcessPath)> ReadEvents()
    {
        using var conn = new SqliteConnection($"Data Source={_dbPath};Mode=ReadOnly");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT path, folder, process_name, process_path FROM events";
        var list = new List<(string, string, string, string)>();
        using var rdr = cmd.ExecuteReader();
        while (rdr.Read())
            list.Add((rdr.GetString(0), rdr.GetString(1), rdr.GetString(2), rdr.GetString(3)));
        return list;
    }
}

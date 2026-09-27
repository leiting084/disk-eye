using System.IO;
using System.Linq;
using DiskEye.Storage;
using Microsoft.Data.Sqlite;
using Xunit;
using Xunit.Abstractions;

namespace DiskEye.Tests;

/// <summary>
/// 迁移预演工具：不改任何数据，只报告"如果现在跑自愈，会动哪些行、改成什么"。
///
/// 默认不执行（免得不小心动到开发机上的真实库）。要预演时指定目标库路径：
///   set DISKEYE_PREVIEW_DB=D:\path\to\events.db
///   dotnet test --filter MojibakePreviewTool
///
/// 存在的理由：自愈是会改用户数据的操作，"跑了才发现改了多少"太晚。上真库前先在这里看一眼。
/// </summary>
public class MojibakePreviewTool
{
    private readonly ITestOutputHelper _out;

    public MojibakePreviewTool(ITestOutputHelper output) => _out = output;

    [Fact]
    public void Preview_RepairsWithoutWriting()
    {
        var dbPath = System.Environment.GetEnvironmentVariable("DISKEYE_PREVIEW_DB");
        if (string.IsNullOrWhiteSpace(dbPath) || !File.Exists(dbPath)) return;   // 未配置 → 空跑

        var targets = new (string Table, string[] Cols)[]
        {
            ("events", new[] { "path", "folder", "process_name", "process_path" }),
            ("write_bytes", new[] { "path", "folder", "process_name", "process_path" }),
            ("events_archive", new[] { "path", "folder", "process_name", "process_path" }),
        };

        using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
        conn.Open();

        int totalRows = 0, repairable = 0;
        foreach (var (table, cols) in targets)
        {
            foreach (var col in cols)
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = $"SELECT rowid, \"{col}\" FROM \"{table}\"";
                using var rdr = cmd.ExecuteReader();
                while (rdr.Read())
                {
                    totalRows++;
                    string oldValue;
                    try { oldValue = rdr.GetString(1); }
                    catch { continue; }
                    var fixedValue = MojibakeRepair.TryRepair(oldValue);
                    if (fixedValue == null) continue;
                    repairable++;
                    if (repairable <= 15)
                    {
                        _out.WriteLine($"[{table}.{col} rowid={rdr.GetInt64(0)}]");
                        _out.WriteLine($"   now: {oldValue}");
                        _out.WriteLine($"   fix: {fixedValue}");
                    }
                }
            }
        }
        _out.WriteLine($"scanned fields: {totalRows}, repairable: {repairable}");
    }
}

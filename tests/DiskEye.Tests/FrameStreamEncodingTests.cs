using System.Text;
using DiskEye.Etw;
using DiskEye.Monitoring;

namespace DiskEye.Tests;

/// <summary>
/// V0.9.16 回归：主从 stdout 管道两端必须显式同编码（无 BOM UTF-8）。
///
/// 历史 bug：子进程用 UTF8Encoding(false) 写 stdout，主进程创建子进程时**没有**设
/// StandardOutputEncoding，.NET 便退回 Console.OutputEncoding —— 中文系统是 CP936(GBK)，
/// 于是所有走管道的非 ASCII 路径被按 GBK 解读：
///   D:\tools\0.mytools\DiskEye 磁盘监控\etw_child.log
/// → D:\tools\0.mytools\DiskEye 纾佺洏鐩戞帶\etw_child.log
/// 只有 write_bytes 表（WR 帧）中招，events 表未中招是因为那些行来自主进程自己的
/// FileSystemWatcher，不经管道。所以这类 bug 只会在「路径含中文」的机器上暴露。
/// </summary>
public class FrameStreamEncodingTests
{
    private const string CnDir = @"D:\tools\0.mytools\DiskEye 磁盘监控";

    [Fact]
    public void CreateChildStartInfo_SetsExplicitOutputEncoding()
    {
        var psi = EtwFrameClient.CreateChildStartInfo(@"C:\tools\DiskEye.exe", 1234);

        Assert.True(psi.RedirectStandardOutput);
        Assert.NotNull(psi.StandardOutputEncoding);
        Assert.Equal(FrameProtocol.StreamEncoding, psi.StandardOutputEncoding);
    }

    [Fact]
    public void StreamEncoding_IsUtf8WithoutBom()
    {
        Assert.Equal("utf-8", FrameProtocol.StreamEncoding.WebName);
        // 无 BOM：子进程首行必须是协议版本 "V1"，BOM 会让首行判定失败而整条链路降级
        Assert.Empty(FrameProtocol.StreamEncoding.GetPreamble());
    }

    [Fact]
    public void Frames_SurvivePipeRoundTrip_WithNonAsciiPaths()
    {
        var frames = new[]
        {
            FrameProtocol.ProtocolVersion,
            FrameProtocol.Open(4321, "DiskEye.exe", false, CnDir + @"\events.db"),
            FrameProtocol.Write(4321, 4096, CnDir + @"\events.db-wal"),
            FrameProtocol.Cleanup(4321, CnDir + @"\startup.log"),
            FrameProtocol.Name(4321, "DiskEye.exe", CnDir + @"\DiskEye.exe"),
            FrameProtocol.Rename(4321, @"D:\临时\旧名.txt"),
            FrameProtocol.Open(99, "node.exe", true, @"D:\tools\0.mytools\智能待办清单"),
        };

        using var pipe = new MemoryStream();
        // 写端 = EtwChildWorker（子进程 stdout）
        using (var w = new StreamWriter(pipe, FrameProtocol.StreamEncoding, 1024, leaveOpen: true))
        {
            foreach (var raw in frames) w.WriteLine(raw);
        }
        pipe.Position = 0;

        // 读端 = EtwFrameClient.ReadLoop（StandardOutputEncoding）
        using var r = new StreamReader(pipe, FrameProtocol.StreamEncoding);
        // 首行是协议版本，ReadLoop 先消费它再逐帧解析
        Assert.Equal(FrameProtocol.ProtocolVersion, r.ReadLine());
        var got = new List<Frame>();
        string? line;
        while ((line = r.ReadLine()) != null)
        {
            Assert.True(FrameProtocol.TryParse(line, out var f));
            got.Add(f);
        }

        Assert.Equal(frames.Length - 1, got.Count);
        Assert.Equal(CnDir + @"\events.db", got[0].Path);
        Assert.Equal(CnDir + @"\events.db-wal", got[1].Path);
        Assert.Equal(CnDir + @"\startup.log", got[2].Path);
        Assert.Equal(CnDir + @"\DiskEye.exe", got[3].ExePath);
        Assert.Equal(@"D:\临时\旧名.txt", got[4].OldPath);
        Assert.Equal(@"D:\tools\0.mytools\智能待办清单", got[5].Path);
        Assert.True(got[5].IsDir);
    }

    [Fact]
    public void PipeBytes_AreUtf8_NotAnsi()
    {
        //「磁盘监控」在 UTF-8 下是 12 字节（每汉字 3 字节），GBK 下只有 8 字节。
        // 子进程按 UTF-8 写、主进程按 GBK 读，就是当年 write_bytes 表里的乱码来源。
        var bytes = FrameProtocol.StreamEncoding.GetBytes("磁盘监控");
        Assert.Equal(12, bytes.Length);
        Assert.Equal("磁盘监控", FrameProtocol.StreamEncoding.GetString(bytes));
        Assert.NotEqual("磁盘监控", Encoding.Latin1.GetString(bytes));
    }
}

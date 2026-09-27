using System.Linq;
using System.Text;
using DiskEye.Storage;
using Xunit;

namespace DiskEye.Tests;

/// <summary>
/// V0.9.17 历史乱码自愈的回归测试。
///
/// 样本全部取自真实 events.db，不是编造的 —— 尤其是含私用区字符（U+E043）的那条：
/// 它是上一轮修复脚本漏掉的那批的代表，也是"必须用 GB18030 而非 GBK"的直接证据。
/// </summary>
public class MojibakeRepairTests
{
    [Fact]
    public void SystemCodePageTables_AreAvailable_OnThisMachine()
    {
        Assert.NotEmpty(MojibakeRepair.CodePages);
        Assert.Equal(936, MojibakeRepair.CodePages[0].CodePage);   // CP936 必须排第一
    }

    /// <summary>
    /// 回归钉子：全部用 CP936 本身构造样本，绝不手工/用别的码表推算乱码串。
    ///
    /// 踩过的坑：先按 Python 的 `gbk` 表推算出 `鏅鸿兘寰呬姟娓呭崟`，再断言能还原成
    /// `智能待办清单` —— 测试红了。因为 Python 的 gbk 用的是 Unicode 官方映射表，与 .NET 的
    /// CP936 表在个别码位（如 `呬`/`姟`）上并不一致，推出来的串根本不是 .NET 会产出的形态。
    /// 污染源既然是 CP936 的解码器，就得用 CP936 的编码器做逆运算，样本也只能由它生成。
    /// </summary>
    [Fact]
    public void RoundTrip_ThroughCp936_RecoversOriginal()
    {
        var cp936 = MojibakeRepair.CodePages.First(e => e.CodePage == 936);
        var utf8 = new UTF8Encoding(false, true);

        string[] originals =
        {
            "智能待办清单.exe",
            "DiskEye 磁盘监控",
            "D:\\个人桌面\\桌面\\当前工作\\2025-9-20.txt",
            "D:\\tools\\0.mytools\\DiskEye 磁盘监控\\etw_child.log",
            "经验总结2025-10-8.txt",
        };

        foreach (var original in originals)
        {
            var corrupt = cp936.GetString(utf8.GetBytes(original));   // 当年被写坏的形态
            Assert.NotEqual(original, corrupt);                       // 前提：确实变形了
            Assert.Equal(original, MojibakeRepair.TryRepair(corrupt));
        }
    }

    /// <summary>
    /// 含私用区字符的样本（上轮修复脚本整条跳过的那批）必须能还原 —— 这是本次修复的主因。
    /// 样本直接取自真实 events.db，未做任何人工改写。
    /// </summary>
    [Fact]
    public void Repairs_RealRowWithPrivateUseCharacter()
    {
        var realCorrupt = "D:\\涓\uE043汉妗岄潰\\妗岄潰\\褰撳墠宸ヤ綔\\2025-9-20.txt";
        Assert.Equal("D:\\个人桌面\\桌面\\当前工作\\2025-9-20.txt", MojibakeRepair.TryRepair(realCorrupt));
    }

    /// <summary>
    /// .NET 的 CP936 解码器遇到无法映射的字节对会替换成 '?'（Console.OutputEncoding 就是
    /// 这种宽松行为），这种串的信息已经永久丢失。自愈必须**放弃**它们，而不是写回一个
    /// 看起来像原文、实际错位的值。
    /// </summary>
    [Fact]
    public void RefusesStringsWhoseBytesWereAlreadyLost()
    {
        var lossy = MojibakeRepair.TryRepair("D:\\tools\\0.mytools\\鏅鸿兘寰呭姙娓?鍗昞data\\webview\\");
        Assert.Null(lossy);
    }

    /// <summary>最常见的形态：目录名被整体曲解（用户截图里那一类）。</summary>
    [Fact]
    public void Repairs_PlainMojibakePath()
    {
        var corrupt = "D:\\tools\\0.mytools\\DiskEye 纾佺洏鐩戞帶\\etw_child.log";
        Assert.Equal("D:\\tools\\0.mytools\\DiskEye 磁盘监控\\etw_child.log",
            MojibakeRepair.TryRepair(corrupt));
    }

    /// <summary>健康中文绝不能被碰 —— 这是自愈功能最危险的失败模式。</summary>
    [Theory]
    [InlineData("D:\\tools\\0.mytools\\DiskEye 磁盘监控\\etw_child.log")]
    [InlineData("D:\\个人桌面\\桌面\\当前工作\\2025-9-20.txt")]
    [InlineData("智能待办清单.exe")]
    [InlineData("DiskEye 磁盘监控")]
    [InlineData("C:\\Program Files\\WindowsApps\\Notepad\\Notepad.exe")]
    [InlineData("D:\\application\\apache\\logs\\")]
    [InlineData("")]
    [InlineData("   ")]
    public void NeverTouchesHealthyText(string healthy)
    {
        Assert.Null(MojibakeRepair.TryRepair(healthy));
    }

    /// <summary>幂等：修好的结果再进来必须原样不动，否则反复启动会把数据越改越坏。</summary>
    [Fact]
    public void Repair_IsIdempotent()
    {
        var once = MojibakeRepair.TryRepair("D:\\涓\uE043汉妗岄潰\\妗岄潰\\褰撳墠宸ヤ綔\\2025-9-20.txt");
        Assert.NotNull(once);
        Assert.Null(MojibakeRepair.TryRepair(once));
    }

    /// <summary>ASCII 路径（占绝大多数）必须走快速通道且零改动。</summary>
    [Fact]
    public void AsciiFastPath_ReturnsNull()
    {
        Assert.Null(MojibakeRepair.TryRepair("D:\\project\\toolproject\\jingyanjilei\\projects\\disk-eye\\README.md"));
    }
}

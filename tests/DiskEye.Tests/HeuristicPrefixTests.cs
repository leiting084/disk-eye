using DiskEye.Monitoring;

namespace DiskEye.Tests;

public class HeuristicPrefixTests
{
    private static readonly ISet<string> Sys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        @"C:\Windows\System32", @"C:\Program Files",
    };

    [Fact]
    public void Matches_ByExeDirectory_NotExeFile()
    {
        // v0.8.1 bug：用 exe 全路径 C:\tools\app\app.exe 永远 StartsWith 不上 logs\a.log
        var procs = new (int, string)[] { (10, @"C:\tools\app\app.exe") };
        var pid = EtwProcessResolver.BestByPrefixMatch(@"C:\tools\app\logs\a.log", procs, Sys);
        Assert.Equal(10, pid);
    }

    [Fact]
    public void SystemDirectories_AreExcluded()
    {
        var procs = new (int, string)[]
        {
            (1, @"C:\Windows\System32\svchost.exe"),
            (2, @"C:\Program Files\vendor\tool.exe"),
            (3, @"D:\dev\myapp\myapp.exe"),
        };
        var pid = EtwProcessResolver.BestByPrefixMatch(@"D:\dev\myapp\out\f.bin", procs, Sys);
        Assert.Equal(3, pid);
    }

    [Fact]
    public void NoMatch_ReturnsZero()
    {
        var procs = new (int, string)[] { (1, @"D:\dev\app\app.exe") };
        Assert.Equal(0, EtwProcessResolver.BestByPrefixMatch(@"E:\other\x.txt", procs, Sys));
    }

    [Fact]
    public void LongestPrefixWins()
    {
        var procs = new (int, string)[]
        {
            (1, @"D:\repo\outer.exe"),
            (2, @"D:\repo\nested\inner.exe"),
        };
        var pid = EtwProcessResolver.BestByPrefixMatch(@"D:\repo\nested\a.txt", procs, Sys);
        Assert.Equal(2, pid);
    }
}

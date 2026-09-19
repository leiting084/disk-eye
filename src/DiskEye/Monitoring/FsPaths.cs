namespace DiskEye.Monitoring;

/// <summary>路径小工具（AttributionEngine/EventStore 共用，保持 folder 口径一致）。</summary>
internal static class FsPaths
{
    /// <summary>"C:\foo\bar.txt" → "C:\foo\"；根目录/异常输入返回 ""。</summary>
    public static string GetFolder(string fullPath)
    {
        try
        {
            var dir = Path.GetDirectoryName(fullPath);
            return string.IsNullOrEmpty(dir) ? "" : dir + Path.DirectorySeparatorChar;
        }
        catch { return ""; }
    }
}

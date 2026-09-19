using System.Diagnostics;

namespace DiskEye.Monitoring;

/// <summary>
/// 引擎按需名字解析：先查 2 秒刷新的进程缓存；缓存未命中（典型：刚启动的短命进程，
/// 如脚本在几百 ms 内完成写+关）直接 Process.GetProcessById 实时解析，不再因缓存新鲜度漏名。
/// </summary>
internal sealed class ProcessCacheNameResolver : INameResolver
{
    private readonly EtwProcessResolver _resolver;
    public ProcessCacheNameResolver(EtwProcessResolver resolver) => _resolver = resolver;

    public (string Name, string ExePath) Resolve(int pid)
    {
        var cached = _resolver.GetProcessInfo(pid);
        if (!string.IsNullOrEmpty(cached.Name) && cached.Name != "unknown") return cached;

        try
        {
            using var p = Process.GetProcessById(pid);
            var name = p.ProcessName + ".exe";
            string exe = "";
            try { exe = p.MainModule?.FileName ?? ""; } catch { }
            return (name, exe);
        }
        catch
        {
            return ("unknown", "");
        }
    }
}

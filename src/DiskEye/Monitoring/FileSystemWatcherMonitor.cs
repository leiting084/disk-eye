using System.IO;

namespace DiskEye.Monitoring;

/// <summary>
/// 封装 FileSystemWatcher 的盘符级监控。
/// 每个盘符一个 watcher（递归），事件入队到共享队列。
/// </summary>
public sealed class FileSystemWatcherMonitor : IDisposable
{
    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly string _driveLetter;
    private readonly string _rootPath;
    private readonly Action<RawEvent> _onEvent;
    private bool _started;

    public string DriveLetter => _driveLetter;

    /// <summary>
    /// 原始事件。ProcessResolver 异步补 pid。
    /// V0.9：Renamed 只发一条，FullPath=新路径、OldFullPath=旧路径（v0.8 曾发旧/新两条）。
    /// </summary>
    public sealed record RawEvent(
        DateTime Timestamp,
        string DriveLetter,
        string FullPath,
        string EventType,    //  Created / Modified / Deleted / Renamed
        long SizeBytes,      //  Created/Modified/Renamed 时=文件当前大小快照；Deleted=0。V0.9 起不参与字节总量
        string? OldFullPath = null   //  仅 Renamed：旧路径
    );

    public FileSystemWatcherMonitor(string driveLetter, Action<RawEvent> onEvent)
    {
        _driveLetter = driveLetter.ToUpperInvariant();
        _rootPath = _driveLetter + ":\\";
        _onEvent = onEvent;
    }

    public void Start()
    {
        if (_started) return;
        if (!Directory.Exists(_rootPath))
        {
            throw new DirectoryNotFoundException($"盘符 {_driveLetter} 不存在");
        }

        CreateWatcher();
        _started = true;
    }

    private void CreateWatcher()
    {
        var w = new FileSystemWatcher(_rootPath)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName
                         | NotifyFilters.Size
                         | NotifyFilters.LastWrite
                         | NotifyFilters.DirectoryName
                         | NotifyFilters.Attributes,
            // CC 修复: 64KB 在大量文件变化场景（下载 1000+ 文件、编译大项目）会溢出。
            // 翻到 256KB，降低溢出概率。
            InternalBufferSize = 256 * 1024,
            EnableRaisingEvents = false,
        };

        w.Created += (s, e) => Emit(e.FullPath, "Created");
        w.Changed += (s, e) => Emit(e.FullPath, "Modified");
        w.Deleted += (s, e) => Emit(e.FullPath, "Deleted");
        w.Renamed += (s, e) =>
        {
            // V0.9：单条改名事件（新路径 + OldFullPath），归因引擎据此迁移路径属主
            Emit(e.FullPath, "Renamed", e.OldFullPath);
        };
        // CC 修复: buffer 溢出时，FileSystemWatcher 抛 Error，但内部 watcher 已停止。
        // 自动重建 watcher 避免永久丢事件。
        // V6.2 #3: 异步重建（避免阻塞 Error 事件分发）
        // CC 三审 P1 修复：加 5 秒节流，避免 Error 反复触发时 Task.Run 无限堆积
        var lastErrorAt = 0L;
        w.Error += (s, e) =>
        {
            System.Diagnostics.Debug.WriteLine($"[FSW {_driveLetter}] Error: {e.GetException().Message}");
            var nowTicks = DateTime.UtcNow.Ticks;
            if (nowTicks - lastErrorAt < TimeSpan.FromSeconds(5).Ticks) return;  // 节流
            lastErrorAt = nowTicks;
            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    w.EnableRaisingEvents = false;
                    w.Dispose();
                    _watchers.Remove(w);
                    CreateWatcher();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[FSW {_driveLetter}] 重建失败: {ex.Message}");
                }
            });
        };

        _watchers.Add(w);
        w.EnableRaisingEvents = true;
    }

    private void Emit(string fullPath, string eventType, string? oldFullPath = null)
    {
        // CC 修复: V3 路径白名单/黑名单过滤
        if (ShouldIgnore(fullPath)) return;

        long size = 0;
        if (eventType != "Deleted")
        {
            try
            {
                var info = new FileInfo(fullPath);
                if (info.Exists) size = info.Length;
            }
            catch { /* 文件可能已被删除或被锁，给 0 */ }
        }

        _onEvent(new RawEvent(
            Timestamp: DateTime.Now,
            DriveLetter: _driveLetter,
            FullPath: fullPath,
            EventType: eventType,
            SizeBytes: size,
            OldFullPath: oldFullPath
        ));
    }

    /// <summary>CC V3: 路径白名单/黑名单过滤。返回 true = 忽略该事件。</summary>
    private static bool ShouldIgnore(string fullPath)
    {
        var filters = PathFilterHolder.Current;
        if (filters == null) return false;

        // 黑名单优先（命中即忽略）
        foreach (var prefix in filters.IgnorePrefixes)
        {
            if (!string.IsNullOrEmpty(prefix) &&
                fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        // 白名单（命中即放行；空集合=不限）
        if (filters.IncludePrefixes.Count > 0)
        {
            foreach (var prefix in filters.IncludePrefixes)
            {
                if (!string.IsNullOrEmpty(prefix) &&
                    fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return false;
            }
            return true;  // 有白名单但路径不在其中 → 忽略
        }

        return false;
    }

    public void Dispose()
    {
        foreach (var w in _watchers)
        {
            try { w.EnableRaisingEvents = false; } catch { }
            w.Dispose();
        }
        _watchers.Clear();
    }
}

/// <summary>
/// 全局静态的路径过滤配置持有者。
/// 通过 PathFilterHolder.Current 在 Emit 时访问。
/// 在 MonitorService 启动时根据 AppConfig 设置。
/// </summary>
public static class PathFilterHolder
{
    private static PathFilter _current = new();
    public static PathFilter Current
    {
        get => _current;
        set => _current = value ?? new PathFilter();
    }
}

/// <summary>
/// 路径过滤配置：黑名单 + 白名单。
/// 命中黑名单 = 忽略；命中白名单 = 放行；都空 = 全部放行。
/// </summary>
public sealed class PathFilter
{
    public List<string> IgnorePrefixes { get; set; } = new();
    public List<string> IncludePrefixes { get; set; } = new();
}
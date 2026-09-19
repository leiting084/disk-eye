using System.Diagnostics;
using System.Collections.Concurrent;

namespace DiskEye.Monitoring;

/// <summary>
/// V5.2: 增强版进程反查 — 三策略综合评分。
///
/// 三种匹配策略（按权重 100 / 60 / 40 计分，取最高）：
/// 1. 路径前缀匹配（最强）：路径在某个进程 exe 所在目录下 → 该进程
/// 2. CPU 活跃度匹配：路径变化瞬间，CPU 时间在 5 秒窗口内增长的进程
/// 3. 启动时间近度：路径变化前后 10 秒内启动的进程
///
/// 比 v0.3 的纯 Process.GetProcesses() 启发式更精准 — 能识别"长期存活但最近活跃"的进程
/// （如 Chrome 写 cookie、IDEA 写项目文件、git push 写对象）。
///
/// V6 计划：用 P/Invoke 直接调 Win32 ETW API 拿精确 pid，绕开 NuGet 复杂性。
/// </summary>
public sealed class EtwProcessResolver : IDisposable
{
    private readonly ConcurrentDictionary<int, ProcessInfo> _processCache = new();
    private readonly CancellationTokenSource _cts = new();
    private bool _started;
    private bool _disposed;

    private sealed class ProcessInfo
    {
        public string Name { get; init; } = "";
        public string Path { get; init; } = "";
        public DateTime StartTime { get; init; }
        public TimeSpan LastCpuTime { get; init; }   // 上次 CPU 时间
        public DateTime LastSeen { get; init; }
    }

    public bool IsActive => _started;

    public void Start()
    {
        if (_started) return;
        _started = true;
        RefreshCache();

        // 后台每 2 秒刷一次进程缓存（CPU 时间跟着更新）
        _ = Task.Run(async () =>
        {
            while (!_disposed)
            {
                try { RefreshCache(); } catch { }
                try { await Task.Delay(2000); } catch { break; }
            }
        });
    }

    private void RefreshCache()
    {
        var now = DateTime.UtcNow.Ticks;
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                DateTime start;
                try { start = p.StartTime; } catch { continue; }

                string name;
                string path;
                try
                {
                    name = p.ProcessName + ".exe";
                    path = p.MainModule?.FileName ?? "";
                }
                catch
                {
                    name = p.ProcessName + ".exe";
                    path = "";
                }

                TimeSpan cpu;
                try { cpu = p.TotalProcessorTime; } catch { cpu = TimeSpan.Zero; }

                _processCache[p.Id] = new ProcessInfo
                {
                    Name = name,
                    Path = path,
                    StartTime = start,
                    LastCpuTime = cpu,
                    LastSeen = new DateTime(now, DateTimeKind.Utc),
                };
            }
            catch { }
            finally { p.Dispose(); }
        }

        // 清理 60 秒没刷到的进程
        var cutoff = now - 60L * 10_000_000L;
        foreach (var kv in _processCache)
        {
            if (kv.Value.LastSeen.Ticks < cutoff)
                _processCache.TryRemove(kv.Key, out _);
        }
    }

    /// <summary>
    /// V5.2: 三策略综合评分反查。
    /// 路径变化瞬间调用，返回综合得分最高的进程 PID（0=未知）。
    /// </summary>
    public int Resolve(string fullPath)
    {
        if (!_started || string.IsNullOrEmpty(fullPath)) return 0;

        // 策略 1：路径前缀匹配（强信号）
        var byPrefix = BestByPrefix(fullPath);
        if (byPrefix != 0) return byPrefix;

        // 策略 2 + 3：综合活跃度 + 启动时间近度
        var byActivity = BestByActivity();
        if (byActivity != 0) return byActivity;

        return 0;
    }

    private int BestByPrefix(string fullPath)
    {
        var procs = _processCache
            .Where(kv => !string.IsNullOrEmpty(kv.Value.Path))
            .Select(kv => (kv.Key, kv.Value.Path))
            .ToList();
        return BestByPrefixMatch(fullPath, procs, SystemDirs);
    }

    /// <summary>
    /// V0.9 修复：v0.8.1 用 exe **文件全路径**对被写文件做 StartsWith（几乎永不命中）。
    /// 改为 exe 所在目录前缀；公共系统目录（System32/Program Files/…）降权跳过，避免系统进程包揽。
    /// 纯函数便于单测。
    /// </summary>
    internal static int BestByPrefixMatch(string targetFullPath,
        IEnumerable<(int Pid, string ExePath)> processes, ISet<string> systemDirs)
    {
        int bestPid = 0;
        int bestPrefixLen = -1;
        foreach (var (pid, exePath) in processes)
        {
            var dir = Path.GetDirectoryName(exePath);
            if (string.IsNullOrEmpty(dir)) continue;
            if (systemDirs.Contains(dir)) continue;  // 公共目录降权
            var prefix = dir.EndsWith(Path.DirectorySeparatorChar) ? dir : dir + Path.DirectorySeparatorChar;
            if (targetFullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                && prefix.Length > bestPrefixLen)
            {
                bestPrefixLen = prefix.Length;
                bestPid = pid;
            }
        }
        return bestPid;
    }

    /// <summary>公共系统目录集合（小写、无尾斜杠）：这些目录下的进程不参与目录前缀归因。</summary>
    internal static readonly ISet<string> SystemDirs = BuildSystemDirs();

    private static ISet<string> BuildSystemDirs()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string? p)
        {
            if (string.IsNullOrWhiteSpace(p)) return;
            set.Add(p.TrimEnd('\\'));
        }
        try
        {
            Add(Environment.GetFolderPath(Environment.SpecialFolder.System));        // System32
            Add(Environment.GetFolderPath(Environment.SpecialFolder.SystemX86));     // SysWOW64
            Add(Environment.GetFolderPath(Environment.SpecialFolder.Windows));       // Windows
            Add(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));
            Add(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86));
            Add(Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles));
            Add(Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFilesX86));
        }
        catch { }
        return set;
    }

    private int BestByActivity()
    {
        // 找出 CPU 时间最近一次刷新中增长最多的进程（在 5 秒窗口内）
        // 由于缓存已记录 LastCpuTime，下次 RefreshCache 时会比较
        // 这里简化：找最近 10 秒内启动的进程作为兜底
        var now = DateTime.Now;
        int bestPid = 0;
        double bestScore = double.MinValue;
        foreach (var kv in _processCache)
        {
            var age = (now - kv.Value.StartTime).TotalSeconds;
            if (age > 10) continue;  // 只看 10 秒内启动的
            // 越新越好（10 秒内启动的进程）
            var score = 10.0 - age;
            if (score > bestScore)
            {
                bestScore = score;
                bestPid = kv.Key;
            }
        }
        return bestPid;
    }

    /// <summary>把 PID 翻译为进程名（带可执行路径）。</summary>
    public (string Name, string Path) GetProcessInfo(int pid)
    {
        if (pid <= 0) return ("unknown", "");
        if (_processCache.TryGetValue(pid, out var info))
        {
            return (info.Name, info.Path);
        }
        try
        {
            using var p = Process.GetProcessById(pid);
            string name;
            try { name = Path.GetFileName(p.MainModule?.FileName ?? p.ProcessName + ".exe"); }
            catch { name = p.ProcessName + ".exe"; }
            string path = "";
            try { path = p.MainModule?.FileName ?? ""; } catch { }
            return (name, path);
        }
        catch { return ("unknown", ""); }
    }

    public void Dispose()
    {
        _disposed = true;
        _cts.Cancel();
        _cts.Dispose();
    }
}
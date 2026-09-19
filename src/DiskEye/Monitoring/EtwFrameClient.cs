using DiskEye.Etw;

namespace DiskEye.Monitoring;

/// <summary>
/// V0.9 主进程侧 ETW 子进程客户端（替代 v0.8.1 的 EtwChildProvider）。
///
/// 存活机制全部沿用 v0.8.1 已验证逻辑：spawn 自身 exe `--etw-child &lt;ppid&gt;`、
/// 异步 ReadLineAsync、15 秒心跳超时判死、Kill(entireProcessTree)、60 秒冷却最多 3 次重启、
/// 就绪前 IsActive=false、startup.log 留痕。Start() 立即返回，零阻塞。
///
/// 变化：首行必须是协议版本 V1，随后 READY 才置活跃；每行用 FrameProtocol.TryParse
/// 解析后触发 OnFrame；ST 帧更新 LastHealth；畸形帧计数跳过（不杀读取循环）。
/// </summary>
public sealed class EtwFrameClient : IDisposable
{
    private const int WatchdogIntervalMs = 5000;
    private const int HeartbeatTimeoutMs = 15000;
    private const int RestartCooldownMs = 60000;
    private const int MaxRestarts = 3;

    private System.Diagnostics.Process? _child;
    private volatile bool _ready;
    private bool _disposed;
    private bool _versionOk;
    private long _lastSignOfLifeTicks;
    private long _deadAtTicks;
    private int _restartCount;
    private long _malformed;
    private readonly object _logLock = new();

    /// <summary>解析成功的 ETW 帧（OP/WR/CL/DL/RN/NM）。ST/READY/HB 不转发。</summary>
    public event Action<Frame>? OnFrame;
    /// <summary>精确（true）↔ 降级（false）切换。</summary>
    public event Action<bool>? OnActiveChanged;

    public FrameHealth? LastHealth { get; private set; }
    public long MalformedCount => Interlocked.Read(ref _malformed);
    public int RestartCount => _restartCount;

    /// <summary>子进程活着、协议版本正确且已 READY。</summary>
    public bool IsActive => _ready && _child is { HasExited: false };

    private void Log(string msg)
    {
        try
        {
            lock (_logLock)
            {
                File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "startup.log"),
                    $"[{DateTime.Now:HH:mm:ss.fff}] [EtwFrame] {msg}\n");
            }
        }
        catch { }
        System.Diagnostics.Debug.WriteLine($"[EtwFrame] {msg}");
    }

    public void Start()
    {
        if (_disposed) return;
        var exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
        {
            Log("拿不到自身 exe 路径，保持降级（启发式）");
            return;
        }
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = exePath,
                Arguments = $"--etw-child {Environment.ProcessId}",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            _child = System.Diagnostics.Process.Start(psi);
            if (_child == null) { Log("Process.Start 返回 null，保持降级"); return; }
            _lastSignOfLifeTicks = DateTime.UtcNow.Ticks;
            _versionOk = false;
            Log($"子进程已启动 pid={_child.Id}（--etw-child 模式）");
        }
        catch (Exception ex)
        {
            Log($"启动子进程失败: {ex.Message}，保持降级");
            _child = null;
            return;
        }
        _ = Task.Run(ReadLoop);
        _ = Task.Run(WatchdogLoop);
    }

    private async Task ReadLoop()
    {
        var child = _child;
        if (child == null) return;
        try
        {
            bool firstLine = true;
            while (!_disposed)
            {
                var line = await child.StandardOutput.ReadLineAsync();
                if (line == null) break;
                _lastSignOfLifeTicks = DateTime.UtcNow.Ticks;

                if (firstLine)
                {
                    firstLine = false;
                    if (line.Trim() != FrameProtocol.ProtocolVersion)
                    {
                        Log($"首行非协议版本（收到 {line.Truncate(40)}），判定不兼容，降级");
                        MarkDead("protocol version mismatch");
                        return;
                    }
                    _versionOk = true;
                    continue;
                }
                HandleLine(line);
            }
        }
        catch (Exception ex) { Log($"读取子进程输出异常: {ex.Message}"); }
        MarkDead("stdout 关闭或读取失败");
    }

    private void HandleLine(string line)
    {
        if (line == "READY")
        {
            if (_versionOk && !_ready)
            {
                _ready = true;
                Log("子进程 READY — ETW 精确归因已启用");
                OnActiveChanged?.Invoke(true);
            }
            return;
        }
        if (line == "HB") return;
        if (!FrameProtocol.TryParse(line, out var frame))
        {
            Interlocked.Increment(ref _malformed);
            return;
        }
        switch (frame.Kind)
        {
            case FrameKind.Error:
                Log($"子进程报错: {frame.Message}，降级");
                MarkDead(frame.Message);
                break;
            case FrameKind.Stats:
                if (frame.Health != null) LastHealth = frame.Health;
                break;
            case FrameKind.Ready:
            case FrameKind.Heartbeat:
                break;
            default:
                if (frame.Kind != FrameKind.Unknown) OnFrame?.Invoke(frame);
                break;
        }
    }

    private void MarkDead(string reason)
    {
        if (!_ready && _child == null) return;
        bool wasActive = _ready;
        _ready = false;
        _versionOk = false;
        if (_deadAtTicks == 0) _deadAtTicks = DateTime.UtcNow.Ticks;
        Log($"子进程不可用（{reason}），{(wasActive ? "降级为启发式" : "维持启发式")}");
        if (wasActive) OnActiveChanged?.Invoke(false);
        KillChild();
    }

    private void KillChild()
    {
        var child = _child;
        _child = null;
        if (child == null) return;
        try
        {
            if (!child.HasExited)
            {
                child.Kill(entireProcessTree: true);
                Log("已 Kill 子进程");
            }
        }
        catch (Exception ex) { Log($"Kill 子进程失败（不致命）: {ex.Message}"); }
        try { child.Dispose(); } catch { }
    }

    private async Task WatchdogLoop()
    {
        while (!_disposed)
        {
            try { await Task.Delay(WatchdogIntervalMs); } catch { break; }
            try
            {
                if (_child is { HasExited: true })
                {
                    MarkDead($"子进程退出 exitCode={SafeExitCode(_child)}");
                }
                else if (_ready && _lastSignOfLifeTicks != 0)
                {
                    var silentMs = (DateTime.UtcNow.Ticks - _lastSignOfLifeTicks) / 10_000;
                    if (silentMs > HeartbeatTimeoutMs) MarkDead($"心跳超时 {silentMs}ms");
                }
                else if (_child == null && _deadAtTicks != 0 && _restartCount < MaxRestarts)
                {
                    var deadMs = (DateTime.UtcNow.Ticks - _deadAtTicks) / 10_000;
                    if (deadMs > RestartCooldownMs)
                    {
                        _restartCount++;
                        Log($"冷却 {deadMs}ms 后尝试重启子进程（第 {_restartCount}/{MaxRestarts} 次）");
                        _deadAtTicks = 0;
                        Start();
                    }
                }
            }
            catch (Exception ex) { Log($"看门狗异常: {ex.Message}"); }
        }
    }

    private static int SafeExitCode(System.Diagnostics.Process p)
    {
        try { return p.ExitCode; } catch { return -1; }
    }

    public void Dispose()
    {
        _disposed = true;
        KillChild();
    }
}

internal static class StringExt
{
    public static string Truncate(this string s, int n) => s.Length <= n ? s : s[..n];
}

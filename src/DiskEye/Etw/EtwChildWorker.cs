using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;

namespace DiskEye.Etw;

/// <summary>
/// V0.9 子进程模式（单 exe 双角色，主程序 `DiskEye.exe --etw-child &lt;父PID&gt;` 再起一份）。
///
/// 与 v0.8 的区别：ETW 回调线程只做固定偏移解析 + HandleStateTable 字典更新 + 有界非阻塞入队，
/// 禁止任何 Process.* 调用与 stdout 直写（v0.7 内核态阻塞红线、v0.8 风暴饱和根因）。
/// 名字由 PidNameResolver 异步解析（NM 帧）；写字节 1 秒聚合（WR 帧）；健康度 5 秒上报（ST 帧）。
///
/// stdout V1 行协议（见 FrameProtocol）：
///   V1（首行） READY  HB  ERR&lt;msg&gt;  ST&lt;json&gt;
///   OP pid\tname\tisDir\tpath   WR pid\tbytes\tpath   CL pid\tpath
///   DL pid\tpath                RN pid\toldPath        NM pid\tname\texePath
/// 禁止任何 Console.ReadKey/ReadLine，不触碰 WinForms。
/// </summary>
internal static class EtwChildWorker
{
    private const string SessionName = "DiskEye-Etw-Win32";
    private static readonly object _logLock = new();

    private static HandleStateTable _table = null!;
    private static BoundedFrameWriter _writer = null!;
    private static PidNameResolver _names = null!;
    private static long _cCreate, _cWrite, _cCleanup, _cDelete, _cRename;

    private static void Log(string msg)
    {
        try
        {
            lock (_logLock)
                File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "etw_child.log"),
                    $"[{DateTime.Now:HH:mm:ss.fff}] {msg}\n");
        }
        catch { /* 日志失败不阻塞 */ }
    }

    /// <summary>子进程模式入口。返回进程退出码。</summary>
    public static int Run(int parentPid)
    {
        var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = false };
        _writer = new BoundedFrameWriter(stdout);
        _table = new HandleStateTable();
        _names = new PidNameResolver((pid, name, exe) => _writer.Enqueue(FrameProtocol.Name(pid, name, exe)));
        _names.Start();

        Log("=== ETW 子进程模式启动（V0.9 FileObject 状态机）===");
        Emit(FrameProtocol.ProtocolVersion);  // 首行协议版本

        // 父进程看门狗：父死则停 session 并退出，避免孤儿 ETW session
        if (parentPid > 0)
        {
            new Thread(() =>
            {
                while (true)
                {
                    try { Thread.Sleep(10000); } catch { break; }
                    try
                    {
                        using var p = System.Diagnostics.Process.GetProcessById(parentPid);
                        if (!p.HasExited) continue;
                    }
                    catch { /* GetProcessById 抛 = 父进程已死 */ }
                    Log("父进程已退出，子进程自我清理");
                    try { _session?.Dispose(); } catch { }
                    Environment.Exit(0);
                }
            }) { IsBackground = true }.Start();
        }

        var deviceMap = BuildDeviceMap();
        Log($"盘符映射 {deviceMap.Count} 条");

        TraceEventSession? session = null;
        try
        {
            session = new TraceEventSession(SessionName) { StopOnDispose = true };
            session.EnableProvider(KernelFileConstants.KernelFileProvider, TraceEventLevel.Always,
                KernelFileConstants.FileIoKeyword);
            Log($"session '{SessionName}' 已启动（keyword=0x{KernelFileConstants.FileIoKeyword:X}）");
        }
        catch (Exception ex)
        {
            Log($"session 启动失败: {ex.Message}");
            Emit($"ERR StartSession {ex.Message.Replace('\t', ' ').Replace('\n', ' ')}");
            return 2;
        }
        _session = session;

        session.Source.UnhandledEvents += OnEvent;
        StartTimers(deviceMap);

        Emit("READY");
        Log("READY 已发出，进入事件消费");

        try { session.Source.Process(); }
        catch (Exception ex) { Log($"Process 异常: {ex.Message}"); }

        Log("Process 返回，子进程退出");
        try { session.Dispose(); } catch { }
        return 0;
    }

    private static TraceEventSession? _session;
    private static List<(string Device, string Drive)> _map = null!;

    private static void OnEvent(TraceEvent e)
    {
        try
        {
            int id = (ushort)e.ID;
            int pid = e.ProcessID;
            if (pid <= 0) return;
            var data = e.EventData();

            switch (id)
            {
                case KernelFileConstants.EventIdCreate:
                    if (KernelFileParser.TryGetCreate(data, _map, out var ci))
                    {
                        _table.OnCreate(ci.FileObject, pid, ci.DosPath, ci.IsDirectory);
                        Interlocked.Increment(ref _cCreate);
                        _names.EnqueuePid(pid);
                        Emit(FrameProtocol.Open(pid, "", ci.IsDirectory, ci.DosPath));
                    }
                    break;

                case KernelFileConstants.EventIdWrite:
                    // 实测：Write 的 FileObject 在 offset16（Create 在 offset8）
                    if (KernelFileParser.TryGetWriteFileObject(data, out var fo)
                        && KernelFileParser.TryGetWriteSize(data, out var bytes))
                    {
                        if (_table.OnWrite(fo, bytes))
                        {
                            Interlocked.Increment(ref _cWrite);
                            _names.EnqueuePid(pid);
                        }
                    }
                    break;

                case KernelFileConstants.EventIdCleanup:
                    if (KernelFileParser.TryGetFileObject(data, out var foC))
                    {
                        var removed = _table.OnCleanup(foC);
                        if (removed is { IsDir: false } r)
                        {
                            // 短命句柄：先冲刷聚合窗内尚未取走的写字节，再发 CL
                            if (r.ResidualBytes > 0)
                                Emit(FrameProtocol.Write(r.Pid, r.ResidualBytes, r.DosPath));
                            Interlocked.Increment(ref _cCleanup);
                            Emit(FrameProtocol.Cleanup(r.Pid, r.DosPath));
                        }
                    }
                    break;

                case KernelFileConstants.EventIdClose:
                    if (KernelFileParser.TryGetFileObject(data, out var foCl))
                    {
                        var closed = _table.OnClose(foCl);
                        if (closed is { IsDir: false, ResidualBytes: > 0 } rc)
                            Emit(FrameProtocol.Write(rc.Pid, rc.ResidualBytes, rc.DosPath));
                    }
                    break;

                case KernelFileConstants.EventIdDelete:
                    if (KernelFileParser.IsDeleteDisposition(data)
                        && KernelFileParser.TryGetFileObject(data, out var foD)
                        && _table.Lookup(foD) is { IsDir: false } d)
                    {
                        Interlocked.Increment(ref _cDelete);
                        Emit(FrameProtocol.Delete(d.Pid, d.DosPath));
                    }
                    break;

                case KernelFileConstants.EventIdRename:
                    if (KernelFileParser.TryGetFileObject(data, out var foR)
                        && _table.Lookup(foR) is { IsDir: false } rn)
                    {
                        Interlocked.Increment(ref _cRename);
                        Emit(FrameProtocol.Rename(rn.Pid, rn.DosPath));
                    }
                    break;
            }
        }
        catch { /* 单事件解析失败不影响整体 */ }
    }

    private static void StartTimers(List<(string Device, string Drive)> deviceMap)
    {
        _map = deviceMap;

        // 1 秒：把聚合写字节作为 WR 帧发出
        new Thread(() =>
        {
            while (true)
            {
                try { Thread.Sleep(1000); } catch { break; }
                try
                {
                    foreach (var s in _table.DrainWrites())
                        Emit(FrameProtocol.Write(s.Pid, s.Bytes, s.DosPath));
                }
                catch { }
            }
        }) { IsBackground = true }.Start();

        // 5 秒：HB + ST 健康帧
        new Thread(() =>
        {
            while (true)
            {
                try { Thread.Sleep(5000); } catch { break; }
                try
                {
                    Emit("HB");
                    long lost = 0;
                    try { lost = _session?.EventsLost ?? 0; } catch { }
                    Emit(FrameProtocol.Stats(new FrameHealth(
                        lost, _writer.DroppedCount, _writer.QueueDepth,
                        Interlocked.Read(ref _cCreate), Interlocked.Read(ref _cWrite),
                        Interlocked.Read(ref _cCleanup), Interlocked.Read(ref _cDelete),
                        Interlocked.Read(ref _cRename))));
                }
                catch { }
            }
        }) { IsBackground = true }.Start();
    }

    private static void Emit(string line)
    {
        try { _writer?.Enqueue(line); } catch { /* 管道断了主进程会判死 */ }
    }

    /// <summary>枚举 A-Z 盘符，QueryDosDevice 拿 \Device\… 目标，长前缀排前（防短前缀误配）。</summary>
    private static List<(string Device, string Drive)> BuildDeviceMap()
    {
        var list = new List<(string, string)>();
        var sb = new StringBuilder(1024);
        for (char c = 'A'; c <= 'Z'; c++)
        {
            var drive = $"{c}:";
            try
            {
                if (QueryDosDevice(drive, sb, sb.Capacity) == 0) continue;
                var target = sb.ToString().Split('\0')[0];
                if (!string.IsNullOrEmpty(target)) list.Add((target, drive));
            }
            catch { }
        }
        list.Sort((a, b) => b.Item1.Length.CompareTo(a.Item1.Length));
        return list;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint QueryDosDevice(string lpDeviceName, StringBuilder lpTargetPath, int ucchMax);
}

using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Windows.Forms;
using DiskEye.Etw;
using DiskEye.Forms;

namespace DiskEye;

static class Program
{
    private const string MutexName = @"Global\DiskEye.SingleInstance.v1";
    private const string PipeName = "DiskEye.Pipe.v1";
    private static readonly object _logLock = new();

    /// <summary>CC 修复：写启动日志到 exe 同目录（用户直觉）。Fallback 到 %LOCALAPPDATA%。
    /// 让"启动挂起"类问题下次启动时可诊断（哪步卡了多久）。</summary>
    internal static void Log(string msg)
    {
        try
        {
            lock (_logLock)
            {
                // 优先写 exe 同目录（用户直觉"日志在程序旁边"）
                var exeDir = Path.GetDirectoryName(
                    System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName
                    ?? AppContext.BaseDirectory);
                var primaryLog = Path.Combine(exeDir ?? ".", "startup.log");
                File.AppendAllText(primaryLog, $"[{DateTime.Now:HH:mm:ss.fff}] {msg}\n");
            }
        }
        catch
        {
            // exe 目录可能只读（Program Files）→ 降级到 LocalAppData
            try
            {
                lock (_logLock)
                {
                    Directory.CreateDirectory(Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "DiskEye"));
                    File.AppendAllText(Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "DiskEye", "startup.log"),
                        $"[{DateTime.Now:HH:mm:ss.fff}] {msg}\n");
                }
            }
            catch { /* 日志失败不阻塞启动 */ }
        }
    }

    [STAThread]
    static int Main(string[] args)
    {
        // V0.8.1: 单 exe 双角色 —— --etw-child <父PID> 时本进程作为 ETW 子进程运行。
        // 必须在 Mutex / WinForms 初始化之前分流，子进程模式不碰任何 UI。
        int etwIdx = Array.IndexOf(args, "--etw-child");
        if (etwIdx >= 0)
        {
            int parentPid = 0;
            if (etwIdx + 1 < args.Length) int.TryParse(args[etwIdx + 1], out parentPid);
            return Etw.EtwChildWorker.Run(parentPid);
        }

        Log("Main 开始");
        bool isMin = args.Any(a => a.Equals("--minimized", StringComparison.OrdinalIgnoreCase));
        Log($"args={string.Join("|", args)}, isMin={isMin}");

        // V6.4: 开机自启延迟（避免开机时和系统 IO 抢资源）
        int delaySec = 0;
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals("--delay", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(args[i + 1], out var d) && d >= 0 && d <= 600)
            {
                delaySec = d;
                break;
            }
        }
        if (delaySec > 0)
        {
            Log($"自启延迟 {delaySec} 秒");
            Thread.Sleep(delaySec * 1000);
        }

        // 单实例 — v7.2 简化：initiallyOwned:true + try/catch AbandonedMutexException
        // 避免 v0.7.1 的 "initiallyOwned:false + WaitOne" 复杂逻辑可能卡死
        Log("创建单例 Mutex");
        Mutex? mutex = null;
        bool createdNew;
        try
        {
            mutex = new Mutex(initiallyOwned: true, MutexName, out createdNew);
        }
        catch (AbandonedMutexException)
        {
            Log("Mutex 孤儿（上一个 owner 死亡），重试获取");
            // 孤儿 mutex — 系统回收后会允许新 owner
            mutex = new Mutex(initiallyOwned: true, MutexName, out createdNew);
        }
        catch (Exception ex)
        {
            Log($"Mutex 创建异常: {ex.Message}，降级为多实例模式");
            mutex = null;
            createdNew = true;
        }

        if (mutex != null && !createdNew)
        {
            Log("已有实例，通知显示窗口");
            // 已有实例：通知它显示窗口
            try
            {
                using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
                client.Connect(2000);
                using var writer = new StreamWriter(client);
                writer.WriteLine("SHOW");
            }
            catch (Exception ex) { Log($"通知已运行实例失败: {ex.Message}"); }
            mutex.Dispose();
            return 0;
        }

        try
        {
            Log("ApplicationConfiguration.Initialize");
            ApplicationConfiguration.Initialize();
            Application.ThreadException += (s, e) =>
            {
                Log($"ThreadException: {e.Exception}");
            };

            DiskEye.Forms.L.Load(DiskEye.Config.AppConfigStore.Load().Language);   // V0.9.13: 语言在窗体构建前加载
            Log("创建 TrayApplicationContext");
            var ctx = new TrayApplicationContext(startMinimized: isMin);
            Log("Application.Run 开始");
            Application.Run(ctx);
            Log("Application.Run 结束");
        }
        finally
        {
            mutex?.Dispose();
        }
        return 0;
    }
}
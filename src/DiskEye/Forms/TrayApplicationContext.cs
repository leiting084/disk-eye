using System.IO.Pipes;
using System.Windows.Forms;
using DiskEye.Config;
using DiskEye.Monitoring;

namespace DiskEye.Forms;

/// <summary>
/// WinForms ApplicationContext — 整个应用的"隐形主循环"。
/// 没有 Form 作为 Application.Run 的入口，但持有托盘、监听 NamedPipe，
/// 收到 "SHOW" 时把 MainForm 显示出来。
/// 关闭 MainForm = 缩到托盘（不是 Exit）；点托盘"退出"才真正 Exit。
/// </summary>
public sealed class TrayApplicationContext : ApplicationContext
{
    private readonly TrayIconManager _tray;
    private readonly MonitorService _monitor;
    private readonly System.Windows.Forms.Timer _refreshTimer;
    private MainForm? _mainForm;
    private FirstRunForm? _firstRun;
    private CancellationTokenSource? _pipeCts;

    public TrayApplicationContext(bool startMinimized)
    {
        _monitor = new MonitorService();
        _monitor.Start();
        _monitor.Aggregator.OnSurgeDetected += OnSurgeAlert;

        _tray = new TrayIconManager();
        _tray.OnShowRequested += ShowMainForm;
        _tray.OnExitRequested += ExitApp;
        _tray.OnSettingsRequested += ShowSettingsOrFirstRun;
        _tray.OnToggleDrive += ToggleDrive;
        _tray.OnShowTopFolders += ShowTopFolders;
        _tray.OnAboutRequested += ShowAbout;
        _tray.Initialize(_monitor);

        // 首次运行引导
        if (_monitor.CurrentConfig.MonitoredDrives.Count == 0)
        {
            ShowSettingsOrFirstRun();
        }
        else if (!startMinimized)
        {
            ShowMainForm();
        }

        // 主窗未打开时也要每 5 秒刷新一次托盘数字
        _refreshTimer = new System.Windows.Forms.Timer { Interval = 5000 };
        _refreshTimer.Tick += (s, e) =>
        {
            _tray.RefreshStats(_monitor.Aggregator.TotalBytesToday, _monitor.Aggregator.IsPrecise);
            // V7.2: 主循环心跳（每 60 秒写一行 startup.log 证明活着 — 帮助诊断"已挂起"）
            if ((DateTime.UtcNow.Ticks / TimeSpan.FromSeconds(60).Ticks) % 12 == 0
                && DateTime.UtcNow.Second < 5)
            {
                try { Heartbeat(); } catch { }
            }
        };
        _refreshTimer.Start();

        // 监听 NamedPipe（第二个进程启动时发 SHOW 信号）
        _pipeCts = new CancellationTokenSource();
        Task.Run(() => ListenForShow(_pipeCts.Token));
    }

    private readonly object _showMainFormLock = new();
    private bool _isShowingMainForm = false;

    private void ShowMainForm()
    {
        // V0.9.15: 防止并发调用（NamedPipe 和 UI 线程可能同时触发）
        lock (_showMainFormLock)
        {
            if (_isShowingMainForm)
                return;
            _isShowingMainForm = true;
        }

        try
        {
            if (_mainForm == null || _mainForm.IsDisposed)
            {
                _mainForm = new MainForm(_monitor, hideToTrayOnClose: true);
                // V0.9.15: 语言切换后立即通知托盘刷新菜单文字
                _mainForm.LanguageChanged += () => _tray.ApplyLocalization();
            }
            _mainForm.RefreshData();
            // V0.9.3: 统一走"还原+屏幕外自愈"，修掉最小化/屏外窗口打不开的问题
            _mainForm.RestoreToScreen();
        }
        finally
        {
            lock (_showMainFormLock)
            {
                _isShowingMainForm = false;
            }
        }
    }

    private void ShowSettingsOrFirstRun()
    {
        if (_mainForm == null || _mainForm.IsDisposed)
        {
            _firstRun = new FirstRunForm(_monitor, onDone: ShowMainForm);
            _firstRun.Show();
            _firstRun.BringToFront();
        }
        else
        {
            // 设置项已点过 → 在已打开的主窗里弹出设置 tab
            _mainForm.OpenSettingsTab();
        }
    }

    private void ShowTopFolders()
    {
        // V2: 弹 TopFoldersPopupForm（轻量、TopMost、双击行打开资源管理器）
        var popup = new TopFoldersPopupForm(_monitor);
        popup.Show();
        popup.Activate();
    }

    private void ShowAbout()
    {
        var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        var verStr = version != null ? $"{version.Major}.{version.Minor}.{version.Build}" : "?";
        MessageBox.Show(string.Format(L.T("about.text"), verStr), "DiskEye",
            MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void ToggleDrive(string letter)
    {
        var cur = _monitor.CurrentConfig.MonitoredDrives;
        if (cur.Contains(letter))
        {
            _monitor.RemoveDrive(letter);
            _monitor.UpdateConfig(c => c.MonitoredDrives.Remove(letter));
        }
        else
        {
            _monitor.TryAddDrive(letter);
            _monitor.UpdateConfig(c => c.MonitoredDrives.Add(letter));
        }
        _tray.RefreshDriveMenu(_monitor.ActiveDrives);
        _mainForm?.RefreshData();
    }

    private void ExitApp()
    {
        try { _pipeCts?.Cancel(); } catch { }
        _refreshTimer.Stop();
        _refreshTimer.Dispose();
        _tray.Dispose();
        _monitor.Dispose();
        ExitThread();
    }

    /// <summary>V4.2: 异常增长弹窗 — Windows Toast 气泡通知。</summary>
    private void OnSurgeAlert(Monitoring.EventAggregator.SurgeAlert alert)
    {
        // 在 UI 线程弹（ShowBalloonTip 必须 UI 线程）
        BeginInvokeOnMain(() =>
        {
            try
            {
                var prefix = alert.Category == "Process" ? L.T("surge.alert.process") : L.T("surge.alert.folder");
                var key = alert.Category == "Process" ? alert.Key : Path.GetFileName(alert.Key.TrimEnd('\\', '/'));
                var title = $"DiskEye — {prefix}";
                var msg = string.Format(L.T("surge.alert.msg"), key, alert.Window.TotalSeconds, FormatBytes(alert.Bytes), alert.EventCount);
                _tray.ShowBalloonTip(title, msg);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SurgeAlert UI] 失败: {ex.Message}");
            }
        });
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / 1024.0 / 1024.0:F1} MB";
        return $"{bytes / 1024.0 / 1024.0 / 1024.0:F2} GB";
    }

    private async Task ListenForShow(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(
                    "DiskEye.Pipe.v1", PipeDirection.In, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await server.WaitForConnectionAsync(ct);
                using var reader = new StreamReader(server);
                var msg = await reader.ReadToEndAsync();
                if (msg.Trim().Equals("SHOW", StringComparison.OrdinalIgnoreCase))
                {
                    // 切回 UI 线程弹窗
                    BeginInvokeOnMain(() => ShowMainForm());
                }
            }
            catch (OperationCanceledException) { break; }
            catch { /* 单次失败不退出循环 */ }
        }
    }

    /// <summary>ApplicationContext 没有内置 BeginInvoke；通过隐藏的 Form 兜底。
/// CC 三审 P0 修复：每次创建临时 Form + Show 后 Shown 事件里执行 action + Close。
/// v6.2 #4 试图复用单例的方案在 IsHandleCreated=false 时 BeginInvoke 不会执行 action，
/// 导致异常增长通知永远不显示。回滚到此实现。</summary>
    /// <summary>V7.2: 主循环心跳（每 60 秒写一行 startup.log 证明活着）。</summary>
    private void Heartbeat()
    {
        try
        {
            var exeDir = System.IO.Path.GetDirectoryName(
                System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName
                ?? AppContext.BaseDirectory);
            var logPath = System.IO.Path.Combine(exeDir ?? ".", "startup.log");
            System.IO.File.AppendAllText(logPath,
                $"[{DateTime.Now:HH:mm:ss.fff}] 心跳 — 主循环活着 (今日 +{_monitor.Aggregator.TotalBytesToday / 1024 / 1024} MB)\n");
        }
        catch { }
    }

    private void BeginInvokeOnMain(Action action)
    {
        var invoker = new MessageOnlyForm();
        invoker.Shown += (s, e) =>
        {
            try { action(); } finally { invoker.Close(); }
        };
        invoker.Show();
    }
}

/// <summary>纯用于 marshal 到 UI 线程的 0 尺寸窗体。</summary>
internal sealed class MessageOnlyForm : Form
{
    public MessageOnlyForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        Opacity = 0;
        StartPosition = FormStartPosition.Manual;
        Location = new System.Drawing.Point(-10000, -10000);
    }
}
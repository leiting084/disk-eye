using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using DiskEye.Monitoring;

namespace DiskEye.Forms;

/// <summary>
/// 托盘管理：图标 + 右键菜单 + tooltip + 双击行为。
/// </summary>
public sealed class TrayIconManager : IDisposable
{
    private readonly NotifyIcon _tray = new();
    private readonly ContextMenuStrip _menu = new();
    private readonly ToolStripMenuItem _showItem = new(L.T("tray.show"));
    private readonly ToolStripMenuItem _topFoldersItem = new(L.T("tray.topfolders"));
    private readonly ToolStripMenuItem _drivesRootItem = new(L.T("tray.drives"));
    private readonly ToolStripMenuItem _settingsItem = new(L.T("tray.settings"));
    private readonly ToolStripMenuItem _exitItem = new(L.T("tray.exit"));
    private readonly ToolStripMenuItem _aboutItem = new(L.T("about.menu"));
    private readonly ToolStripMenuItem _todayBytesItem = new("--");
    private readonly ToolStripMenuItem _attrItem = new("…");

    public event Action? OnShowRequested;
    public event Action? OnSettingsRequested;
    public event Action? OnExitRequested;
    public event Action<string>? OnToggleDrive;
    public event Action? OnShowTopFolders;
    public event Action? OnAboutRequested;

    private MonitorService? _monitor;

    public void Initialize(MonitorService monitor)
    {
        _monitor = monitor;

        _tray.Icon = AppIcon.Eye;   // V0.9.8: 共用统一图标
        _tray.Text = L.T("tray.tooltip");
        _tray.Visible = true;
        // V0.9.6 修复：菜单构建了但从未绑定——右键一直没菜单（用户只能双击）
        _tray.ContextMenuStrip = _menu;

        _tray.MouseDoubleClick += (s, e) =>
        {
            if (e.Button == MouseButtons.Left) OnShowRequested?.Invoke();
        };

        // ─── 菜单组装 ───
        _menu.Items.Add(_todayBytesItem);
        _todayBytesItem.Enabled = false;
        _menu.Items.Add(_attrItem);
        _attrItem.Enabled = false;
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(_showItem);
        _menu.Items.Add(_topFoldersItem);
        _menu.Items.Add(_drivesRootItem);
        _menu.Items.Add(_settingsItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(_aboutItem);
        _menu.Items.Add(_exitItem);

        _showItem.Click += (s, e) => OnShowRequested?.Invoke();
        _topFoldersItem.Click += (s, e) => OnShowTopFolders?.Invoke();
        _settingsItem.Click += (s, e) => OnSettingsRequested?.Invoke();
        _exitItem.Click += (s, e) => OnExitRequested?.Invoke();
        _aboutItem.Click += (s, e) => OnAboutRequested?.Invoke();

        RefreshDriveMenu(monitor.ActiveDrives);
    }

    /// <summary>更新盘符子菜单（运行时勾选状态变化时调用）。</summary>
    public void RefreshDriveMenu(IReadOnlyCollection<string> active)
    {
        _drivesRootItem.DropDownItems.Clear();

        // 扫描所有 A-Z 盘符
        foreach (var letter in EnumerateAvailableDrives())
        {
            var item = new ToolStripMenuItem(string.Format(L.T("settings.drive.item"), letter));
            item.Checked = active.Contains(letter);
            item.CheckOnClick = true;
            // 不能用 CheckOnClick 的自动 toggle，要我们手动控制
            item.CheckStateChanged -= null;  //  noop
            item.Click += (s, e) =>
            {
                item.Checked = !item.Checked;  // 还原我们手动控制的状态
                OnToggleDrive?.Invoke(letter);
            };
            _drivesRootItem.DropDownItems.Add(item);
        }
    }

    /// <summary>V0.9.15: 语言切换后立即刷新菜单文字。</summary>
    public void ApplyLocalization()
    {
        _tray.Text = L.T("tray.tooltip");
        _showItem.Text = L.T("tray.show");
        _topFoldersItem.Text = L.T("tray.topfolders");
        _drivesRootItem.Text = L.T("tray.drives");
        _settingsItem.Text = L.T("tray.settings");
        _aboutItem.Text = L.T("about.menu");
        _exitItem.Text = L.T("tray.exit");
        // 刷新盘符子菜单（"{0}: 盘" 文字随语言变化）
        if (_monitor != null) RefreshDriveMenu(_monitor.ActiveDrives);
        // _todayBytesItem / _attrItem 由 RefreshStats 在下一次 tick 自动刷新
    }

    public void RefreshStats(long todayBytes, bool precise)
    {
        _todayBytesItem.Text = precise
            ? string.Format(L.T("tray.today"), FormatBytes(todayBytes))
            : string.Format(L.T("tray.today.paused"), FormatBytes(todayBytes));
        _attrItem.Text = precise ? L.T("tray.attrib.precise") : L.T("tray.attrib.degraded");
    }

    /// <summary>V4.2: Windows 气泡通知（突增告警）。</summary>
    public void ShowBalloonTip(string title, string text)
    {
        _tray.BalloonTipTitle = title;
        _tray.BalloonTipText = text;
        _tray.BalloonTipIcon = ToolTipIcon.Warning;
        _tray.ShowBalloonTip(8000);  //  8 秒
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / 1024.0 / 1024.0:F1} MB";
        return $"{bytes / 1024.0 / 1024.0 / 1024.0:F2} GB";
    }

    /// <summary>V0.9.15: 快速枚举盘符（跳过 IsReady 检查，避免网络盘/空光驱卡 60 秒）。</summary>
    private static IEnumerable<string> EnumerateAvailableDrives()
    {
        // DriveInfo.GetDrives() 本身很快，但 d.IsReady 对离线网络盘/空光驱会阻塞数秒。
        // 改用 Environment.GetLogicalDrives()（Win32 API，毫秒级返回所有已分配盘符）。
        foreach (var drive in Environment.GetLogicalDrives())
        {
            // drive 格式 "C:\\"，取第一个字符
            if (drive.Length >= 1)
                yield return drive[0].ToString().ToUpperInvariant();
        }
    }

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern bool DeleteObject(IntPtr h);

    public void Dispose()
    {
        _tray.Visible = false;
        _tray.Icon?.Dispose();
        _tray.Dispose();
        _menu.Dispose();
    }
}
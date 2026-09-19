using System.Drawing;
using System.Windows.Forms;
using DiskEye.Models;
using DiskEye.Monitoring;

namespace DiskEye.Forms;

/// <summary>
/// 「今日最狠文件夹」快捷小窗。
/// 弹在鼠标位置，TopMost，双击文件夹行 = 资源管理器打开 + 关闭。
/// </summary>
public sealed class TopFoldersPopupForm : Form
{
    private readonly MonitorService _monitor;
    private readonly ListBox _lst = new();

    public TopFoldersPopupForm(MonitorService monitor)
    {
        _monitor = monitor;

        Text = L.T("popup.title");
        ClientSize = new Size(640, 320);
        FormBorderStyle = FormBorderStyle.SizableToolWindow;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        ShowInTaskbar = false;

        // 定位到鼠标位置
        var pos = Cursor.Position;
        Location = new Point(Math.Max(0, pos.X - 320), Math.Max(0, pos.Y - 160));

        _lst.Dock = DockStyle.Fill;
        _lst.Font = new Font("Consolas", 9.5f);
        _lst.IntegralHeight = false;
        _lst.HorizontalScrollbar = true;

        var folders = _monitor.Store.QueryFolderSummary(DateTime.Today, top: 10);
        if (folders.Count == 0)
        {
            _lst.Items.Add(L.T("popup.empty"));
        }
        else
        {
            // 等宽对齐：排名 | 大小 | 事件数 | 文件夹
            _lst.Items.Add(L.T("popup.header"));
            _lst.Items.Add(new string('─', 80));
            for (int i = 0; i < folders.Count; i++)
            {
                var f = folders[i];
                var line = $"{(i + 1).ToString().PadLeft(2)}.    {FormatBytes(f.TotalBytes).PadLeft(10)}   {f.EventCount.ToString().PadLeft(6)}   {f.Folder}";
                _lst.Items.Add(line);
            }
        }

        _lst.DoubleClick += (s, e) =>
        {
            if (_lst.SelectedItem is string line && line.Length > 20)
            {
                // 抓最后那个路径
                var idx = line.LastIndexOf('\\');
                if (idx > 0)
                {
                    var folder = line.Substring(line.LastIndexOf("   ", StringComparison.Ordinal) + 3);
                    if (Directory.Exists(folder))
                    {
                        OpenInExplorer(folder);
                        Close();
                    }
                }
            }
        };

        _lst.KeyDown += (s, e) =>
        {
            if (e.KeyCode == Keys.Escape) Close();
        };

        Controls.Add(_lst);
        _lst.Focus();
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / 1024.0 / 1024.0:F1} MB";
        return $"{bytes / 1024.0 / 1024.0 / 1024.0:F2} GB";
    }

    private static void OpenInExplorer(string folder)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{folder}\"",
                UseShellExecute = true,
            });
        }
        catch { }
    }
}
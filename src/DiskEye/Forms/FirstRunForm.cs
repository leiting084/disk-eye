using System.Drawing;
using System.Windows.Forms;
using DiskEye.Config;
using DiskEye.Monitoring;

namespace DiskEye.Forms;

/// <summary>
/// 首次运行引导：选监控盘符 + 选自启动 → 开始监控。
/// </summary>
public sealed class FirstRunForm : Form
{
    private readonly MonitorService _monitor;
    private readonly Action _onDone;
    private readonly CheckedListBox _clb = new();
    private readonly CheckBox _chkAuto = new();
    private readonly Button _btnOk = new();

    public FirstRunForm(MonitorService monitor, Action onDone)
    {
        _monitor = monitor;
        _onDone = onDone;

        Text = L.T("firstrun.title");
        ClientSize = new Size(460, 320);
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;

        var lbl = new Label
        {
            Text = L.T("firstrun.hint"),
            Dock = DockStyle.Top,
            Height = 60,
            Padding = new Padding(12),
        };

        _clb.Dock = DockStyle.Top;
        _clb.Height = 130;
        _clb.CheckOnClick = true;
        // 默认勾 C 盘（多数人变满就是系统盘）
        foreach (var d in EnumerateDrives())
        {
            int idx = _clb.Items.Add(string.Format(L.T("settings.drive.item"), d));
            if (d == "C") _clb.SetItemChecked(idx, true);
        }

        _chkAuto.Text = L.T("firstrun.autostart");
        _chkAuto.Checked = true;
        _chkAuto.Dock = DockStyle.Top;
        _chkAuto.Height = 30;
        _chkAuto.Padding = new Padding(12, 0, 0, 0);

        _btnOk.Text = L.T("firstrun.start");
        _btnOk.Dock = DockStyle.Bottom;
        _btnOk.Height = 38;
        _btnOk.Click += OnOk;

        Controls.Add(_btnOk);
        Controls.Add(_chkAuto);
        Controls.Add(_clb);
        Controls.Add(lbl);
    }

    private void OnOk(object? sender, EventArgs e)
    {
        var drives = new HashSet<string>();
        foreach (var item in _clb.CheckedItems)
        {
            var s = item.ToString() ?? "";
            if (s.Length >= 1) drives.Add(s[0].ToString().ToUpperInvariant());
        }
        if (drives.Count == 0)
        {
            MessageBox.Show(this, L.T("firstrun.select.drive"), "DiskEye",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        // 应用配置
        foreach (var d in drives) _monitor.TryAddDrive(d);
        var exe = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? "";
        if (_chkAuto.Checked) StartupManager.Enable(exe, _monitor.CurrentConfig.StartupDelaySeconds);
        else StartupManager.Disable();

        _monitor.UpdateConfig(c =>
        {
            c.MonitoredDrives = drives;
            c.AutoStart = _chkAuto.Checked;
        });

        Close();
        _onDone();
    }

    private static IEnumerable<string> EnumerateDrives()
    {
        foreach (var d in DriveInfo.GetDrives())
        {
            string letter = "";
            try { if (d.IsReady) letter = d.Name[0].ToString().ToUpperInvariant(); }
            catch { }
            if (!string.IsNullOrEmpty(letter)) yield return letter;
        }
    }
}
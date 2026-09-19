using System.Drawing;
using System.Windows.Forms;
using DiskEye.Models;
using DiskEye.Monitoring;
using DiskEye.Storage;

namespace DiskEye.Forms;

/// <summary>
/// 主窗口。两个 tab：凶手指控榜（默认）+ 事件流。
/// 关闭按钮 = 缩到托盘（hideToTrayOnClose=true）。
/// </summary>
public sealed class MainForm : Form
{
    private readonly MonitorService _monitor;
    private readonly bool _hideToTrayOnClose;
    private readonly TabControl _tabs = new();
    private readonly TabPage _tabOffender = new(L.T("tab.offender"));
    private readonly TabPage _tabFolder = new(L.T("tab.folders"));
    private readonly TabPage _tabStream = new(L.T("tab.stream"));
    private readonly TabPage _tabChart = new(L.T("tab.chart"));
    private readonly TabPage _tabProcessTree = new(L.T("tab.tree"));
    private readonly TabPage _tabTools = new(L.T("tab.tools"));
    private readonly TabPage _tabSettings = new(L.T("tab.settings"));
    private readonly TabPage _tabClean = new(L.T("tab.clean"));

    // V5.4 进程树
    private readonly DataGridView _gridProcessTree = new();
    private readonly Label _lblProcessTreeHint = new();

    // V5.3 图表
    private readonly Panel _pnlChart = new();

    // 凶手指控榜
    private readonly DataGridView _gridOffender = new();
    private readonly Label _lblOffenderHint = new();

    // 文件夹重灾区
    private readonly DataGridView _gridFolder = new();
    private readonly Label _lblFolderHint = new();

    // 事件流
    private readonly DataGridView _gridStream = new();

    // 设置
    private readonly CheckedListBox _clbDrives = new();
    private readonly CheckBox _chkAutoStart = new();
    private readonly Button _btnSaveSettings = new();
    private ComboBox _cboLanguage = new();   // V0.9.14: 界面语言
    private TextBox _txtInclude = new();
    private TextBox _txtIgnore = new();
    // V5.1: 阈值配置
    private TextBox _txtProcessThreshold = new();
    private TextBox _txtFolderThreshold = new();
    private TextBox _txtSurgeWindow = new();
    private TextBox _txtSurgeCooldown = new();

    // V6.3: 顶栏搜索
    private readonly Panel _pnlSearch = new();
    private readonly TextBox _txtSearch = new();
    private readonly Label _lblSearchHint = new();
    private readonly Button _btnClearSearch = new();   // V0.9.15: 提升为字段（语言切换要重设文字）
    private string _searchFilter = "";
    private System.Windows.Forms.Timer _searchTimer = new() { Interval = 300 };

    // V0.9.15: 进程图标缓存
    private readonly ImageList _iconList = new() { ImageSize = new Size(16, 16), ColorDepth = ColorDepth.Depth32Bit };
    private readonly Dictionary<string, int> _iconIndexCache = new(StringComparer.OrdinalIgnoreCase);
    private Image? _defaultIcon;

    private readonly System.Windows.Forms.Timer _refreshTimer;

    // V0.9.7: 底部状态条（精确/降级 + 今日写入），从标题栏移出
    private readonly Panel _statusBar = new();
    private readonly Label _lblStatus = new();

    public MainForm(MonitorService monitor, bool hideToTrayOnClose)
    {
        _monitor = monitor;
        _hideToTrayOnClose = hideToTrayOnClose;

        Text = L.T("app.title");
        Font = new Font("Segoe UI", 8.25f);       // V0.9.13: 8.25pt 更精致（原生渲染锐利）
        AutoScaleMode = AutoScaleMode.Dpi;        // V0.9.13: PMv2 下按 DPI 精确缩放（错误基准的 Font 模式移除）
        BackColor = ThemeColors.B0Page;
        ClientSize = new Size(1000, 620);
        StartPosition = FormStartPosition.CenterScreen;
        ShowInTaskbar = true;   // V0.9.7: 任务栏显图标（用户反馈）
        MinimizeBox = true;
        MaximizeBox = true;
        FormBorderStyle = FormBorderStyle.Sizable;
        Icon = AppIcon.Eye;   // V0.9.8: 真眼睛图标（原空白位图 → 任务栏"没图标"观感）

        // 恢复上次窗口位置
        var c = monitor.CurrentConfig;
        if (c.WindowWidth is int w && c.WindowHeight is int h && w > 400 && h > 300)
        {
            ClientSize = new Size(w, h);
        }
        if (c.WindowX is int x && c.WindowY is int y)
        {
            StartPosition = FormStartPosition.Manual;
            var probe = new Point(x, y);
            var probeBounds = new Rectangle(probe, ClientSize);
            // V0.9.3: config 里的历史坐标可能在屏幕外（v0.8 bug 遗留），不合法就居中
            if (Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(probeBounds)))
                Location = probe;
        }

        BuildUI();

        // V0.9.15: 预热 ApplyLocalization（强制 JIT 编译，首次语言切换不卡）
        ApplyLocalization();

        _refreshTimer = new System.Windows.Forms.Timer { Interval = 3000 };
        _refreshTimer.Tick += (s, e) => RefreshData();
        _refreshTimer.Start();

        // V7.2: Form 关闭时释放所有 Timer 资源
        FormClosed += (s, e) =>
        {
            try { _refreshTimer.Stop(); _refreshTimer.Dispose(); } catch { }
            try { _searchTimer.Stop(); _searchTimer.Dispose(); } catch { }
        };

        FormClosing += OnClosing;
        Resize += (s, e) => SaveWindowPos();
    }

    private void UpdateStatusBar(bool precise)
    {
        if (precise)
        {
            _statusBar.BackColor = ThemeColors.SuccessBg;
            _lblStatus.ForeColor = ThemeColors.SuccessText;
            _lblStatus.Text = string.Format(L.T("status.precise"), FormatBytes(_monitor.Aggregator.TotalBytesToday));
        }
        else
        {
            _statusBar.BackColor = ThemeColors.WarnBg;
            _lblStatus.ForeColor = ThemeColors.WarnText;
            _lblStatus.Text = string.Format(L.T("status.degraded"), FormatBytes(_monitor.Aggregator.TotalBytesToday));
        }
    }

    /// <summary>V0.9.7: Tab owner-draw——激活 tab 白底 + 顶部 3px 主色条 + 主色粗体字。</summary>
    private void Tabs_DrawItem(object? sender, DrawItemEventArgs e)
    {
        var tab = _tabs.TabPages[e.Index];
        bool active = _tabs.SelectedIndex == e.Index;
        var rect = _tabs.GetTabRect(e.Index);

        using var bg = new SolidBrush(active ? ThemeColors.B1Surface : ThemeColors.B0Page);
        e.Graphics.FillRectangle(bg, rect);
        if (active)
        {
            using var bar = new SolidBrush(ThemeColors.Primary);
            e.Graphics.FillRectangle(bar, rect.X, rect.Y, rect.Width, 3);
        }
        var textColor = active ? ThemeColors.Primary : ThemeColors.T2;
        TextRenderer.DrawText(e.Graphics, tab.Text, new Font("Segoe UI", 8.25f, active ? FontStyle.Bold : FontStyle.Regular),
            rect, textColor, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    private void BuildUI()
    {
        // V6.3: 顶栏搜索框（先 Dock=Fill，再 Dock=Top 的搜索条覆盖在上面）
        _tabs.Dock = DockStyle.Fill;
        _tabs.TabPages.Add(_tabOffender);
        _tabs.TabPages.Add(_tabFolder);
        _tabs.TabPages.Add(_tabStream);
        _tabs.TabPages.Add(_tabChart);
        _tabs.TabPages.Add(_tabProcessTree);
        _tabs.TabPages.Add(_tabTools);
        _tabs.TabPages.Add(_tabSettings);
        _tabs.TabPages.Add(_tabClean);
        Controls.Add(_tabs);

        BuildSearchBar();
        BuildOffenderTab();
        BuildFolderTab();
        BuildStreamTab();
        BuildChartTab();
        BuildProcessTreeTab();
        BuildCleanTab();
        BuildToolsTab();
        BuildSettingsTab();

        // V0.9.7: Tab 激活白底+顶部 3px 主色条（owner-draw），状态条挂底
        _tabs.DrawMode = TabDrawMode.OwnerDrawFixed;
        _tabs.ItemSize = new Size(0, 28);
        _tabs.Padding = new Point(14, 8);
        _tabs.SizeMode = TabSizeMode.Normal;
        _tabs.DrawItem += Tabs_DrawItem;

        _statusBar.Dock = DockStyle.Bottom;
        _statusBar.BackColor = ThemeColors.SuccessBg;
        _statusBar.Padding = new Padding(10, 0, 10, 0);
        _lblStatus.Font = new Font("Segoe UI", 8.25f);
        _lblStatus.AutoSize = true;
        _lblStatus.Location = new Point(10, 5);
        _statusBar.Controls.Add(_lblStatus);
        _lblStatus.SizeChanged += (s, e) => { _statusBar.Height = _lblStatus.Height + 10; };
        _statusBar.Height = _lblStatus.Height + 10;   // V0.9.11: 高度自适应（DPI 不裁字）
        Controls.Add(_statusBar);

        // V0.9.6 美化：所有表格统一外观 + 双缓冲
        StyleGrid(_gridOffender);
        StyleGrid(_gridFolder);
        StyleGrid(_gridStream);
        StyleGrid(_gridProcessTree);
        StyleGrid(_gridClean);

        // V0.9.5 关键修复：tab 标签条被搜索栏盖住（V6.3 起）。
        // WinForms Dock 布局按 z-order 从底向顶处理：_tabs 必须在 z 顶（最后处理），
        // 才能拿到"扣掉顶部搜索栏 36px"的剩余空间；否则 Fill 占满整个客户区、
        // 标签条被搜索栏覆盖 → 用户永远看不到 7 个 tab。
        _tabs.BringToFront();
    }

    private void BuildSearchBar()
    {
        _pnlSearch.Dock = DockStyle.Top;
        _pnlSearch.Height = 46;   // V0.9.15: 46px 确保按钮边框完整显示（含 DPI 缩放余量）
        _pnlSearch.Padding = new Padding(8, 0, 8, 0);
        _pnlSearch.BackColor = ThemeColors.B1Surface;

        _lblSearchHint.Text = L.T("search.hint");
        _lblSearchHint.Tag = "search.hint";
        _lblSearchHint.AutoSize = true;
        _lblSearchHint.Location = new Point(10, 9);
        _lblSearchHint.ForeColor = ThemeColors.T2;
        _pnlSearch.Controls.Add(_lblSearchHint);

        _txtSearch.PlaceholderText = L.T("search.placeholder");
        _txtSearch.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top;
        _txtSearch.Left = 210;
        _txtSearch.Top = 6;
        _txtSearch.Height = 24;
        _searchTimer.Tick += (s, e) => { _searchTimer.Stop(); _searchFilter = _txtSearch.Text.Trim(); RefreshData(); };
        _txtSearch.TextChanged += (s, e) => { _searchTimer.Stop(); _searchTimer.Start(); };
        _pnlSearch.Controls.Add(_txtSearch);

        // V0.9.15: 使用字段（语言切换要重设文字），垂直居中（修下边框裁切）
        _btnClearSearch.Text = L.T("search.clear");
        _btnClearSearch.Tag = "search.clear";
        _btnClearSearch.AutoSize = true;
        _btnClearSearch.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        _btnClearSearch.Anchor = AnchorStyles.Right | AnchorStyles.Top;
        StyleSecondary(_btnClearSearch);
        _btnClearSearch.Padding = new Padding(10, 3, 10, 3);
        _btnClearSearch.Click += (s, e) => { _txtSearch.Clear(); _searchFilter = ""; RefreshData(); };
        _pnlSearch.Controls.Add(_btnClearSearch);
        void RelayoutSearch()
        {
            // V0.9.15: 垂直居中（修 DPI 下按钮下边框被面板底裁切）
            _lblSearchHint.Top = (_pnlSearch.ClientSize.Height - _lblSearchHint.Height) / 2;
            _txtSearch.Top = (_pnlSearch.ClientSize.Height - _txtSearch.Height) / 2;
            _btnClearSearch.Top = (_pnlSearch.ClientSize.Height - _btnClearSearch.Height) / 2;
            // V0.9.10: TextBox 左缘动态跟 hint 右缘（修 hint 与输入框重叠）
            _txtSearch.Left = _lblSearchHint.Right + 8;
            _txtSearch.Width = Math.Max(200, _pnlSearch.ClientSize.Width - _txtSearch.Left - _btnClearSearch.Width - 24);
            _btnClearSearch.Left = _pnlSearch.ClientSize.Width - _btnClearSearch.Width - 10;
        }
        _pnlSearch.Resize += (s, e) => RelayoutSearch();
        RelayoutSearch();

        // 搜索栏加入（Z 序由 BuildUI 末尾的 _tabs.BringToFront() 统一裁定，V0.9.5）
        Controls.Add(_pnlSearch);
    }

    // V7.3: 多关键词"或"匹配。空格分隔，每个关键词任一命中即匹配。
    private bool MatchFilter(string? text)
    {
        if (string.IsNullOrEmpty(_searchFilter)) return true;
        if (string.IsNullOrEmpty(text)) return false;
        var keywords = _searchFilter.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var kw in keywords)
        {
            if (text.IndexOf(kw, StringComparison.OrdinalIgnoreCase) >= 0)
                return true;  // 任一关键词命中 → 匹配
        }
        return false;
    }

    private void BuildOffenderTab()
    {
        _lblOffenderHint.Text = L.T("hint.offender");
        _lblOffenderHint.Dock = DockStyle.Top;
        _lblOffenderHint.Height = 28;
        _lblOffenderHint.TextAlign = ContentAlignment.MiddleLeft;
        _lblOffenderHint.Padding = new Padding(8, 0, 0, 0);
        _lblOffenderHint.Font = new Font(SystemFonts.MenuFont!, FontStyle.Bold);

        _gridOffender.Dock = DockStyle.Fill;
        _gridOffender.AllowUserToAddRows = false;
        _gridOffender.AllowUserToDeleteRows = false;
        _gridOffender.ReadOnly = true;
        _gridOffender.RowHeadersVisible = false;
        _gridOffender.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _gridOffender.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _gridOffender.BackgroundColor = SystemColors.Window;
        _gridOffender.AlternatingRowsDefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = Color.FromArgb(245, 248, 252)
        };

        _gridOffender.Columns.AddRange(
            new DataGridViewImageColumn { Name = "icon", Width = 24, MinimumWidth = 24, AutoSizeMode = DataGridViewAutoSizeColumnMode.None, HeaderText = "" },
            new DataGridViewTextBoxColumn { HeaderText = L.T("col.process"), Name = "process", FillWeight = 16 },
            new DataGridViewTextBoxColumn { HeaderText = L.T("col.exepath"), Name = "path", FillWeight = 22 },
            new DataGridViewTextBoxColumn { HeaderText = L.T("col.topfolder"), Name = "topfolder", FillWeight = 24 },
            new DataGridViewTextBoxColumn { HeaderText = L.T("col.events"), Name = "count", FillWeight = 7 },
            new DataGridViewTextBoxColumn { HeaderText = L.T("col.writebytes"), Name = "bytes", FillWeight = 10 },
            new DataGridViewTextBoxColumn { HeaderText = L.T("col.lastactivity"), Name = "last", FillWeight = 11 }
        );

        _gridOffender.CellDoubleClick += ShowOffenderDetail;

        _tabOffender.Controls.Add(_gridOffender);
        _tabOffender.Controls.Add(_lblOffenderHint);
    }

    private void BuildFolderTab()
    {
        _lblFolderHint.Text = L.T("hint.folders");
        _lblFolderHint.Dock = DockStyle.Top;
        _lblFolderHint.Height = 28;
        _lblFolderHint.TextAlign = ContentAlignment.MiddleLeft;
        _lblFolderHint.Padding = new Padding(8, 0, 0, 0);
        _lblFolderHint.Font = new Font(SystemFonts.MenuFont!, FontStyle.Bold);

        _gridFolder.Dock = DockStyle.Fill;
        _gridFolder.AllowUserToAddRows = false;
        _gridFolder.AllowUserToDeleteRows = false;
        _gridFolder.ReadOnly = true;
        _gridFolder.RowHeadersVisible = false;
        _gridFolder.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _gridFolder.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _gridFolder.BackgroundColor = SystemColors.Window;
        _gridFolder.AlternatingRowsDefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = Color.FromArgb(245, 248, 252)
        };

        _gridFolder.Columns.AddRange(
            new DataGridViewTextBoxColumn { HeaderText = L.T("col.folder"), Name = "folder", FillWeight = 60 },
            new DataGridViewTextBoxColumn { HeaderText = L.T("col.events"), Name = "count", FillWeight = 10 },
            new DataGridViewTextBoxColumn { HeaderText = L.T("col.writebytes"), Name = "bytes", FillWeight = 12 },
            new DataGridViewTextBoxColumn { HeaderText = L.T("col.lastactivity"), Name = "last", FillWeight = 18 }
        );

        _gridFolder.CellDoubleClick += OpenFolderInExplorer;
        // 右键菜单：打开 / 复制路径
        var ctxMenu = new ContextMenuStrip();
        ctxMenu.Items.Add(L.T("ctxmenu.open"), null, (s, e) =>
        {
            if (_gridFolder.CurrentRow != null)
            {
                var folder = _gridFolder.CurrentRow.Cells["folder"].Value?.ToString() ?? "";
                if (!string.IsNullOrEmpty(folder)) OpenInExplorer(folder);
            }
        });
        ctxMenu.Items.Add(L.T("ctxmenu.copy"), null, (s, e) =>
        {
            if (_gridFolder.CurrentRow != null)
            {
                var folder = _gridFolder.CurrentRow.Cells["folder"].Value?.ToString() ?? "";
                if (!string.IsNullOrEmpty(folder)) Clipboard.SetText(folder);
            }
        });
        _gridFolder.ContextMenuStrip = ctxMenu;

        _tabFolder.Controls.Add(_gridFolder);
        _tabFolder.Controls.Add(_lblFolderHint);
    }

    private void OpenFolderInExplorer(object? sender, DataGridViewCellEventArgs e)
    {
        if (e.RowIndex < 0) return;
        var folder = _gridFolder.Rows[e.RowIndex].Cells["folder"].Value?.ToString() ?? "";
        if (!string.IsNullOrEmpty(folder)) OpenInExplorer(folder);
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
        catch (Exception ex)
        {
            MessageBox.Show(string.Format(L.T("msg.open.fail"), ex.Message), "DiskEye",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void BuildProcessTreeTab()
    {
        _lblProcessTreeHint.Text = L.T("hint.tree");
        _lblProcessTreeHint.Dock = DockStyle.Top;
        _lblProcessTreeHint.Height = 28;
        _lblProcessTreeHint.TextAlign = ContentAlignment.MiddleLeft;
        _lblProcessTreeHint.Padding = new Padding(8, 0, 0, 0);
        _lblProcessTreeHint.Font = new Font(SystemFonts.MenuFont!, FontStyle.Bold);

        _gridProcessTree.Dock = DockStyle.Fill;
        _gridProcessTree.AllowUserToAddRows = false;
        _gridProcessTree.AllowUserToDeleteRows = false;
        _gridProcessTree.ReadOnly = true;
        _gridProcessTree.RowHeadersVisible = false;
        _gridProcessTree.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _gridProcessTree.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _gridProcessTree.BackgroundColor = SystemColors.Window;
        _gridProcessTree.AlternatingRowsDefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = Color.FromArgb(245, 248, 252)
        };

        _gridProcessTree.Columns.AddRange(
            new DataGridViewTextBoxColumn { HeaderText = L.T("col.pid"), Name = "pid", FillWeight = 5 },
            new DataGridViewTextBoxColumn { HeaderText = L.T("col.process"), Name = "name", FillWeight = 15 },
            new DataGridViewTextBoxColumn { HeaderText = L.T("col.ppid"), Name = "ppid", FillWeight = 5 },
            new DataGridViewTextBoxColumn { HeaderText = L.T("col.pname"), Name = "pname", FillWeight = 15 },
            new DataGridViewTextBoxColumn { HeaderText = L.T("col.events"), Name = "count", FillWeight = 8 },
            new DataGridViewTextBoxColumn { HeaderText = L.T("col.writebytes"), Name = "bytes", FillWeight = 10 },
            new DataGridViewTextBoxColumn { HeaderText = L.T("col.lastactivity"), Name = "last", FillWeight = 12 },
            new DataGridViewTextBoxColumn { HeaderText = L.T("col.exepath"), Name = "path", FillWeight = 30 }
        );

        _gridProcessTree.CellDoubleClick += ShowProcessDetail;
        _tabProcessTree.Controls.Add(_gridProcessTree);
        _tabProcessTree.Controls.Add(_lblProcessTreeHint);
    }

    private void ShowProcessDetail(object? sender, DataGridViewCellEventArgs e)
    {
        if (e.RowIndex < 0) return;
        var row = _gridProcessTree.Rows[e.RowIndex];
        var pidStr = row.Cells["pid"].Value?.ToString() ?? "";
        var name = row.Cells["name"].Value?.ToString() ?? "";
        if (string.IsNullOrEmpty(pidStr) || !int.TryParse(pidStr, out var pid)) return;

        var events = _monitor.Store.QueryEventsByPid(pid, DateTime.Today);
        var sb = new System.Text.StringBuilder();
        sb.AppendLine(string.Format(L.T("detail.header.pid"), name, pid, events.Count));
        sb.AppendLine(new string('─', 80));
        foreach (var ev in events.Take(200))
        {
            sb.AppendLine($"{ev.Timestamp:HH:mm:ss}  {ev.EventType,-8}  {FormatBytes(ev.SizeBytes),10}  {ev.FullPath}");
        }
        if (events.Count > 200) sb.AppendLine(string.Format(L.T("detail.truncated"), events.Count));

        MessageBox.Show(this, sb.ToString(), string.Format(L.T("detail.title"), name),
            MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void BuildStreamTab()
    {
        _gridStream.Dock = DockStyle.Fill;
        _gridStream.AllowUserToAddRows = false;
        _gridStream.AllowUserToDeleteRows = false;
        _gridStream.ReadOnly = true;
        _gridStream.RowHeadersVisible = false;
        _gridStream.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _gridStream.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _gridStream.BackgroundColor = SystemColors.Window;

        _gridStream.Columns.AddRange(
            new DataGridViewTextBoxColumn { HeaderText = L.T("col.time"), Name = "ts", FillWeight = 18 },
            new DataGridViewTextBoxColumn { HeaderText = L.T("col.drive"), Name = "drive", FillWeight = 5 },
            new DataGridViewTextBoxColumn { HeaderText = L.T("col.type"), Name = "etype", FillWeight = 8 },
            new DataGridViewTextBoxColumn { HeaderText = L.T("col.file"), Name = "path", FillWeight = 39 },
            new DataGridViewTextBoxColumn { HeaderText = L.T("col.filesize"), Name = "size", FillWeight = 8 },
            new DataGridViewTextBoxColumn { HeaderText = L.T("col.process"), Name = "proc", FillWeight = 12 },
            new DataGridViewTextBoxColumn { HeaderText = L.T("col.pid"), Name = "pid", FillWeight = 5 },
            new DataGridViewTextBoxColumn { HeaderText = L.T("col.exepath"), Name = "ppath", FillWeight = 5 }  //  占位
        );
        _gridStream.Columns["ppath"].Visible = false;  // 太长，单独看详情

        _tabStream.Controls.Add(_gridStream);
    }

    private void BuildToolsTab()
    {
        // 数据导出
        var grpExport = new GroupBox
        {
            Text = L.T("tools.export"),
            Tag = "tools.export",
            Dock = DockStyle.Top,
            Height = 110,
            Padding = new Padding(12),
        };
        var btnExport = new Button
        {
            Text = L.T("tools.export.all"),
            Tag = "tools.export.all",
            Width = 200,
            Height = 32,
            Location = new Point(12, 28),
        };
        btnExport.Click += ExportCsv;
        grpExport.Controls.Add(btnExport);

        var btnExportToday = new Button
        {
            Text = L.T("tools.export.today"),
            Tag = "tools.export.today",
            Width = 150,
            Height = 32,
            Location = new Point(220, 28),
        };
        btnExportToday.Click += ExportTodayCsv;
        grpExport.Controls.Add(btnExportToday);

        var lblExportHint = new Label
        {
            Text = L.T("tools.export.hint"),
            Tag = "tools.export.hint",
            Location = new Point(12, 68),
            AutoSize = true,
            ForeColor = Color.DimGray,
        };
        grpExport.Controls.Add(lblExportHint);

        // 数据管理
        var grpData = new GroupBox
        {
            Text = L.T("tools.data"),
            Tag = "tools.data",
            Dock = DockStyle.Top,
            Height = 110,
            Padding = new Padding(12),
        };
        var btnArchive = new Button
        {
            Text = L.T("tools.archive"),
            Tag = "tools.archive",
            Width = 350,
            Height = 32,
            Location = new Point(12, 28),
        };
        btnArchive.Click += (s, e) =>
        {
            try
            {
                var n = _monitor.Store.Archive(_monitor.CurrentConfig.ArchiveAfterDays);
                MessageBox.Show(this, string.Format(L.T("msg.archived"), n), "DiskEye",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, string.Format(L.T("msg.archive.fail"), ex.Message), "DiskEye",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        };
        grpData.Controls.Add(btnArchive);

        // 数据统计
        var grpStats = new GroupBox
        {
            Text = L.T("tools.stats"),
            Tag = "tools.stats",
            Dock = DockStyle.Top,
            Height = 100,
            Padding = new Padding(12),
        };
        var lblStats = new Label
        {
            Text = ComputeStats(),
            Name = "lblStats",
            Location = new Point(12, 28),
            AutoSize = true,
            Font = new Font(SystemFonts.MenuFont!, FontStyle.Bold),
        };
        grpStats.Controls.Add(lblStats);
        _tabTools.Controls.Add(grpStats);
        _tabTools.Controls.Add(grpData);
        _tabTools.Controls.Add(grpExport);
    }

    private string ComputeStats()
    {
        try
        {
            var s = _monitor.Store.GetStats();
            var days = s.FirstTs.HasValue && s.LastTs.HasValue
                ? (s.LastTs.Value - s.FirstTs.Value).TotalDays.ToString("F1")
                : "0";
            return string.Format(L.T("tools.stats.format"),
                FormatBytes(s.DbSizeBytes), s.EventCount.ToString("N0"),
                days, s.FirstTs?.ToString("yyyy-MM-dd HH:mm") ?? "—",
                s.LastTs?.ToString("yyyy-MM-dd HH:mm") ?? "—");
        }
        catch (Exception ex)
        {
            return string.Format(L.T("tools.stats.fail"), ex.Message);
        }
    }

    private void ExportCsv(object? sender, EventArgs e)
    {
        ExportCsvInternal(null, "all");
    }

    private void ExportTodayCsv(object? sender, EventArgs e)
    {
        ExportCsvInternal(DateTime.Today, "today");
    }

    private void ExportCsvInternal(DateTime? since, string tag)
    {
        using var dlg = new SaveFileDialog
        {
            Filter = L.T("dlg.csv.filter"),
            FileName = $"DiskEye-{tag}-{DateTime.Now:yyyyMMdd-HHmmss}.csv",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            var n = _monitor.Store.ExportToCsv(dlg.FileName, since);
            MessageBox.Show(this, string.Format(L.T("msg.exported"), n, dlg.FileName), "DiskEye",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            // 用 explorer 选中刚导出的文件
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{dlg.FileName}\"",
                    UseShellExecute = true,
                });
            }
            catch { }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, string.Format(L.T("msg.export.fail"), ex.Message), "DiskEye",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    // ───────────── V0.9.6 垃圾清理 tab（识别 + 可清理）─────────────
    private readonly DataGridView _gridClean = new();
    private readonly Label _lblCleanHint = new();
    private readonly Label _lblCleanStatus = new();
    private readonly Button _btnCleanScan = new();
    private readonly Button _btnCleanRun = new();
    private int _cleanBusy;

    /// <summary>安全白名单：只扫描/清理这些目录。绝不碰 claude-code、node_modules、用户文档等。</summary>
    private static (string Name, string Dir, bool NeedAge)[] CleanTargets()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var list = new List<(string, string, bool)>
        {
            (L.T("clean.target.temp"), Path.GetTempPath(), true),
            (string.Format(L.T("clean.target.diskmon"), Environment.UserName), Path.Combine(local, "Temp"), false),
        };
        string chrome = Path.Combine(local, @"Google\Chrome\User Data");
        string edge = Path.Combine(local, @"Microsoft\Edge\User Data");
        foreach (var (root, browser) in new[] { (chrome, "Chrome"), (edge, "Edge") })
        {
            if (!Directory.Exists(root)) continue;
            try
            {
                foreach (var profile in Directory.GetDirectories(root, "*", SearchOption.TopDirectoryOnly))
                {
                    var name = Path.GetFileName(profile);
                    foreach (var cache in new[] { "Cache", "Code Cache", "GPUCache" })
                    {
                        var p = Path.Combine(profile, cache);
                        if (Directory.Exists(p)) list.Add((string.Format(L.T("clean.target.browser"), browser, name, cache), p, false));
                    }
                }
            }
            catch { }
        }
        var thumbs = Path.Combine(local, @"Microsoft\Windows\Explorer");
        if (Directory.Exists(thumbs)) list.Add((L.T("clean.target.thumb"), thumbs, false));
        return list.ToArray();
    }

    /// <summary>V0.9.15: 确保所有行高度一致（解决 RowTemplate.Height 可能不生效的问题）。</summary>
    private static void EnsureRowHeights(DataGridView grid, int height)
    {
        if (grid == null || grid.IsDisposed) return;
        try
        {
            foreach (DataGridViewRow row in grid.Rows)
            {
                if (row != null && row.Height != height)
                    row.Height = height;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[EnsureRowHeights] 设置行高失败: {ex.Message}");
        }
    }

    /// <summary>V0.9.7 美化（设计规范落地）：统一色板/字体层次 + 双缓冲。</summary>
    private void StyleGrid(DataGridView g)
    {
        g.Dock = DockStyle.Fill;
        g.AllowUserToAddRows = false;
        g.AllowUserToDeleteRows = false;
        g.ReadOnly = true;
        g.RowHeadersVisible = false;
        g.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        g.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        g.BackgroundColor = ThemeColors.B1Surface;
        g.BorderStyle = BorderStyle.None;
        g.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
        g.GridColor = ThemeColors.Divider;
        g.AlternatingRowsDefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = ThemeColors.B2Zebra,
        };
        g.DefaultCellStyle = new DataGridViewCellStyle
        {
            SelectionBackColor = ThemeColors.RowSelected,
            SelectionForeColor = ThemeColors.T1,
            ForeColor = ThemeColors.T1,
            Padding = new Padding(4, 4, 4, 4),  // V0.9.15: 上下内边距 2→4（防止下延字母被截断）
        };
        g.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = ThemeColors.B0Page,
            ForeColor = Color.FromArgb(0x3D, 0x4A, 0x5C),
            Font = new Font("Segoe UI", 9f, FontStyle.Bold),
            SelectionBackColor = ThemeColors.B0Page,
            Padding = new Padding(4, 4, 4, 4),  // V0.9.15: 列头也加内边距
        };
        g.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
        g.ColumnHeadersHeight = 32;  // V0.9.15: 28→32（列头字体完整显示 + 内边距）
        g.RowTemplate.Height = 36;   // V0.9.15: 32→36（彻底解决 p/y/g 等下延字母被截断问题）
        g.EnableHeadersVisualStyles = false;
        // 数字列等宽右对齐（字节数/事件数小数点对位）
        foreach (var colName in new[] { "count", "bytes", "files", "size", "pid" })
        {
            if (g.Columns[colName] is DataGridViewTextBoxColumn num)
            {
                num.DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
                num.DefaultCellStyle.Font = ThemeColors.NumericFont;
                num.DefaultCellStyle.Padding = new Padding(0, 0, 8, 0);
            }
        }
        // 双缓冲（DataGridView 保护属性），重绘不闪
        try
        {
            typeof(DataGridView).GetProperty("DoubleBuffered",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                ?.SetValue(g, true);
        }
        catch { }
    }

    /// <summary>V0.9.7 按钮规范：主按钮（实心主色）/次按钮（白底描边）。</summary>
    private void StylePrimary(Button b)
    {
        b.FlatStyle = FlatStyle.Flat;
        b.FlatAppearance.BorderSize = 0;
        b.BackColor = ThemeColors.Primary;
        b.ForeColor = Color.White;
        b.AutoSize = true;                      // V0.9.11: 自量尺寸，DPI 下不裁字
        b.Padding = new Padding(10, 0, 10, 0);
        b.MinimumSize = new Size(0, 28);
        b.Cursor = Cursors.Hand;
        b.FlatAppearance.MouseOverBackColor = ThemeColors.PrimaryHover;
        b.FlatAppearance.MouseDownBackColor = ThemeColors.PrimaryPressed;
    }

    private void StyleSecondary(Button b)
    {
        b.FlatStyle = FlatStyle.Flat;
        b.FlatAppearance.BorderSize = 1;
        b.FlatAppearance.BorderColor = Color.FromArgb(0xC9, 0xD4, 0xE4);
        b.BackColor = Color.White;
        b.ForeColor = Color.FromArgb(0x3D, 0x4A, 0x5C);
        b.AutoSize = true;                      // V0.9.11
        b.Padding = new Padding(10, 0, 10, 0);
        b.MinimumSize = new Size(0, 28);
        b.Cursor = Cursors.Hand;
        b.FlatAppearance.MouseOverBackColor = Color.FromArgb(0xF0, 0xF5, 0xFB);
    }

    private void BuildCleanTab()
    {
        _lblCleanHint.Text = L.T("clean.hint");
        _lblCleanHint.Dock = DockStyle.Top;
        _lblCleanHint.AutoSize = true;  // V0.9.15: 自动高度（文本换行不被裁切）
        _lblCleanHint.MinimumSize = new Size(0, 40);
        _lblCleanHint.TextAlign = ContentAlignment.MiddleLeft;
        _lblCleanHint.Padding = new Padding(8, 4, 0, 4);

        // V0.9.15: 使用 FlowLayoutPanel 自动排列按钮和状态标签，避免重叠
        var pnlBtn = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 50,
            Padding = new Padding(8, 8, 8, 8),
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false
        };
        _btnCleanScan.Text = L.T("clean.scan");
        _btnCleanScan.AutoSize = true;
        _btnCleanScan.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        StyleSecondary(_btnCleanScan);
        _btnCleanScan.Click += (s, e) => ScanCleanTargets();
        _btnCleanRun.Text = L.T("clean.run");
        _btnCleanRun.AutoSize = true;
        _btnCleanRun.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        StylePrimary(_btnCleanRun);
        _btnCleanRun.Enabled = false;
        _btnCleanRun.Click += (s, e) => RunClean();
        _lblCleanStatus.Text = L.T("clean.scan");
        _lblCleanStatus.AutoSize = true;
        _lblCleanStatus.Margin = new Padding(20, 5, 0, 0);  // 左边距 20px，上边距 5px 垂直居中
        pnlBtn.Controls.AddRange(new Control[] { _btnCleanScan, _btnCleanRun, _lblCleanStatus });

        StyleGrid(_gridClean);
        _gridClean.RowTemplate.Height = 44;  // V0.9.15: 44px（扫描结果行高足够显示完整路径）
        _gridClean.ColumnHeadersHeight = 34;  // 列头也加高
        _gridClean.Columns.AddRange(
            new DataGridViewCheckBoxColumn { HeaderText = L.T("clean.col.pick"), Name = "pick", FillWeight = 6 },
            new DataGridViewTextBoxColumn { HeaderText = L.T("clean.col.item"), Name = "name", FillWeight = 26 },
            new DataGridViewTextBoxColumn { HeaderText = L.T("clean.col.path"), Name = "dir", FillWeight = 38 },
            new DataGridViewTextBoxColumn { HeaderText = L.T("clean.col.files"), Name = "files", FillWeight = 8 },
            new DataGridViewTextBoxColumn { HeaderText = L.T("clean.col.freeable"), Name = "bytes", FillWeight = 10 }
        );

        // V0.9.15: 修正 Dock z-order（添加顺序决定 z-order，先加 = 底层）
        _tabClean.Controls.Add(_gridClean);      // 底层：Dock.Fill
        _tabClean.Controls.Add(_lblCleanHint);   // 中层：Dock.Top（按钮下方）
        _tabClean.Controls.Add(pnlBtn);          // 顶层：Dock.Top（最上方）
        // 不需要 BringToFront，添加顺序已经正确
    }

    private sealed record CleanItem(string Name, string Dir, long Files, long Bytes, bool NeedAge);

    private void ScanCleanTargets()
    {
        if (Interlocked.CompareExchange(ref _cleanBusy, 1, 0) != 0) return;
        _btnCleanScan.Enabled = false;
        _lblCleanStatus.Text = L.T("clean.scanning");
        Task.Run(() =>
        {
            var items = new List<CleanItem>();
            var cutoff = DateTime.Now.AddDays(-1);
            foreach (var (name, dir, needAge) in CleanTargets())
            {
                long files = 0, bytes = 0;
                try
                {
                    foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                    {
                        try
                        {
                            var fi = new FileInfo(f);
                            if (needAge && fi.LastWriteTime > cutoff) continue;  // Temp 只算旧文件
                            files++;
                            bytes += fi.Length;
                        }
                        catch { }
                    }
                }
                catch { }
                if (files > 0) items.Add(new CleanItem(name, dir, files, bytes, needAge));
            }
            BeginInvoke(new Action(() =>
            {
                if (IsDisposed) { Interlocked.Exchange(ref _cleanBusy, 0); return; }
                _gridClean.Rows.Clear();
                foreach (var it in items)
                    _gridClean.Rows.Add(true, it.Name, it.Dir, it.Files.ToString("N0"), FormatBytes(it.Bytes));
                EnsureRowHeights(_gridClean, 44);  // V0.9.15: 确保所有行高度一致（清理页用 44px）
                _btnCleanScan.Enabled = true;
                _btnCleanRun.Enabled = items.Count > 0;
                _lblCleanStatus.Text = items.Count > 0
                    ? string.Format(L.T("clean.found"), items.Count)
                    : L.T("clean.none");
                Interlocked.Exchange(ref _cleanBusy, 0);
            }));
        });
    }

    private void RunClean()
    {
        if (Interlocked.CompareExchange(ref _cleanBusy, 1, 0) != 0) return;
        var targets = new List<(string Dir, bool NeedAge)>();
        foreach (DataGridViewRow row in _gridClean.Rows)
        {
            if (Convert.ToBoolean(row.Cells["pick"].Value ?? false))
                targets.Add((row.Cells["dir"].Value?.ToString() ?? "", false));
        }
        // NeedAge（Temp）从 CleanTargets 找回
        for (int i = 0; i < targets.Count; i++)
        {
            var hit = CleanTargets().FirstOrDefault(t => t.Dir == targets[i].Dir);
            targets[i] = (targets[i].Dir, hit.NeedAge);
        }
        if (targets.Count == 0) { Interlocked.Exchange(ref _cleanBusy, 0); return; }
        _btnCleanRun.Enabled = false;
        _lblCleanStatus.Text = L.T("clean.running");
        Task.Run(() =>
        {
            long freed = 0, failed = 0;
            var cutoff = DateTime.Now.AddDays(-1);
            foreach (var (dir, needAge) in targets)
            {
                try
                {
                    foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                    {
                        try
                        {
                            var fi = new FileInfo(f);
                            if (needAge && fi.LastWriteTime > cutoff) continue;
                            var len = fi.Length;
                            fi.Delete();
                            freed += len;
                        }
                        catch { failed++; }  // 被占用/权限——跳过
                    }
                }
                catch { }
            }
            BeginInvoke(new Action(() =>
            {
                if (IsDisposed) { Interlocked.Exchange(ref _cleanBusy, 0); return; }
                _lblCleanStatus.Text = string.Format(L.T("clean.done"), FormatBytes(freed), failed);
                Interlocked.Exchange(ref _cleanBusy, 0);
                ScanCleanTargets();  // 清完重扫，展示剩余
            }));
        });
    }

    private void BuildSettingsTab()
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            ColumnCount = 2,
            RowCount = 6,          // V0.9.14: +语言行
            Height = 420,
            Padding = new Padding(16),
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170));   // V0.9.15: 140→170（中文长标签不裁切）
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        layout.Controls.Add(new Label
        {
            Text = L.T("settings.drives"),
            Tag = "settings.drives",
            TextAlign = ContentAlignment.MiddleLeft,
            Dock = DockStyle.Fill,
            Font = new Font(SystemFonts.MenuFont!, FontStyle.Bold)
        }, 0, 0);

        _clbDrives.Dock = DockStyle.Fill;
        _clbDrives.CheckOnClick = true;
        foreach (var d in EnumerateDrives())
        {
            int idx = _clbDrives.Items.Add(string.Format(L.T("settings.drive.item"), d));
            if (_monitor.CurrentConfig.MonitoredDrives.Contains(d))
                _clbDrives.SetItemChecked(idx, true);
        }
        layout.Controls.Add(_clbDrives, 1, 0);

        layout.Controls.Add(new Label
        {
            Text = L.T("settings.language"),
            Tag = "settings.language",
            TextAlign = ContentAlignment.MiddleLeft,
            Dock = DockStyle.Fill
        }, 0, 1);
        // V0.9.15: 界面语言切换（中文/English），保存后立即生效
        _cboLanguage = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
        _cboLanguage.Items.AddRange(new object[] { "中文", "English" });
        _cboLanguage.SelectedIndex = _monitor.CurrentConfig.Language == "en" ? 1 : 0;
        layout.Controls.Add(_cboLanguage, 1, 1);

        layout.Controls.Add(new Label
        {
            Text = L.T("settings.autostart.label"),
            Tag = "settings.autostart.label",
            TextAlign = ContentAlignment.MiddleLeft,
            Dock = DockStyle.Fill
        }, 0, 2);
        _chkAutoStart.Text = L.T("settings.autostart");
        _chkAutoStart.Tag = "settings.autostart";
        _chkAutoStart.Checked = _monitor.CurrentConfig.AutoStart;
        _chkAutoStart.Dock = DockStyle.Fill;
        layout.Controls.Add(_chkAutoStart, 1, 2);

        // V3: 路径白名单/黑名单（V0.9.14: 因插入语言行，整体下移一行）
        layout.Controls.Add(new Label
        {
            Text = L.T("settings.include"),
            Tag = "settings.include",
            TextAlign = ContentAlignment.MiddleLeft,
            Dock = DockStyle.Fill
        }, 0, 3);
        _txtInclude = new TextBox { Dock = DockStyle.Fill, Multiline = true, Height = 50, ScrollBars = ScrollBars.Vertical };
        _txtInclude.Text = string.Join(Environment.NewLine, _monitor.CurrentConfig.IncludePrefixes);
        layout.Controls.Add(_txtInclude, 1, 3);

        layout.Controls.Add(new Label
        {
            Text = L.T("settings.ignore"),
            Tag = "settings.ignore",
            TextAlign = ContentAlignment.MiddleLeft,
            Dock = DockStyle.Fill
        }, 0, 4);
        _txtIgnore = new TextBox { Dock = DockStyle.Fill, Multiline = true, Height = 50, ScrollBars = ScrollBars.Vertical };
        _txtIgnore.Text = string.Join(Environment.NewLine, _monitor.CurrentConfig.IgnorePrefixes);
        layout.Controls.Add(_txtIgnore, 1, 4);

        layout.Controls.Add(new Label(), 0, 5);
        _btnSaveSettings.Text = L.T("settings.save");
        _btnSaveSettings.Tag = "settings.save";
        _btnSaveSettings.Dock = DockStyle.Left;
        _btnSaveSettings.Width = 120;
        _btnSaveSettings.Click += SaveSettings;
        layout.Controls.Add(_btnSaveSettings, 1, 5);

        // V5.1: 异常增长阈值（独立 GroupBox 放阈值，逻辑分组更清晰）
        var grpThreshold = new GroupBox
        {
            Text = L.T("settings.surge"),
            Tag = "settings.surge",
            Dock = DockStyle.Top,
            Height = 160,
            Padding = new Padding(12),
        };
        var gridTh = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 4,
        };
        gridTh.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 200));
        gridTh.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        gridTh.Controls.Add(new Label { Text = L.T("settings.surge.process"), Tag = "settings.surge.process", TextAlign = ContentAlignment.MiddleLeft, Dock = DockStyle.Fill }, 0, 0);
        _txtProcessThreshold = new TextBox { Dock = DockStyle.Fill, Text = (_monitor.CurrentConfig.ProcessSurgeThresholdBytes / 1024 / 1024).ToString() };
        gridTh.Controls.Add(_txtProcessThreshold, 1, 0);

        gridTh.Controls.Add(new Label { Text = L.T("settings.surge.folder"), Tag = "settings.surge.folder", TextAlign = ContentAlignment.MiddleLeft, Dock = DockStyle.Fill }, 0, 1);
        _txtFolderThreshold = new TextBox { Dock = DockStyle.Fill, Text = (_monitor.CurrentConfig.FolderSurgeThresholdBytes / 1024 / 1024).ToString() };
        gridTh.Controls.Add(_txtFolderThreshold, 1, 1);

        gridTh.Controls.Add(new Label { Text = L.T("settings.surge.window"), Tag = "settings.surge.window", TextAlign = ContentAlignment.MiddleLeft, Dock = DockStyle.Fill }, 0, 2);
        _txtSurgeWindow = new TextBox { Dock = DockStyle.Fill, Text = _monitor.CurrentConfig.SurgeWindowSeconds.ToString() };
        gridTh.Controls.Add(_txtSurgeWindow, 1, 2);

        gridTh.Controls.Add(new Label { Text = L.T("settings.surge.cooldown"), Tag = "settings.surge.cooldown", TextAlign = ContentAlignment.MiddleLeft, Dock = DockStyle.Fill }, 0, 3);
        _txtSurgeCooldown = new TextBox { Dock = DockStyle.Fill, Text = _monitor.CurrentConfig.SurgeCooldownMinutes.ToString() };
        gridTh.Controls.Add(_txtSurgeCooldown, 1, 3);

        grpThreshold.Controls.Add(gridTh);

        var hint = new Label
        {
            Text = L.T("settings.hint"),
            Tag = "settings.hint",
            Dock = DockStyle.Top,
            Height = 50,
            Padding = new Padding(16, 0, 16, 0),
            ForeColor = Color.DimGray
        };

        // V0.9.15: 版本号标签（设置页底部）
        var lblVersion = new Label
        {
            Text = $"DiskEye v{Application.ProductVersion}",
            Dock = DockStyle.Bottom,
            Height = 24,
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = Color.FromArgb(0x99, 0x99, 0x99),
            Font = new Font("Segoe UI", 7.5f),
        };

        _tabSettings.Controls.Add(lblVersion);
        _tabSettings.Controls.Add(hint);
        _tabSettings.Controls.Add(grpThreshold);
        _tabSettings.Controls.Add(layout);
    }

    public void OpenSettingsTab() => _tabs.SelectedTab = _tabSettings;

    private int _refreshSeq;
    private int _refreshRunning;

    public void RefreshData()
    {
        if (IsDisposed) return;
        // 所有 SQLite/WMI 查询移到后台线程，UI 线程只做控件填充（V0.9.1 修复无响应）
        if (Interlocked.CompareExchange(ref _refreshRunning, 1, 0) != 0) return;
        int seq = ++_refreshSeq;
        var precise = _monitor.Aggregator.IsPrecise;
        var today = DateTime.Today;
        var filter = _searchFilter;

        Task.Run(() =>
        {
            try
            {
                var offenders = _monitor.Store.QueryProcessUnified(today);
                var folders = _monitor.Store.QueryFolderUnified(today, 200);
                var stream = _monitor.Store.QueryRecentEvents(500);
                var hourly = _monitor.Store.QueryHourlyWriteBytes(today);
                var tree = _monitor.Store.QueryProcessTree(today);

                if (IsDisposed) return;
                try
                {
                    BeginInvoke(new Action(() =>
                    {
                        if (IsDisposed || seq != _refreshSeq) return;
                        try
                        {
                            // V0.9.7: 状态移到底部状态条（标题栏不再承担状态）
                            UpdateStatusBar(precise);
                            FillOffenders(offenders, filter);
                            FillFolders(folders, filter);
                            FillStream(stream, filter);
                            FillProcessTree(tree, filter);
                            _hourlyBytes = hourly;
                            _pnlChart.Invalidate();
                        }
                        catch (Exception ex)
                        {
                            Text = L.T("error.refresh");
                            System.Diagnostics.Debug.WriteLine($"[MainForm] 填表失败: {ex}");
                        }
                    }));
                }
                catch (ObjectDisposedException) { /* 窗体在查询期间关闭 */ }
                catch (InvalidOperationException) { /* 句柄未创建/已销毁 */ }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MainForm] 后台查询失败: {ex}");
                try
                {
                    if (!IsDisposed)
                        BeginInvoke(new Action(() => { if (!IsDisposed) Text = L.T("error.query"); }));
                }
                catch { }
            }
            finally
            {
                Interlocked.Exchange(ref _refreshRunning, 0);
            }
        });
    }

    /// <summary>V0.9.9: 哈希命名的编译产物（如 todolist_lib-ad6a40e03a21678e.exe）中段截断显示。</summary>
    private static string DisplayName(string name)
    {
        if (name != "unknown" && name.Length > 34)
            return name[..20] + "…" + name[^12..];
        return name;
    }

    private void FillOffenders(List<EventStore.UnifiedRow> rows, string filter)
    {
        _gridOffender.SuspendLayout();
        // V0.9.12: 保留滚动位置（刷新把用户翻到的位置干回顶部 = 糟糕体验）
        int savedScroll = _gridOffender.FirstDisplayedScrollingRowIndex;
        _gridOffender.Rows.Clear();
        foreach (var s in rows)
        {
            if (!string.IsNullOrEmpty(filter) && !MatchFilter(s.Name) && !MatchFilter(s.Path)
                && !MatchFilter(s.TopFolder)) continue;
            _gridOffender.Rows.Add(
                GetProcessIcon(s.Path),
                DisplayName(s.Name),
                string.IsNullOrEmpty(s.Path) ? L.T("row.exited") : s.Path,
                string.IsNullOrEmpty(s.TopFolder) ? "—" : s.TopFolder,
                s.EventCount.ToString("N0"),
                FormatBytes(s.WriteBytes),
                s.LastActivity.ToString("HH:mm:ss"));
        }
        EnsureRowHeights(_gridOffender, 36);  // V0.9.15: 确保所有行高度一致
        _gridOffender.ResumeLayout(true);
        if (savedScroll > 0 && savedScroll < _gridOffender.Rows.Count)
            try { _gridOffender.FirstDisplayedScrollingRowIndex = savedScroll; } catch { }
    }

    /// <summary>V0.9.15: 从 exe 路径提取 16x16 图标（缓存），缺失返回默认图标。</summary>
    private Image GetProcessIcon(string exePath)
    {
        if (string.IsNullOrEmpty(exePath)) return GetDefaultIcon();
        if (_iconIndexCache.TryGetValue(exePath, out var idx))
        {
            if (idx < 0) return GetDefaultIcon();  // -1 表示提取失败，使用默认图标
            return _iconList.Images[idx];
        }
        try
        {
            if (File.Exists(exePath))
            {
                using var icon = Icon.ExtractAssociatedIcon(exePath);
                if (icon != null)
                {
                    _iconList.Images.Add(icon);
                    _iconIndexCache[exePath] = _iconList.Images.Count - 1;
                    return _iconList.Images[_iconList.Images.Count - 1];
                }
            }
        }
        catch { /* 权限/路径无效 → 用默认图标 */ }
        _iconIndexCache[exePath] = -1;  // 标记已尝试但失败，下次直接用默认
        return GetDefaultIcon();
    }

    private Image GetDefaultIcon()
    {
        if (_defaultIcon == null)
        {
            // 使用系统默认文件图标
            try { _defaultIcon = SystemIcons.Application.ToBitmap(); }
            catch { _defaultIcon = new Bitmap(16, 16); }
        }
        return _defaultIcon;
    }

    private void FillFolders(List<EventStore.UnifiedRow> rows, string filter)
    {
        _gridFolder.SuspendLayout();
        // V0.9.12: 保留滚动位置（刷新把用户翻到的位置干回顶部 = 糟糕体验）
        int savedScroll = _gridFolder.FirstDisplayedScrollingRowIndex;
        _gridFolder.Rows.Clear();
        foreach (var s in rows)
        {
            if (!string.IsNullOrEmpty(filter) && !MatchFilter(s.Name)) continue;
            _gridFolder.Rows.Add(
                s.Name,
                s.EventCount.ToString("N0"),
                FormatBytes(s.WriteBytes),
                s.LastActivity.ToString("HH:mm:ss"));
        }
        EnsureRowHeights(_gridFolder, 36);  // V0.9.15: 确保所有行高度一致
        _gridFolder.ResumeLayout(true);
        if (savedScroll > 0 && savedScroll < _gridFolder.Rows.Count)
            try { _gridFolder.FirstDisplayedScrollingRowIndex = savedScroll; } catch { }
    }

    private void FillProcessTree(List<ProcessTreeNode> nodes, string filter)
    {
        _gridProcessTree.SuspendLayout();
        // V0.9.12: 保留滚动位置（刷新把用户翻到的位置干回顶部 = 糟糕体验）
        int savedScroll = _gridProcessTree.FirstDisplayedScrollingRowIndex;
        _gridProcessTree.Rows.Clear();
        foreach (var n in nodes)
        {
            if (!string.IsNullOrEmpty(filter)
                && !MatchFilter(n.ProcessName) && !MatchFilter(n.ProcessPath)
                && !MatchFilter(n.ParentName) && !MatchFilter(n.Pid.ToString())) continue;
            _gridProcessTree.Rows.Add(
                n.Pid.ToString(),
                n.ProcessName,
                n.ParentPid?.ToString() ?? "—",
                n.ParentName,
                n.EventCount.ToString("N0"),
                FormatBytes(n.TotalBytes),
                n.LastActivity.ToString("HH:mm:ss"),
                n.ProcessPath
            );
        }
        EnsureRowHeights(_gridProcessTree, 36);  // V0.9.15: 确保所有行高度一致
        _gridProcessTree.ResumeLayout(true);
        if (savedScroll > 0 && savedScroll < _gridProcessTree.Rows.Count)
            try { _gridProcessTree.FirstDisplayedScrollingRowIndex = savedScroll; } catch { }
    }

    // V5.3 图表数据缓存
    private long[] _hourlyBytes = new long[24];
    private int _hoveredHour = -1;
    private readonly ToolTip _chartTip = new();

    // V0.9.6: 图表绘制资源缓存（每帧 new Pen/Brush 既慢又泄漏 GDI 句柄——闪烁元凶之一）
    private static readonly Pen PenAxis = new(Color.LightGray);
    private static readonly Pen PenGrid = new(Color.FromArgb(240, 240, 240));
    private static readonly Font FontHour = new("Consolas", 7.5f);
    private static readonly Font FontTitle = new("Segoe UI", 9f, FontStyle.Bold);
    private static readonly Brush BrushBar = new SolidBrush(ThemeColors.Primary);
    private static readonly Brush BrushBarHover = new SolidBrush(Color.FromArgb(0xE8, 0x83, 0x3A));
    private static readonly Brush BrushBarPeak = new SolidBrush(ThemeColors.Danger);

    private void BuildChartTab()
    {
        _pnlChart.Dock = DockStyle.Fill;
        _pnlChart.BackColor = Color.White;
        // V0.9.6: 双缓冲（鼠标移动重绘不再闪烁）
        try
        {
            typeof(Panel).GetProperty("DoubleBuffered",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                ?.SetValue(_pnlChart, true);
        }
        catch { }
        _pnlChart.Paint += PaintChart;
        _pnlChart.MouseMove += ChartMouseMove;
        _pnlChart.MouseLeave += (s, e) => { _hoveredHour = -1; _pnlChart.Invalidate(); _chartTip.Hide(_pnlChart); };
        _pnlChart.Resize += (s, e) => _pnlChart.Invalidate();

        var hint = new Label
        {
            Text = L.T("hint.chart"),
            Tag = "hint.chart",
            Dock = DockStyle.Top,
            Height = 28,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(8, 0, 0, 0),
            Font = new Font(SystemFonts.MenuFont!, FontStyle.Bold),
        };

        _tabChart.Controls.Add(_pnlChart);
        _tabChart.Controls.Add(hint);
    }

    private void RefreshChart()
    {
        // V0.9.1: 数据由 RefreshData 后台任务统一获取（_hourlyBytes），这里只重绘
        _pnlChart.Invalidate();
    }

    private void ChartMouseMove(object? sender, MouseEventArgs e)
    {
        // 计算鼠标在哪个柱子上（24 个柱子）
        int hour = HourFromX(e.X);
        if (hour != _hoveredHour)
        {
            _hoveredHour = hour;
            _pnlChart.Invalidate();
            if (hour >= 0 && hour < 24)
            {
                var bytes = _hourlyBytes[hour];
                if (bytes > 0)
                {
                    _chartTip.SetToolTip(_pnlChart, $"{hour:00}:00 — {FormatBytes(bytes)}");
                }
            }
        }
    }

    private int HourFromX(int x)
    {
        var rect = _pnlChart.ClientRectangle;
        int leftMargin = 60, rightMargin = 20, topMargin = 30, bottomMargin = 30;
        int chartW = rect.Width - leftMargin - rightMargin;
        int chartH = rect.Height - topMargin - bottomMargin;
        if (chartW <= 0) return -1;
        int barW = chartW / 24;
        if (barW <= 0) return -1;
        if (x < leftMargin || x >= leftMargin + chartW) return -1;
        return Math.Min(23, (x - leftMargin) / barW);
    }

    private void PaintChart(object? sender, PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        var rect = _pnlChart.ClientRectangle;
        int leftMargin = 60, rightMargin = 20, topMargin = 30, bottomMargin = 30;
        int chartW = rect.Width - leftMargin - rightMargin;
        int chartH = rect.Height - topMargin - bottomMargin;
        if (chartW <= 0 || chartH <= 0) return;

        long maxBytes = 0;
        for (int i = 0; i < 24; i++) if (_hourlyBytes[i] > maxBytes) maxBytes = _hourlyBytes[i];

        // V0.9.6: 绘制资源改为静态缓存（原每帧 new，闪烁 + GDI 泄漏）

        // 标题
        g.DrawString(string.Format(L.T("chart.title"), FormatBytes(maxBytes)),
            FontTitle, Brushes.DimGray, leftMargin, 5);

        // 画坐标轴
        g.DrawLine(PenAxis, leftMargin, topMargin, leftMargin, topMargin + chartH);  // Y 轴
        g.DrawLine(PenAxis, leftMargin, topMargin + chartH, leftMargin + chartW, topMargin + chartH);  // X 轴

        // Y 轴刻度（4 个：0%, 25%, 50%, 75%, 100%）
        for (int i = 0; i <= 4; i++)
        {
            int y = topMargin + chartH - (i * chartH / 4);
            long val = maxBytes * i / 4;
            g.DrawLine(PenAxis, leftMargin - 3, y, leftMargin, y);
            g.DrawString(FormatBytes(val), FontHour, Brushes.Gray, 5, y - 7);
            g.DrawLine(PenGrid, leftMargin + 1, y, leftMargin + chartW, y);
        }

        // 画柱
        int barW = chartW / 24;
        int maxHour = -1;
        for (int i = 0; i < 24; i++) if (_hourlyBytes[i] == maxBytes && maxBytes > 0) { maxHour = i; break; }

        for (int i = 0; i < 24; i++)
        {
            int x = leftMargin + i * barW + 1;
            int barH = maxBytes > 0 ? (int)((long)chartH * _hourlyBytes[i] / maxBytes) : 0;
            int y = topMargin + chartH - barH;
            var brush = i == maxHour ? BrushBarPeak : (i == _hoveredHour ? BrushBarHover : BrushBar);
            g.FillRectangle(brush, x, y, barW - 2, barH);

            // X 轴标签（每 4 小时显示）
            if (i % 4 == 0)
            {
                g.DrawString($"{i:00}", FontHour, Brushes.Gray, x, topMargin + chartH + 5);
            }
        }
    }

    private void FillStream(List<FileEvent> events, string filter)
    {
        _gridStream.SuspendLayout();
        // V0.9.12: 保留滚动位置（刷新把用户翻到的位置干回顶部 = 糟糕体验）
        int savedScroll = _gridStream.FirstDisplayedScrollingRowIndex;
        _gridStream.Rows.Clear();
        foreach (var e in events)
        {
            if (!string.IsNullOrEmpty(filter)
                && !MatchFilter(e.FullPath) && !MatchFilter(e.ProcessName)
                && !MatchFilter(e.ProcessPath)) continue;
            _gridStream.Rows.Add(
                e.Timestamp.ToString("MM-dd HH:mm:ss"),
                e.DriveLetter,
                e.EventType,
                e.FullPath,
                FormatBytes(e.SizeBytes),
                e.ProcessName,
                e.ProcessId == 0 ? "-" : e.ProcessId.ToString(),
                e.ProcessPath
            );
        }
        EnsureRowHeights(_gridStream, 36);  // V0.9.15: 确保所有行高度一致
        _gridStream.ResumeLayout(true);
        if (savedScroll > 0 && savedScroll < _gridStream.Rows.Count)
            try { _gridStream.FirstDisplayedScrollingRowIndex = savedScroll; } catch { }
    }

    private void ShowOffenderDetail(object? sender, DataGridViewCellEventArgs e)
    {
        if (e.RowIndex < 0) return;
        var row = _gridOffender.Rows[e.RowIndex];
        var procName = row.Cells["process"].Value?.ToString() ?? "";
        if (string.IsNullOrEmpty(procName)) return;

        var events = _monitor.Store.QueryEventsByProcess(procName, DateTime.Today);
        var sb = new System.Text.StringBuilder();
        sb.AppendLine(string.Format(L.T("detail.header"), procName, events.Count));
        sb.AppendLine(new string('─', 80));
        foreach (var ev in events.Take(200))
        {
            sb.AppendLine($"{ev.Timestamp:HH:mm:ss}  {ev.EventType,-8}  {FormatBytes(ev.SizeBytes),10}  {ev.FullPath}");
        }
        if (events.Count > 200) sb.AppendLine(string.Format(L.T("detail.truncated"), events.Count));

        MessageBox.Show(this, sb.ToString(), string.Format(L.T("detail.title"), procName),
            MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void SaveSettings(object? sender, EventArgs e)
    {
        // V0.9.14: 语言（重启生效）
        var newLang = _cboLanguage.SelectedIndex == 1 ? "en" : "zh";
        bool langChanged = newLang != _monitor.CurrentConfig.Language;

        var newDrives = new HashSet<string>();
        foreach (var item in _clbDrives.CheckedItems)
        {
            var s = item.ToString() ?? "";
            if (s.Length >= 1) newDrives.Add(s[0].ToString().ToUpperInvariant());
        }

        // 同步监控盘符
        var oldDrives = new HashSet<string>(_monitor.ActiveDrives);
        foreach (var d in oldDrives.Except(newDrives)) _monitor.RemoveDrive(d);
        foreach (var d in newDrives.Except(oldDrives)) _monitor.TryAddDrive(d);

        // 写注册表
        var exe = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? "";
        if (_chkAutoStart.Checked)
            Config.StartupManager.Enable(exe, _monitor.CurrentConfig.StartupDelaySeconds);
        else
            Config.StartupManager.Disable();

        // V3: 解析白名单/黑名单（每行一条，自动 trim，空行忽略）
        var include = (_txtInclude.Text ?? "")
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .ToList();
        var ignore = (_txtIgnore.Text ?? "")
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .ToList();

        // V5.1: 解析阈值（解析失败给默认值）
        long processThreshold = ParseMB(_txtProcessThreshold.Text, 100);
        long folderThreshold = ParseMB(_txtFolderThreshold.Text, 500);
        int surgeWindow = int.TryParse(_txtSurgeWindow.Text, out var w) && w >= 5 && w <= 3600 ? w : 60;
        int surgeCooldown = int.TryParse(_txtSurgeCooldown.Text, out var c) && c >= 1 && c <= 1440 ? c : 30;

        _monitor.UpdateConfig(c =>
        {
            c.MonitoredDrives = newDrives;
            c.AutoStart = _chkAutoStart.Checked;
            c.Language = newLang;                       // V0.9.14: 界面语言（重启生效）
            c.IncludePrefixes = include;
            c.IgnorePrefixes = ignore;
            c.ProcessSurgeThresholdBytes = processThreshold * 1024 * 1024;
            c.FolderSurgeThresholdBytes = folderThreshold * 1024 * 1024;
            c.SurgeWindowSeconds = surgeWindow;
            c.SurgeCooldownMinutes = surgeCooldown;
        });

        // V3: 同步到 PathFilterHolder（立即生效）
        PathFilterHolder.Current = new PathFilter
        {
            IncludePrefixes = new List<string>(include),
            IgnorePrefixes = new List<string>(ignore),
        };

        // V5.1: 同步阈值到 Aggregator（立即生效）
        _monitor.Aggregator.ProcessSurgeThresholdBytes = processThreshold * 1024 * 1024;
        _monitor.Aggregator.FolderSurgeThresholdBytes = folderThreshold * 1024 * 1024;
        _monitor.Aggregator.SurgeWindow = TimeSpan.FromSeconds(surgeWindow);
        _monitor.Aggregator.SurgeCooldown = TimeSpan.FromMinutes(surgeCooldown);

        // V0.9.15: 语言切换立即生效（不再需要重启）
        if (langChanged)
        {
            L.Load(newLang);
            ApplyLocalization();
            LanguageChanged?.Invoke();
        }

        MessageBox.Show(this,
            langChanged ? L.T("msg.settings.saved.lang") : L.T("msg.settings.saved"),
            "DiskEye",
            MessageBoxButtons.OK, MessageBoxIcon.Information);
        RefreshData();
    }

    private static long ParseMB(string s, long defaultMB)
    {
        if (long.TryParse(s, out var n) && n >= 1 && n <= 1024 * 1024) return n;
        return defaultMB;
    }

    private void OnClosing(object? sender, FormClosingEventArgs e)
    {
        if (_hideToTrayOnClose && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        SaveWindowPos();
    }

    private void SaveWindowPos()
    {
        if (WindowState != FormWindowState.Normal) return;
        // V0.9.3: 只在窗口至少与一个屏幕相交时保存（防最小化瞬间的异常坐标入库）
        if (!Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(Bounds))) return;
        _monitor.UpdateConfig(c =>
        {
            c.WindowX = Left;
            c.WindowY = Top;
            c.WindowWidth = ClientSize.Width;
            c.WindowHeight = ClientSize.Height;
        });
    }

    /// <summary>
    /// V0.9.4: 托盘/管道打开主窗的统一入口。
    /// 修复三件套：①最小化 → 还原；②位置在虚拟屏外 → 搬回主屏（实测 -32000 遗留）；
    /// ③后台进程 Activate 常被系统拒绝 → 窗口在 Z 序底部被其他窗口盖住（用户以为没弹出）。
    /// 用 TopMost 闪烁强制置顶。
    /// </summary>
    public void RestoreToScreen()
    {
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        // V0.9.10: 不只查"相交"，还要"完整放得下"——窗口高 914 从 y=397 起，底部超出屏
        // 高 1067，状态条等底部 UI 全被截掉（用户看不到）。超界则 clamp 回工作区。
        var wa = Screen.PrimaryScreen!.WorkingArea;
        bool fits = Screen.AllScreens.Any(s => wa.IntersectsWith(Bounds))
                    && Screen.AllScreens.Any(s => s.WorkingArea.Contains(Bounds));
        if (!fits)
        {
            StartPosition = FormStartPosition.Manual;
            int w = Math.Min(Width, wa.Width);
            int h = Math.Min(Height, wa.Height);
            if (w != Width || h != Height) ClientSize = new Size(w, h);
            Location = new Point(
                wa.Left + Math.Max(0, (wa.Width - Width) / 2),
                wa.Top + Math.Max(0, (wa.Height - Height) / 2));
        }
        Show();
        // 强制置顶：TopMost 短暂置真再还原，绕开前台锁定
        TopMost = true;
        Activate();
        TopMost = false;
        BringToFront();
    }

    /// <summary>V0.9.15: 快速枚举盘符（跳过 IsReady 检查，避免网络盘/空光驱卡 60 秒）。</summary>
    private static IEnumerable<string> EnumerateDrives()
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

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / 1024.0 / 1024.0:F1} MB";
        return $"{bytes / 1024.0 / 1024.0 / 1024.0:F2} GB";
    }

    /// <summary>V0.9.15: 语言切换后立即生效 — 通知 TrayIconManager 刷新菜单文字。</summary>
    public event Action? LanguageChanged;

    /// <summary>V0.9.15: 语言切换后重新设置所有控件文字（不需要重启）。</summary>
    private void ApplyLocalization()
    {
        // V0.9.15: 批量更新，防止逐控件刷新导致卡顿
        SuspendLayout();
        _tabs.SuspendLayout();
        _pnlSearch.SuspendLayout();
        _tabOffender.SuspendLayout();
        _tabFolder.SuspendLayout();
        _tabStream.SuspendLayout();
        _tabChart.SuspendLayout();
        _tabProcessTree.SuspendLayout();
        _tabClean.SuspendLayout();
        _tabTools.SuspendLayout();
        _tabSettings.SuspendLayout();
        try { ApplyLocalizationCore(); }
        finally
        {
            _tabSettings.ResumeLayout(true);
            _tabTools.ResumeLayout(true);
            _tabClean.ResumeLayout(true);
            _tabProcessTree.ResumeLayout(true);
            _tabChart.ResumeLayout(true);
            _tabStream.ResumeLayout(true);
            _tabFolder.ResumeLayout(true);
            _tabOffender.ResumeLayout(true);
            _pnlSearch.ResumeLayout(true);
            _tabs.ResumeLayout(true);
            ResumeLayout(true);
        }
    }

    private void ApplyLocalizationCore()
    {
        Text = L.T("app.title");

        // Tab 页标题
        _tabOffender.Text = L.T("tab.offender");
        _tabFolder.Text = L.T("tab.folders");
        _tabStream.Text = L.T("tab.stream");
        _tabChart.Text = L.T("tab.chart");
        _tabProcessTree.Text = L.T("tab.tree");
        _tabClean.Text = L.T("tab.clean");
        _tabTools.Text = L.T("tab.tools");
        _tabSettings.Text = L.T("tab.settings");

        // 搜索栏
        _lblSearchHint.Text = L.T("search.hint");
        _txtSearch.PlaceholderText = L.T("search.placeholder");
        _btnClearSearch.Text = L.T("search.clear");

        // 提示标签
        _lblOffenderHint.Text = L.T("hint.offender");
        _lblFolderHint.Text = L.T("hint.folders");
        _lblProcessTreeHint.Text = L.T("hint.tree");

        // V0.9.15: 暂停所有 grid 布局，批量更新列头
        _gridOffender.SuspendLayout();
        _gridFolder.SuspendLayout();
        _gridStream.SuspendLayout();
        _gridProcessTree.SuspendLayout();
        _gridClean.SuspendLayout();

        // 凶手指控榜列头
        SetColHeader(_gridOffender, "process", "col.process");
        SetColHeader(_gridOffender, "path", "col.exepath");
        SetColHeader(_gridOffender, "topfolder", "col.topfolder");
        SetColHeader(_gridOffender, "count", "col.events");
        SetColHeader(_gridOffender, "bytes", "col.writebytes");
        SetColHeader(_gridOffender, "last", "col.lastactivity");

        // 文件夹列头
        SetColHeader(_gridFolder, "folder", "col.folder");
        SetColHeader(_gridFolder, "count", "col.events");
        SetColHeader(_gridFolder, "bytes", "col.writebytes");
        SetColHeader(_gridFolder, "last", "col.lastactivity");

        // 事件流列头
        SetColHeader(_gridStream, "ts", "col.time");
        SetColHeader(_gridStream, "drive", "col.drive");
        SetColHeader(_gridStream, "etype", "col.type");
        SetColHeader(_gridStream, "path", "col.file");
        SetColHeader(_gridStream, "size", "col.filesize");
        SetColHeader(_gridStream, "proc", "col.process");
        SetColHeader(_gridStream, "pid", "col.pid");

        // 进程树列头
        SetColHeader(_gridProcessTree, "pid", "col.pid");
        SetColHeader(_gridProcessTree, "name", "col.process");
        SetColHeader(_gridProcessTree, "ppid", "col.ppid");
        SetColHeader(_gridProcessTree, "pname", "col.pname");
        SetColHeader(_gridProcessTree, "count", "col.events");
        SetColHeader(_gridProcessTree, "bytes", "col.writebytes");
        SetColHeader(_gridProcessTree, "last", "col.lastactivity");
        SetColHeader(_gridProcessTree, "path", "col.exepath");

        // 清理 tab
        _lblCleanHint.Text = L.T("clean.hint");
        _btnCleanScan.Text = L.T("clean.scan");
        _btnCleanRun.Text = L.T("clean.run");
        SetColHeader(_gridClean, "pick", "clean.col.pick");
        SetColHeader(_gridClean, "name", "clean.col.item");
        SetColHeader(_gridClean, "dir", "clean.col.path");
        SetColHeader(_gridClean, "files", "clean.col.files");
        SetColHeader(_gridClean, "bytes", "clean.col.freeable");

        // V0.9.15: 恢复所有 grid 布局
        _gridOffender.ResumeLayout(true);
        _gridFolder.ResumeLayout(true);
        _gridStream.ResumeLayout(true);
        _gridProcessTree.ResumeLayout(true);
        _gridClean.ResumeLayout(true);

        // 递归更新所有带 Tag 的控件（设置页 + 工具页 + 图表页 hint）
        ApplyLocRecursive(this);

        // V0.9.15: 重建盘符列表（CheckedListBox 的 item 是静态字符串，需要重建）
        var checkedDrives = new HashSet<string>();
        foreach (var item in _clbDrives.CheckedItems)
        {
            var s = item.ToString() ?? "";
            if (s.Length >= 1) checkedDrives.Add(s[0].ToString().ToUpperInvariant());
        }
        _clbDrives.BeginUpdate();
        try
        {
            _clbDrives.Items.Clear();
            foreach (var d in EnumerateDrives())
            {
                int idx = _clbDrives.Items.Add(string.Format(L.T("settings.drive.item"), d));
                if (checkedDrives.Contains(d)) _clbDrives.SetItemChecked(idx, true);
            }
        }
        finally { _clbDrives.EndUpdate(); }

        // V0.9.15: 刷新工具页统计标签
        var lblStats = _tabTools.Controls.Find("lblStats", true).FirstOrDefault() as Label;
        if (lblStats != null) lblStats.Text = ComputeStats();

        // V0.9.15: 垃圾清理页——如果已有扫描结果，重新扫描以刷新目标名称（目标名用 L.T 生成）
        if (_gridClean.Rows.Count > 0) ScanCleanTargets();

        // 状态栏立即刷新
        UpdateStatusBar(_monitor.Aggregator.IsPrecise);
    }

    private static void SetColHeader(DataGridView grid, string colName, string key)
    {
        if (grid.Columns[colName] is DataGridViewTextBoxColumn col)
            col.HeaderText = L.T(key);
    }

    private static void ApplyLocRecursive(Control root)
    {
        // V0.9.15: 跳过不可见控件（隐藏 tab 页的控件不需要立即更新，下次切到该 tab 时自然会刷新）
        if (!root.Visible && root is not Form) return;
        foreach (Control c in root.Controls)
        {
            if (c.Tag is string key && !string.IsNullOrEmpty(key))
                c.Text = L.T(key);
            if (c is DataGridView dgv)
            {
                foreach (DataGridViewColumn col in dgv.Columns)
                {
                    if (col.Tag is string ck && !string.IsNullOrEmpty(ck))
                        col.HeaderText = L.T(ck);
                }
            }
            ApplyLocRecursive(c);
        }
    }
}
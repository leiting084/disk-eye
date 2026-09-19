using System.Runtime.InteropServices;

namespace DiskEye.Forms;

/// <summary>
/// V0.9.8: 统一应用图标（托盘/窗体/任务栏共用）。
/// 修复：主窗曾用空白 Bitmap 当图标——任务栏按钮显示白块，被用户当作"没有图标"。
/// </summary>
internal static class AppIcon
{
    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    private static Icon? _eye;

    public static Icon Eye => _eye ??= CreateEyeIcon();

    private static Icon CreateEyeIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var bg = new SolidBrush(Color.FromArgb(40, 40, 50));
            g.FillEllipse(bg, 0, 0, 31, 31);
            using var white = new SolidBrush(Color.FromArgb(0, 170, 230));
            g.FillEllipse(white, 6, 12, 20, 10);
            using var pupil = new SolidBrush(Color.FromArgb(20, 20, 30));
            g.FillEllipse(pupil, 12, 13, 8, 8);
            g.FillEllipse(Brushes.White, 14, 14, 3, 3);
        }
        var hicon = bmp.GetHicon();
        var icon = (Icon)Icon.FromHandle(hicon).Clone();
        DeleteObject(hicon);  // 防 GDI 句柄泄漏（V0.8 教训）
        return icon;
    }
}

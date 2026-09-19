using Microsoft.Win32;

namespace DiskEye.Config;

/// <summary>
/// 开机自启动：写入 HKCU\Software\Microsoft\Windows\CurrentVersion\Run。
/// 不需要管理员权限（HKCU 不需要）。
/// </summary>
public static class StartupManager
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "DiskEye";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) != null;
    }

    public static void Enable(string exePath, int delaySeconds = 30)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        // V7.4: 自启延迟可配，默认 30 秒（避免开机时和系统 IO 抢资源）+ 启动时最小化
        var delay = delaySeconds >= 0 && delaySeconds <= 600 ? delaySeconds : 30;
        key.SetValue(ValueName, $"\"{exePath}\" --minimized --delay {delay}");
    }

    public static void Disable()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (key?.GetValue(ValueName) != null)
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}
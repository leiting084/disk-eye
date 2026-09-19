using System.Text.Json;
using DiskEye.Models;

namespace DiskEye.Config;

/// <summary>
/// 应用配置持久化。V0.8.1：绿色版——config.json / events.db 跟 exe 同目录；
/// exe 目录只读（如 Program Files）时回落 %LOCALAPPDATA%\DiskEye\。
/// 首次启动时把 LocalAppData 里的旧数据迁移到 exe 旁边。
/// </summary>
public static class AppConfigStore
{
    private static string? _configDir;

    public static string ConfigDir => _configDir ??= ResolveConfigDir();

    private static string ResolveConfigDir()
    {
        var legacyDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DiskEye");
        var exeDir = AppContext.BaseDirectory;
        try
        {
            // 探测 exe 目录可写
            var probe = Path.Combine(exeDir, ".write_probe.tmp");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            MigrateFromLegacy(legacyDir, exeDir);
            return exeDir;
        }
        catch
        {
            return legacyDir;
        }
    }

    /// <summary>把 %LOCALAPPDATA%\DiskEye 的旧配置和数据库拷到 exe 旁边（exe 旁已有配置则不覆盖）。</summary>
    private static void MigrateFromLegacy(string legacyDir, string exeDir)
    {
        try
        {
            if (string.Equals(Path.GetFullPath(legacyDir).TrimEnd('\\'),
                    Path.GetFullPath(exeDir).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                return;
            if (!Directory.Exists(legacyDir)) return;
            if (File.Exists(Path.Combine(exeDir, "config.json"))) return;  // exe 旁已有配置，不动

            foreach (var name in new[] { "config.json", "events.db", "events.db-wal", "events.db-shm" })
            {
                var src = Path.Combine(legacyDir, name);
                var dst = Path.Combine(exeDir, name);
                if (File.Exists(src) && !File.Exists(dst))
                    File.Copy(src, dst);
            }
        }
        catch { /* 迁移失败不阻塞启动（旧数据还在原处） */ }
    }

    public static string ConfigPath => Path.Combine(ConfigDir, "config.json");

    private static readonly JsonSerializerOptions _json = new() { WriteIndented = true };

    public static AppConfig Load()
    {
        if (!File.Exists(ConfigPath)) return new AppConfig();
        try
        {
            var json = File.ReadAllText(ConfigPath);
            return JsonSerializer.Deserialize<AppConfig>(json) ?? new AppConfig();
        }
        catch
        {
            // 配置损坏时不阻塞启动，回退到默认
            return new AppConfig();
        }
    }

    public static void Save(AppConfig cfg)
    {
        Directory.CreateDirectory(ConfigDir);
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(cfg, _json));
    }
}
namespace DiskEye.Forms;

/// <summary>
/// V0.9.13 轻量双语（中文默认 / English）。V0.9.14 起字典改为编译期 C# 常量（Strings.Zh/En）——
/// 内嵌 JSON 曾因资源命名规则导致 key 裸奔，编译期字典零加载失败面。
/// 用法：L.T("tab.offender")；切换语言后 MainForm.ApplyLocalization() 立即生效（无需重启）。
/// 回退链：当前语言缺失 → 中文 → key 本身。
/// </summary>
public static class L
{
    private static Dictionary<string, string>? _cur;

    /// <summary>"zh"（默认）或 "en"。在窗体构建前调用 Load。</summary>
    public static string Culture { get; private set; } = "zh";

    public static void Load(string culture)
    {
        Culture = culture == "en" ? "en" : "zh";
        _cur = Culture == "en" ? Strings.En : Strings.Zh;
    }

    /// <summary>翻译。当前语言缺失回退中文；都没有则返回 key。</summary>
    public static string T(string key)
    {
        if (_cur != null && _cur.TryGetValue(key, out var v)) return v;
        if (Strings.Zh.TryGetValue(key, out var f)) return f;
        return key;
    }
}

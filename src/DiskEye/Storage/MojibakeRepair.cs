using System.Text;

namespace DiskEye.Storage;

/// <summary>
/// 历史乱码自愈（V0.9.17）。
///
/// 背景：V0.9.15 及更早，ETW 子进程用 UTF-8 写 stdout，主进程却按 Console.OutputEncoding
/// （中文系统 = CP936）解码，于是所有非 ASCII 路径都被"UTF-8 字节 → 按 GBK 解读"污染后落库：
///   D:\tools\0.mytools\DiskEye 磁盘监控  →  D:\tools\0.mytools\DiskEye 纾佺洏鐩戞帶
/// V0.9.16 修好了写入端（FrameProtocol.StreamEncoding），但**库里已有的脏数据不会自己变干净** ——
/// 用户在界面上看到的乱码列表就是这些历史行。本类负责把它们还原。
///
/// 逆变换：把串按 GB18030 编回字节，再按 UTF-8 解 —— 得到原文。
///
/// 两个必须踩准的点：
/// 1. **优先用 CP936（936），而不是 GB18030**。写过一版用 GB18030，测试里 `智能待办清单`
///    还原成了 `智能待䊡清单` —— 因为 GB18030 的双字节区虽号称 GBK 超集，个别码位（如 `呬`）
///    的映射与 CP936 并不一致。而污染数据的**正是** Console.OutputEncoding 背后的 CP936，
///    用同一个码表做逆变换才对得上。GB18030 只作为 936 拿不到时的兜底。
///    （顺带解释了上一轮 Python 脚本的失败：Python 的 `gbk` codec 用的是 Unicode 官方映射，
///    不含 CP936 的私用区映射，所以编不回去；那是 codec 表不全，不是方向不对。）
/// 2. **编码器必须设 ExceptionFallback**。.NET 默认是 ReplacementFallback —— 遇到编不了的
///    字符会**静默替换成 '?'**，那就把"只是乱码"的数据改成了永久损坏。宁可跳过，不可乱改。
///
/// 判据方向决定了它不会误伤正常数据：正常中文按 GBK 编出的字节通常不是合法 UTF-8，
/// 解码这步会失败，于是原样返回 null。
/// </summary>
internal static class MojibakeRepair
{
    private static readonly object Gate = new();
    private static Encoding[] _codepages = Array.Empty<Encoding>();
    private static bool _resolved;

    /// <summary>
    /// 还原用的码表链：CP936 优先（与污染源一致），GB18030 兜底。系统码表，
    /// .NET Core 需先注册 provider。一个都拿不到时返回空数组 → 自愈静默跳过。
    /// </summary>
    internal static Encoding[] CodePages
    {
        get
        {
            if (_resolved) return _codepages;
            lock (Gate)
            {
                if (_resolved) return _codepages;
                var found = new List<Encoding>(2);
                try
                {
                    Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                    foreach (var cp in new[] { 936, 54936 })
                    {
                        try
                        {
                            found.Add(Encoding.GetEncoding(cp,
                                EncoderFallback.ExceptionFallback,   // 编不了就抛，绝不静默替换成 '?'
                                DecoderFallback.ExceptionFallback));
                        }
                        catch { /* 该码表不可用，试下一个 */ }
                    }
                }
                catch { /* provider 注册失败 */ }
                _codepages = found.ToArray();
                _resolved = true;
                return _codepages;
            }
        }
    }

    private static readonly Encoding StrictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    /// <summary>
    /// 尝试还原一个乱码串。返回 null 表示"不是乱码/拿不准，别动它"。
    /// 幂等：还原后的正常串再进来会返回 null。
    /// </summary>
    internal static string? TryRepair(string? s)
    {
        if (string.IsNullOrEmpty(s)) return null;

        bool nonAscii = false;
        foreach (var c in s)
        {
            if (c > 0x7F) { nonAscii = true; break; }
        }
        if (!nonAscii) return null;   // 纯 ASCII 不可能是这种乱码

        foreach (var legacy in CodePages)
        {
            byte[] bytes;
            try { bytes = legacy.GetBytes(s); }
            catch (EncoderFallbackException) { continue; }

            string candidate;
            try { candidate = StrictUtf8.GetString(bytes); }
            catch (DecoderFallbackException) { continue; }

            if (candidate == s) return null;               // 固定点 = 本来就是好的
            if (!ContainsCjk(candidate)) return null;      // 还原结果得有中文，否则多半是误判
            if (HasPrivateUse(candidate)) return null;     // 真路径不含私用区字符 → 还原不干净，别写回
            return candidate;
        }
        return null;
    }

    internal static bool ContainsCjk(string s)
    {
        foreach (var c in s)
        {
            if (c >= 0x4E00 && c <= 0x9FFF) return true;
        }
        return false;
    }

    internal static bool HasPrivateUse(string s)
    {
        foreach (var c in s)
        {
            if (c >= 0xE000 && c <= 0xF8FF) return true;
        }
        return false;
    }
}

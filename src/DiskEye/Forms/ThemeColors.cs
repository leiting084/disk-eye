namespace DiskEye.Forms;

/// <summary>
/// V0.9.7 统一色板（设计 agent 产出，全部可直译 C# Color）。
/// 令牌化替代散落的 FromArgb 魔法值。
/// </summary>
internal static class ThemeColors
{
    // 主色
    public static readonly Color Primary = Color.FromArgb(0x2B, 0x7D, 0xE0);      // #2B7DE0
    public static readonly Color PrimaryHover = Color.FromArgb(0x1E, 0x63, 0xB8); // #1E63B8
    public static readonly Color PrimaryPressed = Color.FromArgb(0x17, 0x53, 0x9F);
    public static readonly Color PrimaryTint = Color.FromArgb(0xE8, 0xF1, 0xFB);  // #E8F1FB

    // 背景层级
    public static readonly Color B0Page = Color.FromArgb(0xEE, 0xF2, 0xF7);       // #EEF2F7 页面底/表头/Tab区
    public static readonly Color B1Surface = Color.White;                          // 表格/搜索栏
    public static readonly Color B2Zebra = Color.FromArgb(0xF5, 0xF8, 0xFC);      // 斑马纹

    // 边框
    public static readonly Color Border = Color.FromArgb(0xD8, 0xDE, 0xE8);
    public static readonly Color Divider = Color.FromArgb(0xE6, 0xEB, 0xF2);

    // 文本层级
    public static readonly Color T1 = Color.FromArgb(0x1F, 0x29, 0x33);
    public static readonly Color T2 = Color.FromArgb(0x5A, 0x66, 0x75);
    public static readonly Color T3 = Color.FromArgb(0x98, 0xA2, 0xB0);

    // 状态
    public static readonly Color Success = Color.FromArgb(0x2F, 0xA3, 0x6B);
    public static readonly Color SuccessBg = Color.FromArgb(0xE6, 0xF6, 0xEE);
    public static readonly Color SuccessText = Color.FromArgb(0x1E, 0x7A, 0x50);
    public static readonly Color Warn = Color.FromArgb(0xE6, 0xA2, 0x3C);
    public static readonly Color WarnBg = Color.FromArgb(0xFD, 0xF3, 0xE3);
    public static readonly Color WarnText = Color.FromArgb(0x9A, 0x65, 0x10);
    public static readonly Color Danger = Color.FromArgb(0xD6, 0x45, 0x45);

    // 选中
    public static readonly Color RowSelected = Color.FromArgb(0xD6, 0xE7, 0xFA);

    // 数字列
    public static readonly Font NumericFont = new("Consolas", 9f);
}

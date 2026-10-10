using System.Drawing;
using System.Text.RegularExpressions;

namespace BlogTool;

/// <summary>一行日志的显示样式。</summary>
public sealed record LogLineStyle(Color Color, bool Bold);

/// <summary>
/// 把纯文本日志行判成该用什么颜色显示，做出终端那种一眼能分轻重的感觉。
///
/// 不解析 ANSI 转义序列再还原颜色，而是**剥掉后自己判**。原因：这些输出
/// 不是从真终端来的（没有 TTY），对方不会发颜色码；就算某些包无条件发了，
/// 直接打进控件也只会显示成 [0m 之类的方块。所以剥掉、再按语义重新上色，
/// 比实现一个 ANSI 解释器省事得多也稳得多。
///
/// 配色按浅色背景选的：深色终端那套 ANSI 色直接搬到白底上会看不清。
/// </summary>
public static class LogStyle
{
    // ANSI 控制序列。astro/vite 里有包会无条件发。
    private static readonly Regex Ansi = new(
        @"\x1B\[[0-9;?]*[ -/]*[@-~]|\x1B[@-Z\\-_]", RegexOptions.Compiled);

    /// <summary>阶段标题用的最深色。</summary>
    public static readonly Color Heading = Color.FromArgb(24, 28, 34);
    public static readonly Color Normal = Color.FromArgb(58, 66, 76);
    public static readonly Color Dim = Color.FromArgb(140, 150, 162);
    public static readonly Color Accent = Color.FromArgb(11, 95, 208);    // 命令行
    public static readonly Color Good = Color.FromArgb(18, 133, 90);     // 成功
    public static readonly Color Bad = Color.FromArgb(192, 57, 43);      // 失败

    /// <summary>剥掉 ANSI 控制序列。</summary>
    public static string Clean(string line) => Ansi.Replace(line, "");

    /// <summary>判断一行该显示成什么样式。</summary>
    public static LogLineStyle Classify(string line)
    {
        var t = line.Trim();

        // 命令行："> pnpm build" 这种。用醒目色让人一眼找到「刚才跑的是什么」。
        if (t.StartsWith('>')) return new LogLineStyle(Accent, true);
        // 阶段标题：▸ 构建 / ▸ 提交
        if (t.StartsWith('▸')) return new LogLineStyle(Heading, true);

        // 失败要先判。「构建没通过」这类一句话里可能同时有正负两个词，
        // 先命中成功色就会把报错染绿，比不染色更糟。
        if (Contains(t, "✗", "×", "失败", "错误", "error", "Error", "ERROR", "ERR!")) 
            return new LogLineStyle(Bad, false);
        if (Contains(t, "✓", "√", "成功", "完成", "已提交", "已推送", "已创建", "已清除"))
            return new LogLineStyle(Good, false);

        // 构建工具的时间戳行（"[build] xxx"）压暗，属于过程噪音
        if (t.StartsWith('[') || StartsWithTimestamp(t)) return new LogLineStyle(Dim, false);

        // 空行没有信息可显示，跟正文同色就行
        return new LogLineStyle(Normal, false);
    }

    private static bool Contains(string text, params string[] tokens)
    {
        foreach (var t in tokens)
            if (text.Contains(t, StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>行首是 14:26:59 这种时间戳。</summary>
    private static bool StartsWithTimestamp(string text)
    {
        if (text.Length < 9 || text[2] != ':' || text[5] != ':') return false;
        return text[..2].All(char.IsDigit) && text[3..5].All(char.IsDigit) && text[6..8].All(char.IsDigit);
    }
}
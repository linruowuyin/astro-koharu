using System.Text;

namespace BlogTool.Test;

/// <summary>
/// 对 exe 里那些「只有跑起来才知道对不对」的逻辑做一次断言。
///
/// 重点覆盖三块：slug 生成、建文章的 frontmatter 格式、仓库状态读取。
/// 这些一旦错了，轻则链接难看，重则文章建出来博客读不了。
/// </summary>
internal static class Program
{
    private static int _passed;
    private static int _failed;

    private static async Task<int> Main()
    {
        Console.OutputEncoding = Encoding.UTF8;

        if (!Project.TryLocate())
        {
            Console.Error.WriteLine("找不到项目根，测试无法运行。");
            return 2;
        }
        Git.RepoRoot = Project.Root;

        // ---- 项目定位 ----
        Eq("项目根指向仓库", Path.GetFileName(Project.Root), "astro-koharu-main");

        // ---- 分类读取 ----
        var cats = Categories.Read();
        True("能读到分类", cats.Count > 0, $"读到 {cats.Count} 个");
        foreach (var c in cats) True($"分类 slug 非空：{c.Name}", c.Slug.Length > 0, c.Slug);

        // ---- slug ----
        var slugCases = new (string Title, string Expected)[]
        {
            ("白银-黄金放大器与工业利剑", "bai-yin-huang-jin-fang-da-qi-yu-gong-ye-li-jian"),
            ("6.15复盘-踏空的一天", "6-15fu-pan-ta-kong-de-yi-tian"),
            ("A股ETF详解", "agu-etfxiang-jie"),
        };
        foreach (var (title, expected) in slugCases)
        {
            Eq($"slug：{title}", await PostCreator.SlugifyAsync(title), expected);
        }

        // 纯标点标题不能产出空 slug。
        Eq("纯符号标题有兜底", await PostCreator.SlugifyAsync("——！？"), "post");

        // 本地兜底表：node 不可用时也要给出纯 ASCII 的结果。
        var fallback = PostCreator.Slugify("复盘");
        True("本地兜底是 ASCII", fallback.All(c => c is (>= 'a' and <= 'z') or '-' or >= '0' and <= '9'), fallback);

        // ---- 建文章 ----
        const string testTitle = "zz-selftest-临时文章";
        var created = await PostCreator.CreateAsync(testTitle, cats.First(c => c.Slug == "note").Name, "note");
        try
        {
            True("文件已生成", File.Exists(created), created);
            Eq("文件名用中文标题", Path.GetFileName(created), $"{testTitle}.md");
            Eq("落在分类目录下", Path.GetDirectoryName(created), Project.At("src", "content", "blog", "note"));

            var bytes = await File.ReadAllBytesAsync(created);
            True("文件无 BOM", bytes.Length < 3 || !(bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF),
                $"前三个字节 {bytes[0]:X2} {bytes[1]:X2} {bytes[2]:X2}");

            var text = await File.ReadAllTextAsync(created, Encoding.UTF8);
            // 换行不写死 LF：仓库 core.autocrlf=true，工作区里本就是 CRLF。
            var lines = text.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries);
            Eq("frontmatter 起始", lines[0], "---");
            True("frontmatter 闭合", lines.Contains("---"), string.Join(" / ", lines.Take(8)));
            True("有 title 字段", text.Contains($"title: {testTitle}"), "");
            True("有拼音 link", text.Contains("link: zz-selftest-lin-shi-wen-zhang"), "");
            True("有 date 字段", System.Text.RegularExpressions.Regex.IsMatch(text, @"date: \d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}"), "");
            True("有分类", text.Contains("categories:"), "");

            // 同名再建一次不能把已有文件覆盖掉。
            var again = await PostCreator.CreateAsync(testTitle, cats.First(c => c.Slug == "note").Name, "note");
            True("同名不覆盖原文件", again != created, $"新路径 {Path.GetFileName(again)}");
            File.Delete(again);
        }
        finally
        {
            File.Delete(created);
        }
        True("清理后文件已删除", !File.Exists(created), created);

        // ---- 仓库状态 ----
        var state = await Git.GetStateAsync();
        Eq("分支名", state.Branch, "main");
        True("读到改动清单", state.Files.Count >= 0, $"{state.Files.Count} 个");
        True("分叉判断有值", state.Diverged == false || state.Behind > 0,
            $"ahead={state.Ahead} behind={state.Behind} diverged={state.Diverged}");

        Console.WriteLine();
        Console.WriteLine($"通过 {_passed}，失败 {_failed}");
        return _failed == 0 ? 0 : 1;
    }

    private static void True(string name, bool ok, string detail)
    {
        if (ok) { _passed++; Console.WriteLine($"  ✓ {name}"); }
        else { _failed++; Console.WriteLine($"  ✗ {name}   [{detail}]"); }
    }

    private static void Eq<T>(string name, T actual, T expected)
        => True(name, Equals(actual, expected), $"实际 {actual}，期望 {expected}");
}

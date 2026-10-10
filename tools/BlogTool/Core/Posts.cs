using System.Text;
using System.Text.RegularExpressions;

namespace BlogTool;

/// <summary>
/// 一篇文章的元信息。
///
/// 只带 frontmatter 里那几个字段，不留正文——文章库列表只需要这些，
/// 而 62 篇正文全读进内存既慢又没必要（要搜正文时按需单独读那一个文件）。
/// </summary>
public sealed record PostInfo
{
    public required string Title { get; init; }
    public required string Slug { get; init; }
    public required string CategoryDir { get; init; }   // research / note / fund …
    public string CategoryName { get; init; } = "";     // 投研 / 笔记 …
    public DateTime Date { get; init; }
    public IReadOnlyList<string> Tags { get; init; } = [];
    public string Cover { get; init; } = "";
    public bool Encrypted { get; init; }                // frontmatter 里有 password
    public string RelativePath { get; init; } = "";     // 相对项目根，正斜杠

    /// <summary>绝对路径，供「打开源文件」用。</summary>
    public string FullPath
    {
        get
        {
            var parts = RelativePath.Split('/');
            var all = new string[parts.Length + 1];
            all[0] = Project.Root;
            Array.Copy(parts, 0, all, 1, parts.Length);
            return Path.Combine(all);
        }
    }

    /// <summary>
    /// 线上地址。
    ///
    /// 规则对着 src/pages/post/[...slug].astro 与 lib/content/locale.ts 的
    /// getPostSlug：slug 直接取 frontmatter 的 link，取不到才转写文件名。
    /// 62 篇都写了 link，所以这里不需要实现转写——没有 link 的老文章
    /// 就干脆不给链接，宁可少给也不给错地址。
    /// </summary>
    public string OnlineUrl => OnlineUrlFor(SiteUrl);

    /// <summary>按给定站点地址拼线上链接。站点地址或 slug 缺一就不给。</summary>
    public string OnlineUrlFor(string siteUrl)
        => Slug.Length > 0 && siteUrl is { Length: > 0 }
            ? $"{siteUrl.TrimEnd('/')}/post/{Slug}"
            : "";

    /// <summary>从 site.yaml 读到的站点地址，文章库所有链接都基于它。</summary>
    public static string SiteUrl { get; set; } = "";
}

/// <summary>
/// 文章库：扫描 src/content/blog 下的全部文章并提供搜索。
///
/// frontmatter 用手写解析而不是引 YAML 库，理由和 Categories 一样：
/// 这份文件的开头是固定格式的正文头，引入依赖会让 exe 再涨几十 MB。
/// 真正的 YAML 解析交给构建期的 astro，用户编辑的也只是这几个固定字段。
/// </summary>
public static class Posts
{
    private const string BlogDir = "src/content/blog";

    /// <summary>扫描全部文章，按日期倒序（新的在前）。</summary>
    public static List<PostInfo> ReadAll()
    {
        var result = new List<PostInfo>();
        var root = Path.Combine(Project.Root, "src", "content", "blog");
        if (!Directory.Exists(root)) return result;

        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in Categories.Read()) names[c.Slug] = c.Name;

        foreach (var file in Directory.EnumerateFiles(root, "*.md", SearchOption.AllDirectories))
        {
            var post = ReadOne(file, names);
            if (post is not null) result.Add(post);
        }

        result.Sort((a, b) => b.Date.CompareTo(a.Date));
        return result;
    }

    /// <summary>读单篇文章。解析不出来返回 null，不抛——列表里少一篇不该让整个库打不开。</summary>
    public static PostInfo? ReadOne(string path, Dictionary<string, string>? categoryNames = null)
    {
        string[] lines;
        try
        {
            lines = File.ReadAllLines(path);
        }
        catch
        {
            return null;
        }
        if (lines.Length == 0 || lines[0].Trim() != "---") return null;

        var fm = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var tags = new List<string>();
        var categories = new List<string>();
        var inTags = false;
        var inCategories = false;

        for (var i = 1; i < lines.Length; i++)
        {
            var line = lines[i];

            if (line.Trim() == "---") break;                    // frontmatter 结束

            // 列表项：tags / categories 下面的 "  - xxx"
            var dash = Regex.Match(line, @"^\s+-\s+(.+?)\s*$");
            if (dash.Success && (inTags || inCategories))
            {
                var item = Unquote(dash.Groups[1].Value);
                if (inTags) tags.Add(item);
                else categories.Add(item);
                continue;
            }

            var kv = Regex.Match(line, @"^([A-Za-z][A-Za-z0-9_\-]*):\s*(.*)$");
            if (!kv.Success)
            {
                // 缩进块（没有 key 的行）说明还在上一个列表里，保持原状态
                if (line.Trim().Length > 0 && char.IsWhiteSpace(line[0])) continue;
                inTags = inCategories = false;
                continue;
            }

            inTags = inCategories = false;
            var key = kv.Groups[1].Value;
            var raw = kv.Groups[2].Value;

            // 行尾注释只在值后面还有空格时才当注释，避免把 URL 里的 # 砍掉
            var value = raw;
            var hash = Regex.Match(raw, @"\s+#.*$");
            if (hash.Success) value = raw[..hash.Index];

            if (value.Trim().Length == 0)
            {
                inTags = key.Equals("tags", StringComparison.OrdinalIgnoreCase);
                inCategories = key.Equals("categories", StringComparison.OrdinalIgnoreCase);
                continue;
            }
            fm[key] = Unquote(value.Trim());
        }

        var title = Get(fm, "title");
        if (title.Length == 0) return null;

        var rel = Path.GetRelativePath(Project.Root, path).Replace('\\', '/');
        var dir = Path.GetFileName(Path.GetDirectoryName(path)) ?? "";
        var catName = categories.Count > 0 ? categories[0] : "";
        if (catName.Length == 0 && categoryNames is not null && categoryNames.TryGetValue(dir, out var mapped))
            catName = mapped;

        return new PostInfo
        {
            Title = title,
            Slug = Get(fm, "link"),
            CategoryDir = dir,
            CategoryName = catName,
            Date = ParseDate(Get(fm, "date")),
            Tags = tags,
            Cover = Get(fm, "cover"),
            Encrypted = fm.ContainsKey("password"),
            RelativePath = rel,
        };
    }

    /// <summary>
    /// 搜索。
    ///
    /// 标题/分类/标签走内存里的元信息，不够时才回磁盘读正文——
    /// 62 篇正文全读一遍对每次按键来说太奢侈了。
    /// </summary>
    public static List<PostInfo> Search(IEnumerable<PostInfo> all, string keyword, bool searchBody)
    {
        var kw = keyword.Trim();
        if (kw.Length == 0) return all.ToList();

        // 空格分隔的多个词按「与」处理：搜"白银 黄金"要两词都有
        var words = kw.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        return all.Where(p =>
        {
            var meta = string.Join(' ', new[] { p.Title, p.CategoryName, p.CategoryDir }
                .Concat(p.Tags)).ToLowerInvariant();

            foreach (var w in words)
            {
                if (!meta.Contains(w, StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            if (!searchBody) return true;

            // 正文只在元信息没命中时才读，命中就不必付出读盘代价
            try
            {
                var body = File.ReadAllText(p.FullPath);
                foreach (var w in words)
                    if (!body.Contains(w, StringComparison.OrdinalIgnoreCase)) return false;
                return true;
            }
            catch
            {
                return false;
            }
        }).ToList();
    }

    /// <summary>全部文章里出现过的标签，按使用次数倒序。</summary>
    public static List<(string Tag, int Count)> AllTags(IEnumerable<PostInfo> posts)
        => posts.SelectMany(p => p.Tags)
            .GroupBy(t => t, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Tag: g.Key, Count: g.Count()))
            .OrderByDescending(x => x.Count)
            .ThenBy(x => x.Tag, StringComparer.Ordinal)
            .ToList();

    /// <summary>去掉引号。YAML 里值可以带引号，不剥掉的话标题会多出一对引号。</summary>
    private static string Unquote(string value)
    {
        if (value.Length >= 2
            && ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
            return value[1..^1];
        return value;
    }

    private static string Get(Dictionary<string, string> map, string key)
        => map.TryGetValue(key, out var v) ? v : "";

    /// <summary>
    /// 解析日期。frontmatter 里既有 "2026-02-08 10:39:44" 也有 "2026-02-08"，
    /// 还有的写成带引号的 ISO 串，三种都得吃下。解析不了就退回纪元，
    /// 排在列表最后——总比整篇被丢掉好。
    /// </summary>
    private static DateTime ParseDate(string raw)
    {
        var value = Unquote(raw.Trim());
        if (value.Length == 0) return DateTime.MinValue;

        var formats = new[]
        {
            "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm", "yyyy-MM-dd",
            "yyyy/MM/dd HH:mm:ss", "yyyy/MM/dd", "yyyyMMdd",
        };
        if (DateTime.TryParseExact(value, formats, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out var exact))
            return exact;

        return DateTime.TryParse(value, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out var loose) ? loose : DateTime.MinValue;
    }
}

/// <summary>
/// 站点体检：扫配置与内容里那些「看起来配了、其实用不了」的地方。
///
/// 只读，不改任何文件。发现的每一条都给出文件与行号，可以直接跳过去改。
/// </summary>
public static class SiteHealth
{
    public sealed record Finding(string Level, string Title, string Detail, string File, int Line);

    private static readonly string[] Severity = { "错误", "警告", "提示" };

    /// <summary>跑一遍体检。</summary>
    public static List<Finding> Scan()
    {
        var found = new List<Finding>();

        CheckUmami(found);
        CheckWaline(found);
        CheckPlaceholder(found);
        CheckExternalServices(found);

        return found;
    }

    /// <summary>
    /// Umami 统计：占位 id + 占位 endpoint 会让每页都发一次注定失败的请求。
    /// 这是已知问题，扫出来免得忘了。
    /// </summary>
    private static void CheckUmami(List<Finding> found)
    {
        var path = Project.At("config", "site.yaml");
        if (!File.Exists(path)) return;
        var lines = File.ReadAllLines(path);

        var enabled = false;
        var id = "";
        var endpoint = "";
        for (var i = 0; i < lines.Length; i++)
        {
            var t = lines[i].Trim();
            if (t.StartsWith("analytics:")) enabled = true;
            else if (enabled && t.StartsWith("id:")) id = t[3..].Trim();
            else if (enabled && t.StartsWith("endpoint:")) endpoint = t[9..].Trim();
        }

        if (!enabled) return;
        if (id.Contains("your-") || id.Length == 0)
            found.Add(new Finding(Severity[1], "Umami 统计是占位配置",
                $"id 为 {id}，每页都会触发一次注定失败的请求。要么填真 id，要么把 analytics 关掉。",
                "config/site.yaml", FindLine(lines, "id:")));
        if (endpoint.Contains("example.com"))
            found.Add(new Finding(Severity[1], "Umami endpoint 是占位地址",
                $"endpoint = {endpoint}，这是保留示例域名，永远解析不到自己的统计服务。",
                "config/site.yaml", FindLine(lines, "endpoint:")));
    }

    /// <summary>评论区：域名指向 Vercel 弃用占位 IP 的话，握手必然失败。</summary>
    private static void CheckWaline(List<Finding> found)
    {
        var path = Project.At("config", "site.yaml");
        if (!File.Exists(path)) return;
        var lines = File.ReadAllLines(path);

        foreach (var (i, line) in lines.Select((l, i) => (i, l)))
        {
            if (!line.Contains("serverURL", StringComparison.OrdinalIgnoreCase)) continue;
            var url = line.Split(':', 2).LastOrDefault()?.Trim().Trim('"', '\'') ?? "";
            if (url.Length == 0) continue;

            found.Add(new Finding(Severity[0], "评论区地址需要确认可用",
                $"serverURL = {url}。这个域名此前解析到 Vercel 弃用占位 IP，TLS 握手失败，" +
                "评论功能实际是坏的。修之前先访问一下确认。",
                "config/site.yaml", i + 1));
            break;
        }
    }

    /// <summary>全站搜残留的占位文本，上游模板带过来的那种。</summary>
    private static void CheckPlaceholder(List<Finding> found)
    {
        var markers = new[] { "your-umami-id", "example.com", "TODO", "待补充" };
        foreach (var file in new[]
        {
            Project.At("config", "site.yaml"),
            Project.At("astro.config.mjs"),
        })
        {
            if (!File.Exists(file)) continue;
            var lines = File.ReadAllLines(file);
            foreach (var (i, line) in lines.Select((l, i) => (i, l)))
            {
                foreach (var m in markers)
                {
                    if (!line.Contains(m, StringComparison.Ordinal)) continue;
                    var rel = Path.GetRelativePath(Project.Root, file).Replace('\\', '/');
                    found.Add(new Finding(Severity[2], $"配置里还有占位文本「{m}」",
                        line.Trim().Length > 90 ? line.Trim()[..90] + "…" : line.Trim(),
                        rel, i + 1));
                }
            }
        }
    }

    /// <summary>页面里引的外部服务。国内读者打不开的会一并点出来。</summary>
    private static void CheckExternalServices(List<Finding> found)
    {
        var blogRoot = Project.At("src", "content", "blog");
        if (!Directory.Exists(blogRoot)) return;

        var tvCount = 0;
        foreach (var file in Directory.EnumerateFiles(blogRoot, "*.md", SearchOption.AllDirectories))
        {
            string content;
            try { content = File.ReadAllText(file); } catch { continue; }
            if (!content.Contains("tradingview-widget-container", StringComparison.OrdinalIgnoreCase)) continue;
            tvCount++;
            if (tvCount == 1)
                found.Add(new Finding(Severity[1], $"文章里嵌了 {tvCount} 处 TradingView 图表",
                    "widget 走的是「读者 → TradingView」这条链路，CDN 和 Clash 都救不了，" +
                    "国内访问基本加载不出来。要么换国内数据源，要么改成静态截图。",
                    "src/content/blog", 0));
        }
    }

    private static int FindLine(string[] lines, string marker)
    {
        for (var i = 0; i < lines.Length; i++)
            if (lines[i].Contains(marker, StringComparison.OrdinalIgnoreCase)) return i + 1;
        return 0;
    }
}
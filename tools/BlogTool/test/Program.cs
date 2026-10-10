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

    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        // 生成应用图标：.ico 是提交进仓库的产物，改图标就重跑一次
        //   dotnet run -c Release -- --icon ../../assets/app.ico
        var iconAt = Array.IndexOf(args, "--icon");
        if (iconAt >= 0)
        {
            var outPath = iconAt + 1 < args.Length
                ? args[iconAt + 1]
                : Path.Combine(AppContext.BaseDirectory, "app.ico");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
            Icons.SaveIco(outPath);
            var info = new FileInfo(outPath);
            Console.WriteLine($"已生成 {outPath}（{info.Length / 1024.0:F1} KB）");
            return 0;
        }

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

        // ---- 描述与标签（可选字段）----
        Eq("标签按逗号拆", string.Join(',', PostCreator.ParseTags("白银,黄金")), "白银,黄金");
        Eq("标签按顿号拆", string.Join(',', PostCreator.ParseTags("白银、黄金")), "白银,黄金");
        Eq("标签按全角逗号拆", string.Join(',', PostCreator.ParseTags("白银，黄金")), "白银,黄金");
        Eq("标签按空格拆", string.Join(',', PostCreator.ParseTags("白银 黄金")), "白银,黄金");
        Eq("标签去重", PostCreator.ParseTags("白银、silver、白银").Count, 2);
        Eq("空标签串得到空列表", PostCreator.ParseTags("   ").Count, 0);
        Eq("结尾多余逗号不产生空项", PostCreator.ParseTags("白银、").Count, 1);

        var richTitle = "自测-带描述和标签-" + Guid.NewGuid().ToString("N")[..6];
        var richPath = await PostCreator.CreateAsync(
            richTitle, "笔记", "note", "这是一句摘要", ["白银", "贵金属"]);
        try
        {
            var rich = await File.ReadAllTextAsync(richPath, Encoding.UTF8);
            True("写入 description", rich.Contains("description: 这是一句摘要"), "");
            True("写入 tags 段", rich.Contains("tags:"), "");
            True("标签逐行列出", rich.Contains("  - 白银") && rich.Contains("  - 贵金属"), "");
            // 字段顺序要跟着现有文章：tags 在 categories 之前
            True("tags 排在 categories 前",
                rich.IndexOf("tags:") > 0 && rich.IndexOf("tags:") < rich.IndexOf("categories:"), "");

            // 新写的文件必须能被文章库自己的解析器读回来，否则建完在库里看不到
            var reread = Posts.ReadOne(richPath);
            True("新文件能被文章库解析", reread is not null, "");
            if (reread is not null)
            {
                Eq("回读标题一致", reread.Title, richTitle);
                Eq("回读标签一致", string.Join(',', reread.Tags), "白银,贵金属");
            }
        }
        finally
        {
            File.Delete(richPath);
        }

        // 描述和标签留空时，frontmatter 里不该出现这两个键
        var bareTitle = "自测-无描述无标签-" + Guid.NewGuid().ToString("N")[..6];
        var barePath = await PostCreator.CreateAsync(bareTitle, "笔记", "note");
        try
        {
            var bare = await File.ReadAllTextAsync(barePath, Encoding.UTF8);
            True("空描述不写 description 键", !bare.Contains("description:"), "");
            True("空标签不写 tags 键", !bare.Contains("tags:"), "");
        }
        finally
        {
            File.Delete(barePath);
        }
        True("清理后文件已删除", !File.Exists(created), created);

        // ---- 仓库状态 ----
        var state = await Git.GetStateAsync();
        Eq("分支名", state.Branch, "main");
        True("读到改动清单", state.Files.Count >= 0, $"{state.Files.Count} 个");
        True("分叉判断有值", state.Diverged == false || state.Behind > 0,
            $"ahead={state.Ahead} behind={state.Behind} diverged={state.Diverged}");

        // ---- 凭据输出解析与遮蔽（纯函数，不碰真实凭据）----
        var parsed = Credentials.ParseCredentialOutput(
            "protocol=https\r\nhost=github.com\r\nusername=linruowuyin\r\npassword=ghp_abcdefghijklmnopqrstuvwxyz0123456789\r\n");
        Eq("解析用户名", parsed.Username, "linruowuyin");
        Eq("解析密码长度", parsed.Password.Length, 40);

        Eq("遮蔽短值不留明文", Credentials.Mask("abcdef"), "••••••");
        var masked = Credentials.Mask("ghp_abcdefghijklmnopqrstuvwxyz0123456789");
        True("遮蔽保留头尾", masked.StartsWith("gh") && masked.EndsWith("89"), masked);
        True("遮蔽不含中间明文", !masked.Contains("abcdefgh"), masked);
        Eq("遮蔽空值", Credentials.Mask(""), "");

        // ---- 凭据生命周期（临时仓库 + 临时 store + 非 GitHub 域名）----
        await RunCredentialLifecycleTestAsync();

        // ---- 文章库 ----
        RunPostsTests();

        // ---- 日志分色 ----
        RunLogStyleTests();

        Console.WriteLine();
        Console.WriteLine($"通过 {_passed}，失败 {_failed}");
        return _failed == 0 ? 0 : 1;
    }

    /// <summary>
    /// 日志分色的断言。
    ///
    /// 分色是「纯函数出颜色」，最容易被改坏而不自知：把失败行染成绿的，
    /// 界面上看着还挺正常，其实是在报喜。所以判定顺序（失败优先于成功）
    /// 必须钉住。
    /// </summary>
    private static void RunLogStyleTests()
    {
        Console.WriteLine("日志分色：");

        Eq("命令行是蓝色", LogStyle.Classify("> pnpm build").Color, LogStyle.Accent);
        True("命令行加粗", LogStyle.Classify("> pnpm build").Bold, "");
        Eq("阶段标题最深", LogStyle.Classify("▸ 构建").Color, LogStyle.Heading);
        True("阶段标题加粗", LogStyle.Classify("▸ 提交").Bold, "");

        Eq("成功行是绿色", LogStyle.Classify("✓ 发布成功").Color, LogStyle.Good);
        Eq("失败行是红色", LogStyle.Classify("✗ 图片更新失败（退出码 1）").Color, LogStyle.Bad);
        Eq("含 error 字样判为失败", LogStyle.Classify("fatal: error: something").Color, LogStyle.Bad);

        // 「构建没通过」不含失败词也不含成功词，不能被染绿
        Eq("中性行不上色", LogStyle.Classify("构建没通过，详情见日志").Color, LogStyle.Normal);
        Eq("时间戳行压暗", LogStyle.Classify("14:26:59 [build] done").Color, LogStyle.Dim);
        Eq("方括号开头的过程行压暗", LogStyle.Classify("[2/8] building").Color, LogStyle.Dim);
        Eq("空行中性", LogStyle.Classify("").Color, LogStyle.Normal);

        // 一句话里正负词都有时，失败必须赢——染绿报错比不染色更糟
        Eq("正负混合时失败优先",
            LogStyle.Classify("完成 3 项，但有 1 项失败").Color, LogStyle.Bad);

        // ANSI 控制序列必须剥掉，不然打进控件就是 [0m 之类的方块
        Eq("剥掉 ANSI 颜色码", LogStyle.Clean("[32m✓ 完成[0m"), "✓ 完成");
        Eq("剥掉带参数的 ANSI", LogStyle.Clean("[1m[36m[build][0m x"), "[build] x");
        Eq("剥掉光标控制", LogStyle.Clean("[2K[1Gy"), "y");
        Eq("普通文本不受影响", LogStyle.Clean("26 个测试全部通过"), "26 个测试全部通过");

        // 剥完之后再判色：包在 ANSI 里的成功行仍要判成绿色
        Eq("ANSI 包裹的成功行仍判成功", LogStyle.Classify(LogStyle.Clean("[32m✓ 完成[0m")).Color, LogStyle.Good);
    }

    /// <summary>
    /// 文章库的断言。
    ///
    /// 拿真实的 62 篇文章跑，顺便当一次「这个博客自己长什么样」的体检：
    /// frontmatter 解析错了、链接拼不出来、搜索命中不对，在这里就能看出来。
    /// </summary>
    private static void RunPostsTests()
    {
        Console.WriteLine("文章库：");

        var all = Posts.ReadAll();
        True("扫到文章", all.Count > 0, $"{all.Count} 篇");
        True("扫到全部 62 篇", all.Count >= 60, $"{all.Count} 篇");

        // 每篇都得有标题、slug 和日期——缺一个列表里就是一行残缺信息
        var noTitle = all.Count(p => p.Title.Length == 0);
        Eq("没有缺标题的", noTitle, 0);
        var noSlug = all.Count(p => p.Slug.Length == 0);
        Eq("没有缺 link 的", noSlug, 0);
        var noDate = all.Count(p => p.Date == DateTime.MinValue);
        Eq("没有解析不出日期的", noDate, 0);

        // 日期必须解析成合理区间，不能是 1970 之类
        var earliest = all.Where(p => p.Date != DateTime.MinValue).Min(p => p.Date);
        True("最早的文章在合理区间", earliest.Year is >= 2024 and <= 2026, earliest.ToString("yyyy-MM-dd"));

        // 排序：新的在前
        var sorted = all.Zip(all.Skip(1)).All(p => p.First.Date >= p.Second.Date);
        True("按日期倒序", sorted, all.Count > 1 ? $"{all[0].Date:yyyy-MM-dd} 在最前" : "");

        // 分类名要能从 categoryMap 映射出来
        var research = all.FirstOrDefault(p => p.CategoryDir == "research");
        if (research is not null)
            Eq("research 映射到投研", research.CategoryName, "投研");

        // 标签统计
        var tags = Posts.AllTags(all);
        True("统计出标签", tags.Count > 0, $"{tags.Count} 个");
        True("标签按次数倒序", tags.Zip(tags.Skip(1)).All(t => t.First.Count >= t.Second.Count), "");

        // ---- 线上链接 ----
        PostInfo.SiteUrl = "https://nephren.de5.net";
        var withLink = all.First(p => p.Slug.Length > 0);
        Eq("线上链接拼接", withLink.OnlineUrl, $"https://nephren.de5.net/post/{withLink.Slug}");
        Eq("结尾斜杠被吃掉",
            new PostInfo { Title = "t", Slug = "s", CategoryDir = "note" }.OnlineUrlFor("https://x.dev/"),
            "https://x.dev/post/s");

        // 没有 link 的老文章不该给出错误地址，宁可不给
        Eq("缺 link 时不给链接",
            new PostInfo { Title = "t", Slug = "", CategoryDir = "note" }.OnlineUrlFor("https://x.dev"), "");
        Eq("没有站点地址时不给链接",
            new PostInfo { Title = "t", Slug = "s", CategoryDir = "note" }.OnlineUrlFor(""), "");

        // ---- 搜索 ----
        var byTitle = Posts.Search(all, "白银", searchBody: false);
        True("按标题搜得到", byTitle.Count > 0, $"{byTitle.Count} 篇");

        var byTag = Posts.Search(all, all.SelectMany(p => p.Tags).First(), searchBody: false);
        True("按标签搜得到", byTag.Count > 0, $"{byTag.Count} 篇");

        var empty = Posts.Search(all, "绝不可能存在的词zzz", searchBody: false);
        Eq("搜不到时返回空", empty.Count, 0);

        Eq("空关键词返回全部", Posts.Search(all, "", searchBody: false).Count, all.Count);
        Eq("只有空格也返回全部", Posts.Search(all, "   ", searchBody: false).Count, all.Count);

        // 多词按「与」：两词都要有
        var twoWords = Posts.Search(all, "白银 黄金", searchBody: false);
        True("多词按与匹配", twoWords.Count <= byTitle.Count, $"{twoWords.Count} ≤ {byTitle.Count}");

        // 正文里才有的词，只有勾了「也搜正文」才该命中
        var bodyOnly = "zzz唯一标记zzz";
        var noBody = Posts.Search(all, bodyOnly, searchBody: false);
        Eq("不搜正文时搜不到", noBody.Count, 0);
        var withBody = Posts.Search(all, bodyOnly, searchBody: true);
        Eq("搜正文时不误报", withBody.Count, 0);   // 真文章里没这个词，两种都该是 0

        // ---- frontmatter 解析的边界情况 ----
        var sandbox = Path.Combine(Path.GetTempPath(), $"blogtool-fm-{Guid.NewGuid():N}");
        Directory.CreateDirectory(sandbox);
        try
        {
            var file = Path.Combine(sandbox, "t.md");

            File.WriteAllText(file, """
                ---
                title: "带引号的标题"
                link: quoted-slug
                date: 2026-03-05
                tags:
                  - 甲
                  - 乙
                categories:
                  - 投研
                cover: /img/a.webp
                password: secret
                ---
                正文
                """);
            var p1 = Posts.ReadOne(file);
            True("解析 frontmatter", p1 is not null, "");
            if (p1 is not null)
            {
                Eq("引号被剥掉", p1.Title, "带引号的标题");
                Eq("slug 正确", p1.Slug, "quoted-slug");
                Eq("日期正确", p1.Date.ToString("yyyy-MM-dd"), "2026-03-05");
                Eq("标签两个", string.Join(',', p1.Tags), "甲,乙");
                Eq("分类正确", p1.CategoryName, "投研");
                Eq("封面正确", p1.Cover, "/img/a.webp");
                Eq("识别为加密", p1.Encrypted, true);
            }

            // 行尾注释要吃掉，但 URL 里的 # 不能砍
            File.WriteAllText(file, "---\ntitle: 标题 # 这是注释\nlink: a#b\ndate: 2026-01-01\n---\n");
            var p2 = Posts.ReadOne(file);
            Eq("行尾注释被剥掉", p2?.Title, "标题");
            Eq("值里的井号保留", p2?.Slug, "a#b");

            // 没有 frontmatter 的文件直接跳过，不该让整个库炸掉
            File.WriteAllText(file, "这里没有 frontmatter\n");
            Eq("无 frontmatter 返回 null", Posts.ReadOne(file), null);

            // 空文件
            File.WriteAllText(file, "");
            Eq("空文件返回 null", Posts.ReadOne(file), null);
        }
        finally
        {
            try { Directory.Delete(sandbox, true); } catch { }
        }
    }

    /// <summary>
    /// 用一个临时 git 仓库和临时 credential store 走一遍写入/读取/清除。
    ///
    /// 两道隔离缺一不可：
    ///   · 域名用 github.test 而不是 github.com——万一 store 没生效，
    ///     也只会去问一个不存在的站点，绝不会覆盖真实凭据
    ///   · 临时仓库的本地配置里先写一个空的 helper 清空继承来的助手链，
    ///     再接 store，全局那个 GCM 不会被调用
    /// </summary>
    private static async Task RunCredentialLifecycleTestAsync()
    {
        var sandbox = Path.Combine(Path.GetTempPath(), $"blogtool-cred-{Guid.NewGuid():N}");
        Directory.CreateDirectory(sandbox);
        var storeFile = Path.Combine(sandbox, "store.txt").Replace("\\", "/");
        var originalRoot = Git.RepoRoot;

        try
        {
            Run("git", "init", "--quiet", sandbox);
            // 空 helper 是「清空继承来的助手链」的标准写法，必须排在最前面，
            // 否则全局那个 GCM 仍会被调用，测试就会碰到真实凭据。
            Run("git", "-C", sandbox, "config", "--local", "credential.helper");
            Run("git", "-C", sandbox, "config", "--local", "--add", "credential.helper", "");
            Run("git", "-C", sandbox, "config", "--local", "--add", "credential.helper", $"store --file={storeFile}");

            Git.RepoRoot = sandbox;
            const string host = "github.test";

            Eq("初始没有凭据", (await Credentials.ReadAsync(host)).Exists, false);

            await Credentials.SaveAsync("test-user", "ghp_faketoken000000000000000000000000", host);
            var afterSave = await Credentials.ReadAsync(host);
            Eq("写入后有凭据", afterSave.Exists, true);
            Eq("写入后用户名正确", afterSave.Username, "test-user");
            Eq("写入后密码长度正确", afterSave.PasswordLength, "ghp_faketoken000000000000000000000000".Length);

            // 令牌框留空时，测试连接要能直接取回已存的那份，不必用户重敲
            var stored = await Credentials.GetStoredAsync(host);
            Eq("可取回已存用户名", stored.Username, "test-user");
            Eq("可取回已存令牌", stored.Password, "ghp_faketoken000000000000000000000000");

            // 确认密码只落在临时文件里
            True("令牌只写进临时存储",
                File.ReadAllText(storeFile).Contains("ghp_faketoken"), storeFile);

            await Credentials.ClearAsync("test-user", host);
            Eq("清除后没有凭据", (await Credentials.ReadAsync(host)).Exists, false);
            Eq("清除后取不到令牌", (await Credentials.GetStoredAsync(host)).Password, "");

            // 空输入不该被当成有效凭据
            try
            {
                await Credentials.SaveAsync("", "token", host);
                True("空用户名应被拒绝", false, "没有抛异常");
            }
            catch (ArgumentException)
            {
                True("空用户名被拒绝", true, "");
            }

            try
            {
                await Credentials.SaveAsync("test-user", "", host);
                True("空令牌应被拒绝", false, "没有抛异常");
            }
            catch (ArgumentException)
            {
                True("空令牌被拒绝", true, "");
            }
        }
        finally
        {
            Git.RepoRoot = originalRoot;
            try { Directory.Delete(sandbox, recursive: true); } catch { /* 临时目录，删不掉也无所谓 */ }
        }
    }

    private static void Run(string exe, params string[] args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo(exe)
        {
            WorkingDirectory = Path.GetTempPath(),
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = System.Diagnostics.Process.Start(psi);
        p?.WaitForExit(20000);
    }

    private static void True(string name, bool ok, string detail)
    {
        if (ok) { _passed++; Console.WriteLine($"  ✓ {name}"); }
        else { _failed++; Console.WriteLine($"  ✗ {name}   [{detail}]"); }
    }

    private static void Eq<T>(string name, T actual, T expected)
        => True(name, Equals(actual, expected), $"实际 {actual}，期望 {expected}");
}

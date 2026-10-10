using BlogPublisher;

namespace BlogPublisher.ConsoleApp;

/// <summary>
/// 控制台版发布工具。
///
/// 双击后进入循环：列改动 → 勾选 → 填信息 → 检查 → 推送。
/// 全程键盘操作，Esc 可在任何一步退回。
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        Git.RepoRoot = ResolveRepoRoot();

        Console.OutputEncoding = System.Text.Encoding.UTF8;
        try { Console.InputEncoding = System.Text.Encoding.UTF8; } catch { /* 控制台可能不支持，忽略 */ }

        var sub = args.FirstOrDefault()?.ToLowerInvariant();

        try
        {
            switch (sub)
            {
                case "status": await StatusAsync(); return 0;
                case "rollback": await RollbackAsync(); return 0;
                case "help" or "--help" or "-h": PrintHelp(); return 0;
                default:
                    await DeployAsync();
                    return 0;
            }
        }
        catch (Exception ex)
        {
            WriteLine();
            Fail($"出错：{ex.Message}");
            WriteLine();
            Console.Write("按回车退出…");
            Console.ReadLine();
            return 1;
        }
    }

    /// <summary>
    /// 定位仓库根目录。优先用 exe 所在位置的上级（部署到项目里时成立），
    /// 其次用当前工作目录。找不到 package.json 就直接报错，
    /// 而不是继续在错误的目录里跑 git。
    /// </summary>
    private static string ResolveRepoRoot()
    {
        var candidates = new[]
        {
            AppContext.BaseDirectory,
            Directory.GetCurrentDirectory(),
        };

        // exe 可能在项目根目录或其任意子目录，逐级向上找带 package.json 的。
        foreach (var start in candidates)
        {
            var dir = new DirectoryInfo(start);
            while (dir is not null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "package.json"))
                    && Directory.Exists(Path.Combine(dir.FullName, ".git")))
                    return dir.FullName;
                dir = dir.Parent;
            }
        }

        Fail($"找不到项目目录（需要同时有 package.json 和 .git）\n当前工作目录：{Directory.GetCurrentDirectory()}");
        Environment.Exit(1);
        return "";
    }

    private static async Task DeployAsync()
    {
        while (true)
        {
            GitState state;
            try
            {
                state = await Git.GetStateAsync();
            }
            catch (Exception ex)
            {
                Fail($"读取仓库状态失败：{ex.Message}");
                return;
            }

            Header(state);

            if (state.Diverged)
            {
                Fail($"本地与远端已分叉（领先 {state.Ahead} / 落后 {state.Behind}），已阻断。\n"
                   + "  git fetch && git rebase origin/main        保留本地提交\n"
                   + "  git fetch && git reset --hard origin/main   丢弃本地");
                Pause();
                return;
            }

            if (state.Files.Count == 0)
            {
                Info(state.Behind > 0
                    ? $"工作区干净，但本地落后远端 {state.Behind} 个提交，请先 git pull"
                    : "工作区干净，没有需要提交的改动");
                Pause();
                return;
            }

            // ---- 选择文件 ----
            var selected = SelectFiles(state.Files);
            if (selected is null) { Info("已取消，未做任何改动。"); Pause(); return; }
            if (selected.Count == 0) { Warn("没有勾选任何文件，返回重选。"); continue; }

            // ---- 提交信息 ----
            var message = AskCommitMessage();
            if (message is null) { Info("已取消，未做任何改动。"); Pause(); return; }
            if (message.Length == 0) { Warn("提交信息不能为空，返回重选。"); continue; }

            // ---- 最终确认 ----
            WriteLine();
            Info($"提交信息：{message}");
            Info($"将提交 {selected.Count} 个文件，随后自动跑构建与单测");
            if (!AskYesNo("确认发布？推送后 Cloudflare 会立即开始构建")) { Info("已取消，未做任何改动。"); Pause(); return; }

            // ---- 执行 ----
            WriteLine();
            Info("正在检查…");
            var result = await Publisher.PublishAsync(selected, message, line => Console.WriteLine(line));

            WriteLine();
            if (result.Ok) Success(result.Message);
            else Fail(result.Message);
            Pause();
            return;
        }
    }

    /// <summary>多选文件。返回 null 表示用户取消。</summary>
    private static List<string>? SelectFiles(IReadOnlyList<ChangedFile> files)
    {
        var selected = new HashSet<int>();
        var cursor = 0;
        var showAll = false;
        var rows = Math.Max(5, Console.WindowHeight - 12);

        while (true)
        {
            Clear();
            Info($"待提交文件（{files.Count} 项，已选 {selected.Count}）");
            Console.WriteLine();

            var start = showAll || files.Count <= rows
                ? 0
                : Math.Clamp(cursor - rows / 2, 0, Math.Max(0, files.Count - rows));
            var end = showAll ? files.Count : Math.Min(files.Count, start + rows);
            if (!showAll && start > 0) Console.WriteLine($"  … 上面还有 {start} 项");

            for (var i = start; i < end; i++)
            {
                var f = files[i];
                var mark = selected.Contains(i) ? "[x]" : "[ ]";
                var arrow = i == cursor ? "❯" : " ";
                var stat = f.Added is null ? "" : $"  {(f.Added > 0 ? "+" + f.Added : "")}{(f.Removed > 0 ? "-" + f.Removed : "")}";
                var line = $" {arrow} {mark} {Pad(f.Label, 6)} {f.Path}{stat}";
                // 长路径截断，避免在窄窗口里换行导致视觉错位。
                var width = Math.Max(40, Console.WindowWidth - 1);
                Console.WriteLine(line.Length > width ? line[..width] + "…" : line);
            }
            if (end < files.Count) Console.WriteLine($"  … 下面还有 {files.Count - end} 项");
            if (!showAll && files.Count > rows) WriteColored($"  按 e 展开全部（共 {files.Count} 项）", ConsoleColor.DarkGray);

            WriteColored("  ↑↓ 移动   空格 勾选   a 全选   n 全不选   回车 确认   Esc 取消", ConsoleColor.DarkGray);

            var key = Console.ReadKey(true);
            switch (key.Key)
            {
                case ConsoleKey.UpArrow: cursor = cursor == 0 ? files.Count - 1 : cursor - 1; break;
                case ConsoleKey.DownArrow: cursor = cursor == files.Count - 1 ? 0 : cursor + 1; break;
                case ConsoleKey.Spacebar:
                    if (!selected.Add(cursor)) selected.Remove(cursor);
                    break;
                case ConsoleKey.A:
                    selected = [.. Enumerable.Range(0, files.Count)];
                    break;
                case ConsoleKey.N:
                    selected.Clear();
                    break;
                case ConsoleKey.E:
                    showAll = !showAll;
                    break;
                case ConsoleKey.Enter:
                    return [.. selected.OrderBy(i => i).Select(i => files[i].Path)];
                case ConsoleKey.Escape:
                    return null;
            }
        }
    }

    /// <summary>整行宽度对齐：中文按 2 列计。</summary>
    private static string Pad(string text, int width)
    {
        var len = DisplayWidth(text);
        return len >= width ? text : text + new string(' ', width - len);
    }

    private static int DisplayWidth(string s)
    {
        var w = 0;
        foreach (var ch in s) w += ch > 0x1100 ? 2 : 1;
        return w;
    }

    private static string? AskCommitMessage()
    {
        while (true)
        {
            WriteLine();
            Info("请输入提交信息（直接回车取消）");
            Console.Write("> ");
            var input = Console.ReadLine();
            if (input is null) return null;   // Ctrl+Z / EOF
            if (input.Trim().Length == 0) return input.Trim();
            return input.Trim();
        }
    }

    private static bool AskYesNo(string question)
    {
        Console.Write($"{question} [y/N] ");
        var key = Console.ReadKey(true);
        return key.Key == ConsoleKey.Y;
    }

    private static async Task StatusAsync()
    {
        var state = await Git.GetStateAsync();
        WriteLine();
        Info($"分支：{state.Branch}");
        if (state.UpstreamMissing) Info("上游追踪：未配置（首次推送时会自动建立）");
        else Info($"领先远端：{state.Ahead}  落后远端：{state.Behind}");
        Info($"改动文件：{state.Files.Count} 项");
        foreach (var f in state.Files)
            Console.WriteLine($"  {Pad(f.Label, 6)} {f.Path}");

        var commits = await Git.RecentCommitsAsync(8);
        if (commits.Count > 0)
        {
            WriteLine();
            Info("最近提交：");
            foreach (var c in commits) Console.WriteLine($"  {c.Short}  {c.Date}  {c.Subject}");
        }
        WriteLine();
    }

    private static async Task RollbackAsync()
    {
        var state = await Git.GetStateAsync();
        var commits = await Git.RecentCommitsAsync(8);

        WriteLine();
        Info("最近提交：");
        for (var i = 0; i < commits.Count; i++)
        {
            var marker = i == 0 ? "  ← 当前线上" : i == 1 ? "  ← 将回滚到这里" : "";
            Console.WriteLine($"  {commits[i].Short}  {commits[i].Date}  {commits[i].Subject}{marker}");
        }

        if (commits.Count < 2)
        {
            WriteLine();
            Warn("提交历史不足两个，无法回滚。");
            return;
        }

        if (state.Files.Count > 0)
        {
            WriteLine();
            Warn($"工作区还有 {state.Files.Count} 项未提交改动，回滚会丢失它们，已阻断。");
            Info("先发布这些改动，或用 git stash 暂存。");
            return;
        }

        var target = commits[1];
        WriteLine();
        Info($"即将把线上从 {commits[0].Short} 回滚到 {target.Short}（{target.Subject}）");
        Console.Write("输入 yes 确认执行：");
        var answer = Console.ReadLine();
        if (!string.Equals(answer?.Trim(), "yes", StringComparison.OrdinalIgnoreCase))
        {
            WriteLine();
            Info("已取消，未做任何改动。");
            return;
        }

        var result = await Publisher.RollbackAsync(target.Short, line => Console.WriteLine(line));
        WriteLine();
        if (result.Ok) Success(result.Message);
        else Fail(result.Message);
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""

        博客发布工具

        用法：
          博客发布.exe            发布改动（勾选文件 → 写提交信息 → 检查 → 推送）
          博客发布.exe status     查看分支、改动与最近提交
          博客发布.exe rollback   回滚线上到上一个提交

        快捷键（选文件时）：
          ↑↓ 移动   空格 勾选   a 全选   n 全不选   e 展开/收起
          回车 确认   Esc 取消

        安全约束：
          · 只提交勾选的文件
          · 推送前强制跑构建与单测，不通过会阻断
          · 回滚用 force-with-lease，他人提交不会被覆盖
        """);
    }

    // ---- 输出helpers ----
    private static void Header(GitState state)
    {
        Clear();
        Console.WriteLine("╔══════════════════════════════════════════════════╗");
        Console.WriteLine("║           博客发布                              ║");
        Console.WriteLine("╚══════════════════════════════════════════════════╝");
        var remote = state.UpstreamMissing
            ? "上游未配置"
            : state.Diverged ? "已分叉" : $"领先 {state.Ahead} / 落后 {state.Behind}";
        Console.WriteLine($" 分支 {state.Branch}   {remote}");
    }

    private static void Clear()
    {
        try { Console.Clear(); } catch { /* 重定向输出时不支持，忽略 */ }
    }

    private static void WriteLine() => Console.WriteLine();

    private static void Info(string text) => WriteColored(text, ConsoleColor.Cyan);
    private static void Dim(string text) => WriteColored(text, ConsoleColor.DarkGray);
    private static void Warn(string text) => WriteColored(text, ConsoleColor.Yellow);
    private static void Success(string text) => WriteColored(text, ConsoleColor.Green);
    private static void Fail(string text) => WriteColored(text, ConsoleColor.Red);

    private static void WriteColored(string text, ConsoleColor color)
    {
        var original = Console.ForegroundColor;
        try
        {
            Console.ForegroundColor = color;
            Console.WriteLine(text);
        }
        finally
        {
            Console.ForegroundColor = original;
        }
    }

    private static void Pause()
    {
        WriteLine();
        Console.Write("按回车退出…");
        Console.ReadLine();
    }
}

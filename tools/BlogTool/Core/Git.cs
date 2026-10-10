using System.Diagnostics;
using System.Text;

namespace BlogTool;

/// <summary>
/// 一个被改动的文件。
/// 覆写 ToString 是必须的：CheckedListBox 用它作为显示文本，
/// 而 record 的默认 ToString 会把 Status/Path/Added 全部字段打印出来。
/// </summary>
public sealed record ChangedFile(string Status, string Label, string Path, int? Added, int? Removed)
{
    public override string ToString()
    {
        var stat = Added is null ? "" : $"  +{Added} -{Removed}";
        return $"{Label.PadRight(6)} {Path}{stat}";
    }
}

/// <summary>最近一条提交。</summary>
public sealed record CommitInfo(string Short, string Subject, string Date);

/// <summary>仓库当前状态。</summary>
public sealed class GitState
{
    public string Branch { get; init; } = "";
    public int Ahead { get; init; }
    public int Behind { get; init; }
    public bool UpstreamMissing { get; init; }
    public bool Diverged { get; init; }
    public IReadOnlyList<ChangedFile> Files { get; init; } = [];
}

/// <summary>
/// git 操作层。控制台版与图形版共用。
///
/// 与 TypeScript 版（scripts/deploy/git-ops.ts）保持同一套语义：
/// 只提交显式给出的文件、推送前强制检查、回滚用 force-with-lease。
/// </summary>
public static class Git
{
    public static string RepoRoot { get; set; } = "";

    /// <summary>
    /// 执行一条 git 命令。始终带 -c core.quotepath=false，
    /// 否则中文路径会显示成八进制转义，用户认不出哪个文件被改了。
    /// </summary>
    private static async Task<(int Code, string Output)> GitAsync(string args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = RepoRoot,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        // 逐个加参数，不拼命令串：避免引号与空格的二次解析问题。
        foreach (var arg in Split($"-c core.quotepath=false {args}"))
            psi.ArgumentList.Add(arg);

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("无法启动 git");
        var stdout = await proc.StandardOutput.ReadToEndAsync();
        var stderr = await proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();
        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"git {args} 失败：{stderr.Trim()}");
        return (proc.ExitCode, stdout);
    }

    /// <summary>
    /// 把命令串切成参数数组。只处理引号内的空格 —— 输入全部来自本程序，
    /// 不需要处理更复杂的转义。
    /// </summary>
    private static List<string> Split(string commandLine)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;
        foreach (var ch in commandLine)
        {
            if (ch == '"') { inQuotes = !inQuotes; continue; }
            if (char.IsWhiteSpace(ch) && !inQuotes)
            {
                if (current.Length > 0) { result.Add(current.ToString()); current.Clear(); }
                continue;
            }
            current.Append(ch);
        }
        if (current.Length > 0) result.Add(current.ToString());
        return result;
    }

    /// <summary>把两字母状态码翻成人话。</summary>
    public static string DescribeStatus(string status)
    {
        if (status == "??") return "未跟踪";
        foreach (var c in status)
        {
            switch (c)
            {
                case 'M': return "修改";
                case 'A': return "新增";
                case 'D': return "删除";
                case 'R': return "重命名";
                case 'C': return "复制";
                case 'U': return "冲突";
            }
        }
        return status;
    }

    public static async Task<GitState> GetStateAsync()
    {
        var (_, branchOut) = await GitAsync("rev-parse --abbrev-ref HEAD");
        var branch = branchOut.Trim();

        var (_, porcelain) = await GitAsync("status --porcelain");
        var parsed = ParsePorcelain(porcelain);

        // 已跟踪文件补增删行数。
        var statMap = new Dictionary<string, (int A, int R)>();
        try
        {
            var (_, numstat) = await GitAsync("diff HEAD --numstat");
            foreach (var line in numstat.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Split('\t');
                if (parts.Length < 3) continue;
                statMap[parts[2]] = (
                    parts[0] == "-" ? 0 : int.TryParse(parts[0], out var a) ? a : 0,
                    parts[1] == "-" ? 0 : int.TryParse(parts[1], out var r) ? r : 0);
            }
        }
        catch (InvalidOperationException)
        {
            // 没有任何已跟踪改动时 diff 会返回非零，忽略即可。
        }

        var files = new List<ChangedFile>();
        foreach (var (status, path) in parsed)
        {
            int? added = null, removed = null;
            if (status != "??" && statMap.TryGetValue(path, out var st))
            {
                added = st.A;
                removed = st.R;
            }
            files.Add(new ChangedFile(status, DescribeStatus(status), path, added, removed));
        }

        // 上游追踪配置可能在 rebase/reset 后丢失，这时读 @{upstream} 会
        // 静默得到 0/0，把「已分叉」伪装成「一致」。所以一律对着
        // origin/<branch> 算，并用 merge-base 显式判断分叉。
        var remote = (await ConfigAsync($"branch.{branch}.remote")) ?? "origin";
        var upstreamMissing = (await ConfigAsync($"branch.{branch}.remote")) is null;
        var remoteRef = $"{remote}/{branch}";

        var ahead = 0;
        var behind = 0;
        try
        {
            var (_, counts) = await GitAsync($"rev-list --left-right --count HEAD...{remoteRef}");
            var parts = counts.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2)
            {
                int.TryParse(parts[0], out ahead);
                int.TryParse(parts[1], out behind);
            }
        }
        catch (InvalidOperationException)
        {
            // 远端还没有这个分支（首次推送），保持 0。
        }

        var diverged = false;
        if (behind > 0)
        {
            try
            {
                var (_, mergeBase) = await GitAsync($"merge-base HEAD {remoteRef}");
                var (_, head) = await GitAsync("rev-parse HEAD");
                var mb = mergeBase.Trim();
                diverged = mb.Length > 0 && mb != head.Trim();
            }
            catch (InvalidOperationException)
            {
                diverged = false;
            }
        }

        return new GitState
        {
            Branch = branch,
            Ahead = ahead,
            Behind = behind,
            UpstreamMissing = upstreamMissing,
            Diverged = diverged,
            Files = files,
        };
    }

    private static async Task<string?> ConfigAsync(string key)
    {
        try
        {
            var (_, outp) = await GitAsync($"config --get {key}");
            var value = outp.Trim();
            return value.Length == 0 ? null : value;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static List<(string Status, string Path)> ParsePorcelain(string raw)
    {
        var files = new List<(string, string)>();
        foreach (var line in raw.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.StartsWith("##")) continue;
            if (line.Length < 4) continue;
            var status = line[..2];
            var path = line[3..].Trim();
            if (path.Length == 0) continue;

            if (status.Contains('R') || status.Contains('C'))
            {
                var arrow = path.LastIndexOf(" -> ", StringComparison.Ordinal);
                if (arrow > 0) path = path[(arrow + 4)..];
            }
            if (path.StartsWith('"') && path.EndsWith('"')) path = path[1..^1];
            files.Add((status, path));
        }
        return files;
    }

    public static Task StageAsync(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return Task.CompletedTask;
        // 用 -- 分隔选项与路径：路径以 - 开头时不会被当成参数。
        return GitAsync($"add -- {string.Join(' ', paths.Select(Quote))}");
    }

    public static Task CommitAsync(string message) => GitAsync($"commit -F \"{WriteMessageFile(message)}\"");

    /// <summary>
    /// 把提交信息写成临时文件供 git commit -F 读取。
    ///
    /// 不用 -m：多行信息里含引号会被 shell 重新解析，中文在某些代码页下
    /// 还会被按 GBK 处理。写文件是最稳的路径。必须无 BOM，
    /// 否则 git 会把 BOM 当成提交信息的第一个字符。
    /// </summary>
    private static string WriteMessageFile(string message)
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"blog-commit-{Guid.NewGuid():N}.txt");
        File.WriteAllText(path, message, new UTF8Encoding(false));
        return path;
    }

    /// <summary>
    /// 推送到远端。不用裸 git push：它依赖 branch.&lt;name&gt;.merge 配置，
    /// 而该配置在 rebase/reset 后可能丢失，届时报 "has no upstream branch"。
    /// </summary>
    public static async Task PushAsync(bool force = false)
    {
        var (_, branchOut) = await GitAsync("rev-parse --abbrev-ref HEAD");
        var branch = branchOut.Trim();
        var remote = (await ConfigAsync($"branch.{branch}.remote")) ?? "origin";
        var hasUpstream = (await ConfigAsync($"branch.{branch}.merge")) is not null;

        var args = new List<string> { "push" };
        if (force) args.Add("--force-with-lease");
        if (!hasUpstream) args.AddRange(["--set-upstream", remote, branch]);
        else args.AddRange([remote, branch]);
        await GitAsync(string.Join(' ', args));
    }

    public static async Task<IReadOnlyList<CommitInfo>> RecentCommitsAsync(int n)
    {
        // 不用 --date=format:%Y-%m-%d %H:%M：其中的空格会把 format 串
        // 截断，git 收到残缺 format 就报 invalid object name。
        var (_, raw) = await GitAsync($"log -{n} --format=%h%x09%ad%x09%s --date=format-local:%Y-%m-%dT%H:%M");
        var list = new List<CommitInfo>();
        foreach (var line in raw.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split('\t');
            if (parts.Length < 3) continue;
            list.Add(new CommitInfo(
                parts[0][..Math.Min(7, parts[0].Length)],
                string.Join('\t', parts[2..]),
                FormatDate(parts[1])));
        }
        return list;
    }

    public static async Task<string> HeadShaAsync()
    {
        var (_, outp) = await GitAsync("rev-parse HEAD");
        return outp.Trim();
    }

    /// <summary>
    /// 回滚：重置到指定提交后强推。lease 绑定回滚前的远端 sha，
    /// 若期间有人推了新提交会拒绝，而不是把别人的改动无声覆盖。
    /// </summary>
    public static async Task RollbackAsync(string target, string expectedHead)
    {
        var (_, branchOut) = await GitAsync("rev-parse --abbrev-ref HEAD");
        var branch = branchOut.Trim();
        var remote = (await ConfigAsync($"branch.{branch}.remote")) ?? "origin";
        await GitAsync($"reset --hard {target}");
        await GitAsync($"push {remote} {branch} --force-with-lease={expectedHead}:{remote}/{branch}");
    }

    private static string Quote(string value) => value.Contains(' ') ? $"\"{value}\"" : value;

    /// <summary>把 2026-10-10T14:32 压成 10-10 14:32，省地方。</summary>
    private static string FormatDate(string iso) => iso.Length < 16 ? iso : $"{iso[5..10]} {iso[11..16]}";
}

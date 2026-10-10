using System.Diagnostics;
using System.Text;

namespace BlogPublisher;

/// <summary>临时文件辅助。</summary>
public static class TempFile
{
    /// <summary>
    /// 把提交信息写成临时文件供 git commit -F 读取。
    ///
    /// 不用 -m：多行信息里含引号会被 shell 重新解析，中文在某些代码页下
    /// 还会被按 GBK 处理。写文件是最稳的路径。显式无 BOM 的 UTF8，
    /// 否则 git 会把 BOM 当成提交信息的第一个字符。
    /// </summary>
    public static string WriteMessage(string message)
    {
        var path = Path.Combine(Path.GetTempPath(), $"blog-commit-{Guid.NewGuid():N}.txt");
        File.WriteAllText(path, message, new UTF8Encoding(false));
        return path;
    }
}

/// <summary>任务结果。</summary>
public sealed class CheckResult
{
    public bool Ok { get; init; }
    public string Message { get; init; } = "";
}

/// <summary>
/// 构建、单测、发布、回滚流程。控制台版与图形版共用，
/// 保证两个 exe 与 pnpm deploy 的行为完全一致。
/// </summary>
public static class Publisher
{
    /// <summary>跑一条命令，把输出逐行回报。</summary>
    private static async Task<int> RunAndReportAsync(
        string label, string command, string args, Action<string> onLine)
    {
        onLine($"[{label}] $ {command} {args}");

        // Windows 上 pnpm 是 .cmd 批处理，UseShellExecute=false 直接启动会
        // 失败（EINVAL），必须经由 shell。
        var psi = new ProcessStartInfo
        {
            FileName = command,
            Arguments = args,
            WorkingDirectory = Git.RepoRoot,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException($"无法启动 {command}");

        // stdout 与 stderr 并行读，否则任一写满管道缓冲就会互相死锁。
        var stdoutTask = PumpAsync(proc.StandardOutput, onLine);
        var stderrTask = PumpAsync(proc.StandardError, onLine);
        await Task.WhenAll(stdoutTask, stderrTask);
        await proc.WaitForExitAsync();
        return proc.ExitCode;
    }

    private static async Task PumpAsync(StreamReader reader, Action<string> onLine)
    {
        while (await reader.ReadLineAsync() is { } line) onLine(line);
    }

    /// <summary>构建 + 单测。任一失败即返回失败，绝不继续推送。</summary>
    public static async Task<CheckResult> RunChecksAsync(Action<string> onLine)
    {
        var steps = new (string Label, string Args)[]
        {
            ("构建", "build"),
            ("单测", "test:index"),
        };

        foreach (var (label, args) in steps)
        {
            var code = await RunAndReportAsync(label, "pnpm", args, onLine);
            if (code != 0)
                return new CheckResult { Ok = false, Message = $"{label}未通过（退出码 {code}），已阻断推送。" };
        }
        return new CheckResult { Ok = true, Message = "构建与单测全部通过" };
    }

    /// <summary>完整发布：检查 → 暂存 → 提交 → 推送。</summary>
    public static async Task<CheckResult> PublishAsync(
        IReadOnlyList<string> files, string message, Action<string> onLine)
    {
        var checks = await RunChecksAsync(onLine);
        if (!checks.Ok) return checks;

        onLine($"[提交] 暂存 {files.Count} 个文件");
        await Git.StageAsync(files);
        onLine($"[提交] {message}");
        await Git.CommitAsync(message);

        onLine("[推送] 推送到 origin/main");
        await Git.PushAsync();

        return new CheckResult
        {
            Ok = true,
            Message = "已推送到 origin/main，Cloudflare 将自动构建，约 1-2 分钟后生效。",
        };
    }

    /// <summary>回滚线上到指定提交。</summary>
    public static async Task<CheckResult> RollbackAsync(string target, Action<string> onLine)
    {
        try
        {
            var before = await Git.HeadShaAsync();
            onLine($"[回滚] HEAD {before[..7]} → {target}");
            await Git.RollbackAsync(target, before);
            return new CheckResult
            {
                Ok = true,
                Message = $"已回滚到 {target[..Math.Min(7, target.Length)]}，Cloudflare 正在重新构建。\n"
                          + $"撤销：git reset --hard {before[..7]} && git push --force-with-lease",
            };
        }
        catch (Exception ex)
        {
            return new CheckResult { Ok = false, Message = $"回滚失败：{ex.Message}" };
        }
    }
}

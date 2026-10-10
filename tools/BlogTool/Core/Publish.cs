using System.Windows.Forms;

namespace BlogTool;

/// <summary>任务结果。</summary>
public sealed class TaskResult
{
    public bool Ok { get; init; }
    public string Message { get; init; } = "";
}

/// <summary>发布与回滚。</summary>
public static class Publish
{
    /// <summary>
    /// 发布：列出改动让用户确认 → 跑构建与单测 → 提交 → 推送。
    ///
    /// 与旧 bat 的区别只在两点：
    ///   1. 不用 git add . 无差别全提交，而是把改动列出来让用户挑
    ///   2. 推送前先跑构建与单测，不通过就停下来
    /// </summary>
    public static async Task<TaskResult> RunAsync(Action<string> log)
    {
        GitState state;
        try
        {
            state = await Git.GetStateAsync();
        }
        catch (Exception ex)
        {
            return Fail($"读不到仓库状态：{ex.Message}");
        }

        if (state.Diverged)
        {
            return Fail(
                $"本地和远端的历史对不上了，已经拦下来。\n\n" +
                $"本地领先 {state.Ahead} 个提交、落后 {state.Behind} 个提交。\n" +
                "二选一处理：\n" +
                "  git fetch && git rebase origin/main        保留本地提交\n" +
                "  git fetch && git reset --hard origin/main   丢弃本地");
        }

        if (state.Files.Count == 0)
        {
            return new TaskResult
            {
                Ok = false,
                Message = state.Behind > 0
                    ? $"工作区是干净的，但本地落后远端 {state.Behind} 个提交，先 git pull 一下。"
                    : "工作区是干净的，没有要发布的东西。",
            };
        }

        log($"分支 {state.Branch}");
        log("本次要提交的改动：");
        foreach (var f in state.Files) log($"  {f.Label}  {f.Path}");
        log("");

        using var dialog = new CommitDialog(state.Files, BuildCommitMessage(state));
        if (dialog.ShowDialog() != DialogResult.OK)
        {
            return new TaskResult { Ok = false, Message = "已取消，什么都没改。" };
        }

        var chosen = dialog.SelectedPaths;
        var message = dialog.CommitMessage;
        log($"确认提交 {chosen.Count} 个文件：{message}");
        log("");

        // ---- 检查 ----
        log("▸ 构建");
        var code = await Project.RunPnpmAsync("build", log);
        if (code != 0)
            return Fail($"构建没通过（退出码 {code}），已经拦下来，没有推送。\n看日志找原因。");

        log("");
        log("▸ 单测");
        code = await Project.RunPnpmAsync("test:index", log);
        if (code != 0)
            return Fail($"单测没通过（退出码 {code}），已经拦下来，没有推送。");

        // ---- 提交 ----
        log("");
        log("▸ 提交");
        try
        {
            // 只提交用户勾中的文件。剩下的改动留在工作区不动，
            // 下次发布还会再列出来，不会被顺手带走。
            await Git.StageAsync([.. chosen]);
            await Git.CommitAsync(message);
            log($"已提交：{message}");
        }
        catch (Exception ex)
        {
            return Fail($"提交失败：{ex.Message}");
        }

        // ---- 推送 ----
        log("");
        log("▸ 推送");
        try
        {
            await Git.PushAsync();
        }
        catch (Exception ex)
        {
            return Fail($"推送失败：{ex.Message}\n\n提交已经在本地了，网络恢复后再推一次就行。");
        }

        return new TaskResult
        {
            Ok = true,
            Message = "发布成功，Cloudflare 正在重新构建，1-2 分钟后线上生效。",
        };
    }

    /// <summary>回滚线上到上一个提交。</summary>
    public static async Task<TaskResult> RollbackAsync(string target, Action<string> log)
    {
        try
        {
            var before = await Git.HeadShaAsync();
            log($"HEAD {before[..7]} → {target}");
            await Git.RollbackAsync(target, before);
            return new TaskResult
            {
                Ok = true,
                Message = $"已回滚到 {target[..Math.Min(7, target.Length)]}，Cloudflare 正在重新构建。\n\n"
                          + $"要撤销这次回滚：\ngit reset --hard {before[..7]} && git push --force-with-lease",
            };
        }
        catch (Exception ex)
        {
            return new TaskResult { Ok = false, Message = $"回滚失败：{ex.Message}" };
        }
    }

    /// <summary>按改动类型生成提交信息，省得每次手打。</summary>
    private static string BuildCommitMessage(GitState state)
    {
        var labels = state.Files.Select(f => f.Label).ToHashSet();
        var scope = labels.Contains("新增") ? "feat" : labels.Contains("删除") ? "chore" : "fix";
        var now = DateTime.Now.ToString("MM-dd HH:mm");
        var detail = labels.Contains("新增")
            ? $"新增 {state.Files.Count(f => f.Label == "新增")} 个文件"
            : labels.Contains("修改")
                ? $"更新 {state.Files.Count(f => f.Label == "修改")} 个文件"
                : $"{state.Files.Count} 个文件改动";
        return $"{scope}: {detail} ({now})";
    }

    private static TaskResult Fail(string message) => new() { Ok = false, Message = message };
}

using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;

namespace BlogTool;

/// <summary>当前凭据状态。只带元信息，绝不携带明文 token。</summary>
public sealed record CredentialInfo
{
    /// <summary>Windows 凭据管理器里有没有 github.com 的凭据。</summary>
    public bool Exists { get; init; }

    public string Username { get; init; } = "";
    public int PasswordLength { get; init; }
    public string Helper { get; init; } = "";
    public string OriginUrl { get; init; } = "";
    public string Note { get; init; } = "";
}

/// <summary>
/// GitHub 凭据的读写。
///
/// 关键设计：**不自己存 token**，一律通过 git 的 credential helper 转交。
/// 当前机器的 helper 是 manager（GCM），它会写进 Windows 凭据管理器并用
/// DPAPI 加密；自己存的话就多了一份明文副本，风险高一层，
/// 而且 git 推送时照样认不出它，等于白做。
/// </summary>
public static class Credentials
{
    public const string DefaultHost = "github.com";

    /// <summary>credential fill 那一条的结果。单独抽出来是为了能被并发地跑。</summary>
    private sealed record FillResult(bool Exists, string Username, int PasswordLength, string Note);

    /// <summary>读出当前状态。只有元信息，不含 token 明文。</summary>
    public static async Task<CredentialInfo> ReadAsync(string host = DefaultHost)
    {
        // 三条命令互不依赖，串着跑就是三份进程启动开销一份接一份地等。
        // 这段延迟全程挡在用户眼前——设置窗口是同步弹出来的，先摆好界面
        // 再等它们回来，所以并发的收益全落在体感上。
        var helperTask = ConfigAsync("credential.helper");
        var originTask = RemoteUrlAsync("origin");
        var fillTask = FillAsync(host);

        await Task.WhenAll(helperTask, originTask, fillTask);

        var fill = fillTask.Result;
        return new CredentialInfo
        {
            Exists = fill.Exists,
            Username = fill.Username,
            PasswordLength = fill.PasswordLength,
            Helper = await helperTask ?? "(未配置)",
            OriginUrl = await originTask,
            Note = fill.Note,
        };
    }

    private static async Task<FillResult> FillAsync(string host)
    {
        try
        {
            var (code, stdout, _) = await Git.RunWithInputAsync(
                "credential fill", $"protocol=https\nhost={host}\n\n");

            if (code != 0) return new FillResult(false, "", 0, "");

            var parsed = ParseCredentialOutput(stdout);
            return new FillResult(
                parsed.Password.Length > 0,
                parsed.Username,
                parsed.Password.Length,
                "");
        }
        catch (Exception ex)
        {
            return new FillResult(false, "", 0, ex.Message);
        }
    }

    /// <summary>写入凭据。token 由 helper 负责落盘，本程序不留存。</summary>
    public static async Task SaveAsync(string username, string token, string host = DefaultHost)
    {
        if (string.IsNullOrWhiteSpace(username)) throw new ArgumentException("用户名不能为空");
        if (string.IsNullOrWhiteSpace(token)) throw new ArgumentException("令牌不能为空");

        var input = $"protocol=https\nhost={host}\nusername={username}\npassword={token}\n\n";
        var (code, _, stderr) = await Git.RunWithInputAsync("credential approve", input);
        if (code != 0)
            throw new InvalidOperationException($"git 拒绝了这次写入：{stderr.Trim()}");
    }

    /// <summary>清除已存凭据，下次推送时会重新弹窗索要。</summary>
    public static async Task ClearAsync(string username, string host = DefaultHost)
    {
        var input = string.IsNullOrEmpty(username)
            ? $"protocol=https\nhost={host}\n\n"
            : $"protocol=https\nhost={host}\nusername={username}\n\n";
        var (code, _, stderr) = await Git.RunWithInputAsync("credential reject", input);
        if (code != 0)
            throw new InvalidOperationException($"清除失败：{stderr.Trim()}");
    }

    /// <summary>
/// 用浏览器完成 GitHub 登录，不经过令牌输入框。
///
/// 走的是 git 自带的 Git Credential Manager（credential.helper = manager 时
/// 一定在），它会打开浏览器做 OAuth 授权，成功后把凭据写进 Windows 凭据
/// 管理器。用户不需要知道什么是 PAT，也不需要手动粘贴任何一串字符。
///
/// 无窗口运行：本程序没有控制台，弹黑框既突兀又没法读状态，
/// 进度由调用方用 onStatus 回调自己显示。
/// </summary>
public static async Task<(bool Ok, string Message)> BrowserLoginAsync(
    string username, Action<string>? onStatus = null, CancellationToken cancellation = default)
{
    var psi = new ProcessStartInfo
    {
        FileName = "git",
        WorkingDirectory = Git.RepoRoot,
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        CreateNoWindow = true,
        StandardOutputEncoding = Encoding.UTF8,
        StandardErrorEncoding = Encoding.UTF8,
    };
    psi.ArgumentList.Add("credential-manager");
    psi.ArgumentList.Add("github");
    psi.ArgumentList.Add("login");
    psi.ArgumentList.Add("--browser");     // 打开浏览器完成授权
    psi.ArgumentList.Add("--no-ui");       // 不要 GCM 自己的图形提示
    psi.ArgumentList.Add("--force");       // 用户主动点了登录，就是要覆盖旧的
    if (!string.IsNullOrWhiteSpace(username))
    {
        psi.ArgumentList.Add("--username");
        psi.ArgumentList.Add(username.Trim());
    }

    onStatus?.Invoke("正在启动 GitHub 登录…");

    using var proc = Process.Start(psi);
    if (proc is null) return (false, "无法启动 git-credential-manager。");

    var stdoutTask = proc.StandardOutput.ReadToEndAsync();
    var stderrTask = proc.StandardError.ReadToEndAsync();

    try
    {
        // 浏览器授权要人来点，可能要一两分钟；这里只防「永远不返回」
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        cts.CancelAfter(TimeSpan.FromMinutes(5));
        await proc.WaitForExitAsync(cts.Token);
    }
    catch (OperationCanceledException)
    {
        if (!proc.HasExited) { try { proc.Kill(entireProcessTree: true); } catch { } }
        return (false, cancellation.IsCancellationRequested ? "已取消登录。" : "5 分钟内没完成，已放弃。");
    }

    var stdout = await stdoutTask;
    var stderr = await stderrTask;

    if (proc.ExitCode == 0)
    {
        onStatus?.Invoke("登录成功。");
        return (true, "登录成功，凭据已交给 Git 的凭据助手保存。");
    }

    var detail = (stderr.Length > 0 ? stderr : stdout).Trim();
    if (detail.Length > 300) detail = detail[..300];
    return (false, $"登录失败（退出码 {proc.ExitCode}）{(detail.Length > 0 ? "：\n" + detail : "")}");
}

/// <summary>在系统默认浏览器里打开 GitHub 的令牌生成页。</summary>
public static void OpenTokenPage()
{
    Process.Start(new ProcessStartInfo
    {
        FileName = "https://github.com/settings/tokens/new",
        UseShellExecute = true,
    });
}

/// <summary>
/// 取回已保存的凭据原文。
///
/// 存在的意义是「测试连接」不必让用户把令牌重新敲一遍：
/// 令牌框留空时，直接拿这里取到的值去问 GitHub。
/// 取出的密码只在内存里活着，不显示、不写日志、不落盘。
/// </summary>
public static async Task<(string Username, string Password)> GetStoredAsync(
    string host = DefaultHost)
{
    var (code, stdout, _) = await Git.RunWithInputAsync(
        "credential fill", $"protocol=https\nhost={host}\n\n");

    if (code != 0) return ("", "");
    var parsed = ParseCredentialOutput(stdout);
    return parsed.Password.Length > 0 ? parsed : ("", "");
}

/// <summary>
/// 拿 token 直接问 GitHub 要一次身份，验证它还有没有效。
///
/// 不做这个检查的话，token 过期时用户只能等到推送失败才知道，
/// 而那时候构建和单测都已经跑完了，浪费的是时间不是 token。
/// </summary>
public static async Task<(bool Ok, string Message)> TestAsync(string username, string token)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        var auth = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{token}"));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", auth);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("BlogTool");

        try
        {
            using var resp = await client.GetAsync("https://api.github.com/user");
            if (resp.IsSuccessStatusCode)
                return (true, "令牌有效，GitHub 认得这个账号。");

            return resp.StatusCode switch
            {
                HttpStatusCode.Unauthorized =>
                    (false, "GitHub 拒绝了：用户名或令牌不对（401）。"),
                HttpStatusCode.Forbidden =>
                    (false, "403：令牌权限不足，或触发了 GitHub 的速率限制。"),
                _ => (false, $"连不通 GitHub（{(int)resp.StatusCode} {resp.StatusCode}）。"),
            };
        }
        catch (TaskCanceledException)
        {
            return (false, "15 秒没等到 GitHub 回应，网络可能不通。");
        }
        catch (HttpRequestException ex)
        {
            return (false, $"连不上 GitHub：{ex.Message}");
        }
    }

    /// <summary>
    /// 解析 git credential fill 的输出。
    /// 拆出来是因为这段格式是稳定的、可以离线断言，不该靠真的写凭据来测。
    /// </summary>
    public static (string Username, string Password) ParseCredentialOutput(string stdout)
    {
        var user = "";
        var pass = "";
        foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var line2 = line.TrimEnd('\r');
            if (line2.StartsWith("username=", StringComparison.Ordinal)) user = line2[9..];
            else if (line2.StartsWith("password=", StringComparison.Ordinal)) pass = line2[9..];
        }
        return (user, pass);
    }

    /// <summary>遮蔽用：只保留头尾各两位，中间换成点。</summary>
    public static string Mask(string value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        if (value.Length <= 8) return new string('•', value.Length);
        return value[..2] + new string('•', Math.Min(12, value.Length - 4)) + value[^2..];
    }

    /// <summary>读 origin 的推送地址，剥掉可能存在的用户名。</summary>
    private static async Task<string> RemoteUrlAsync(string remote)
    {
        try
        {
            var (code, stdout, _) = await Git.RunWithInputAsync(
                $"remote get-url {remote}", "");
            return code == 0 ? stdout.Trim() : "";
        }
        catch
        {
            return "";
        }
    }

    private static async Task<string?> ConfigAsync(string key)
    {
        try
        {
            var (code, stdout, _) = await Git.RunWithInputAsync($"config --get {key}", "");
            var value = stdout.Trim();
            return code == 0 && value.Length > 0 ? value : null;
        }
        catch
        {
            return null;
        }
    }
}
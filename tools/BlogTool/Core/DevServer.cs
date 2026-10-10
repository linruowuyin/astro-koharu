using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace BlogTool;

/// <summary>
/// 本地预览：起一个 astro dev，浏览器打开它，停的时候连整棵进程树一起收掉。
///
/// 三件容易踩的事这里都处理了：
///   · pnpm 在 Windows 上是 .cmd，必须经 cmd.exe 起
///   · astro 起的不是这个进程，是它再 spawn 的子进程；
///     只杀父进程会把 node 留在后台继续占着端口，所以按进程树杀
///   · 端口被占时 astro 会自己换一个，报出来的地址和 4321 不一样，
///     所以从输出里把真实地址抠出来，而不是写死
/// </summary>
public sealed class DevServer : IDisposable
{
    public enum State { Stopped, Starting, Running, Failed }

    /// <summary>astro dev 常用端口，探测时按这个顺序试。</summary>
    private static readonly int[] CandidatePorts = [4321, 4322, 4323, 4324, 4325];

    private Process? _proc;
    private readonly List<string> _log = [];
    private readonly Lock _gate = new();

    public State Status { get; private set; } = State.Stopped;
    public string Url { get; private set; } = "";
    public string Error { get; private set; } = "";

    public event Action<string>? OnLog;
    public event Action? OnStateChanged;

    /// <summary>已跑的日志副本，供界面读取。</summary>
    public IReadOnlyList<string> LogLines
    {
        get { lock (_gate) return _log.ToArray(); }
    }

    /// <summary>先看有没有一个已经在跑的 dev server，能复用就不重复起。</summary>
    public static async Task<string?> DetectRunningAsync()
    {
        foreach (var port in CandidatePorts)
        {
            if (!await IsAliveAsync(port)) continue;
            // 端口开着不一定就是它，得问一句是不是 astro
            var (code, body) = await HttpProbeAsync($"http://localhost:{port}/");
            if (code == 200 && body.Contains("astro", StringComparison.OrdinalIgnoreCase))
                return $"http://localhost:{port}/";
        }
        return null;
    }

    /// <summary>起服务。onLine 会实时收到每一行输出。</summary>
    public async Task<bool> StartAsync(Action<string>? onLine = null)
    {
        if (Status is State.Starting or State.Running) return Status == State.Running;

        SetState(State.Starting);
        Error = "";
        lock (_gate) _log.Clear();

        var psi = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            // dev 脚本本身是 pnpm koharu migrate --check && astro dev，
            // 直接跑 dev 就带上这个前置检查，不自己拼命令绕过它
            Arguments = "/d /s /c pnpm dev",
            WorkingDirectory = Project.Root,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        // pnpm 在 CI 环境下会对非交互式操作卡住确认
        psi.Environment["CI"] = "true";
        psi.Environment["ASTRO_TELEMETRY_DISABLED"] = "1";

        try
        {
            _proc = Process.Start(psi) ?? throw new InvalidOperationException("无法启动 cmd.exe");
        }
        catch (Exception ex)
        {
            Error = $"启动失败：{ex.Message}";
            Append($"[启动失败] {ex.Message}");
            SetState(State.Failed);
            return false;
        }

        var outTask = PumpAsync(_proc.StandardOutput, onLine);
        var errTask = PumpAsync(_proc.StandardError, onLine);

        // 等它把地址打出来。astro 冷启动（含 vite 预构建）可能要十几秒，
        // 给足时间而不是一看到进程活着就报成功。
        var ready = await WaitForUrlAsync(TimeSpan.FromSeconds(90));

        if (_proc.HasExited)
        {
            var code = _proc.ExitCode;
            if (!ready)
            {
                Error = $"pnpm dev 退出码 {code}。看上面的日志找原因。";
                SetState(State.Failed);
                return false;
            }
        }

        await Task.WhenAll(outTask, errTask).ConfigureAwait(false);

        if (!ready)
        {
            Error = "等了 90 秒还没看到 dev server 起来。日志区应该有线索。";
            SetState(State.Failed);
            return false;
        }

        Append($"就绪：{Url}");
        SetState(State.Running);
        return true;
    }

    /// <summary>停服务。连同子进程一起收，避免 node 留在后台占端口。</summary>
    public void Stop()
    {
        if (_proc is null)
        {
            // 认领来的实例：没有句柄，也不该去杀——那不是我们起的进程
            if (Status == State.Running) Append("已断开（这个服务不是本窗口启动的，仍在运行）");
            Url = "";
            SetState(State.Stopped);
            return;
        }

        try
        {
            if (!_proc.HasExited)
            {
                // cmd.exe 不会把信号转给 node，得按树杀
                _proc.Kill(entireProcessTree: true);
                _proc.WaitForExit(5000);
            }
        }
        catch
        {
            // 已经退了就算了
        }
        finally
        {
            _proc.Dispose();
            _proc = null;
        }

        Url = "";
        Append("已停止");
        SetState(State.Stopped);
    }

    public void Dispose() => Stop();

    /// <summary>
    /// 认领一个已经在跑的 dev server。
    ///
    /// 复用而不是重复起：同端口再起一个 astro 不是不行，但会让用户面对
    /// 两个热更新实例，改了文件哪边生效说不清。认领来的实例没有进程句柄，
    /// 所以 Stop 不会去杀它——那是别人的进程。
    /// </summary>
    public void Adopt(string url)
    {
        Url = url;
        Status = State.Running;
        SetState(State.Running);
    }

    /// <summary>从输出里抠出 astro 打出来的本地地址。</summary>
    private async Task<bool> WaitForUrlAsync(TimeSpan timeout)
    {
        // 形如 Local: http://localhost:4321/
        var pattern = new Regex(@"https?://localhost:\d+/?", RegexOptions.IgnoreCase);
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            lock (_gate)
            {
                foreach (var line in _log)
                {
                    var m = pattern.Match(line);
                    if (m.Success)
                    {
                        Url = m.Value;
                        if (!Url.EndsWith('/')) Url += "/";
                        return true;
                    }
                }
            }

            if (_proc is { HasExited: true }) return Url.Length > 0;
            await Task.Delay(200);
        }
        return Url.Length > 0;
    }

    private async Task PumpAsync(StreamReader reader, Action<string>? onLine)
    {
        while (await reader.ReadLineAsync() is { } line)
        {
            Append(line);
            onLine?.Invoke(line);
        }
    }

    private void Append(string line)
    {
        lock (_gate)
        {
            _log.Add(line);
            // astro 首屏刷得非常凶，不截断的话内存会一直涨
            if (_log.Count > 500) _log.RemoveRange(0, _log.Count - 500);
        }
        OnLog?.Invoke(line);
    }

    private void SetState(State s)
    {
        Status = s;
        OnStateChanged?.Invoke();
    }

    private static async Task<bool> IsAliveAsync(int port)
    {
        try
        {
            using var client = new TcpClient();
            var connect = client.ConnectAsync("127.0.0.1", port);
            return await Task.WhenAny(connect, Task.Delay(400)) == connect && client.Connected;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<(int Code, string Body)> HttpProbeAsync(string url)
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            var resp = await client.GetAsync(url);
            var body = await resp.Content.ReadAsStringAsync();
            return ((int)resp.StatusCode, body);
        }
        catch
        {
            return (0, "");
        }
    }

    /// <summary>在默认浏览器里打开地址。</summary>
    public static void OpenBrowser(string url)
    {
        Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
    }
}
using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace BlogTool;

/// <summary>
/// 项目定位与基础操作。
/// </summary>
public static class Project
{
    /// <summary>项目根目录（含 package.json 与 .git）。</summary>
    public static string Root { get; private set; } = "";

    /// <summary>设置文件放在用户目录，不污染 exe 所在的文件夹。</summary>
    private static string StateFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BlogTool", "project.txt");

    /// <summary>
    /// 定位项目根，按可靠程度依次尝试：
    ///
    ///   1. 从 exe 自身位置逐级向上找——exe 放在项目里时最准
    ///   2. 记住的上次路径——exe 放桌面时唯一可行的办法
    ///   3. 编译时写死的路径——首次运行还没有「上次」可用时的兜底
    ///
    /// 只靠「向上找」是不够的：exe 放到桌面后，往上到磁盘根都碰不到
    /// package.json，程序会直接说找不到项目。
    /// </summary>
    public static bool TryLocate()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            var dir = new DirectoryInfo(start);
            while (dir is not null)
            {
                if (LooksLikeRoot(dir.FullName)) { return SetRoot(dir.FullName); }
                dir = dir.Parent;
            }
        }

        foreach (var candidate in new[] { RememberedRoot(), BakedRoot() })
        {
            if (candidate is { Length: > 0 } && LooksLikeRoot(candidate)) return SetRoot(candidate);
        }
        return false;
    }

    /// <summary>用户手动指定的路径优先，之后一直生效。</summary>
    public static bool TryUseExplicitRoot(string path)
        => LooksLikeRoot(path) && SetRoot(path);

    /// <summary>记住本次用到的路径，下次 exe 在别处也能直接开工。</summary>
    public static void Remember()
    {
        try
        {
            var dir = Path.GetDirectoryName(StateFile)!;
            Directory.CreateDirectory(dir);
            File.WriteAllText(StateFile, Root, Encoding.UTF8);
        }
        catch
        {
            // 记不住不影响使用，下次再选一次就行。
        }
    }

    private static bool SetRoot(string root)
    {
        // 向上找是从 AppContext.BaseDirectory 开始的，它自带结尾反斜杠。
        // 不削掉的话后面拼路径、给 git 当工作目录都带着个多余的斜杠。
        Root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        Remember();
        return true;
    }

    private static bool LooksLikeRoot(string path)
        => File.Exists(Path.Combine(path, "package.json"))
        && Directory.Exists(Path.Combine(path, ".git"));

    private static string? RememberedRoot()
    {
        try
        {
            return File.Exists(StateFile) ? File.ReadAllText(StateFile, Encoding.UTF8).Trim() : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 编译时通过 AssemblyMetadata 写进来的项目路径。
    /// 由 BlogTool.csproj 里的 BlogRoot 属性提供，路径变了重新编译即可。
    /// </summary>
    private static string? BakedRoot()
    {
        var attr = typeof(Project).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a?.Key == "BlogRoot");
        return attr?.Value;
    }

    /// <summary>拼出项目内的绝对路径。方法名不叫 Path，避免和 System.IO.Path 撞名。</summary>
    public static string At(params string[] parts)
        => System.IO.Path.Combine(new[] { Root }.Concat(parts).ToArray());

    /// <summary>
    /// 跑一条 pnpm 命令，实时把输出回调出去。
    /// Windows 上 pnpm 是 .cmd 批处理，必须经由 shell 才能启动。
    /// </summary>
    public static async Task<int> RunPnpmAsync(string args, Action<string> onLine, Action<string>? onPhase = null)
    {
        onLine?.Invoke($"> pnpm {args}");

        var psi = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/d /s /c pnpm {args}",
            WorkingDirectory = Root,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.Environment["CI"] = "true";

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("无法启动 pnpm");
        // stdout 与 stderr 并行读，否则任一写满管道缓冲就会死锁。
        var outTask = PumpAsync(proc.StandardOutput, onLine);
        var errTask = PumpAsync(proc.StandardError, onLine);
        await Task.WhenAll(outTask, errTask);
        return proc.ExitCode;
    }

    private static async Task PumpAsync(StreamReader reader, Action<string>? onLine)
    {
        while (await reader.ReadLineAsync() is { } line) onLine?.Invoke(line);
    }

    /// <summary>
    /// 跑一条命令并拿回 stdout，用于把结果塞进界面。
    ///
    /// 参数走 <see cref="ProcessStartInfo.ArgumentList"/> 而不是拼字符串：
    /// 拼字符串时带空格或中文的参数会被自己拆开（之前 --date=format 里
    /// 那个 %H 就是这么变成"对象名"的）。ArgumentList 由运行时负责转义。
    /// </summary>
    public static async Task<(int Code, string Output)> CaptureAsync(
        string fileName, IEnumerable<string> args, string? workingDirectory = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory ?? Root,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        try
        {
            using var proc = Process.Start(psi)
                ?? throw new InvalidOperationException($"无法启动 {fileName}");
            var outTask = proc.StandardOutput.ReadToEndAsync();
            var errTask = proc.StandardError.ReadToEndAsync();
            await Task.WhenAll(outTask, errTask);
            await proc.WaitForExitAsync();
            return (proc.ExitCode, outTask.Result);
        }
        catch
        {
            // node 不在 PATH 上之类的情况由调用方兜底，不在这里抛。
            return (-1, "");
        }
    }
}

/// <summary>一个分类（中文名 + 目录 slug）。</summary>
/// <remarks>
/// 必须覆写 ToString：ComboBox 直接拿它当显示文本，
/// record 的默认实现会打印成 "CategoryInfo { Name = 基金, Slug = fund }"。
/// </remarks>
public sealed record CategoryInfo(string Name, string Slug)
{
    public override string ToString() => Name;
}

/// <summary>读取 site.yaml 的分类表。</summary>
public static class Categories
{
    /// <summary>
    /// 从 config/site.yaml 的 categoryMap 读出「分类名 → 目录名」。
    ///
    /// 用正则而不是引入 YAML 库：只需要顶层这一个 map，
    /// 引入依赖会让 exe 体积再涨一大截，而且 YAML 的引号/注释规则
    /// 容易踩坑。categoryMap 的格式在项目里是固定的单行键值对。
    /// </summary>
    public static List<CategoryInfo> Read()
    {
        var configPath = Project.At("config", "site.yaml");
        if (!File.Exists(configPath)) return [];

        var result = new List<CategoryInfo>();
        // 形如：  基金: fund
        var pattern = new Regex(@"^\s{2}([^:#\s][^:]*?):\s*([A-Za-z0-9\-_]+)\s*$", RegexOptions.Compiled);
        bool inMap = false;

        foreach (var line in File.ReadAllLines(configPath))
        {
            if (line.StartsWith("categoryMap:", StringComparison.Ordinal))
            {
                inMap = true;
                continue;
            }
            if (!inMap) continue;

            // 下一个顶层键出现，说明 categoryMap 结束了。
            if (line.Length > 0 && !char.IsWhiteSpace(line[0]) && line.Trim().Length > 0) break;
            if (line.Trim().Length == 0 || line.TrimStart().StartsWith('#')) continue;

            var m = pattern.Match(line);
            if (m.Success)
            {
                result.Add(new CategoryInfo(m.Groups[1].Value.Trim(), m.Groups[2].Value.Trim()));
            }
        }
        return result;
    }
}

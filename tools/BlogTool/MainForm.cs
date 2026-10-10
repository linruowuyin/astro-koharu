using System.Drawing;
using System.Windows.Forms;

namespace BlogTool;

/// <summary>
/// 博客工具主窗口。
///
/// 一个窗口对应原来四个 bat：新建文章 / 图片更新 / 发布 / 回滚。
/// 目标很单纯——点一下就跑完，不闪黑框，中途能看到进度。
/// </summary>
internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        // 自检排在「找不到项目」的弹窗之前：自检本身就是用来诊断
        // 定位失败的，放后面就会被那个弹窗挡住，脚本也拿不到结果。
        var selftestAt = Array.IndexOf(args, "--selftest");
        if (selftestAt >= 0)
        {
            var outFile = selftestAt + 1 < args.Length ? args[selftestAt + 1] : "";
            SelfTest.Run(outFile);
            return;
        }

        if (!Project.TryLocate())
        {
            if (!AskForProject()) return;
        }

        // git 层必须显式指到项目根。留空的话它会在进程的当前目录里找仓库，
        // 而 exe 放桌面时那个目录正是桌面，所有 git 命令都会失败。
        Git.RepoRoot = Project.Root;

        var shotAt = Array.IndexOf(args, "--shot");
        if (shotAt >= 0)
        {
            var target = shotAt + 1 < args.Length ? args[shotAt + 1] : "";
            var which = shotAt + 2 < args.Length ? args[shotAt + 2] : "main";
            Shot(target, which);
            return;
        }
        Application.Run(new MainForm());
    }

    /// <summary>
    /// 找不到项目时让用户自己指一次，之后记下来不用再选。
    ///
    /// exe 放到桌面时「从自身位置向上找」注定失败，必须给一条出路，
    /// 否则用户只会看到一个没有下文的报错框。
    /// </summary>
    private static bool AskForProject()
    {
        MessageBox.Show(
            "还没有指定博客项目的位置。\n\n" +
            "点「选择文件夹」找到博客根目录——就是里面同时有 package.json 和 .git 的那一层。\n\n" +
            "选一次就好，以后会自动记住。",
            "选择项目", MessageBoxButtons.OK, MessageBoxIcon.Information);

        using var picker = new FolderBrowserDialog
        {
            Description = "选择博客项目根目录（包含 package.json 和 .git）",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false,
        };
        if (picker.ShowDialog() != DialogResult.OK) return false;

        if (!Project.TryUseExplicitRoot(picker.SelectedPath))
        {
            MessageBox.Show(
                $"这个文件夹里没有 package.json 和 .git：\n\n{picker.SelectedPath}\n\n请选项目根目录。",
                "选错了", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return AskForProject();
        }
        return true;
    }

    /// <summary>
    /// 把某个界面渲染成图片后退出。
    ///
    /// 布局问题靠肉眼读代码很难发现——控件被挤到可视区外、Dock 顺序反了、
    /// 文字被截断，这些只有真渲染一次才看得见。
    /// </summary>
    private static void Shot(string target, string which)
    {
        // 各弹窗类型不同，统一按 Form 收口。
        Form? control = which switch
        {
            "newpost" => new NewPostDialog(Categories.Read(), "10月第二周宏观利率周评"),
            "rollback" => new RollbackDialog(),
            "commit" => BuildCommitSample(),
            _ => null,
        };

        if (control is null)
        {
            var main = new MainForm();
            // 主窗口要带上真实内容才看得出比例。
            main.SeedSampleLog();
            control = main;
        }

        control.StartPosition = FormStartPosition.Manual;
        control.Location = new Point(0, 0);
        control.ShowInTaskbar = false;

        control.Shown += (_, _) =>
        {
            // 弹窗是异步填内容的（回滚列表要读 git），等它自己画完。
            for (var i = 0; i < 40; i++)
            {
                Thread.Sleep(100);
                Application.DoEvents();
            }
            try
            {
                using var bmp = new Bitmap(control.Width, control.Height);
                control.DrawToBitmap(bmp, new Rectangle(0, 0, control.Width, control.Height));
                bmp.Save(target, System.Drawing.Imaging.ImageFormat.Png);
            }
            catch (Exception ex)
            {
                File.WriteAllText(target + ".err", ex.ToString());
            }
            control.Close();
            Application.Exit();
        };

        Application.Run(control);
    }

    /// <summary>造一份假改动清单，用来验证提交对话框的排版。</summary>
    private static CommitDialog BuildCommitSample()
    {
        var files = new List<ChangedFile>
        {
            new("M", "修改", "src/components/layout/StockBar.astro", 42, 17),
            new("M", "修改", "config/site.yaml", 3, 1),
            new("A", "新增", "src/content/blog/research/测试文章.md", 24, 0),
            new("D", "删除", "public/old-cover.png", null, null),
        };
        return new CommitDialog(files, "feat: 更新行情条并新增一篇测试文章 (10-10 15:20)");
    }
}

/// <summary>
/// 自检：把「定位项目 / 读分类 / 生成 slug / 读 git 状态」一次性跑一遍。
///
/// 这些环节各自都可能在换机器后失效（node 不在 PATH、site.yaml 结构变了、
/// 仓库路径挪了），有个能一条命令验完的入口比逐个点按钮试快得多。
/// </summary>
internal static class SelfTest
{
    public static void Run(string outFile)
    {
        var located = Project.TryLocate();
        if (located) Git.RepoRoot = Project.Root;

        var lines = new List<string>
        {
            located ? $"项目根：{Project.Root}" : $"项目根：没找到（当前目录 {Environment.CurrentDirectory}）",
            "",
            "分类：",
        };

        if (!located)
        {
            lines.Add("  跳过：没有项目根就读不到 site.yaml");
        }
        else try
        {
            var cats = Categories.Read();
            if (cats.Count == 0) lines.Add("  （读不到，检查 config/site.yaml 的 categoryMap）");
            foreach (var c in cats) lines.Add($"  {c.Name} → {c.Slug}");

            lines.Add("");
            lines.Add("slug：");
            foreach (var t in new[]
            {
                "白银-黄金放大器与工业利剑",
                "6.15复盘-踏空的一天",
                "A股ETF详解",
                "2026年10月宏观利率周评",
            })
            {
                lines.Add($"  {t}");
                lines.Add($"    → {PostCreator.SlugifyAsync(t).GetAwaiter().GetResult()}");
            }
        }
        catch (Exception ex)
        {
            lines.Add($"失败：{ex}");
        }

        if (located) try
        {
            var state = Git.GetStateAsync().GetAwaiter().GetResult();
            lines.Add("");
            lines.Add($"分支：{state.Branch}  领先 {state.Ahead}  落后 {state.Behind}  分叉 {(state.Diverged ? "是" : "否")}");
            lines.Add($"未提交改动：{state.Files.Count} 个");
            foreach (var f in state.Files) lines.Add($"  {f}");
        }
        catch (Exception ex)
        {
            lines.Add("");
            lines.Add($"git 读取失败：{ex.Message}");
        }

        var text = string.Join(Environment.NewLine, lines);
        if (outFile.Length == 0)
        {
            MessageBox.Show(text, "自检结果", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        else
        {
            // 指定了输出路径就是给脚本用的，不能再弹窗把人卡住。
            File.WriteAllText(outFile, text, new System.Text.UTF8Encoding(false));
        }
    }
}

internal sealed class MainForm : Form
{
    private static readonly Color Bg = Color.FromArgb(240, 242, 245);
    private static readonly Color Card = Color.White;
    private static readonly Color Line = Color.FromArgb(216, 220, 226);
    private static readonly Color Fg = Color.FromArgb(28, 32, 38);
    private static readonly Color Dim = Color.FromArgb(120, 128, 140);
    private static readonly Color Accent = Color.FromArgb(22, 119, 255);
    private static readonly Color Good = Color.FromArgb(22, 160, 90);
    private static readonly Color Bad = Color.FromArgb(200, 60, 60);

    private readonly Button _newPost = new();
    private readonly Button _lqip = new();
    private readonly Button _publish = new();
    private readonly Button _rollback = new();
    private readonly TextBox _log = new();
    private readonly Panel _logPanel = new();
    private readonly Panel _logHost = new();
    private readonly Label _status = new();
    private readonly Button _clearLog = new();
    private bool _busy;

    public MainForm()
    {
        Text = "博客工具";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(900, 580);
        MinimumSize = new Size(780, 560);
        BackColor = Bg;
        ForeColor = Fg;
        Font = new Font("Microsoft YaHei UI", 9F);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;

        BuildUi();

        // 窗口可以拖边框改大小，日志区跟着撑满剩余空间。
        Resize += (_, _) =>
        {
            _logHost.Bounds = new Rectangle(20, 232, ClientSize.Width - 40, ClientSize.Height - 252);
            _status.Bounds = new Rectangle(22, 200, ClientSize.Width - 44, 24);
        };
    }

    private void BuildUi()
    {
        // ---- 标题 ----
        var title = new Label
        {
            Text = "博客工具",
            Font = new Font("Microsoft YaHei UI", 15F, FontStyle.Bold),
            AutoSize = true,
            Location = new Point(20, 16),
        };
        var sub = new Label
        {
            Text = Project.Root,
            ForeColor = Dim,
            AutoSize = true,
            Location = new Point(22, 46),
            MaximumSize = new Size(800, 0),
        };

        // ---- 四个功能按钮 ----
        MakeActionButton(_newPost, "新建文章", "填标题和分类", 20, 84,
            async () => await RunNewPostAsync());
        MakeActionButton(_lqip, "图片更新", "生成占位图", 243, 84,
            async () => await RunLqipAsync());
        MakeActionButton(_publish, "发布", "检查后推送", 466, 84,
            async () => await RunPublishAsync());
        MakeActionButton(_rollback, "回滚", "退回上一版", 689, 84,
            async () => await RunRollbackAsync());

        // ---- 状态栏 ----
        _status.Text = "就绪";
        _status.ForeColor = Dim;
        _status.AutoSize = false;
        _status.Bounds = new Rectangle(22, 200, 856, 24);
        _status.TextAlign = ContentAlignment.MiddleLeft;

        // ---- 日志区 ----
        // 沿用浅色系：深色日志块贴在浅色窗口里像贴错了一块。
        //
        // 用绝对坐标而不是 Dock：Dock 的填充顺序要按 z-order 反推，
        // 一旦控件添加次序变了，日志区就会盖到按钮上（踩过一次）。
        // 日志区的高度跟着窗口走，Form.Resize 时再调。
        _logPanel.Bounds = new Rectangle(12, 10, 836, 296);
        _logPanel.BackColor = Color.FromArgb(250, 251, 252);
        _logPanel.Padding = new Padding(12, 8, 12, 8);

        var logTitle = new Label
        {
            Text = "执行日志",
            ForeColor = Color.FromArgb(140, 148, 160),
            AutoSize = true,
            Location = new Point(14, 6),
        };
        _clearLog.Text = "清空";
        _clearLog.FlatStyle = FlatStyle.Flat;
        _clearLog.ForeColor = Color.FromArgb(120, 128, 140);
        _clearLog.BackColor = Color.FromArgb(236, 238, 242);
        _clearLog.Size = new Size(58, 24);
        _clearLog.Click += (_, _) => _log.Clear();

        _log.Bounds = new Rectangle(10, 38, 816, 250);
        _log.Multiline = true;
        _log.ReadOnly = true;
        _log.ScrollBars = ScrollBars.Vertical;
        _log.BackColor = Color.FromArgb(250, 251, 252);
        _log.ForeColor = Color.FromArgb(60, 66, 74);
        _log.BorderStyle = BorderStyle.None;
        _log.Font = new Font("Consolas", 9F);

        _logPanel.Controls.Add(_log);
        _logPanel.Controls.Add(logTitle);
        _logPanel.Controls.Add(_clearLog);
        _clearLog.Location = new Point(_logPanel.ClientSize.Width - 70, 4);
        _logPanel.Resize += (_, _) =>
        {
            _clearLog.Left = _logPanel.ClientSize.Width - 70;
            _log.Width = Math.Max(120, _logPanel.ClientSize.Width - 20);
            _log.Height = Math.Max(80, _logPanel.ClientSize.Height - 46);
        };

        var logHost = _logHost;
        logHost.Bounds = new Rectangle(20, 232, 860, 316);
        logHost.Controls.Add(_logPanel);

        // 日志区必须最后加：WinForms 里后加的控件 z-order 靠后，
        // 靠后才会被先加的按钮和标题压住。
        Controls.Add(logHost);
        Controls.Add(_status);
        Controls.Add(sub);
        Controls.Add(title);
        Controls.AddRange([_newPost, _lqip, _publish, _rollback]);
        logHost.SendToBack();
    }

    private void MakeActionButton(Button b, string text, string desc, int x, int y, Action onClick)
    {
        b.Text = $"{text}\n\n{desc}";
        b.Size = new Size(203, 96);
        b.Location = new Point(x, y);
        b.FlatStyle = FlatStyle.Flat;
        b.FlatAppearance.BorderColor = Line;
        b.BackColor = Card;
        b.ForeColor = Fg;
        b.TextAlign = ContentAlignment.MiddleCenter;
        b.Cursor = Cursors.Hand;
        b.Font = new Font("Microsoft YaHei UI", 9.5F);
        b.Click += (_, _) => onClick();
    }

    // ============ 日志 ============

    private void Log(string line)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => Log(line));
            return;
        }
        _log.AppendText(line + Environment.NewLine);
        _log.SelectionStart = _log.TextLength;
        _log.ScrollToCaret();
    }

    private void SetBusy(bool busy, string status)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => SetBusy(busy, status));
            return;
        }
        _busy = busy;
        _newPost.Enabled = _lqip.Enabled = _publish.Enabled = _rollback.Enabled = !busy;
        _status.Text = status;
        _status.ForeColor = busy ? Accent : Dim;
        Cursor = busy ? Cursors.WaitCursor : Cursors.Default;
    }

    private void Done(bool ok, string message)
    {
        Log("");
        Log(ok ? $"✓ {message}" : $"✗ {message}");
        _status.Text = ok ? message.Split('\n')[0] : "执行失败";
        _status.ForeColor = ok ? Good : Bad;
        MessageBox.Show(this, message, ok ? "完成" : "未完成",
            MessageBoxButtons.OK, ok ? MessageBoxIcon.Information : MessageBoxIcon.Error);
    }

    // ============ 四个功能 ============

    private async Task RunNewPostAsync()
    {
        var categories = Categories.Read();
        if (categories.Count == 0)
        {
            MessageBox.Show(this,
                "读不到分类列表。\n\n请确认 config\\site.yaml 里有 categoryMap 配置。",
                "新建文章", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        using var dialog = new NewPostDialog(categories);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        SetBusy(true, "正在创建文章…");
        Log("");

        try
        {
            var path = await PostCreator.CreateAsync(dialog.Title.Trim(), dialog.SelectedCategory.Name, dialog.SelectedCategory.Slug);
            Log($"已创建：{path}");
            Done(true, $"文章已创建\n\n{Path.GetFileName(path)}\n\n在 src\\content\\blog\\{dialog.SelectedCategory.Slug}\\ 下，填好内容后用「发布」推上去。");
        }
        catch (Exception ex)
        {
            Done(false, $"创建失败：{ex.Message}");
        }
        finally
        {
            SetBusy(false, "就绪");
        }
    }

    private async Task RunLqipAsync()
    {
        SetBusy(true, "正在生成图片占位图…");
        Log("");
        try
        {
            var code = await Project.RunPnpmAsync("generate:lqips", Log);
            Done(code == 0, code == 0
                ? "图片占位图已生成，发布后线上生效。"
                : $"图片更新失败（退出码 {code}），详情见日志。");
        }
        catch (Exception ex)
        {
            Done(false, $"图片更新失败：{ex.Message}");
        }
        finally
        {
            SetBusy(false, "就绪");
        }
    }

    private async Task RunPublishAsync()
    {
        SetBusy(true, "正在发布…");
        Log("");
        try
        {
            var result = await Publish.RunAsync(Log);
            Done(result.Ok, result.Message);
        }
        catch (Exception ex)
        {
            Done(false, $"发布失败：{ex.Message}");
        }
        finally
        {
            SetBusy(false, "就绪");
        }
    }

    private async Task RunRollbackAsync()
    {
        // 选提交交给独立对话框，主窗口保持简单。
        using var dialog = new RollbackDialog();
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        if (!dialog.Confirmed) return;

        SetBusy(true, "正在回滚…");
        Log("");
        try
        {
            var result = await Publish.RollbackAsync(dialog.Target!, Log);
            Done(result.Ok, result.Message);
        }
        catch (Exception ex)
        {
            Done(false, $"回滚失败：{ex.Message}");
        }
        finally
        {
            SetBusy(false, "就绪");
        }
    }

    /// <summary>填一段示例日志，供截图验证主窗口排版。</summary>
    public void SeedSampleLog()
    {
        Log("项目根：" + Project.Root);
        Log("");
        Log("▸ 构建");
        Log("> pnpm build");
        Log("18:42:07 [build] output: server/static");
        Log("18:42:31 [build] ✓ Completed in 24s");
        Log("");
        Log("▸ 单测");
        Log("> pnpm test:index");
        Log("26 个测试全部通过");
        Log("");
        Log("▸ 提交");
        Log("已提交：feat: 更新行情条 (10-10 15:20)");
        Log("");
        Log("▸ 推送");
        Log("✓ 发布成功，Cloudflare 正在重新构建");
        _status.Text = "就绪";
    }
}

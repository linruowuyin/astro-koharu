using BlogPublisher;
using System.Drawing;
using System.Windows.Forms;

namespace BlogPublisher.Gui;

/// <summary>
/// 图形版发布面板。
///
/// 与控制台版共用 Core 里的 Git / Publisher，行为完全一致，
/// 差别只在于用鼠标点选而不是方向键。
/// </summary>
internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        Git.RepoRoot = ResolveRepoRoot();
        ApplicationConfiguration.Initialize();

        // --shot <路径>：把窗体渲染成图片后退出，用于验证界面布局。
        // 不加这个参数就是正常交互模式。
        var shotIndex = Array.FindIndex(args, a => a == "--shot");
        if (shotIndex >= 0)
        {
            var target = shotIndex + 1 < args.Length ? args[shotIndex + 1] : "shot.png";
            var form = new MainForm();
            // 用 Shown 而不是 Load：Load 在窗体显示前触发，那一刻布局还没完成，
            // DrawToBitmap 画出来是空的。Shown 保证已经有真实尺寸。
            form.Shown += async (_, _) =>
            {
                // 等 RefreshAsync 把文件列表填上再截。
                for (var i = 0; i < 30; i++)
                {
                    await Task.Delay(100);
                    Application.DoEvents();
                }
                try
                {
                    using var bmp = new Bitmap(form.Width, form.Height);
                    form.DrawToBitmap(bmp, new Rectangle(0, 0, form.Width, form.Height));
                    bmp.Save(target, System.Drawing.Imaging.ImageFormat.Png);
                }
                catch (Exception ex)
                {
                    File.WriteAllText(target + ".err", ex.ToString());
                }
                form.Close();
                Application.Exit();
            };
            Application.Run(form);
            return;
        }

        Application.Run(new MainForm());
    }

    /// <summary>
    /// 定位仓库根目录。从 exe 所在位置逐级向上找同时含
    /// package.json 与 .git 的目录，这样 exe 放哪儿都能找到项目。
    /// </summary>
    private static string ResolveRepoRoot()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
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
        MessageBox.Show(
            $"找不到项目目录（需要同时有 package.json 和 .git）\n\n当前工作目录：{Directory.GetCurrentDirectory()}",
            "无法定位项目", MessageBoxButtons.OK, MessageBoxIcon.Error);
        Environment.Exit(1);
        return "";
    }
}

internal sealed class MainForm : Form
{
    // 深色主题，跟站点风格一致。
    private static readonly Color Bg = Color.FromArgb(15, 17, 21);
    private static readonly Color Card = Color.FromArgb(23, 26, 33);
    private static readonly Color Line = Color.FromArgb(37, 42, 52);
    private static readonly Color Fg = Color.FromArgb(230, 232, 235);
    private static readonly Color Dim = Color.FromArgb(139, 147, 161);
    private static readonly Color Accent = Color.FromArgb(59, 130, 246);
    private static readonly Color Good = Color.FromArgb(74, 222, 128);
    private static readonly Color Bad = Color.FromArgb(248, 113, 113);
    private static readonly Color Warn = Color.FromArgb(251, 191, 36);

    private readonly Label _branch = new();
    private readonly Label _remote = new();
    private readonly Label _count = new();
    private readonly CheckedListBox _files = new();
    private readonly TextBox _message = new();
    private readonly Button _publish = new();
    private readonly Button _refresh = new();
    private readonly Button _all = new();
    private readonly Button _none = new();
    private readonly ListBox _commits = new();
    private readonly TextBox _log = new();
    private readonly Panel _logPanel = new();
    private readonly Label _status = new();
    private readonly Button _rollback = new();
    private readonly Button _foldLog = new();

    private bool _busy;

    public MainForm()
    {
        Text = "博客管理面板";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(880, 620);
        Size = new Size(960, 720);
        BackColor = Bg;
        ForeColor = Fg;
        Font = new Font("Microsoft YaHei UI", 9F);

        BuildUi();

        // 状态是异步拉的，先禁用操作，等 Loaded 里刷新完再放开。
        _publish.Enabled = false;
        _rollback.Enabled = false;
        this.Load += async (_, _) => await RefreshAsync();
    }

    private void BuildUi()
    {
        // ---- 顶栏 ----
        var header = new Panel { Dock = DockStyle.Top, Height = 60, BackColor = Card, Padding = new Padding(16, 12, 16, 12) };
        var title = new Label
        {
            Text = "博客管理面板",
            Font = new Font("Microsoft YaHei UI", 13F, FontStyle.Bold),
            ForeColor = Fg,
            AutoSize = true,
            Location = new Point(16, 10),
        };
        _branch.Text = "读取中…";
        _branch.ForeColor = Dim;
        _branch.AutoSize = true;
        _branch.Location = new Point(18, 38);
        _remote.AutoSize = true;
        _remote.ForeColor = Dim;
        _remote.Location = new Point(190, 38);

        _refresh.Text = "刷新";
        StyleButton(_refresh, accent: false);
        _refresh.Size = new Size(72, 30);
        _refresh.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _refresh.Location = new Point(Width - 96, 14);
        header.Controls.AddRange([title, _branch, _remote, _refresh]);
        _refresh.Location = new Point(header.Width - 88, 14);
        header.Resize += (_, _) => _refresh.Left = header.ClientSize.Width - 88;
        Controls.Add(header);

        // 用 SplitContainer 而不是 Dock：Dock 的填充顺序在嵌套容器里很难推理，
        // 曾经导致发布卡片和右侧面板被完全挤出可视区。Split 显式给比例，
        // 行为可预期，用户还能拖分隔条。
        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Vertical,
            SplitterDistance = 600,
            FixedPanel = FixedPanel.Panel2,
        };

        // ---- 左侧：文件列表 + 发布 ----
        var left = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12, 6, 6, 6) };

        var pubCard = MakeCard("发布", out var pubBody, 150);
        pubCard.Dock = DockStyle.Bottom;
        var fileCard = MakeCard("待提交文件", out var fileBody, 300);
        fileCard.Dock = DockStyle.Fill;
        _count.ForeColor = Dim;
        _count.AutoSize = true;
        fileBody.Controls.Add(_count);
        _count.Location = new Point(96, 8);
        _files.CheckOnClick = true;
        // 顶部留 26px 给卡片标题；ListBox 直接铺满会让第一行盖住标题。
        _files.BorderStyle = BorderStyle.None;
        _files.BackColor = Bg;
        _files.ForeColor = Fg;
        _files.Font = new Font("Consolas", 9.5F);
        _files.IntegralHeight = false;
        _files.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        // 用 ItemCheck 而不是 ItemChecked：勾选时状态尚未落定，
        // ItemCheck 在状态改变前触发，用来更新按钮的可用性正好。
        _files.ItemCheck += (_, _) => BeginInvoke(UpdateButtons);
        fileBody.Resize += (_, _) =>
        {
            var inner = fileBody.ClientSize;
            _files.SetBounds(0, 26, Math.Max(50, inner.Width), Math.Max(50, inner.Height - 26));
        };
        fileBody.Controls.Add(_files);

        _all.Text = "全选";
        _none.Text = "全不选";
        StyleButton(_all, accent: false);
        StyleButton(_none, accent: false);
        _all.Size = _none.Size = new Size(66, 26);
        // 放在标题同一行（y=4），与 _count 并排。
        _all.Location = new Point(180, 4);
        _none.Location = new Point(252, 4);
        _all.Anchor = _none.Anchor = AnchorStyles.Top | AnchorStyles.Left;
        _all.Click += (_, _) => SetAll(true);
        _none.Click += (_, _) => SetAll(false);
        fileBody.Controls.AddRange([_all, _none]);

        _message.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        _message.Location = new Point(0, 28);
        _message.Height = 30;
        _message.BackColor = Bg;
        _message.ForeColor = Fg;
        _message.BorderStyle = BorderStyle.FixedSingle;
        _message.Font = new Font("Microsoft YaHei UI", 9.5F);
        _message.PlaceholderText = "提交信息，例如：feat: 新增两篇复盘笔记";
        pubBody.Resize += (_, _) =>
        {
            var w = Math.Max(120, pubBody.ClientSize.Width);
            _message.SetBounds(0, 28, w, 30);
            _publish.SetBounds(0, 66, 150, 34);
            _status.SetBounds(160, 62, Math.Max(80, w - 160), 44);
        };
        pubBody.Controls.Add(_message);

        _publish.Text = "构建 · 单测 · 推送";
        StyleButton(_publish, accent: true);
        _publish.Size = new Size(150, 34);
        _publish.Anchor = AnchorStyles.Top | AnchorStyles.Left;
        _publish.Font = new Font("Microsoft YaHei UI", 9.5F, FontStyle.Bold);
        _publish.Click += async (_, _) => await PublishAsync();
        pubBody.Controls.Add(_publish);

        _status.ForeColor = Dim;
        _status.AutoSize = false;
        _status.Size = new Size(400, 44);
        _status.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        pubBody.Controls.Add(_status);

        // Fill 的必须先加，Bottom 的后加，否则 Fill 会把 Bottom 挤没。
        left.Controls.Add(fileCard);
        left.Controls.Add(pubCard);

        // ---- 右侧：提交历史 ----
        var right = new Panel { Dock = DockStyle.Right, Width = 360, Padding = new Padding(6, 6, 12, 6) };
        var hisCard = MakeCard("提交历史", out var hisBody, 0);
        // 卡片要跟着面板一起拉伸：Dock=Fill + 高度写死会让内容超出后
        // 把底部按钮挤出可视区。
        hisCard.Dock = DockStyle.Fill;
        // 用 ListBox 而不是 ListView：ListView 的 Details 模式即使只留两列，
        // 列宽合计一旦超过容器就会出横向滚动条，把底部按钮顶出可视区。
        // ListBox 单列自动填满宽度，没有这个问题。
        _commits.BorderStyle = BorderStyle.None;
        _commits.BackColor = Bg;
        _commits.ForeColor = Fg;
        _commits.IntegralHeight = false;
        _commits.Font = new Font("Consolas", 9F);
        hisBody.Controls.Add(_commits);

        // 回滚按钮固定在底部，ListBox 用 Anchor 四边拉伸填满剩余空间。
        // 不用 Dock：Dock 与 MakeCard 的 Padding 叠加后会把 Button 挤出
        // 可视区，这里 Anchor 的行为更可预期。
        _rollback.Text = "回滚选中提交";
        StyleButton(_rollback, accent: false);
        _rollback.Size = new Size(150, 30);
        _rollback.Location = new Point(0, 0);
        _rollback.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
        _rollback.Click += async (_, _) => await RollbackAsync();
        hisBody.Controls.Add(_rollback);

        _commits.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        hisBody.Resize += (_, _) =>
        {
            var inner = hisBody.ClientSize;
            // 顶部留 26px 给卡片标题，否则列表第一行会盖住标题。
            _commits.SetBounds(0, 26, Math.Max(50, inner.Width), Math.Max(50, inner.Height - 26 - 34));
            _rollback.Location = new Point(0, Math.Max(0, inner.Height - 32));
        };
        right.Controls.Add(hisCard);

        // ---- 底部日志 ----
        _logPanel.Dock = DockStyle.Bottom;
        _logPanel.Height = 180;
        _logPanel.BackColor = Card;
        _logPanel.Padding = new Padding(12, 6, 12, 8);
        _logPanel.Visible = false;
        _foldLog.Text = "收起";
        StyleButton(_foldLog, accent: false);
        _foldLog.Size = new Size(60, 24);
        _foldLog.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _foldLog.Click += (_, _) =>
        {
            _logPanel.Visible = false;
            if (Parent is not null) Parent.PerformLayout();
        };
        var logTitle = new Label { Text = "执行日志", ForeColor = Fg, AutoSize = true, Location = new Point(16, 12) };
        _logPanel.Controls.AddRange([logTitle, _foldLog, _log]);
        _foldLog.Location = new Point(280, 8);
        _logPanel.Resize += (_, _) => _foldLog.Left = _logPanel.ClientSize.Width - 76;
        _log.Dock = DockStyle.Bottom;
        _log.Height = 130;
        _log.Multiline = true;
        _log.ReadOnly = true;
        _log.ScrollBars = ScrollBars.Vertical;
        _log.BackColor = Color.FromArgb(11, 13, 17);
        _log.ForeColor = Dim;
        _log.BorderStyle = BorderStyle.None;
        _log.Font = new Font("Consolas", 8.5F);

        split.Panel1.Controls.Add(left);
        split.Panel2.Controls.Add(right);

        // 上下分割：上半是主区，下半是日志。
        var splitV = new SplitContainer
        {
            Dock = DockStyle.Fill,
            Orientation = Orientation.Horizontal,
        };
        splitV.Panel1.Controls.Add(split);
        splitV.Panel2.Controls.Add(_logPanel);

        // 窗体 Loaded 时按实际尺寸设定分隔位置：构造期窗体还没布局，
        // 写死 SplitterDistance 会越界。
        this.Load += (_, _) =>
        {
            split.SplitterDistance = Math.Max(200, split.Width - 360);
            splitV.SplitterDistance = Math.Max(200, splitV.Height - 190);
        };

        Controls.Add(splitV);
    }

    /// <summary>造一个带标题的卡片容器，返回内容区。</summary>
    private static Panel MakeCard(string title, out Control body, int height)
    {
        var card = new Panel
        {
            Height = height,
            Dock = DockStyle.None,
            BackColor = Card,
            Padding = new Padding(12),
        };
        var label = new Label
        {
            Text = title,
            ForeColor = Fg,
            Font = new Font("Microsoft YaHei UI", 9.5F, FontStyle.Bold),
            AutoSize = true,
            Location = new Point(12, 10),
        };
        // 顶部留 30px 给标题，卡片内还要留 12px 边距。
        body = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(0, 30, 0, 0),
            BackColor = Card,
        };
        card.Controls.Add(body);
        card.Controls.Add(label);
        return card;
    }

    private static void StyleButton(Button b, bool accent)
    {
        b.FlatStyle = FlatStyle.Flat;
        b.FlatAppearance.BorderColor = accent ? Accent : Line;
        b.BackColor = accent ? Accent : Color.FromArgb(30, 34, 43);
        b.ForeColor = accent ? Color.White : Fg;
        b.Cursor = Cursors.Hand;
    }

    private void SetAll(bool value)
    {
        for (var i = 0; i < _files.Items.Count; i++) _files.SetItemChecked(i, value);
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        if (_busy) return;
        _publish.Enabled = _files.CheckedItems.Count > 0;
        // 索引 0 是当前线上，不能回滚到它。
        _rollback.Enabled = _commits.SelectedIndex > 0;
    }

    /// <summary>按显示宽度截断（中文算 2 列）。</summary>
    private static string Truncate(string text, int width)
    {
        var w = 0;
        var chars = text.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            w += chars[i] > 0x1100 ? 2 : 1;
            if (w > width) return new string(chars, 0, i) + "…";
        }
        return text;
    }

    private async Task RefreshAsync()
    {
        try
        {
            var state = await Git.GetStateAsync();
            _branch.Text = $"分支 {state.Branch}";
            _remote.Text = state.UpstreamMissing
                ? "上游未配置"
                : state.Diverged
                    ? "已分叉 · 已阻断"
                    : $"领先 {state.Ahead} / 落后 {state.Behind}";
            _remote.ForeColor = state.Diverged ? Bad : Dim;

            // 保留已勾选项，只丢弃已经不存在的路径。
            var previous = _files.CheckedItems.Cast<ChangedFile>().Select(f => f.Path).ToHashSet();
            _files.BeginUpdate();
            _files.Items.Clear();
            foreach (var f in state.Files)
            {
                // 用 ChangedFile 本身作为 item 并覆写 ToString：CheckedListBox
                // 只会调用 ToString() 取显示文本，record 的默认实现会把
                // Status/Path 等全部字段打印出来。
                _files.Items.Add(f, previous.Contains(f.Path));
            }
            _files.EndUpdate();
            _count.Text = state.Files.Count == 0 ? "（工作区干净）" : $"共 {state.Files.Count} 项";

            var commits = await Git.RecentCommitsAsync(10);
            _commits.BeginUpdate();
            _commits.Items.Clear();
            for (var i = 0; i < commits.Count; i++)
            {
                var c = commits[i];
                // 当前线上那条加 ← 标记；日期跟在标题后，宽度不够时
                // 鼠标悬停能看到完整信息（ListBox 无 ToolTip，
                // 但内容在 WinForms 里够长时仍可横向滚动查看）。
                var prefix = i == 0 ? "← " : "  ";
                _commits.Items.Add($"{prefix}{c.Short}  {Truncate(c.Subject, 26)}");
            }
            _commits.EndUpdate();

            _status.Text = state.Diverged
                ? "本地与远端已分叉，发布已阻断。请先 git rebase origin/main 或 git reset --hard origin/main"
                : state.Files.Count == 0
                    ? "工作区干净"
                    : "推送前会自动跑构建与单测";

            UpdateButtons();
        }
        catch (Exception ex)
        {
            _status.Text = $"读取状态失败：{ex.Message}";
            _status.ForeColor = Bad;
        }
    }

    private void AppendLog(string line)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => AppendLog(line));
            return;
        }
        _log.AppendText(line + Environment.NewLine);
        _log.SelectionStart = _log.TextLength;
        _log.ScrollToCaret();
        if (!_logPanel.Visible)
        {
            _logPanel.Visible = true;
            PerformLayout();
        }
    }

    private async Task PublishAsync()
    {
        var files = _files.CheckedItems.Cast<ChangedFile>().Select(f => f.Path).ToList();
        var message = _message.Text.Trim();
        if (files.Count == 0 || message.Length == 0) return;

        // 与 CLI 同样的前置校验，错误要在开跑之前给出来。
        var state = await Git.GetStateAsync();
        if (state.Diverged)
        {
            MessageBox.Show(this,
                $"本地与远端已分叉（领先 {state.Ahead} / 落后 {state.Behind}），已阻断。\n\n"
                + "git fetch && git rebase origin/main        保留本地提交\n"
                + "git fetch && git reset --hard origin/main   丢弃本地",
                "已分叉", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        var known = state.Files.Select(f => f.Path).ToHashSet();
        var unknown = files.Where(f => !known.Contains(f)).ToList();
        if (unknown.Count > 0)
        {
            MessageBox.Show(this, "以下文件已不在改动列表里：\n" + string.Join("\n", unknown),
                "文件已变化", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            await RefreshAsync();
            return;
        }

        var confirm = MessageBox.Show(this,
            $"提交 {files.Count} 个文件并推送？\n\n提交信息：{message}\n\n"
            + "推送前会先跑构建与单测，不通过会自动阻断。",
            "确认发布", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
        if (confirm != DialogResult.OK) return;

        SetBusy(true);
        _log.Clear();
        try
        {
            var result = await Publisher.PublishAsync(files, message, AppendLog);
            ShowResult(result.Ok, result.Message);
        }
        catch (Exception ex)
        {
            ShowResult(false, ex.Message);
        }
        finally
        {
            SetBusy(false);
            await RefreshAsync();
        }
    }

    private async Task RollbackAsync()
    {
        var index = _commits.SelectedIndex;
        // 索引 0 是当前线上，回滚到它没有意义。
        if (index <= 0) return;
        var commits = await Git.RecentCommitsAsync(10);
        if (index >= commits.Count) return;
        var target = commits[index];

        var state = await Git.GetStateAsync();
        if (state.Files.Count > 0)
        {
            MessageBox.Show(this,
                $"工作区还有 {state.Files.Count} 项未提交改动，回滚会丢失它们，已阻断。\n\n"
                + "先发布这些改动，或用 git stash 暂存。",
                "工作区不干净", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var confirm = MessageBox.Show(this,
            $"将把线上回滚到 {target.Short}（{target.Subject}）。\n\n确定继续？",
            "确认回滚", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning);
        if (confirm != DialogResult.OK) return;

        SetBusy(true);
        _log.Clear();
        try
        {
            var result = await Publisher.RollbackAsync(target.Short, AppendLog);
            ShowResult(result.Ok, result.Message);
        }
        finally
        {
            SetBusy(false);
            await RefreshAsync();
        }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        _publish.Enabled = !busy;
        _refresh.Enabled = !busy;
        _rollback.Enabled = !busy;
        _publish.Text = busy ? "执行中…" : "构建 · 单测 · 推送";
        Cursor = busy ? Cursors.WaitCursor : Cursors.Default;
    }

    private void ShowResult(bool ok, string message)
    {
        _status.Text = message.Split('\n')[0];
        _status.ForeColor = ok ? Good : Bad;
        MessageBox.Show(this, message, ok ? "完成" : "未完成",
            MessageBoxButtons.OK, ok ? MessageBoxIcon.Information : MessageBoxIcon.Error);
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        _commits.SelectedIndexChanged += (_, _) => UpdateButtons();
    }
}

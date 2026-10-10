using System.Drawing;
using System.Windows.Forms;

namespace BlogTool;

/// <summary>
/// 本地预览：起 astro dev、实时看输出、开浏览器、停服务。
///
/// 这个面板存在的理由是 dev server 是个**长驻进程**。主窗口是干完活就走的
/// 形态，「起服务 → 改文章 → 看效果 → 关掉」这条链用命令行要开好几个窗口，
/// 面板把它们收在一处，并且明确告诉用户服务还活着、什么时候起的、怎么关。
///
/// 关停这条尤其要紧：astro 会 spawn 一堆 node 子进程，杀父进程收不干净，
/// 端口就一直占着，下次起服务失败。DevServer 里按进程树杀就是为这个。
/// </summary>
internal sealed class DevDialog : Form
{
    private const int PadX = 20;

    private readonly DevServer _server = new();
    private readonly TextBox _log = new();
    private readonly Label _state = new();
    private readonly Label _hint = new();
    private readonly Button _start = null!;
    private readonly Button _stop = null!;
    private readonly Button _open = null!;
    private readonly Button _close = null!;

    public DevDialog()
    {
        Ui.StyleDialog(this, "本地预览", 900, 620);

        var title = Ui.Label("起一个 astro dev，改文章时不用每次重新构建", PadX, 18);
        title.ForeColor = Ui.Dim;

        _state.SetBounds(PadX, 46, 860, 28);
        _state.Font = new Font("Microsoft YaHei UI", 11F, FontStyle.Bold);
        _state.TextAlign = ContentAlignment.MiddleLeft;

        _hint.SetBounds(PadX, 76, 860, 24);
        _hint.ForeColor = Color.FromArgb(150, 158, 170);
        _hint.Font = new Font("Microsoft YaHei UI", 8.5F);

        BuildLog();

        var y = 566;
        _start = Ui.Button("启动服务", true, PadX, y, 140, 34);
        _start.Click += async (_, _) => await StartAsync();

        _stop = Ui.Button("停止", false, PadX + 152, y, 110, 34);
        _stop.ForeColor = Color.FromArgb(200, 60, 60);
        _stop.Click += (_, _) => StopServer();

        _open = Ui.Button("在浏览器打开", false, PadX + 274, y, 160, 34);
        _open.Click += (_, _) => OpenBrowser();

        _close = Ui.Button("关闭", false, 0, 0, 110, 34);
        _close.Click += (_, _) => Close();
        _close.Location = new Point(900 - PadX - _close.Width, y);

        Controls.AddRange([title, _state, _hint, _log, _start, _stop, _open, _close]);

        _server.OnLog += line => AppendLine(line);
        _server.OnStateChanged += () => PaintState();

        Load += async (_, _) =>
        {
            PaintState();
            AppendLine("提示：首次启动 astro 要做 vite 预构建，通常十几秒。");
            await DetectExistingAsync();
        };

        FormClosing += (_, _) => ConfirmClose();
    }

    private void BuildLog()
    {
        _log.Bounds = new Rectangle(PadX, 110, 860, 440);
        _log.Multiline = true;
        _log.ReadOnly = true;
        _log.ScrollBars = ScrollBars.Vertical;
        _log.BackColor = Color.FromArgb(252, 252, 253);
        _log.ForeColor = Color.FromArgb(70, 78, 88);
        _log.BorderStyle = BorderStyle.FixedSingle;
        _log.Font = new Font("Consolas", 9.5F);
        // 记事本式控件会把 Tab 当焦点跳走，改成插入制表符免得打字丢焦点
        _log.AcceptsTab = true;
    }

    private async Task DetectExistingAsync()
    {
        AppendLine("检查是否已经有 dev server 在跑…");
        var running = await DevServer.DetectRunningAsync();
        if (running is null)
        {
            AppendLine("没有。就绪，点「启动服务」。");
            return;
        }

        _server.Adopt(running);
        AppendLine($"发现已在运行：{running}（直接复用，不用再起一个）");
        PaintState();
    }

    private async Task StartAsync()
    {
        _start.Enabled = false;
        try
        {
            var ok = await _server.StartAsync(AppendLine);
            if (ok)
            {
                PaintState();
                // 起好了就直接开浏览器，少点一次
                OpenBrowser();
            }
            else
            {
                PaintState();
                MessageBox.Show(this, _server.Error.Length > 0 ? _server.Error : "启动失败，看下面的日志。",
                    "本地预览", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
        finally
        {
            _start.Enabled = true;
            PaintState();
        }
    }

    private void StopServer()
    {
        if (_server.Status == DevServer.State.Stopped) return;

        if (MessageBox.Show(this,
            "停止后端口会释放，本地预览就断了。\n\n确定停止？",
            "停止本地预览", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
            return;

        _start.Enabled = false;
        try
        {
            _server.Stop();
            PaintState();
        }
        finally
        {
            _start.Enabled = true;
        }
    }

    private void OpenBrowser()
    {
        if (_server.Url.Length == 0)
        {
            MessageBox.Show(this, "服务还没起来。", "本地预览", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        try
        {
            DevServer.OpenBrowser(_server.Url);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"打不开浏览器：{ex.Message}", "本地预览", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    /// <summary>
    /// 关窗口前必须把服务停掉。
    ///
    /// 面板关了但 node 还在后台跑着、端口还占着，是这类工具最容易留给
    /// 用户的暗坑——下次「启动服务」莫名其妙失败，还查不到原因。
    /// </summary>
    private void ConfirmClose()
    {
        if (_server.Status is DevServer.State.Running or DevServer.State.Starting)
        {
            var answer = MessageBox.Show(this,
                "本地预览还在跑。\n\n关掉这个窗口会一并停掉服务（否则端口会一直被占着）。\n\n继续关闭？",
                "关掉预览", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
            if (answer != DialogResult.OK)
            {
                // 取消关闭要把窗口留下重来一次，否则 Cancel 之后窗口已经关了
                return;
            }
        }
        _server.Dispose();
    }

    private void PaintState()
    {
        switch (_server.Status)
        {
            case DevServer.State.Running:
                _state.Text = "运行中";
                _state.ForeColor = Color.FromArgb(22, 140, 80);
                _hint.Text = _server.Url + "　·　关掉这个窗口会自动停掉服务";
                _start.Enabled = false;
                _stop.Enabled = true;
                _open.Enabled = true;
                break;

            case DevServer.State.Starting:
                _state.Text = "启动中…";
                _state.ForeColor = Color.FromArgb(214, 148, 26);
                _hint.Text = "astro 首次启动要做 vite 预构建，等一会儿";
                _start.Enabled = false;
                _stop.Enabled = true;
                _open.Enabled = false;
                break;

            case DevServer.State.Failed:
                _state.Text = "启动失败";
                _state.ForeColor = Color.FromArgb(200, 60, 60);
                _hint.Text = _server.Error.Length > 0 ? _server.Error : "看下面的日志";
                _start.Enabled = true;
                _stop.Enabled = false;
                _open.Enabled = false;
                break;

            default:
                _state.Text = "未启动";
                _state.ForeColor = Ui.Dim;
                _hint.Text = "点「启动服务」，或先看看是不是已经有服务在跑";
                _start.Enabled = true;
                _stop.Enabled = false;
                _open.Enabled = false;
                break;
        }

        // FlatStyle.Flat 的按钮禁用时几乎看不出变化——边框和底色都还在，
        // 用户会以为能点。主动把字压暗，禁用才看得见。
        _stop.ForeColor = _stop.Enabled ? Color.FromArgb(200, 60, 60) : Color.FromArgb(185, 190, 198);
        _open.ForeColor = _open.Enabled ? Ui.Fg : Color.FromArgb(185, 190, 198);
    }

    private void AppendLine(string line)
    {
        if (_log.IsDisposed) return;
        if (_log.InvokeRequired)
        {
            try { _log.BeginInvoke(() => AppendLine(line)); } catch { /* 窗口正在关，丢掉这行 */ }
            return;
        }
        _log.AppendText(line + Environment.NewLine);
    }
}
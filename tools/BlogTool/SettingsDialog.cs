using System.Drawing;
using System.Windows.Forms;

namespace BlogTool;

/// <summary>
/// GitHub 凭据设置。
///
/// 这是个安全相关的界面，规矩有三条：
///   1. token 输入框默认掩码，要手动点「显示」才看得见
///   2. token 只往 git 的 credential helper 送，本程序不落盘、不进日志
///   3. 界面上永远不显示完整 token，最多给个长度
/// </summary>
internal sealed class SettingsDialog : Form
{
    private const string DefaultHint =
        "推荐直接点「浏览器登录」，不用记任何步骤。\r\n" +
        "想手动填令牌的话，点令牌框右边的链接看说明。";

    private readonly TextBox _user = new();
    private readonly TextBox _token = new();
    private readonly CheckBox _reveal = new();
    private readonly Label _current = new();
    private readonly Label _hint = new();
    private readonly LinkLabel _helpLink = null!;
    private readonly Button _save = null!;
    private readonly Button _clear = null!;
    private readonly Button _login = null!;
    private readonly Button _close = null!;

    private CancellationTokenSource? _loginCts;
    private bool _loggingIn;

    public SettingsDialog()
    {
        Ui.StyleDialog(this, "GitHub 凭据", 620, 486);

        var title = new Label
        {
            Text = "推送时用来连接 GitHub 的账号与令牌",
            Location = new Point(24, 20),
            AutoSize = true,
            ForeColor = Ui.Dim,
        };

        // ---- 当前状态 ----
        var stateBox = new Panel
        {
            Bounds = new Rectangle(24, 50, 572, 96),
            BackColor = Color.FromArgb(248, 249, 251),
        };
        _current.SetBounds(14, 12, 544, 72);
        _current.ForeColor = Color.FromArgb(90, 98, 110);
        stateBox.Controls.Add(_current);

        // ---- 账号 ----
        var userLabel = Ui.Label("GitHub 用户名", 24, 166, bold: true);
        _user.SetBounds(24, 192, 572, 30);
        _user.Font = new Font("Microsoft YaHei UI", 10F);

        // ---- 令牌 ----
        var tokenLabel = Ui.Label("个人访问令牌（PAT）", 24, 242, bold: true);
        _token.SetBounds(24, 268, 460, 30);
        _token.Font = new Font("Consolas", 10F);
        _token.UseSystemPasswordChar = true;

        _reveal.Text = "显示";
        _reveal.SetBounds(496, 272, 100, 24);
        _reveal.FlatStyle = FlatStyle.Flat;
        _reveal.ForeColor = Ui.Dim;
        _reveal.BackColor = Color.FromArgb(238, 240, 244);
        _reveal.Cursor = Cursors.Hand;
        _reveal.CheckedChanged += (_, _) => _token.UseSystemPasswordChar = !_reveal.Checked;

        // 忘了怎么填令牌是常态，给两个明确入口：看步骤 / 直接打开生成页
        var help = new LinkLabel
        {
            Text = "不会填？看这里",
            Bounds = new Rectangle(24, 302, 110, 18),
            AutoSize = false,
            Font = new Font("Microsoft YaHei UI", 8.5F),
            LinkColor = Icons.Brand,
            ActiveLinkColor = Icons.Brand,
        };
        help.LinkClicked += (_, _) => ShowTokenGuide();
        _helpLink = help;

        var openPage = new LinkLabel
        {
            Text = "直接打开 GitHub 令牌页",
            Bounds = new Rectangle(140, 302, 200, 18),
            AutoSize = false,
            Font = new Font("Microsoft YaHei UI", 8.5F),
            LinkColor = Icons.Brand,
            ActiveLinkColor = Icons.Brand,
        };
        openPage.LinkClicked += (_, _) => OpenTokenPage();

        _hint.SetBounds(24, 330, 572, 34);
        _hint.ForeColor = Color.FromArgb(150, 158, 170);
        _hint.Font = new Font("Microsoft YaHei UI", 8.5F);
        _hint.Text = DefaultHint;

        // ---- 操作 ----
        // 「浏览器登录」是首选：不需要记任何步骤，也不用碰令牌。
        _login = Ui.Button("浏览器登录", true, 24, 412, 140, 34);
        _login.Click += async (_, _) => await LoginAsync();

        _save = Ui.Button("保存", false, 172, 412, 112, 34);
        _save.Click += async (_, _) => await SaveAsync();

        var test = Ui.Button("测试连接", false, 292, 412, 112, 34);
        test.Click += async (_, _) => await TestAsync();

        _clear = Ui.Button("清除", false, 412, 412, 112, 34);
        _clear.ForeColor = Color.FromArgb(200, 60, 60);
        _clear.Click += async (_, _) => await ClearAsync();

        _close = Ui.Button("关闭", false, 484, 412, 112, 34);
        _close.Click += (_, _) =>
        {
            if (_loggingIn) { _loginCts?.Cancel(); return; }
            DialogResult = DialogResult.Cancel;
            Close();
        };

        Controls.AddRange([title, stateBox, userLabel, _user, tokenLabel, _token, _reveal,
            help, openPage, _hint, _login, _save, test, _clear, _close]);
        CancelButton = _close;

        Load += async (_, _) => await LoadAsync();
    }

    /// <summary>手填令牌时的一次性照做说明。</summary>
    private void ShowTokenGuide()
    {
        MessageBox.Show(this,
            "如果你更想手动填令牌，按这四步来：\n\n" +
            "1. 浏览器打开 https://github.com/settings/tokens/new\n" +
            "   （这个地址可以点「浏览器登录」旁边的链接直接跳）\n\n" +
            "2. Note（备注）随便填，比如 blog-tool\n" +
            "   Expiration（有效期）选个日子，建议 90 天\n\n" +
            "3. 勾选权限 public_repo 就够了\n" +
            "   你的博客是公开仓库，不需要勾全权限\n\n" +
            "4. 滚到底点 Generate token，复制那串 gh 开头的字符\n" +
            "   粘到上面的令牌框，点「保存」\n\n" +
            "⚠ 令牌只在这一刻显示，关掉页面就再也看不到了，忘了就得重新生成。",
            "令牌怎么填", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private async Task LoadAsync()
    {
        var info = await Credentials.ReadAsync();
        _user.Text = info.Username;

        if (!info.Exists)
        {
            _current.Text = "当前：没有已保存的凭据\r\n推送时 git 会弹出登录框要求输入。";
        }
        else
        {
            _current.Text =
                $"当前：已保存 · 用户名 {info.Username} · 令牌 {info.PasswordLength} 位\r\n"
              + $"存储方式：credential.helper = {info.Helper}";
        }
        if (info.OriginUrl.Length > 0)
        {
            _current.Text += $"\r\n远端：{info.OriginUrl}";
        }

        // 令牌生成入口只在该让人去弄新令牌时才提示，平时是噪音
        if (!info.Exists) Say("当前没有凭据：点「浏览器登录」最快，或点下面的链接手动填令牌。", Bad: false);
    }

    /// <summary>
/// 浏览器登录：一键完成，不需要用户知道 PAT 是什么。
/// </summary>
private async Task LoginAsync()
    {
        // 重新登录会覆盖当前那份已经能用的凭据。宁可多问一句，
        // 也不要用一次误点击换来推送突然失败。
        var info = await Credentials.ReadAsync();
        if (info.Exists)
        {
            var ok = MessageBox.Show(this,
                $"当前已有一份可用的凭据（{info.Username}，{info.PasswordLength} 位）。\n\n" +
                "重新登录会用新的结果替换它。令牌本身没问题的话，不用重登。\n\n" +
                "仍要重新登录？",
                "覆盖现有凭据", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning);
            if (ok != DialogResult.OK) return;
        }

        _loginCts = new CancellationTokenSource();
        SetBusy(true, "正在启动 GitHub 登录…");
        _close.Text = "取消登录";

        try
        {
            var (ok, message) = await Credentials.BrowserLoginAsync(
                _user.Text, status => Say(status, Bad: false), _loginCts.Token);

            if (!ok)
            {
                Say(message, Bad: true);
                return;
            }

            await LoadAsync();

            // 登录完顺手验一次，省得用户自己再点一下「测试连接」
            var stored = await Credentials.GetStoredAsync();
            if (stored.Password.Length == 0)
            {
                Say("登录流程走完了，但凭据库里还是没东西。", Bad: true);
                return;
            }

            var (valid, detail) = await Credentials.TestAsync(stored.Username, stored.Password);
            Say(valid ? $"登录成功，{detail}" : $"凭据已保存，但验证没通过：{detail}", Bad: !valid);
        }
        catch (Exception ex)
        {
            Say($"登录失败：{ex.Message}", Bad: true);
        }
        finally
        {
            _loginCts.Dispose();
            _loginCts = null;
            SetBusy(false, "");
            _close.Text = "关闭";
        }
    }

    /// <summary>打开 GitHub 的令牌生成页。</summary>
    private void OpenTokenPage()
    {
        try
        {
            Credentials.OpenTokenPage();
            Say("已打开令牌生成页，照上面的步骤填完再粘回来。", Bad: false);
        }
        catch (Exception ex)
        {
            Say($"打不开浏览器：{ex.Message}", Bad: true);
        }
    }

    private bool ValidateInput(bool tokenRequired)
    {
        if (string.IsNullOrWhiteSpace(_user.Text))
        {
            Say("先填 GitHub 用户名。", Bad: true);
            _user.Focus();
            return false;
        }
        if (tokenRequired && string.IsNullOrWhiteSpace(_token.Text))
        {
            Say("要保存新凭据就得先填令牌。", Bad: true);
            _token.Focus();
            return false;
        }
        return true;
    }

    private async Task SaveAsync()
    {
        if (!ValidateInput(tokenRequired: true)) return;
        SetBusy(true, "正在保存…");
        try
        {
            await Credentials.SaveAsync(_user.Text.Trim(), _token.Text);
            _token.Clear();
            _reveal.Checked = false;
            await LoadAsync();
            Say("已保存。下次推送会自动使用它。", Bad: false);
        }
        catch (Exception ex)
        {
            Say($"保存失败：{ex.Message}", Bad: true);
        }
        finally
        {
            SetBusy(false, "");
        }
    }

    private async Task TestAsync()
    {
        // 令牌框留空表示「用已保存的那份」。凭据明明就在那儿，
        // 却因为框是空的就报「先填令牌」，等于白存了。
        var typed = _token.Text;
        if (!ValidateInput(tokenRequired: false)) return;

        SetBusy(true, "正在连接 GitHub…");
        try
        {
            var user = _user.Text.Trim();
            string token;

            if (!string.IsNullOrWhiteSpace(typed))
            {
                token = typed;
            }
            else
            {
                var stored = await Credentials.GetStoredAsync();
                if (stored.Password.Length == 0)
                {
                    Say("既没有已保存的凭据，令牌框也是空的。先填一个令牌。", Bad: true);
                    _token.Focus();
                    return;
                }
                user = string.IsNullOrEmpty(stored.Username) ? user : stored.Username;
                token = stored.Password;
            }

            var (ok, message) = await Credentials.TestAsync(user, token);
            Say(message, Bad: !ok);
        }
        catch (Exception ex)
        {
            Say($"测试失败：{ex.Message}", Bad: true);
        }
        finally
        {
            SetBusy(false, "");
        }
    }

    private async Task ClearAsync()
    {
        var confirm = MessageBox.Show(this,
            "清除后，下次推送 git 会重新弹窗要求输入账号和令牌。\n\n确定清除？",
            "清除凭据", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning);
        if (confirm != DialogResult.OK) return;

        SetBusy(true, "正在清除…");
        try
        {
            await Credentials.ClearAsync(_user.Text.Trim());
            _token.Clear();
            await LoadAsync();
            Say("已清除。", Bad: false);
        }
        catch (Exception ex)
        {
            Say($"清除失败：{ex.Message}", Bad: true);
        }
        finally
        {
            SetBusy(false, "");
        }
    }

    /// <summary>底部提示条。<paramref name="Bad"/> 为真表示出错。</summary>
    private void Say(string message, bool Bad)
    {
        _hint.Text = message;
        _hint.ForeColor = Bad ? Color.FromArgb(200, 60, 60) : Color.FromArgb(22, 150, 90);
    }

    private void SetBusy(bool busy, string status)
    {
        if (busy) Say(status, Bad: false);
        _loggingIn = busy;
        _login.Enabled = !busy;
        _save.Enabled = !busy;
        _clear.Enabled = !busy;
        _user.Enabled = !busy;
        _token.Enabled = !busy;
        _helpLink.Enabled = !busy;
        Cursor = busy ? Cursors.WaitCursor : Cursors.Default;
    }
}
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
    private readonly TextBox _user = new();
    private readonly TextBox _token = new();
    private readonly CheckBox _reveal = new();
    private readonly Label _current = new();
    private readonly Label _hint = new();
    private readonly Button _save = null!;
    private readonly Button _clear = null!;

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

        _hint.SetBounds(24, 306, 572, 40);
        _hint.ForeColor = Color.FromArgb(150, 158, 170);
        _hint.Font = new Font("Microsoft YaHei UI", 8.5F);
        _hint.Text = "令牌由 git 的凭据助手保存，本工具不留存明文。\r\n"
                   + "GitHub → Settings → Developer settings → Personal access tokens 生成。";

        // ---- 操作 ----
        _save = Ui.Button("保存", true, 236, 412, 112, 34);
        _save.Click += async (_, _) => await SaveAsync();

        var test = Ui.Button("测试连接", false, 356, 412, 112, 34);
        test.Click += async (_, _) => await TestAsync();

        _clear = Ui.Button("清除", false, 476, 412, 120, 34);
        _clear.ForeColor = Color.FromArgb(200, 60, 60);
        _clear.Click += async (_, _) => await ClearAsync();

        var close = Ui.Button("关闭", false, 24, 412, 112, 34);
        close.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };

        Controls.AddRange([title, stateBox, userLabel, _user, tokenLabel, _token, _reveal, _hint, close, test, _save, _clear]);
        CancelButton = close;

        Load += async (_, _) => await LoadAsync();
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
    }

    private bool ValidateInput()
    {
        if (string.IsNullOrWhiteSpace(_user.Text))
        {
            Say("先填 GitHub 用户名。", Bad: true);
            _user.Focus();
            return false;
        }
        if (string.IsNullOrWhiteSpace(_token.Text))
        {
            Say("先填个人访问令牌。", Bad: true);
            _token.Focus();
            return false;
        }
        return true;
    }

    private async Task SaveAsync()
    {
        if (!ValidateInput()) return;
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
        if (!ValidateInput()) return;
        SetBusy(true, "正在连接 GitHub…");
        try
        {
            var (ok, message) = await Credentials.TestAsync(_user.Text.Trim(), _token.Text);
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
        _save.Enabled = !busy;
        _clear.Enabled = !busy;
        _user.Enabled = !busy;
        _token.Enabled = !busy;
        Cursor = busy ? Cursors.WaitCursor : Cursors.Default;
    }
}
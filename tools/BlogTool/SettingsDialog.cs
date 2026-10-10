using System.Drawing;
using System.Windows.Forms;

namespace BlogTool;

/// <summary>
/// GitHub 凭据设置。
///
/// 设计上最要紧的一条：**状态正常时不要摆出一个半填的表单。**
///
/// 之前用户名预填、令牌留空，看着像「这页没填完」——可凭据明明是齐的，
/// 什么也不用填。空着的令牌框只是在说「密钥不显示」，却被读成「缺东西」。
/// 所以这里分成两种状态：
///   · 已配置 → 一张状态卡 + 操作按钮，手动表单整个收起来，对话框也跟着变矮
///   · 没配置 / 用户主动展开 → 才展开手动表单
///
/// 按钮不用固定坐标。每行按钮数量随状态变（没凭据时就没有「测试连接」「清除」），
/// 写死 x 迟早撞车——上一版正是这么让「清除」压在「关闭」上 40 像素的。
/// 现在改成按可见顺序流式排布，「关闭」固定贴右边。
///
/// 另外两条老规矩：令牌框默认掩码；令牌只交给 git 的凭据助手，
/// 本程序不落盘、不进日志。
/// </summary>
internal sealed class SettingsDialog : Form
{
    private const int PadX = 24;
    private const int ContentW = 632;
    private const int Gap = 8;
    private const int CardY = 50;
    private const int CardH = 190;
    private const int LinkY = 250;
    private const int RowH = 22;
    private const int RowStep = 26;
    private const int RowTop = 54;

    private readonly Panel _card = new();
    private readonly Label _cardIcon = new();
    private readonly Label _cardTitle = new();
    private readonly Label _cardNote = new();
    private readonly Label _cardFoot = new();
    // 这四个在 BuildCard / BuildManualForm 里填，不能是 readonly
    private Label[] _cardKeys = new Label[4];
    private Label[] _cardVals = new Label[4];
    // 这几个在 BuildManualForm / BuildButtons 里填，不能是 readonly
    private Label _userLabel = null!;
    private Label _tokenLabel = null!;
    private Button _login = null!;
    private Button _save = null!;
    private Button _test = null!;
    private Button _clear = null!;
    private Button _close = null!;

    private readonly LinkLabel _manualLink = new();
    private readonly TextBox _user = new();
    private readonly TextBox _token = new();
    private readonly CheckBox _reveal = new();
    private readonly LinkLabel _helpLink = new();
    private readonly LinkLabel _openPageLink = new();
    private readonly Label _hint = new();

    private CancellationTokenSource? _loginCts;
    private bool _loggingIn;
    private bool _manualOpen;
    private bool _loaded;

    // 各按钮该不该出现。这里存一份自己的真值，而不是回头去读 Visible。
    private bool _showSave;
    private bool _showTest;
    private bool _showClear;

    private CredentialInfo _info = new();

    public SettingsDialog()
    {
        Ui.StyleDialog(this, "GitHub 凭据", 680, 396);

        var title = new Label
        {
            Text = "推送时用来连接 GitHub 的登录凭据",
            Location = new Point(PadX, 20),
            AutoSize = true,
            ForeColor = Ui.Dim,
        };

        BuildCard();

        _manualLink.SetBounds(PadX, LinkY, 220, 18);
        _manualLink.AutoSize = false;
        _manualLink.Font = new Font("Microsoft YaHei UI", 8.5F);
        _manualLink.LinkColor = Icons.Brand;
        _manualLink.ActiveLinkColor = Icons.Brand;
        _manualLink.LinkClicked += (_, _) =>
        {
            _manualOpen = !_manualOpen;
            ApplyMode();
        };

        BuildManualForm();
        BuildButtons();

        Controls.AddRange([title, _card, _manualLink, _userLabel, _user,
            _tokenLabel, _token, _reveal, _helpLink, _openPageLink, _hint,
            _login, _save, _test, _clear, _close]);
        CancelButton = _close;

        // 构造函数末尾同步摆一次初始布局。
        //
        // Load 是 async 的，得连着跑几条 git 命令才回来，可窗口在这之前
        // 就已经显示了。不先摆一次，用户看到的不是「正在检查」，而是控件
        // 还停在创建时那套坐标上的半成品：按钮堆在左上角、卡片一片空白、
        // 手动表单整个露在外面。截图里那个怪样子就是这么来的。
        ApplyMode();

        Load += async (_, _) => await LoadAsync();
    }

    // ============ 状态卡 ============

    private void BuildCard()
    {
        _card.Bounds = new Rectangle(PadX, CardY, ContentW, CardH);
        _card.BackColor = Color.FromArgb(248, 249, 251);

        _cardIcon.SetBounds(20, 20, 28, 28);

        _cardTitle.SetBounds(58, 18, 556, 26);
        _cardTitle.Font = new Font("Microsoft YaHei UI", 12F, FontStyle.Bold);

        // 键值分两列而不是一整行带全角空格。比例字体里全角空格对不齐，
        // 键和值的边界会随内容长短漂，右侧看着参差不齐。
        for (var i = 0; i < 4; i++)
        {
            var y = RowTop + i * RowStep;
            _cardKeys[i] = new Label
            {
                Bounds = new Rectangle(58, y, 60, RowH),
                AutoSize = false,
                ForeColor = Color.FromArgb(140, 148, 158),
                Font = new Font("Microsoft YaHei UI", 9F),
            };
            _cardVals[i] = new Label
            {
                Bounds = new Rectangle(124, y, 490, RowH),
                AutoSize = false,
                ForeColor = Color.FromArgb(56, 62, 70),
                Font = new Font("Microsoft YaHei UI", 9F),
            };
            _card.Controls.Add(_cardKeys[i]);
            _card.Controls.Add(_cardVals[i]);
        }

        _cardNote.SetBounds(58, RowTop, 556, 112);
        _cardNote.AutoSize = false;
        _cardNote.ForeColor = Color.FromArgb(96, 104, 116);
        _cardNote.Font = new Font("Microsoft YaHei UI", 9F);

        _cardFoot.SetBounds(58, RowTop + 4 * RowStep + 8, 556, 20);
        _cardFoot.AutoSize = false;
        _cardFoot.ForeColor = Color.FromArgb(140, 148, 158);
        _cardFoot.Font = new Font("Microsoft YaHei UI", 8.5F);

        // 别漏挂：只设 Bounds 不 Add 到 _card，控件根本不会显示。
        // 上一版就是标题和对勾图标没挂上去，卡片顶上整整空了一截。
        _card.Controls.AddRange([_cardIcon, _cardTitle, _cardNote, _cardFoot]);
    }

    private void PaintCard()
    {
        if (!_loaded)
        {
            _card.BackColor = Color.FromArgb(248, 249, 251);
            SetIcon(null);
            _cardTitle.Text = "正在检查本机凭据…";
            _cardTitle.ForeColor = Ui.Dim;
            foreach (var k in _cardKeys) k.Visible = false;
            foreach (var v in _cardVals) v.Visible = false;
            _cardFoot.Visible = false;
            _cardNote.Visible = true;
            _cardNote.Text = "要读 git 的凭据助手和远端地址，等一下就好。";
            return;
        }

        if (_info.Exists)
        {
            _card.BackColor = Color.FromArgb(244, 250, 247);
            SetIcon(Icons.Check(28, Color.FromArgb(22, 150, 90)));
            _cardTitle.Text = "已配置，可以直接推送";
            _cardTitle.ForeColor = Color.FromArgb(22, 120, 75);

            SetRow(0, "账号", string.IsNullOrEmpty(_info.Username) ? "（未知）" : _info.Username);
            SetRow(1, "凭据", $"1 条 = 用户名 + 令牌，令牌 {_info.PasswordLength} 位");
            SetRow(2, "保存", FriendlyHelper(_info.Helper));
            SetRow(3, "推送", string.IsNullOrEmpty(_info.OriginUrl) ? "（没读到 origin）" : _info.OriginUrl);

            _cardNote.Visible = false;
            _cardFoot.Visible = true;
            _cardFoot.Text = "推送时 git 自动取用它，你不用在这里填任何东西。";
        }
        else
        {
            _card.BackColor = Color.FromArgb(252, 250, 244);
            SetIcon(Icons.Warn(28, Color.FromArgb(214, 148, 26)));
            _cardTitle.Text = "还没有配置凭据";
            _cardTitle.ForeColor = Color.FromArgb(172, 120, 18);

            foreach (var k in _cardKeys) k.Visible = false;
            foreach (var v in _cardVals) v.Visible = false;

            _cardFoot.Visible = false;
            _cardNote.Visible = true;
            _cardNote.Text =
                "没有它，推送时 git 会弹窗要求输入账号和令牌。\r\n\r\n" +
                "最快的办法是点下面的「浏览器登录」：浏览器打开 GitHub，\r\n" +
                "你在页面上点一下确认就行，不需要知道令牌是什么。\r\n\r\n" +
                "想自己填令牌也可以，表单已经展开在下面。";
        }

        _manualLink.Text = _manualOpen ? "收起手动设置" : "改为手动设置";
    }

    private void SetRow(int i, string key, string value)
    {
        _cardKeys[i].Visible = true;
        _cardVals[i].Visible = true;
        _cardKeys[i].Text = key;
        _cardVals[i].Text = value;
    }

    /// <summary>换图标时把上一张释放掉，否则每次刷新状态都漏一张位图。</summary>
    private void SetIcon(Bitmap? image)
    {
        var old = _cardIcon.Image;
        _cardIcon.Image = image;
        old?.Dispose();
    }

    private static string FriendlyHelper(string helper) => helper switch
    {
        "manager" => "Windows 凭据管理器（推荐）",
        "store" => "git 凭据文件（明文，不推荐）",
        "" or "(未配置)" => "默认位置",
        _ => helper,
    };

    // ============ 手动表单 ============

    private void BuildManualForm()
    {
        _userLabel = Ui.Label("GitHub 用户名", PadX, 284, bold: true);
        _user.SetBounds(PadX, 306, ContentW, 30);
        _user.Font = new Font("Microsoft YaHei UI", 10F);

        _tokenLabel = Ui.Label("个人访问令牌（PAT）", PadX, 346, bold: true);
        _token.SetBounds(PadX, 368, 496, 30);
        _token.Font = new Font("Consolas", 10F);
        _token.UseSystemPasswordChar = true;

        _reveal.Text = "显示令牌";
        _reveal.SetBounds(528, 372, 128, 24);
        _reveal.FlatStyle = FlatStyle.Flat;
        _reveal.ForeColor = Ui.Dim;
        _reveal.BackColor = Color.FromArgb(238, 240, 244);
        _reveal.Cursor = Cursors.Hand;
        _reveal.CheckedChanged += (_, _) => _token.UseSystemPasswordChar = !_reveal.Checked;

        _helpLink.Text = "不会填？看步骤";
        _helpLink.SetBounds(PadX, 406, 110, 18);
        StyleLink(_helpLink);
        _helpLink.LinkClicked += (_, _) => ShowTokenGuide();

        _openPageLink.Text = "直接打开 GitHub 令牌页";
        _openPageLink.SetBounds(140, 406, 200, 18);
        StyleLink(_openPageLink);
        _openPageLink.LinkClicked += (_, _) => OpenTokenPage();

        _hint.AutoSize = false;
        _hint.ForeColor = Color.FromArgb(150, 158, 170);
        _hint.Font = new Font("Microsoft YaHei UI", 8.5F);
    }

    private static void StyleLink(LinkLabel link)
    {
        link.AutoSize = false;
        link.Font = new Font("Microsoft YaHei UI", 8.5F);
        link.LinkColor = Icons.Brand;
        link.ActiveLinkColor = Icons.Brand;
    }

    // ============ 按钮 ============

    private void BuildButtons()
    {
        _login = Ui.Button("浏览器登录", true, 0, 0, 128, 34);
        _login.Click += async (_, _) => await LoginAsync();

        _save = Ui.Button("保存", false, 0, 0, 96, 34);
        _save.Click += async (_, _) => await SaveAsync();

        _test = Ui.Button("测试连接", false, 0, 0, 104, 34);
        _test.Click += async (_, _) => await TestAsync();

        _clear = Ui.Button("清除", false, 0, 0, 104, 34);
        _clear.ForeColor = Color.FromArgb(200, 60, 60);
        _clear.Click += async (_, _) => await ClearAsync();

        _close = Ui.Button("关闭", false, 0, 0, 120, 34);
        _close.Click += (_, _) =>
        {
            if (_loggingIn) { _loginCts?.Cancel(); return; }
            DialogResult = DialogResult.Cancel;
            Close();
        };
    }

    /// <summary>
    /// 截图用：强制展开手动表单。
    ///
    /// 展开态是按钮最多的时候（登录/保存/测试/清除/关闭 五个），
    /// 也正是上一版重叠出问题的那个状态。凭据明明是好的，默认永远截不到它，
    /// 所以留这一个口子——只改界面状态，不碰凭据。
    /// </summary>
    internal void ExpandManualForShot()
    {
        _manualOpen = true;
        PrefillUser();
        ApplyMode();
    }

    /// <summary>按当前状态决定可见性、高度和每个控件的位置。</summary>
    private void ApplyMode()
    {
        var has = _info.Exists;
        PaintCard();

        // 没读完之前一律按收起处理。读回来的结果九成是「已配置」，
        // 先展开再收回去，窗口会当着用户的面抖一下，比慢更难看。
        var showForm = _loaded && (_manualOpen || !has);
        _userLabel.Visible = _tokenLabel.Visible = showForm;
        _user.Visible = _token.Visible = _reveal.Visible = showForm;
        _helpLink.Visible = _openPageLink.Visible = showForm;

        _login.Text = has ? "重新登录" : "浏览器登录";
        _login.Enabled = _loaded;          // 没读清楚之前不让点，免得连着触发两条 git
        _showSave = showForm;
        _showTest = has;
        _showClear = has;
        _save.Visible = showForm;
        _test.Visible = has;
        _clear.Visible = has;

        LayoutFor(showForm);

        if (!_loaded) { Say("正在检查本机凭据…", Bad: false); return; }
        if (!has) Say("点「浏览器登录」最省事：浏览器打开 GitHub，你点一下确认就行。", Bad: false);
        else if (_manualOpen) Say("手动填入新令牌会覆盖当前那份，保存后立即生效。", Bad: false);
        else Say("平时不用管这里，推送会自动使用上面那份凭据。", Bad: false);
    }

    /// <summary>
    /// 全部坐标在这里算出来，不留任何硬编码 x。
    ///
    /// 收起时对话框变矮，展开时变高，省掉中间那一大片空白；
    /// 按钮按可见顺序排，「关闭」固定贴右边，所以按钮数量怎么变都不会重叠。
    ///
    /// 「该显示哪个」一律读 _showSave/_showTest/_showClear 这些字段，
    /// 不读控件的 Visible：窗口尚未显示时 Visible 的 getter 会因为父窗
    /// 不可见而返回 false，构造阶段拿它排位会一个都排不上，
    /// 按钮就全留在创建时的 (0,0) 堆在左上角。
    /// </summary>
    private void LayoutFor(bool showForm)
    {
        const int buttonH = 34;
        const int bottomPad = 24;

        ClientSize = new Size(680, showForm ? 546 : 396);

        if (showForm) _hint.SetBounds(PadX, 432, ContentW, 48);
        else _hint.SetBounds(PadX, 282, ContentW, 48);

        var y = ClientSize.Height - bottomPad - buttonH;
        var x = PadX;
        foreach (var (button, show) in new[]
        {
            (_login, true), (_save, _showSave), (_test, _showTest), (_clear, _showClear),
        })
        {
            if (!show) continue;
            button.Location = new Point(x, y);
            x += button.Width + Gap;
        }
        _close.Location = new Point(PadX + ContentW - _close.Width, y);
    }

    // ============ 数据 ============

    private async Task LoadAsync()
    {
        _info = await Credentials.ReadAsync();
        _loaded = true;
        PrefillUser();
        ApplyMode();
    }

    /// <summary>
    /// 手动表单里预填已知的用户名——用户多半只是想换令牌。
    /// 只在框是空的时候填，绝不覆盖已经输入的内容。
    /// </summary>
    private void PrefillUser()
    {
        if (string.IsNullOrWhiteSpace(_user.Text) && !string.IsNullOrEmpty(_info.Username))
            _user.Text = _info.Username;
    }

    private async Task LoginAsync()
    {
        // 重新登录会覆盖当前那份已经能用的凭据。这是破坏性操作，
        // 宁可多问一句，也不要一次误点击换来推送突然失败。
        if (_info.Exists)
        {
            var answer = MessageBox.Show(this,
                $"当前已有一份可用的凭据（{_info.Username}，{_info.PasswordLength} 位）。\n\n" +
                "重新登录会用新的结果替换它。令牌没问题的话，不用重登。\n\n" +
                "仍要重新登录？",
                "覆盖现有凭据", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning);
            if (answer != DialogResult.OK) return;
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

            _manualOpen = false;
            await LoadAsync();

            // 登录完顺手验一次，省得用户自己再点「测试连接」
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

    private async Task SaveAsync()
    {
        if (string.IsNullOrWhiteSpace(_user.Text))
        {
            Say("先填 GitHub 用户名。", Bad: true);
            _user.Focus();
            return;
        }
        if (string.IsNullOrWhiteSpace(_token.Text))
        {
            Say("要保存新凭据就得先填令牌。", Bad: true);
            _token.Focus();
            return;
        }

        SetBusy(true, "正在保存…");
        try
        {
            await Credentials.SaveAsync(_user.Text.Trim(), _token.Text);
            _token.Clear();
            _reveal.Checked = false;
            _manualOpen = false;
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
        SetBusy(true, "正在连接 GitHub…");
        try
        {
            // 令牌框留空表示「用已保存的那份」。凭据明明就在那儿，
            // 却因为框是空的就报「先填令牌」，等于白存了。
            var user = _user.Text.Trim();
            var token = _token.Text;

            if (string.IsNullOrWhiteSpace(token))
            {
                var stored = await Credentials.GetStoredAsync();
                if (stored.Password.Length == 0)
                {
                    Say("既没有已保存的凭据，令牌框也是空的。先点「浏览器登录」或填一个令牌。", Bad: true);
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
        var answer = MessageBox.Show(this,
            "清除后，下次推送 git 会重新弹窗要求登录。\n\n确定清除？",
            "清除凭据", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning);
        if (answer != DialogResult.OK) return;

        SetBusy(true, "正在清除…");
        try
        {
            await Credentials.ClearAsync(_info.Username);
            _token.Clear();
            await LoadAsync();
            Say("已清除。点「浏览器登录」可以重新建立。", Bad: false);
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

    /// <summary>手填令牌时的照做说明。</summary>
    private void ShowTokenGuide()
    {
        MessageBox.Show(this,
            "如果你更想手动填令牌，按这四步来：\n\n" +
            "1. 浏览器打开 https://github.com/settings/tokens/new\n" +
            "   （下面有链接可以直接跳）\n\n" +
            "2. Note（备注）随便填，比如 blog-tool\n" +
            "   Expiration（有效期）选个日子，建议 90 天\n\n" +
            "3. 勾选权限 public_repo 就够了\n" +
            "   你的博客是公开仓库，不需要勾全权限\n\n" +
            "4. 滚到底点 Generate token，复制那串字符，\n" +
            "   粘到上面的令牌框，点「保存」\n\n" +
            "令牌只在这一刻显示，关掉页面就再也看不到了，忘了就得重新生成。",
            "令牌怎么填", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void OpenTokenPage()
    {
        try
        {
            Credentials.OpenTokenPage();
            Say("已打开令牌生成页，按提示填完粘回来。", Bad: false);
        }
        catch (Exception ex)
        {
            Say($"打不开浏览器：{ex.Message}", Bad: true);
        }
    }

    private void Say(string message, bool Bad)
    {
        _hint.Text = message;
        _hint.ForeColor = Bad ? Color.FromArgb(200, 60, 60) : Color.FromArgb(150, 158, 170);
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
        _manualLink.Enabled = !busy;
        Cursor = busy ? Cursors.WaitCursor : Cursors.Default;
    }
}
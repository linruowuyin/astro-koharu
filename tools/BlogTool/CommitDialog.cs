using System.Drawing;
using System.Windows.Forms;

namespace BlogTool;

/// <summary>
/// 发布确认对话框：看清要提交什么，再写一句说明。
///
/// 把「文件清单」和「提交信息」放进同一个窗口，是为了让用户点一次确定就完事——
/// 旧 bat 的做法是先在控制台勾选、再手打信息，中间还要回车好几轮。
/// </summary>
internal sealed class CommitDialog : Form
{
    private readonly CheckedListBox _files = new();
    private readonly TextBox _message = new();
    private readonly Label _count = new();

    public string CommitMessage => _message.Text.Trim();

    /// <summary>用户最终勾选的路径。</summary>
    public IReadOnlyList<string> SelectedPaths { get; private set; } = [];

    public CommitDialog(IReadOnlyList<ChangedFile> files, string suggestedMessage)
    {
        Ui.StyleDialog(this, "确认发布", 660, 520);

        var tip = Ui.Label("勾掉不想提交的文件，然后写一句这次改了什么。", 24, 20);
        tip.ForeColor = Ui.Dim;

        _count.SetBounds(460, 18, 136, 22);
        _count.TextAlign = ContentAlignment.MiddleRight;
        _count.ForeColor = Ui.Dim;
        _count.Font = new Font("Microsoft YaHei UI", 8.5F);

        var all = Ui.Button("全选", false, 24, 48, 62, 26);
        var none = Ui.Button("全不选", false, 92, 48, 76, 26);
        all.Click += (_, _) => SetAll(true);
        none.Click += (_, _) => SetAll(false);

        _files.SetBounds(24, 84, 612, 268);
        _files.BorderStyle = BorderStyle.FixedSingle;
        _files.CheckOnClick = true;
        _files.IntegralHeight = false;
        _files.Font = new Font("Consolas", 9F);
        foreach (var f in files)
        {
            _files.Items.Add(f, true);
        }
        // 勾选状态一变就更新计数，否则用户不知道还剩几个。
        _files.ItemCheck += (_, _) => BeginInvoke(UpdateCount);
        UpdateCount();

        var msgLabel = Ui.Label("提交信息", 24, 362, bold: true);
        _message.SetBounds(24, 388, 612, 30);
        _message.Font = new Font("Microsoft YaHei UI", 10F);
        _message.Text = suggestedMessage;

        var note = Ui.Label("确认后会先跑构建和单测，通过才提交推送。", 24, 428);
        note.ForeColor = Ui.Dim;

        var ok = Ui.Button("发布", true, 524, 462, 112, 34);
        ok.Click += (_, _) =>
        {
            if (!ValidateInput()) return;
            // 在关窗前把勾选结果拍下来：CheckedListBox 的选中态
            // 只在对话框还活着的时候读得到。
            SelectedPaths = [.. _files.CheckedIndices.Cast<int>()
                .Select(i => ((ChangedFile)_files.Items[i]!).Path)];
            DialogResult = DialogResult.OK;
            Close();
        };
        var cancel = Ui.Button("取消", false, 404, 462, 112, 34);
        cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };

        Controls.AddRange([tip, _count, all, none, _files, msgLabel, _message, note, ok, cancel]);
        AcceptButton = ok;
        CancelButton = cancel;
    }

    private void SetAll(bool value)
    {
        for (var i = 0; i < _files.Items.Count; i++) _files.SetItemChecked(i, value);
    }

    private void UpdateCount()
    {
        _count.Text = $"已选 {_files.CheckedItems.Count} / {_files.Items.Count}";
    }

    private bool ValidateInput()
    {
        if (_files.CheckedItems.Count == 0)
        {
            MessageBox.Show(this, "一个文件都没勾，发布就没有内容了。", "确认发布",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return false;
        }
        if (CommitMessage.Length == 0)
        {
            MessageBox.Show(this, "写一句这次改了什么，方便以后翻历史。", "确认发布",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            _message.Focus();
            return false;
        }
        return true;
    }
}

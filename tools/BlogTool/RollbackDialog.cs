using System.Drawing;
using System.Windows.Forms;

namespace BlogTool;

/// <summary>回滚对话框：选一条历史提交，确认后执行。</summary>
internal sealed class RollbackDialog : Form
{
    private readonly ListBox _list = new();
    private readonly Label _detail = new();
    private readonly Button _ok;

    public string? Target { get; private set; }
    public bool Confirmed { get; private set; }

    public RollbackDialog()
    {
        Ui.StyleDialog(this, "回滚线上版本", 620, 440);

        var tip = new Label
        {
            Text = "选一个要退回去的版本。回滚会强制覆盖远端，只在确定线上有问题时用。",
            Location = new Point(24, 20),
            Size = new Size(572, 20),
            ForeColor = Ui.Dim,
        };

        _list.SetBounds(24, 48, 572, 280);
        _list.BorderStyle = BorderStyle.FixedSingle;
        _list.Font = new Font("Consolas", 9F);
        _list.IntegralHeight = false;
        _list.SelectedIndexChanged += (_, _) => OnSelectionChanged();

        _detail.SetBounds(24, 338, 572, 42);
        _detail.ForeColor = Ui.Dim;
        _detail.Font = new Font("Microsoft YaHei UI", 8.5F);

        _ok = Ui.Button("回滚到这里", true, 484, 384, 112, 34);
        _ok.Enabled = false;
        _ok.Click += (_, _) =>
        {
            if (_list.SelectedItem is not ListBoxItem item) return;
            Target = item.Tag;
            Confirmed = true;
            DialogResult = DialogResult.OK;
            Close();
        };
        var cancel = Ui.Button("取消", false, 364, 384, 112, 34);
        cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };

        Controls.AddRange([tip, _list, _detail, _ok, cancel]);
        CancelButton = cancel;

        Load += (_, _) => LoadCommits();
    }

    private async void LoadCommits()
    {
        try
        {
            var state = await Git.GetStateAsync();
            if (state.Files.Count > 0)
            {
                _detail.Text = $"⚠ 工作区还有 {state.Files.Count} 个未提交的改动，回滚会丢掉它们。";
                _detail.ForeColor = Color.FromArgb(200, 140, 30);
            }

            var commits = await Git.RecentCommitsAsync(12);
            // 第一条是当前线上，往后是可以退回去的目标。
            for (var i = 1; i < commits.Count; i++)
            {
                var c = commits[i];
                var item = new ListBoxItem
                {
                    Display = $"{c.Short}  {c.Subject}",
                    Tag = c.Short,
                    Date = c.Date,
                };
                _list.Items.Add(item);
            }
        }
        catch (Exception ex)
        {
            _detail.Text = $"读不到提交历史：{ex.Message}";
            _detail.ForeColor = Color.FromArgb(200, 60, 60);
        }
    }

    private void OnSelectionChanged()
    {
        var item = _list.SelectedItem as ListBoxItem;
        _ok.Enabled = item is not null;
        _detail.Text = item is null ? "" : $"{item.Tag}  {item.Date}\r\n将把线上退回到这个版本。";
    }

    private sealed class ListBoxItem
    {
        public string Display { get; init; } = "";
        public string Tag { get; init; } = "";
        public string Date { get; init; } = "";
        public override string ToString() => Display;
    }
}

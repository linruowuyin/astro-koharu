using System.Drawing;
using System.Windows.Forms;

namespace BlogTool;

/// <summary>
/// 新建文章对话框。
///
/// 原向导问九件事，这里问四件——标题、分类、描述、标签。后两个留空就不写进
/// frontmatter：62 篇文章里 description 只有 1 篇、tags 只有 14 篇出现过，
/// 所以做成可选；但既然偶尔会用到，放在对话框里比事后手填强。
/// </summary>
internal sealed class NewPostDialog : Form
{
    private const int PadX = 24;
    private const int ContentW = 652;

    private readonly TextBox _title = new();
    private readonly ComboBox _category = new();
    private readonly TextBox _tags = new();
    private readonly TextBox _description = new();
    private readonly Label _preview = new();
    private readonly System.Windows.Forms.Timer _debounce = new() { Interval = 350 };

    private string _pendingTitle = "";
    private CategoryInfo? _pendingCategory;
    private int _previewStamp;

    public string Title => _title.Text;
    public CategoryInfo SelectedCategory => (CategoryInfo)_category.SelectedItem!;
    public string Description => _description.Text;
    public List<string> Tags => PostCreator.ParseTags(_tags.Text);

    public NewPostDialog(IReadOnlyList<CategoryInfo> categories, string initialTitle = "")
    {
        Ui.StyleDialog(this, "新建文章", 700, 468);

        var titleLabel = Ui.Label("标题", PadX, 26, bold: true);
        _title.SetBounds(PadX, 50, ContentW, 30);
        _title.Font = new Font("Microsoft YaHei UI", 11F);
        // 回车即提交，省得再点一次按钮。
        _title.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter && ValidateInput()) { DialogResult = DialogResult.OK; Close(); }
        };
        // 标题一变就刷新下面的路径预览。
        _title.TextChanged += (_, _) => RefreshPreview();

        var catLabel = Ui.Label("分类", PadX, 96, bold: true);
        _category.SetBounds(PadX, 120, 320, 28);
        _category.DropDownStyle = ComboBoxStyle.DropDownList;
        _category.Items.AddRange([.. categories.Select(c => (object)c)]);
        _category.SelectedIndex = 0;
        _category.SelectedIndexChanged += (_, _) => RefreshPreview();

        var tagsLabel = Ui.Label("标签（可选）", PadX, 162, bold: true);
        _tags.SetBounds(PadX, 186, ContentW, 28);
        _tags.Font = new Font("Microsoft YaHei UI", 9.5F);
        _tags.PlaceholderText = "用顿号或逗号隔开，比如：白银、黄金、贵金属";

        var descLabel = Ui.Label("描述（可选）", PadX, 228, bold: true);
        _description.SetBounds(PadX, 252, ContentW, 50);
        _description.Multiline = true;
        _description.ScrollBars = ScrollBars.Vertical;
        _description.Font = new Font("Microsoft YaHei UI", 9.5F);

        _preview.SetBounds(PadX, 314, ContentW, 56);
        _preview.ForeColor = Color.FromArgb(120, 128, 140);
        _preview.Font = new Font("Consolas", 8.5F);
        _preview.Text = "";

        var tip = Ui.Label("链接与日期会自动生成；描述和标签留空就不写进 frontmatter。", PadX, 374);
        tip.ForeColor = Color.FromArgb(140, 148, 160);
        tip.AutoSize = false;
        tip.Size = new Size(ContentW, 20);

        var ok = Ui.Button("创建", true, 564, 410, 112, 34);
        ok.Click += (_, _) =>
        {
            if (!ValidateInput()) return;
            DialogResult = DialogResult.OK;
            Close();
        };
        var cancel = Ui.Button("取消", false, 444, 410, 112, 34);
        cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };

        Controls.AddRange([titleLabel, _title, catLabel, _category,
            tagsLabel, _tags, descLabel, _description, _preview, tip, ok, cancel]);
        AcceptButton = ok;
        CancelButton = cancel;

        _debounce.Tick += OnDebounceTick;
        if (initialTitle.Length > 0) _title.Text = initialTitle;
        _title.Select();
        RefreshPreview();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _debounce.Dispose();
        base.Dispose(disposing);
    }

    private bool ValidateInput()
    {
        if (string.IsNullOrWhiteSpace(_title.Text))
        {
            MessageBox.Show(this, "先填个标题。", "新建文章", MessageBoxButtons.OK, MessageBoxIcon.Information);
            _title.Focus();
            return false;
        }
        return true;
    }

    /// <summary>实时显示会生成的文件名和链接，让用户心里有数。</summary>
    private void RefreshPreview()
    {
        if (string.IsNullOrWhiteSpace(_title.Text) || _category.SelectedItem is not CategoryInfo cat)
        {
            _preview.Text = "";
            return;
        }

        var t = _title.Text.Trim();
        var path = $"文件：src\\content\\blog\\{cat.Slug}\\{t}.md";

        // 生成 slug 要起一次 node（约 0.15 秒），边打字边起会卡手，
        // 所以先显示文件名，链接延迟一会儿再补。
        _preview.Text = path + "\r\n链接：生成中…";
        _debounce.Stop();
        var stamp = ++_previewStamp;
        _debounce.Start();
        _pendingTitle = t;
        _pendingCategory = cat;
    }

    private async void OnDebounceTick(object? sender, EventArgs e)
    {
        _debounce.Stop();
        var t = _pendingTitle;
        var cat = _pendingCategory;
        var stamp = _previewStamp;
        if (cat is null) return;

        var link = await PostCreator.SlugifyAsync(t);

        // 等待期间用户又改了输入，丢弃这次过期结果。
        if (stamp != _previewStamp || IsDisposed) return;
        _preview.Text = $"文件：src\\content\\blog\\{cat.Slug}\\{t}.md\r\n链接：{link}";
    }
}
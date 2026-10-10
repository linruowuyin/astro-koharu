using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace BlogTool;

/// <summary>
/// 文章库：62 篇文章总得有个地方能翻、能搜、能一键打开。
///
/// 定位是「找得到、跳得过去」，不是编辑器。文章的增删改仍然交给
/// 新建文章和外部编辑器，工具不去碰正文——正文是这个博客最不能乱动的东西。
///
/// 列表用 ListView 而不是 DataGridView：只需要标题/分类/日期三列，
/// ListView 的 OwnerDraw 更好控制行高与配色，也不用拖一整套表格样式。
/// </summary>
internal sealed class PostsDialog : Form
{
    private const int PadX = 20;

    private readonly TextBox _search = new();
    private readonly CheckBox _searchBody = new();
    private readonly ListView _list = new();
    private readonly Label _summary = new();
    private readonly Label _detail = new();
    private readonly Button _open = null!;
    private readonly Button _copy = null!;
    private readonly Button _reveal = null!;
    private readonly Button _close = null!;

    private List<PostInfo> _all = [];
    private List<PostInfo> _shown = [];

    public PostsDialog()
    {
        Ui.StyleDialog(this, "文章库", 940, 640);

        var title = Ui.Label("按标题、分类、标签搜索；勾上「也搜正文」可以搜全文", PadX, 18);
        title.ForeColor = Ui.Dim;

        _search.SetBounds(PadX, 46, 560, 30);
        _search.Font = new Font("Microsoft YaHei UI", 10F);
        _search.TextChanged += (_, _) => RefreshList();

        _searchBody.Text = "也搜正文";
        _searchBody.SetBounds(PadX + 574, 50, 110, 24);
        _searchBody.FlatStyle = FlatStyle.Flat;
        _searchBody.ForeColor = Ui.Dim;
        _searchBody.Cursor = Cursors.Hand;
        _searchBody.CheckedChanged += (_, _) => RefreshList();

        _summary.SetBounds(PadX, 88, 900, 22);
        _summary.ForeColor = Color.FromArgb(140, 148, 160);
        _summary.Font = new Font("Microsoft YaHei UI", 8.5F);

        BuildList();

        _detail.SetBounds(PadX, 452, 900, 56);
        _detail.AutoSize = false;
        _detail.ForeColor = Color.FromArgb(96, 104, 116);
        _detail.Font = new Font("Microsoft YaHei UI", 9F);

        var y = 528;
        _open = Ui.Button("打开源文件", true, PadX, y, 150, 34);
        _open.Click += (_, _) => OpenSelected();

        _copy = Ui.Button("复制线上链接", false, PadX + 162, y, 150, 34);
        _copy.Click += (_, _) => CopyUrl();

        _reveal = Ui.Button("在资源管理器中显示", false, PadX + 324, y, 180, 34);
        _reveal.Click += (_, _) => RevealSelected();

        _close = Ui.Button("关闭", false, 0, 0, 110, 34);
        _close.Click += (_, _) => Close();
        _close.Location = new Point(940 - PadX - _close.Width, y);

        Controls.AddRange([title, _search, _searchBody, _summary, _list,
            _detail, _open, _copy, _reveal, _close]);
        CancelButton = _close;

        Load += (_, _) => LoadAll();
    }

    private void BuildList()
    {
        _list.Bounds = new Rectangle(PadX, 116, 900, 324);
        _list.View = View.Details;
        _list.FullRowSelect = true;
        _list.MultiSelect = false;
        _list.HideSelection = false;
        _list.BorderStyle = BorderStyle.FixedSingle;
        _list.Font = new Font("Microsoft YaHei UI", 9.5F);
        // 列宽合计刻意比列表可视宽度窄一截。顶满 900 会因为右边那根纵向
        // 滚动条多出十几像素，ListView 就自己加出一根横向滚动条，
        // 「标签」列被推到看不见的地方——列还在，只是够不着。
        _list.Columns.Add("标题", 430);
        _list.Columns.Add("分类", 90);
        _list.Columns.Add("发布", 130);
        _list.Columns.Add("标签", 180);
        _list.SelectedIndexChanged += (_, _) => ShowDetail();
        _list.DoubleClick += (_, _) => OpenSelected();
    }

    private void LoadAll()
    {
        _all = Posts.ReadAll();
        if (PostInfo.SiteUrl.Length == 0) PostInfo.SiteUrl = ReadSiteUrl();
        RefreshList();
    }

    private void RefreshList()
    {
        var picked = _list.SelectedItems.Count > 0 ? _list.SelectedItems[0].Tag as PostInfo : null;
        _shown = Posts.Search(_all, _search.Text, _searchBody.Checked);

        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var p in _shown)
        {
            var item = new ListViewItem(p.Title) { Tag = p };
            item.SubItems.Add(p.CategoryName.Length > 0 ? p.CategoryName : p.CategoryDir);
            item.SubItems.Add(p.Date == DateTime.MinValue ? "—" : p.Date.ToString("yyyy-MM-dd"));
            item.SubItems.Add(p.Tags.Count > 0 ? string.Join(' ', p.Tags.Take(4)) : "");
            if (p.Encrypted) item.SubItems[^1].Text = "🔒 加密";
            _list.Items.Add(item);
        }
        _list.EndUpdate();

        _summary.Text = _search.Text.Trim().Length > 0
            ? $"搜到 {_shown.Count} 篇 / 共 {_all.Count} 篇"
            : $"共 {_all.Count} 篇，其中 {_all.Count(p => p.Encrypted)} 篇加密";

        // 重新填完列表后把原来选中的那篇找回来，搜索框一个字一个字敲时不会跳掉
        if (picked is not null)
        {
            for (var i = 0; i < _list.Items.Count; i++)
            {
                if (_list.Items[i].Tag is PostInfo p && p.RelativePath == picked.RelativePath)
                {
                    _list.Items[i].Selected = true;
                    _list.EnsureVisible(i);
                    break;
                }
            }
        }
        // 没得挑就选中第一条：详情区空着会让这页看着像没加载出来，
        // 而且用户想复制第一篇链接时还得先点一下。
        else if (_list.Items.Count > 0)
        {
            _list.Items[0].Selected = true;
        }

        if (_list.Items.Count == 0) _detail.Text = "";
        else ShowDetail();
    }

    private PostInfo? Selected => _list.SelectedItems.Count > 0
        ? _list.SelectedItems[0].Tag as PostInfo
        : null;

    private void ShowDetail()
    {
        if (Selected is not { } p)
        {
            _detail.Text = "";
            return;
        }

        var parts = new List<string> { p.RelativePath };
        if (p.Tags.Count > 0) parts.Add("标签：" + string.Join('、', p.Tags));
        if (p.Cover.Length > 0) parts.Add("封面：" + p.Cover);
        if (p.Encrypted) parts.Add("（内容加密，源码是密文）");
        if (p.OnlineUrl.Length > 0) parts.Add(p.OnlineUrl);

        _detail.Text = string.Join("\r\n", parts);
    }

    private void OpenSelected()
    {
        if (Selected is not { } p)
        {
            MessageBox.Show(this, "先选一篇文章。", "文章库", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (!File.Exists(p.FullPath))
        {
            MessageBox.Show(this, $"文件不在了：\n{p.FullPath}", "文章库", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        try
        {
            // 交给系统默认关联程序。工具不内置编辑器：写文章的事
            // 该在真正的编辑器里干，工具只负责把人送到门口。
            Process.Start(new ProcessStartInfo { FileName = p.FullPath, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"打不开：{ex.Message}", "文章库", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void CopyUrl()
    {
        if (Selected is not { } p)
        {
            MessageBox.Show(this, "先选一篇文章。", "文章库", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        if (p.OnlineUrl.Length == 0)
        {
            MessageBox.Show(this,
                "这篇没有 frontmatter 的 link 字段，算不出线上地址。\n给它补一个 link 就行。",
                "没有链接", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        try
        {
            Clipboard.SetText(p.OnlineUrl);
            MessageBox.Show(this, $"已复制：\n{p.OnlineUrl}", "文章库", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"复制失败：{ex.Message}", "文章库", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void RevealSelected()
    {
        if (Selected is not { } p) return;
        try
        {
            // /select 会让资源管理器定位到文件并高亮，/ 只会打开所在目录
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{p.FullPath}\"",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"打不开资源管理器：{ex.Message}", "文章库", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    /// <summary>从 site.yaml 读站点地址，供拼线上链接用。</summary>
    private static string ReadSiteUrl()
    {
        var path = Project.At("config", "site.yaml");
        if (!File.Exists(path)) return "";
        foreach (var line in File.ReadAllLines(path))
        {
            var t = line.Trim();
            if (!t.StartsWith("url:")) continue;
            return t[4..].Trim().Trim('"', '\'');
        }
        return "";
    }
}
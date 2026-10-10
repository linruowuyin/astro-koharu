using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace BlogTool;

/// <summary>
/// 仓库状态条：分支、领先/落后、未提交改动数。
///
/// 之前这些信息只有点「发布」之后才看得到，想知道「现在能不能发」得先点一次
/// 才被告知。放到顶上一眼就能判断。
/// </summary>
internal sealed class RepoBar : Control
{
    private string _branch = "读取中…";
    private int _ahead, _behind;
    private int _changed;
    private bool _diverged;
    private bool _loaded;

    public event EventHandler? RefreshRequested;

    public RepoBar()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint
            | ControlStyles.UserPaint
            | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.ResizeRedraw, true);
        Height = 34;
        Cursor = Cursors.Hand;
    }

    /// <summary>把结果画上去。</summary>
    public void SetState(GitState state)
    {
        _branch = state.Branch;
        _ahead = state.Ahead;
        _behind = state.Behind;
        _changed = state.Files.Count;
        _diverged = state.Diverged;
        _loaded = true;
        Invalidate();
    }

    public void SetUnknown()
    {
        _branch = "读不到仓库状态";
        _loaded = false;
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button == MouseButtons.Left) RefreshRequested?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        var y = (Height - 26) / 2f;
        var x = 0f;
        x = DrawChip(g, x, y, _branch, Icons.Brand, filled: true);

        if (_diverged)
        {
            x = DrawChip(g, x, y, "历史已分叉", Icons.RollbackColor);
        }
        else if (_ahead > 0)
        {
            x = DrawChip(g, x, y, $"领先 {_ahead}", Icons.PublishColor);
        }

        if (_behind > 0)
        {
            x = DrawChip(g, x, y, $"落后 {_behind}", Icons.LqipColor);
        }

        if (_loaded && _changed > 0)
        {
            x = DrawChip(g, x, y, $"{_changed} 个未提交", Icons.LqipColor);
        }
        else if (_loaded)
        {
            DrawChip(g, x, y, "工作区干净", Icons.NewPostColor);
        }

        // 右侧的刷新提示
        using (var font = new Font("Microsoft YaHei UI", 8.5F))
        using (var brush = new SolidBrush(Color.FromArgb(160, 168, 178)))
        {
            const string hint = "点击刷新";
            var size = g.MeasureString(hint, font);
            g.DrawString(hint, font, brush, Width - size.Width - 2, (Height - size.Height) / 2f);
        }
    }

    private float DrawChip(Graphics g, float x, float y, string text, Color accent, bool filled = false)
    {
        using var font = new Font("Microsoft YaHei UI", 8.5F);
        var textSize = g.MeasureString(text, font);
        var box = new RectangleF(x, y, textSize.Width + 22, 26);

        using (var path = Icons.RoundedRect(box, 13f))
        {
            // 分支徽章用浅色底 + 主题色文字；其余用白底描边区分层级
            using var brush = new SolidBrush(filled ? Icons.Tint(accent, 0.88) : Color.White);
            g.FillPath(brush, path);
            if (!filled)
            {
                using var pen = new Pen(Icons.Tint(accent, 0.55), 1f);
                g.DrawPath(pen, path);
            }
        }

        using var textBrush = new SolidBrush(filled ? accent : Icons.Tint(accent, 0.05));
        g.DrawString(text, font, textBrush, x + 11, y + 5);

        return box.Right + 8;
    }
}

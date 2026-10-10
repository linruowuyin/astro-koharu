using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace BlogTool;

/// <summary>
/// 四个功能卡片。
///
/// 整个卡片自绘，不用一堆子控件拼：图标、标题、说明、快捷键角标全在
/// OnPaint 里画，位置和层级都是死的，不会再出现控件被挤走、
/// z-order 盖住之类的问题（Dock + Controls.Add 顺序踩过一次）。
/// </summary>
internal sealed class ActionCard : Control
{
    private readonly Bitmap _icon;
    private readonly Color _accent;
    private bool _hover;

    public string Title { get; }
    public string Description { get; }
    public string Shortcut { get; }
    public Action OnActivate { get; }

    public ActionCard(string title, string description, string shortcut, Bitmap icon, Color accent, Action onActivate)
    {
        Title = title;
        Description = description;
        Shortcut = shortcut;
        _icon = icon;
        _accent = accent;
        OnActivate = onActivate;

        SetStyle(ControlStyles.AllPaintingInWmPaint
            | ControlStyles.UserPaint
            | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.ResizeRedraw, true);
        BackColor = Color.White;
        Cursor = Cursors.Hand;
        TabStop = true;
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        _hover = true;
        Invalidate();
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hover = false;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnGotFocus(EventArgs e)
    {
        _hover = true;
        Invalidate();
        base.OnGotFocus(e);
    }

    protected override void OnLostFocus(EventArgs e)
    {
        _hover = false;
        Invalidate();
        base.OnLostFocus(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        Focus();
        base.OnMouseDown(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        // 鼠标在按钮上松开才算数，拖出去再松手不该触发
        if (Enabled && _hover && e.Button == MouseButtons.Left) OnActivate?.Invoke();
    }

    /// <summary>执行中置灰：整个画面按不透明度统一降一档。</summary>
    protected override void OnEnabledChanged(EventArgs e)
    {
        Invalidate();
        base.OnEnabledChanged(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        var rect = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
        using var path = Icons.RoundedRect(rect, 10f);

        // ---- 底色 ----
        Color bg;
        if (!Enabled) bg = Color.FromArgb(250, 250, 251);
        else if (_hover) bg = Icons.Tint(_accent, 0.96);
        else bg = Color.White;
        using (var brush = new SolidBrush(bg)) g.FillPath(brush, path);

        // ---- 描边 ----
        var border = !Enabled ? Color.FromArgb(232, 235, 239)
                    : _hover ? _accent
                    : Color.FromArgb(222, 226, 232);
        using (var pen = new Pen(border, _hover && Enabled ? 1.4f : 1f)) g.DrawPath(pen, path);

        // ---- 图标底座 ----
        // 底座要浅：图标线条本身就是主题色，底座太深会把图标吃掉
        var badge = new RectangleF(16, (Height - 42) / 2f, 42, 42);
        using var bpath = Icons.RoundedRect(badge, 12f);
        using (var brush = new SolidBrush(Icons.Tint(_accent, Enabled ? 0.87 : 0.92)))
        {
            g.FillPath(brush, bpath);
        }
        if (_hover && Enabled)
        {
            using var pen = new Pen(Icons.Tint(_accent, 0.55f), 1f);
            g.DrawPath(pen, bpath);
        }
        if (_icon != null) g.DrawImage(_icon, badge.X + 9, badge.Y + 9, 24, 24);

        // ---- 文字 ----
        var alpha = Enabled ? 1f : 0.45f;
        var textLeft = 70f;

        using (var titleFont = new Font("Microsoft YaHei UI", 11.5F, FontStyle.Bold))
        using (var brush = new SolidBrush(Fade(Color.FromArgb(30, 34, 40), alpha)))
        {
            g.DrawString(Title, titleFont, brush, textLeft, badge.Y + 1);
        }

        using (var descFont = new Font("Microsoft YaHei UI", 8.5F))
        using (var brush = new SolidBrush(Fade(Color.FromArgb(138, 146, 158), alpha)))
        {
            g.DrawString(Description, descFont, brush, textLeft, badge.Y + 25);
        }

        // ---- 快捷键角标 ----
        DrawShortcut(g, alpha);
    }

    private void DrawShortcut(Graphics g, float alpha)
    {
        var w = 22f;
        var box = new RectangleF(Width - w - 14, 14, w, 19);
        using var path = Icons.RoundedRect(box, 6f);
        using (var brush = new SolidBrush(Fade(Color.FromArgb(240, 242, 245), alpha)))
        {
            g.FillPath(brush, path);
        }
        using var font = new Font("Microsoft YaHei UI", 8F, FontStyle.Bold);
        using var text = new SolidBrush(Fade(Color.FromArgb(140, 148, 160), alpha));
        var size = g.MeasureString(Shortcut, font);
        g.DrawString(Shortcut, font, text, box.X + (w - size.Width) / 2f, box.Y + 3);
    }

    private static Color Fade(Color c, float alpha)
        => Color.FromArgb((int)(255 * alpha), c.R, c.G, c.B);

    protected override void Dispose(bool disposing)
    {
        if (disposing) _icon?.Dispose();
        base.Dispose(disposing);
    }
}

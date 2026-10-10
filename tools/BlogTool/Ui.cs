using System.Drawing;
using System.Windows.Forms;

namespace BlogTool;

/// <summary>三个对话框共用的配色与控件工厂。</summary>
internal static class Ui
{
    public static readonly Color Fg = Color.FromArgb(28, 32, 38);
    public static readonly Color Dim = Color.FromArgb(120, 128, 140);
    public static readonly Color Line = Color.FromArgb(216, 220, 226);
    public static readonly Color Accent = Color.FromArgb(22, 119, 255);

    public static Label Label(string text, int x, int y, bool bold = false)
    {
        var l = new Label
        {
            Text = text,
            Location = new Point(x, y),
            AutoSize = true,
            Font = new Font("Microsoft YaHei UI", bold ? 9.5F : 9F, bold ? FontStyle.Bold : FontStyle.Regular),
        };
        return l;
    }

    public static Button Button(string text, bool primary, int x, int y, int w, int h)
    {
        var b = new Button
        {
            Text = text,
            Bounds = new Rectangle(x, y, w, h),
            FlatStyle = FlatStyle.Flat,
            DialogResult = DialogResult.None,
            Cursor = Cursors.Hand,
            Font = new Font("Microsoft YaHei UI", 9.5F),
        };
        if (primary)
        {
            b.BackColor = Accent;
            b.ForeColor = Color.White;
            b.FlatAppearance.BorderColor = Accent;
        }
        else
        {
            b.BackColor = Color.White;
            b.ForeColor = Fg;
            b.FlatAppearance.BorderColor = Line;
        }
        return b;
    }

    /// <summary>对话框公共外观：白底、不可缩放、居中于父窗。</summary>
    public static void StyleDialog(Form f, string title, int width, int height)
    {
        f.Text = title;
        f.FormBorderStyle = FormBorderStyle.FixedDialog;
        f.StartPosition = FormStartPosition.CenterParent;
        f.ClientSize = new Size(width, height);
        f.MaximizeBox = false;
        f.MinimizeBox = false;
        f.BackColor = Color.White;
        f.ForeColor = Fg;
        f.Font = new Font("Microsoft YaHei UI", 9F);
        ApplyAppIcon(f);
    }

    /// <summary>给窗口套上应用图标，标题栏和任务栏才不是空白。</summary>
    public static void ApplyAppIcon(Form f)
    {
        try
        {
            f.Icon = Icons.ToIcon(Icons.App(32));
        }
        catch
        {
            // 图标画不出来不影响功能，别为此让程序起不来。
        }
    }
}

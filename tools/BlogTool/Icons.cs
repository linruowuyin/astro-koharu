using System.Drawing;
using System.Drawing.Drawing2D;

namespace BlogTool;

/// <summary>
/// 全部图标都用 GDI+ 现画，不带任何图片资源。
///
/// 这么做的理由：单文件 exe 之外再挂一堆 png/ico，既怕丢文件又要处理嵌入，
/// 而且位图在 125%/150% 缩放下会发糊。矢量按目标像素重新绘制，
/// 任何 DPI 下都是清晰的。
/// </summary>
internal static class Icons
{
    /// <summary>四个功能各自的主题色，卡片底色从这里派生。</summary>
    public static readonly Color NewPostColor = Color.FromArgb(22, 163, 116);   // 翠绿：新建
    public static readonly Color LqipColor = Color.FromArgb(217, 131, 26);     // 琥珀：媒体
    public static readonly Color PublishColor = Color.FromArgb(22, 119, 255);  // 蓝：发布
    public static readonly Color RollbackColor = Color.FromArgb(200, 72, 72);  // 红：危险操作

    // 浏览类功能的颜色刻意与四个动作卡区分开：那四张是「改动仓库」，
    // 这两个是「查看」，不该看着像又一个要执行的批次操作。
    public static readonly Color PostsColor = Color.FromArgb(124, 92, 200);    // 紫：文章库
    public static readonly Color PreviewColor = Color.FromArgb(20, 150, 140); // 青：本地预览

    public static readonly Color Brand = PublishColor;

    /// <summary>
    /// 按权重混色，weightOfA 是 a 的占比。
    ///
    /// 特意单独提供 <see cref="Tint"/>：调底色时几乎总是「往白色里掺」，
    /// 让调用方自己算 1-x 迟早会把 0.9 写成 0.1，图标就会糊在底色里看不见。
    /// </summary>
    public static Color Mix(Color a, Color b, double weightOfA)
        => Color.FromArgb(
            (int)Math.Round(a.R * weightOfA + b.R * (1 - weightOfA)),
            (int)Math.Round(a.G * weightOfA + b.G * (1 - weightOfA)),
            (int)Math.Round(a.B * weightOfA + b.B * (1 - weightOfA)));

    /// <summary>掺白。<paramref name="whiteAmount"/> 是白色占比，0.9 表示非常浅。</summary>
    public static Color Tint(Color c, double whiteAmount) => Mix(c, Color.White, 1 - whiteAmount);

    /// <summary>
    /// 画一个 <paramref name="logicalSize"/> 逻辑像素见方的图标。
    /// 图标内部统一按 24×24 网格作图，这里只负责缩放到实际像素并抗锯齿。
    /// </summary>
    public static Bitmap Render(Action<Graphics, float> draw, int logicalSize, Color color, float scale = 1f)
    {
        var px = Math.Max(8, (int)Math.Round(logicalSize * scale));
        var bmp = new Bitmap(px, px, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        var k = px / 24f;
        g.ScaleTransform(k, k);
        draw(g, k);
        return bmp;
    }

    private static Pen Stroke(Color c, float w) => new(c, w)
    {
        StartCap = LineCap.Round,
        EndCap = LineCap.Round,
        LineJoin = LineJoin.Round,
    };

    // ============ 四个功能图标 ============

    /// <summary>新建文章：一张带折角的纸，右下角一个加号。</summary>
    public static Bitmap NewPost(int size, float scale = 1f)
        => Render((g, _) =>
        {
            using var pen = Stroke(NewPostColor, 1.9f);
            // 纸张：右上角留出折角的缺口
            g.DrawLines(pen, new[]
            {
                new PointF(5, 2.5f), new PointF(13, 2.5f), new PointF(16.5f, 6),
                new PointF(16.5f, 13), new PointF(13, 16.5f), new PointF(5, 16.5f),
                new PointF(5, 2.5f),
            });
            // 折角
            g.DrawLines(pen, new[] { new PointF(13, 2.5f), new PointF(13, 6), new PointF(16.5f, 6) });
            // 正文两行
            g.DrawLine(pen, 7.6f, 9.5f, 12.4f, 9.5f);
            g.DrawLine(pen, 7.6f, 12.6f, 11.2f, 12.6f);
            // 加号
            using var plus = Stroke(NewPostColor, 2.3f);
            g.DrawLine(plus, 20f, 15.5f, 20f, 22f);
            g.DrawLine(plus, 16.75f, 18.75f, 23.25f, 18.75f);
        }, size, NewPostColor, scale);

    /// <summary>图片更新：相框 + 远山 + 太阳。</summary>
    public static Bitmap Image(int size, float scale = 1f)
        => Render((g, _) =>
        {
            using var pen = Stroke(LqipColor, 1.9f);
            g.DrawRectangle(pen, 3f, 4f, 18f, 16f);
            using var fill = new SolidBrush(LqipColor);
            g.FillEllipse(fill, 7f, 7.6f, 3.1f, 3.1f);
            // 山脊
            g.DrawLines(pen, new[]
            {
                new PointF(4.6f, 18.4f), new PointF(9.6f, 11.6f), new PointF(13.4f, 16.4f),
                new PointF(16.2f, 13.2f), new PointF(19.4f, 18.4f),
            });
        }, size, LqipColor, scale);

    /// <summary>发布：纸飞机。</summary>
    public static Bitmap Publish(int size, float scale = 1f)
        => Render((g, _) =>
        {
            using var pen = Stroke(PublishColor, 1.9f);
            g.DrawLines(pen, new[]
            {
                new PointF(2.6f, 11.3f), new PointF(21.6f, 2.6f),
                new PointF(14.4f, 21.4f), new PointF(11.2f, 14.2f), new PointF(2.6f, 11.3f),
            });
            g.DrawLine(pen, 11.2f, 14.2f, 21.6f, 2.6f);
        }, size, PublishColor, scale);

    /// <summary>回滚：一段逆时针回转箭头。</summary>
    public static Bitmap Rollback(int size, float scale = 1f)
        => Render((g, _) =>
        {
            using var pen = Stroke(RollbackColor, 2.0f);
            // 缺口留在左下，箭头指回起点
            g.DrawArc(pen, 4.4f, 4.4f, 15.2f, 15.2f, 118f, 255f);
            using var head = new GraphicsPath();
            head.AddPolygon(new[]
            {
                new PointF(4.6f, 12.6f), new PointF(9.4f, 13.2f), new PointF(6.0f, 17.2f),
            });
            using var fill = new SolidBrush(RollbackColor);
            g.FillPath(fill, head);
        }, size, RollbackColor, scale);

    /// <summary>日志区用的通用小图标（文档）。</summary>
    public static Bitmap Doc(int size, Color color, float scale = 1f)
        => Render((g, _) =>
        {
            using var pen = Stroke(color, 2f);
            g.DrawLines(pen, new[]
            {
                new PointF(6, 2.5f), new PointF(14, 2.5f), new PointF(18, 6.5f),
                new PointF(18, 21.5f), new PointF(6, 21.5f), new PointF(6, 2.5f),
            });
            g.DrawLines(pen, new[] { new PointF(14, 2.5f), new PointF(14, 6.5f), new PointF(18, 6.5f) });
        }, size, color, scale);

    /// <summary>齿轮：设置入口。</summary>
    public static Bitmap Gear(int size, Color color, float scale = 1f)
        => Render((g, _) =>
        {
            using var pen = Stroke(color, 1.9f);
            // 外圈八齿
            g.DrawArc(pen, 4.2f, 4.2f, 15.6f, 15.6f, 22.5f, 45f);
            g.DrawArc(pen, 4.2f, 4.2f, 15.6f, 15.6f, 112.5f, 45f);
            g.DrawArc(pen, 4.2f, 4.2f, 15.6f, 15.6f, 202.5f, 45f);
            g.DrawArc(pen, 4.2f, 4.2f, 15.6f, 15.6f, 292.5f, 45f);
            // 四个方向的齿
            foreach (var a in new[] { 0f, 90f, 180f, 270f })
            {
                var rad = a * MathF.PI / 180f;
                var dx = MathF.Cos(rad);
                var dy = MathF.Sin(rad);
                g.DrawLine(pen, 12f + dx * 7.2f, 12f + dy * 7.2f, 12f + dx * 10.4f, 12f + dy * 10.4f);
            }
            g.DrawEllipse(pen, 9.4f, 9.4f, 5.2f, 5.2f);
        }, size, color, scale);

    // ============ 状态图标 ============

    /// <summary>实心圆 + 白对勾：一切正常的状态。</summary>
    public static Bitmap Check(int size, Color color, float scale = 1f)
        => Render((g, _) =>
        {
            using var disc = new SolidBrush(color);
            g.FillEllipse(disc, 2f, 2f, 20f, 20f);
            using var pen = Stroke(Color.White, 2.4f);
            g.DrawLines(pen, new[]
            {
                new PointF(6.6f, 12.3f), new PointF(10.3f, 16f), new PointF(17.6f, 8.4f),
            });
        }, size, color, scale);

    /// <summary>实心三角 + 白感叹号：需要注意的状态。</summary>
    public static Bitmap Warn(int size, Color color, float scale = 1f)
        => Render((g, _) =>
        {
            using var tri = new SolidBrush(color);
            g.FillPolygon(tri, new[]
            {
                new PointF(12f, 2.2f), new PointF(23.2f, 21.2f), new PointF(0.8f, 21.2f),
            });
            using var pen = Stroke(Color.White, 2.2f);
            g.DrawLine(pen, 12f, 9.2f, 12f, 15f);
            using var dot = new SolidBrush(Color.White);
            g.FillEllipse(dot, 10.6f, 16.8f, 2.8f, 2.8f);
        }, size, color, scale);

    /// <summary>文章库：左边一列圆点，右边一列横线，就是个列表的样子。</summary>
    public static Bitmap Posts(int size, float scale = 1f)
        => Render((g, _) =>
        {
            using var pen = Stroke(PostsColor, 1.9f);
            using var dot = new SolidBrush(PostsColor);
            foreach (var y in new[] { 6.5f, 12f, 17.5f })
            {
                g.FillEllipse(dot, 3f, y - 1.1f, 2.2f, 2.2f);
                g.DrawLine(pen, 8.5f, y, 21f, y);
            }
        }, size, PostsColor, scale);

    /// <summary>本地预览：一个浏览器窗口，右上角带个播放三角。</summary>
    public static Bitmap Preview(int size, float scale = 1f)
        => Render((g, _) =>
        {
            using var pen = Stroke(PreviewColor, 1.9f);
            g.DrawRectangle(pen, 2.6f, 4f, 18.8f, 16f);
            // 标题栏分隔线
            g.DrawLine(pen, 2.6f, 8.4f, 21.4f, 8.4f);
            // 播放三角：预览就是「跑起来看」
            var tri = new[] { new PointF(10f, 11.4f), new PointF(10f, 18f), new PointF(16.4f, 14.7f) };
            using var fill = new SolidBrush(PreviewColor);
            g.FillPolygon(fill, tri);
        }, size, PreviewColor, scale);

    // ============ 应用图标 ============

    /// <summary>
    /// 应用图标：蓝色圆角方块 + 白色纸飞机。
    /// 窗口标题栏、任务栏、以及 --shot 截图里的窗口图标都用它。
    /// </summary>
    public static Bitmap App(int size)
    {
        var bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);

        var inset = size * 0.06f;
        var box = new RectangleF(inset, inset, size - inset * 2, size - inset * 2);
        var radius = size * 0.22f;

        // 竖向渐变，比纯色更有质感
        using (var path = RoundedRect(box, radius))
        using (var brush = new LinearGradientBrush(
            box, Color.FromArgb(38, 143, 255), Color.FromArgb(12, 82, 196), LinearGradientMode.Vertical))
        {
            g.FillPath(brush, path);
        }

        // 纸飞机
        var k = size / 24f;
        var pts = new[]
        {
            new PointF(6.4f, 11.7f), new PointF(17.8f, 6.2f),
            new PointF(13.1f, 18.0f), new PointF(10.9f, 13.3f), new PointF(6.4f, 11.7f),
        }
        .Select(p => new PointF(p.X * k, p.Y * k))
        .ToArray();
        using (var plane = new SolidBrush(Color.White))
        {
            g.FillPolygon(plane, pts);
        }
        using (var cut = new Pen(Color.FromArgb(12, 82, 196), Math.Max(1f, size * 0.035f)))
        {
            g.DrawLine(cut, pts[3], pts[1]);
        }
        return bmp;
    }

    /// <summary>把 Bitmap 包成 Win32 图标，窗口标题栏和任务栏要用。</summary>
    public static Icon ToIcon(Bitmap bmp)
    {
        var hIcon = bmp.GetHicon();
        try
        {
            using var tmp = Icon.FromHandle(hIcon);
            return (Icon)tmp.Clone();
        }
        finally
        {
            NativeDestroyIcon(hIcon);
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);
    private static void NativeDestroyIcon(IntPtr handle) => DestroyIcon(handle);

    public static GraphicsPath RoundedRect(RectangleF rect, float radius)
    {
        var path = new GraphicsPath();
        var d = radius * 2;
        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>
    /// 把应用图标打包成多尺寸 .ico，供 csproj 的 ApplicationIcon 嵌入 exe。
    ///
    /// 条目用 PNG 编码：Vista 以后都认，比手写 BMP 位图省事得多，
    /// 256×256 的 PNG 也就几 KB。
    /// </summary>
    public static void SaveIco(string path, params int[] sizes)
    {
        if (sizes.Length == 0) sizes = [16, 24, 32, 48, 64, 128, 256];

        var images = sizes
            .Select(s =>
            {
                using var bmp = App(s);
                using var ms = new MemoryStream();
                bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                return ms.ToArray();
            })
            .ToArray();

        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var w = new BinaryWriter(fs);

        w.Write((ushort)0);          // reserved
        w.Write((ushort)1);          // type: icon
        w.Write((ushort)sizes.Length);

        var offset = 6 + 16 * sizes.Length;
        for (var i = 0; i < sizes.Length; i++)
        {
            // 256 在目录项里必须写成 0，这是 ICO 格式的老规矩
            w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
            w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
            w.Write((byte)0);        // 调色板数量
            w.Write((byte)0);        // reserved
            w.Write((ushort)1);      // planes
            w.Write((ushort)32);     // bpp
            w.Write(images[i].Length);
            w.Write(offset);
            offset += images[i].Length;
        }
        foreach (var img in images) w.Write(img);
    }
}

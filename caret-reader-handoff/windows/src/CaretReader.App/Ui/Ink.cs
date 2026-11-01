using System.Drawing.Drawing2D;

namespace CaretReader.App.Ui;

/// <summary>
/// 界面风格：日式极简 + 漫画“印刷感”。
///   * 只用黑、白、灰，没有任何彩色。
///   * 1 像素黑色细线边框，纯色平涂，没有渐变、阴影；按钮和输入框带苹果式“连续曲率”圆角（用户 2026-10-13 要求）。
///   * 纸白底色上铺一层极淡的网点（screentone / 网点纸），像印刷品。
///   * 所有尺寸按 8 像素网格排布；字体优先 Microsoft YaHei UI，其次 Yu Gothic UI。
/// 所有颜色、字体、网点画刷都集中在这里，改风格只需要改这一个文件。
/// </summary>
internal static class Ink
{
    // ---------- 颜色（只有黑白灰） ----------
    public static readonly Color Paper = Color.FromArgb(250, 250, 250);       // 纸白（纯灰阶 R=G=B）
    public static readonly Color PaperPure = Color.White;
    public static readonly Color InkBlack = Color.FromArgb(17, 17, 17);       // 墨黑
    public static readonly Color Grey1 = Color.FromArgb(232, 232, 232);       // 很浅的灰：网点、分隔线
    public static readonly Color Grey2 = Color.FromArgb(200, 200, 200);       // 浅灰：禁用边框
    public static readonly Color Grey3 = Color.FromArgb(128, 128, 128);       // 中灰：次要文字
    public static readonly Color Grey4 = Color.FromArgb(72, 72, 72);          // 深灰

    // ---------- 字体 ----------
    private static string? _family;
    public static string FontFamilyName
    {
        get
        {
            if (_family != null) return _family;
            var installed = new HashSet<string>(FontFamily.Families.Select(f => f.Name), StringComparer.OrdinalIgnoreCase);
            foreach (var name in new[] { "Microsoft YaHei UI", "Yu Gothic UI", "Microsoft JhengHei UI", "Segoe UI" })
                if (installed.Contains(name)) return _family = name;
            return _family = SystemFonts.MessageBoxFont?.FontFamily.Name ?? "Segoe UI";
        }
    }

    private static Font? _ui, _small, _title;
    /// <summary>正文字体 9pt。</summary>
    public static Font UiFont => _ui ??= new Font(FontFamilyName, 9f * (float)Zoom, FontStyle.Regular, GraphicsUnit.Point);
    /// <summary>小字 8pt（状态栏、提示）。</summary>
    public static Font SmallFont => _small ??= new Font(FontFamilyName, 8f * (float)Zoom, FontStyle.Regular, GraphicsUnit.Point);
    /// <summary>分组标题 9pt 粗体。</summary>
    public static Font TitleFont => _title ??= new Font(FontFamilyName, 9f * (float)Zoom, FontStyle.Bold, GraphicsUnit.Point);

    // ---------- 网点画刷 ----------
    private static TextureBrush? _toneLight, _toneMedium;

    /// <summary>极淡的背景网点（铺满整个窗口背景）。</summary>
    public static Brush ToneLight => _toneLight ??= MakeTone(Paper, Color.FromArgb(226, 226, 226), 6);

    /// <summary>稍深的网点（按钮悬停时）。</summary>
    public static Brush ToneMedium => _toneMedium ??= MakeTone(PaperPure, Color.FromArgb(150, 150, 150), 4);

    /// <summary>
    /// 生成一个“网点”贴图：每 size 像素一个点，隔行错开半格，像传统印刷的网点纸。
    /// </summary>
    private static TextureBrush MakeTone(Color bg, Color dot, int size)
    {
        var bmp = new Bitmap(size * 2, size * 2);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(bg);
            bmp.SetPixel(0, 0, dot);
            bmp.SetPixel(size, size, dot);
        }
        return new TextureBrush(bmp, WrapMode.Tile);
    }

    // ---------- 绘制工具 ----------

    /// <summary>画 1 像素的矩形边框（完全在矩形内部，不会被裁掉）。</summary>
    public static void Border(Graphics g, Rectangle r, Color c)
    {
        using var pen = new Pen(c, 1);
        g.DrawRectangle(pen, r.X, r.Y, r.Width - 1, r.Height - 1);
    }

    /// <summary>
    /// 苹果式圆角矩形路径（“连续曲率”，比普通圆弧更柔和）：
    /// 每个角用一段三次贝塞尔曲线，从离角 1.28×半径 的地方开始弯，控制点贴近直边，
    /// 效果接近 iOS / macOS 的 squircle。
    /// </summary>
    public static GraphicsPath Round(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        float R = Math.Min(radius * 1.28f, Math.Min(r.Width, r.Height) / 2f);
        if (R < 1) { p.AddRectangle(r); return p; }
        float c = R * 0.36f; // 控制点离角的距离：越小越“方”（0.08 太尖，会出现台阶状的小方角），0.36 ≈ 苹果式柔和圆角
        float L = r.Left, T = r.Top, Rt = r.Right, B = r.Bottom;
        p.StartFigure();
        p.AddLine(L + R, T, Rt - R, T);
        p.AddBezier(Rt - R, T, Rt - c, T, Rt, T + c, Rt, T + R);
        p.AddLine(Rt, T + R, Rt, B - R);
        p.AddBezier(Rt, B - R, Rt, B - c, Rt - c, B, Rt - R, B);
        p.AddLine(Rt - R, B, L + R, B);
        p.AddBezier(L + R, B, L + c, B, L, B - c, L, B - R);
        p.AddLine(L, B - R, L, T + R);
        p.AddBezier(L, T + R, L, T + c, L + c, T, L + R, T);
        p.CloseFigure();
        return p;
    }

    /// <summary>按钮、下拉框的圆角半径（96 DPI 下 7 像素）。</summary>
    public static float Radius(Control c) => S(c, 7);

    /// <summary>
    /// 画一个圆角控件的底：先把父窗口的网点背景画进来（四个角露出的就是背景），
    /// 再用 fill 填圆角形状、用 border 颜色描 1px 边。fill 或 border 为 null 则跳过。
    /// </summary>
    public static void RoundBox(Control ctl, Graphics g, Brush? fill, Color? border, float radius)
    {
        ButtonRenderer.DrawParentBackground(g, ctl.ClientRectangle, ctl);
        var old = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        // GDI+ 默认像素中心在整数坐标上：1px 线画在 0 和 W-1 上才是实打实的一整像素；
        // 用 0.5 偏移会让右/下边落到控件外被裁掉（或在抗锯齿时变成半透明的灰边）。
        var rf = new RectangleF(0f, 0f, ctl.Width - 1f, ctl.Height - 1f);
        using (var path = Round(rf, radius))
        {
            if (fill != null) g.FillPath(fill, path);
            if (border != null) { using var pen = new Pen(border.Value, 1f); g.DrawPath(pen, path); }
        }
        g.SmoothingMode = old;
    }

    /// <summary>Win11 上让窗口本身也用系统圆角（Win10 不支持，调用会安静失败）。</summary>
    public static void RoundWindow(Form f)
    {
        try { int pref = 2; DwmSetWindowAttribute(f.Handle, 33, ref pref, sizeof(int)); } catch { }
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    /// <summary>按 DPI 缩放像素值（设计时以 96 DPI 为准，即 100% 缩放）。</summary>
    public static int S(Control c, int px) => (int)Math.Round(px * c.DeviceDpi / 96.0 * Zoom);

    /// <summary>大字模式倍数（1.0 或 1.5），启动时设置一次。</summary>
    public static double Zoom { get; set; } = 1.0;
}

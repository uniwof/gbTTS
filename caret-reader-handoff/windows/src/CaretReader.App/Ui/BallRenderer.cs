using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Reflection;
using System.Text.Json;
using CaretReader.Core;

namespace CaretReader.App.Ui;

/// <summary>
/// v0.6 悬浮球的画法：分层窗口（UpdateLayeredWindow，每个像素带透明度 → 圆形、边缘平滑）。
///
/// 资源占用设计（老电脑 4GB 内存）：
///   * 小人是预先画好的精灵图（sprites.png，嵌在 exe 里，约 50KB，10 帧）。启动时按屏幕缩放比例缩放一次，
///     之后每帧只是把一张约 60×60 的小图贴进一块常驻的位图（几十微秒）再交给系统合成。
///   * 只在画面真的变化时才重画；空闲时绝大部分时间是静止的（眨眼、翻页按随机间隔才动一下）。
///   * 不用 WPF / GPU / Live2D / 视频解码，内存增加约 1MB。
/// </summary>
internal sealed class BallRenderer : IDisposable
{
    public enum Look { Pet, Ball }

    private readonly Bitmap? _sheet;
    private readonly int _cell;
    private readonly Dictionary<string, int> _index = new(StringComparer.OrdinalIgnoreCase);
    private Bitmap[]? _scaled;        // 当前尺寸下的每一帧
    private int _size;                 // 球的直径（像素）
    private int _pad;                  // 球外面留给进度圈的边距

    // 常驻位图（DIB），直接在上面画，再交给 UpdateLayeredWindow
    private IntPtr _memDc, _dib, _oldBmp, _bits;
    private Bitmap? _canvas;
    private int _canvasW;

    public int WindowSize => _size + _pad * 2;

    public BallRenderer()
    {
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            using var png = asm.GetManifestResourceStream("sprites.png");
            using var json = asm.GetManifestResourceStream("sprites.json");
            if (png != null && json != null)
            {
                using var tmp = new Bitmap(png);
                _sheet = new Bitmap(tmp.Width, tmp.Height, PixelFormat.Format32bppPArgb);
                using (var g = Graphics.FromImage(_sheet)) g.DrawImage(tmp, 0, 0, tmp.Width, tmp.Height);
                using var doc = JsonDocument.Parse(json);
                _cell = doc.RootElement.GetProperty("cell").GetInt32();
                foreach (var p in doc.RootElement.GetProperty("frames").EnumerateObject()) _index[p.Name] = p.Value.GetInt32();
            }
        }
        catch (Exception ex) { Log.Write("读取小人精灵图失败，改用简单圆球: " + ex.Message); _sheet = null; }
    }

    public bool HasPet => _sheet != null;

    /// <summary>按直径准备好每一帧（换屏幕 / 改缩放时再调用一次）。</summary>
    public void EnsureSize(int diameter)
    {
        int pad = Math.Max(3, diameter / 14);
        if (diameter == _size && pad == _pad && _canvas != null) return;
        _size = diameter; _pad = pad;
        if (_scaled != null) foreach (var b in _scaled) b.Dispose();
        _scaled = null;
        if (_sheet != null)
        {
            int n = _sheet.Width / _cell;
            _scaled = new Bitmap[n];
            for (int i = 0; i < n; i++)
            {
                var b = new Bitmap(diameter, diameter, PixelFormat.Format32bppPArgb);
                using var g = Graphics.FromImage(b);
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.CompositingMode = CompositingMode.SourceCopy;
                g.DrawImage(_sheet, new Rectangle(0, 0, diameter, diameter), new Rectangle(i * _cell, 0, _cell, _cell), GraphicsUnit.Pixel);
                _scaled[i] = b;
            }
        }
        CreateCanvas(WindowSize);
    }

    public int FrameOf(string name) => _index.TryGetValue(name, out int i) ? i : 0;

    private void CreateCanvas(int w)
    {
        FreeCanvas();
        var bmi = new Native.BITMAPINFOHEADER
        {
            biSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.BITMAPINFOHEADER>(),
            biWidth = w, biHeight = -w,   // 负数 = 自上而下
            biPlanes = 1, biBitCount = 32,
        };
        IntPtr screen = Native.GetDC(IntPtr.Zero);
        _memDc = Native.CreateCompatibleDC(screen);
        _dib = Native.CreateDIBSection(screen, ref bmi, 0, out _bits, IntPtr.Zero, 0);
        Native.ReleaseDC(IntPtr.Zero, screen);
        _oldBmp = Native.SelectObject(_memDc, _dib);
        _canvas = new Bitmap(w, w, w * 4, PixelFormat.Format32bppPArgb, _bits);
        _canvasW = w;
    }

    private void FreeCanvas()
    {
        _canvas?.Dispose(); _canvas = null;
        if (_memDc != IntPtr.Zero)
        {
            Native.SelectObject(_memDc, _oldBmp);
            Native.DeleteObject(_dib);
            Native.DeleteDC(_memDc);
            _memDc = _dib = IntPtr.Zero;
        }
    }

    /// <summary>
    /// 画一帧并交给系统显示。frame = 小人帧号（简单圆球外观时忽略）；progress = 0~1 加载进度（null = 不显示）；
    /// badge = 右下角小标记（"pause" = 暂停，null = 无）；glyph = 简单圆球中间的图形。
    /// </summary>
    public void Present(IntPtr hwnd, Point screenPos, Look look, int frame, double? progress, string? badge, string glyph, bool hover)
    {
        if (_canvas == null) return;
        using (var g = Graphics.FromImage(_canvas))
        {
            g.CompositingMode = CompositingMode.SourceCopy;
            g.Clear(Color.Transparent);
            g.CompositingMode = CompositingMode.SourceOver;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var ball = new Rectangle(_pad, _pad, _size, _size);

            if (look == Look.Pet && _scaled != null && frame >= 0 && frame < _scaled.Length)
                g.DrawImageUnscaled(_scaled[frame], _pad, _pad);
            else
                DrawSimpleBall(g, ball, glyph);

            if (hover)   // 鼠标停在上面：外圈加一道细灰圈（告诉用户“可以点”）
            {
                using var hp = new Pen(Color.FromArgb(150, 128, 128, 128), Math.Max(1.5f, _pad * 0.45f));
                g.DrawEllipse(hp, _pad * 0.45f, _pad * 0.45f, _canvasW - _pad * 0.9f - 1, _canvasW - _pad * 0.9f - 1);
            }
            if (progress is double p)
            {
                float w = Math.Max(2f, _pad * 0.8f);
                var rr = new RectangleF(w / 2, w / 2, _canvasW - w - 1, _canvasW - w - 1);
                using var bg = new Pen(Color.FromArgb(200, 232, 232, 232), w);
                using var fg = new Pen(Ink.InkBlack, w) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                g.DrawEllipse(bg, rr);
                g.DrawArc(fg, rr, -90, (float)(360 * Math.Clamp(p, 0.02, 1)));
            }
            if (badge == "pause") DrawPauseBadge(g);
        }
        var size = new Native.SIZE { cx = _canvasW, cy = _canvasW };
        var dst = new Native.POINT { X = screenPos.X, Y = screenPos.Y };
        var src = new Native.POINT();
        var blend = new Native.BLENDFUNCTION { BlendOp = Native.AC_SRC_OVER, SourceConstantAlpha = 255, AlphaFormat = Native.AC_SRC_ALPHA };
        IntPtr screen = Native.GetDC(IntPtr.Zero);
        try { Native.UpdateLayeredWindow(hwnd, screen, ref dst, ref size, _memDc, ref src, 0, ref blend, Native.ULW_ALPHA); }
        finally { Native.ReleaseDC(IntPtr.Zero, screen); }
    }

    /// <summary>简单圆球：纸白圆 + 墨线圈 + 中间一个图形（光标 / 声波 / 暂停 / 睡眠）。完全静止。</summary>
    private void DrawSimpleBall(Graphics g, Rectangle r, string glyph)
    {
        using (var fill = new SolidBrush(Ink.PaperPure)) g.FillEllipse(fill, r);
        float lw = Math.Max(1.5f, r.Width * 0.034f);
        using (var pen = new Pen(Ink.InkBlack, lw)) g.DrawEllipse(pen, r.X + lw / 2, r.Y + lw / 2, r.Width - lw, r.Height - lw);
        float cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f, u = r.Width / 100f;
        using var ink = new SolidBrush(Ink.InkBlack);
        using var stroke = new Pen(Ink.InkBlack, 7 * u) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        switch (glyph)
        {
            case "wave":   // 朗读中：三道声波竖条
                foreach (var (dx, h) in new[] { (-16f, 18f), (0f, 34f), (16f, 24f) })
                    g.DrawLine(stroke, cx + dx * u, cy - h * u / 2, cx + dx * u, cy + h * u / 2);
                break;
            case "sleep":  // 加载中 / 没有音色：横线
                g.DrawLine(stroke, cx - 18 * u, cy, cx + 18 * u, cy);
                break;
            default:       // 就绪 / 暂停：文字光标 I
                g.DrawLine(stroke, cx, cy - 22 * u, cx, cy + 22 * u);
                g.DrawLine(stroke, cx - 10 * u, cy - 22 * u, cx + 10 * u, cy - 22 * u);
                g.DrawLine(stroke, cx - 10 * u, cy + 22 * u, cx + 10 * u, cy + 22 * u);
                break;
        }
    }

    /// <summary>右下角黑色小圆 + 白色“‖”：暂停中。</summary>
    private void DrawPauseBadge(Graphics g)
    {
        float d = _size * 0.36f;
        float x = _pad + _size - d * 0.92f, y = _pad + _size - d * 0.92f;
        using (var b = new SolidBrush(Ink.InkBlack)) g.FillEllipse(b, x, y, d, d);
        using (var w = new Pen(Color.White, Math.Max(1.2f, d * 0.06f))) g.DrawEllipse(w, x, y, d, d);
        float bw = d * 0.13f, bh = d * 0.42f, cy = y + d / 2;
        using var wb = new SolidBrush(Color.White);
        g.FillRectangle(wb, x + d * 0.33f - bw / 2, cy - bh / 2, bw, bh);
        g.FillRectangle(wb, x + d * 0.67f - bw / 2, cy - bh / 2, bw, bh);
    }

    public void Dispose()
    {
        FreeCanvas();
        if (_scaled != null) foreach (var b in _scaled) b.Dispose();
        _sheet?.Dispose();
    }
}

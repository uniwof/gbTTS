namespace CaretReader.App.Ui;
internal sealed class Bubble : Form
{
    private string _text = "";
    private readonly System.Windows.Forms.Timer _hide = new();
    private static Font? _font;
    private static Font BubbleFont => _font ??= new Font(Ink.FontFamilyName, 11f, FontStyle.Regular, GraphicsUnit.Point);
    public Bubble()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Color.White;
        DoubleBuffered = true;
        TopMost = true;
        _hide.Tick += (_, _) => { _hide.Stop(); HideNow(); };
    }
    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW | Native.WS_EX_TOPMOST | Native.WS_EX_TRANSPARENT;
            return cp;
        }
    }
    protected override bool ShowWithoutActivation => true;
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Native.WM_MOUSEACTIVATE) { m.Result = (IntPtr)Native.MA_NOACTIVATE; return; }
        base.WndProc(ref m);
    }
    public bool Showing => Visible && _text.Length > 0;
    public string CurrentText => _text;
    public void ShowNear(Rectangle anchor, string text, int ms)
    {
        _text = text;
        int pad = Ink.S(this, 12);
        int maxW = Ink.S(this, 380);
        var sz = TextRenderer.MeasureText(text, BubbleFont, new Size(maxW - pad * 2, 2000), TextFormatFlags.WordBreak);
        var size = new Size(Math.Min(maxW, sz.Width + pad * 2 + 2), sz.Height + pad * 2);
        var wa = Screen.FromRectangle(anchor).WorkingArea;
        int gap = Ink.S(this, 8);
        var pos = new Point(anchor.Left - gap - size.Width, anchor.Bottom - size.Height);
        if (pos.X < wa.Left) pos.X = anchor.Right + gap;
        if (pos.X + size.Width > wa.Right) { pos.X = Math.Max(wa.Left, anchor.Right - size.Width); pos.Y = anchor.Top - gap - size.Height; }
        if (pos.Y < wa.Top) pos.Y = anchor.Bottom + gap;
        pos.Y = Math.Clamp(pos.Y, wa.Top, Math.Max(wa.Top, wa.Bottom - size.Height));
        pos.X = Math.Clamp(pos.X, wa.Left, Math.Max(wa.Left, wa.Right - size.Width));
        bool sizeChanged = Size != size;
        Bounds = new Rectangle(pos, size);
        if (sizeChanged) Ink.RoundWindow(this);
        if (!Visible) Native.SetWindowPos(Handle, Native.HWND_TOPMOST, pos.X, pos.Y, size.Width, size.Height,
                                          Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
        Visible = true;
        Invalidate();
        _hide.Stop();
        if (ms > 0) { _hide.Interval = ms; _hide.Start(); }
    }
    public void HideNow()
    {
        _hide.Stop();
        _text = "";
        if (Visible) Hide();
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var r = ClientRectangle;
        g.Clear(Color.White);
        Ink.Border(g, r, Ink.InkBlack);
        int pad = Ink.S(this, 12);
        TextRenderer.DrawText(g, _text, BubbleFont, Rectangle.Inflate(r, -pad, -pad), Ink.InkBlack,
                              TextFormatFlags.WordBreak | TextFormatFlags.Left | TextFormatFlags.Top);
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) _hide.Dispose();
        base.Dispose(disposing);
    }
}

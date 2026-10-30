using CaretReader.Core;

namespace CaretReader.App.Ui;

/// <summary>
/// P0：悬浮球旁边的 ■ 停止按钮（文档 10.1）。
///   * 取文字 / 朗读 / 暂停时，从球后面用 120ms 滑到“远离屏幕边缘的一侧”；回到空闲就收起。
///   * 28dip 圆角方块，黑白灰 1px 细线，和悬浮球同一风格。
///   * WS_EX_NOACTIVATE + MA_NOACTIVATE：点它不会抢走文档的焦点、光标和选区。
///   * 按下立刻触发（不等松手），让“按 ■ 到静音”尽量快（验收：≤ 0.1 秒）。
/// </summary>
internal sealed class StopButton : Form
{
    private readonly System.Windows.Forms.Timer _slide = new() { Interval = 15 };
    private readonly System.Diagnostics.Stopwatch _clock = new();
    private Rectangle _ball, _target;
    private bool _hot;

    /// <summary>用户按下了 ■。</summary>
    public event Action? StopClicked;

    public StopButton()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Ink.PaperPure;
        DoubleBuffered = true;
        TopMost = true;
        Cursor = Cursors.Hand;
        _slide.Tick += (_, _) => SlideStep();
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW | Native.WS_EX_TOPMOST;
            cp.ExStyle &= ~Native.WS_EX_APPWINDOW;
            return cp;
        }
    }
    protected override bool ShowWithoutActivation => true;

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Native.WM_MOUSEACTIVATE) { m.Result = (IntPtr)Native.MA_NOACTIVATE; return; }
        base.WndProc(ref m);
    }

    public bool Showing => Visible;

    /// <summary>球在 ball（屏幕坐标）时，滑出或移动到正确位置。已经显示时只跟随，不重播动画。</summary>
    public void ShowBeside(Rectangle ball)
    {
        int size = Ink.S(this, BallLayout.StopButtonDip);
        var wa = Screen.FromRectangle(ball).WorkingArea;
        _ball = ball;
        _target = BallLayout.StopButtonRect(ball, wa, size, Ink.S(this, 6));
        if (Visible && !_slide.Enabled) { Bounds = _target; return; }
        if (Visible) return;   // 正在滑：目标已更新，下一帧会用新的
        var start = BallLayout.SlideAt(_ball, _target, 0);
        Bounds = new Rectangle(start, _target.Size);
        Ink.RoundWindow(this);
        Native.SetWindowPos(Handle, Native.HWND_TOPMOST, start.X, start.Y, size, size, Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
        Visible = true;
        _clock.Restart();
        _slide.Start();
    }

    private void SlideStep()
    {
        double t = _clock.Elapsed.TotalMilliseconds;
        Location = BallLayout.SlideAt(_ball, _target, t);
        if (t >= BallLayout.StopSlideMs) { _slide.Stop(); Location = _target.Location; }
    }

    public void HideNow()
    {
        _slide.Stop();
        _hot = false;
        if (Visible) Hide();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left) StopClicked?.Invoke();
    }

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hot = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hot = false; Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var r = ClientRectangle;
        g.Clear(_hot ? Ink.Grey1 : Ink.PaperPure);
        Ink.Border(g, r, Ink.InkBlack);
        int s = Math.Max(6, r.Width * 10 / 28);   // ■ 约占 10/28
        using var b = new SolidBrush(Ink.InkBlack);
        g.FillRectangle(b, r.Left + (r.Width - s) / 2, r.Top + (r.Height - s) / 2, s, s);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _slide.Dispose();
        base.Dispose(disposing);
    }
}

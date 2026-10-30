using CaretReader.Core;

namespace CaretReader.App.Ui;

/// <summary>
/// P0：悬停 0.8 秒展开的迷你工具条（文档 10.1）：■ 停止 | ⏮ | ⏭ | 音色名 ▾。
/// ⏮ ⏭ 的功能属于 P2，现在灰显不可点。WS_EX_NOACTIVATE，点它不抢文档焦点。
/// </summary>
internal sealed class MiniToolbar : Form
{
    private const int Cells = 4;
    private int _hotCell = -1;
    private string _voiceName = "音色";
    public event Action? StopClicked;
    public event Action? VoiceClicked;
    public event Action? PrevClicked;
    public event Action? NextClicked;
    private readonly System.Windows.Forms.Timer _tipTimer = new() { Interval = 800 };
    private readonly ToolTip _tip = new();
    private static readonly string[] Tips = { "停止朗读", "上一句", "下一句", "换音色" };

    public MiniToolbar()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Ink.PaperPure;
        DoubleBuffered = true;
        TopMost = true;
        _tipTimer.Tick += (_, _) => { _tipTimer.Stop(); if (_hotCell >= 0 && Visible) _tip.Show(Tips[_hotCell], this, 4, Height + 2, 2500); };
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

    public void SetVoiceName(string name) { _voiceName = string.IsNullOrWhiteSpace(name) ? "音色" : name; if (Visible) Invalidate(); }

    public void ShowBeside(Rectangle ball)
    {
        int h = Ink.S(this, 32), w = Ink.S(this, 28 * 3 + 120);
        var wa = Screen.FromRectangle(ball).WorkingArea;
        var r = BallLayout.ToolbarRect(ball, wa, w, h, Ink.S(this, 6));
        Bounds = r;
        Ink.RoundWindow(this);
        Native.SetWindowPos(Handle, Native.HWND_TOPMOST, r.X, r.Y, r.Width, r.Height, Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
        Visible = true;
    }

    public void HideNow() { _hotCell = -1; _tipTimer.Stop(); _tip.Hide(this); if (Visible) Hide(); }

    private Rectangle CellRect(int i)
    {
        int small = Ink.S(this, 28);
        return i < 3 ? new Rectangle(i * small, 0, small, Height) : new Rectangle(3 * small, 0, Width - 3 * small, Height);
    }
    private int CellAt(Point p) { for (int i = 0; i < Cells; i++) if (CellRect(i).Contains(p)) return i; return -1; }

    protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); int c = CellAt(e.Location); if (c != _hotCell) { _hotCell = c; _tip.Hide(this); _tipTimer.Stop(); if (c >= 0) _tipTimer.Start(); Invalidate(); } }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hotCell = -1; Invalidate(); }
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        int c = CellAt(e.Location);
        if (c == 0) StopClicked?.Invoke();
        else if (c == 3) VoiceClicked?.Invoke();
        else if (c == 1) PrevClicked?.Invoke();
        else if (c == 2) NextClicked?.Invoke();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Ink.PaperPure);
        string[] t = { "■", "⏮", "⏭", _voiceName + " ▾" };
        for (int i = 0; i < Cells; i++)
        {
            var r = CellRect(i);
            bool enabled = true;
            if (enabled && i == _hotCell) { using var b = new SolidBrush(Ink.Grey1); g.FillRectangle(b, r); }
            if (i > 0) { using var pen = new Pen(Ink.InkBlack); g.DrawLine(pen, r.Left, 3, r.Left, Height - 4); }
            using var br = new SolidBrush(enabled ? Ink.InkBlack : Color.FromArgb(170, 170, 170));
            var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter };
            g.DrawString(t[i], Ink.UiFont, br, r, sf);
        }
        Ink.Border(g, ClientRectangle, Ink.InkBlack);
    }
}

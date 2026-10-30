using System.ComponentModel;
using System.Drawing.Drawing2D;

namespace CaretReader.App.Ui;

// 本文件是几个“自己画”的小控件。都很简单：重写 OnPaint，用黑白灰画出来。
// 不用系统自带样式，是为了让 Win10 / Win11 上看起来完全一致，并符合“日式极简”风格。

/// <summary>
/// 平面按钮：1px 黑框 + 白底 + 苹果式圆角。
///   鼠标悬停 → 铺一层网点；按下 / “激活”状态 → 黑底白字（反白）；禁用 → 灰色。
/// </summary>
internal class InkButton : Control
{
    private bool _hover, _down, _active;

    /// <summary>“激活”状态（例如正在朗读时，开始按钮变成黑底白字的“停止”）。</summary>
    [DefaultValue(false)]
    public bool Active { get => _active; set { _active = value; Invalidate(); } }

    public InkButton()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        SetStyle(ControlStyles.Selectable, false); // 不抢键盘焦点
        Font = Ink.UiFont;
        Cursor = Cursors.Hand;
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; _down = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { _down = true; Invalidate(); } base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
    protected override void OnTextChanged(EventArgs e) { Invalidate(); base.OnTextChanged(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var r = ClientRectangle;
        float rad = Ink.Radius(this);
        Color fg;
        if (!Enabled)
        {
            Ink.RoundBox(this, g, Brushes.White, Ink.Grey2, rad);
            fg = Ink.Grey2;
        }
        else if (_down || _active)
        {
            using var b = new SolidBrush(Ink.InkBlack);
            Ink.RoundBox(this, g, b, Ink.InkBlack, rad);
            fg = Color.White;
        }
        else
        {
            Ink.RoundBox(this, g, _hover ? Ink.ToneMedium : Brushes.White, Ink.InkBlack, rad);
            fg = Ink.InkBlack;
        }
        TextRenderer.DrawText(g, Text, Font, r, fg,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
    }
}

/// <summary>
/// 平面下拉选择框：外观像按钮，左边是选中的文字，右边一个小三角。
/// 点击后弹出一个同样黑白风格的菜单。
/// （系统自带的 ComboBox 很难改成这种风格，所以自己做一个。）
///
/// v0.4：菜单里每个音色右边有一个小喇叭，点喇叭 = 试听（菜单不关闭、不切换音色）；点名字 = 选这个音色。
/// 鼠标停在音色上会显示它的一句话简介。
/// </summary>
internal sealed class InkDropDown : InkButton
{
    private readonly NoActivateMenu _menu = new() { Renderer = new InkMenuRenderer(), ShowImageMargin = false, ShowCheckMargin = false, ShowItemToolTips = true };
    private readonly List<object> _items = new();
    private int _selected = -1;
    private bool _speakerHit;   // 最近一次在菜单里按下鼠标时，是不是按在小喇叭上

    /// <summary>用户选了另一个选项时触发。</summary>
    public event EventHandler? SelectedIndexChanged;

    /// <summary>菜单弹出之前触发（主窗口借这个机会重新扫描「音色」文件夹，新放进去的音色不用重启就能出现）。</summary>
    public event EventHandler? Opening;

    /// <summary>点了第 index 项的小喇叭。</summary>
    public event Action<int>? SpeakerClicked;

    /// <summary>这一项有没有试听文件（没有时喇叭显示为灰色）。</summary>
    public Func<object, bool>? HasSample { get; set; }

    /// <summary>这一项是不是正在试听（喇叭反白显示）。</summary>
    public Func<object, bool>? IsPlaying { get; set; }

    /// <summary>鼠标停留时显示的说明。</summary>
    public Func<object, string?>? Tooltip { get; set; }

    public InkDropDown()
    {
        _menu.Font = Ink.UiFont;
        _menu.Padding = new Padding(0);
        // 点的是小喇叭：菜单保持打开，方便连续试听几个音色
        _menu.Closing += (_, e) =>
        {
            if (e.CloseReason == ToolStripDropDownCloseReason.ItemClicked && _speakerHit) e.Cancel = true;
        };
    }

    public IReadOnlyList<object> Items => _items;

    public bool IsMenuOpen => _menu.Visible;

    public void SetItems(IEnumerable<object> items)
    {
        _items.Clear();
        _items.AddRange(items);
        _selected = _items.Count > 0 ? 0 : -1;
        Text = _selected >= 0 ? _items[0].ToString() : "";
        if (_menu.Visible) _menu.Close();
    }

    public int SelectedIndex
    {
        get => _selected;
        set
        {
            if (value < -1 || value >= _items.Count) return;
            _selected = value;
            Text = value >= 0 ? _items[value].ToString() : "";
        }
    }

    public object? SelectedItem => _selected >= 0 ? _items[_selected] : null;

    /// <summary>试听状态变了：重画菜单里的喇叭。</summary>
    public void RefreshSpeakers()
    {
        if (!_menu.Visible) return;
        foreach (ToolStripItem it in _menu.Items) it.Invalidate();
    }

    protected override void OnClick(EventArgs e)
    {
        base.OnClick(e);
        if (!Enabled) return;
        if (_menu.Visible) { _menu.Close(); return; }
        if (!BuildMenu()) return;
        // 菜单出现在下拉框正下方，左对齐；留 4px 空隙，不压住下拉框的圆角底边
        _menu.Show(this, new Point(0, Height + Ink.S(this, 4)));
    }

    /// <summary>v0.6：不放在窗口里，直接在屏幕某处弹出音色菜单（悬浮球右键菜单里的「选择音色」）。</summary>
    public void ShowAt(Point screen)
    {
        if (_menu.Visible) { _menu.Close(); return; }
        if (!BuildMenu()) return;
        _menu.Show(screen);
    }

    /// <summary>音色菜单关闭时触发（悬浮球借此把焦点还给文档）。</summary>
    public event ToolStripDropDownClosedEventHandler? MenuClosed
    {
        add => _menu.Closed += value;
        remove => _menu.Closed -= value;
    }

    private bool BuildMenu()
    {
        Opening?.Invoke(this, EventArgs.Empty);
        if (_items.Count == 0) return false;
        _menu.Items.Clear();
        int width = Math.Max(Width, Ink.S(this, 296));
        for (int i = 0; i < _items.Count; i++)
        {
            int index = i;
            object item = _items[i];
            var it = new VoiceMenuItem(this, item, (i == _selected ? "■  " : "□  ") + item)
            {
                AutoSize = false,
                Width = width,
                Height = Ink.S(this, 36),
                Padding = new Padding(Ink.S(this, 8), 0, Ink.S(this, 8) + VoiceMenuItem.SpeakerWidth(this), 0),
                TextAlign = ContentAlignment.MiddleLeft,
                ToolTipText = Tooltip?.Invoke(item),
                AccessibleName = item + "（右侧喇叭：试听）",
            };
            it.MouseDown += (_, me) => _speakerHit = me.X >= it.Width - VoiceMenuItem.SpeakerWidth(this) - Ink.S(this, 8);
            it.Click += (_, _) =>
            {
                if (_speakerHit) { SpeakerClicked?.Invoke(index); it.Invalidate(); return; }
                if (index == _selected) return;
                SelectedIndex = index;
                SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
            };
            _menu.Items.Add(it);
        }
        _speakerHit = false;
        return true;
    }

    /// <summary>菜单里的一项：文字由 InkMenuRenderer 画，右边的小喇叭在这里画。</summary>
    private sealed class VoiceMenuItem : ToolStripMenuItem
    {
        private readonly InkDropDown _owner;
        private readonly object _item;

        public VoiceMenuItem(InkDropDown owner, object item, string text) : base(text) { _owner = owner; _item = item; }

        public static int SpeakerWidth(Control c) => Ink.S(c, 28);

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            bool has = _owner.HasSample?.Invoke(_item) ?? false;
            bool playing = _owner.IsPlaying?.Invoke(_item) ?? false;
            int w = SpeakerWidth(_owner);
            var area = new Rectangle(Width - w - Ink.S(_owner, 8), 0, w, Height);

            // 喇叭左边一条细竖线，和名字分开（同下拉框的小三角）
            using (var sep = new Pen(Selected ? Ink.Grey4 : Ink.Grey1))
                g.DrawLine(sep, area.X, Ink.S(_owner, 8), area.X, Height - Ink.S(_owner, 9));

            Color fg = Selected ? Color.White : Ink.InkBlack;
            if (!has) fg = Selected ? Ink.Grey3 : Ink.Grey2;
            int d = Ink.S(_owner, 22);
            var circle = new Rectangle(area.X + (area.Width - d) / 2 + Ink.S(_owner, 2), (Height - d) / 2, d, d);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            if (playing)
            {
                // 正在试听：喇叭反白（圆形底），一眼看出是哪一个在响
                using var b = new SolidBrush(Selected ? Color.White : Ink.InkBlack);
                g.FillEllipse(b, circle);
                fg = Selected ? Ink.InkBlack : Color.White;
            }
            DrawSpeaker(g, circle, fg, waves: has);
            g.SmoothingMode = SmoothingMode.None;
        }

        /// <summary>画一个小喇叭：方形喇叭口 + 梯形 + 两道声波弧线（没有试听文件时不画声波）。</summary>
        private void DrawSpeaker(Graphics g, Rectangle r, Color c, bool waves)
        {
            float u = r.Width / 22f;   // 22 个单位宽的小画布
            float x0 = r.X, y0 = r.Y;
            PointF P(float x, float y) => new(x0 + x * u, y0 + y * u);
            using var b = new SolidBrush(c);
            g.FillPolygon(b, new[] { P(5, 9), P(8, 9), P(12, 5.5f), P(12, 16.5f), P(8, 13), P(5, 13) });
            if (!waves) return;
            using var pen = new Pen(c, Math.Max(1f, 1.3f * u)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawArc(pen, x0 + 10.5f * u, y0 + 8f * u, 5f * u, 6f * u, -50, 100);
            g.DrawArc(pen, x0 + 10.5f * u, y0 + 5f * u, 8.5f * u, 12f * u, -50, 100);
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var r = ClientRectangle;
        bool en = Enabled;
        Ink.RoundBox(this, g, Brushes.White, en ? Ink.InkBlack : Ink.Grey2, Ink.Radius(this));
        Color fg = en ? Ink.InkBlack : Ink.Grey2;

        int pad = Ink.S(this, 8);
        int arrowW = Ink.S(this, 8);
        var textRect = new Rectangle(r.X + pad, r.Y, r.Width - pad * 3 - arrowW, r.Height);
        TextRenderer.DrawText(g, Text, Font, textRect, fg,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);

        // 小三角 ▼（实心，黑色）
        int cx = r.Right - pad - arrowW / 2, cy = r.Y + r.Height / 2;
        int h = Math.Max(3, arrowW / 2);
        var pts = new[] { new Point(cx - arrowW / 2, cy - h / 2), new Point(cx + arrowW / 2, cy - h / 2), new Point(cx, cy + h / 2 + 1) };
        using var b = new SolidBrush(fg);
        g.SmoothingMode = SmoothingMode.None;
        g.FillPolygon(b, pts);

        // 三角左边一条细竖线，把文字区和箭头区分开
        using var pen = new Pen(en ? Ink.Grey2 : Ink.Grey1);
        int lx = r.Right - pad * 2 - arrowW;
        g.DrawLine(pen, lx, r.Y + Ink.S(this, 8), lx, r.Bottom - Ink.S(this, 9));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _menu.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>弹出菜单的画法：白底、1px 黑框、悬停项黑底白字。</summary>
internal sealed class InkMenuRenderer : ToolStripRenderer
{
    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        => e.Graphics.Clear(Color.White);

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        => Ink.Border(e.Graphics, new Rectangle(Point.Empty, e.ToolStrip.Size), Ink.InkBlack);

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        var r = new Rectangle(1, 0, e.Item.Width - 2, e.Item.Height);
        if (e.Item.Selected)
        {
            using var b = new SolidBrush(Ink.InkBlack);
            e.Graphics.FillRectangle(b, r);
        }
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Selected ? Color.White : Ink.InkBlack;
        e.TextFormat |= TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis;
        base.OnRenderItemText(e);
    }
}

/// <summary>
/// 细长的进度条：1px 黑框，已完成部分用黑色平涂。
/// </summary>
internal sealed class InkProgressBar : Control
{
    private double _value;

    /// <summary>进度 0.0 ~ 1.0。</summary>
    public double Value
    {
        get => _value;
        set { _value = Math.Clamp(value, 0, 1); Invalidate(); }
    }

    public InkProgressBar()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        SetStyle(ControlStyles.Selectable, false);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var r = ClientRectangle;
        Ink.RoundBox(this, g, Brushes.White, Ink.InkBlack, r.Height / 2f);
        int w = (int)Math.Round((r.Width - 4) * _value);
        if (w > 0)
        {
            using var b = new SolidBrush(Ink.InkBlack);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var path = Ink.Round(new RectangleF(r.X + 2, r.Y + 2, Math.Max(w, r.Height - 4), r.Height - 4), (r.Height - 4) / 2f);
            g.FillPath(b, path);
        }
    }
}

/// <summary>
/// 平面复选框：12px 方框，选中时里面填黑色方块。
/// </summary>
internal sealed class InkCheckBox : Control
{
    private bool _checked;

    public bool Checked { get => _checked; set { _checked = value; Invalidate(); } }

    public InkCheckBox()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        Font = Ink.UiFont;
        Cursor = Cursors.Hand;
        BackColor = Color.Transparent;
    }

    protected override void OnClick(EventArgs e) { Checked = !Checked; base.OnClick(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        int box = Ink.S(this, 12);
        int y = (Height - box) / 2;
        var br = new Rectangle(0, y, box, box);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var path = Ink.Round(new RectangleF(br.X, br.Y, box - 1, box - 1), Ink.S(this, 2))) // 小方框用小圆角，太圆会像单选圆点
        {
            g.FillPath(Brushes.White, path);
            using var pen = new Pen(Ink.InkBlack, 1f);
            g.DrawPath(pen, path);
        }
        if (_checked)
        {
            using var b = new SolidBrush(Ink.InkBlack);
            int inset = Ink.S(this, 3);
            using var inner = Ink.Round(new RectangleF(br.X + inset, br.Y + inset, box - inset * 2, box - inset * 2), Ink.S(this, 1));
            g.FillPath(b, inner);
        }
        g.SmoothingMode = SmoothingMode.None;
        var tr = new Rectangle(box + Ink.S(this, 8), 0, Width - box - Ink.S(this, 8), Height);
        TextRenderer.DrawText(g, Text, Font, tr, Ink.InkBlack, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
    }
}

/// <summary>
/// 快捷键输入框：点一下，然后直接按下想要的组合键（例如 Ctrl+Alt+R），就会被记录下来。
/// 按 Esc 取消本次修改，按 Backspace 恢复默认。
/// </summary>
internal sealed class HotkeyBox : TextBox
{
    private Hotkey _value = Hotkey.Default;
    private bool _capturing;

    /// <summary>按 Backspace 时恢复成的默认值（主快捷键 / 停止快捷键各自不同）。</summary>
    public Hotkey DefaultValue { get; set; } = Hotkey.Default;

    /// <summary>用户录入了新的组合键。</summary>
    public event EventHandler? ValueChanged;

    public Hotkey Value { get => _value; set { _value = value; Text = value.ToString(); ValueChanged?.Invoke(this, EventArgs.Empty); } }

    public HotkeyBox()
    {
        ReadOnly = true;
        BorderStyle = BorderStyle.None; // 圆角边框由设置窗口画在外面
        BackColor = Color.White;
        ForeColor = Ink.InkBlack;
        Font = Ink.UiFont;
        ShortcutsEnabled = false;
        Cursor = Cursors.Hand;
        TextAlign = HorizontalAlignment.Center;
    }

    protected override void OnEnter(EventArgs e) { _capturing = true; Text = "请按下组合键…"; base.OnEnter(e); }
    protected override void OnLeave(EventArgs e) { _capturing = false; Text = _value.ToString(); base.OnLeave(e); }

    // 方向键等也交给我们处理；但单独按 Tab / Shift+Tab 仍然用来切换到下一个输入框（键盘用户需要）
    protected override bool IsInputKey(Keys keyData) => (keyData & Keys.KeyCode) != Keys.Tab || (keyData & (Keys.Control | Keys.Alt)) != 0;

    protected override void OnKeyDown(KeyEventArgs e)
    {
        e.SuppressKeyPress = true;
        e.Handled = true;
        if (!_capturing) return;

        Keys key = e.KeyCode;
        if (key == Keys.Escape) { Text = _value.ToString(); Parent?.SelectNextControl(this, true, true, true, true); return; }
        if (key == Keys.Back && e.Modifiers == Keys.None) { Value = DefaultValue; return; }

        Keys mods = e.Modifiers & (Keys.Control | Keys.Alt | Keys.Shift);
        if (Hotkey.IsModifierKey(key))
        {
            // 只按了修饰键，先显示出来，等待用户按下普通键
            var parts = new List<string>();
            if (mods.HasFlag(Keys.Control)) parts.Add("Ctrl");
            if (mods.HasFlag(Keys.Alt)) parts.Add("Alt");
            if (mods.HasFlag(Keys.Shift)) parts.Add("Shift");
            Text = string.Join("+", parts) + "+…";
            return;
        }
        var hk = new Hotkey(mods, key);
        if (!hk.IsValid) { Text = "需要包含 Ctrl 或 Alt"; return; }
        // 不允许的组合（Ctrl+C、Alt+F4 等）：录进去，由设置窗口显示原因并拒绝保存
        Value = hk;
    }

    // Alt 组合键会被系统当作菜单快捷键吃掉，这里拦下来
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (_capturing && keyData is not (Keys.Tab or (Keys.Tab | Keys.Shift)))
        {
            OnKeyDown(new KeyEventArgs(keyData));
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }
}

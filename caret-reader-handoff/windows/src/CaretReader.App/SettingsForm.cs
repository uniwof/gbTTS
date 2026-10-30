using CaretReader.App.Ui;
using CaretReader.Core;

namespace CaretReader.App;

/// <summary>
/// 设置窗口。修改的是一份“拷贝”，点“保存”才生效，点“取消”什么都不改。
///
///   快捷键
///     朗读 / 暂停 / 继续 [ Ctrl+Alt+R ]
///     停止             [ Ctrl+Alt+S ]
///   朗读
///     语速             [ 1.00 ]
///     线程数           [ 2 ]
///   标点停顿（秒）
///     逗号 ，、,        [ 0.15 ]
///     句号 。！？.!?    [ 0.50 ]
///     分号 ；;          [ 0.30 ]
///     冒号 ：:          [ 0.25 ]
///     换行             [ 0.50 ]
///   窗口
///     ■ 总在最前
///                          [恢复默认]   [取消] [保存]
/// </summary>
internal sealed class SettingsForm : Form
{
    public AppSettings Result { get; private set; }

    private readonly List<Rectangle> _frames = new();   // 输入框的圆角外框
    private readonly HotkeyBox _hotkey = new();
    private readonly HotkeyBox _stopHotkey = new() { DefaultValue = Hotkey.DefaultStop };
    private readonly Label _hotkeyError = new();
    private readonly NumericUpDown _speed = Num(0.5m, 2.0m, 0.05m, 2);
    private readonly NumericUpDown _threads = Num(1, 8, 1, 0);
    private readonly NumericUpDown _pComma = Num(0, 3, 0.05m, 2);
    private readonly NumericUpDown _pPeriod = Num(0, 3, 0.05m, 2);
    private readonly NumericUpDown _pSemi = Num(0, 3, 0.05m, 2);
    private readonly NumericUpDown _pColon = Num(0, 3, 0.05m, 2);
    private readonly NumericUpDown _pNewline = Num(0, 3, 0.05m, 2);
    private readonly InkCheckBox _topMost = new() { Text = "总在最前" };

    private int _y;                       // 布局时的“当前行”纵坐标
    private int S(int px) => Ink.S(this, px);

    public SettingsForm(AppSettings working, string settingsPath)
    {
        Result = working;

        Text = "设置 · 光标朗读";
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Ink.Paper;
        Font = Ink.UiFont;
        TopMost = true;
        DoubleBuffered = true;
        KeyPreview = true;

        // ---------- 布局：左列标签 x=24，右列控件 x=176，每行 40px ----------
        _y = S(16);
        Section("快捷键");
        Row("朗读 / 暂停 / 继续", _hotkey, S(152));
        Row("停止", _stopHotkey, S(152));
        Hint("点击输入框，直接按下新的组合键。字母键请用两个修饰键，如 Ctrl+Alt+R");
        // 快捷键有问题时显示在这里（黑色小字，不弹窗）
        _hotkeyError.Font = Ink.SmallFont;
        _hotkeyError.ForeColor = Ink.InkBlack;
        _hotkeyError.BackColor = Color.Transparent;
        _hotkeyError.AutoSize = false;
        _hotkeyError.AutoEllipsis = true;
        _hotkeyError.Bounds = new Rectangle(S(24), _y - S(4), S(400) - S(48), S(18));
        Controls.Add(_hotkeyError);
        _y += S(16);

        Section("朗读");
        Row("语速", _speed);
        Hint("1.00 = 原速。建议保持 1.00，用下面的“停顿”来放慢节奏");
        Row("线程数", _threads);
        Hint("默认 2。电脑较好可调到 4；修改后会重新加载模型");

        // P1：标点停顿改由标点表决定（「读法与标点…」→ 节奏（标点）页），旧的五档不再显示

        Section("窗口");
        _topMost.Bounds = new Rectangle(S(24), _y, S(200), S(24));
        Controls.Add(_topMost);
        _y += S(40);

        // ---------- v0.5：关于 / 第三方许可 ----------
        Section("关于");
        var about = new InkButton { Text = "关于 / 第三方许可", Bounds = new Rectangle(S(24), _y, S(152), S(28)) };
        about.Click += (_, _) => { using var f = new AboutForm(); f.ShowDialog(this); };
        Controls.Add(about);
        _y += S(36);
        // P1：读法规则（含测试框）与标点停顿表
        var rulesBtn = new InkButton { Text = "读法与标点…", Bounds = new Rectangle(S(24), _y, S(152), S(28)) };
        rulesBtn.Click += (_, _) =>
        {
            using var f = new TextRulesForm(Result.Text, Path.Combine(AppContext.BaseDirectory, "rules"),
                CaretReader.Core.Settings.TextLayerSettings.UserDataDir(settingsPath));
            f.ShowDialog(this);
        };
        Controls.Add(rulesBtn);
        _y += S(36);
        var paceBtn = new InkButton { Text = "语速与停顿…", Bounds = new Rectangle(S(24), _y, S(152), S(28)) };
        paceBtn.Click += (_, _) => { using var f = new PacingForm(Result.VoiceId ?? "chaowen-0"); f.ShowDialog(this); };
        Controls.Add(paceBtn);
        _y += S(36);
        Hint("读错的字、网络用语、标点停顿长短，都在这里改");
        _y += S(8);

        // ---------- 底部按钮 ----------
        int bw = S(80), bh = S(32);
        int width = S(400);
        var reset = new InkButton { Text = "恢复默认", Bounds = new Rectangle(S(24), _y, S(96), bh) };
        var cancel = new InkButton { Text = "取消", Bounds = new Rectangle(width - S(24) - bw * 2 - S(8), _y, bw, bh) };
        var save = new InkButton { Text = "保存", Bounds = new Rectangle(width - S(24) - bw, _y, bw, bh), Active = true };
        Controls.AddRange(new Control[] { reset, cancel, save });
        _y += bh + S(16);

        // 设置文件位置（小字，方便高级用户手动编辑）
        var path = new Label
        {
            Text = "设置文件：" + settingsPath,
            Font = Ink.SmallFont,
            ForeColor = Ink.Grey3,
            AutoSize = false,
            AutoEllipsis = true,
            BackColor = Color.Transparent,
            Bounds = new Rectangle(S(24), _y, width - S(48), S(32)),
        };
        Controls.Add(path);
        _y += S(40);

        ClientSize = new Size(width, _y);

        reset.Click += (_, _) => LoadValues(new AppSettings());
        cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        save.Click += (_, _) =>
        {
            // 快捷键检查：不允许的组合、两个快捷键相同 → 不保存，提示原因
            string? err = HotkeyError();
            if (err != null) { _hotkeyError.Text = "⚠ " + err; System.Media.SystemSounds.Beep.Play(); return; }
            Result = ReadValues(); DialogResult = DialogResult.OK; Close();
        };
        _hotkey.ValueChanged += (_, _) => _hotkeyError.Text = HotkeyError() ?? "";
        _stopHotkey.ValueChanged += (_, _) => _hotkeyError.Text = HotkeyError() ?? "";

        LoadValues(working);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        ActiveControl = _speed;   // 打开时不要让快捷键输入框直接进入“录入”状态
    }

    /// <summary>两个快捷键是否可用；可用返回 null，否则返回中文原因。</summary>
    private string? HotkeyError()
    {
        if (_hotkey.Value.Problem is string a) return "朗读快捷键：" + a;
        if (_stopHotkey.Value.Problem is string b) return "停止快捷键：" + b;
        if (_hotkey.Value.Equals(_stopHotkey.Value)) return "停止快捷键不能和朗读快捷键相同";
        return null;
    }

    protected override bool ProcessDialogKey(Keys keyData)
    {
        // Esc 关闭（快捷键输入框正在录入时除外，它自己会处理）
        if (keyData == Keys.Escape && !_hotkey.Focused && !_stopHotkey.Focused) { DialogResult = DialogResult.Cancel; Close(); return true; }
        return base.ProcessDialogKey(keyData);
    }

    // ---------- 读 / 写 控件里的值 ----------
    private void LoadValues(AppSettings s)
    {
        _hotkey.Value = Hotkey.Parse(s.Hotkey) ?? Hotkey.Default;
        _stopHotkey.Value = Hotkey.Parse(s.StopHotkey) ?? Hotkey.DefaultStop;
        _hotkeyError.Text = HotkeyError() ?? "";
        _speed.Value = Clamp(_speed, (decimal)s.Speed);
        _threads.Value = Clamp(_threads, s.NumThreads);
        _pComma.Value = Clamp(_pComma, (decimal)s.Pauses.Comma);
        _pPeriod.Value = Clamp(_pPeriod, (decimal)s.Pauses.Period);
        _pSemi.Value = Clamp(_pSemi, (decimal)s.Pauses.Semicolon);
        _pColon.Value = Clamp(_pColon, (decimal)s.Pauses.Colon);
        _pNewline.Value = Clamp(_pNewline, (decimal)s.Pauses.Newline);
        _topMost.Checked = s.TopMost;
    }

    private AppSettings ReadValues()
    {
        var s = Result.Clone();
        s.Hotkey = _hotkey.Value.ToString();
        s.StopHotkey = _stopHotkey.Value.ToString();
        s.Speed = (double)_speed.Value;
        s.NumThreads = (int)_threads.Value;
        s.Pauses = new PauseSettings
        {
            Comma = (double)_pComma.Value,
            Period = (double)_pPeriod.Value,
            Semicolon = (double)_pSemi.Value,
            Colon = (double)_pColon.Value,
            Newline = (double)_pNewline.Value,
        };
        s.TopMost = _topMost.Checked;
        s.Clamp();
        return s;
    }

    private static decimal Clamp(NumericUpDown n, decimal v) => Math.Min(n.Maximum, Math.Max(n.Minimum, v));

    // ---------- 布局小工具 ----------

    /// <summary>分组标题：粗体文字 + 右侧一条细线。</summary>
    private void Section(string title)
    {
        var lbl = new Label
        {
            Text = title, Font = Ink.TitleFont, ForeColor = Ink.InkBlack, AutoSize = true,
            BackColor = Color.Transparent, Location = new Point(S(24), _y + S(8)),
        };
        Controls.Add(lbl);
        var line = new Label   // 用 1px 高的 Label 当分隔线，最省事
        {
            AutoSize = false, BackColor = Ink.InkBlack,
            Bounds = new Rectangle(S(24), _y + S(32), S(400) - S(48), 1),
        };
        Controls.Add(line);
        _y += S(40);
    }

    private void Row(string label, Control input, int inputWidth = 0)
    {
        var lbl = new Label
        {
            Text = label, ForeColor = Ink.InkBlack, AutoSize = false, BackColor = Color.Transparent,
            TextAlign = ContentAlignment.MiddleLeft, Bounds = new Rectangle(S(24), _y, S(144), S(24)),
        };
        // 输入框本身无边框，外面由窗口画一个苹果式圆角框；输入框缩进放在框里
        var frame = new Rectangle(S(176), _y, inputWidth > 0 ? inputWidth : S(88), S(24));
        _frames.Add(frame);
        int h = input.PreferredSize.Height;
        int top = frame.Y + Math.Max(1, (frame.Height - h + 1) / 2);
        // 数字框的文字贴着自身顶部画、下面还留着字体的下沉空间，看起来偏上；往下挪 1px 让数字在圆角框里视觉居中
        if (input is NumericUpDown) top += S(1);
        input.Bounds = new Rectangle(frame.X + S(7), top, frame.Width - S(10), h);
        Controls.Add(lbl);
        Controls.Add(input);
        _y += S(32);
    }

    private void Hint(string text)
    {
        var lbl = new Label
        {
            Text = text, Font = Ink.SmallFont, ForeColor = Ink.Grey3, AutoSize = false,
            BackColor = Color.Transparent, Bounds = new Rectangle(S(24), _y - S(6), S(400) - S(48), S(18)),
        };
        Controls.Add(lbl);
        _y += S(16);
    }

    private static NumericUpDown Num(decimal min, decimal max, decimal step, int decimals) => new()
    {
        Minimum = min, Maximum = max, Increment = step, DecimalPlaces = decimals,
        BorderStyle = BorderStyle.None, BackColor = Color.White, ForeColor = Ink.InkBlack,
        TextAlign = HorizontalAlignment.Right, Font = Ink.UiFont,
    };

    /// <summary>背景：纸白 + 极淡网点 + 顶部一条黑色粗线（呼应主窗口左侧的墨条）。</summary>
    protected override void OnPaintBackground(PaintEventArgs e)
    {
        e.Graphics.FillRectangle(Ink.ToneLight, ClientRectangle);
        using var b = new SolidBrush(Ink.InkBlack);
        e.Graphics.FillRectangle(b, 0, 0, ClientSize.Width, S(4));

        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var pen = new Pen(Ink.InkBlack, 1f);
        foreach (var f in _frames)
        {
            using var path = Ink.Round(new RectangleF(f.X, f.Y, f.Width - 1, f.Height - 1), S(6));
            g.FillPath(Brushes.White, path);
            g.DrawPath(pen, path);
        }
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.None;
    }

    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Ink.RoundWindow(this); }
}

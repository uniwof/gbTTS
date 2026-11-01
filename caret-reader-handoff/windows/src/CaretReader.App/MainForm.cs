using System.Diagnostics;
using System.Reflection;
using CaretReader.App.Ui;
using CaretReader.Core;

namespace CaretReader.App;

/// <summary>
/// 主窗口 v0.6：屏幕右下角的悬浮球（一只在看书的小人，也可以换成简单圆球）。
///
///   单击：没在读 → 从光标处开始朗读（有选中文字就只读选中的）；正在读 → 暂停；已暂停 → 继续
///         P0 起单击立刻生效（取消了“双击停止”，不再等双击判定）
///   ■ 停止：取文字 / 朗读 / 暂停时，球旁边（远离屏幕边缘的一侧）滑出一个 ■ 按钮（Ui/StopButton.cs）
///   右键：菜单（选择音色 / 试听、导入音色包、始终置顶、外观、设置、关于、退出）
///   按住拖动：移到任意位置（位置会记住）
///
/// 为什么点它不会让文档“失去焦点”（v0.5 的问题，见 DESIGN.md「v0.6 聚焦问题」）：
///   * 窗口样式 WS_EX_NOACTIVATE + WS_EX_TOOLWINDOW + WS_EX_TOPMOST，鼠标点击时回答 MA_NOACTIVATE，
///     并且整个窗口没有任何子控件（没有按钮、下拉框会自己去抢键盘焦点）。点击后文档仍是前台窗口，
///     光标和选区都还在原处。
///   * FocusTracker 用系统事件实时记住“最后一个在用的别的程序窗口”，单击那一刻就把它定为朗读目标。
///   * 菜单 / 设置窗口用完后把前台还给那个窗口；万一朗读时本程序在前台，先还回去再读。
///
/// 两个全局快捷键注册在一个单独的隐形消息窗口上（HotkeyWindow），任何时候都有效。
/// </summary>
internal sealed class MainForm : Form
{
    // ---------- 程序状态 ----------
    private enum State { NoModel, Loading, Idle, Grabbing, Reading, Paused }
    private State _state = State.NoModel;

    private readonly string _appDir = AppContext.BaseDirectory;
    private readonly string _settingsPath;
    private AppSettings _settings;
    private List<VoicePack> _packs = new();
    private string _edition = "";
    private string _lastScanProblems = "";
    private readonly TtsEngine _engine = new();
    private SpeedStore _speed = null!;           // 第 3 轮：本机测速结果（data\speed.json）
    private readonly SamplePlayer _player = new();
    private bool _importing;
    private bool _readingSelection;
    private readonly CaretTextGrabber _grabber = new();

    private Hotkey _hotkey = Hotkey.Default;
    private Hotkey _stopHotkey = Hotkey.DefaultStop;
    private bool _mainRegistered, _stopRegistered;
    private readonly HotkeyWindow _hotkeys = new();

    private CancellationTokenSource? _readCts;
    private Task _readTask = Task.CompletedTask;
    private int _readSession;
    private NAudioSink? _sink;
    private bool _pauseRequested;
    // 第 3 轮：上一句 / 下一句、朗读中换音色续读
    private ClauseQueue? _queue;                 // 当前这篇文字的所有小句
    private PlaybackTracker? _track;             // 当前播放到第几句
    private CancellationTokenSource? _docCts;    // 这篇文字的后台读取（换音色、翻句时不取消）
    private int? _navIndex;                      // 正在重新开始的小句序号（连按时用）
    private int _runStart;

    private FocusTracker? _tracker;
    private readonly System.Windows.Forms.Timer _anim = new();
    private readonly System.Windows.Forms.Timer _hoverTimer = new() { Interval = 700 };
    private readonly System.Windows.Forms.Timer _fsTimer = new() { Interval = 1500 };
    private bool _hiddenByFs;
    private readonly System.Windows.Forms.Timer _progressTimer = new() { Interval = 80 };
    private readonly Stopwatch _loadClock = new();
    private double _loadExpectSeconds = 3;
    private double? _progress;              // 加载 / 导入进度（null = 不显示进度圈）

    // ---------- 外观 ----------
    private readonly BallRenderer _render = new();
    private readonly Bubble _bubble = new();
    private readonly MiniToolbar _toolbar = new();                // P0：悬停 0.8s 迷你工具条
    private readonly System.Windows.Forms.Timer _tbPoll = new() { Interval = 100 };
    private int _tbOverMs, _tbAwayMs;
    private readonly StopButton _stop = new();               // P0：■ 停止按钮
    private readonly InkDropDown _voiceMenu = new();          // 不放进窗口，只用它的音色菜单（带试听小喇叭）
    private readonly NoActivateMenu _moreMenu = new() { Renderer = new InkMenuRenderer(), ShowImageMargin = false, ShowCheckMargin = false };
    private readonly NoActivateMenu _menu = new() { Renderer = new InkMenuRenderer(), ShowImageMargin = false, ShowCheckMargin = false };
    private NotifyIcon? _tray;
    private string _petFrame = "sleep";
    private int _lastDrawnFrame = -1;
    private string _lastDrawnKey = "";
    private long _nextIdleEvent;            // 下一次眨眼 / 翻页的时间（毫秒）
    private int _idleSeq;                   // 正在播放的空闲小动作（0 = 无）
    private int _idleStep;
    private readonly Random _rnd = new();
    private bool _hover;
    // ---------- v0.7-r4：v0.5 横条 ----------
    private readonly InkButton _btnMain = new() { Text = "开始" };
    private readonly InkButton _btnStop = new() { Text = "■ 停止", Enabled = false };
    private readonly InkButton _btnSet = new() { Text = "设置" };
    private readonly InkProgressBar _bar = new() { Visible = false };
    private readonly System.Windows.Forms.Timer _readTimer = new() { Interval = 250 };
    private string _lastBarKey = "";
    private bool _sessionLocked;
    private float _mouthLevel;

    // ---------- 鼠标 ----------
    private bool _leftDown, _dragging;
    private Point _downScreen, _downWindow;
    private CaretTextGrabber.Target? _clickTarget;

    public MainForm()
    {
        _settingsPath = AppSettings.ResolvePath(_appDir);
        _settings = AppSettings.Load(_settingsPath);
        Ink.Zoom = _settings.LargeMode ? 1.5 : 1.0;
        _speed = new SpeedStore(Path.Combine(CaretReader.Core.Settings.TextLayerSettings.UserDataDir(_settingsPath), "speed.json"));
        // P1：旧版的五档停顿、旧用户词库迁移到新的用户标点表 / 用户读法词库（只做一次，不覆盖）
        try
        {
            CaretReader.Core.Settings.SettingsMigration.Run(
                CaretReader.Core.Settings.TextLayerSettings.UserDataDir(_settingsPath), _settings.Pauses,
                Path.Combine(_appDir, PolyphoneFrontend.UserDictFileName),
                CaretReader.Core.Text.PunctuationTable.Load(Path.Combine(CaretReader.Core.Text.TextLayer.RulesDir(_appDir), "punctuation.tsv"), null));
        }
        catch (Exception ex) { Log.Write("设置迁移失败: " + ex.GetType().Name); }

        Text = "光标朗读";
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        ShowInTaskbar = true;
        BackColor = Ink.Paper;
        DoubleBuffered = true;
        TopMost = _settings.TopMost;
        AutoStart.Apply(_settings.AutoStart);
        Font = Ink.UiFont;
        try { Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!); } catch { }

        _voiceMenu.SelectedIndexChanged += async (_, _) => await OnVoiceChangedAsync();
        _voiceMenu.Opening += (_, _) => RefreshVoices(tidy: false);
        _voiceMenu.SpeakerClicked += PreviewVoice;
        _voiceMenu.HasSample = o => o is VoicePack p && p.SamplePath != null;
        _voiceMenu.IsPlaying = o => o is VoicePack p && _player.PlayingId == p.Id;
        _voiceMenu.Tooltip = o => o is VoicePack p ? (string.IsNullOrWhiteSpace(p.Description) ? "" : p.Description + "\n") + "点右侧小喇叭试听" : null;
        _voiceMenu.MenuClosed += (_, _) => ReturnFocus();
        _player.StateChanged += () => _voiceMenu.RefreshSpeakers();
        _player.Failed += msg => FlashStatus(msg);

        _hotkeys.Pressed += OnHotkey;
        _anim.Tick += (_, _) => AnimTick();
        _stop.StopClicked += () =>
        {
            Log.Write($"点 ■ 停止（状态 {_state}）");
            if (_state is State.Grabbing or State.Reading or State.Paused) { StopReading(); FlashStatus("已停止", 2500); }
        };
        _toolbar.StopClicked += () => { try { _toolbar.HideNow(); if (_state is State.Grabbing or State.Reading or State.Paused) { StopReading(); FlashStatus("已停止", 2500); } } catch { } };
        _toolbar.PrevClicked += () => { try { NavSentence(-1); } catch { } };
        _toolbar.NextClicked += () => { try { NavSentence(+1); } catch { } };
        _toolbar.VoiceClicked += () => { try { ShowMenu(Cursor.Position); } catch { } };
        _hoverTimer.Tick += (_, _) => { _hoverTimer.Stop(); { } };
        _progressTimer.Tick += (_, _) => UpdateLoadProgress();
        _fsTimer.Tick += (_, _) => CheckFullscreen();
        _fsTimer.Start();
        _menu.Font = Ink.UiFont;
        _moreMenu.Font = Ink.UiFont;
        _moreMenu.Closed += (_, _) => { ReturnFocus(); Redraw(); };
        _menu.Closed += (_, _) => { ReturnFocus(); Redraw(); };

        // 横条的控件
        Controls.AddRange(new Control[] { _btnMain, _btnStop, _voiceMenu, _btnSet, _bar });
        _btnMain.Click += (_, _) =>
        {
            var target = CurrentTarget("开始按钮");
            Log.Write($"点主按钮（状态 {_state}）");
            if (_state == State.Reading) PauseReading();
            else _ = OnMainActionAsync(Stopwatch.GetTimestamp(), target);
        };
        _btnStop.Click += (_, _) =>
        {
            Log.Write($"点 ■ 停止（状态 {_state}）");
            if (_state is State.Grabbing or State.Reading or State.Paused) { StopReading(); FlashStatus("已停止", 2500); }
        };
        _btnSet.Click += (_, _) => OpenSettings();
        foreach (Control c in new Control[] { _btnMain, _btnStop, _voiceMenu, _btnSet, _bar })
            c.MouseUp += (_, e) => { if (e.Button == MouseButtons.Right) ShowMenu(Cursor.Position); };
        _readTimer.Tick += (_, _) => { if (_state == State.Reading) Redraw(); };
        Microsoft.Win32.SystemEvents.SessionSwitch += OnSessionSwitch;
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
    }

    // =====================================================================
    // 窗口：不抢焦点、置顶、不进任务栏、分层（圆形）
    // =====================================================================
    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= Native.WS_EX_NOACTIVATE | Native.WS_EX_APPWINDOW;
            cp.ExStyle &= ~(Native.WS_EX_TOOLWINDOW | Native.WS_EX_LAYERED);
            return cp;
        }
    }
    protected override bool ShowWithoutActivation => true;

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyBallSize();
        RestoreBallPosition();
    }

    private void ShowGuideOnce()
    {
        if (_settings.GuideShown) return;
        _settings.GuideShown = true;
        try { _settings.Save(_settingsPath); } catch { }
        BeginInvoke(new Action(() =>
        {
            using var g = new GuideForm(_settings.Hotkey, _settings.StopHotkey);
            g.ShowDialog(this);
            ReassertTopMost();
        }));
    }

    private void ApplyBallSize()
    {
        Size = new Size(Ink.S(this, 456), Ink.S(this, 80));
        int S(int v) => Ink.S(this, v);
        int y = S(10), h = S(34);
        _btnMain.SetBounds(S(16), y, S(80), h);
        _btnStop.SetBounds(S(102), y, S(76), h);
        _voiceMenu.SetBounds(S(184), y, S(190), h);
        _btnSet.SetBounds(S(380), y, S(68), h);
        _bar.SetBounds(S(316), S(60), S(132), S(8));
        Ink.RoundWindow(this);
        _lastBarKey = "";
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Ink.RoundBox(this, g, Ink.ToneLight, Ink.InkBlack, Ink.Radius(this) * 1.5f);
        using (var ink = new SolidBrush(Ink.InkBlack)) g.FillRectangle(ink, 0, 0, Ink.S(this, 8), Height);
        using (var pen = new Pen(Ink.Grey2)) g.DrawLine(pen, Ink.S(this, 16), Ink.S(this, 52), Width - Ink.S(this, 8), Ink.S(this, 52));
        int right = _bar.Visible ? _bar.Left - Ink.S(this, 8) : Width - Ink.S(this, 8);
        var rc = new Rectangle(Ink.S(this, 16), Ink.S(this, 54), Math.Max(10, right - Ink.S(this, 16)), Ink.S(this, 22));
        TextRenderer.DrawText(g, StatusText().Replace("\n", " "), Ink.SmallFont, rc, Ink.Grey4,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Native.WM_MOUSEACTIVATE)
        {
            // 鼠标点击时不激活本窗口：文档继续保持焦点、光标、选区
            m.Result = (IntPtr)Native.MA_NOACTIVATE;
            return;
        }
        if (m.Msg == Native.WM_DPICHANGED)
        {
            base.WndProc(ref m);
            BeginInvoke(() => { ApplyBallSize(); KeepOnScreen(); Redraw(); });
            return;
        }
        base.WndProc(ref m);
    }

    private void RestoreBallPosition()
    {
        var wa = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 720);
        int margin = Ink.S(this, 24);
        var pos = new Point(wa.Right - Width - margin, wa.Bottom - Height - margin);   // 默认：右下角
        if (_settings.WindowX is int x && _settings.WindowY is int y)
        {
            var r = new Rectangle(x, y, Width, Height);
            if (Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(Rectangle.Inflate(r, -Width / 3, -Height / 3)))) pos = new Point(x, y);
        }
        Location = pos;
        KeepOnScreen();
    }

    /// <summary>球必须完整地在某个屏幕的工作区里（拔掉副屏、改分辨率之后也不会跑丢）。</summary>
    private void KeepOnScreen()
    {
        var wa = Screen.FromRectangle(Bounds).WorkingArea;
        int x = Math.Clamp(Left, wa.Left, Math.Max(wa.Left, wa.Right - Width));
        int y = Math.Clamp(Top, wa.Top, Math.Max(wa.Top, wa.Bottom - Height));
        if (x != Left || y != Top) Location = new Point(x, y);
    }

    private void OnDisplayChanged(object? sender, EventArgs e)
    {
        if (IsDisposed) return;
        BeginInvoke(() => { ApplyBallSize(); KeepOnScreen(); Redraw(); });
    }

    private void OnSessionSwitch(object? sender, Microsoft.Win32.SessionSwitchEventArgs e)
    {
        _sessionLocked = e.Reason is Microsoft.Win32.SessionSwitchReason.SessionLock or Microsoft.Win32.SessionSwitchReason.ConsoleDisconnect
                                     or Microsoft.Win32.SessionSwitchReason.RemoteDisconnect;
        if (!_sessionLocked && !IsDisposed) BeginInvoke(() => ScheduleAnim());
    }

    /// <summary>重新置顶（有的全屏程序、任务栏会把置顶窗口压下去）。只在切换前台窗口时做一次，很便宜。</summary>
    private void ReassertTopMost()
    {
        if (!_settings.TopMost || !IsHandleCreated) return;
        Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }

    // =====================================================================
    // 画面
    // =====================================================================
    private BallRenderer.Look CurrentLook => _settings.Appearance == "ball" || !_render.HasPet ? BallRenderer.Look.Ball : BallRenderer.Look.Pet;

    private void Redraw()
    {
        if (!IsHandleCreated || IsDisposed) return;
        bool busy = _state != State.Loading && !_importing;
        bool active = _state is State.Reading or State.Grabbing;
        string main = _state switch { State.Reading or State.Grabbing => "暂停", State.Paused => "继续", _ => "开始" };
        bool canMain = _state is State.Idle or State.Reading or State.Paused or State.Grabbing;
        bool canStop = _state is State.Grabbing or State.Reading or State.Paused;
        double? prog = _progress;
        if (prog == null && _state == State.Reading && _queue != null && _track != null)
        {
            int n = Math.Max(1, _queue.Count);
            prog = Math.Min(1.0, (_track.CurrentIndex(_runStart) + 1.0) / n);
        }
        string status = StatusText();
        string key = $"{main}|{active}|{canMain}|{canStop}|{busy}|{(prog is double q ? (int)(q * 100) : -1)}|{status}|{_packs.Count}";
        if (key == _lastBarKey) return;
        _lastBarKey = key;
        _btnMain.Text = main; _btnMain.Active = active; _btnMain.Enabled = canMain;
        _btnStop.Enabled = canStop;
        _voiceMenu.Enabled = busy && _packs.Count > 0;
        _btnSet.Enabled = busy;
        if (prog is double pv) { _bar.Value = pv; if (!_bar.Visible) { _bar.Visible = true; } } else if (_bar.Visible) _bar.Visible = false;
        Invalidate();
    }

    protected override void OnMove(EventArgs e)
    {
        base.OnMove(e);
    }

    private void UpdateStopButton() { Redraw(); }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        UpdateStopButton();
    }

    /// <summary>
    /// 动画节拍：
    ///   朗读中：每 100 毫秒看一次音量，嘴巴闭 / 半张 / 张开（画面没变就不重画）；
    ///   空闲：静止，每隔 3~7 秒眨一次眼，每隔 15~30 秒翻一页；
    ///   暂停 / 加载 / 简单圆球 / 锁屏 / 全屏程序在前台：完全不动（不开定时器）。
    /// </summary>
    private void ScheduleAnim()
    {
        _anim.Stop();
        if (_state == State.Reading) _readTimer.Start(); else _readTimer.Stop();
        Redraw();
    }

    private void AnimTick()
    {
        if (_sessionLocked || Native.ForegroundIsFullscreen()) { _petFrame = _state == State.Reading ? "talk0" : "idle"; Redraw(); _anim.Interval = 1000; return; }
        if (_state == State.Reading)
        {
            float lv = _sink?.Level ?? 0f;
            _mouthLevel = Math.Max(lv, _mouthLevel * 0.55f);   // 张嘴快、闭嘴稍慢，看起来不抽搐
            _petFrame = _mouthLevel < 0.06f ? "talk0" : _mouthLevel < 0.22f ? "talk1" : "talk2";
            Redraw();
            return;
        }
        if (_state != State.Idle) { _anim.Stop(); return; }

        // 空闲小动作：1 = 眨眼（blink 150ms），2 = 翻页（page1→2→3，各 140ms）
        if (_idleSeq == 0)
        {
            _idleSeq = _rnd.Next(5) == 0 ? 2 : 1;
            _idleStep = 0;
        }
        string[] seq = _idleSeq == 1 ? new[] { "blink", "idle" } : new[] { "page1", "page2", "page3", "idle" };
        _petFrame = seq[_idleStep];
        Redraw();
        _idleStep++;
        if (_idleStep >= seq.Length)
        {
            _idleSeq = 0;
            _nextIdleEvent = Environment.TickCount64 + (_rnd.Next(4) == 0 ? _rnd.Next(15000, 30000) : _rnd.Next(3000, 7000));
            ScheduleAnim();
        }
        else _anim.Interval = _idleSeq == 1 ? 150 : 140;
    }

    // =====================================================================
    // 鼠标：单击 / 右键 / 拖动（P0 取消双击）
    // =====================================================================
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left)
        {
            _leftDown = true; _dragging = false;
            _downScreen = Cursor.Position; _downWindow = Location;
            Capture = true;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_leftDown) return;
        var p = Cursor.Position;
        if (!_dragging && BallLayout.IsDrag(p.X - _downScreen.X, p.Y - _downScreen.Y, Ink.S(this, BallLayout.DragThresholdPx)))
        { _dragging = true; _bubble.HideNow(); }
        if (_dragging) Location = new Point(_downWindow.X + p.X - _downScreen.X, _downWindow.Y + p.Y - _downScreen.Y);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button == MouseButtons.Left && _leftDown)
        {
            _leftDown = false; Capture = false;
            if (_dragging)
            {
                _dragging = false;
                KeepOnScreen();
                _settings.WindowX = Left; _settings.WindowY = Top;
                try { _settings.Save(_settingsPath); } catch { }
            }
        }
        else if (e.Button == MouseButtons.Right) ShowMenu(Cursor.Position);
    }

    // =====================================================================
    // 右键菜单 + 托盘图标
    // =====================================================================
    private void ShowMenu(Point screen)
    {
        _bubble.HideNow();
        BuildMenu();
        _menu.Show(screen);
    }

    private void BuildMenu()
    {
        _menu.Items.Clear();
        int w = Ink.S(this, 260), h = Ink.S(this, 34);
        ToolStripMenuItem Item(string text, Action? onClick, bool enabled = true, bool check = false)
        {
            var it = new ToolStripMenuItem((check ? "■  " : "    ") + text) { AutoSize = false, Width = w, Height = h, Enabled = enabled, TextAlign = ContentAlignment.MiddleLeft };
            if (onClick != null) it.Click += (_, _) => onClick();
            return it;
        }
        string act = _state switch { State.Reading or State.Grabbing => "暂停", State.Paused => "继续", _ => "朗读" };
        bool canAct = _state is State.Idle or State.Reading or State.Paused or State.Grabbing;
        bool busy = _state != State.Loading && !_importing;
        _menu.Items.Add(Item(act, () => _ = OnMainActionAsync(Stopwatch.GetTimestamp(), CurrentTarget("菜单")), canAct));
        _menu.Items.Add(Item("停止", () => { StopReading(); FlashStatus("已停止"); }, _state is State.Reading or State.Paused or State.Grabbing));
        _menu.Items.Add(Item("音色 ▸", () => BeginInvoke(() => _voiceMenu.ShowAt(new Point(Left, Top))), busy && _packs.Count > 0));
        _menu.Items.Add(Item("设置…", OpenSettings, busy));
        _menu.Items.Add(Item("退出", Close));
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(Item("更多 ▸", () => BeginInvoke(() => ShowMoreMenu(Cursor.Position))));
    }

    private void ShowMoreMenu(Point screen)
    {
        _moreMenu.Items.Clear();
        int w = Ink.S(this, 240), h = Ink.S(this, 34);
        ToolStripMenuItem Item(string text, Action onClick, bool enabled = true, bool check = false)
        {
            var it = new ToolStripMenuItem((check ? "■  " : "    ") + text) { AutoSize = false, Width = w, Height = h, Enabled = enabled, TextAlign = ContentAlignment.MiddleLeft };
            it.Click += (_, _) => onClick();
            return it;
        }
        bool navOk = _queue != null && _state is State.Reading or State.Paused;
        _moreMenu.Items.Add(Item("上一句", () => NavSentence(-1), navOk));
        _moreMenu.Items.Add(Item("下一句", () => NavSentence(+1), navOk));
        _moreMenu.Items.Add(Item("朗读剪贴板", () => OnClipboardKey(Stopwatch.GetTimestamp()),
                             _state is State.Idle or State.Reading or State.Paused or State.Grabbing));
        _moreMenu.Items.Add(Item("导入音色包…", () => ImportVoices(), _state != State.Loading && !_importing));
        _moreMenu.Items.Add(new ToolStripSeparator());
        _moreMenu.Items.Add(Item("始终置顶", ToggleTopMost, check: _settings.TopMost));
        _moreMenu.Items.Add(Item("关于 / 第三方许可", () => { using var f = new AboutForm(); f.ShowDialog(this); ReturnFocus(); }));
        _moreMenu.Show(screen);
    }

    /// <summary>A2：全屏程序（QUNS_BUSY / D3D 全屏 / 演示模式）时自动隐藏横条，结束后恢复；不抢焦点。</summary>
    private void CheckFullscreen()
    {
        try
        {
            bool fs = false;
            if (_settings.HideOnFullscreen) fs = Native.ForegroundIsFullscreen();
            if (fs && Visible && !_hiddenByFs) { _hiddenByFs = true; Hide(); _bubble.HideNow(); }
            else if (!fs && _hiddenByFs) { _hiddenByFs = false; Show(); if (_settings.TopMost) ReassertTopMost(); }
        }
        catch { }
    }

    private void ToggleTopMost()
    {
        _settings.TopMost = !_settings.TopMost;
        TopMost = _settings.TopMost;
        AutoStart.Apply(_settings.AutoStart);
        if (!_settings.TopMost) Native.SetWindowPos(Handle, Native.HWND_NOTOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        else ReassertTopMost();
        try { _settings.Save(_settingsPath); } catch { }
        FlashStatus(_settings.TopMost ? "已设为始终在最前面" : "已取消置顶（可以被别的窗口挡住；右下角托盘图标能找回它）");
    }

    private void SetAppearance(string look)
    {
        _settings.Appearance = look;
        try { _settings.Save(_settingsPath); } catch { }
        _lastDrawnKey = "";
        ScheduleAnim();
        Log.Write("外观: " + look);
    }

    private void SetupTray()
    {
        try
        {
            _tray = new NotifyIcon { Icon = Icon ?? SystemIcons.Application, Text = "光标朗读", Visible = true };
            _tray.MouseUp += (_, e) =>
            {
                if (e.Button == MouseButtons.Right) ShowMenu(Cursor.Position);
                else if (e.Button == MouseButtons.Left)
                {
                    // 左键：显示 / 隐藏悬浮球
                    if (Visible) { Hide(); _bubble.HideNow(); }
                    else
                    {
                        Show();
                        RestoreBallPosition();
                        ReassertTopMost();
                        FlashStatus("我在这里 · " + StatusText());
                    }
                }
            };
            _tray.DoubleClick += (_, _) => { };
            _tray.MouseMove += (_, _) => UpdateTrayText();
        }
        catch (Exception ex) { Log.Write("托盘图标创建失败: " + ex.Message); }
    }

    private void UpdateTrayText()
    {
        if (_tray == null) return;
        string t = "光标朗读 · " + StatusText().Replace("\n", " ");
        _tray.Text = t.Length > 62 ? t.Substring(0, 62) : t;   // NotifyIcon.Text 上限 63 字符
    }

    // =====================================================================
    // 提示气泡
    // =====================================================================
    private bool _bubbleIsHover;

    private void ShowStatusBubble()
    {
        if (_bubble.Showing && !_bubbleIsHover) return;   // 正在显示重要提示：不覆盖
        _bubbleIsHover = true;
        _bubble.ShowNear(Bounds, StatusText() + "\n单击：朗读 / 暂停 / 继续 · ■ 或停止快捷键：停止\n右键：音色、设置、置顶、退出", 6000);
    }

    /// <summary>在球旁边显示一条提示，ms 毫秒后消失（默认 4 秒）。</summary>
    private void FlashStatus(string text, int ms = 2500)
    {
        if (IsDisposed) return;
        _bubbleIsHover = false;
        _bubble.ShowNear(Bounds, text, ms);
    }

    private string StopHint => _stopRegistered ? $"{_stopHotkey} 停止" : "停止快捷键不可用";

    private string StatusText()
    {
        string ed = string.IsNullOrEmpty(_edition) ? "" : _edition + " · ";
        return _state switch
        {
            State.NoModel => _packs.Count == 0 ? "没有可用的音色 · 请右键「导入音色包」" : "音色加载失败 · 请右键换一个音色",
            State.Loading => _progressText ?? "正在加载…",
            State.Idle => _mainRegistered ? $"{ed}就绪 · {_hotkey} 朗读 · {StopHint}" : $"{ed}就绪 · 快捷键不可用，请在设置中更换",
            State.Grabbing => "正在获取文字…",
            State.Reading => $"{(_readingSelection ? "朗读选中" : "朗读中")} · {_hotkey} 暂停 · {StopHint}",
            State.Paused => $"已暂停 · 点「继续」或 {_hotkey} · {StopHint}",
            _ => "",
        };
    }
    private string? _progressText;

    // =====================================================================
    // 启动 / 关闭
    // =====================================================================
    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        ReassertTopMost();
        _tracker = new FocusTracker();
        _tracker.ForegroundChanged += ReassertTopMost;
        CaretTextGrabber.WarmUp();
        SetupTray();
        Redraw();
        ShowGuideOnce();

        if (CrashGuard.PreviousCrashStage is string lastStage &&
            !lastStage.Contains("就绪") && !lastStage.Contains("朗读中") && !lastStage.Contains("正在退出"))
        {
            ShowError("上次运行时，光标朗读意外退出了（停在：" + lastStage + "）。\n\n" +
                      "能找到的错误信息已写进程序文件夹里的 log.txt。\n" +
                      "如果这次还会退出，请把 log.txt 发出来，以便查明原因。");
        }

        CrashGuard.Mark("扫描音色文件夹 voices");
        try { _edition = File.ReadAllText(Path.Combine(_appDir, "edition.txt")).Trim(); } catch { _edition = ""; }
        if (_edition.Length > 12) _edition = _edition.Substring(0, 12);
        if (_edition != "") { Text = "光标朗读 · " + _edition; if (_tray != null) _tray.Text = Text; }
        VoiceImporter.CleanupTemp(_appDir);
        // A3：把 .gbvoice / 音色包文件夹拖到横条上导入
        AllowDrop = true;
        DragEnter += (_, e) =>
        {
            e.Effect = e.Data?.GetData(DataFormats.FileDrop) is string[] f && f.Length > 0
                && (File.Exists(f[0]) ? VoiceImporter.IsPackFile(f[0]) : Directory.Exists(f[0]))
                ? DragDropEffects.Copy : DragDropEffects.None;
        };
        DragDrop += (_, e) =>
        {
            if (e.Data?.GetData(DataFormats.FileDrop) is string[] f && f.Length > 0) BeginInvoke(() => ImportVoices(f[0]));
        };
        RefreshVoices(tidy: true);

        CrashGuard.Mark("注册快捷键");
        ApplyHotkeys(_settings.Hotkey, _settings.StopHotkey, showErrors: true);
        RegisterRound3Hotkeys();

        if (_packs.Count == 0)
        {
            SetState(State.NoModel);
            ShowError("「音色」文件夹（程序旁边的 voices 文件夹）里没有可用的音色。\n\n" +
                      "请右键横条 →「导入音色包…」，选择一个音色包文件夹；\n或者把音色包文件夹直接复制到 voices 文件夹里。");
            return;
        }
        await LoadSelectedVoiceAsync();
        if (_state == State.Idle) FlashStatus("光标朗读已就绪\n点「开始」：从光标处朗读 · ■ 停止：立刻静音 · 右键：菜单", 5000);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        base.OnFormClosing(e);
        _anim.Stop(); _progressTimer.Stop(); _hoverTimer.Stop();
        _readTimer.Stop();
        Microsoft.Win32.SystemEvents.SessionSwitch -= OnSessionSwitch;
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
        UnregisterHotkeys();
        StopReading();
        _player.Stop();
        _bubble.HideNow();
        if (_tray != null) { _tray.Visible = false; _tray.Dispose(); _tray = null; }
        _tracker?.Dispose();
        try
        {
            _settings.WindowX = Left; _settings.WindowY = Top;
            _settings.Save(_settingsPath);
        }
        catch (Exception ex) { Log.Write("保存设置失败: " + ex.Message); }
        CrashGuard.Mark("正在退出");
        try { _readTask.Wait(3000); } catch { }
        try { Task.Run(_engine.Dispose).Wait(3000); } catch { }
        Log.Write("===== 退出");
        CrashGuard.CleanExit();
    }

    // =====================================================================
    // 全局快捷键（注册在 HotkeyWindow 上）
    // =====================================================================
    private void OnHotkey(int id, long pressedAt)
    {
        if (id == Hotkey.StopId) { Log.Write("停止快捷键"); OnStopKey(); return; }
        if (id == Hotkey.PrevId) { Log.Write("上一句快捷键"); NavSentence(-1); return; }
        if (id == Hotkey.NextId) { Log.Write("下一句快捷键"); NavSentence(+1); return; }
        if (id == Hotkey.AddReadingId) { Log.Write("添加读法快捷键"); _ = OnAddReadingKeyAsync(); return; }
        if (id == Hotkey.ClipId) { Log.Write("朗读剪贴板快捷键"); OnClipboardKey(pressedAt); return; }
        // 快捷键那一刻的前台窗口就是要读的文档（本程序在前台时用最后一个外部窗口）
        var target = CurrentTarget("快捷键");
        Log.Write($"朗读快捷键（状态 {_state}），目标 {target}");
        _ = OnMainActionAsync(pressedAt, target);
    }

    /// <summary>此刻的朗读目标：前台窗口；前台是本程序（菜单、设置窗口）时用最后一个外部窗口。</summary>
    private CaretTextGrabber.Target CurrentTarget(string trigger)
    {
        var fg = Native.GetForegroundWindow();
        if (fg != IntPtr.Zero && !FocusTracker.IsOwn(fg))
        {
            _tracker?.Update(fg);
            return CaretTextGrabber.Target.Describe(fg, Native.ThreadFocusOf(fg).focus, trigger);
        }
        var last = _tracker?.LastWindow ?? IntPtr.Zero;
        return CaretTextGrabber.Target.Describe(last, _tracker?.LastFocus ?? IntPtr.Zero, trigger + (fg == IntPtr.Zero ? "，当时没有前台窗口" : "，当时本程序在前台"));
    }

    private void UnregisterHotkeys()
    {
        if (_mainRegistered) Hotkey.Unregister(_hotkeys.Handle, Hotkey.MainId);
        if (_stopRegistered) Hotkey.Unregister(_hotkeys.Handle, Hotkey.StopId);
        _mainRegistered = _stopRegistered = false;
    }

    private static Hotkey Sanitize(string text, Hotkey fallback, string name, List<string> errors)
    {
        var hk = Hotkey.Parse(text);
        if (hk == null)
        {
            Log.Write($"{name}快捷键格式无效：{text}，改用默认 {fallback}");
            errors.Add($"{name}快捷键“{text}”无法识别，已改用默认的 {fallback}。");
            return fallback;
        }
        if (hk.Value.Problem is string why)
        {
            Log.Write($"{name}快捷键不允许：{text}（{why}），改用默认 {fallback}");
            errors.Add($"{why}。\n{name}快捷键已改用默认的 {fallback}。");
            return fallback;
        }
        return hk.Value;
    }

    private bool ApplyHotkeys(string mainText, string stopText, bool showErrors)
    {
        var errors = new List<string>();
        var main = Sanitize(mainText, Hotkey.Default, "朗读 / 暂停", errors);
        var stop = Sanitize(stopText, Hotkey.DefaultStop, "停止", errors);

        UnregisterHotkeys();
        _mainRegistered = TryRegister(main, Hotkey.MainId, _hotkey, "朗读 / 暂停", errors, out _hotkey);
        if (stop.Equals(_hotkey))
        {
            Log.Write($"停止快捷键与朗读快捷键相同（{stop}），停止快捷键未注册");
            errors.Add($"停止快捷键不能和朗读快捷键相同（都是 {stop}）。请在“设置”里换一个停止快捷键。");
            _stopRegistered = false;
        }
        else
        {
            var oldStop = _stopHotkey.Equals(_hotkey) ? stop : _stopHotkey;
            _stopRegistered = TryRegister(stop, Hotkey.StopId, oldStop, "停止", errors, out _stopHotkey);
        }
        if (errors.Count > 0 && showErrors) ShowError(string.Join("\n\n", errors));
        return errors.Count == 0;
    }

    /// <summary>第 3 轮的三个固定快捷键（不要用 Ctrl+Alt+方向键：会触发 Intel 显卡的屏幕旋转）。</summary>
    private void RegisterRound3Hotkeys()
    {
        var bad = new List<string>();
        void Reg(Hotkey hk, int id, string name)
        {
            if (hk.Register(_hotkeys.Handle, id)) Log.Write($"{name}快捷键已注册: {hk}");
            else { Log.Write($"{name}快捷键注册失败（可能被其他程序占用）: {hk}"); bad.Add($"{name} {hk}"); }
        }
        const Keys CA = Keys.Control | Keys.Alt;
        Reg(new Hotkey(CA, Keys.PageUp), Hotkey.PrevId, "上一句");
        Reg(new Hotkey(CA, Keys.PageDown), Hotkey.NextId, "下一句");
        Reg(new Hotkey(CA, Keys.C), Hotkey.ClipId, "朗读剪贴板");
        Reg(new Hotkey(CA, Keys.D), Hotkey.AddReadingId, "添加读法");
        if (bad.Count > 0) FlashStatus("这些快捷键被别的程序占用了，暂时用不了：\n" + string.Join("、", bad), 6000);
    }

    private bool TryRegister(Hotkey wanted, int id, Hotkey previous, string name, List<string> errors, out Hotkey result)
    {
        if (wanted.Register(_hotkeys.Handle, id))
        {
            Log.Write($"{name}快捷键已注册（系统级，本程序不在前台也有效）: {wanted}");
            result = wanted;
            return true;
        }
        Log.Write($"{name}快捷键注册失败（可能被其他程序占用）: {wanted}，错误码 {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}");
        if (!wanted.Equals(previous) && previous.Register(_hotkeys.Handle, id))
        {
            errors.Add($"{name}快捷键 {wanted} 已被其他程序占用，无法使用，继续使用原来的 {previous}。\n请在“设置”里换一个组合键。");
            result = previous;
            return true;
        }
        errors.Add($"{name}快捷键 {wanted} 已被其他程序占用，无法使用。\n请在“设置”里换一个组合键。");
        result = previous;
        return false;
    }

    // =====================================================================
    // 朗读：开始 / 暂停 / 继续 / 停止
    // =====================================================================

    /// <summary>主快捷键 / 单击小人 / 菜单：开始 → 暂停 → 继续 → 暂停 …</summary>
    private async Task OnMainActionAsync(long pressedAt, CaretTextGrabber.Target target)
    {
        switch (_state)
        {
            case State.Idle:
                await StartReadingAsync(pressedAt, target);
                return;
            case State.Reading:
                PauseReading();
                return;
            case State.Paused:
                ResumeReading();
                return;
            case State.Grabbing:
                _pauseRequested = !_pauseRequested;
                FlashStatus(_pauseRequested ? "正在获取文字…（取到后暂停）" : "正在获取光标后的文字…", 2000);
                return;
            case State.Loading:
                FlashStatus("模型还在加载，请稍候…");
                return;
            case State.NoModel:
                FlashStatus(_packs.Count == 0 ? "没有可用的音色，请右键「导入音色包」" : "音色加载失败，请右键换一个音色或重新导入");
                return;
        }
    }

    private void OnStopKey()
    {
        if (_state is State.Grabbing or State.Reading or State.Paused)
        {
            StopReading();
            FlashStatus("已停止");
        }
    }

    private void PauseReading()
    {
        _sink?.Pause();
        SetState(State.Paused);
        Log.Write("暂停");
    }

    private void ResumeReading()
    {
        _player.Stop();
        _sink?.Resume();
        SetState(State.Reading);
        Log.Write("继续");
    }

    private async Task StartReadingAsync(long pressedAt, CaretTextGrabber.Target target, string? clipboardText = null)
    {
        int session = ++_readSession;
        var cts = new CancellationTokenSource();
        _readCts = cts;
        _pauseRequested = false;
        _readingSelection = false;
        _player.Stop();
        SetState(State.Grabbing);

        CaretTextGrabber.Result grab;
        try
        {
            grab = clipboardText != null
                ? new CaretTextGrabber.Result(clipboardText, "剪贴板", "Ctrl+Alt+C 朗读剪贴板") { Selection = true }
                : await _grabber.GrabAsync(target, cts.Token);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            Log.Write("取文字出错: " + ex);
            grab = new CaretTextGrabber.Result(null, "?", ex.Message);
        }
        if (session != _readSession || cts.IsCancellationRequested) return;

        double grabSeconds = Stopwatch.GetElapsedTime(pressedAt).TotalSeconds;
        Log.Write($"取文字方式: {grab.Method}{(grab.Selection ? "（选中的文字）" : "")}，长度 {grab.Text?.Length ?? 0}{(grab.Rest != null ? "（其余后台读取）" : "")}，" +
                  $"用时 {grabSeconds:0.00}s，目标 {target}，{grab.Detail}");

        // P1：新文本层（读法规则、数字、字母、Markdown、标点表停顿、标题识别）
        var layer = CaretReader.Core.Text.TextLayer.For(_appDir, _settingsPath, _settings.Text);
        string? text = grab.Text;
        var first = await Task.Run(() => layer.Split(text, firstChunk: true, CaretTextGrabber.MaxChars));
        if (session != _readSession || cts.IsCancellationRequested) return;
        if (first.Count == 0 && grab.Rest == null)
        {
            SetState(State.Idle);
            FlashStatus(grab.Unsupported ? (grab.Detail ?? "这个窗口不支持朗读")
                      : grab.NoCaret ? "这个程序里没有文字光标（例如网页、PDF）。\n请先用鼠标选中要读的文字，再单击小人或按快捷键。"
                      : grab.Text == null ? "没能取到文字（详见 log.txt）"
                      : grab.Selection ? "选中的内容里没有可朗读的文字" : "光标后面没有可朗读的文字", 6000);
            return;
        }

        var queue = new ClauseQueue();
        queue.Add(first);
        if (grab.Rest == null) queue.Complete();
        _docCts?.Cancel();
        _docCts = new CancellationTokenSource();
        _queue = queue;
        _readingSelection = grab.Selection;
        if (grab.Rest != null) _ = FetchRestAsync(grab.Rest, queue, layer, _docCts.Token);

        await RunQueueAsync(queue, 0, pressedAt, session, cts, _pauseRequested);
    }

    /// <summary>从第 startIndex 句开始读这篇文字（开始朗读、上一句 / 下一句、换音色续读都走这里）。</summary>
    private async Task RunQueueAsync(ClauseQueue queue, int startIndex, long pressedAt, int session, CancellationTokenSource cts, bool startPaused)
    {
        var voice = (VoicePack)_voiceMenu.SelectedItem!;
        float speed = (float)Math.Clamp(voice.Speed * _settings.Speed, 0.5, 2.0);
        var sink = new NAudioSink();
        var track = new PlaybackTracker();
        _sink = sink; _track = track; _runStart = startIndex; _navIndex = null;
        if (startPaused) { sink.Pause(); SetState(State.Paused); }
        else SetState(State.Reading);
        _pauseRequested = false;

        CrashGuard.Mark($"朗读中（{voice.Id}）");
        var task = SpeechPipeline.RunAsync(queue, _engine, voice.Sid, speed, sink, cts.Token,
                                           onClause: null, hotkeyTimestamp: pressedAt, gain: (float)(voice.Gain * _settings.VolumePercent / 100.0),
                                           startIndex: startIndex, tracker: track)
            .ContinueWith(t =>
            {
                var st = t.Result;
                Log.Write($"朗读结束: 小句 {st.Clauses}/{queue.Count}（从第 {startIndex + 1} 句起），按键→出声 {st.FirstAudioLatencySeconds:0.00}s" +
                          $"（其中取文字和切分 {st.StartOffsetSeconds:0.00}s），RTF {st.Rtf:0.00}，取消={st.Cancelled}，错误={st.Error ?? "无"}");
                sink.Dispose();
                return st;
            }, TaskScheduler.Default);
        _readTask = task;

        PipelineStats? stats = null;
        try { stats = await task; }
        catch (Exception ex) { Log.Write("朗读任务异常: " + ex); }
        finally
        {
            if (session == _readSession)
            {
                _sink = null; _track = null; _queue = null;
                _docCts?.Cancel();
                _readingSelection = false;
                SetState(State.Idle);
            }
        }
        if (session == _readSession && stats?.Error != null) FlashStatus("朗读出错：" + stats.Error, 6000);
    }

    // ---------- 第 3 轮：上一句 / 下一句、朗读剪贴板 ----------

    private int CurrentClauseIndex() => _navIndex ?? _track?.CurrentIndex(_runStart) ?? _runStart;

    /// <summary>从第 index 句重新开始读。先停掉当前这一路（声音立刻停），等当前合成收尾，再接着读。</summary>
    private async Task RestartFromAsync(int index, bool startPaused)
    {
        var q = _queue;
        if (q == null) return;
        int session = ++_readSession;
        _readCts?.Cancel();
        var cts = new CancellationTokenSource();
        _readCts = cts;
        _sink = null;
        _navIndex = index;
        var old = _readTask;
        try { await old; } catch { }
        if (session != _readSession || cts.IsCancellationRequested) return;
        await RunQueueAsync(q, index, Stopwatch.GetTimestamp(), session, cts, startPaused);
    }

    private void NavSentence(int delta)
    {
        if (_queue == null || _state is not (State.Reading or State.Paused))
        {
            FlashStatus("正在朗读时才能用上一句 / 下一句");
            return;
        }
        int cur = CurrentClauseIndex();
        int target = Math.Max(0, cur + delta);
        if (delta > 0 && _queue.Peek(target, out bool end) == null && end) { FlashStatus("已经是最后一句了"); return; }
        Log.Write($"{(delta < 0 ? "上一句" : "下一句")}: 第 {cur + 1} 句 → 第 {target + 1} 句");
        _ = RestartFromAsync(target, startPaused: false);
    }

    /// <summary>Ctrl+Alt+C：朗读剪贴板里的文字（只读剪贴板，不改它）。读不到光标文字的程序都能用。</summary>
    private void OnClipboardKey(long pressedAt)
    {
        if (_state is State.Loading or State.NoModel) { _ = OnMainActionAsync(pressedAt, CurrentTarget("剪贴板")); return; }
        string? text = null;
        for (int i = 0; i < 3 && text == null; i++)
        {
            try { text = Clipboard.ContainsText() ? Clipboard.GetText() : ""; }
            catch (Exception ex) { Log.Write("读剪贴板失败: " + ex.GetType().Name); Thread.Sleep(30); }
        }
        if (string.IsNullOrWhiteSpace(text)) { FlashStatus("剪贴板里没有文字。先复制（Ctrl+C）一段文字，再按 Ctrl+Alt+C。", 4000); return; }
        if (text.Length > CaretTextGrabber.MaxChars) text = text.Substring(0, CaretTextGrabber.MaxChars);
        if (_state is State.Grabbing or State.Reading or State.Paused) StopReading();
        _ = StartReadingAsync(pressedAt, CurrentTarget("剪贴板"), text);
    }

    private bool _addReadingOpen;

    /// <summary>Ctrl+Alt+D：取选中的文字 → 弹「添加读法」小窗；关窗后焦点还给原程序。</summary>
    private async Task OnAddReadingKeyAsync()
    {
        if (_addReadingOpen) return;
        _addReadingOpen = true;
        try
        {
            var target = CurrentTarget("添加读法");
            using var cts = new CancellationTokenSource(4000);
            CaretTextGrabber.Result grab;
            try { grab = await _grabber.GrabAsync(target, cts.Token); }
            catch { grab = new CaretTextGrabber.Result(null, "?", ""); }
            string sel = grab.Selection ? (grab.Text ?? "").Trim() : "";
            if (sel.Length == 0) { FlashStatus("先选中读错的文字，再按 Ctrl+Alt+D。", 4000); return; }
            if (sel.Length > AddReadingForm.MaxSourceChars) { FlashStatus($"选中的太长了（超过 {AddReadingForm.MaxSourceChars} 字），请只选读错的那几个字。", 5000); return; }
            string dir = CaretReader.Core.Settings.TextLayerSettings.UserDataDir(_settingsPath);
            using var f = new AddReadingForm(sel, dir, t => _ = StartReadingAsync(Stopwatch.GetTimestamp(), CurrentTarget("试听读法"), t));
            f.ShowDialog();
            StopReading();
            ReturnFocus();
        }
        catch (Exception ex) { Log.Write("添加读法出错: " + ex.GetType().Name); }
        finally { _addReadingOpen = false; }
    }

    private static async Task FetchRestAsync(Func<string?> rest, ClauseQueue queue, CaretReader.Core.Text.TextLayer layer, CancellationToken token)
    {
        try
        {
            string? more = await Task.Run(rest);
            if (token.IsCancellationRequested || string.IsNullOrEmpty(more)) return;
            var clauses = await Task.Run(() => layer.Split(more, firstChunk: false, CaretTextGrabber.MaxChars));
            queue.Add(clauses);
            Log.Write($"后台读取剩余文字: {more.Length} 字，{clauses.Count} 句");
        }
        catch (Exception ex) { Log.Write("后台读取剩余文字失败: " + ex.Message); }
        finally { queue.Complete(); }
    }

    private void StopReading()
    {
        _readSession++;
        _readCts?.Cancel();
        _readCts = null;
        _docCts?.Cancel();
        _queue = null; _track = null; _navIndex = null;
        _sink = null;
        _pauseRequested = false;
        _player.Stop();   // 正在试听的样本音也一并停（审查意见 3）
        if (_state is State.Reading or State.Grabbing or State.Paused) SetState(State.Idle);
    }

    // =====================================================================
    // 切换音色 / 加载模型
    // =====================================================================
    private async Task OnVoiceChangedAsync()
    {
        if (_voiceMenu.SelectedItem is not VoicePack v) return;
        _settings.VoiceId = v.Id;
        try { _settings.Save(_settingsPath); } catch { }

        // 第 3 轮：朗读中换音色 → 记住读到第几句，换好后从这句继续（不从头来）
        var q = _queue;
        if (q != null && _state is State.Reading or State.Paused)
        {
            int resumeAt = CurrentClauseIndex();
            bool wasPaused = _state == State.Paused;
            int session = ++_readSession;
            _readCts?.Cancel();
            var cts = new CancellationTokenSource();
            _readCts = cts;
            _sink = null;
            try { await _readTask; } catch { }
            if (session != _readSession) return;     // 期间用户按了停止或又换了一次
            Log.Write($"朗读中换音色: 从第 {resumeAt + 1} 句继续");
            await LoadSelectedVoiceAsync();
            if (session != _readSession) return;
            if (_state == State.Idle && !cts.IsCancellationRequested)
                await RunQueueAsync(q, resumeAt, Stopwatch.GetTimestamp(), session, cts, wasPaused);
            else { _docCts?.Cancel(); _queue = null; _track = null; }
            return;
        }
        StopReading();
        await LoadSelectedVoiceAsync();
    }

    private async Task LoadSelectedVoiceAsync(bool forceReload = false)
    {
        if (_voiceMenu.SelectedItem is not VoicePack voice) return;

        if (!forceReload && _engine.IsLoaded(voice, EffectiveThreads(voice.Id)))
        {
            Log.Write($"切换音色: {voice.Id}（同一个模型，免加载）");
            SetState(State.Idle);
            FlashStatus($"已切换到「{voice.Name}」", 2500);
            return;
        }

        if (!_readTask.IsCompleted) { _progressText = "正在停止…"; SetState(State.Loading); }
        try { await _readTask; } catch { }

        _progressText = $"正在加载「{voice.Name}」…";
        SetState(State.Loading);
        CrashGuard.Mark($"开始加载音色「{voice.Name}」（{voice.Id}，{voice.Engine}，线程 {_settings.NumThreads}）");
        _loadExpectSeconds = Math.Max(1, voice.ApproxLoadSeconds);
        _loadClock.Restart();
        _progress = 0;
        _progressTimer.Start();

        try
        {
            int threads = EffectiveThreads(voice.Id);
            double newlyMeasured = 0;
            await Task.Run(() =>
            {
                if (forceReload) _engine.Unload();
                _engine.Load(voice, threads, _appDir);
                // 第 3 轮：这个音色第一次用时，测一次本机速度（约 1～2 秒，只测一次，结果存 data\speed.json）
                if (!_speed.TryGet(voice.Id, out _))
                {
                    try
                    {
                        double rtf = VoiceSpeed.Measure(t => _engine.Synthesize(TextSplitter.TextForEngine(t), voice.Sid, 1f), _engine.SampleRate);
                        if (rtf > 0) { _speed.Set(voice.Id, rtf); newlyMeasured = rtf; }
                    }
                    catch (Exception ex) { Log.Write("测速失败: " + ex.GetType().Name); }
                }
            });
            if (newlyMeasured > 0)
            {
                Log.Write($"本机测速: {voice.Id} RTF {newlyMeasured:0.00}（{VoiceSpeed.Label(VoiceSpeed.Classify(newlyMeasured))}）");
                RefreshVoices(tidy: false);
            }
            CrashGuard.Mark($"模型已加载，就绪（{voice.Id}）");
            Log.Write($"模型已加载: {voice.Id}，用时 {_loadClock.Elapsed.TotalSeconds:0.0}s，采样率 {_engine.SampleRate}，线程 {threads}");
            _progressTimer.Stop();
            _progress = 1; Redraw();
            await Task.Delay(300);
            _progress = null;
            _bubble.HideNow();
            SetState(State.Idle);
            if (newlyMeasured > VoiceSpeed.UsableMax)
                FlashStatus($"「{voice.Name}」在这台电脑上可能卡顿。\n右键「选择音色」，换一个标着「流畅」的试试。", 7000);
        }
        catch (Exception ex)
        {
            _progressTimer.Stop();
            _progress = null;
            _bubble.HideNow();
            Log.Write("模型加载失败: " + ex);
            SetState(State.NoModel);
            ShowError(FriendlyLoadError(ex));
        }
    }

    private int _tierBump;          // A4：运行时卡顿后额外降的档数（本次运行有效）
    private bool _tierHooked;

    /// <summary>A4：自动档位下实际用的线程数；没测过速或关闭自适应时用设置里的值。</summary>
    private int EffectiveThreads(string voiceId)
    {
        if (!_tierHooked)
        {
            _tierHooked = true;
            SpeechPipeline.Stutter.Degraded += () => BeginInvoke(new Action(OnStutter));
        }
        if (!_settings.AutoTier || !_speed.TryGet(voiceId, out double rtf)) return _settings.NumThreads;
        int tier = Math.Min(2, AdaptiveTier.TierOf(rtf) + _tierBump);
        return AdaptiveTier.Threads(tier, Environment.ProcessorCount);
    }

    private void OnStutter()
    {
        if (!_settings.AutoTier || _tierBump >= 2) return;
        _tierBump++;
        Log.Write($"朗读中连续卡顿，自动降一档（当前加成 {_tierBump}）");
        FlashStatus("检测到有点卡，已自动调整，读完这段后生效。", 4000);
        // 读完（回到空闲）后再重载，不打断当前朗读
        var tm = new System.Windows.Forms.Timer { Interval = 1000 };
        tm.Tick += (_, _) =>
        {
            if (_state != State.Idle) return;
            tm.Stop(); tm.Dispose();
            _ = LoadSelectedVoiceAsync(forceReload: true);
        };
        tm.Start();
    }

    private static string FriendlyLoadError(Exception ex)
    {
        var e = ex;
        while (e is TypeInitializationException or TargetInvocationException or AggregateException && e.InnerException != null)
            e = e.InnerException!;
        return e switch
        {
            DllNotFoundException =>
                "缺少程序文件 sherpa-onnx-c-api.dll 或 onnxruntime.dll，无法加载语音模型。\n\n" +
                "请把压缩包完整解压，确认这两个 DLL 和 CaretReader.exe 在同一个文件夹里（不要只拷贝 exe）。",
            BadImageFormatException => "程序文件损坏或版本不对（DLL 不是 64 位版本）。请重新完整解压压缩包。",
            EntryPointNotFoundException => "sherpa-onnx-c-api.dll 的版本和程序不匹配。请重新完整解压压缩包。",
            OutOfMemoryException => "内存不足，无法加载这个音色。请关闭一些程序后再试，或换用较小的音色（zh-ll 或轻量版）。",
            _ => e.Message,
        };
    }

    private void UpdateLoadProgress()
    {
        double t = _loadClock.Elapsed.TotalSeconds;
        double p = Math.Min(0.95, 1 - Math.Exp(-t / (_loadExpectSeconds * 0.6)));
        _progress = p;
        if (_voiceMenu.SelectedItem is VoicePack v) _progressText = $"正在加载「{v.Name}」… {(int)(p * 100)}%";
        Redraw();
        if (_hover || _bubble.Showing) { _bubbleIsHover = false; _bubble.ShowNear(Bounds, _progressText ?? "", 0); }
    }

    // =====================================================================
    // 音色包（扫描 / 试听 / 导入）
    // =====================================================================
    private void RefreshVoices(bool tidy)
    {
        VoiceLibrary.ScanResult scan;
        try { scan = VoiceLibrary.Scan(_appDir, tidy); }
        catch (Exception ex) { Log.Write("扫描音色文件夹出错: " + ex); return; }

        string problems = string.Join("\n", scan.Problems);
        if (problems != _lastScanProblems)
        {
            foreach (var pr in scan.Problems) Log.Write("音色包跳过: " + pr);
            _lastScanProblems = problems;
        }

        var current = _voiceMenu.SelectedItem as VoicePack;
        string? wantId = current?.Id ?? _settings.VoiceId;
        var list = scan.Packs;
        if (current != null && !list.Any(p => p.Id.Equals(current.Id, StringComparison.OrdinalIgnoreCase)) && _engine.LoadedPackId == current.Id)
            list.Add(current);

        VoiceSpeed.Order(list, _speed);   // 流畅 → 可用 → 未测 → 可能卡顿；同档按 rank
        bool same = list.Count == _packs.Count && list.Zip(_packs).All(t => t.First.Id == t.Second.Id && t.First.Name == t.Second.Name
                                                                        && t.First.SamplePath == t.Second.SamplePath
                                                                        && t.First.SpeedLabel == t.Second.SpeedLabel);
        if (same && !tidy) return;
        if (wantId == null && list.Count > 0)
        {
            // 第一次启动：选“流畅”（还没测时按 voice.json 的 rank）里音质最好的
            wantId = VoiceSpeed.PickDefault(list, _speed)?.Id;
            if (wantId != null) { _settings.VoiceId = wantId; try { _settings.Save(_settingsPath); } catch { } Log.Write("首次启动，默认音色: " + wantId); }
        }

        _packs = list;
        _voiceMenu.SetItems(_packs);
        int idx = wantId == null ? -1 : _packs.FindIndex(p => p.Id.Equals(wantId, StringComparison.OrdinalIgnoreCase));
        _voiceMenu.SelectedIndex = idx >= 0 ? idx : (_packs.Count > 0 ? 0 : -1);
        Log.Write($"音色列表: {string.Join("，", _packs.Select(p => p.Id))}（跳过 {scan.Problems.Count} 项）");
    }

    private void PreviewVoice(int index)
    {
        if (index < 0 || index >= _packs.Count) return;
        var p = _packs[index];
        if (p.SamplePath == null) { FlashStatus("这个音色包没有试听文件"); return; }
        if (_player.PlayingId != p.Id && _state == State.Reading) PauseReading();
        Log.Write($"试听: {p.Id}");
        _player.Toggle(p.Id, p.SamplePath);
    }

    private async void ImportVoices(string? dropped = null)
    {
        if (_importing || _state == State.Loading) return;
        StopReading();
        _player.Stop();

        string? folder = dropped;
        if (folder == null)
        using (var dlg = new FolderBrowserDialog
        {
            Description = "选择音色包文件夹（里面有 voice.json；选它外面一两层的文件夹也可以）",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false,
        })
        {
            var r = dlg.ShowDialog(this);
            folder = r == DialogResult.OK ? dlg.SelectedPath : null;
        }
        ReturnFocus();
        if (string.IsNullOrEmpty(folder)) return;

        var before = _state;
        _importing = true;
        _progress = 0;
        FlashStatus("正在检查音色包…", 0);
        var progress = new Progress<(string text, double frac)>(x =>
        {
            if (!_importing || IsDisposed) return;
            _progress = x.frac;
            Redraw();
            _bubble.ShowNear(Bounds, $"{x.text} {(int)(x.frac * 100)}%", 0);
        });
        IProgress<(string, double)> rep = progress;

        VoiceImporter.Result result;
        try
        {
            Log.Write("导入音色包，来源: " + folder);
            result = await Task.Run(() => VoiceImporter.Import(folder, _appDir, (t, f) => rep.Report((t, f)), CancellationToken.None));
        }
        catch (Exception ex)
        {
            Log.Write("导入音色包出错: " + ex);
            result = new VoiceImporter.Result(new() { new VoiceImporter.Item(folder, null, null, ex.Message) });
        }
        finally
        {
            _importing = false;
            _progress = null;
            _bubble.HideNow();
            Redraw();
        }

        RefreshVoices(tidy: false);
        SetState(before == State.NoModel && _engine.LoadedModelId != null ? State.Idle : before);

        var ok = result.Imported.ToList();
        var skipped = result.Items.Where(i => i.Skipped).ToList();
        var bad = result.Items.Where(i => i.Error != null).ToList();
        var msg = new List<string>();
        if (ok.Count > 0) msg.Add("已添加音色：\n  " + string.Join("\n  ", ok.Select(i => i.Name)) + "\n\n右键小人 →「选择音色 / 试听」就能选到；右边的小喇叭可以试听。");
        if (skipped.Count > 0) msg.Add("这些音色已经有了，没有重复添加：\n  " + string.Join("\n  ", skipped.Select(i => i.Name)));
        if (bad.Count > 0) msg.Add("没能导入：\n  " + string.Join("\n  ", bad.Select(i => (i.Name != null ? i.Name + "：" : "") + i.Error)));
        if (msg.Count > 0) ShowError(string.Join("\n\n", msg), info: bad.Count == 0);

        if (before == State.NoModel && _engine.LoadedModelId == null && _packs.Count > 0)
        {
            var first = ok.Select(i => _packs.FindIndex(p => p.Id == i.Id)).FirstOrDefault(i => i >= 0, 0);
            _voiceMenu.SelectedIndex = first;
            await OnVoiceChangedAsync();
        }
    }

    // =====================================================================
    // 设置
    // =====================================================================
    private async void OpenSettings()
    {
        StopReading();
        _player.Stop();
        UnregisterHotkeys();

        using var dlg = new SettingsForm(_settings.Clone(), _settingsPath);
        var result = dlg.ShowDialog(this);
        ReturnFocus();

        if (result != DialogResult.OK)
        {
            ApplyHotkeys(_settings.Hotkey, _settings.StopHotkey, showErrors: true);
            return;
        }

        var n = dlg.Result;
        bool threadsChanged = n.NumThreads != _settings.NumThreads;
        n.VoiceId = _settings.VoiceId;
        n.WindowX = Left; n.WindowY = Top; n.BallX = _settings.BallX; n.BallY = _settings.BallY; n.Appearance = _settings.Appearance;
        _settings = n;
        TopMost = _settings.TopMost;
        AutoStart.Apply(_settings.AutoStart);
        if (!_settings.TopMost) Native.SetWindowPos(Handle, Native.HWND_NOTOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        else ReassertTopMost();
        ApplyHotkeys(_settings.Hotkey, _settings.StopHotkey, showErrors: true);
        _settings.Hotkey = _hotkey.ToString();
        _settings.StopHotkey = _stopHotkey.ToString();
        try { _settings.Save(_settingsPath); }
        catch (Exception ex) { ShowError("保存设置失败：" + ex.Message); }
        Log.Write($"设置已保存: 快捷键 {_settings.Hotkey} / 停止 {_settings.StopHotkey}，语速 {_settings.Speed}，线程 {_settings.NumThreads}，置顶 {_settings.TopMost}");

        if (threadsChanged && _state != State.NoModel)
        {
            StopReading();
            await LoadSelectedVoiceAsync(forceReload: true);
        }
    }

    // =====================================================================
    // 状态
    // =====================================================================
    private void SetState(State s)
    {
        var old = _state;
        _state = s;
        if (s != State.Reading) _mouthLevel = 0;
        if (old != s) { _petFrame = s == State.Reading ? "talk0" : "idle"; _idleSeq = 0; }
        if (_tray != null) { string t = "光标朗读 · " + StatusText(); _tray.Text = t.Length > 63 ? t.Substring(0, 63) : t; }
        UpdateStopButton();
        ScheduleAnim();
    }

    private void ShowError(string message, bool info = false)
    {
        _bubble.HideNow();
        MessageBox.Show(this, message, "光标朗读", MessageBoxButtons.OK, info ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        ReturnFocus();
    }

    /// <summary>菜单 / 对话框关闭后，把前台还给之前在用的程序（记事本 / Word），光标回到原处。</summary>
    private void ReturnFocus()
    {
        var h = _tracker?.LastWindow ?? IntPtr.Zero;
        if (h != IntPtr.Zero && Native.IsWindow(h) && FocusTracker.IsOwn(Native.GetForegroundWindow()))
            Native.SetForegroundWindow(h);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _anim.Dispose(); _hoverTimer.Dispose(); _stop.Dispose(); _toolbar.Dispose(); _tbPoll.Dispose(); _readTimer.Dispose(); _progressTimer.Dispose();
            _readCts?.Dispose();
            _player.Dispose();
            _render.Dispose();
            _bubble.Dispose();
            _menu.Dispose();
            _voiceMenu.Dispose();
            _hotkeys.Dispose();
        }
        base.Dispose(disposing);
    }
}

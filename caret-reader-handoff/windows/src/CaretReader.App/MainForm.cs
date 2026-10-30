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

    private FocusTracker? _tracker;
    private readonly System.Windows.Forms.Timer _anim = new();
    private readonly System.Windows.Forms.Timer _hoverTimer = new() { Interval = 700 };
    private readonly System.Windows.Forms.Timer _progressTimer = new() { Interval = 80 };
    private readonly Stopwatch _loadClock = new();
    private double _loadExpectSeconds = 3;
    private double? _progress;              // 加载 / 导入进度（null = 不显示进度圈）

    // ---------- 外观 ----------
    private readonly BallRenderer _render = new();
    private readonly Bubble _bubble = new();
    private readonly StopButton _stop = new();               // P0：■ 停止按钮
    private readonly InkDropDown _voiceMenu = new();          // 不放进窗口，只用它的音色菜单（带试听小喇叭）
    private readonly ContextMenuStrip _menu = new() { Renderer = new InkMenuRenderer(), ShowImageMargin = false, ShowCheckMargin = false };
    private NotifyIcon? _tray;
    private string _petFrame = "sleep";
    private int _lastDrawnFrame = -1;
    private string _lastDrawnKey = "";
    private long _nextIdleEvent;            // 下一次眨眼 / 翻页的时间（毫秒）
    private int _idleSeq;                   // 正在播放的空闲小动作（0 = 无）
    private int _idleStep;
    private readonly Random _rnd = new();
    private bool _hover;
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

        Text = "光标朗读";
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        ShowInTaskbar = false;
        TopMost = _settings.TopMost;
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
        _hoverTimer.Tick += (_, _) => { _hoverTimer.Stop(); if (_hover && !_leftDown) ShowStatusBubble(); };
        _progressTimer.Tick += (_, _) => UpdateLoadProgress();
        _menu.Font = Ink.UiFont;
        _menu.Closed += (_, _) => { ReturnFocus(); Redraw(); };

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
            cp.ExStyle |= Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW | Native.WS_EX_LAYERED;
            cp.ExStyle &= ~Native.WS_EX_APPWINDOW;
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

    private int BallDiameter => Ink.S(this, 60);

    private void ApplyBallSize()
    {
        _render.EnsureSize(BallDiameter);
        Size = new Size(_render.WindowSize, _render.WindowSize);
        _lastDrawnKey = "";
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
        if (_settings.BallX is int x && _settings.BallY is int y)
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
        string frameName = _state switch
        {
            State.NoModel or State.Loading => "sleep",
            State.Grabbing => "look",
            State.Paused => "look",
            State.Reading => _petFrame.StartsWith("talk") ? _petFrame : "talk0",
            _ => _petFrame is "idle" or "blink" or "page1" or "page2" or "page3" ? _petFrame : "idle",
        };
        string glyph = _state switch
        {
            State.NoModel or State.Loading => "sleep",
            State.Reading or State.Grabbing => "wave",
            _ => "caret",
        };
        string? badge = _state == State.Paused ? "pause" : null;
        int frame = _render.FrameOf(frameName);
        string key = $"{CurrentLook}|{frame}|{glyph}|{badge}|{_hover}|{(_progress is double p ? (int)(p * 100) : -1)}|{Left},{Top}";
        if (key == _lastDrawnKey) return;     // 画面没变：什么都不做（空闲时几乎一直是这样）
        _lastDrawnKey = key;
        _lastDrawnFrame = frame;
        _render.Present(Handle, Location, CurrentLook, frame, _progress, badge, glyph, _hover);
    }

    protected override void OnMove(EventArgs e)
    {
        base.OnMove(e);
        Redraw();
        if (_stop.Showing) _stop.ShowBeside(Bounds);   // 拖动球时 ■ 跟着走
    }

    /// <summary>P0：取文字 / 朗读 / 暂停时显示 ■，其它状态收起。球被隐藏时也收起。</summary>
    private void UpdateStopButton()
    {
        if (IsDisposed || !IsHandleCreated) return;
        bool show = Visible && BallLayout.ShowStop(_state == State.Grabbing, _state == State.Reading, _state == State.Paused);
        if (show) _stop.ShowBeside(Bounds); else _stop.HideNow();
    }

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
        if (CurrentLook != BallRenderer.Look.Pet || _sessionLocked || !Visible) { Redraw(); return; }
        if (_state == State.Reading) { _anim.Interval = 100; _anim.Start(); return; }
        if (_state == State.Idle)
        {
            long now = Environment.TickCount64;
            if (_nextIdleEvent <= now) _nextIdleEvent = now + _rnd.Next(3000, 7000);
            _anim.Interval = (int)Math.Clamp(_nextIdleEvent - now, 50, 10_000);
            _anim.Start();
        }
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
        _hoverTimer.Stop();
        if (e.Button == MouseButtons.Left)
        {
            _leftDown = true;
            _dragging = false;
            _downScreen = Cursor.Position;
            _downWindow = Location;
            // 单击那一刻就确定朗读目标（悬浮球不抢焦点，此时前台仍是文档）
            _clickTarget = CurrentTarget("单击悬浮球");
            Capture = true;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_hover) { _hover = true; Redraw(); _hoverTimer.Start(); }
        if (!_leftDown) return;
        var p = Cursor.Position;
        // P0：按下后移动超过 4px（按 DPI 缩放）算拖动，不算点击
        if (!_dragging && BallLayout.IsDrag(p.X - _downScreen.X, p.Y - _downScreen.Y, Ink.S(this, BallLayout.DragThresholdPx)))
        {
            _dragging = true;
            _bubble.HideNow();
        }
        if (_dragging) Location = new Point(_downWindow.X + p.X - _downScreen.X, _downWindow.Y + p.Y - _downScreen.Y);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button == MouseButtons.Left && _leftDown)
        {
            _leftDown = false;
            Capture = false;
            if (_dragging)
            {
                _dragging = false;
                KeepOnScreen();
                _settings.BallX = Left; _settings.BallY = Top;
                try { _settings.Save(_settingsPath); } catch { }
                return;
            }
            OnLeftClick(Cursor.Position);
        }
        else if (e.Button == MouseButtons.Right)
        {
            ShowMenu(Cursor.Position);
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = false;
        _hoverTimer.Stop();
        if (_state is not State.Loading && !_importing && _bubble.Showing && _bubbleIsHover) _bubble.HideNow();
        Redraw();
    }

    /// <summary>
    /// P0：单击立刻生效（取消了 v0.6 的“双击停止”和等待双击判定的 0.5 秒）。
    ///   * 正在朗读 → 立刻暂停；其它状态 → 开始 / 继续。停止请点球旁边的 ■ 或按停止快捷键。
    /// </summary>
    private void OnLeftClick(Point pt)
    {
        if (_state == State.Reading)
        {
            Log.Write("单击悬浮球：暂停");
            PauseReading();
            return;
        }
        OnSingleClick();
    }

    private void OnSingleClick()
    {
        Log.Write($"单击悬浮球（状态 {_state}）");
        var target = _clickTarget ?? CurrentTarget("单击悬浮球");
        _ = OnMainActionAsync(Stopwatch.GetTimestamp(), target);
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
        var head = Item(StatusText(), null, enabled: false);
        _menu.Items.Add(head);
        _menu.Items.Add(new ToolStripSeparator());

        string act = _state switch { State.Reading or State.Grabbing => "暂停", State.Paused => "继续", _ => "从光标处朗读（单击小人）" };
        bool canAct = _state is State.Idle or State.Reading or State.Paused or State.Grabbing;
        _menu.Items.Add(Item(act, () => _ = OnMainActionAsync(Stopwatch.GetTimestamp(), CurrentTarget("菜单")), canAct));
        _menu.Items.Add(Item("停止（点小人旁边的 ■）", () => { StopReading(); FlashStatus("已停止"); }, _state is State.Reading or State.Paused or State.Grabbing));
        _menu.Items.Add(new ToolStripSeparator());
        string voiceName = (_voiceMenu.SelectedItem as VoicePack)?.Name ?? "（没有音色）";
        _menu.Items.Add(Item($"选择音色 / 试听：{voiceName}", () => BeginInvoke(() => _voiceMenu.ShowAt(new Point(Left, Top))),
                             _state != State.Loading && !_importing && _packs.Count > 0));
        _menu.Items.Add(Item("导入音色包…", ImportVoices, _state != State.Loading && !_importing));
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(Item("始终置顶", ToggleTopMost, check: _settings.TopMost));
        _menu.Items.Add(Item("外观：看书的小人", () => SetAppearance("pet"), _render.HasPet, check: CurrentLook == BallRenderer.Look.Pet));
        _menu.Items.Add(Item("外观：简单圆球（最省资源）", () => SetAppearance("ball"), check: CurrentLook == BallRenderer.Look.Ball));
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(Item("设置…", OpenSettings, _state != State.Loading && !_importing));
        _menu.Items.Add(Item("关于 / 第三方许可", () => { using var f = new AboutForm(); f.ShowDialog(this); ReturnFocus(); }));
        _menu.Items.Add(Item("退出", Close));
    }

    private void ToggleTopMost()
    {
        _settings.TopMost = !_settings.TopMost;
        TopMost = _settings.TopMost;
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
            State.Paused => $"已暂停 · 单击或 {_hotkey} 继续 · {StopHint}",
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
        RefreshVoices(tidy: true);

        CrashGuard.Mark("注册快捷键");
        ApplyHotkeys(_settings.Hotkey, _settings.StopHotkey, showErrors: true);

        if (_packs.Count == 0)
        {
            SetState(State.NoModel);
            ShowError("「音色」文件夹（程序旁边的 voices 文件夹）里没有可用的音色。\n\n" +
                      "请右键小人 →「导入音色包…」，选择一个音色包文件夹；\n或者把音色包文件夹直接复制到 voices 文件夹里。");
            return;
        }
        await LoadSelectedVoiceAsync();
        if (_state == State.Idle) FlashStatus("光标朗读已就绪\n单击小人：从光标处朗读 · 朗读时旁边的 ■：停止 · 右键：菜单", 5000);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        base.OnFormClosing(e);
        _anim.Stop(); _progressTimer.Stop(); _hoverTimer.Stop();
        _stop.HideNow();
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
            _settings.BallX = Left; _settings.BallY = Top;
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

    private async Task StartReadingAsync(long pressedAt, CaretTextGrabber.Target target)
    {
        int session = ++_readSession;
        var cts = new CancellationTokenSource();
        _readCts = cts;
        _pauseRequested = false;
        _readingSelection = false;
        _player.Stop();
        SetState(State.Grabbing);

        CaretTextGrabber.Result grab;
        try { grab = await _grabber.GrabAsync(target, cts.Token); }
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

        var pauses = _settings.Pauses;
        string? text = grab.Text;
        var first = await Task.Run(() => TextSplitter.Split(text, pauses, firstChunk: true, CaretTextGrabber.MaxChars));
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

        var voice = (VoicePack)_voiceMenu.SelectedItem!;
        float speed = (float)Math.Clamp(voice.Speed * _settings.Speed, 0.5, 2.0);
        _readingSelection = grab.Selection;
        var sink = new NAudioSink();
        _sink = sink;
        if (_pauseRequested) { sink.Pause(); SetState(State.Paused); }
        else SetState(State.Reading);
        _pauseRequested = false;

        CrashGuard.Mark($"朗读中（{voice.Id}）");
        var task = SpeechPipeline.RunAsync(queue, _engine, voice.Sid, speed, sink, cts.Token,
                                           onClause: null, hotkeyTimestamp: pressedAt, gain: (float)voice.Gain)
            .ContinueWith(t =>
            {
                var st = t.Result;
                Log.Write($"朗读结束: 小句 {st.Clauses}/{queue.Count}，按键→出声 {st.FirstAudioLatencySeconds:0.00}s" +
                          $"（其中取文字和切分 {st.StartOffsetSeconds:0.00}s），RTF {st.Rtf:0.00}，取消={st.Cancelled}，错误={st.Error ?? "无"}");
                sink.Dispose();
                return st;
            }, TaskScheduler.Default);
        _readTask = task;

        if (grab.Rest != null) _ = FetchRestAsync(grab.Rest, queue, pauses, cts.Token);

        PipelineStats? stats = null;
        try { stats = await task; }
        catch (Exception ex) { Log.Write("朗读任务异常: " + ex); }
        finally
        {
            if (session == _readSession)
            {
                _sink = null;
                _readingSelection = false;
                SetState(State.Idle);
            }
        }
        if (session == _readSession && stats?.Error != null) FlashStatus("朗读出错：" + stats.Error, 6000);
    }

    private static async Task FetchRestAsync(Func<string?> rest, ClauseQueue queue, PauseSettings pauses, CancellationToken token)
    {
        try
        {
            string? more = await Task.Run(rest);
            if (token.IsCancellationRequested || string.IsNullOrEmpty(more)) return;
            var clauses = await Task.Run(() => TextSplitter.Split(more, pauses, firstChunk: false, CaretTextGrabber.MaxChars));
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
        _sink = null;
        _pauseRequested = false;
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
        StopReading();
        await LoadSelectedVoiceAsync();
    }

    private async Task LoadSelectedVoiceAsync(bool forceReload = false)
    {
        if (_voiceMenu.SelectedItem is not VoicePack voice) return;

        if (!forceReload && _engine.IsLoaded(voice, _settings.NumThreads))
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
            int threads = _settings.NumThreads;
            await Task.Run(() =>
            {
                if (forceReload) _engine.Unload();
                _engine.Load(voice, threads, _appDir);
            });
            CrashGuard.Mark($"模型已加载，就绪（{voice.Id}）");
            Log.Write($"模型已加载: {voice.Id}，用时 {_loadClock.Elapsed.TotalSeconds:0.0}s，采样率 {_engine.SampleRate}，线程 {threads}");
            _progressTimer.Stop();
            _progress = 1; Redraw();
            await Task.Delay(300);
            _progress = null;
            _bubble.HideNow();
            SetState(State.Idle);
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

        bool same = list.Count == _packs.Count && list.Zip(_packs).All(t => t.First.Id == t.Second.Id && t.First.Name == t.Second.Name
                                                                        && t.First.SamplePath == t.Second.SamplePath);
        if (same && !tidy) return;

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

    private async void ImportVoices()
    {
        if (_importing || _state == State.Loading) return;
        StopReading();
        _player.Stop();

        string? folder;
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
        n.WindowX = _settings.WindowX; n.WindowY = _settings.WindowY; n.VoiceId = _settings.VoiceId;
        n.BallX = Left; n.BallY = Top; n.Appearance = _settings.Appearance;
        _settings = n;
        TopMost = _settings.TopMost;
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
            _anim.Dispose(); _hoverTimer.Dispose(); _stop.Dispose(); _progressTimer.Dispose();
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

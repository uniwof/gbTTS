using System.Diagnostics;
using System.Reflection;
using CaretReader.App.Ui;
using CaretReader.Core;
namespace CaretReader.App;
internal sealed class MainForm : Form
{
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
    private readonly System.Windows.Forms.Timer _clickTimer = new();
    private readonly System.Windows.Forms.Timer _hoverTimer = new() { Interval = 700 };
    private readonly System.Windows.Forms.Timer _progressTimer = new() { Interval = 80 };
    private readonly Stopwatch _loadClock = new();
    private double _loadExpectSeconds = 3;
    private double? _progress;
    private readonly BallRenderer _render = new();
    private readonly Bubble _bubble = new();
    private readonly InkDropDown _voiceMenu = new();
    private readonly ContextMenuStrip _menu = new() { Renderer = new InkMenuRenderer(), ShowImageMargin = false, ShowCheckMargin = false };
    private NotifyIcon? _tray;
    private string _petFrame = "sleep";
    private int _lastDrawnFrame = -1;
    private string _lastDrawnKey = "";
    private long _nextIdleEvent;
    private int _idleSeq;
    private int _idleStep;
    private readonly Random _rnd = new();
    private bool _hover;
    private bool _sessionLocked;
    private float _mouthLevel;
    private bool _leftDown, _dragging;
    private Point _downScreen, _downWindow;
    private bool _singlePending;
    private CaretTextGrabber.Target? _clickTarget;
    public MainForm()
    {
        _settingsPath = AppSettings.ResolvePath(_appDir);
        _settings = AppSettings.Load(_settingsPath);
        Text = "<zh>";
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
        _voiceMenu.Tooltip = o => o is VoicePack p ? (string.IsNullOrWhiteSpace(p.Description) ? "" : p.Description + "\n") + "<zh>" : null;
        _voiceMenu.MenuClosed += (_, _) => ReturnFocus();
        _player.StateChanged += () => _voiceMenu.RefreshSpeakers();
        _player.Failed += msg => FlashStatus(msg);
        _hotkeys.Pressed += OnHotkey;
        _anim.Tick += (_, _) => AnimTick();
        _clickTimer.Tick += (_, _) => { _clickTimer.Stop(); if (_singlePending) { _singlePending = false; OnSingleClick(); } };
        _hoverTimer.Tick += (_, _) => { _hoverTimer.Stop(); if (_hover && !_leftDown) ShowStatusBubble(); };
        _progressTimer.Tick += (_, _) => UpdateLoadProgress();
        _menu.Font = Ink.UiFont;
        _menu.Closed += (_, _) => { ReturnFocus(); Redraw(); };
        Microsoft.Win32.SystemEvents.SessionSwitch += OnSessionSwitch;
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
    }
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
        var pos = new Point(wa.Right - Width - margin, wa.Bottom - Height - margin);
        if (_settings.BallX is int x && _settings.BallY is int y)
        {
            var r = new Rectangle(x, y, Width, Height);
            if (Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(Rectangle.Inflate(r, -Width / 3, -Height / 3)))) pos = new Point(x, y);
        }
        Location = pos;
        KeepOnScreen();
    }
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
    private void ReassertTopMost()
    {
        if (!_settings.TopMost || !IsHandleCreated) return;
        Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }
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
        if (key == _lastDrawnKey) return;
        _lastDrawnKey = key;
        _lastDrawnFrame = frame;
        _render.Present(Handle, Location, CurrentLook, frame, _progress, badge, glyph, _hover);
    }
    protected override void OnMove(EventArgs e)
    {
        base.OnMove(e);
        Redraw();
    }
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
            _mouthLevel = Math.Max(lv, _mouthLevel * 0.55f);
            _petFrame = _mouthLevel < 0.06f ? "talk0" : _mouthLevel < 0.22f ? "talk1" : "talk2";
            Redraw();
            return;
        }
        if (_state != State.Idle) { _anim.Stop(); return; }
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
            _clickTarget = CurrentTarget("<zh>");
            Capture = true;
        }
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_hover) { _hover = true; Redraw(); _hoverTimer.Start(); }
        if (!_leftDown) return;
        var p = Cursor.Position;
        var drag = SystemInformation.DragSize;
        if (!_dragging && (Math.Abs(p.X - _downScreen.X) > Math.Max(4, drag.Width) || Math.Abs(p.Y - _downScreen.Y) > Math.Max(4, drag.Height)))
        {
            _dragging = true;
            _bubble.HideNow();
            _singlePending = false; _clickTimer.Stop();
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
    private void OnLeftClick(Point pt)
    {
        var dbl = SystemInformation.DoubleClickSize;
        _clicks ??= new ClickClassifier(SystemInformation.DoubleClickTime, Math.Max(dbl.Width, dbl.Height));
        switch (_clicks.OnClick(Environment.TickCount64, pt.X, pt.Y, reading: _state == State.Reading))
        {
            case ClickClassifier.Action.Double:
                _singlePending = false; _clickTimer.Stop();
                OnDoubleClickBall();
                return;
            case ClickClassifier.Action.PauseNow:
                Log.Write("<zh>");
                PauseReading();
                return;
            default:
                _singlePending = true;
                _clickTimer.Interval = _clicks.DeferMs;
                _clickTimer.Stop(); _clickTimer.Start();
                return;
        }
    }
    private ClickClassifier? _clicks;
    private void OnSingleClick()
    {
        Log.Write("<zh>");
        var target = _clickTarget ?? CurrentTarget("<zh>");
        _ = OnMainActionAsync(Stopwatch.GetTimestamp(), target);
    }
    private void OnDoubleClickBall()
    {
        Log.Write("<zh>");
        if (_state is State.Grabbing or State.Reading or State.Paused) { StopReading(); FlashStatus("<zh>"); }
        else FlashStatus("<zh>");
    }
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
        string act = _state switch { State.Reading or State.Grabbing => "<zh>", State.Paused => "<zh>", _ => "<zh>" };
        bool canAct = _state is State.Idle or State.Reading or State.Paused or State.Grabbing;
        _menu.Items.Add(Item(act, () => _ = OnMainActionAsync(Stopwatch.GetTimestamp(), CurrentTarget("<zh>")), canAct));
        _menu.Items.Add(Item("<zh>", () => { StopReading(); FlashStatus("<zh>"); }, _state is State.Reading or State.Paused or State.Grabbing));
        _menu.Items.Add(new ToolStripSeparator());
        string voiceName = (_voiceMenu.SelectedItem as VoicePack)?.Name ?? "<zh>";
        _menu.Items.Add(Item("<zh>", () => BeginInvoke(() => _voiceMenu.ShowAt(new Point(Left, Top))),
                             _state != State.Loading && !_importing && _packs.Count > 0));
        _menu.Items.Add(Item("<zh>", ImportVoices, _state != State.Loading && !_importing));
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(Item("<zh>", ToggleTopMost, check: _settings.TopMost));
        _menu.Items.Add(Item("<zh>", () => SetAppearance("pet"), _render.HasPet, check: CurrentLook == BallRenderer.Look.Pet));
        _menu.Items.Add(Item("<zh>", () => SetAppearance("ball"), check: CurrentLook == BallRenderer.Look.Ball));
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(Item("<zh>", OpenSettings, _state != State.Loading && !_importing));
        _menu.Items.Add(Item("<zh>", () => { using var f = new AboutForm(); f.ShowDialog(this); ReturnFocus(); }));
        _menu.Items.Add(Item("<zh>", Close));
    }
    private void ToggleTopMost()
    {
        _settings.TopMost = !_settings.TopMost;
        TopMost = _settings.TopMost;
        if (!_settings.TopMost) Native.SetWindowPos(Handle, Native.HWND_NOTOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        else ReassertTopMost();
        try { _settings.Save(_settingsPath); } catch { }
        FlashStatus(_settings.TopMost ? "<zh>" : "<zh>");
    }
    private void SetAppearance(string look)
    {
        _settings.Appearance = look;
        try { _settings.Save(_settingsPath); } catch { }
        _lastDrawnKey = "";
        ScheduleAnim();
        Log.Write("<zh>" + look);
    }
    private void SetupTray()
    {
        try
        {
            _tray = new NotifyIcon { Icon = Icon ?? SystemIcons.Application, Text = "<zh>", Visible = true };
            _tray.MouseUp += (_, e) =>
            {
                if (e.Button == MouseButtons.Right) ShowMenu(Cursor.Position);
                else if (e.Button == MouseButtons.Left)
                {
                    if (!Visible) Show();
                    _settings.BallX = null; _settings.BallY = null;
                    RestoreBallPosition();
                    ReassertTopMost();
                    FlashStatus("<zh>");
                }
            };
        }
        catch (Exception ex) { Log.Write("<zh>" + ex.Message); }
    }
    private bool _bubbleIsHover;
    private void ShowStatusBubble()
    {
        if (_bubble.Showing && !_bubbleIsHover) return;
        _bubbleIsHover = true;
        _bubble.ShowNear(Bounds, StatusText() + "<zh>", 6000);
    }
    private void FlashStatus(string text, int ms = 4000)
    {
        if (IsDisposed) return;
        _bubbleIsHover = false;
        _bubble.ShowNear(Bounds, text, ms);
    }
    private string StopHint => _stopRegistered ? "<zh>" : "<zh>";
    private string StatusText()
    {
        string ed = string.IsNullOrEmpty(_edition) ? "" : _edition + " · ";
        return _state switch
        {
            State.NoModel => _packs.Count == 0 ? "<zh>" : "<zh>",
            State.Loading => _progressText ?? "<zh>",
            State.Idle => _mainRegistered ? "<zh>" : "<zh>",
            State.Grabbing => "<zh>",
            State.Reading => $"{(_readingSelection ? "朗读选中" : "朗读中"<zh>",
            State.Paused => "<zh>",
            _ => "",
        };
    }
    private string? _progressText;
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
            !lastStage.Contains("<zh>") && !lastStage.Contains("<zh>") && !lastStage.Contains("<zh>"))
        {
            ShowError("<zh>" + lastStage + "）。\n\n" +
                      "<zh>" +
                      "<zh>");
        }
        CrashGuard.Mark("<zh>");
        try { _edition = File.ReadAllText(Path.Combine(_appDir, "edition.txt")).Trim(); } catch { _edition = ""; }
        if (_edition.Length > 12) _edition = _edition.Substring(0, 12);
        if (_edition != "") { Text = "<zh>" + _edition; if (_tray != null) _tray.Text = Text; }
        VoiceImporter.CleanupTemp(_appDir);
        RefreshVoices(tidy: true);
        CrashGuard.Mark("<zh>");
        ApplyHotkeys(_settings.Hotkey, _settings.StopHotkey, showErrors: true);
        if (_packs.Count == 0)
        {
            SetState(State.NoModel);
            ShowError("<zh>" +
                      "<zh>");
            return;
        }
        await LoadSelectedVoiceAsync();
        if (_state == State.Idle) FlashStatus("<zh>", 5000);
    }
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        base.OnFormClosing(e);
        _anim.Stop(); _progressTimer.Stop(); _clickTimer.Stop(); _hoverTimer.Stop();
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
        catch (Exception ex) { Log.Write("<zh>" + ex.Message); }
        CrashGuard.Mark("<zh>");
        try { _readTask.Wait(3000); } catch { }
        try { Task.Run(_engine.Dispose).Wait(3000); } catch { }
        Log.Write("<zh>");
        CrashGuard.CleanExit();
    }
    private void OnHotkey(int id, long pressedAt)
    {
        if (id == Hotkey.StopId) { Log.Write("<zh>"); OnStopKey(); return; }
        var target = CurrentTarget("<zh>");
        Log.Write("<zh>");
        _ = OnMainActionAsync(pressedAt, target);
    }
    private CaretTextGrabber.Target CurrentTarget(string trigger)
    {
        var fg = Native.GetForegroundWindow();
        if (fg != IntPtr.Zero && !FocusTracker.IsOwn(fg))
        {
            _tracker?.Update(fg);
            return CaretTextGrabber.Target.Describe(fg, Native.ThreadFocusOf(fg).focus, trigger);
        }
        var last = _tracker?.LastWindow ?? IntPtr.Zero;
        return CaretTextGrabber.Target.Describe(last, _tracker?.LastFocus ?? IntPtr.Zero, trigger + (fg == IntPtr.Zero ? "<zh>" : "<zh>"));
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
            Log.Write("<zh>");
            errors.Add("<zh>");
            return fallback;
        }
        if (hk.Value.Problem is string why)
        {
            Log.Write("<zh>");
            errors.Add("<zh>");
            return fallback;
        }
        return hk.Value;
    }
    private bool ApplyHotkeys(string mainText, string stopText, bool showErrors)
    {
        var errors = new List<string>();
        var main = Sanitize(mainText, Hotkey.Default, "<zh>", errors);
        var stop = Sanitize(stopText, Hotkey.DefaultStop, "<zh>", errors);
        UnregisterHotkeys();
        _mainRegistered = TryRegister(main, Hotkey.MainId, _hotkey, "<zh>", errors, out _hotkey);
        if (stop.Equals(_hotkey))
        {
            Log.Write("<zh>");
            errors.Add("<zh>");
            _stopRegistered = false;
        }
        else
        {
            var oldStop = _stopHotkey.Equals(_hotkey) ? stop : _stopHotkey;
            _stopRegistered = TryRegister(stop, Hotkey.StopId, oldStop, "<zh>", errors, out _stopHotkey);
        }
        if (errors.Count > 0 && showErrors) ShowError(string.Join("\n\n", errors));
        return errors.Count == 0;
    }
    private bool TryRegister(Hotkey wanted, int id, Hotkey previous, string name, List<string> errors, out Hotkey result)
    {
        if (wanted.Register(_hotkeys.Handle, id))
        {
            Log.Write("<zh>");
            result = wanted;
            return true;
        }
        Log.Write("<zh>");
        if (!wanted.Equals(previous) && previous.Register(_hotkeys.Handle, id))
        {
            errors.Add("<zh>");
            result = previous;
            return true;
        }
        errors.Add("<zh>");
        result = previous;
        return false;
    }
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
                FlashStatus(_pauseRequested ? "<zh>" : "<zh>", 2000);
                return;
            case State.Loading:
                FlashStatus("<zh>");
                return;
            case State.NoModel:
                FlashStatus(_packs.Count == 0 ? "<zh>" : "<zh>");
                return;
        }
    }
    private void OnStopKey()
    {
        if (_state is State.Grabbing or State.Reading or State.Paused)
        {
            StopReading();
            FlashStatus("<zh>");
        }
    }
    private void PauseReading()
    {
        _sink?.Pause();
        SetState(State.Paused);
        Log.Write("<zh>");
    }
    private void ResumeReading()
    {
        _player.Stop();
        _sink?.Resume();
        SetState(State.Reading);
        Log.Write("<zh>");
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
            Log.Write("<zh>" + ex);
            grab = new CaretTextGrabber.Result(null, "?", ex.Message);
        }
        if (session != _readSession || cts.IsCancellationRequested) return;
        double grabSeconds = Stopwatch.GetElapsedTime(pressedAt).TotalSeconds;
        Log.Write("<zh>"（选中的文字）" : ""<zh>"（其余后台读取）" : "")}，" +
                  "<zh>");
        var pauses = _settings.Pauses;
        string? text = grab.Text;
        var first = await Task.Run(() => TextSplitter.Split(text, pauses, firstChunk: true, CaretTextGrabber.MaxChars));
        if (session != _readSession || cts.IsCancellationRequested) return;
        if (first.Count == 0 && grab.Rest == null)
        {
            SetState(State.Idle);
            FlashStatus(grab.Unsupported ? (grab.Detail ?? "<zh>")
                      : grab.NoCaret ? "<zh>"
                      : grab.Text == null ? "<zh>"
                      : grab.Selection ? "<zh>" : "<zh>", 6000);
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
        CrashGuard.Mark("<zh>");
        var task = SpeechPipeline.RunAsync(queue, _engine, voice.Sid, speed, sink, cts.Token,
                                           onClause: null, hotkeyTimestamp: pressedAt, gain: (float)voice.Gain)
            .ContinueWith(t =>
            {
                var st = t.Result;
                Log.Write("<zh>" +
                          "<zh>"无"}");
                sink.Dispose();
                return st;
            }, TaskScheduler.Default);
        _readTask = task;
        if (grab.Rest != null) _ = FetchRestAsync(grab.Rest, queue, pauses, cts.Token);
        PipelineStats? stats = null;
        try { stats = await task; }
        catch (Exception ex) { Log.Write("<zh>" + ex); }
        finally
        {
            if (session == _readSession)
            {
                _sink = null;
                _readingSelection = false;
                SetState(State.Idle);
            }
        }
        if (session == _readSession && stats?.Error != null) FlashStatus("<zh>" + stats.Error, 6000);
    }
    private static async Task FetchRestAsync(Func<string?> rest, ClauseQueue queue, PauseSettings pauses, CancellationToken token)
    {
        try
        {
            string? more = await Task.Run(rest);
            if (token.IsCancellationRequested || string.IsNullOrEmpty(more)) return;
            var clauses = await Task.Run(() => TextSplitter.Split(more, pauses, firstChunk: false, CaretTextGrabber.MaxChars));
            queue.Add(clauses);
            Log.Write("<zh>");
        }
        catch (Exception ex) { Log.Write("<zh>" + ex.Message); }
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
            Log.Write("<zh>");
            SetState(State.Idle);
            FlashStatus("<zh>", 2500);
            return;
        }
        if (!_readTask.IsCompleted) { _progressText = "<zh>"; SetState(State.Loading); }
        try { await _readTask; } catch { }
        _progressText = "<zh>";
        SetState(State.Loading);
        CrashGuard.Mark("<zh>");
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
            CrashGuard.Mark("<zh>");
            Log.Write("<zh>");
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
            Log.Write("<zh>" + ex);
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
                "<zh>" +
                "<zh>",
            BadImageFormatException => "<zh>",
            EntryPointNotFoundException => "<zh>",
            OutOfMemoryException => "<zh>",
            _ => e.Message,
        };
    }
    private void UpdateLoadProgress()
    {
        double t = _loadClock.Elapsed.TotalSeconds;
        double p = Math.Min(0.95, 1 - Math.Exp(-t / (_loadExpectSeconds * 0.6)));
        _progress = p;
        if (_voiceMenu.SelectedItem is VoicePack v) _progressText = "<zh>";
        Redraw();
        if (_hover || _bubble.Showing) { _bubbleIsHover = false; _bubble.ShowNear(Bounds, _progressText ?? "", 0); }
    }
    private void RefreshVoices(bool tidy)
    {
        VoiceLibrary.ScanResult scan;
        try { scan = VoiceLibrary.Scan(_appDir, tidy); }
        catch (Exception ex) { Log.Write("<zh>" + ex); return; }
        string problems = string.Join("\n", scan.Problems);
        if (problems != _lastScanProblems)
        {
            foreach (var pr in scan.Problems) Log.Write("<zh>" + pr);
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
        Log.Write("<zh>"，"<zh>");
    }
    private void PreviewVoice(int index)
    {
        if (index < 0 || index >= _packs.Count) return;
        var p = _packs[index];
        if (p.SamplePath == null) { FlashStatus("<zh>"); return; }
        if (_player.PlayingId != p.Id && _state == State.Reading) PauseReading();
        Log.Write("<zh>");
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
            Description = "<zh>",
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
        FlashStatus("<zh>", 0);
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
            Log.Write("<zh>" + folder);
            result = await Task.Run(() => VoiceImporter.Import(folder, _appDir, (t, f) => rep.Report((t, f)), CancellationToken.None));
        }
        catch (Exception ex)
        {
            Log.Write("<zh>" + ex);
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
        if (ok.Count > 0) msg.Add("<zh>" + string.Join("\n  ", ok.Select(i => i.Name)) + "<zh>");
        if (skipped.Count > 0) msg.Add("<zh>" + string.Join("\n  ", skipped.Select(i => i.Name)));
        if (bad.Count > 0) msg.Add("<zh>" + string.Join("\n  ", bad.Select(i => (i.Name != null ? i.Name + "：" : "") + i.Error)));
        if (msg.Count > 0) ShowError(string.Join("\n\n", msg), info: bad.Count == 0);
        if (before == State.NoModel && _engine.LoadedModelId == null && _packs.Count > 0)
        {
            var first = ok.Select(i => _packs.FindIndex(p => p.Id == i.Id)).FirstOrDefault(i => i >= 0, 0);
            _voiceMenu.SelectedIndex = first;
            await OnVoiceChangedAsync();
        }
    }
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
        catch (Exception ex) { ShowError("<zh>" + ex.Message); }
        Log.Write("<zh>");
        if (threadsChanged && _state != State.NoModel)
        {
            StopReading();
            await LoadSelectedVoiceAsync(forceReload: true);
        }
    }
    private void SetState(State s)
    {
        var old = _state;
        _state = s;
        if (s != State.Reading) _mouthLevel = 0;
        if (old != s) { _petFrame = s == State.Reading ? "talk0" : "idle"; _idleSeq = 0; }
        if (_tray != null) { string t = "<zh>" + StatusText(); _tray.Text = t.Length > 63 ? t.Substring(0, 63) : t; }
        ScheduleAnim();
    }
    private void ShowError(string message, bool info = false)
    {
        _bubble.HideNow();
        MessageBox.Show(this, message, "<zh>", MessageBoxButtons.OK, info ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        ReturnFocus();
    }
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
            _anim.Dispose(); _clickTimer.Dispose(); _hoverTimer.Dispose(); _progressTimer.Dispose();
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

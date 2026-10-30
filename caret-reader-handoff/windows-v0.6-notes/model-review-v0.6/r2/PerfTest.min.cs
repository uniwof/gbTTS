using System.Diagnostics;
using CaretReader.App.Ui;
using CaretReader.Core;
namespace CaretReader.App;
internal static class PerfTest
{
    public static int Run(string logDir)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        using var f = new PerfForm(logDir);
        Application.Run(f);
        return 0;
    }
    private sealed class PerfForm : Form
    {
        private readonly string _logDir;
        private readonly BallRenderer _render = new();
        private readonly System.Windows.Forms.Timer _tick = new();
        private readonly Stopwatch _phaseClock = new();
        private readonly Random _rnd = new(1);
        private readonly List<string> _report = new();
        private int _phase = -1;
        private long _cpu0Ms;
        private long _paintTicks, _paintCount;
        private long _nextIdle;
        private int _idleStep = -1;
        private string[] _idleSeq = Array.Empty<string>();
        private readonly List<long> _ws = new(), _priv = new();
        private readonly System.Windows.Forms.Timer _sampler = new() { Interval = 1000 };
        private static readonly (string name, int seconds)[] Phases =
        {
            ("<zh>", 20),
            ("<zh>", 30),
            ("<zh>", 30),
            ("<zh>", 25),
            ("<zh>", 25),
        };
        public PerfForm(string logDir)
        {
            _logDir = logDir;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            Text = "<zh>";
            _tick.Tick += (_, _) => OnTick();
            _sampler.Tick += (_, _) => Sample();
        }
        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW | Native.WS_EX_LAYERED;
                return cp;
            }
        }
        protected override bool ShowWithoutActivation => true;
        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            _render.EnsureSize(Ink.S(this, 60));
            Size = new Size(_render.WindowSize, _render.WindowSize);
            var wa = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 720);
            Location = new Point(wa.Right - Width - Ink.S(this, 24), wa.Bottom - Height - Ink.S(this, 24));
            _report.Add("<zh>");
            _report.Add("<zh>");
            _report.Add("<zh>"已加载" : "没有加载（只能测圆球）"<zh>");
            _report.Add("<zh>");
            _report.Add("");
            Paint1("idle", BallRenderer.Look.Pet, "caret");
            NextPhase();
        }
        private void Paint1(string frame, BallRenderer.Look look, string glyph)
        {
            long t = Stopwatch.GetTimestamp();
            _render.Present(Handle, Location, look, _render.FrameOf(frame), null, null, glyph, false);
            _paintTicks += Stopwatch.GetTimestamp() - t;
            _paintCount++;
        }
        private void NextPhase()
        {
            if (_phase >= 0) EndPhase();
            _phase++;
            if (_phase >= Phases.Length) { Finish(); return; }
            _paintTicks = _paintCount = 0;
            _ws.Clear(); _priv.Clear();
            GC.Collect();
            var p = Process.GetCurrentProcess();
            _cpu0Ms = (long)p.TotalProcessorTime.TotalMilliseconds;
            _phaseClock.Restart();
            _tick.Stop();
            switch (_phase)
            {
                case 0: Paint1("idle", BallRenderer.Look.Pet, "caret"); break;
                case 1: _tick.Interval = 100; _tick.Start(); break;
                case 2: _nextIdle = Environment.TickCount64 + _rnd.Next(3000, 7000); _tick.Interval = 50; _tick.Start(); break;
                case 3: Paint1("idle", BallRenderer.Look.Ball, "wave"); _tick.Interval = 1000; _tick.Start(); break;
                case 4: Paint1("idle", BallRenderer.Look.Ball, "caret"); _tick.Interval = 1000; _tick.Start(); break;
            }
            _sampler.Start();
        }
        private string _lastFrame = "";
        private float _mouth;
        private void OnTick()
        {
            if (_phaseClock.Elapsed.TotalSeconds >= Phases[_phase].seconds) { NextPhase(); return; }
            switch (_phase)
            {
                case 1:
                {
                    float lv = _rnd.Next(10) == 0 ? 0f : (float)(0.1 + _rnd.NextDouble() * 0.5);
                    _mouth = Math.Max(lv, _mouth * 0.55f);
                    string f = _mouth < 0.06f ? "talk0" : _mouth < 0.22f ? "talk1" : "talk2";
                    if (f != _lastFrame) { _lastFrame = f; Paint1(f, BallRenderer.Look.Pet, "wave"); }
                    break;
                }
                case 2:
                {
                    long now = Environment.TickCount64;
                    if (_idleStep < 0)
                    {
                        if (now < _nextIdle) { _tick.Interval = (int)Math.Clamp(_nextIdle - now, 50, 10_000); return; }
                        _idleSeq = _rnd.Next(5) == 0 ? new[] { "page1", "page2", "page3", "idle" } : new[] { "blink", "idle" };
                        _idleStep = 0;
                    }
                    Paint1(_idleSeq[_idleStep], BallRenderer.Look.Pet, "caret");
                    _idleStep++;
                    if (_idleStep >= _idleSeq.Length) { _idleStep = -1; _nextIdle = now + (_rnd.Next(4) == 0 ? _rnd.Next(15000, 30000) : _rnd.Next(3000, 7000)); }
                    _tick.Interval = 145;
                    break;
                }
                default: break;
            }
        }
        private void Sample()
        {
            var p = Process.GetCurrentProcess();
            _ws.Add(p.WorkingSet64); _priv.Add(p.PrivateMemorySize64);
            if (_phase == 0 && _phaseClock.Elapsed.TotalSeconds >= Phases[0].seconds) NextPhase();
        }
        private void EndPhase()
        {
            _sampler.Stop();
            var p = Process.GetCurrentProcess();
            double secs = _phaseClock.Elapsed.TotalSeconds;
            double cpuMs = p.TotalProcessorTime.TotalMilliseconds - _cpu0Ms;
            double cpuPct = cpuMs / (secs * 1000) * 100;
            double perFrameUs = _paintCount == 0 ? 0 : _paintTicks * 1_000_000.0 / Stopwatch.Frequency / _paintCount;
            string mem = _ws.Count == 0 ? "" :
                "<zh>";
            string line = "<zh>";
            _report.Add(line);
            Log.Write("<zh>" + line);
        }
        private void Finish()
        {
            _tick.Stop(); _sampler.Stop();
            _report.Add("");
            _report.Add("<zh>");
            try { File.WriteAllLines(Path.Combine(_logDir, "<zh>"), _report, new System.Text.UTF8Encoding(true)); } catch { }
            Close();
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) { _tick.Dispose(); _sampler.Dispose(); _render.Dispose(); }
            base.Dispose(disposing);
        }
    }
}

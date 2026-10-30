using System.Diagnostics;
using CaretReader.App.Ui;
using CaretReader.Core;

namespace CaretReader.App;

/// <summary>
/// v0.6 隐藏的资源实测模式：CaretReader.exe --perftest（或双击「资源实测.bat」）。
///
/// 在屏幕右下角放一个和正式版一模一样的悬浮窗（同样的分层窗口、同样的画法），依次跑 5 段：
///   1) 基线：窗口静止不重画
///   2) 小人 · 朗读动画：每 100 毫秒按模拟音量换嘴型（和正式版朗读时完全相同的节拍）
///   3) 小人 · 空闲：每 3~7 秒眨眼、偶尔翻页（和正式版相同）
///   4) 圆球 · 朗读：正式版圆球朗读时不做动画，只在状态变化时画一次
///   5) 圆球 · 空闲：同上
/// 每段记录 CPU 占用、内存（工作集 / 私有内存）、GDI 对象数、每画一帧的平均耗时。
/// 朗读本身（语音合成）的开销两种外观完全一样，所以这里不合成声音，只量“外观”这一项的差别。
/// 结果写到 log.txt 同目录的「资源实测结果.txt」，跑完自动退出（约 2 分 10 秒）。
/// </summary>
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
        private readonly Process _self = Process.GetCurrentProcess();   // 只取一次（审查意见：每秒新建 Process 对象会漏句柄）

        private static readonly (string name, int seconds)[] Phases =
        {
            ("基线：窗口静止（不重画）", 20),
            ("小人 · 朗读动画（每 0.1 秒换嘴型）", 30),
            ("小人 · 空闲（3~7 秒眨眼，偶尔翻页）", 30),
            ("圆球 · 朗读（不做动画）", 25),
            ("圆球 · 空闲（不做动画）", 25),
        };

        public PerfForm(string logDir)
        {
            _logDir = logDir;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            Text = "光标朗读 · 资源实测";
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
            _report.Add($"光标朗读 {Program.Version} 资源实测  {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            _report.Add($"电脑：{Environment.OSVersion.VersionString}，{Environment.ProcessorCount} 个逻辑处理器，屏幕缩放 {DeviceDpi * 100 / 96}%");
            _report.Add($"小人精灵图：{(_render.HasPet ? "已加载" : "没有加载（只能测圆球）")}；悬浮窗 {Width}×{Height} 像素");
            _report.Add("说明：CPU% 按“占满一个核 = 100%”计；朗读时语音合成的开销两种外观相同，这里只量外观本身。");
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
            var p = _self; p.Refresh();
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
                    // 模拟朗读音量：说话时 0.1~0.6 随机，句间偶尔停顿
                    float lv = _rnd.Next(10) == 0 ? 0f : (float)(0.1 + _rnd.NextDouble() * 0.5);
                    _mouth = Math.Max(lv, _mouth * 0.55f);
                    string f = _mouth < 0.06f ? "talk0" : _mouth < 0.22f ? "talk1" : "talk2";
                    if (f != _lastFrame) { _lastFrame = f; Paint1(f, BallRenderer.Look.Pet, "wave"); }   // 和正式版一样：画面没变就不画
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
                default: break;   // 圆球：什么都不做（正式版也一样）
            }
        }

        private void Sample()
        {
            var p = _self; p.Refresh();
            _ws.Add(p.WorkingSet64); _priv.Add(p.PrivateMemorySize64);
            if (_phase == 0 && _phaseClock.Elapsed.TotalSeconds >= Phases[0].seconds) NextPhase();
        }

        private void EndPhase()
        {
            _sampler.Stop();
            var p = _self; p.Refresh();
            double secs = _phaseClock.Elapsed.TotalSeconds;
            double cpuMs = p.TotalProcessorTime.TotalMilliseconds - _cpu0Ms;
            double cpuPct = cpuMs / (secs * 1000) * 100;
            double perFrameUs = _paintCount == 0 ? 0 : _paintTicks * 1_000_000.0 / Stopwatch.Frequency / _paintCount;
            string mem = _ws.Count == 0 ? "" :
                $"工作集 平均 {_ws.Average() / 1048576:0.0} MB（峰值 {_ws.Max() / 1048576.0:0.0}），私有内存 平均 {_priv.Average() / 1048576:0.0} MB";
            string line = $"{Phases[_phase].name}：{secs:0} 秒，CPU {cpuPct:0.00}%（单核），共 {cpuMs:0} 毫秒；画了 {_paintCount} 帧，每帧 {perFrameUs:0} 微秒；{mem}；GDI 对象 {Native.GdiObjects()}";
            _report.Add(line);
            Log.Write("资源实测 " + line);
        }

        private void Finish()
        {
            _tick.Stop(); _sampler.Stop();
            _report.Add("");
            _report.Add("对照：正式版加载一个音色后整个程序约 150~500 MB（看音色）；朗读时语音合成约占 1~2 个核的一部分。");
            try { File.WriteAllLines(Path.Combine(_logDir, "资源实测结果.txt"), _report, new System.Text.UTF8Encoding(true)); } catch { }
            Close();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { _tick.Dispose(); _sampler.Dispose(); _render.Dispose(); }
            base.Dispose(disposing);
        }
    }
}

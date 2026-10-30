using System.Diagnostics;
using CaretReader.Core;

namespace CaretReader.App;

/// <summary>程序入口。</summary>
internal static class Program
{
    public const string Version = "v0.6";

    [STAThread] // WinForms 和剪贴板都要求“单线程单元”(STA)
    private static int Main(string[] args)
    {
        string appDir = AppContext.BaseDirectory;

        // v0.3：日志最先初始化（以前在主窗口里才初始化，更早出的错就没有记录）
        string settingsPath;
        try { settingsPath = AppSettings.ResolvePath(appDir); }
        catch { settingsPath = Path.Combine(appDir, "settings.json"); }
        string logDir = Path.GetDirectoryName(settingsPath)!;
        Log.Init(Path.Combine(logDir, "log.txt"));

        // 任何没被捕获的错误：写日志（完整调用栈）+ 用中文提示，而不是直接闪退
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Report(e.ExceptionObject as Exception, "后台线程", fatal: e.IsTerminating);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Write("未处理的后台任务错误: " + e.Exception);
            e.SetObserved();
        };

        // 隐藏的自检模式（给测试用，不显示界面）：CaretReader.exe --selftest
        if (args.Any(a => a.Equals("--selftest", StringComparison.OrdinalIgnoreCase)))
            return SelfTest.Run(appDir, logDir);
        // v0.6 隐藏的资源实测模式：CaretReader.exe --perftest（结果写到「资源实测结果.txt」）
        if (args.Any(a => a.Equals("--perftest", StringComparison.OrdinalIgnoreCase)))
            return PerfTest.Run(logDir);

        // 只允许运行一个实例（否则两个程序会抢同一个快捷键）
        using var mutex = new Mutex(initiallyOwned: true, "CaretReader_SingleInstance_v0", out bool isFirst);
        if (!isFirst)
        {
            Log.Write("已经有一个光标朗读在运行，这次启动不打开新窗口");
            MessageBox.Show(
                "光标朗读已经在运行了（请看屏幕右下角的小人悬浮球，或任务栏右下角的托盘图标）。\n\n" +
                "如果看不到它的窗口，请打开“任务管理器”，结束 CaretReader.exe 后再启动。",
                "光标朗读", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }

        CrashGuard.Init(logDir);
        Log.Write($"===== 启动 {Version}  设置文件: {settingsPath}");
        CrashGuard.LogEnvironment(appDir);
        if (CrashGuard.PreviousCrashStage != null)
        {
            Log.Write($"!!! 上次运行没有正常退出，最后记录的步骤是：{CrashGuard.PreviousCrashStage}");
            CrashGuard.LogRecentWindowsErrorsAsync();
        }
        TtsEngine.Stage = CrashGuard.Mark;
        // v0.5：多音字前端在后台加载（约 1 秒，和模型加载同时进行，不阻塞界面）
        PolyphoneFrontend.StartLoading(appDir, logDir);

        try
        {
            ApplicationConfiguration.Initialize();   // 高 DPI、视觉样式等（由项目文件里的设置生成）
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (_, e) => Report(e.Exception, "界面线程", fatal: false);

            CrashGuard.Mark("创建主窗口");
            Application.Run(new MainForm());
        }
        catch (Exception ex)
        {
            Report(ex, "启动", fatal: true);
            return 1;
        }
        finally
        {
            CrashGuard.CleanExit();
        }
        return 0;
    }

    private static int _reporting;

    private static void Report(Exception? ex, string where, bool fatal)
    {
        Log.Write($"未处理的错误（{where}{(fatal ? "，程序将退出" : "")}）: {ex}");
        // 同时弹出多个错误框没有意义；只弹第一个
        if (Interlocked.Exchange(ref _reporting, 1) == 1) return;
        try
        {
            MessageBox.Show("程序遇到一个错误：\n" + ex?.Message +
                            "\n\n详细信息已写入程序文件夹里的 log.txt。" + (fatal ? "\n程序需要关闭，请重新打开。" : ""),
                "光标朗读", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch { }
        finally { if (!fatal) Interlocked.Exchange(ref _reporting, 0); }
    }
}

/// <summary>
/// 隐藏的自检：不显示界面，依次加载 voices 文件夹里的每个音色包，各合成一句话，
/// 结果写进 log.txt 和 selftest.txt（以及 selftest_模型id.wav），全部成功时退出码为 0。
/// </summary>
internal static class SelfTest
{
    public static int Run(string appDir, string logDir)
    {
        var report = new List<string>();
        void Say(string s) { Log.Write("[自检] " + s); report.Add(s); }
        TtsEngine.Stage = s => Log.Write("[步骤] " + s);
        int fail = 0;
        Say($"光标朗读 {Program.Version} 自检开始，程序文件夹 {appDir}，系统代码页 {NativePaths.AnsiCodePage}");
        PolyphoneFrontend.StartLoading(appDir, logDir);
        try
        {
            var fe = PolyphoneFrontend.Shared(30000);
            if (fe == null) { fail++; Say("失败 多音字前端没有加载（frontend 文件夹缺失或损坏）"); }
            else
            {
                string got = string.Join(" ", fe.G2P("银行行长在总行开会").Select(r => r.Pinyin));
                bool ok = got == "yin2 hang2 hang2 zhang3 zai4 zong3 hang2 kai1 hui4";
                if (!ok) fail++;
                Say($"{(ok ? "通过" : "失败")} 多音字前端：加载 {fe.LoadSeconds:0.00}s，用户词 {fe.UserWordCount} 个，“银行行长在总行开会” → {got}");
                foreach (var w in fe.UserDictWarnings) Say("用户自定义词库：" + w);
            }
            var scan = VoiceLibrary.Scan(appDir, tidy: false);
            foreach (var pr in scan.Problems) Say("跳过 " + pr);
            if (scan.Packs.Count == 0) throw new InvalidDataException("voices 文件夹里没有可用的音色");
            using var engine = new TtsEngine();
            foreach (var voice in scan.Packs)
            {
                try
                {
                    var sw = Stopwatch.StartNew();
                    bool reused = engine.IsLoaded(voice, 2);
                    engine.Load(voice, 2, appDir);
                    double load = sw.Elapsed.TotalSeconds;
                    sw.Restart();
                    var samples = engine.Synthesize("今天天气很好，我们一起去公园散步吧。", voice.Sid, (float)voice.Speed);
                    double synth = sw.Elapsed.TotalSeconds;
                    if (samples.Length == 0) throw new InvalidDataException("合成结果为空");
                    SpeechPipeline.ApplyGain(samples, (float)voice.Gain);
                    string wav = Path.Combine(logDir, $"selftest_{voice.Id}.wav");
                    WriteWav(wav, samples, engine.SampleRate);
                    Say($"通过 {voice.Id}: {(reused ? "同模型免加载" : $"加载 {load:0.0}s")}，合成 {synth:0.00}s，{samples.Length} 个采样 @ {engine.SampleRate} Hz，试听文件{(voice.SamplePath != null ? "有" : "无")}");
                }
                catch (Exception ex) { fail++; Say($"失败 {voice.Id}: {ex}"); }
            }
        }
        catch (Exception ex) { fail++; Say("失败: " + ex); }
        Say(fail == 0 ? "自检全部通过" : $"自检失败 {fail} 项");
        try { File.WriteAllLines(Path.Combine(logDir, "selftest.txt"), report); } catch { }
        return fail == 0 ? 0 : 1;
    }

    private static void WriteWav(string path, float[] samples, int rate)
    {
        using var bw = new BinaryWriter(File.Create(path));
        int bytes = samples.Length * 2;
        bw.Write("RIFF"u8); bw.Write(36 + bytes); bw.Write("WAVE"u8);
        bw.Write("fmt "u8); bw.Write(16); bw.Write((short)1); bw.Write((short)1);
        bw.Write(rate); bw.Write(rate * 2); bw.Write((short)2); bw.Write((short)16);
        bw.Write("data"u8); bw.Write(bytes);
        foreach (var f in samples) bw.Write((short)Math.Clamp(f * 32767f, -32768f, 32767f));
    }
}

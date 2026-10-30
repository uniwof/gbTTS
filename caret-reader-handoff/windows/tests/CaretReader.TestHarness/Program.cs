// 跨平台测试程序（Linux / Windows 都能跑，不需要界面）。
//
// 用法：
//   dotnet run -- split
//       只测试“文本切分 + 停顿”逻辑（不需要模型）。
//   dotnet run -- synth <音色包文件夹> <输出.wav> [线程数]
//       加载模型，按真实流程（切分 → 逐句合成 → 插静音 → 模拟实时播放）朗读一段测试文字，
//       输出 wav，并报告：加载时间、首句出声延迟、RTF、是否卡顿、内存峰值。
//   dotnet run -- switch <程序文件夹>
//       依次切换 voices 里的每个音色，检查“只保留一个模型”的内存表现和“同模型免加载”。
//   dotnet run -- sample <音色包文件夹> <输出.wav>      v0.4：用统一试听文本生成试听音频（真实切分 + 停顿 + 增益）
//   dotnet run -- scan <程序文件夹>                    v0.4：扫描 voices，列出有效 / 跳过的音色包
//   dotnet run -- g2p <frontend数据> <输入.txt> [用户词库]    v0.5：每行输出多音字前端的定音结果（与 Python 实验版逐字对比用）
//   dotnet run -- fbench <frontend数据> <输入.txt> [用户词库] v0.5：前端单独的加载时间、内存峰值、每千字耗时
//   dotnet run -- frontendtest <frontend数据> <音色包>        v0.5：用户词库格式 / PUA 转换 / 生僻字 自动测试
//   dotnet run -- packtest                             v0.4：音色包校验 / 扫描 / 导入（中文路径、坏包、重复）自动测试

using System.Diagnostics;
using CaretReader.Core;

Log.AlsoConsole = Environment.GetEnvironmentVariable("CR_LOGCONSOLE") == "1";
// v0.5：CR_APPDIR=<程序文件夹> 时像正式程序一样后台加载多音字前端；CR_NOFRONTEND=1 时关掉（= v0.4 的朗读方式，用来对比）
if (Environment.GetEnvironmentVariable("CR_NOFRONTEND") == "1") TtsEngine.UseFrontend = false;
else if (Environment.GetEnvironmentVariable("CR_APPDIR") is string crApp)
{
    PolyphoneFrontend.StartLoading(crApp);
    // CR_WAITFRONTEND=1：先等前端加载完再开始（模拟正式程序里前端在启动阶段就已加载好），只测稳定后的合成速度
    if (Environment.GetEnvironmentVariable("CR_WAITFRONTEND") == "1") PolyphoneFrontend.Shared(60000);
}
string mode = args.Length > 0 ? args[0] : "split";
return mode switch
{
    "split" => Tests.SplitTests(),
    "synth" => Tests.Synth(args[1], args[2], args.Length > 3 ? int.Parse(args[3]) : 2),
    "switch" => Tests.Switch(args[1]),
    "sample" => V04Tests.Sample(args[1], args[2]),
    "scan" => V04Tests.ScanCmd(args[1]),
    "packtest" => V04Tests.PackTest(),
    "dump" => Tests.Dump(args[1]),
    "crash_v02" => V03Tests.CrashV02(),
    "nullguard" => V03Tests.NullGuard(),
    "paths" => V03Tests.Paths(),
    "pause" => Tests.PauseTest(args[1], args.Length > 2 ? int.Parse(args[2]) : 2),
    "g2p" => V05Tests.G2PDump(args[1], args[2], args.Length > 3 ? args[3] : null),
    "fbench" => V05Tests.Bench(args[1], args[2], args.Length > 3 ? args[3] : null),
    "abcpu" => V05Tests.AbCpu(args[1], args[2], args.Length > 3 ? int.Parse(args[3]) : 3),
    "frontendtest" => V05Tests.FrontendTest(args[1], args[2]),
    "v06" => V06Tests.Run(),
    _ => Usage()
};

static int Usage() { Console.WriteLine("用法见 Program.cs 顶部注释"); return 2; }

static class Tests
{
    public const string SampleText =
        "雨停了，街上的灯一盏一盏亮起来。他站在门口，没有进去，也没有离开；" +
        "屋里传来熟悉的笑声：像很多年前一样。\n" +
        "他深吸一口气，抬手敲了敲门！门开了吗？\r\n\r\n" +
        "2024年3月15日，价格是3.14元，共1,000件。Hello, world. This is a test!";

    // ------------------------------------------------------------------
    public static int SplitTests()
    {
        var pz = new PauseSettings();
        int fail = 0;
        void Check(string name, bool ok) { Console.WriteLine((ok ? "  [通过] " : "  [失败] ") + name); if (!ok) fail++; }

        var cl = TextSplitter.Split(SampleText, pz);
        Console.WriteLine("切分结果：");
        foreach (var c in cl) Console.WriteLine($"  {c.PauseAfterSeconds,4:0.00}s | {c.Text}");

        Check("逗号停 0.15", cl[0].Text == "雨停了，" && cl[0].PauseAfterSeconds == 0.15);
        Check("句号停 0.5", cl[1].Text == "街上的灯一盏一盏亮起来。" && cl[1].PauseAfterSeconds == 0.5);
        Check("分号停 0.3", cl.Any(c => c.Text.EndsWith("；") && c.PauseAfterSeconds == 0.3));
        Check("冒号停 0.25", cl.Any(c => c.Text.EndsWith("：") && c.PauseAfterSeconds == 0.25));
        Check("换行=句号停顿", cl.Any(c => c.Text == "像很多年前一样。" && c.PauseAfterSeconds == 0.5));
        Check("！停 0.5", cl.Any(c => c.Text == "抬手敲了敲门！" && c.PauseAfterSeconds == 0.5));
        Check("3.14 不被切开", cl.Any(c => c.Text.Contains("3.14")));
        Check("1,000 不被切开", cl.Any(c => c.Text.Contains("1,000")));
        Check("英文逗号", cl.Any(c => c.Text == "Hello," && c.PauseAfterSeconds == 0.15));
        Check("英文句号", cl.Any(c => c.Text == "world." && c.PauseAfterSeconds == 0.5));
        Check("最后的英文感叹号", cl[^1].Text == "This is a test!" && cl[^1].PauseAfterSeconds == 0.5);

        var a = TextSplitter.Split("他说：“好的！”然后走了……", pz);
        foreach (var c in a) Console.WriteLine($"  {c.PauseAfterSeconds,4:0.00}s | {c.Text}");
        Check("右引号跟随前句", a.Count == 3 && a[1].Text == "“好的！”");
        Check("省略号=句号", a[2].Text == "然后走了……" && a[2].PauseAfterSeconds == 0.5);

        var b = TextSplitter.Split("。。，，\n\n   ", pz);
        Check("纯标点不产生小句", b.Count == 0);

        var d = TextSplitter.Split("你好！？\n\n世界", pz);
        Check("连续标点+空行只停一次，取最大", d.Count == 2 && d[0].PauseAfterSeconds == 0.5);

        // ---------- 长度控制 ----------
        string longText = new string('字', 150);
        var e = TextSplitter.Split(longText, pz);
        Check("超长无标点：第一句 ≤20 字，其余 ≤36 字，内容不丢",
            e[0].Text.Length <= TextSplitter.FirstClauseMaxChars && e.All(c => c.Text.Length <= TextSplitter.MaxClauseChars)
            && string.Concat(e.Select(c => c.Text)) == longText);

        var first = TextSplitter.Split("今天下午我们一起去学校旁边的那家新开的书店看看有没有好书，然后回家。", pz);
        Console.WriteLine("  首句切分: " + string.Join(" | ", first.Select(c => $"{c.Text}({c.PauseAfterSeconds})")));
        Check("首句在 8~20 字的自然断点切开", first[0].Text.Length is >= 8 and <= 20);
        Check("首句切点优先在虚词后（的/了…）", "的了着过和与及或在是就也都而并但把被从向对给让".Contains(first[0].Text[^1]));
        Check("切开后原逗号停顿仍在该句末尾", first.Any(c => c.Text.EndsWith("，") && c.PauseAfterSeconds == 0.15));

        string emoji = new string('中', 19) + "😀" + new string('文', 40);
        var em = TextSplitter.Split(emoji, pz);
        Check("不切开 emoji（代理对）", em.All(c => !char.IsHighSurrogate(c.Text[^1]) && !char.IsLowSurrogate(c.Text[0])));

        string num = new string('中', 18) + "1234567" + new string('文', 40);
        var nm = TextSplitter.Split(num, pz);
        Check("不切开数字", nm.Any(c => c.Text.Contains("1234567")));

        string eng = new string('中', 17) + "internationalization" + new string('文', 40);
        var en = TextSplitter.Split(eng, pz);
        Check("不切开英文单词", en.Any(c => c.Text.Contains("internationalization")));

        string engSent = "This is a fairly long English sentence without any punctuation that keeps going on and on for a while";
        var es = TextSplitter.Split(engSent, pz);
        Console.WriteLine("  英文长句: " + string.Join(" | ", es.Select(c => c.Text)));
        Check("英文长句在空格处切，单词完整", string.Join(" ", es.Select(c => c.Text)) == engSent);

        // ---------- 数字 / 网址 / 缩写 / 引号 ----------
        Check("全角冒号 10：30 不切", TextSplitter.Split("10：30开会", pz).Count == 1);
        Check("全角逗号 １，０００ 不切", TextSplitter.Split("１，０００元", pz).Count == 1);
        Check("全角句点 ３．１４ 不切", TextSplitter.Split("３．１４", pz).Count == 1);
        var url = TextSplitter.Split("访问https://example.com/a.b?x=1,2吧", pz);
        Console.WriteLine("  网址: " + string.Join(" | ", url.Select(c => c.Text)));
        Check("网址里的 ? , : 不切，网址完整", url.Count == 1 && url[0].Text.Contains("https://example.com/a.b?x=1,2"));
        var url2 = TextSplitter.Split("看这里 https://a.com/x。然后呢", pz);
        Check("网址后的句号仍然生效", url2.Count == 2 && url2[0].PauseAfterSeconds == 0.5);
        Check("Mr. 后不停顿", TextSplitter.Split("我用Mr. Smith的电脑", pz).Count == 1);
        Check("e.g. 后不停顿", TextSplitter.Split("e.g. 例如", pz).Count == 1);
        var q = TextSplitter.Split("他说:\"好\"", pz);
        Console.WriteLine("  引号: " + string.Join(" | ", q.Select(c => c.Text)));
        Check("ASCII 开引号跟随后一句", q.Count == 2 && q[1].Text == "\"好\"");
        var dash = TextSplitter.Split("他——走了", pz);
        Check("破折号——短停顿", dash.Count == 2 && dash[0].PauseAfterSeconds == 0.15);
        Check("间隔号·不停顿", TextSplitter.Split("马克·吐温", pz).Count == 1);

        // ---------- v0.2：超长 / 读不出来的字符串（第二轮审查 P1） ----------
        bool AllWithin(List<Clause> l, int max) => l.All(c => c.Text.Length <= max);
        bool NoBrokenSurrogate(List<Clause> l) => l.All(c => c.Text.Length == 0 ||
            (!char.IsHighSurrogate(c.Text[^1]) && !char.IsLowSurrogate(c.Text[0])));
        var rnd = new Random(42);
        string hex = string.Concat(Enumerable.Range(0, 4000).Select(_ => "0123456789abcdef"[rnd.Next(16)]));
        var hx = TextSplitter.Split("校验值" + hex + "，请核对。", pz);
        Console.WriteLine("  4000 位十六进制: " + string.Join(" | ", hx.Select(c => c.Text)));
        Check("4000 位十六进制 → “一串字符”，没有超长小句", hx.Any(c => c.Text.Contains("一串字符")) && AllWithin(hx, 36) && hx.Count <= 3);
        string longUrl = "https://example.com/path/" + string.Concat(Enumerable.Range(0, 1800).Select(i => (char)('a' + i % 26)));
        var lu = TextSplitter.Split("请访问" + longUrl + "。谢谢", pz);
        Console.WriteLine("  1828 字网址: " + string.Join(" | ", lu.Select(c => $"{c.Text}({c.PauseAfterSeconds})")));
        Check("长网址 → “链接”，后面的句号停顿保留", lu.Count == 2 && lu[0].Text == "请访问链接。" && lu[0].PauseAfterSeconds == 0.5);
        Check("短网址照常保留", TextSplitter.Split("访问https://a.com/x吧", pz)[0].Text == "访问https://a.com/x吧");
        var hy = TextSplitter.Split("Supercalifragilistic-expialidocious-and-other-very-long-hyphenated-words-hyphens-forever then more", pz);
        Console.WriteLine("  连字符长串: " + string.Join(" | ", hy.Select(c => c.Text)));
        Check("连字符长串：第一句 ≤20 字（找不到安全切点时硬切）", hy[0].Text.Length <= 20 && AllWithin(hy, 36));
        string hyLong = string.Join("-", Enumerable.Range(0, 120).Select(i => "word" + (char)('a' + i % 26)));
        var hl = TextSplitter.Split("开头一句话，" + hyLong, pz);
        Check("超长连字符串：每段 ≤36 字，内容不丢", AllWithin(hl, TextSplitter.MaxClauseChars)
            && string.Concat(hl.Select(c => c.Text)).Replace("，", "") == "开头一句话" + hyLong);
        string letters = string.Concat(Enumerable.Range(0, 3000).Select(_ => (char)('a' + rnd.Next(26))));
        var lt = TextSplitter.Split("密钥" + letters + "结束", pz);
        Check("3000 个连续字母（不是单词）→ “一串字符”", lt.Count == 1 && lt[0].Text == "密钥一串字符结束");
        Check("ID / 序列号 → “一串字符”", TextSplitter.Split("订单号 A1B2C3D4E5F6G7H8 已发货", pz)[0].Text == "订单号 一串字符 已发货");
        Check("16 位以上纯数字 → “一串数字”", TextSplitter.Split("卡号6222020200112233445。", pz)[0].Text == "卡号一串数字。");
        Check("普通长单词不替换", TextSplitter.Split("internationalization", pz)[0].Text == "internationalization");
        Check("Base64 → “一串字符”", TextSplitter.Split("数据 aGVsbG8gd29ybGQgdGhpcyBpcyBiYXNlNjQ0= 结束", pz)[0].Text.Contains("一串字符"));
        string syms = "a" + new string('=', 3000) + "b";
        var sy = TextSplitter.Split(syms, pz);
        Check("3000 个符号：没有超长小句", AllWithin(sy, TextSplitter.HardMaxClauseChars));
        string emo = string.Concat(Enumerable.Repeat("😀中", 300));
        var eo = TextSplitter.Split(emo, pz);
        Check("emoji 长串：不切开代理对，每段 ≤36", NoBrokenSurrogate(eo) && AllWithin(eo, 36));
        var big = new System.Text.StringBuilder();
        string pool = "中文字ab12 😀,.-_/=:；";
        for (int k = 0; k < 200_000; k++)
        {
            string piece = pool[rnd.Next(pool.Length)].ToString();
            if (char.IsSurrogate(piece[0])) piece = "😀";
            big.Append(piece);
        }
        var swBig = Stopwatch.StartNew();
        var bg = TextSplitter.Split(big.ToString(), pz, firstChunk: true, maxChars: 100_000);
        swBig.Stop();
        Console.WriteLine($"  20 万字随机混合文本（截断到 10 万）: {bg.Count} 句，最长 {bg.Max(c => c.Text.Length)} 字，用时 {swBig.ElapsedMilliseconds}ms");
        Check("随机混合文本：所有小句 ≤80 字，不切开 emoji，截断生效", AllWithin(bg, TextSplitter.HardMaxClauseChars) && NoBrokenSurrogate(bg)
            && bg.Sum(c => c.Text.Length) <= 100_000);
        var cont = TextSplitter.Split("今天下午我们一起去学校旁边的那家新开的书店看看有没有好书，然后回家。", pz, firstChunk: false);
        Check("后续文字（firstChunk=false）第一句不强制缩到 20 字", cont[0].Text.Length > 20 && cont[0].Text.Length <= 36);

        // ---------- 送进引擎的文字 ----------
        Check("引擎文字：10:30 保持", TextSplitter.TextForEngine("10:30开会") == "10:30开会");
        Check("引擎文字：10：30 保持", TextSplitter.TextForEngine("10：30开会") == "10：30开会");
        Check("引擎文字：网址保持", TextSplitter.TextForEngine("访问https://a.com/x吧") == "访问https://a.com/x吧");
        Check("引擎文字：句末 ：；替换为，", TextSplitter.TextForEngine("他说：") == "他说，" && TextSplitter.TextForEngine("离开；") == "离开，");
        Check("引擎文字：英文 ;: 替换为 ,", TextSplitter.TextForEngine("note: a; b") == "note, a, b");

        // ---------- 设置文件容错 ----------
        string tmp = Path.Combine(Path.GetTempPath(), "cr_settings_test.json");
        File.WriteAllText(tmp, "{ \"Hotkey\": \"Ctrl+Alt+F8\", \"Speed\": \"快\", \"NumThreads\": 4, \"Pauses\": { \"Comma\": \"x\", \"Period\": 0.8 }, \"TopMost\": false }");
        var st = AppSettings.Load(tmp);
        Check("设置：坏的一项只重置这一项", st.Hotkey == "Ctrl+Alt+F8" && st.Speed == 1.0 && st.NumThreads == 4
            && st.Pauses.Comma == 0.15 && st.Pauses.Period == 0.8 && st.TopMost == false);
        Check("设置：坏文件另存为 .bad", File.Exists(tmp + ".bad"));
        File.WriteAllText(tmp, "这不是 JSON");
        Check("设置：完全损坏时用默认值", AppSettings.Load(tmp).Hotkey == "Ctrl+Alt+R");
        st.Save(tmp);
        Check("设置：保存后能读回", AppSettings.Load(tmp).Hotkey == "Ctrl+Alt+F8");
        File.WriteAllText(tmp, "{ \"Hotkey\": \"Ctrl+Alt+F8\", \"StopHotkey\": 123, \"Speed\": 1.2 }");
        var st3 = AppSettings.Load(tmp);
        Check("设置：停止快捷键写错只重置这一项", st3.StopHotkey == "Ctrl+Alt+S" && st3.Hotkey == "Ctrl+Alt+F8" && st3.Speed == 1.2);
        File.WriteAllText(tmp, "{ \"StopHotkey\": \"Ctrl+Shift+F9\" }");
        Check("设置：停止快捷键能读回", AppSettings.Load(tmp).StopHotkey == "Ctrl+Shift+F9");
        Check("设置：旧版 settings.json（没有停止快捷键）用默认值", new AppSettings().StopHotkey == "Ctrl+Alt+S");

        Console.WriteLine(fail == 0 ? "全部通过" : $"{fail} 项失败");
        return fail == 0 ? 0 : 1;
    }

    // ------------------------------------------------------------------
    /// <summary>逐行读取测试句子（\\r \\n 写成转义），打印切分结果和送进引擎的文字。</summary>
    public static int Dump(string file)
    {
        foreach (var line in File.ReadLines(file))
        {
            if (line.Length == 0) continue;
            string t = line.Replace("\\r", "\r").Replace("\\n", "\n");
            Console.WriteLine("IN : " + line);
            foreach (var c in TextSplitter.Split(t, new PauseSettings()))
                Console.WriteLine($"   [{c.Text}] +{c.PauseAfterSeconds}  -> engine: [{TextSplitter.TextForEngine(c.Text)}]  len={c.Text.Length}");
        }
        return 0;
    }

    // ------------------------------------------------------------------
    public static int Synth(string packDir, string outWav, int threads)
    {
        var voice = VoicePack.TryLoad(packDir, out var err) ?? throw new Exception("音色包无效: " + err);
        using var engine = new TtsEngine();

        var sw = Stopwatch.StartNew();
        engine.Load(voice, threads, Path.GetDirectoryName(Path.GetDirectoryName(voice.Dir))!);
        double load = sw.Elapsed.TotalSeconds;
        Console.WriteLine($"[{voice.Name}] 音色包 {voice.Id} 加载 {load:0.00}s，采样率 {engine.SampleRate}，说话人数 {engine.NumSpeakers}，RSS {Mem.RssMb()}MB");

        var clauses = TextSplitter.Split(SampleText, new PauseSettings());
        var sink = new SimulatedRealtimeSink();
        var stats = SpeechPipeline.RunAsync(new ClauseQueue(clauses), engine, voice.Sid, (float)voice.Speed, sink, CancellationToken.None, null, 0, (float)voice.Gain).Result;
        if (stats.Error != null) { Console.WriteLine("错误: " + stats.Error); return 1; }

        sink.SaveWav(outWav);
        Console.WriteLine($"  小句 {stats.Clauses}，语音 {stats.SpeechSeconds:0.00}s + 静音 {stats.SilenceSeconds:0.00}s，" +
                          $"合成耗时 {stats.SynthSeconds:0.00}s，RTF {stats.Rtf:0.000}");
        Console.WriteLine($"  首句出声延迟 {stats.FirstAudioLatencySeconds:0.00}s，播放中断(卡顿) {sink.Underruns} 次 / 共 {sink.UnderrunSeconds:0.00}s");
        Console.WriteLine($"  内存峰值 VmHWM {Mem.PeakMb()}MB，当前 RSS {Mem.RssMb()}MB → {outWav}");

        // 取消测试：开始朗读 1.5 秒后停止，检查能否立即结束
        var cts = new CancellationTokenSource();
        var sink2 = new SimulatedRealtimeSink();
        var t = SpeechPipeline.RunAsync(clauses, engine, voice.Sid, 1.0f, sink2, cts.Token);
        Thread.Sleep(1500);
        var s0 = Stopwatch.StartNew();
        cts.Cancel();
        double soundStopMs = sink2.StopCalledAfterMs(s0);
        var st2 = t.Result;
        Console.WriteLine($"  停止测试：Cancel 后 {soundStopMs:0}ms 声音停止；{s0.Elapsed.TotalMilliseconds:0}ms 后台合成线程结束（正在合成的那一句会被丢弃）；Cancelled={st2.Cancelled}");
        return 0;
    }

    // ------------------------------------------------------------------
    /// <summary>
    /// v0.2 暂停 / 继续 + 分批追加小句的测试：
    ///   1) 先只放前 2 句开始朗读，1.5 秒后再追加其余句子（模拟“先读 2000 字，剩下的后台再读”）；
    ///   2) 播放 2 秒后暂停 4 秒：检查暂停期间合成线程因“背压”停下（提前量不超过 8 秒 + 一句），
    ///      并且没有声音被丢掉；
    ///   3) 继续，直到读完：检查所有小句都读了。
    /// </summary>
    public static int PauseTest(string packDir, int threads)
    {
        var voice = VoicePack.TryLoad(packDir, out var err) ?? throw new Exception("音色包无效: " + err);
        using var engine = new TtsEngine();
        engine.Load(voice, threads, Path.GetDirectoryName(Path.GetDirectoryName(voice.Dir))!);
        string text = string.Concat(Enumerable.Repeat(SampleText + "\n", 3));
        var all = TextSplitter.Split(text, new PauseSettings());
        var queue = new ClauseQueue();
        queue.Add(all.Take(2));
        var sink = new SimulatedRealtimeSink();
        long pressed = Stopwatch.GetTimestamp();
        Thread.Sleep(200);   // 模拟取文字花了 0.2 秒
        var task = SpeechPipeline.RunAsync(queue, engine, voice.Sid, 1.0f, sink, CancellationToken.None, null, pressed);
        Thread.Sleep(1500);
        queue.Add(all.Skip(2)); queue.Complete();
        Thread.Sleep(500);
        sink.Pause();
        double playedAtPause = sink.PlayedSeconds;
        Thread.Sleep(4000);
        double aheadDuringPause = sink.BufferedSeconds;
        double playedAfterPause = sink.PlayedSeconds;
        sink.Resume();
        var st = task.Result;
        int fail = 0;
        void Check(string name, bool ok) { Console.WriteLine((ok ? "  [通过] " : "  [失败] ") + name); if (!ok) fail++; }
        Console.WriteLine($"  小句 {st.Clauses}/{all.Count}，按键→出声 {st.FirstAudioLatencySeconds:0.00}s（其中取文字 {st.StartOffsetSeconds:0.00}s），RTF {st.Rtf:0.00}");
        Console.WriteLine($"  暂停时已播 {playedAtPause:0.00}s，暂停 4 秒后已播 {playedAfterPause:0.00}s，暂停期间提前合成 {aheadDuringPause:0.00}s，卡顿 {sink.Underruns} 次");
        Check("分批追加的小句全部读完", st.Clauses == all.Count && st.Error == null && !st.Cancelled);
        Check("暂停期间不再播放", Math.Abs(playedAfterPause - playedAtPause) < 0.01);
        Check("暂停期间合成被背压限制（≤ 8 秒 + 一句）", aheadDuringPause <= SpeechPipeline.MaxAheadSeconds + 6);
        Check("首句延迟从按键算起（包含取文字的 0.2 秒）", st.StartOffsetSeconds >= 0.19 && st.FirstAudioLatencySeconds >= st.StartOffsetSeconds);
        Check("没有声音被丢掉（写入的语音 = 合成的语音）", Math.Abs(sink.WrittenSeconds - (st.SpeechSeconds + st.SilenceSeconds)) < 0.05);
        return fail == 0 ? 0 : 1;
    }

    // ------------------------------------------------------------------
    public static int Switch(string appDir)
    {
        var scan = VoiceLibrary.Scan(appDir, tidy: false);
        foreach (var pr in scan.Problems) Console.WriteLine("  跳过: " + pr);
        using var engine = new TtsEngine();
        foreach (var v in scan.Packs)
        {
            var sw = Stopwatch.StartNew();
            bool reload = !engine.IsLoaded(v, 2);
            engine.Load(v, 2, appDir);
            double load = sw.Elapsed.TotalSeconds;
            sw.Restart();
            var pcm = engine.Synthesize("这是一个切换音色的测试。", v.Sid, 1.0f);
            Console.WriteLine($"  {v.Name,-28} {(reload ? "重新加载" : "同模型免加载")} {load:0.00}s  合成 {sw.Elapsed.TotalSeconds:0.00}s/{(double)pcm.Length / engine.SampleRate:0.00}s音频  RSS {Mem.RssMb()}MB  峰值 {Mem.PeakMb()}MB");
        }
        return 0;
    }
}

/// <summary>读取 Linux /proc/self/status 里的内存数据（Windows 上返回进程工作集）。</summary>
static class Mem
{
    static long Read(string key)
    {
        if (!OperatingSystem.IsLinux()) return Process.GetCurrentProcess().PeakWorkingSet64 / 1048576;
        foreach (var line in File.ReadLines("/proc/self/status"))
            if (line.StartsWith(key + ":"))
                return long.Parse(line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1]) / 1024;
        return -1;
    }
    public static long PeakMb() => Read("VmHWM");
    public static long RssMb() => Read("VmRSS");
}

/// <summary>
/// 假扬声器：按真实时间“播放”（用时钟模拟），记录是否出现“播放追上合成”导致的卡顿，
/// 并把所有音频保存下来写成 wav。
/// </summary>
sealed class SimulatedRealtimeSink : IAudioSink
{
    readonly List<float> _all = new();
    readonly Stopwatch _clock = new();
    int _sr;
    double _queuedSec;         // 已写入音频的总时长（含卡顿造成的空档）
    public int Underruns;
    public double UnderrunSeconds;
    DateTime _stopAt = DateTime.MaxValue;
    public double StopCalledAfterMs(Stopwatch since)
    {
        // Stop() 是在 Cancel() 内同步回调的，所以这里直接看是否已被调用
        return _stopAt == DateTime.MaxValue ? -1 : since.Elapsed.TotalMilliseconds;
    }

    public void Begin(int sampleRate) { _sr = sampleRate; }

    // ----- v0.2：模拟暂停（时钟停下 = 声卡不再消耗缓冲） -----
    bool _paused;
    public void Pause() { lock (_all) { _paused = true; _clock.Stop(); } }
    public void Resume() { lock (_all) { _paused = false; if (_all.Count > 0) _clock.Start(); } }
    public double PlayedSeconds { get { lock (_all) return Math.Min(_queuedSec, _clock.Elapsed.TotalSeconds); } }
    public double WrittenSeconds { get { lock (_all) return _sr > 0 ? (double)_all.Count / _sr : 0; } }

    public void Write(float[] samples)
    {
        lock (_all)
        {
            if (!_clock.IsRunning && _all.Count == 0 && !_paused) _clock.Start();  // 第一段音频到达 = 开始播放
            else if (_paused || !_clock.IsRunning) { }       // 暂停中：只进缓冲
            else
            {
                double now = _clock.Elapsed.TotalSeconds;
                if (now > _queuedSec + 0.005)                  // 播放已经播完了，新音频还没来 = 卡顿
                {
                    Underruns++;
                    UnderrunSeconds += now - _queuedSec;
                    _queuedSec = now;
                }
            }
            _all.AddRange(samples);
            _queuedSec += (double)samples.Length / _sr;
        }
    }

    public double BufferedSeconds
    {
        get { lock (_all) return _all.Count > 0 ? Math.Max(0, _queuedSec - _clock.Elapsed.TotalSeconds) : 0; }
    }
    public bool IsDrained { get { lock (_all) if (_paused) return false; return BufferedSeconds <= 0; } }
    public void Stop() { _clock.Stop(); if (_stopAt == DateTime.MaxValue) _stopAt = DateTime.Now; }

    public void SaveWav(string path)
    {
        using var fs = File.Create(path);
        using var bw = new BinaryWriter(fs);
        int n = _all.Count;
        bw.Write("RIFF"u8); bw.Write(36 + n * 2); bw.Write("WAVE"u8);
        bw.Write("fmt "u8); bw.Write(16); bw.Write((short)1); bw.Write((short)1);
        bw.Write(_sr); bw.Write(_sr * 2); bw.Write((short)2); bw.Write((short)16);
        bw.Write("data"u8); bw.Write(n * 2);
        foreach (var f in _all) bw.Write((short)Math.Clamp(f * 32767f, -32768f, 32767f));
    }
}


// ---------------------------------------------------------------------------
// v0.3 新增测试：复现 v0.2 的闪退机制，并验证 v0.3 的防护。
// ---------------------------------------------------------------------------
static class V03Tests
{
    static SherpaOnnx.OfflineTtsConfig BadConfig()
    {
        var c = new SherpaOnnx.OfflineTtsConfig();
        c.Model.NumThreads = 1; c.Model.Provider = "cpu";
        c.Model.Vits.Model = "/nonexistent/光标朗读/model.onnx";   // 原生库“看不到”的文件
        c.Model.Vits.Tokens = "/nonexistent/tokens.txt";
        return c;
    }

    /// <summary>v0.2 的做法：创建引擎后直接读 SampleRate。预期：进程被系统直接结束（SIGSEGV）。</summary>
    public static int CrashV02()
    {
        Console.WriteLine("v0.2 做法：new OfflineTts(坏配置) → tts.SampleRate …");
        Console.Out.Flush();
        var tts = new SherpaOnnx.OfflineTts(BadConfig());
        Console.WriteLine("SampleRate = " + tts.SampleRate + "（如果看到这一行，说明没有崩溃）");
        return 0;
    }

    /// <summary>v0.3 的做法：先检查原生句柄是否为空。</summary>
    public static int NullGuard()
    {
        var tts = new SherpaOnnx.OfflineTts(BadConfig());
        var f = typeof(SherpaOnnx.OfflineTts).GetField("_handle",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        if (f == null) { Console.WriteLine("[失败] 找不到 _handle 字段（sherpa-onnx 封装改了？）"); return 1; }
        var h = ((System.Runtime.InteropServices.HandleRef)f.GetValue(tts)!).Handle;
        GC.SuppressFinalize(tts);
        Console.WriteLine(h == IntPtr.Zero ? "[通过] 坏配置 → 原生句柄为空，v0.3 会抛出中文异常而不是闪退" : "[失败] 句柄不为空：" + h);
        return h == IntPtr.Zero ? 0 : 1;
    }

    public static int Paths()
    {
        int fail = 0;
        void Check(string name, bool ok) { Console.WriteLine((ok ? "  [通过] " : "  [失败] ") + name); if (!ok) fail++; }
        string b = "/u/Oland/Downloads/光标朗读_标准版_win10x64_v0.2/光标朗读";
        string m = b + "/models/vits-melo-tts-zh_en/model.onnx";
        string r(string full, int acp, Func<string, string?> sp) => NativePaths.ForNative(full, b, true, acp, sp);
        Check("中文程序文件夹 → 英文相对路径", r(m, 936, _ => null) == Path.Combine("models", "vits-melo-tts-zh_en", "model.onnx"));
        Check("英文系统 1252 同样 → 相对路径", r(m, 1252, _ => null) == Path.Combine("models", "vits-melo-tts-zh_en", "model.onnx"));
        string cm = b + "/models/我的音色/model.onnx";
        Check("模型文件夹也是中文 → 用 8.3 短路径", r(cm, 936, _ => "/u/OLAND~1/MODEL~1.ONN") == "/u/OLAND~1/MODEL~1.ONN");
        Check("模型文件夹中文 + 无短路径 + UTF-8 代码页 → 原样", r(cm, 65001, _ => null) == cm);
        bool threw = false; try { r(cm, 936, _ => null); } catch (IOException) { threw = true; }
        Check("模型文件夹中文 + 无短路径 + GBK → 中文提示（不闪退）", threw);
        Check("模型在程序文件夹外的英文路径 → 原样", r("/data/models/x.onnx", 936, _ => null) == "/data/models/x.onnx");
        Check("非 Windows → 原样", NativePaths.ForNative(m, b, false, 936, _ => null) == m);
        Check("空路径 → 空", r("", 936, _ => null) == "");
        Console.WriteLine(fail == 0 ? "全部通过" : $"失败 {fail} 项");
        return fail == 0 ? 0 : 1;
    }
}

/// <summary>v0.4：音色包相关测试。</summary>
static class V04Tests
{
    /// <summary>统一的试听文本（所有音色的 sample.mp3 都读这一段，方便比较）。</summary>
    public const string SampleText = "你好，欢迎使用光标朗读。把光标放在文章里，按下快捷键，我就从这里开始，一句一句读给你听；读累了，随时可以暂停。";

    /// <summary>不模拟实时、只收集音频的“扬声器”。</summary>
    sealed class CollectSink : IAudioSink
    {
        public readonly List<float> All = new();
        public int Rate;
        public void Begin(int sampleRate) => Rate = sampleRate;
        public void Write(float[] samples) { lock (All) All.AddRange(samples); }
        public double BufferedSeconds => 0;
        public bool IsDrained => true;
        public void Stop() { }
    }

    public static int Sample(string packDir, string outWav)
    {
        var p = VoicePack.TryLoad(packDir, out var err) ?? throw new Exception("音色包无效: " + err);
        using var engine = new TtsEngine();
        var sw = Stopwatch.StartNew();
        engine.Load(p, 2, Path.GetDirectoryName(Path.GetDirectoryName(p.Dir))!);
        double load = sw.Elapsed.TotalSeconds;
        var clauses = TextSplitter.Split(SampleText, new PauseSettings());
        var sink = new CollectSink();
        // 开头留 0.15 秒静音
        var st = SpeechPipeline.RunAsync(new ClauseQueue(clauses), engine, p.Sid, (float)p.Speed, sink, CancellationToken.None, null, 0, (float)p.Gain).Result;
        if (st.Error != null) { Console.WriteLine("错误: " + st.Error); return 1; }
        var all = new float[(int)(0.15 * sink.Rate)].Concat(sink.All).ToArray();
        WriteWav(outWav, all, sink.Rate);
        float peak = all.Length == 0 ? 0 : all.Max(Math.Abs);
        double rms = Math.Sqrt(all.Select(x => (double)x * x).Average());
        Console.WriteLine($"{p.Id}\tload={load:0.00}s\tRTF={st.Rtf:0.000}\tclauses={st.Clauses}\tdur={all.Length / (double)sink.Rate:0.00}s\tpeak={peak:0.000}\trms_db={20 * Math.Log10(rms + 1e-9):0.0}\tsr={sink.Rate}\tgain={p.Gain}");
        return 0;
    }

    static void WriteWav(string path, float[] samples, int rate)
    {
        using var bw = new BinaryWriter(File.Create(path));
        int bytes = samples.Length * 2;
        bw.Write("RIFF"u8); bw.Write(36 + bytes); bw.Write("WAVE"u8);
        bw.Write("fmt "u8); bw.Write(16); bw.Write((short)1); bw.Write((short)1);
        bw.Write(rate); bw.Write(rate * 2); bw.Write((short)2); bw.Write((short)16);
        bw.Write("data"u8); bw.Write(bytes);
        foreach (var f in samples) bw.Write((short)Math.Clamp(f * 32767f, -32768f, 32767f));
    }

    public static int ScanCmd(string appDir)
    {
        var r = VoiceLibrary.Scan(appDir, tidy: false);
        foreach (var p in r.Packs) Console.WriteLine($"  有效  {p.Id,-14} {p.Name}  [{p.Engine} sid={p.Sid} gain={p.Gain} speed={p.Speed} order={p.Order}] 试听={(p.SamplePath != null ? "有" : "无")}");
        foreach (var x in r.Problems) Console.WriteLine("  跳过  " + x);
        return r.Packs.Count > 0 ? 0 : 1;
    }

    // ------------------------------------------------------------------
    static int fail;
    static void Check(string name, bool ok) { Console.WriteLine((ok ? "  [通过] " : "  [失败] ") + name); if (!ok) fail++; }

    static string MakePack(string dir, string json, bool model = true, int modelBytes = 4096, byte seed = 1)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "voice.json"), json);
        if (model)
        {
            var b = new byte[modelBytes]; new Random(seed).NextBytes(b);
            File.WriteAllBytes(Path.Combine(dir, "model.onnx"), b);
        }
        File.WriteAllText(Path.Combine(dir, "tokens.txt"), "a 1\n");
        File.WriteAllText(Path.Combine(dir, "lexicon.txt"), "a a\n");
        File.WriteAllText(Path.Combine(dir, "date.fst"), "x");
        File.WriteAllBytes(Path.Combine(dir, "sample.mp3"), new byte[100]);
        return dir;
    }

    const string Good = "{ \"id\":\"zhll-1\", \"name\":\"男声 1\", \"engine\":\"VITS\", \"sid\":1, \"gain\":9, \"speed\":\"1.1\", \"order\":20, // 注释\n \"files\": { \"model\":\"model.onnx\", \"tokens\":\"tokens.txt\", \"lexicon\":\"lexicon.txt\", \"ruleFsts\":[\"date.fst\",\"number.fst\"] }, }";

    public static int PackTest()
    {
        fail = 0;
        string root = Path.Combine(Path.GetTempPath(), "cr_v04_" + Guid.NewGuid().ToString("N").Substring(0, 6));
        string app = Path.Combine(root, "光标朗读 程序");
        string voices = Path.Combine(app, "voices");
        Directory.CreateDirectory(voices);

        // ---------- 校验 ----------
        var g = MakePack(Path.Combine(root, "t", "good"), Good);
        var p = VoicePack.TryLoad(g, out var e);
        Check("有效包能读（大小写、注释、尾逗号、字符串数字都容忍）", p != null && p.Engine == "vits" && p.Sid == 1 && Math.Abs(p.Speed - 1.1) < 1e-9);
        Check("增益被限制在 5 以内", p?.Gain == 5);
        Check("缺失的规则文件只提醒、不拒绝", p != null && p.Files!.RuleFsts!.Count == 1 && p.Warnings.Count == 1);
        Check("找到试听文件", p?.SamplePath != null);
        Check("没有 icon.png 也可以", p != null && p.IconPath == null);
        Check("没有 voice.json → 报错", VoicePack.TryLoad(Path.Combine(root, "t"), out e) == null && e!.Contains("voice.json"));
        var bad1 = MakePack(Path.Combine(root, "t", "bad1"), Good.Replace("VITS", "kokoro"));
        Check("不支持的引擎 → 中文原因", VoicePack.TryLoad(bad1, out e) == null && e!.Contains("不支持"));
        var bad2 = MakePack(Path.Combine(root, "t", "bad2"), Good, model: false);
        Check("缺模型文件 → 列出缺的文件", VoicePack.TryLoad(bad2, out e) == null && e!.Contains("model.onnx"));
        var bad3 = MakePack(Path.Combine(root, "t", "bad3"), Good.Replace("\"model.onnx\"", "\"../good/model.onnx\""));
        Check("文件指到包外面（../）→ 拒绝", VoicePack.TryLoad(bad3, out e) == null && e!.Contains("必须在音色包"));
        var bad4 = MakePack(Path.Combine(root, "t", "bad4"), Good.Replace("VITS", "matcha"));
        Check("matcha 没有声码器 → 拒绝", VoicePack.TryLoad(bad4, out e) == null && e!.Contains("vocoder"));
        var bad5 = MakePack(Path.Combine(root, "t", "bad5"), "{ 这不是 json");
        Check("voice.json 格式坏 → 拒绝不崩溃", VoicePack.TryLoad(bad5, out e) == null && e!.Contains("格式"));
        var bad6 = MakePack(Path.Combine(root, "t", "bad6"), Good, modelBytes: 10);
        Check("模型文件过小（没下载完整）→ 拒绝", VoicePack.TryLoad(bad6, out e) == null && e!.Contains("太小"));
        var bad7 = MakePack(Path.Combine(root, "t", "bad7"), Good.Replace("\"sid\":1", "\"sid\":-3"));
        Check("负数 sid → 拒绝", VoicePack.TryLoad(bad7, out e) == null);
        var bad8 = MakePack(Path.Combine(root, "t", "bad8"), Good.Replace("\"order\":20", "\"order\":20, \"modelBytes\": 9999"));
        Check("modelBytes 对不上（没解压完整）→ 拒绝", VoicePack.TryLoad(bad8, out e) == null && e!.Contains("不完整"));
        var ok8 = MakePack(Path.Combine(root, "t", "ok8"), Good.Replace("\"order\":20", "\"order\":20, \"modelBytes\": 4096"));
        Check("modelBytes 对得上 → 通过", VoicePack.TryLoad(ok8, out e) != null);

        // ---------- 扫描 ----------
        MakePack(Path.Combine(voices, "melo-0"), Good.Replace("\"order\":20", "\"order\":10").Replace("男声 1", "女声 1"), seed: 7);
        MakePack(Path.Combine(voices, "broken"), "{ 坏的");
        Directory.CreateDirectory(Path.Combine(voices, "empty"));
        MakePack(Path.Combine(voices, "音色_男声1_外层", "zhll-1"), Good);           // 多套了一层
        MakePack(Path.Combine(voices, "我的音色"), Good.Replace("zhll-1", "").Replace("\"sid\":1", "\"sid\":2"), seed: 3);   // 中文文件夹名
        Directory.CreateDirectory(Path.Combine(voices, ".import-dead"));
        var s1 = VoiceLibrary.Scan(app, tidy: false);
        Check("扫描：坏包 / 空文件夹跳过，不崩溃", s1.Packs.Count == 3 && s1.Problems.Any(x => x.Contains("broken")) && s1.Problems.Any(x => x.Contains("empty")));
        Check("扫描：按 order 排序", s1.Packs[0].Id == "melo-0");
        var s2 = VoiceLibrary.Scan(app, tidy: true);
        Check("整理：外层文件夹里的包挪到 voices 下", Directory.Exists(Path.Combine(voices, "zhll-1")) && !Directory.Exists(Path.Combine(voices, "音色_男声1_外层")));
        Check("整理：中文文件夹名改成英文", s2.Packs.All(x => NativePaths.IsAscii(x.Id)) && !Directory.Exists(Path.Combine(voices, "我的音色")));
        Check("整理后仍是 3 个有效音色", s2.Packs.Count == 3);
        VoiceImporter.CleanupTemp(app);
        Check("清理导入临时文件夹", !Directory.Exists(Path.Combine(voices, ".import-dead")));

        // ---------- 导入 ----------
        string src = Path.Combine(root, "D盘", "下载的音色", "音色_女声4_清亮_zhll-0");
        MakePack(Path.Combine(src, "zhll-0"), Good.Replace("zhll-1", "zhll-0").Replace("\"sid\":1", "\"sid\":0").Replace("男声 1", "女声 4"), seed: 1);
        double lastProgress = 0;
        var r = VoiceImporter.Import(src, app, (t, f) => lastProgress = f, CancellationToken.None);
        Check("导入：中文路径 + 多一层文件夹 → 成功", r.Imported.Count() == 1 && Directory.Exists(Path.Combine(voices, "zhll-0")));
        Check("导入：进度到 100%", Math.Abs(lastProgress - 1) < 1e-9);
        Check("导入：没有留下临时文件夹", !Directory.GetDirectories(voices, ".import-*").Any());
        var r2 = VoiceImporter.Import(src, app, null, CancellationToken.None);
        Check("导入：同一个音色再导一次 → 跳过", r2.Items.Count == 1 && r2.Items[0].Skipped);
        var r3 = VoiceImporter.Import(Path.Combine(voices, "zhll-0"), app, null, CancellationToken.None);
        Check("导入：选了程序自己的音色文件夹 → 提示不用导入", r3.Items.Count == 1 && r3.Items[0].Error != null);
        var r4 = VoiceImporter.Import(Path.Combine(root, "t", "bad1"), app, null, CancellationToken.None);
        Check("导入：坏包 → 中文原因，不复制", r4.Items.Count == 1 && r4.Items[0].Error!.Contains("不支持") && VoiceLibrary.Scan(app, false).Packs.Count == 4);
        var r5 = VoiceImporter.Import(Path.Combine(root, "D盘", "下载的音色"), app, null, CancellationToken.None);
        Check("导入：选了更外层的文件夹（往下两层找）", r5.Items.Count == 1 && r5.Items[0].Skipped);
        var emptyDir = Directory.CreateDirectory(Path.Combine(root, "空的")).FullName;
        var r6 = VoiceImporter.Import(emptyDir, app, null, CancellationToken.None);
        Check("导入：没有音色包 → 提示先解压", r6.Items[0].Error!.Contains("解压"));
        // 同名但不同音色 → 自动加后缀
        string src2 = MakePack(Path.Combine(root, "别的", "zhll-0"), Good.Replace("zhll-1", "zhll-0").Replace("\"sid\":1", "\"sid\":3"), seed: 1);
        var r7 = VoiceImporter.Import(src2, app, null, CancellationToken.None);
        Check("导入：文件夹名冲突 → zhll-0-2", r7.Imported.Count() == 1 && Directory.Exists(Path.Combine(voices, "zhll-0-2")));
        Check("同一模型的两个包 ModelKey 相同（切换免加载）",
              VoicePack.TryLoad(Path.Combine(voices, "zhll-0"), out _)!.ModelKey == VoicePack.TryLoad(Path.Combine(voices, "zhll-0-2"), out _)!.ModelKey);
        Check("不同模型 ModelKey 不同", VoicePack.TryLoad(Path.Combine(voices, "melo-0"), out _)!.ModelKey != VoicePack.TryLoad(Path.Combine(voices, "zhll-0"), out _)!.ModelKey);

        // ---------- 文件夹名 ----------
        Check("英文名：用 voice.json 的 id", VoiceLibrary.SafeFolderName("zhll-4", "音色", "x") == "zhll-4");
        Check("英文名：纯中文 → voice-哈希", VoiceLibrary.SafeFolderName(null, "我的音色", "seed").StartsWith("voice-"));
        Check("英文名：Windows 保留名 CON / com1 不用", VoiceLibrary.SafeFolderName("CON", "com1", "s").StartsWith("voice-"));

        try { Directory.Delete(root, true); } catch { }
        Console.WriteLine(fail == 0 ? "全部通过" : $"失败 {fail} 项");
        return fail == 0 ? 0 : 1;
    }
}



static class V05Tests
{
    static long PeakMb()
    {
        try { foreach (var l in File.ReadLines("/proc/self/status")) if (l.StartsWith("VmHWM:")) return long.Parse(l.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1]) / 1024; }
        catch { }
        return Process.GetCurrentProcess().PeakWorkingSet64 >> 20;
    }

    public static int G2PDump(string data, string input, string? user)
    {
        var f = PolyphoneFrontend.Load(data, user);
        var outp = new System.Text.StringBuilder();
        foreach (var line in File.ReadLines(input))
            outp.AppendLine(string.Join(" ", f.G2P(line).Select(r => r.Char + ":" + r.Pinyin)));
        Console.Out.Write(outp.ToString());
        return 0;
    }

    public static int Bench(string data, string input, string? user)
    {
        long before = PeakMb();
        var sw = Stopwatch.StartNew();
        var f = PolyphoneFrontend.Load(data, user);
        f.G2P("预热一下");
        double load = sw.Elapsed.TotalSeconds;
        GC.Collect();
        long afterLoad = Process.GetCurrentProcess().WorkingSet64 >> 20;
        var lines = File.ReadAllLines(input).Where(l => l.Trim().Length > 0).ToArray();
        int n = 0; foreach (var l in lines) n += l.Count(c => c >= '\u4e00' && c <= '\u9fff');
        int reps = 6;
        sw.Restart(); var cpu0 = Process.GetCurrentProcess().TotalProcessorTime;
        for (int r = 0; r < reps; r++) foreach (var l in lines) f.G2P(l);
        double wall = sw.Elapsed.TotalSeconds; double cpu = (Process.GetCurrentProcess().TotalProcessorTime - cpu0).TotalSeconds;
        Console.WriteLine($"{{\"hanzi\":{n * reps},\"load_s\":{load:0.00},\"ms_per_1000\":{wall / (n * reps) * 1e6:0.0},\"cpu_ms_per_1000\":{cpu / (n * reps) * 1e6:0.0},\"rss_before_mb\":{before},\"ws_after_load_mb\":{afterLoad},\"peak_rss_mb\":{PeakMb()}}}");
        return 0;
    }

    /// <summary>同一进程里交替测“关前端（= v0.4）/ 开前端”的合成耗时（墙钟 + CPU），消除加载和机器负载差异。</summary>
    public static int AbCpu(string packDir, string appDir, int rounds)
    {
        PolyphoneFrontend.StartLoading(appDir);
        if (PolyphoneFrontend.Shared(60000) == null) { Console.WriteLine("前端没有加载"); return 1; }
        var pack = VoicePack.TryLoad(packDir, out var why) ?? throw new Exception(why);
        using var engine = new TtsEngine();
        engine.Load(pack, 2, Directory.GetCurrentDirectory());
        string text = File.ReadAllText("/workspace/work/polylex/integ/bench_in.txt").Substring(0, int.Parse(Environment.GetEnvironmentVariable("CR_ABCHARS") ?? "600"));
        var clauses = TextSplitter.Split(text, new PauseSettings()).Select(c => TextSplitter.TextForEngine(c.Text)).ToList();
        var proc = Process.GetCurrentProcess();
        double[] wall = new double[2], cpu = new double[2], audio = new double[2];
        for (int r = 0; r < rounds; r++)
            foreach (bool fe in new[] { false, true })
            {
                TtsEngine.UseFrontend = fe;
                proc.Refresh(); var c0 = proc.TotalProcessorTime; var sw = Stopwatch.StartNew(); long n = 0;
                foreach (var c in clauses) n += engine.Synthesize(c, pack.Sid, 1f).Length;
                proc.Refresh();
                int k = fe ? 1 : 0; wall[k] += sw.Elapsed.TotalSeconds; cpu[k] += (proc.TotalProcessorTime - c0).TotalSeconds; audio[k] += n / (double)engine.SampleRate;
            }
        for (int k = 0; k < 2; k++)
            Console.WriteLine($"{pack.Id} {(k == 0 ? "关前端(v0.4方式)" : "开前端(v0.5)")}: 语音 {audio[k]:0.0}s，墙钟 RTF {wall[k] / audio[k]:0.000}，CPU 每秒语音 {cpu[k] / audio[k]:0.000}s");
        Console.WriteLine($"峰值内存 {PeakMb()} MB；{clauses.Count} 个小句 × {rounds} 轮");
        return 0;
    }

    public static int FrontendTest(string data, string packDir)
    {
        int fail = 0;
        void Check(string name, bool ok) { Console.WriteLine((ok ? "  [通过] " : "  [失败] ") + name); if (!ok) fail++; }
        string tmp = Path.Combine(Path.GetTempPath(), "cr_user_" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(tmp, "\uFEFF# 注释\n银行 yín háng\n六安 lu4 an1 # 地名\n长大 zhang3\n坏行 xx9 yy\n数一数 shu3 yi5 shu3\n绿地 lü4 di4\n老总行 lao3 zong3 hang2\n", new System.Text.UTF8Encoding(false));
        var f = PolyphoneFrontend.Load(data, tmp);
        File.Delete(tmp);
        Console.WriteLine($"  用户词 {f.UserWordCount} 个；提示：{string.Join(" | ", f.UserDictWarnings)}");
        Console.WriteLine($"  ToTone3: {PolyphoneFrontend.ToTone3("háng")} {PolyphoneFrontend.ToTone3("lǘ")} {PolyphoneFrontend.ToTone3("de")} {PolyphoneFrontend.ToTone3("ň")}");
        Check("用户词库：调号/数字调/注释/BOM 读入 5 条", f.UserWordCount == 5);
        Check("用户词库：字数不符、拼音不识别的行跳过并提示", f.UserDictWarnings.Count == 2);
        string R(string t) => string.Join(" ", f.G2P(t).Select(r => r.Pinyin));
        Check("用户词优先：六安", R("他去六安出差") == "ta1 qu4 lu4 an1 chu1 chai1");
        Check("ü 写法：绿地 lv4", R("绿地") == "lv4 di4");
        Check("词库 A 修正：总行 hang2", R("总行").EndsWith("hang2"));
        Check("ToTone3：háng→hang2、lǘ→lv2、de→de5、ň→n3", PolyphoneFrontend.ToTone3("háng") == "hang2" && PolyphoneFrontend.ToTone3("lǘ") == "lv2" && PolyphoneFrontend.ToTone3("de") == "de5" && PolyphoneFrontend.ToTone3("ň") == "n3");
        Check("上下文规则仍生效：还钱 / 还是", R("还钱").StartsWith("huan2") && R("还是").StartsWith("hai2"));
        var rd = f.G2P("一样不对"); PolyphoneFrontend.Sandhi(rd);
        Check("变调：一样 yi2，不对 bu2", rd[0].Pinyin == "yi2" && rd[2].Pinyin == "bu2");
        var pack = VoicePack.TryLoad(packDir, out var why);
        var map = pack?.Files?.PuaMap is string pm && pack.Inside(pm) is string pf ? PuaMap.TryLoad(pf) : null;
        Check("音色包带 pua_map 且能读入", map != null && map.Count > 1000);
        if (map != null)
        {
            string e = f.ToEngineText("银行行长，2024年。", map);
            Console.WriteLine("  PUA: " + string.Join(" ", e.Select(ch => ((int)ch).ToString("X4"))));
            Check("PUA 转换：汉字全换成私用区字符，标点和数字原样", e.Length == "银行行长，2024年。".Length && e[0] >= '\uE000' && e[4] == '，' && e.Contains("2024") && e[1] == e[2] && e[1] == map.Get("hang2")![0] && e[3] == map.Get("zhang3")![0]);
            string rare = "\U0002C317";   // 𬌗（只在 pinyin-data 补充表里，he2）
            string e2 = f.ToEngineText("咬" + rare, map);
            Console.WriteLine("  生僻字: " + string.Join(" ", e2.Select(ch => ((int)ch).ToString("X4"))) + " he2=" + ((int)map.Get("he2")![0]).ToString("X4"));
            Check("生僻字（扩展区代理对）按补充读音转换", e2.Length == 2 && e2[1] == map.Get("he2")![0]);
            Check("英文原样", f.ToEngineText("OK 好", map).StartsWith("OK "));
        }
        Console.WriteLine(fail == 0 ? "全部通过" : $"失败 {fail} 项");
        return fail == 0 ? 0 : 1;
    }
}


/// <summary>v0.6：悬浮球单击 / 双击判定、剪贴板方式规则的自测（纯逻辑，Linux 上也能跑）。</summary>
static class V06Tests
{
    public static int Run()
    {
        int fail = 0, n = 0;
        void Check(string name, bool ok) { n++; if (!ok) fail++; Console.WriteLine((ok ? "通过 " : "失败 ") + name); }

        // ---- 单击 / 双击 ----
        var A = ClickClassifier.Action.DeferSingle; var P = ClickClassifier.Action.PauseNow; var D = ClickClassifier.Action.Double;
        var c = new ClickClassifier(500, 4);
        Check("空闲时单击 → 延迟单击（等双击时间）", c.OnClick(1000, 10, 10, false) == A);
        Check("400ms 内同位置第二下 → 双击", c.OnClick(1400, 12, 11, false) == D);
        Check("双击之后紧接着第三下 → 不是双击（新的单击）", c.OnClick(1600, 12, 11, false) == A);
        c = new ClickClassifier(500, 4);
        Check("朗读中单击 → 立即暂停", c.OnClick(5000, 50, 50, true) == P);
        Check("朗读中单击后 300ms 第二下 → 双击（停止）", c.OnClick(5300, 51, 50, false) == D);
        c = new ClickClassifier(500, 4);
        c.OnClick(9000, 10, 10, false);
        Check("隔 600ms 的第二下 → 单击，不是双击", c.OnClick(9600, 10, 10, false) == A);
        c = new ClickClassifier(500, 4);
        c.OnClick(20000, 10, 10, false);
        Check("位置差 30 像素的第二下 → 单击，不是双击", c.OnClick(20200, 40, 10, false) == A);
        Check("延迟单击时间 = 系统双击时间（500）", new ClickClassifier(500, 4).DeferMs == 500);
        Check("系统双击时间被改成 2000 → 最多等 700ms", new ClickClassifier(2000, 4).DeferMs == 700);

        // ---- 剪贴板方式规则 ----
        var r = ClipboardRules.Decide("Notepad", "RichEditD2DPT", "notepad", 50030, true);
        Check("Win11 记事本：可以 Ctrl+C，也可以光标到文末", r.allowed && r.caretToEnd);
        r = ClipboardRules.Decide("OpusApp", "_WwG", "WINWORD", 0, false);
        Check("Word：可以光标到文末", r.allowed && r.caretToEnd);
        r = ClipboardRules.Decide("OpusApp", "", "wps", 0, false);
        Check("WPS 文字：可以光标到文末", r.allowed && r.caretToEnd);
        r = ClipboardRules.Decide("Chrome_WidgetWin_1", "Chrome_RenderWidgetHostHWND", "msedge", 50030, true);
        Check("Edge 网页（Document）：只探测选中，不按 Ctrl+Shift+End", r.allowed && !r.caretToEnd);
        r = ClipboardRules.Decide("Chrome_WidgetWin_1", "Chrome_RenderWidgetHostHWND", "chrome", 50004, true);
        Check("Chrome 网页里的输入框（Edit）：可以光标到文末", r.allowed && r.caretToEnd);
        r = ClipboardRules.Decide("AcrobatSDIWindow", "AVL_AVView", "Acrobat", 50033, true);
        Check("Adobe PDF：只探测选中", r.allowed && !r.caretToEnd);
        r = ClipboardRules.Decide("SUMATRA_PDF_FRAME", "SUMATRA_PDF_CANVAS", "SumatraPDF", 0, false);
        Check("SumatraPDF：只探测选中", r.allowed && !r.caretToEnd);
        r = ClipboardRules.Decide("CASCADIA_HOSTING_WINDOW_CLASS", "", "WindowsTerminal", 50004, true);
        Check("Windows 终端：绝不发 Ctrl+C", !r.allowed);
        r = ClipboardRules.Decide("ConsoleWindowClass", "", "cmd", 0, false);
        Check("命令提示符：绝不发 Ctrl+C", !r.allowed);
        r = ClipboardRules.Decide("CabinetWClass", "DirectUIHWND", "explorer", 0, false);
        Check("资源管理器：绝不发 Ctrl+C（会复制文件）", !r.allowed);
        r = ClipboardRules.Decide("TscShellContainerClass", "", "mstsc", 0, false);
        Check("远程桌面：绝不发 Ctrl+C", !r.allowed);

        Console.WriteLine($"v0.6 自测：{n - fail}/{n} 通过");
        return fail == 0 ? 0 : 1;
    }
}

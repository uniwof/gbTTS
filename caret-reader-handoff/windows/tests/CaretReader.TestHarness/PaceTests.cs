// 第 2 轮自动检查（CI 用真实音色跑）：
//   dotnet run -- pace <音色包文件夹> [更多音色包文件夹...]
//   1) 中文路径：把第一个音色包复制到含中文的临时文件夹再加载、合成，不闪退
//   2) 去静音：模型原始输出的首尾静音 vs 去静音后（≤ 保留量 + 1 帧）
//   3) 字/分：每个音色用固定句子测 cpmAtSpeed1；按目标 200/250/320 字/分换算 speed 再实测，误差 ≤ ±8%
//   4) 多个音色时：统一到 250 字/分后，彼此的实测字/分误差 ≤ ±8%
using CaretReader.Core;
using CaretReader.Core.Pacing;

static class PaceTests
{
    public static int Run(string[] packDirs)
    {
        if (packDirs.Length == 0) { Console.WriteLine("用法：pace <音色包文件夹>..."); return 2; }
        var trim = TrimOptions.LoadOrDefault(Path.Combine(AppContext.BaseDirectory, "rules", "pacing.tsv"));
        Pacer.Current = new Pacer(new PacingStore(null), trim);
        int fail = 0;
        void Check(bool ok, string what) { Console.WriteLine((ok ? "  [通过] " : "  [失败] ") + what); if (!ok) fail++; }

        // 1) 中文路径
        string cnRoot = Path.Combine(Path.GetTempPath(), "光标朗读 中文路径测试_" + Guid.NewGuid().ToString("N")[..6]);
        try
        {
            var src = VoicePack.TryLoad(packDirs[0], out var e0) ?? throw new Exception("音色包无效: " + e0);
            string dst = Path.Combine(cnRoot, "voices", src.Id);
            CopyDir(src.Dir, dst);
            var cn = VoicePack.TryLoad(dst, out var e1);
            Check(cn != null, "中文路径下音色包校验通过" + (e1 != null ? "（" + e1 + "）" : ""));
            if (cn != null)
            {
                using var eng = new TtsEngine();
                eng.Load(cn, 2, cnRoot);
                var a = eng.Synthesize("中文路径测试", cn.Sid, 1.0f);
                Check(a.Length > 0, $"中文路径下加载并合成不闪退（{(double)a.Length / eng.SampleRate:0.00}s）");
            }
        }
        catch (Exception ex) { Check(false, "中文路径加载：" + ex.Message); }
        finally { try { Directory.Delete(cnRoot, true); } catch { } }

        // 2)~4)
        var unified = new List<(string name, double cpm)>();
        foreach (var dir in packDirs)
        {
            var voice = VoicePack.TryLoad(dir, out var err);
            if (voice == null) { Check(false, $"音色包 {dir} 无效：{err}"); continue; }
            using var engine = new TtsEngine();
            engine.Load(voice, 2, Path.GetDirectoryName(Path.GetDirectoryName(voice.Dir))!);
            int sr = engine.SampleRate;
            Console.WriteLine($"[{voice.Name}] {voice.Id}，采样率 {sr}");

            double rawHead = 0, rawTail = 0, cutHead = 0, cutTail = 0;
            foreach (var c in PaceCalibrator.FixedClauses.Take(6))
            {
                var raw = engine.Synthesize(c, voice.Sid, 1.0f);
                var (h0, t0) = SilenceTrimmer.MeasureEdges(raw, sr, trim);
                var (h1, t1) = SilenceTrimmer.MeasureEdges(SilenceTrimmer.Trim(raw, sr, trim), sr, trim);
                rawHead = Math.Max(rawHead, h0); rawTail = Math.Max(rawTail, t0);
                cutHead = Math.Max(cutHead, h1); cutTail = Math.Max(cutTail, t1);
            }
            Console.WriteLine($"  模型原始首尾静音最多 {rawHead * 1000:0}ms / {rawTail * 1000:0}ms → 去静音后 {cutHead * 1000:0}ms / {cutTail * 1000:0}ms");
            Check(cutHead <= (trim.KeepHeadMs + trim.FrameMs) / 1000 + 0.005, $"去静音后开头静音 ≤ {trim.KeepHeadMs + trim.FrameMs:0}ms");
            Check(cutTail <= (trim.KeepTailMs + trim.FrameMs) / 1000 + 0.005, $"去静音后结尾静音 ≤ {trim.KeepTailMs + trim.FrameMs:0}ms");

            var m1 = PaceCalibrator.Measure(t => engine.Synthesize(t, voice.Sid, 1.0f), sr, trim);
            Console.WriteLine($"  speed=1.0：{m1.Chars} 字 / {m1.Seconds:0.00}s = {m1.CpmAtSpeed1:0} 字/分");
            Check(m1.CpmAtSpeed1 > 60, "测得 cpmAtSpeed1");
            foreach (int target in new[] { 200, 250, 320 })
            {
                double sp = PacingResolver.SpeedFor(target, m1.CpmAtSpeed1);
                var m = PaceCalibrator.Measure(t => engine.Synthesize(t, voice.Sid, (float)sp), sr, trim);
                double expect = PacingResolver.IsClamped(target, m1.CpmAtSpeed1) ? m1.CpmAtSpeed1 * sp : target;
                double errPct = (m.CpmAtSpeed1 / expect - 1) * 100;
                Check(Math.Abs(errPct) <= 8, $"目标 {target} 字/分 → speed {sp:0.000} → 实测 {m.CpmAtSpeed1:0} 字/分（误差 {errPct:+0.0;-0.0}%）");
                if (target == 250) unified.Add((voice.Name, m.CpmAtSpeed1));
            }
        }
        if (unified.Count >= 2)
        {
            double avg = unified.Average(x => x.cpm);
            foreach (var (name, cpm) in unified)
                Check(Math.Abs(cpm / avg - 1) <= 0.08, $"统一 250 字/分后 {name} {cpm:0} 字/分，与平均 {avg:0} 相差 {(cpm / avg - 1) * 100:+0.0;-0.0}%");
        }
        Console.WriteLine(fail == 0 ? "第 2 轮节奏检查全部通过" : $"第 2 轮节奏检查失败 {fail} 项");
        return fail == 0 ? 0 : 1;
    }

    private static void CopyDir(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.GetFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)), true);
        foreach (var d in Directory.GetDirectories(from)) CopyDir(d, Path.Combine(to, Path.GetFileName(d)));
    }
}

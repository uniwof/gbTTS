using CaretReader.Core;
using CaretReader.Core.Pacing;
using Xunit;

namespace CaretReader.Tests;

/// <summary>第 2 轮自动检查：去静音、字/分换算、微调设置升级不丢、中文路径。</summary>
public class PacingTests
{
    private static float[] Tone(int sr, double silenceHead, double speech, double silenceTail, float amp = 0.3f)
    {
        int h = (int)(sr * silenceHead), n = (int)(sr * speech), t = (int)(sr * silenceTail);
        var x = new float[h + n + t];
        var rnd = new Random(1);
        for (int i = 0; i < h; i++) x[i] = (float)(rnd.NextDouble() - 0.5) * 0.0005f;   // 底噪约 -70dB
        for (int i = 0; i < n; i++) x[h + i] = amp * (float)Math.Sin(2 * Math.PI * 220 * i / sr);
        for (int i = 0; i < t; i++) x[h + n + i] = (float)(rnd.NextDouble() - 0.5) * 0.0005f;
        return x;
    }

    [Fact]
    public void 去静音后首尾静音不超过阈值()
    {
        int sr = 22050;
        var o = TrimOptions.LoadOrDefault(Path.Combine(AppContext.BaseDirectory, "rules", "pacing.tsv"));
        Assert.True(o.Enabled);
        var x = Tone(sr, 0.35, 1.0, 0.6);
        var y = SilenceTrimmer.Trim(x, sr, o);
        var (head, tail) = SilenceTrimmer.MeasureEdges(y, sr, o);
        Assert.InRange(head, 0, o.KeepHeadMs / 1000 + o.FrameMs / 1000 + 0.005);
        Assert.InRange(tail, 0, o.KeepTailMs / 1000 + o.FrameMs / 1000 + 0.005);
        // 语音本身不被切掉（留了防切字的余量）
        Assert.InRange((double)y.Length / sr, 1.0, 1.0 + 0.09);
        Assert.Empty(SilenceTrimmer.Trim(new float[sr], sr, o));   // 全静音 → 空
    }

    [Fact]
    public void 字每分换算到speed并限制范围()
    {
        Assert.Equal(1.0, PacingResolver.SpeedFor(250, 250), 3);
        Assert.Equal(250.0 / 200, PacingResolver.SpeedFor(250, 200), 3);
        Assert.Equal(0.7, PacingResolver.SpeedFor(150, 400), 3);
        Assert.Equal(1.6, PacingResolver.SpeedFor(400, 150), 3);
        Assert.Equal(250.0 / 268 * 1.1, PacingResolver.SpeedFor(250, 268, 1.1), 3);
        Assert.True(PacingResolver.IsClamped(400, 150));
        Assert.Equal(250, PacingResolver.MigrateTarget(1.0, 268));
        Assert.Equal(320, PacingResolver.MigrateTarget(1.2, 268));
        Assert.Equal(5, PacingResolver.CountChars("你好，AB 1！"));
        // 三个不同快慢的音色：统一后实际字/分 = cpm × speed，与目标误差 ≤ 8%
        foreach (var cpm in new[] { 210.0, 268.0, 320.0 })
            Assert.InRange(cpm * PacingResolver.SpeedFor(250, cpm) / 250, 0.92, 1.08);
    }

    [Fact]
    public void 真实朗读累计够200字后启用统一语速()
    {
        var p = new Pacer(new PacingStore(null), new TrimOptions());
        Assert.Equal(1.2f, p.ResolveSpeed("v", 1.2f));       // 还不知道 cpm → 旧语速
        for (int i = 0; i < 25; i++) p.Observe("v", "一二三四五六七八九十", 22050 * 2, 22050, 1.0f);   // 10 字 / 2 秒 = 300 字/分
        Assert.Equal(300, p.CpmFor("v")!.Value, 0);
        Assert.Equal((float)(250.0 / 300), p.ResolveSpeed("v", 1.0f), 3);   // 旧倍数 1.0 → 目标 250 → 250/300
        Assert.Equal(250, p.Store.TargetCpm);
        Assert.Equal((float)(250.0 / 300), p.ResolveSpeed("v", 1.0f), 3);
        // 新界面上线前：用户把旧语速框改成 1.2 → 目标重新换算为 1.2×300=360
        Assert.Equal(1.2f, p.ResolveSpeed("v", 1.2f), 3);
        Assert.Equal(360, p.Store.TargetCpm);
        // 新界面直接改目标（不动旧框）→ 用新目标
        p.Store.TargetCpm = 270;
        Assert.Equal((float)(270.0 / 300), p.ResolveSpeed("v", 1.2f), 3);
    }

    [Fact]
    public void 微调设置在中文路径下保存并在升级后不丢()
    {
        string root = Path.Combine(Path.GetTempPath(), "光标朗读 测试_" + Guid.NewGuid().ToString("N"), "程序");
        Directory.CreateDirectory(root);
        try
        {
            string settings = Path.Combine(root, "settings.json");
            var s = PacingStore.ForSettingsFile(settings);
            s.TargetCpm = 283;                                   // → 取整 280
            s.SetOverride("chaowen-0", new VoiceOverride { Speed = 1.05, Pause = 1.2 });
            s.SetOverride("fanchen-119", new VoiceOverride { Speed = 3, Pause = 0.1 });   // 超范围 → 截断
            s.SetCpm("chaowen-0", new CpmRecord { CpmAtSpeed1 = 268, Source = "fixed" });
            s.Save();
            // “升级”：程序文件被覆盖，新版本重新打开 → 仍然在
            var t = PacingStore.ForSettingsFile(settings);
            Assert.Equal(280, t.TargetCpm);
            Assert.Equal(1.05, t.GetOverride("chaowen-0").Speed, 3);
            Assert.Equal(1.2, t.GetOverride("chaowen-0").Pause, 3);
            Assert.Equal(1.2, t.GetOverride("fanchen-119").Speed, 3);
            Assert.Equal(0.5, t.GetOverride("fanchen-119").Pause, 3);
            Assert.Equal(268, t.GetCpm("chaowen-0")!.CpmAtSpeed1);
            // 恢复跟随全局
            t.SetOverride("chaowen-0", null);
            Assert.True(t.GetOverride("chaowen-0").IsDefault);
            // 文件被写坏一部分：坏的那项跳过，其它照常
            File.WriteAllText(t.PacingPath!, "{ \"targetCpm\": \"快\", \"voices\": { \"a\": { \"speed\": 1.1 }, \"b\": 5 } }");
            var u = PacingStore.ForSettingsFile(settings);
            Assert.Null(u.TargetCpm);
            Assert.Equal(1.1, u.GetOverride("a").Speed, 3);
            // 整个文件坏掉：不闪退，用默认
            File.WriteAllText(u.PacingPath!, "不是 JSON");
            Assert.Null(PacingStore.ForSettingsFile(settings).TargetCpm);
        }
        finally { try { Directory.Delete(Path.GetDirectoryName(root)!, true); } catch { } }
    }

    [Fact]
    public void 去静音参数文件错行跳过()
    {
        string f = Path.Combine(Path.GetTempPath(), "pacing_" + Guid.NewGuid().ToString("N") + ".tsv");
        File.WriteAllText(f, "# 注释\nrelativeDb\t-30\nkeepTailMs\t很长\n坏行没有tab\nfloorDb\t-999\n");
        try
        {
            var o = TrimOptions.LoadOrDefault(f);
            Assert.Equal(-30, o.RelativeDb);
            Assert.Equal(50, o.KeepTailMs);   // 错行 → 默认
            Assert.Equal(-90, o.FloorDb);     // 超范围 → 截断
        }
        finally { File.Delete(f); }
    }
}

namespace CaretReader.Core.Pacing;

/// <summary>
/// 第 2 轮：首尾静音切除（文档 6.1）。纯计算，不引用 sherpa-onnx。
///
/// 做法：
///   * 以 10ms 为一帧算 RMS（dBFS）。
///   * 阈值 = max(FloorDb, 本句峰值帧 + RelativeDb)（默认 max(-50, 峰值-35)）。
///   * 找到第一个、最后一个超过阈值的帧；开头保留 KeepHeadMs（20ms），结尾保留 KeepTailMs（50ms），防止切字。
///   * 加淡入 3ms、淡出 8ms，防止爆音。
/// 句间停顿之后只由标点停顿表决定（SpeechPipeline 在切好的语音后面补静音）。
/// 参数来自 rules/pacing.tsv，音色包 voice.json 的 pacing.trimDb 可以覆盖 RelativeDb。
/// </summary>
public sealed class TrimOptions
{
    public bool Enabled { get; set; } = true;
    public double FrameMs { get; set; } = 10;
    public double FloorDb { get; set; } = -50;
    /// <summary>相对本句峰值的阈值（负数，dB）。</summary>
    public double RelativeDb { get; set; } = -35;
    public double KeepHeadMs { get; set; } = 20;
    public double KeepTailMs { get; set; } = 50;
    public double FadeInMs { get; set; } = 3;
    public double FadeOutMs { get; set; } = 8;
    /// <summary>长句中间（没有标点）切开处固定插入的间隙，秒，不乘倍率。</summary>
    public double InnerCutSeconds { get; set; } = 0.04;

    public TrimOptions Clone() => (TrimOptions)MemberwiseClone();

    public void Clamp()
    {
        FrameMs = Math.Clamp(Ok(FrameMs, 10), 2, 50);
        FloorDb = Math.Clamp(Ok(FloorDb, -50), -90, -20);
        RelativeDb = Math.Clamp(Ok(RelativeDb, -35), -70, -10);
        KeepHeadMs = Math.Clamp(Ok(KeepHeadMs, 20), 0, 300);
        KeepTailMs = Math.Clamp(Ok(KeepTailMs, 50), 0, 300);
        FadeInMs = Math.Clamp(Ok(FadeInMs, 3), 0, 50);
        FadeOutMs = Math.Clamp(Ok(FadeOutMs, 8), 0, 50);
        InnerCutSeconds = Math.Clamp(Ok(InnerCutSeconds, 0.04), 0, 0.5);
    }

    private static double Ok(double v, double def) => double.IsFinite(v) ? v : def;

    /// <summary>
    /// 读 rules/pacing.tsv（每行：键 Tab 值，# 开头是注释）。文件没有或某行写错 → 那一项用默认值，跳过错行，不抛异常。
    /// </summary>
    public static TrimOptions LoadOrDefault(string? path)
    {
        var o = new TrimOptions();
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return o;
        try
        {
            int bad = 0;
            foreach (var raw in File.ReadAllLines(path))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#')) continue;
                var cols = line.Split('\t');
                if (cols.Length < 2) { bad++; continue; }
                string key = cols[0].Trim(), val = cols[1].Trim();
                if (key.Equals("enabled", StringComparison.OrdinalIgnoreCase))
                {
                    if (val is "1" or "true" or "开") o.Enabled = true;
                    else if (val is "0" or "false" or "关") o.Enabled = false;
                    else bad++;
                    continue;
                }
                if (!double.TryParse(val, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d)) { bad++; continue; }
                switch (key.ToLowerInvariant())
                {
                    case "framems": o.FrameMs = d; break;
                    case "floordb": o.FloorDb = d; break;
                    case "relativedb": o.RelativeDb = d; break;
                    case "keepheadms": o.KeepHeadMs = d; break;
                    case "keeptailms": o.KeepTailMs = d; break;
                    case "fadeinms": o.FadeInMs = d; break;
                    case "fadeoutms": o.FadeOutMs = d; break;
                    case "innercutseconds": o.InnerCutSeconds = d; break;
                    default: break;   // 不认识的键：忽略（给以后的版本用）
                }
            }
            if (bad > 0) Log.Write($"pacing.tsv 有 {bad} 行无效，已跳过");
        }
        catch (Exception ex) { Log.Write("读取 pacing.tsv 失败，用默认去静音参数：" + ex.Message); }
        o.Clamp();
        return o;
    }
}

public static class SilenceTrimmer
{
    /// <summary>
    /// 返回切掉首尾静音、加好淡入淡出的新数组（不改原数组）。全是静音时返回空数组。
    /// </summary>
    public static float[] Trim(float[] samples, int sampleRate, TrimOptions o)
    {
        if (samples == null || samples.Length == 0 || sampleRate <= 0) return samples ?? Array.Empty<float>();
        var (start, end) = FindSpeech(samples, sampleRate, o);
        if (end <= start) return Array.Empty<float>();
        var r = new float[end - start];
        Array.Copy(samples, start, r, 0, r.Length);
        Fade(r, sampleRate, o.FadeInMs, o.FadeOutMs);
        return r;
    }

    /// <summary>找出要保留的范围 [start, end)（已含首尾保留的毫秒数）。</summary>
    public static (int start, int end) FindSpeech(float[] samples, int sampleRate, TrimOptions o)
    {
        int frame = Math.Max(1, (int)Math.Round(sampleRate * o.FrameMs / 1000.0));
        int frames = (samples.Length + frame - 1) / frame;
        var db = new double[frames];
        double peak = double.NegativeInfinity;
        for (int f = 0; f < frames; f++)
        {
            int a = f * frame, b = Math.Min(samples.Length, a + frame);
            double sum = 0;
            for (int i = a; i < b; i++) sum += samples[i] * (double)samples[i];
            double rms = Math.Sqrt(sum / Math.Max(1, b - a));
            db[f] = rms > 1e-9 ? 20 * Math.Log10(rms) : -180;
            if (db[f] > peak) peak = db[f];
        }
        double thr = Math.Max(o.FloorDb, peak + o.RelativeDb);
        int first = -1, last = -1;
        for (int f = 0; f < frames; f++) if (db[f] > thr) { first = f; break; }
        if (first < 0) return (0, 0);
        for (int f = frames - 1; f >= 0; f--) if (db[f] > thr) { last = f; break; }
        int head = (int)Math.Round(sampleRate * o.KeepHeadMs / 1000.0);
        int tail = (int)Math.Round(sampleRate * o.KeepTailMs / 1000.0);
        int start = Math.Max(0, first * frame - head);
        int end = Math.Min(samples.Length, (last + 1) * frame + tail);
        return (start, end);
    }

    /// <summary>首尾“静音”的长度（秒）：开头/结尾连续低于阈值的部分。自动检查用。</summary>
    public static (double head, double tail) MeasureEdges(float[] samples, int sampleRate, TrimOptions o)
    {
        var strict = o.Clone();
        strict.KeepHeadMs = 0; strict.KeepTailMs = 0;
        var (s, e) = FindSpeech(samples, sampleRate, strict);
        if (e <= s) return ((double)samples.Length / sampleRate, 0);
        return ((double)s / sampleRate, (double)(samples.Length - e) / sampleRate);
    }

    private static void Fade(float[] x, int sr, double inMs, double outMs)
    {
        int fi = Math.Min(x.Length / 2, (int)Math.Round(sr * inMs / 1000.0));
        int fo = Math.Min(x.Length / 2, (int)Math.Round(sr * outMs / 1000.0));
        for (int i = 0; i < fi; i++) x[i] *= (float)i / fi;
        for (int i = 0; i < fo; i++) x[x.Length - 1 - i] *= (float)i / fo;
    }
}

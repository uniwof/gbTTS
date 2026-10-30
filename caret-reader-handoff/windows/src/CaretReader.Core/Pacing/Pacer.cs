using System.Collections.Concurrent;

namespace CaretReader.Core.Pacing;

/// <summary>
/// 第 2 轮：“字/分”语速统一的纯计算部分（文档 6.3 / 精简版 4.2）。
///   speed = clamp(目标字每分 ÷ 音色 cpmAtSpeed1 × 本音色微调, 0.7, 1.6)
/// </summary>
public static class PacingResolver
{
    public const double MinSpeed = 0.7, MaxSpeed = 1.6;
    /// <summary>从真实朗读估计 cpm 时，至少累计这么多字才采用。</summary>
    public const int MinCharsForCpm = 200;
    /// <summary>累计到这么多字后不再更新（足够稳定）。</summary>
    public const int FreezeCharsForCpm = 1000;

    public static double SpeedFor(double targetCpm, double cpmAtSpeed1, double voiceSpeed = 1.0)
    {
        if (!(cpmAtSpeed1 > 0) || !(targetCpm > 0)) return 1.0;
        double s = targetCpm / cpmAtSpeed1 * (voiceSpeed > 0 ? voiceSpeed : 1);
        return Math.Clamp(s, MinSpeed, MaxSpeed);
    }

    /// <summary>超出 0.7～1.6 时为 true（界面可提示“此音色在该语速下可能失真”）。</summary>
    public static bool IsClamped(double targetCpm, double cpmAtSpeed1, double voiceSpeed = 1.0)
    {
        if (!(cpmAtSpeed1 > 0)) return false;
        double s = targetCpm / cpmAtSpeed1 * voiceSpeed;
        return s < MinSpeed - 1e-9 || s > MaxSpeed + 1e-9;
    }

    /// <summary>旧版“语速倍数”迁移到目标字/分：倍数≈1 用默认 250，否则 = 倍数 × cpm（取整到 10，限制 150～400）。</summary>
    public static int MigrateTarget(double legacySpeed, double cpmAtSpeed1)
    {
        if (!(cpmAtSpeed1 > 0) || !double.IsFinite(legacySpeed) || Math.Abs(legacySpeed - 1.0) < 0.02)
            return PacingStore.DefaultTargetCpm;
        return PacingStore.ClampTarget((int)Math.Round(legacySpeed * cpmAtSpeed1));
    }

    /// <summary>
    /// 计“字”：汉字、私用区字符（多音字前端）、英文字母、数字各算 1；标点和空格不算。
    /// 只返回数字，不保存、不记录文字本身。
    /// </summary>
    public static int CountChars(string? text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        int n = 0;
        foreach (char c in text)
            if ((c >= '\u4E00' && c <= '\u9FFF') || (c >= '\u3400' && c <= '\u4DBF') || (c >= '\uF900' && c <= '\uFAFF')
                || (c >= '\uE000' && c <= '\uF8FF') || (c < 128 && char.IsLetterOrDigit(c)))
                n++;
        return n;
    }
}

/// <summary>
/// 用固定句子在 speed=1.0 下测 cpm（CI 自动检查、第 3 轮本机测速都用它）。
/// synth 是“合成一个小句、返回采样”的函数（例如 t => engine.Synthesize(t, sid, 1.0f)）。
/// </summary>
public static class PaceCalibrator
{
    /// <summary>约 200 字的叙事文本（只用常见字，不含数字字母，结果稳定）。</summary>
    public static readonly string[] FixedClauses =
    {
        "清晨的阳光照进小院", "奶奶坐在门口择菜", "一边哼着年轻时学会的老歌",
        "院子里的桂花开得正好", "风一吹，香气就飘满了整条小巷", "隔壁的孩子背着书包跑过", "回头冲奶奶喊了一声早上好",
        "奶奶笑着答应", "叮嘱他路上慢一点", "别忘了把水壶带上",
        "过了一会儿", "邮递员骑着自行车来了", "送来一封远方的信", "信是孙女写的",
        "她说城里的工作很忙", "但每天都会想起家里的饭菜", "等到放假一定回来住几天",
        "奶奶把信读了一遍又一遍", "然后小心地折好放进抽屉", "转身去厨房准备午饭",
        "锅里的汤慢慢冒出热气", "窗外的鸟儿也安静了下来", "这样平常的一天", "就在饭菜的香味里慢慢过去了",
    };

    public sealed record Result(double CpmAtSpeed1, int Chars, double Seconds);

    public static Result Measure(Func<string, float[]> synth, int sampleRate, TrimOptions trim, CancellationToken token = default)
    {
        int chars = 0; double secs = 0;
        foreach (var c in FixedClauses)
        {
            token.ThrowIfCancellationRequested();
            var s = synth(c);
            var t = trim.Enabled ? SilenceTrimmer.Trim(s, sampleRate, trim) : s;
            chars += PacingResolver.CountChars(c);
            secs += (double)t.Length / sampleRate;
        }
        return new Result(secs > 0 ? chars / secs * 60 : 0, chars, secs);
    }
}

/// <summary>
/// 朗读时的节奏“总管”：SpeechPipeline 每次朗读开始时问它要语速和停顿倍率、每句合成后让它去静音并累计 cpm。
/// 界面（A 线设置页）通过 Store 读写「语速：字/分」和「本音色微调」，改完调用 Store.Save()。
/// </summary>
public sealed class Pacer
{
    /// <summary>总开关（测试程序对比旧版时可关）。关掉 = 完全按旧版：不去静音、按旧语速倍数。</summary>
    public static bool Enabled { get; set; } = true;

    private static Pacer? _current;
    private static readonly object CreateGate = new();

    /// <summary>
    /// 当前实例。没有显式设置时，第一次用到时按 settings.json 的位置自动打开（data\pacing.json、data\bench.json），
    /// 去静音参数读程序目录 rules\pacing.tsv。任何问题都退回内存里的默认值，绝不抛异常。
    /// </summary>
    public static Pacer Current
    {
        get
        {
            if (_current != null) return _current;
            lock (CreateGate)
            {
                if (_current != null) return _current;
                PacingStore store;
                try { store = PacingStore.ForSettingsFile(AppSettings.ResolvePath(AppContext.BaseDirectory)); }
                catch (Exception ex) { Log.Write("节奏设置无法打开，本次只用默认值：" + ex.Message); store = new PacingStore(null); }
                var trim = TrimOptions.LoadOrDefault(Path.Combine(AppContext.BaseDirectory, "rules", "pacing.tsv"));
                _current = new Pacer(store, trim);
                return _current;
            }
        }
        set { lock (CreateGate) _current = value; }
    }

    /// <summary>音色包 voice.json 里自带的 pacing（id → 数据）。VoicePack.TryLoad 读到时登记。</summary>
    public static ConcurrentDictionary<string, VoicePacing> PackPacing { get; } = new(StringComparer.OrdinalIgnoreCase);

    public PacingStore Store { get; }
    public TrimOptions Trim { get; }
    private readonly object _obsGate = new();

    public Pacer(PacingStore store, TrimOptions trim)
    {
        Store = store;
        Trim = trim;
    }

    /// <summary>某个音色在 speed=1.0 时的 cpm：音色包自带 → 固定句子测的 → 真实朗读累计够了的；都没有为 null。</summary>
    public double? CpmFor(string? voiceId)
    {
        if (string.IsNullOrEmpty(voiceId)) return null;
        if (PackPacing.TryGetValue(voiceId, out var p) && p.CpmAtSpeed1 > 0) return p.CpmAtSpeed1;
        var r = Store.GetCpm(voiceId);
        if (r == null || !(r.CpmAtSpeed1 > 0)) return null;
        if (r.Source == "fixed" || r.Chars >= PacingResolver.MinCharsForCpm) return r.CpmAtSpeed1;
        return null;
    }

    /// <summary>
    /// 本次朗读送给引擎的 speed。legacySpeed = 旧版算法得到的速度（音色倍数 × 设置里的倍数）。
    /// 还不知道这个音色的 cpm 时返回 legacySpeed（第一次朗读顺便累计，够 200 字后自动启用统一语速）。
    /// </summary>
    public float ResolveSpeed(string? voiceId, float legacySpeed)
    {
        if (!Enabled || string.IsNullOrEmpty(voiceId)) return legacySpeed;
        var cpm = CpmFor(voiceId);
        if (cpm == null) return legacySpeed;
        int target;
        double ls = Math.Round(legacySpeed, 3);
        // 目标已设过、且旧语速框没被改过 → 用目标；否则（第一次 / 用户改了旧语速框）由旧语速换算
        if (Store.TargetCpm is int t && (Store.LegacySpeed is not double prev || Math.Abs(prev - ls) < 0.005)) target = t;
        else
        {
            target = PacingResolver.MigrateTarget(legacySpeed, cpm.Value);
            Log.Write($"节奏：目标 {target} 字/分（由旧语速 {legacySpeed:0.00} 换算）");
            Store.TargetCpm = target;
            Store.LegacySpeed = ls;
            Store.Save();
        }
        if (Store.LegacySpeed == null) { Store.LegacySpeed = ls; Store.Save(); }
        var o = Store.GetOverride(voiceId);
        return (float)PacingResolver.SpeedFor(target, cpm.Value, o.Speed);
    }

    /// <summary>本音色的停顿倍率（音色包默认 × 用户微调），上限由调用方按 3 秒截断。</summary>
    public double PauseScale(string? voiceId)
    {
        if (!Enabled || string.IsNullOrEmpty(voiceId)) return 1.0;
        double pack = PackPacing.TryGetValue(voiceId, out var p) && p.DefaultPauseScale > 0 ? p.DefaultPauseScale : 1.0;
        return Math.Clamp(pack * Store.GetOverride(voiceId).Pause, 0.25, 4.0);
    }

    /// <summary>本音色的去静音参数（音色包 pacing.trimDb 可覆盖相对阈值）。</summary>
    public TrimOptions TrimFor(string? voiceId)
    {
        if (!string.IsNullOrEmpty(voiceId) && PackPacing.TryGetValue(voiceId, out var p) && p.TrimDb is double db && db < 0)
        {
            var o = Trim.Clone();
            o.RelativeDb = db;
            o.Clamp();
            return o;
        }
        return Trim;
    }

    /// <summary>
    /// 每句合成并去静音后调用：用真实朗读累计 cpm（只记字数和秒数，不记文字）。
    /// 音色包自带 cpm 或已用固定句子测过时不做事。
    /// </summary>
    public void Observe(string? voiceId, string clauseText, int trimmedSamples, int sampleRate, float speed)
    {
        if (!Enabled || string.IsNullOrEmpty(voiceId) || sampleRate <= 0 || trimmedSamples <= 0 || !(speed > 0)) return;
        if (PackPacing.TryGetValue(voiceId, out var p) && p.CpmAtSpeed1 > 0) return;
        int chars = PacingResolver.CountChars(clauseText);
        if (chars < 4) return;   // 太短的句子误差大
        bool save = false;
        lock (_obsGate)
        {
            var r = Store.GetCpm(voiceId) ?? new CpmRecord { Source = "reading" };
            if (r.Source == "fixed" || r.Chars >= PacingResolver.FreezeCharsForCpm) return;
            int before = r.Chars;
            r.Chars += chars;
            r.Seconds += (double)trimmedSamples / sampleRate * speed;   // 折算到 speed=1.0
            r.CpmAtSpeed1 = r.Seconds > 0 ? Math.Round(r.Chars / r.Seconds * 60, 1) : 0;
            Store.SetCpm(voiceId, r);
            save = before / PacingResolver.MinCharsForCpm != r.Chars / PacingResolver.MinCharsForCpm;
            if (save && before < PacingResolver.MinCharsForCpm)
                Log.Write($"节奏：音色 {voiceId} 测得约 {r.CpmAtSpeed1:0} 字/分（speed=1.0），下次朗读起统一语速");
        }
        if (save) Store.Save();
    }

    /// <summary>用固定句子测好后登记（CI / 第 3 轮测速）。</summary>
    public void RecordFixed(string voiceId, PaceCalibrator.Result r)
    {
        Store.SetCpm(voiceId, new CpmRecord { CpmAtSpeed1 = Math.Round(r.CpmAtSpeed1, 1), Chars = r.Chars, Seconds = Math.Round(r.Seconds, 3), Source = "fixed" });
        Store.Save();
    }
}

/// <summary>voice.json 里的 "pacing": { "cpmAtSpeed1": 268, "trimDb": -35, "defaultPauseScale": 1.0 }（全部可选）。</summary>
public sealed class VoicePacing
{
    public double CpmAtSpeed1 { get; set; }
    public double? TrimDb { get; set; }
    public double DefaultPauseScale { get; set; } = 1.0;
}

using CaretReader.Core.Pacing;
using System.Diagnostics;

namespace CaretReader.Core;

/// <summary>
/// “声音输出口”的抽象。Windows 程序里它是真正的扬声器播放 (NAudio)，
/// 测试程序里它是“写入 wav 文件 + 模拟实时播放”的假扬声器。
/// 这样同一套流水线代码在两边都能用、能测试。
/// </summary>
public interface IAudioSink
{
    /// <summary>开始一次朗读，告诉输出口采样率。</summary>
    void Begin(int sampleRate);

    /// <summary>追加一段音频（-1~1 浮点）。必须很快返回，不能阻塞到播放完。</summary>
    void Write(float[] samples);

    /// <summary>已经写入、但还没播放出去的音频有多少秒。</summary>
    double BufferedSeconds { get; }

    /// <summary>所有写入的音频都已经播放完了吗？</summary>
    bool IsDrained { get; }

    /// <summary>立刻停止播放并丢弃剩余音频。</summary>
    void Stop();
}

/// <summary>
/// 要朗读的小句队列（v0.2）。可以先放进一部分、开始朗读，之后再陆续追加，最后调用 Complete()。
/// 用途：先取光标后约 2000 字立刻开始读，剩下的文字在后台读取、切分后再追加进来。
/// 线程安全。
/// </summary>
public sealed class ClauseQueue
{
    private readonly List<Clause> _items = new();
    private readonly object _gate = new();
    private bool _completed;

    public ClauseQueue() { }
    public ClauseQueue(IEnumerable<Clause> all) { _items.AddRange(all); _completed = true; }

    public void Add(IEnumerable<Clause> more)
    {
        lock (_gate) { if (!_completed) _items.AddRange(more); Monitor.PulseAll(_gate); }
    }

    /// <summary>不会再追加了。</summary>
    public void Complete()
    {
        lock (_gate) { _completed = true; Monitor.PulseAll(_gate); }
    }

    /// <summary>当前已知的小句数量。</summary>
    public int Count { get { lock (_gate) return _items.Count; } }

    public bool IsCompleted { get { lock (_gate) return _completed; } }

    /// <summary>
    /// 取第 i 句。还没到就等（直到追加进来、Complete 或被取消）。返回 null = 已经没有了。
    /// </summary>
    public Clause? WaitFor(int i, CancellationToken token)
    {
        lock (_gate)
        {
            while (i >= _items.Count && !_completed && !token.IsCancellationRequested)
                Monitor.Wait(_gate, 50);
            return i < _items.Count ? _items[i] : null;
        }
    }

    /// <summary>不等待地看一眼第 i 句：(是否存在, 小句, 是否确定是最后一句之后)。</summary>
    public Clause? Peek(int i, out bool definitelyEnd)
    {
        lock (_gate)
        {
            definitelyEnd = _completed && i >= _items.Count;
            return i < _items.Count ? _items[i] : null;
        }
    }
}

/// <summary>一次朗读的统计数据（写日志、测试报告用）。</summary>
public sealed class PipelineStats
{
    public int Clauses;
    /// <summary>
    /// 第一段声音送出的时间。如果调用方给了“按下快捷键的时刻”，就从按键算起（= 用户实际感受到的延迟，
    /// 包含取文字的时间）；否则从流水线开始算起。
    /// </summary>
    public double FirstAudioLatencySeconds = -1;
    /// <summary>流水线开始时，距离按下快捷键已经过了多少秒（主要是取文字的耗时）。没有给按键时刻时为 0。</summary>
    public double StartOffsetSeconds;
    public double SynthSeconds;                  // 合成总耗时
    public double SpeechSeconds;                 // 合成出的语音总时长（不含插入的静音）
    public double SilenceSeconds;                // 插入的停顿静音总时长
    public bool Cancelled;
    public string? Error;
    /// <summary>实时率 RTF = 合成耗时 / 语音时长。小于 1 才来得及实时朗读，越小越好。</summary>
    public double Rtf => SpeechSeconds > 0 ? SynthSeconds / SpeechSeconds : 0;
}

/// <summary>
/// 边合成边播放的流水线：
///
///   [后台线程] 合成第 1 句 → 送去播放 → 立刻合成第 2 句 → 送去播放 → ...
///   [声卡]                  播放第 1 句……  播放第 2 句……
///
/// 第 1 句一合成好就开始出声（通常 1 秒内），之后“合成”总是跑在“播放”前面，
/// 所以听起来是连贯的。为了不浪费内存，最多只提前合成 MaxAheadSeconds 秒的音频。
///
/// 停止：调用 CancellationTokenSource.Cancel() 即可。播放会立刻停止；
/// 如果此刻正在合成某一句，这一句合成完后会被直接丢弃（引擎本身无法中途打断）。
/// </summary>
public static class SpeechPipeline
{
    /// <summary>最多提前合成多少秒的音频。</summary>
    public static double MaxAheadSeconds { get; set; } = 8.0;

    /// <summary>
    /// 启动缓冲（只在需要时才等）：
    ///   第 1 句合成好后，先预测“第 2 句要合成多久”（= 第 2 句字数 × 每字时长 × 当前 RTF）。
    ///   如果第 1 句的音频比这个时间长，说明来得及 → 立刻出声；
    ///   否则（例如第 1 句只有“雨停了，”三个字、而第 2 句很长、模型又较慢）
    ///   就再攒一句再出声，避免第 1 句播完后多出一段空白。
    ///   但无论如何，从开始到出声最多等 MaxStartupWaitSeconds 秒。
    /// 快的模型（Matcha、zh-ll 等）几乎总是立刻出声。
    /// </summary>
    public static double MaxStartupWaitSeconds { get; set; } = 1.0;   // 用户要求约 1 秒内出声（从按下快捷键算起）

    /// <summary>
    /// 启动缓冲最少给多少秒（取文字已经花掉了大部分预算时，也至少等这么久，避免开头就卡顿）。
    /// </summary>
    public static double MinStartupWaitSeconds { get; set; } = 0.3;

    /// <summary>
    /// 朗读一组小句，直到读完或被取消。整个过程在后台线程里跑。
    /// </summary>
    /// <param name="onClause">每送出一句时回调（可选，用于界面显示/日志）。</param>
    public static Task<PipelineStats> RunAsync(
        IReadOnlyList<Clause> clauses, TtsEngine engine, int sid, float speed,
        IAudioSink sink, CancellationToken token, Action<int, Clause>? onClause = null)
        => RunAsync(new ClauseQueue(clauses), engine, sid, speed, sink, token, onClause);

    /// <param name="hotkeyTimestamp">
    /// 按下快捷键时的 Stopwatch.GetTimestamp()（可选）。给了的话，“1 秒内出声”的预算从按键算起，
    /// 日志里的首句延迟也是用户真正感受到的“按键 → 出声”。
    /// </param>
    public static Task<PipelineStats> RunAsync(
        ClauseQueue clauses, TtsEngine engine, int sid, float speed,
        IAudioSink sink, CancellationToken token, Action<int, Clause>? onClause = null, long hotkeyTimestamp = 0,
        float gain = 1f)
    {
        return Task.Factory.StartNew(
            () => Run(clauses, engine, sid, speed, sink, token, onClause, hotkeyTimestamp, gain),
            token, TaskCreationOptions.LongRunning, TaskScheduler.Default)
            // 如果在开始前就被取消了，也返回一个统计对象而不是抛异常
            .ContinueWith(t => t.IsCanceled ? new PipelineStats { Cancelled = true }
                             : t.IsFaulted ? new PipelineStats { Error = t.Exception?.GetBaseException().Message }
                             : t.Result,
                          TaskScheduler.Default);
    }

    /// <summary>
    /// 最后一道保险：送进引擎的文字不超过 HardMaxClauseChars（正常情况下 TextSplitter 已经保证了）。
    /// </summary>
    private static IEnumerable<string> Guard(string text)
    {
        while (text.Length > TextSplitter.HardMaxClauseChars)
        {
            var (head, tail) = TextSplitter.HardCut(text, TextSplitter.MaxClauseChars);
            yield return head;
            text = tail;
        }
        if (text.Length > 0) yield return text;
    }

    /// <summary>
    /// v0.4：音色包的音量增益（voice.json 的 gain）。有的模型天生声音小（例如 fanchen-C #29 比别的小约 10dB），
    /// 乘上增益拉齐音量；超过 ±1 的部分用柔和的削波（tanh 过渡），避免硬削波的“破音”。
    /// </summary>
    public static void ApplyGain(float[] samples, float gain)
    {
        if (Math.Abs(gain - 1f) < 1e-4f) return;
        const float knee = 0.9f;
        for (int i = 0; i < samples.Length; i++)
        {
            float v = samples[i] * gain;
            float a = Math.Abs(v);
            if (a > knee) v = Math.Sign(v) * (knee + (1 - knee) * MathF.Tanh((a - knee) / (1 - knee)));
            samples[i] = v;
        }
    }

    private static PipelineStats Run(
        ClauseQueue clauses, TtsEngine engine, int sid, float speed,
        IAudioSink sink, CancellationToken token, Action<int, Clause>? onClause, long hotkeyTimestamp, float gain)
    {
        var stats = new PipelineStats();
        var clock = Stopwatch.StartNew();
        // 已经花掉的时间（取文字等）；没有给按键时刻时为 0
        double offset = hotkeyTimestamp > 0 ? Math.Max(0, Stopwatch.GetElapsedTime(hotkeyTimestamp).TotalSeconds) : 0;
        stats.StartOffsetSeconds = offset;
        double startupBudget = Math.Max(MinStartupWaitSeconds, MaxStartupWaitSeconds - offset);
        Timer? startTimer = null;
        int sr = engine.SampleRate;
        // 第 2 轮：节奏（去首尾静音、“字/分”统一语速、本音色停顿倍率）。Pacer.Enabled=false 时完全按旧版。
        var pacer = Pacer.Enabled ? Pacer.Current : null;
        string? voiceId = engine.LoadedPackId;
        speed = pacer?.ResolveSpeed(voiceId, speed) ?? speed;
        var trim = pacer?.TrimFor(voiceId);
        if (trim != null && !trim.Enabled) trim = null;
        double pauseScale = pacer?.PauseScale(voiceId) ?? 1.0;
        float[] Speak(string t)
        {
            var raw = engine.Synthesize(TextSplitter.TextForEngine(t), sid, speed);
            return trim == null ? raw : SilenceTrimmer.Trim(raw, sr, trim);
        }
        // 一按“停止”就立刻让声音停下来（不必等当前这句合成完）
        using var stopNow = token.Register(() => { try { sink.Stop(); } catch { } });
        try
        {
            if (sr <= 0) throw new InvalidOperationException("模型还没有加载。");
            sink.Begin(sr);
            List<float[]>? pending = new();   // 启动缓冲（见 MaxStartupWaitSeconds）
            double pendingSeconds = 0;
            int charsSoFar = 0;
            object pendingGate = new();
            void FlushPending()   // 调用时必须持有 pendingGate
            {
                if (pending == null || token.IsCancellationRequested) return;
                foreach (var p in pending) sink.Write(p);
                pending = null;
                stats.FirstAudioLatencySeconds = offset + clock.Elapsed.TotalSeconds;
            }

            for (int i = 0; ; i++)
            {
                // 播放缓冲里已经攒够了（或者用户按了“暂停”，声卡不再消耗缓冲）→ 等一等，别合成太多占内存
                while (sink.BufferedSeconds > MaxAheadSeconds && !token.IsCancellationRequested)
                    Thread.Sleep(30);
                if (token.IsCancellationRequested) break;

                // 取下一句（后续文字还在后台读取时会在这里等一下）
                var next = clauses.WaitFor(i, token);
                if (next == null || token.IsCancellationRequested) break;
                var clause = next;
                var t0 = clock.Elapsed;
                float[] speech;
                if (clause.Text.Length <= TextSplitter.HardMaxClauseChars)
                    speech = Speak(clause.Text);
                else
                {
                    var parts = new List<float[]>();
                    // 长句中间（没有标点）切开处：去静音后固定补 0.04 秒（InnerCut，不乘倍率）
                    var gap = trim == null ? Array.Empty<float>() : new float[(int)Math.Round(trim.InnerCutSeconds * sr)];
                    foreach (var part in Guard(clause.Text))
                    {
                        if (token.IsCancellationRequested) break;
                        if (parts.Count > 0 && gap.Length > 0) parts.Add(gap);
                        parts.Add(Speak(part));
                    }
                    speech = parts.SelectMany(x => x).ToArray();
                }
                pacer?.Observe(voiceId, clause.Text, trim == null ? 0 : speech.Length, sr, speed);
                stats.SynthSeconds += (clock.Elapsed - t0).TotalSeconds;
                if (token.IsCancellationRequested) break; // 合成期间被停止 → 丢弃这一句
                ApplyGain(speech, gain);

                // 语音 + 后面的停顿静音，拼成一段
                int silence = (int)Math.Round(Math.Clamp(clause.PauseAfterSeconds * pauseScale, 0, 3.0) * sr);
                var chunk = new float[speech.Length + silence];
                Array.Copy(speech, chunk, speech.Length);

                stats.Clauses++;
                stats.SpeechSeconds += (double)speech.Length / sr;
                stats.SilenceSeconds += (double)silence / sr;

                lock (pendingGate)
                {
                    if (pending != null)
                    {
                        // 还在“启动缓冲”阶段：先攒着
                        pending.Add(chunk);
                        pendingSeconds += (double)chunk.Length / sr;
                        var following = clauses.Peek(i + 1, out bool last);
                        double perChar = stats.SpeechSeconds / Math.Max(1, charsSoFar += clause.Text.Length);
                        // 下一句还没取到（后台还在读）时，按一个普通小句（36 字）估计
                        int nextLen = following?.Text.Length ?? TextSplitter.MaxClauseChars;
                        double nextSynth = last ? 0 : nextLen * perChar * stats.Rtf;
                        bool enough = pendingSeconds >= nextSynth + 0.1;
                        if (last || enough || clock.Elapsed.TotalSeconds >= startupBudget)
                            FlushPending();
                        else
                            // 最迟到预算用完时由定时器强制出声（此时后台可能正在合成下一句）
                            startTimer ??= new Timer(_ => { lock (pendingGate) FlushPending(); }, null,
                                TimeSpan.FromSeconds(Math.Max(0, startupBudget - clock.Elapsed.TotalSeconds)),
                                Timeout.InfiniteTimeSpan);
                    }
                    else
                    {
                        sink.Write(chunk);
                    }
                }
                onClause?.Invoke(i, clause);
            }

            // 正常结束时把启动缓冲里剩下的也送出（例如最后一句之后才知道“已经没有了”）
            lock (pendingGate) FlushPending();
            startTimer?.Dispose();
            startTimer = null;
            // 被取消时 pending 里的音频直接丢弃
            // 全部送出后，等声卡把剩下的播完（暂停期间 IsDrained 一直是 false，所以会一直等到继续并播完）
            while (!token.IsCancellationRequested && !sink.IsDrained)
                Thread.Sleep(30);
        }
        catch (Exception ex)
        {
            stats.Error = ex.Message;
            Log.Write("朗读出错: " + ex);
        }
        finally
        {
            try { startTimer?.Dispose(); } catch { }
            stats.Cancelled = token.IsCancellationRequested;
            try { sink.Stop(); } catch { }
        }
        return stats;
    }
}

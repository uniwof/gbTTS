namespace CaretReader.Core;

/// <summary>
/// 第 4 轮 A4：三档自适应。按本机测速（RTF）把电脑分成 流畅/可用/可能卡顿，
/// 自动选线程数和分句长度；朗读中连续卡顿则自动降一档。只用数字，不接触朗读文字。
/// </summary>
public static class AdaptiveTier
{
    /// <summary>档位 0=流畅 1=可用 2=可能卡顿；没测过按“可用”。</summary>
    public static int TierOf(double rtf)
    {
        var c = VoiceSpeed.Classify(rtf);
        return c switch { SpeedClass.Smooth => 0, SpeedClass.Laggy => 2, _ => 1 };
    }

    /// <summary>线程数：流畅档保守（省电不抢资源），卡顿档用满核（最多 6，留一核给系统）。</summary>
    public static int Threads(int tier, int cores)
    {
        cores = Math.Max(1, cores);
        int t = tier switch { 0 => 2, 1 => 3, _ => cores - 1 };
        return Math.Clamp(t, 1, Math.Min(6, cores));
    }

    /// <summary>单句最长字数：卡顿档切短一些，让第一句更快出声。不超过 TextSplitter.MaxClauseChars。</summary>
    public static int ClauseChars(int tier) => tier switch { 0 => 36, 1 => 28, _ => 20 };

    public static string Label(int tier) => tier switch { 0 => "流畅", 1 => "可用", _ => "可能卡顿" };
}

/// <summary>
/// 运行时卡顿监视：每合成一句报告（合成耗时秒, 语音秒数）。连续 3 句 RTF &gt; 1.0 判为卡顿，
/// 触发一次 Degraded（调用方降一档）。降档后清零重新数。线程安全。
/// </summary>
public sealed class StutterMonitor
{
    public const double SlowRtf = 1.0;
    public const int ConsecutiveLimit = 3;
    private int _slow;
    private readonly object _gate = new();
    public event Action? Degraded;

    public void Report(double synthSeconds, double speechSeconds)
    {
        if (speechSeconds < 0.5) return;   // 太短的句子不可靠
        bool fire = false;
        lock (_gate)
        {
            if (synthSeconds / speechSeconds > SlowRtf) { if (++_slow >= ConsecutiveLimit) { _slow = 0; fire = true; } }
            else _slow = 0;
        }
        if (fire) Degraded?.Invoke();
    }
}

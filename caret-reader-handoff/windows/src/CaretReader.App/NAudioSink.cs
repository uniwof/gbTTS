using System.Diagnostics;
using CaretReader.Core;
using NAudio.Wave;

namespace CaretReader.App;

/// <summary>
/// 真正的“扬声器”：用 NAudio 的 WaveOutEvent 播放（底层是 Windows 自带的 winmm，兼容性最好）。
///
/// 工作方式：后台合成线程不断往 BufferedWaveProvider（一个“音频水池”）里加音频，
/// 声卡从水池里取音频播放。水池空了就自动播放静音（ReadFully=true），不会报错或卡死。
///
/// 暂停 / 继续（v0.2）：调用 WaveOutEvent.Pause() / Play()。暂停时声卡不再从水池取音频，
/// 水池里的音频原样保留，所以“继续”会从停下的地方接着读；后台合成线程发现水池满了（超过 8 秒）
/// 就会自动停下来等待（流水线的“背压”），不会无限占用内存。
/// </summary>
internal sealed class NAudioSink : IAudioSink, IDisposable
{
    private readonly object _gate = new();
    private WaveOutEvent? _out;
    private BufferedWaveProvider? _buf;
    private readonly Stopwatch _emptySince = new();
    private bool _stopped;
    private bool _paused;

    /// <summary>当前是否处于暂停状态。</summary>
    public bool IsPaused { get { lock (_gate) return _paused; } }

    // v0.6：当前播放音量（0~1），悬浮球小人的嘴巴跟着它开合。暂停时为 0。
    private LevelMeter? _meter;
    public float Level => IsPaused ? 0f : (_meter?.Level ?? 0f);

    /// <summary>声卡每取一段音频就算一下这段的峰值（取音频比真正出声早约 0.1~0.2 秒，看起来正好跟得上）。</summary>
    private sealed class LevelMeter : IWaveProvider
    {
        private readonly IWaveProvider _src;
        private volatile float _level;
        public LevelMeter(IWaveProvider src) => _src = src;
        public WaveFormat WaveFormat => _src.WaveFormat;
        public float Level => _level;
        public int Read(byte[] buffer, int offset, int count)
        {
            int n = _src.Read(buffer, offset, count);
            int peak = 0;
            for (int i = offset; i + 1 < offset + n; i += 2)
            {
                int v = Math.Abs((int)(short)(buffer[i] | (buffer[i + 1] << 8)));
                if (v > peak) peak = v;
            }
            _level = peak / 32768f;
            return n;
        }
    }

    public void Begin(int sampleRate)
    {
        lock (_gate)
        {
            if (_stopped) return;
            _buf = new BufferedWaveProvider(new WaveFormat(sampleRate, 16, 1))
            {
                // 水池容量：远大于“最多提前合成”的 8 秒，保证不会溢出
                BufferDuration = TimeSpan.FromSeconds(90),
                DiscardOnBufferOverflow = false,
                ReadFully = true,
            };
            // 200ms 延迟、3 个缓冲：低配电脑上也不容易爆音
            var output = new WaveOutEvent { DesiredLatency = 200, NumberOfBuffers = 3 };
            try
            {
                _meter = new LevelMeter(_buf);
                output.Init(_meter);
                if (!_paused) output.Play();   // 还没开始出声就按了暂停：先不播放，等“继续”
            }
            catch (Exception ex)
            {
                try { output.Dispose(); } catch { }
                _buf = null;
                throw new InvalidOperationException(FriendlyAudioError(ex), ex);
            }
            _out = output;
        }
    }

    /// <summary>把 NAudio / winmm 的英文错误换成用户看得懂的中文。</summary>
    private static string FriendlyAudioError(Exception ex)
    {
        string m = ex.Message ?? "";
        if (m.Contains("NoDriver", StringComparison.OrdinalIgnoreCase) || m.Contains("BadDeviceId", StringComparison.OrdinalIgnoreCase))
            return "没有找到可以发声的设备（扬声器或耳机）。请插上耳机/音箱，或在 Windows 声音设置里启用输出设备后再试。";
        if (m.Contains("Allocated", StringComparison.OrdinalIgnoreCase))
            return "声音设备正被其它程序独占，请关闭占用声卡的程序后再试。";
        return "无法打开声音设备：" + m;
    }

    /// <summary>暂停播放（可以从任意线程调用）。水池里的音频保留，继续时接着播。</summary>
    public void Pause()
    {
        lock (_gate)
        {
            if (_stopped || _paused) return;
            _paused = true;
            try { _out?.Pause(); } catch { }
        }
    }

    /// <summary>从暂停处继续播放。</summary>
    public void Resume()
    {
        lock (_gate)
        {
            if (_stopped || !_paused) return;
            _paused = false;
            _emptySince.Reset();
            try { _out?.Play(); } catch { }
        }
    }

    public void Write(float[] samples)
    {
        // 浮点 (-1~1) → 16 位整数 PCM（所有声卡都支持的格式）
        var bytes = new byte[samples.Length * 2];
        for (int i = 0; i < samples.Length; i++)
        {
            float f = samples[i];
            if (f > 1f) f = 1f; else if (f < -1f) f = -1f;
            short v = (short)(f * 32767f);
            bytes[2 * i] = (byte)v;
            bytes[2 * i + 1] = (byte)(v >> 8);
        }
        lock (_gate)
        {
            if (_stopped || _buf == null) return;
            _buf.AddSamples(bytes, 0, bytes.Length);
            _emptySince.Reset();
        }
    }

    public double BufferedSeconds
    {
        get { lock (_gate) return _buf?.BufferedDuration.TotalSeconds ?? 0; }
    }

    /// <summary>
    /// 水池空了之后，声卡里还有约 DesiredLatency 的音频在播，所以要再等 0.4 秒才算真正播完。
    /// </summary>
    public bool IsDrained
    {
        get
        {
            lock (_gate)
            {
                if (_stopped || _buf == null) return true;
                // 暂停中：声卡里还压着最后约 0.2 秒音频没播，不能算播完
                if (_paused) { _emptySince.Reset(); return false; }
                if (_buf.BufferedBytes > 0) { _emptySince.Reset(); return false; }
                if (!_emptySince.IsRunning) _emptySince.Start();
                return _emptySince.ElapsedMilliseconds > 400;
            }
        }
    }

    /// <summary>立刻停止（可以从任意线程调用，多次调用也没关系）。</summary>
    public void Stop()
    {
        lock (_gate)
        {
            _stopped = true;
            try { _out?.Stop(); } catch { }
            try { _out?.Dispose(); } catch { }
            _out = null;
            _buf?.ClearBuffer();
            _buf = null;
        }
    }

    public void Dispose() => Stop();
}

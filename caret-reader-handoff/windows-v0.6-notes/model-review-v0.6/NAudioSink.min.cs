using System.Diagnostics;
using CaretReader.Core;
using NAudio.Wave;
namespace CaretReader.App;
internal sealed class NAudioSink : IAudioSink, IDisposable
{
    private readonly object _gate = new();
    private WaveOutEvent? _out;
    private BufferedWaveProvider? _buf;
    private readonly Stopwatch _emptySince = new();
    private bool _stopped;
    private bool _paused;
    public bool IsPaused { get { lock (_gate) return _paused; } }
    private LevelMeter? _meter;
    public float Level => IsPaused ? 0f : (_meter?.Level ?? 0f);
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
                BufferDuration = TimeSpan.FromSeconds(90),
                DiscardOnBufferOverflow = false,
                ReadFully = true,
            };
            var output = new WaveOutEvent { DesiredLatency = 200, NumberOfBuffers = 3 };
            try
            {
                _meter = new LevelMeter(_buf);
                output.Init(_meter);
                if (!_paused) output.Play();
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
    private static string FriendlyAudioError(Exception ex)
    {
        string m = ex.Message ?? "";
        if (m.Contains("NoDriver", StringComparison.OrdinalIgnoreCase) || m.Contains("BadDeviceId", StringComparison.OrdinalIgnoreCase))
            return "<zh>";
        if (m.Contains("Allocated", StringComparison.OrdinalIgnoreCase))
            return "<zh>";
        return "<zh>" + m;
    }
    public void Pause()
    {
        lock (_gate)
        {
            if (_stopped || _paused) return;
            _paused = true;
            try { _out?.Pause(); } catch { }
        }
    }
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
    public bool IsDrained
    {
        get
        {
            lock (_gate)
            {
                if (_stopped || _buf == null) return true;
                if (_paused) { _emptySince.Reset(); return false; }
                if (_buf.BufferedBytes > 0) { _emptySince.Reset(); return false; }
                if (!_emptySince.IsRunning) _emptySince.Start();
                return _emptySince.ElapsedMilliseconds > 400;
            }
        }
    }
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

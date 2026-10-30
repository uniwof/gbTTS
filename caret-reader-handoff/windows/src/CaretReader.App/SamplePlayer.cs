using CaretReader.Core;
using NAudio.Wave;

namespace CaretReader.App;

/// <summary>
/// v0.4：播放音色包里预先做好的试听文件 sample.mp3（不实时合成，点一下立刻出声，也不占用语音引擎）。
///
/// MP3 用纯 C# 的 NLayer 解码（约 100KB），不依赖 Windows 自带的 MP3 解码器
/// （Windows N / KN 版本、精简版系统里可能没有）。也接受 sample.wav。
/// 同一时间只播一个；再点同一个喇叭 = 停止。
/// </summary>
internal sealed class SamplePlayer : IDisposable
{
    private readonly object _gate = new();
    private WaveOutEvent? _out;
    private int _session;
    private readonly SynchronizationContext? _ui = SynchronizationContext.Current;

    /// <summary>正在试听的音色 id（没有则为 null）。</summary>
    public string? PlayingId { get; private set; }

    /// <summary>开始 / 结束试听时触发（在界面线程上）。</summary>
    public event Action? StateChanged;

    /// <summary>出错时触发（中文说明，在界面线程上）。</summary>
    public event Action<string>? Failed;

    public void Toggle(string id, string path)
    {
        if (PlayingId == id) { Stop(); return; }
        Play(id, path);
    }

    public void Play(string id, string path)
    {
        Stop();
        int session;
        lock (_gate) session = ++_session;
        PlayingId = id;
        Raise();
        // 解码放到后台线程（10 秒左右的 mp3，几十毫秒）
        Task.Run(() =>
        {
            try
            {
                var (pcm, rate, ch) = Decode(path);
                lock (_gate)
                {
                    if (session != _session) return;   // 已经被停止 / 换成别的试听了
                    var src = new RawSourceWaveStream(new MemoryStream(pcm), new WaveFormat(rate, 16, ch));
                    var o = new WaveOutEvent { DesiredLatency = 150, NumberOfBuffers = 3 };
                    o.Init(src);
                    o.PlaybackStopped += (_, e) =>
                    {
                        if (e.Exception != null) Log.Write("试听播放出错: " + e.Exception.Message);
                        bool mine;
                        lock (_gate) mine = session == _session;
                        if (mine) Post(() => { if (session == _session) { Cleanup(); PlayingId = null; Raise(); } });
                    };
                    _out = o;
                    o.Play();
                }
            }
            catch (Exception ex)
            {
                Log.Write($"试听失败（{path}）: {ex}");
                Post(() =>
                {
                    if (session != _session) return;
                    PlayingId = null;
                    Raise();
                    Failed?.Invoke(ex is FileNotFoundException ? "找不到试听文件" : "试听文件无法播放（文件损坏或没有声音设备）");
                });
            }
        });
    }

    public void Stop()
    {
        lock (_gate)
        {
            _session++;
            Cleanup();
        }
        if (PlayingId != null) { PlayingId = null; Raise(); }
    }

    private void Cleanup()
    {
        var o = _out;
        _out = null;
        if (o == null) return;
        try { o.Stop(); } catch { }
        try { o.Dispose(); } catch { }
    }

    private void Raise() => StateChanged?.Invoke();

    private void Post(Action a)
    {
        if (_ui != null) _ui.Post(_ => a(), null); else a();
    }

    /// <summary>解码成 16 位 PCM。mp3 用 NLayer；wav 用 NAudio。最多 30 秒（试听文件不应更长；防止异常文件解码出几百 MB）。</summary>
    private static (byte[] pcm, int rate, int channels) Decode(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException(path);
        if (path.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
        {
            using var r = new WaveFileReader(path);
            var sp = r.ToSampleProvider();
            return ToPcm(sp.WaveFormat.SampleRate, sp.WaveFormat.Channels, buf => sp.Read(buf, 0, buf.Length));
        }
        using var mp3 = new NLayer.MpegFile(path);
        return ToPcm(mp3.SampleRate, mp3.Channels, buf => mp3.ReadSamples(buf, 0, buf.Length));
    }

    private static (byte[], int, int) ToPcm(int rate, int ch, Func<float[], int> read)
    {
        if (rate < 8000 || rate > 192000 || ch < 1 || ch > 2) throw new InvalidDataException("试听文件格式不支持");
        int max = rate * ch * 30;
        var ms = new MemoryStream();
        var buf = new float[8192];
        int total = 0, n;
        while ((n = read(buf)) > 0 && total < max)
        {
            for (int i = 0; i < n; i++)
            {
                short v = (short)(Math.Clamp(buf[i], -1f, 1f) * 32767f);
                ms.WriteByte((byte)v);
                ms.WriteByte((byte)(v >> 8));
            }
            total += n;
        }
        if (total == 0) throw new InvalidDataException("试听文件里没有声音");
        return (ms.ToArray(), rate, ch);
    }

    public void Dispose() => Stop();
}

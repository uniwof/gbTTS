using CaretReader.Core;
using Xunit;

namespace CaretReader.Tests;

/// <summary>第 3 轮：上一句 / 下一句靠“播放到第几句”，这里只测这个小计算。</summary>
public class PlaybackTrackerTests
{
    private sealed class FakeSink : IAudioSink
    {
        public double Buffered;
        public void Begin(int sampleRate) { }
        public void Write(float[] samples) { }
        public double BufferedSeconds => Buffered;
        public bool IsDrained => Buffered <= 0;
        public void Stop() { }
    }

    [Fact]
    public void FollowsPlaybackPosition()
    {
        var sink = new FakeSink();
        var t = new PlaybackTracker();
        t.Attach(sink);
        Assert.Equal(7, t.CurrentIndex(7));      // 一句都没写入：用备用值
        t.Written(3, 2.0); t.Written(4, 2.0); t.Written(5, 2.0);
        sink.Buffered = 6.0;                      // 还没播
        Assert.Equal(3, t.CurrentIndex());
        sink.Buffered = 3.5;                      // 已播 2.5 秒 → 第 4 句
        Assert.Equal(4, t.CurrentIndex());
        sink.Buffered = 1.0;                      // 已播 5 秒 → 第 5 句
        Assert.Equal(5, t.CurrentIndex());
        sink.Buffered = 0;                        // 全播完：停在最后一句
        Assert.Equal(5, t.CurrentIndex());
    }
}

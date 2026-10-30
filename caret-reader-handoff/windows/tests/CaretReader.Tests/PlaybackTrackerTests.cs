using CaretReader.Core;
using Xunit;
using CaretReader.Core;

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

/// <summary>第 3 轮：测速分档、排序、默认音色的小检查。</summary>
public class VoiceSpeedTests
{
    private static VoicePack P(string name, int rank, int order = 100) => new() { Name = name, Rank = rank, Order = order };

    [Theory]
    [InlineData(0.3, SpeedClass.Smooth)]
    [InlineData(0.5, SpeedClass.Smooth)]
    [InlineData(0.51, SpeedClass.Usable)]
    [InlineData(0.8, SpeedClass.Usable)]
    [InlineData(0.81, SpeedClass.Laggy)]
    [InlineData(0, SpeedClass.Unknown)]
    public void Classify(double rtf, SpeedClass expect) => Assert.Equal(expect, VoiceSpeed.Classify(rtf));

    [Fact]
    public void StoreSurvivesBadFileAndChineseFolder()
    {
        string dir = Path.Combine(Path.GetTempPath(), "gb测速" + Guid.NewGuid().ToString("N")[..6]);
        string path = Path.Combine(dir, "data", "speed.json");
        try
        {
            var s = new SpeedStore(path);
            s.Set("a", 0.4);
            Assert.True(new SpeedStore(path).TryGet("a", out double r) && Math.Abs(r - 0.4) < 1e-9);
            File.WriteAllText(path, "{坏文件");
            Assert.False(new SpeedStore(path).TryGet("a", out _));   // 坏文件当作没测过，不崩
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Default_Is_Best_Rank_When_Nothing_Measured()
    {
        var store = new SpeedStore(Path.Combine(Path.GetTempPath(), "gbnone" + Guid.NewGuid().ToString("N")[..6], "s.json"));
        var list = new List<VoicePack> { P("乙", 5), P("甲", 1), P("丙", 9) };
        Assert.Equal("甲", VoiceSpeed.PickDefault(list, store)!.Name);
    }
}

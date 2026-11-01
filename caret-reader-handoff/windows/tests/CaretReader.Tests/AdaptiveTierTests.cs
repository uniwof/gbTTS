using CaretReader.Core;
using Xunit;

public class AdaptiveTierTests
{
    [Theory]
    [InlineData(0.3, 0)] [InlineData(0.6, 1)] [InlineData(0.9, 2)] [InlineData(0, 1)]
    public void Tier(double rtf, int tier) => Assert.Equal(tier, AdaptiveTier.TierOf(rtf));

    [Fact]
    public void ThreadsAndClauseBounds()
    {
        foreach (int cores in new[] { 1, 2, 4, 8, 16 })
            for (int t = 0; t < 3; t++)
            {
                int n = AdaptiveTier.Threads(t, cores);
                Assert.InRange(n, 1, Math.Min(6, cores));
            }
        Assert.True(AdaptiveTier.ClauseChars(2) < AdaptiveTier.ClauseChars(0));
        Assert.True(AdaptiveTier.ClauseChars(0) <= TextSplitter.MaxClauseChars);
    }

    [Fact]
    public void MonitorFiresAfterThreeSlowOnly()
    {
        var m = new StutterMonitor(); int n = 0; m.Degraded += () => n++;
        m.Report(2, 1); m.Report(2, 1); m.Report(0.2, 1); m.Report(2, 1); m.Report(2, 1);
        Assert.Equal(0, n);
        m.Report(2, 1);
        Assert.Equal(1, n);
        m.Report(5, 0.2); // 太短忽略
        Assert.Equal(1, n);
    }
}

using System.Drawing;
using CaretReader.Core;
using Xunit;

namespace CaretReader.Tests;

public class ToolbarLayoutTests
{
    static readonly Rectangle Wa = new(0, 0, 1920, 1040);

    [Fact] public void ToolbarOnLeftWhenBallOnRight()
    {
        var r = BallLayout.ToolbarRect(new Rectangle(1800, 500, 56, 56), Wa, 200, 32, 6);
        Assert.True(r.Right <= 1800);
    }
    [Fact] public void ToolbarOnRightWhenBallOnLeft()
    {
        var r = BallLayout.ToolbarRect(new Rectangle(10, 500, 56, 56), Wa, 200, 32, 6);
        Assert.True(r.Left >= 66);
    }
    [Fact] public void ToolbarStaysInWorkArea()
    {
        var r = BallLayout.ToolbarRect(new Rectangle(10, 1030, 56, 56), Wa, 200, 32, 6);
        Assert.True(Wa.Contains(r));
    }
    [Theory]
    [InlineData(false, true, 799, 0, false)]
    [InlineData(false, true, 800, 0, true)]
    [InlineData(false, false, 5000, 0, false)]
    [InlineData(true, false, 0, 999, true)]
    [InlineData(true, false, 0, 1000, false)]
    [InlineData(true, true, 0, 5000, true)]
    public void HoverTiming(bool open, bool over, int overMs, int awayMs, bool expect)
        => Assert.Equal(expect, BallLayout.ToolbarShouldBeOpen(open, over, overMs, awayMs));
}

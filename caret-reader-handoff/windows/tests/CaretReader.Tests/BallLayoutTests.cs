using System.Drawing;
using CaretReader.Core;
using Xunit;

namespace CaretReader.Tests;

/// <summary>P0 悬浮球：拖动阈值、■ 按钮位置、滑出动画（文档 10.1）。</summary>
public class BallLayoutTests
{
    private static readonly Rectangle Wa = new(0, 0, 1920, 1040);

    [Theory]
    [InlineData(0, 0, false)]
    [InlineData(4, 0, false)]      // 正好 4px：仍是点击
    [InlineData(-4, 4, false)]
    [InlineData(5, 0, true)]       // 超过 4px：拖动
    [InlineData(0, -5, true)]
    public void Drag_threshold_is_4px(int dx, int dy, bool drag) => Assert.Equal(drag, BallLayout.IsDrag(dx, dy));

    [Fact]
    public void Ball_at_right_edge_puts_stop_on_left()
    {
        var ball = new Rectangle(1840, 960, 60, 60);
        var r = BallLayout.StopButtonRect(ball, Wa, 28, 6);
        Assert.True(r.Right <= ball.Left);
        Assert.True(Wa.Contains(r));
    }

    [Fact]
    public void Ball_at_left_edge_puts_stop_on_right()
    {
        var ball = new Rectangle(0, 400, 60, 60);
        var r = BallLayout.StopButtonRect(ball, Wa, 28, 6);
        Assert.True(r.Left >= ball.Right);
        Assert.Equal(ball.Top + 16, r.Top);   // 与球中心对齐
    }

    [Fact]
    public void Second_monitor_uses_its_own_work_area()
    {
        var wa2 = new Rectangle(1920, 0, 1280, 1024);
        var ball = new Rectangle(1930, 500, 60, 60);    // 副屏左边缘
        Assert.Equal(1, BallLayout.SideAwayFromEdge(ball, wa2));
    }

    [Fact]
    public void Stop_button_stays_inside_work_area_at_corner()
    {
        var ball = new Rectangle(1860, 1020, 60, 60);   // 部分超出底部
        Assert.True(Wa.Contains(BallLayout.StopButtonRect(ball, Wa, 28, 6)));
    }

    [Fact]
    public void Slide_ends_at_target_after_120ms()
    {
        var ball = new Rectangle(1840, 960, 60, 60);
        var target = BallLayout.StopButtonRect(ball, Wa, 28, 6);
        Assert.Equal(target.Location, BallLayout.SlideAt(ball, target, 120));
        Assert.Equal(target.Location, BallLayout.SlideAt(ball, target, 500));
        var start = BallLayout.SlideAt(ball, target, 0);
        Assert.Equal(ball.Left + 30 - 14, start.X);
        var mid = BallLayout.SlideAt(ball, target, 60);
        Assert.InRange(mid.X, target.Left, start.X);
    }

    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(true, false, false, true)]
    [InlineData(false, true, false, true)]
    [InlineData(false, false, true, true)]
    public void Stop_shown_only_while_grabbing_reading_or_paused(bool g, bool r, bool p, bool show)
        => Assert.Equal(show, BallLayout.ShowStop(g, r, p));

    [Fact]
    public void Timings_match_spec()
    {
        Assert.Equal(120, BallLayout.StopSlideMs);
        Assert.Equal(800, BallLayout.HoverShowMs);
        Assert.Equal(1000, BallLayout.HoverHideMs);
        Assert.Equal(28, BallLayout.StopButtonDip);
    }
}

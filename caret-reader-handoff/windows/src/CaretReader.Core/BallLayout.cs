using System.Drawing;

namespace CaretReader.Core;

/// <summary>
/// P0：悬浮球交互的纯计算部分（不含界面，方便自动测试）。文档第 10.1 节。
///   * 单击立即生效（取消双击停止）；按下后移动超过 4px 算拖动。
///   * ■ 停止按钮：取文字 / 朗读 / 暂停时，在球“远离屏幕边缘的一侧”用 120ms 滑出，28dip。
///   * 悬停 0.8 秒展开迷你工具条，移开 1 秒收起。
/// </summary>
public static class BallLayout
{
    public const int DragThresholdPx = 4;
    public const int StopSlideMs = 120;
    public const int StopButtonDip = 28;
    public const int HoverShowMs = 800;
    public const int HoverHideMs = 1000;

    /// <summary>按下后移动超过阈值（任一方向）就算拖动，不算点击。</summary>
    public static bool IsDrag(int dx, int dy, int thresholdPx = DragThresholdPx)
        => Math.Abs(dx) > thresholdPx || Math.Abs(dy) > thresholdPx;

    /// <summary>球在工作区右半边 → 按钮放左边（-1）；否则放右边（+1）。</summary>
    public static int SideAwayFromEdge(Rectangle ball, Rectangle workArea)
    {
        int cx = ball.Left + ball.Width / 2;
        int mid = workArea.Left + workArea.Width / 2;
        return cx >= mid ? -1 : 1;
    }

    /// <summary>
    /// 停止按钮完全滑出后的位置：在球远离屏幕边缘的一侧，与球的中心水平对齐，间隔 gap。
    /// 结果始终完整落在工作区里。
    /// </summary>
    public static Rectangle StopButtonRect(Rectangle ball, Rectangle workArea, int size, int gap)
    {
        int side = SideAwayFromEdge(ball, workArea);
        int x = side < 0 ? ball.Left - gap - size : ball.Right + gap;
        int y = ball.Top + (ball.Height - size) / 2;
        x = Math.Clamp(x, workArea.Left, Math.Max(workArea.Left, workArea.Right - size));
        y = Math.Clamp(y, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - size));
        return new Rectangle(x, y, size, size);
    }

    /// <summary>滑出动画：t 毫秒时按钮的位置（从球中心滑向目标位置，缓出）。t ≥ 120ms 时就是目标位置。</summary>
    public static Point SlideAt(Rectangle ball, Rectangle target, double tMs, int durationMs = StopSlideMs)
    {
        double k = durationMs <= 0 ? 1 : Math.Clamp(tMs / durationMs, 0, 1);
        k = 1 - (1 - k) * (1 - k);   // 缓出
        int sx = ball.Left + ball.Width / 2 - target.Width / 2, sy = target.Top;
        return new Point((int)Math.Round(sx + (target.Left - sx) * k), (int)Math.Round(sy + (target.Top - sy) * k));
    }

    /// <summary>球处于这些状态时显示 ■ 停止按钮（取文字、朗读、暂停）。</summary>
    public static bool ShowStop(bool grabbing, bool reading, bool paused) => grabbing || reading || paused;
}

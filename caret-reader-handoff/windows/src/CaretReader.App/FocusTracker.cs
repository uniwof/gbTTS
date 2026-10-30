using CaretReader.Core;

namespace CaretReader.App;

/// <summary>
/// v0.6：记住“最后一个在用的别的程序窗口”（记事本 / Word / 浏览器……）。
///
/// 用系统事件 EVENT_SYSTEM_FOREGROUND（前台窗口变化）和 EVENT_OBJECT_FOCUS（焦点控件变化）实时记录，
/// 排除本程序自己的窗口（悬浮球、菜单、设置窗口）。以前（v0.5）是每 300 毫秒轮询一次，
/// 点击的那一刻可能还没记上。
///
/// 朗读时以这里记下的窗口为“目标”：
///   * 正常情况下点击悬浮球不会激活本程序，目标窗口仍在前台，直接读它；
///   * 万一本程序被激活了（打开过菜单 / 设置窗口），先把前台还给目标窗口再读。
/// 钩子用 WINEVENT_OUTOFCONTEXT，回调在界面线程的消息循环里执行，不注入任何别的程序。
/// </summary>
internal sealed class FocusTracker : IDisposable
{
    private readonly Native.WinEventProc _proc;   // 必须一直引用着，否则会被垃圾回收导致崩溃
    private IntPtr _hookFg, _hookFocus;

    /// <summary>最后一个前台的外部顶层窗口。</summary>
    public IntPtr LastWindow { get; private set; }
    /// <summary>该窗口里最后拿到焦点的子控件窗口（可能为 Zero）。</summary>
    public IntPtr LastFocus { get; private set; }
    public DateTime LastAt { get; private set; }

    /// <summary>前台窗口变化（每次切换窗口都会触发；悬浮球借这个机会重新置顶）。</summary>
    public event Action? ForegroundChanged;

    public FocusTracker()
    {
        _proc = OnEvent;
        // 跳过本进程自己的事件
        _hookFg = Native.SetWinEventHook(Native.EVENT_SYSTEM_FOREGROUND, Native.EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _proc, 0, 0,
                                         Native.WINEVENT_OUTOFCONTEXT | Native.WINEVENT_SKIPOWNPROCESS);
        _hookFocus = Native.SetWinEventHook(Native.EVENT_OBJECT_FOCUS, Native.EVENT_OBJECT_FOCUS, IntPtr.Zero, _proc, 0, 0,
                                            Native.WINEVENT_OUTOFCONTEXT | Native.WINEVENT_SKIPOWNPROCESS);
        if (_hookFg == IntPtr.Zero) Log.Write("前台窗口事件钩子安装失败，改用定时检查");
        Update(Native.GetForegroundWindow());
    }

    private void OnEvent(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        try
        {
            if (ev == Native.EVENT_SYSTEM_FOREGROUND)
            {
                Update(hwnd);
                ForegroundChanged?.Invoke();
            }
            else if (ev == Native.EVENT_OBJECT_FOCUS && hwnd != IntPtr.Zero)
            {
                var root = Native.GetAncestor(hwnd, Native.GA_ROOT);
                if (root != IntPtr.Zero && root == Native.GetForegroundWindow() && !IsOwn(root))
                {
                    LastWindow = root; LastFocus = hwnd; LastAt = DateTime.Now;
                }
            }
        }
        catch { /* 钩子回调里绝不能抛异常 */ }
    }

    /// <summary>手动补记一次（定时器备用 / 启动时）。</summary>
    public void Update(IntPtr fg)
    {
        if (fg == IntPtr.Zero || IsOwn(fg)) return;
        if (fg != LastWindow) LastFocus = IntPtr.Zero;
        LastWindow = fg;
        var (focus, _) = Native.ThreadFocusOf(fg);
        if (focus != IntPtr.Zero) LastFocus = focus;
        LastAt = DateTime.Now;
    }

    public static bool IsOwn(IntPtr h)
    {
        if (h == IntPtr.Zero) return false;
        Native.GetWindowThreadProcessId(h, out uint pid);
        return pid == (uint)Environment.ProcessId;
    }

    public void Dispose()
    {
        if (_hookFg != IntPtr.Zero) { Native.UnhookWinEvent(_hookFg); _hookFg = IntPtr.Zero; }
        if (_hookFocus != IntPtr.Zero) { Native.UnhookWinEvent(_hookFocus); _hookFocus = IntPtr.Zero; }
    }
}

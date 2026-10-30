using CaretReader.Core;
namespace CaretReader.App;
internal sealed class FocusTracker : IDisposable
{
    private readonly Native.WinEventProc _proc;
    private IntPtr _hookFg, _hookFocus;
    public IntPtr LastWindow { get; private set; }
    public IntPtr LastFocus { get; private set; }
    public DateTime LastAt { get; private set; }
    public event Action? ForegroundChanged;
    public FocusTracker()
    {
        _proc = OnEvent;
        _hookFg = Native.SetWinEventHook(Native.EVENT_SYSTEM_FOREGROUND, Native.EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _proc, 0, 0,
                                         Native.WINEVENT_OUTOFCONTEXT | Native.WINEVENT_SKIPOWNPROCESS);
        _hookFocus = Native.SetWinEventHook(Native.EVENT_OBJECT_FOCUS, Native.EVENT_OBJECT_FOCUS, IntPtr.Zero, _proc, 0, 0,
                                            Native.WINEVENT_OUTOFCONTEXT | Native.WINEVENT_SKIPOWNPROCESS);
        if (_hookFg == IntPtr.Zero) Log.Write("<zh>");
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

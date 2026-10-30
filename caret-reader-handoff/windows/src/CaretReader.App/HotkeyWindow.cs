using CaretReader.Core;

namespace CaretReader.App;

/// <summary>
/// v0.6：专门接收全局快捷键的“隐形窗口”（消息专用窗口 HWND_MESSAGE，看不见、不能激活）。
///
/// 为什么不再注册在主窗口上：全局快捷键（RegisterHotKey）是绑在某个窗口句柄上的。WinForms 在某些属性
/// 变化时会悄悄重建窗口句柄（例如切换“显示在任务栏”），旧句柄上的快捷键就跟着失效了，而程序并不知道。
/// 单独用一个永不重建的消息窗口，快捷键在任何情况下都有效：本程序在不在前台、窗口被遮住、
/// 悬浮球被拖到屏幕边上都不影响（RegisterHotKey 本来就是系统级的，不需要本程序在前台）。
/// </summary>
internal sealed class HotkeyWindow : NativeWindow, IDisposable
{
    private static readonly IntPtr HWND_MESSAGE = new(-3);

    /// <summary>收到快捷键：参数 = 快捷键编号（Hotkey.MainId / StopId）、按下的时刻（Stopwatch 时间戳）。</summary>
    public event Action<int, long>? Pressed;

    public HotkeyWindow()
    {
        CreateHandle(new CreateParams { Caption = "CaretReaderHotkeys", Parent = HWND_MESSAGE });
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Native.WM_HOTKEY)
        {
            long at = System.Diagnostics.Stopwatch.GetTimestamp();
            try { Pressed?.Invoke((int)m.WParam, at); }
            catch (Exception ex) { Log.Write("快捷键处理出错: " + ex); }
            return;
        }
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        try { Hotkey.Unregister(Handle, Hotkey.MainId); Hotkey.Unregister(Handle, Hotkey.StopId); } catch { }
        DestroyHandle();
    }
}

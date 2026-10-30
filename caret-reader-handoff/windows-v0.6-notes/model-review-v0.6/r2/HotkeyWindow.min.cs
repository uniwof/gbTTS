using CaretReader.Core;
namespace CaretReader.App;
internal sealed class HotkeyWindow : NativeWindow, IDisposable
{
    private static readonly IntPtr HWND_MESSAGE = new(-3);
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
            catch (Exception ex) { Log.Write("<zh>" + ex); }
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

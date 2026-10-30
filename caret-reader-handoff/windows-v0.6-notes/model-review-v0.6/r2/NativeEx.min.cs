using System.Runtime.InteropServices;
namespace CaretReader.App;
internal static partial class Native
{
    public const int WS_EX_TOPMOST = 0x00000008;
    public const int WS_EX_TOOLWINDOW = 0x00000080;
    public const int WS_EX_LAYERED = 0x00080000;
    public const int WS_EX_TRANSPARENT = 0x00000020;
    public const int WM_NCHITTEST = 0x0084, HTCLIENT = 1;
    public const int WM_DISPLAYCHANGE = 0x007E, WM_DPICHANGED = 0x02E0;
    public static readonly IntPtr HWND_TOPMOST = new(-1), HWND_NOTOPMOST = new(-2);
    public const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOACTIVATE = 0x0010, SWP_SHOWWINDOW = 0x0040, SWP_NOOWNERZORDER = 0x0200;
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    public const uint GA_ROOT = 2;
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr h);
    public const uint EVENT_SYSTEM_FOREGROUND = 0x0003, EVENT_OBJECT_FOCUS = 0x8005;
    public const uint WINEVENT_OUTOFCONTEXT = 0x0000, WINEVENT_SKIPOWNPROCESS = 0x0002;
    public delegate void WinEventProc(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);
    [DllImport("user32.dll")]
    public static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr hmod, WinEventProc proc, uint pid, uint tid, uint flags);
    [DllImport("user32.dll")] public static extern bool UnhookWinEvent(IntPtr hook);
    [StructLayout(LayoutKind.Sequential)]
    public struct RECTI { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    public struct GUITHREADINFO_FULL
    {
        public int cbSize, flags;
        public IntPtr hwndActive, hwndFocus, hwndCapture, hwndMenuOwner, hwndMoveSize, hwndCaret;
        public RECTI rcCaret;
    }
    [DllImport("user32.dll", EntryPoint = "GetGUIThreadInfo")]
    private static extern bool GetGUIThreadInfoFull(uint idThread, ref GUITHREADINFO_FULL info);
    public static (IntPtr focus, IntPtr caret) ThreadFocusOf(IntPtr anyWindowOfThread)
    {
        if (anyWindowOfThread == IntPtr.Zero) return (IntPtr.Zero, IntPtr.Zero);
        uint tid = GetWindowThreadProcessId(anyWindowOfThread, out _);
        var info = new GUITHREADINFO_FULL { cbSize = Marshal.SizeOf<GUITHREADINFO_FULL>() };
        return GetGUIThreadInfoFull(tid, ref info) ? (info.hwndFocus, info.hwndCaret) : (IntPtr.Zero, IntPtr.Zero);
    }
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] public struct SIZE { public int cx, cy; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }
    public const int ULW_ALPHA = 2;
    public const byte AC_SRC_OVER = 0, AC_SRC_ALPHA = 1;
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize, IntPtr hdcSrc,
                                                  ref POINT pprSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);
    [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr hDC);
    [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr hDC, IntPtr hObject);
    [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr hObject);
    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight; public short biPlanes, biBitCount; public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
    }
    [DllImport("gdi32.dll")]
    public static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER bmi, uint usage, out IntPtr bits, IntPtr hSection, uint offset);
    [DllImport("user32.dll")] private static extern uint GetGuiResources(IntPtr hProcess, uint flags);
    public static uint GdiObjects() { try { return GetGuiResources(System.Diagnostics.Process.GetCurrentProcess().Handle, 0); } catch { return 0; } }
    [DllImport("shell32.dll")] private static extern int SHQueryUserNotificationState(out int state);
    public static bool ForegroundIsFullscreen()
    {
        try
        {
            if (SHQueryUserNotificationState(out int s) == 0)
                return s is 2 /*BUSY 全屏*/ or 3 /*D3D 全屏*/ or 4 /*演示模式*/;
        }
        catch { }
        return false;
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(IntPtr proc, uint access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(IntPtr token, int cls, IntPtr info, int len, out int retLen);
    [DllImport("advapi32.dll")] private static extern IntPtr GetSidSubAuthority(IntPtr sid, uint n);
    [DllImport("advapi32.dll")] private static extern IntPtr GetSidSubAuthorityCount(IntPtr sid);
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000, TOKEN_QUERY = 0x0008;
    private const int TokenIntegrityLevel = 25;
    public static int IntegrityLevelOf(uint pid)
    {
        IntPtr proc = pid == (uint)Environment.ProcessId ? GetCurrentProcess() : OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (proc == IntPtr.Zero) return -1;
        try
        {
            if (!OpenProcessToken(proc, TOKEN_QUERY, out var tok)) return -1;
            try
            {
                GetTokenInformation(tok, TokenIntegrityLevel, IntPtr.Zero, 0, out int len);
                if (len <= 0) return -1;
                IntPtr buf = Marshal.AllocHGlobal(len);
                try
                {
                    if (!GetTokenInformation(tok, TokenIntegrityLevel, buf, len, out _)) return -1;
                    IntPtr sid = Marshal.ReadIntPtr(buf);
                    int count = Marshal.ReadByte(GetSidSubAuthorityCount(sid));
                    return Marshal.ReadInt32(GetSidSubAuthority(sid, (uint)(count - 1)));
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
            finally { CloseHandle(tok); }
        }
        finally { if (pid != (uint)Environment.ProcessId) CloseHandle(proc); }
    }
    [DllImport("user32.dll")] private static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    private const uint KEYEVENTF_KEYUP_ = 0x0002;
    public static async Task<bool> WaitModifiersReleasedAsync(int maxMs)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (AnyModifierDown() && sw.ElapsedMilliseconds < maxMs) await Task.Delay(15);
        if (!AnyModifierDown()) return true;
        keybd_event(0xE8, 0, 0, UIntPtr.Zero);
        keybd_event(0xE8, 0, KEYEVENTF_KEYUP_, UIntPtr.Zero);
        foreach (byte vk in new byte[] { 0xA2, 0xA3, 0xA4, 0xA5, 0xA0, 0xA1, 0x5B, 0x5C })
            if ((GetAsyncKeyState(vk) & 0x8000) != 0) keybd_event(vk, 0, KEYEVENTF_KEYUP_, UIntPtr.Zero);
        await Task.Delay(40);
        return !AnyModifierDown();
    }
    [DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(int pid);
    [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr h);
    public static async Task<bool> RestoreForegroundAsync(IntPtr h, int maxMs)
    {
        if (h == IntPtr.Zero || !IsWindow(h)) return false;
        if (GetForegroundWindow() == h) return true;
        if (IsIconic(h)) return false;
        SetForegroundWindow(h);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < maxMs)
        {
            if (GetForegroundWindow() == h) { await Task.Delay(60); return true; }
            await Task.Delay(20);
        }
        return GetForegroundWindow() == h;
    }
}

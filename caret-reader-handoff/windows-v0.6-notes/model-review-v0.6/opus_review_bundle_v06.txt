Project: CaretReader v0.6 — tiny offline Windows 10/11 x64 TTS reader (C# .NET 8 WinForms, self-contained, cross-compiled on Linux; sherpa-onnx; NAudio WaveOutEvent; UIA COM via Interop.UIAutomationClient). Users: beginners on low-end PCs (4 GB RAM). The user has an eye condition. All previous features must stay (global hotkeys main=start/pause/resume, stop; voice packs folder + import; speaker preview; polyphone frontend; selection reading).

USER BUG REPORT (v0.5, real Win11 25H2 PC):
 (a) select text in a document, click our app's Start button -> "这个窗口不支持朗读 / 获取不到聚焦" (focus lost);
 (b) put caret somewhere, click Start -> same;
 (c) when our app is not in front, pressing the global hotkey also fails / does not read.
v0.5 facts: main window was a WinForms bar with WS_EX_NOACTIVATE + WM_MOUSEACTIVATE->MA_NOACTIVATE, custom non-Selectable child controls (InkButton, InkDropDown opening a ContextMenuStrip); hotkeys via RegisterHotKey on the Form handle; foreground tracked by a 300 ms polling timer; grab = UIA GetFocusedElement on a thread-pool thread (1.5 s timeout) -> if focused element belongs to our process: give up; then clipboard fallback ONLY for "editor" windows (Edit/RichEdit/_WwG/Scintilla classes, notepad/winword/wordpad/wps processes, or UIA Edit/Document focusable); otherwise "这个窗口不支持朗读（焦点不在文字编辑框里）". Clipboard fallback waited up to 1.5 s for modifiers then gave up. No UIPI/elevation check.
Our current root-cause analysis (please challenge it):
 1. Many targets fail regardless of focus: PDF readers (Adobe/Sumatra/Foxit) and many browser pages expose no TextPattern caret; the editor-only clipboard rule then refused them -> exactly the reported message.
 2. When our window did get activated (menu/dropdown/dialog, or MA_NOACTIVATE leaving the previous window deactivated with GetForegroundWindow()==0 — known Win32 behaviour), the polling-timer "last external window" and the GetForegroundWindow-based target were wrong/zero -> class "" -> refused. Subsequent hotkey presses also fail if the document was left inactive.
 3. Hotkey path: UIA cold-start can exceed 1.5 s on first call (Chrome builds its a11y tree lazily) -> timeout -> refused; with Ctrl+Alt still held, simulated Ctrl+C becomes Ctrl+Alt+C; elevated targets silently block UIA and SendInput (UIPI).
 4. Hotkey registered on a Form handle can be lost if WinForms recreates the handle.
v0.6 fixes:
 - Floating ball: layered window (UpdateLayeredWindow per-pixel alpha), WS_EX_NOACTIVATE|WS_EX_TOOLWINDOW|WS_EX_LAYERED (+TopMost), MA_NOACTIVATE, NO child controls at all. Default bottom-right, draggable, position remembered, tray icon to find it again.
 - FocusTracker: SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_OBJECT_FOCUS, WINEVENT_OUTOFCONTEXT|SKIPOWNPROCESS) remembers last external top-level + focused child.
 - Target captured at trigger time (hotkey: foreground at WM_HOTKEY; click: foreground at WM_LBUTTONDOWN; if foreground is ours -> tracker's last window). If our process is foreground at grab time -> SetForegroundWindow(target) and wait until it is really foreground (<=600 ms).
 - UIA on one dedicated long-lived MTA thread with a job queue, warm-up at startup, timeout 3 s first call / 1.5 s later; if the focused element's pid != target pid -> ElementFromHandle(target focus hwnd via GetGUIThreadInfo) and search ancestors / first descendant with IsTextPatternAvailable.
 - Integrity-level check (TokenIntegrityLevel) -> explicit "run as administrator" message.
 - Clipboard: WaitModifiersReleased (1.2 s, then inject key-ups with a 0xE8 mask key to avoid Alt menu activation); ensure target is foreground before SendInput; "selection probe" (Ctrl+C only) now allowed in every non-blocked app (browsers, PDF readers); Ctrl+Shift+End caret-to-end still only for editors (browsers' Document no longer counts as editor). If no selection in a caret-less app -> message "please select text first".
 - Hotkeys registered on a dedicated message-only NativeWindow (HWND_MESSAGE).
 - Interaction: single click = start (idle) / pause (reading, immediate) / resume (paused, deferred by system double-click time); double click = stop; right click = menu (voice + preview popup, import, always-on-top toggle, appearance pet/ball, settings, about, exit); drag to move.
 - Mascot: pre-rendered 10-frame sprite sheet (160 px cells, 52 KB PNG embedded), scaled once per DPI; idle mostly static (blink every 3–7 s, page turn every 15–30 s); reading = mouth frame from audio peak level every 100 ms, redraw only when the frame changes; no timer when paused/loading/simple-ball/locked/fullscreen. "Simple ball" appearance = zero animation.
Code below is minified: comments removed, Chinese user-facing strings replaced by "<zh>" (not bugs).
Reviewer instructions: Do NOT just agree with me or with the user. Be skeptical and argue objectively for the best deliverable for a beginner user with weak eyes on old PCs. Find real bugs: Win32/WinForms pitfalls with layered + no-activate windows (ContextMenuStrip on a WS_EX_NOACTIVATE owner, MessageBox owner, mouse capture, MouseLeave, DPI change, ULW with Form.Location), threading (UI vs UIA MTA thread vs thread pool; COM objects crossing threads; the UIA queue when a call hangs), focus restoration races, SendInput/clipboard edge cases, hotkey edge cases, GDI handle leaks, timers/CPU when idle, anything that could crash. Say if a design choice is wrong and what is better. Cite code; give confidence (high/med/low); concrete fixes. Be concise. Markdown: ## Critical ## Important ## Minor ## Design objections ## Correct as-is

下面是全部相关源码（已去注释、中文字符串替换为 <zh>）。所有文件属于同一个程序，请先交叉核对再下结论。

===== NativeEx.cs =====
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

===== FocusTracker.cs =====
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

===== HotkeyWindow.cs =====
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

===== InteractionRules.cs =====
namespace CaretReader.Core;
public sealed class ClickClassifier
{
    public enum Action { PauseNow, DeferSingle, Double }
    private readonly int _doubleMs, _slop;
    private long _lastAt = long.MinValue / 2;
    private int _lastX, _lastY;
    public ClickClassifier(int doubleClickMs, int slopPx)
    {
        _doubleMs = Math.Clamp(doubleClickMs, 200, 900);
        _slop = Math.Max(4, slopPx);
    }
    public int DeferMs => Math.Min(_doubleMs, 700);
    public Action OnClick(long nowMs, int x, int y, bool reading)
    {
        bool dbl = nowMs - _lastAt <= _doubleMs && Math.Abs(x - _lastX) <= _slop && Math.Abs(y - _lastY) <= _slop;
        if (dbl)
        {
            _lastAt = long.MinValue / 2;
            return Action.Double;
        }
        _lastAt = nowMs; _lastX = x; _lastY = y;
        return reading ? Action.PauseNow : Action.DeferSingle;
    }
}
public static class ClipboardRules
{
    public const int UiaEdit = 50004, UiaDocument = 50030;
    public static readonly HashSet<string> BlockedClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "ConsoleWindowClass", "CASCADIA_HOSTING_WINDOW_CLASS", "PseudoConsoleWindow", "mintty", "VirtualConsoleClass",
        "PuTTY", "CabinetWClass", "ExploreWClass", "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd",
        "SysListView32", "SysTreeView32", "DirectUIHWND",
    };
    public static readonly HashSet<string> BlockedProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "cmd", "powershell", "pwsh", "WindowsTerminal", "OpenConsole", "conhost", "wt", "explorer", "putty", "kitty",
        "mintty", "ConEmu", "ConEmu64", "alacritty", "wezterm-gui", "Taskmgr", "wsl", "bash", "ssh", "MobaXterm", "Tabby",
        "Hyper", "WinSCP", "mstsc", "vmconnect", "VirtualBoxVM", "vmware-vmx", "TeamViewer", "AnyDesk", "SunloginClient",
    };
    public static readonly HashSet<string> EditorClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Edit", "RichEdit20A", "RichEdit20W", "RichEdit50W", "RICHEDIT60W", "RichEditD2DPT", "_WwG", "Scintilla",
    };
    public static readonly HashSet<string> EditorProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "notepad", "winword", "wordpad", "wps", "et", "notepad++", "sublime_text", "Code", "Typora", "Obsidian",
    };
    public static readonly HashSet<string> Browsers = new(StringComparer.OrdinalIgnoreCase)
    {
        "msedge", "chrome", "firefox", "360se", "360chrome", "QQBrowser", "SogouExplorer", "brave", "opera", "vivaldi",
    };
    public static (bool allowed, bool caretToEnd, string? refuse) Decide(
        string foregroundClass, string focusClass, string process, int uiaControlType, bool uiaFocusable)
    {
        if (BlockedClasses.Contains(foregroundClass) || BlockedClasses.Contains(focusClass) || BlockedProcesses.Contains(process))
            return (false, false, "<zh>");
        bool browser = Browsers.Contains(process);
        bool uiaEditor = uiaFocusable && (uiaControlType == UiaEdit || (uiaControlType == UiaDocument && !browser));
        bool editor = uiaEditor || EditorClasses.Contains(focusClass) || EditorProcesses.Contains(process);
        return (true, editor, null);
    }
}

===== CaretTextGrabber.cs =====
using System.Diagnostics;
using System.Text;
using CaretReader.Core;
using UIA = Interop.UIAutomationClient;
namespace CaretReader.App;
internal sealed class CaretTextGrabber
{
    public const int MaxChars = 100_000;
    public const int FirstChunkChars = 2000;
    public const int UiaTimeoutMs = 1500, UiaFirstTimeoutMs = 3000;
    private static UIA.IUIAutomation? _uia;
    private static bool _uiaWarm;
    public sealed record Result(string? Text, string Method, string? Detail, Func<string?>? Rest = null)
    {
        public bool Unsupported { get; init; }
        public bool Selection { get; init; }
        public bool NoCaret { get; init; }
    }
    private sealed record UiaOutcome(Result Result, int ControlType, bool Focusable);
    public sealed record Target(IntPtr Window, IntPtr Focus, string Trigger)
    {
        public string ForegroundClass { get; init; } = "";
        public string FocusClass { get; init; } = "";
        public string Process { get; init; } = "?";
        public uint Pid { get; init; }
        public override string ToString() => $"{Process} [{ForegroundClass} / {FocusClass}]（{Trigger}）";
        public static Target Describe(IntPtr window, IntPtr focus, string trigger)
        {
            if (focus == IntPtr.Zero && window != IntPtr.Zero) focus = Native.ThreadFocusOf(window).focus;
            string proc = "?";
            uint pid = 0;
            try
            {
                Native.GetWindowThreadProcessId(window, out pid);
                if (pid != 0) proc = System.Diagnostics.Process.GetProcessById((int)pid).ProcessName;
            }
            catch { }
            return new Target(window, focus, trigger)
            {
                ForegroundClass = Native.ClassNameOf(window),
                FocusClass = Native.ClassNameOf(focus != IntPtr.Zero ? focus : window),
                Process = proc,
                Pid = pid,
            };
        }
    }
    private readonly SemaphoreSlim _gate = new(1, 1);
    private static readonly bool TestLog = Environment.GetEnvironmentVariable("CARETREADER_TESTLOG") == "1";
    private static readonly System.Collections.Concurrent.BlockingCollection<Action> UiaQueue = new();
    private static volatile bool _uiaBusy;
    static CaretTextGrabber()
    {
        var t = new Thread(() =>
        {
            foreach (var job in UiaQueue.GetConsumingEnumerable())
            {
                _uiaBusy = true;
                try { job(); } catch (Exception ex) { Log.Write("<zh>" + ex.Message); }
                finally { _uiaBusy = false; }
            }
        }) { IsBackground = true, Name = "UIA" };
        t.SetApartmentState(ApartmentState.MTA);
        t.Start();
    }
    private static Task<T> RunOnUia<T>(Func<T> f)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        UiaQueue.Add(() => { try { tcs.SetResult(f()); } catch (Exception ex) { tcs.SetException(ex); } });
        return tcs.Task;
    }
    public static void WarmUp()
    {
        _ = RunOnUia(() =>
        {
            var sw = Stopwatch.StartNew();
            try { _uia ??= CreateUia(); _uia.GetRootElement(); _ = _uia.GetFocusedElement(); _uiaWarm = true; }
            catch (Exception ex) { Log.Write("<zh>" + ex.Message); }
            Log.Write("<zh>");
            return 0;
        });
    }
    public async Task<Result> GrabAsync(Target target, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            if (target.Window == IntPtr.Zero || !Native.IsWindow(target.Window))
                return new Result(null, "?", "<zh>") { Unsupported = true };
            int own = Native.IntegrityLevelOf((uint)Environment.ProcessId);
            int theirs = target.Pid == 0 ? 0 : Native.IntegrityLevelOf(target.Pid);
            if (theirs == -1) Log.Write("<zh>");
            if (own > 0 && theirs > own)
            {
                Log.Write("<zh>");
                return new Result(null, "<zh>", "<zh>" +
                                                "<zh>") { Unsupported = true };
            }
            if (FocusTracker.IsOwn(Native.GetForegroundWindow()))
            {
                bool ok = await Native.RestoreForegroundAsync(target.Window, 600);
                Log.Write("<zh>"成功" : "失败")}");
            }
            if (Native.GetForegroundWindow() != target.Window)
                Log.Write("<zh>");
            int timeout = _uiaWarm ? UiaTimeoutMs : UiaFirstTimeoutMs;
            UiaOutcome? u = null;
            if (_uiaBusy)
                Log.Write("<zh>");
            else
            {
                var uiaTask = RunOnUia(() => TryUia(target));
                var done = await Task.WhenAny(uiaTask, Task.Delay(timeout, token));
                token.ThrowIfCancellationRequested();
                if (done == uiaTask && uiaTask.Status == TaskStatus.RanToCompletion)
                {
                    u = uiaTask.Result;
                    _uiaWarm = true;
                    if (u.Result.Text != null) { TestDump(u.Result); return u.Result; }
                    Log.Write("<zh>");
                }
                else Log.Write("<zh>");
            }
            var (allowed, caretToEnd, refuse) = ClipboardPolicy(target, u);
            if (!allowed)
            {
                Log.Write("<zh>");
                return new Result(null, "<zh>", refuse) { Unsupported = true };
            }
            Log.Write("<zh>"选中 / 光标到文末" : "只探测选中"<zh>");
            var r = await TryClipboardAsync(target, caretToEnd);
            TestDump(r);
            return r;
        }
        finally
        {
            _gate.Release();
        }
    }
    private static void TestDump(Result r)
    {
        if (!TestLog) return;
        string t = r.Text ?? "(null)";
        t = t.Replace("\r", "\\r").Replace("\n", "\\n");
        Log.Write("<zh>");
    }
    private static (bool allowed, bool caretToEnd, string? refuse) ClipboardPolicy(Target t, UiaOutcome? u)
        => ClipboardRules.Decide(t.ForegroundClass, t.FocusClass, t.Process, u?.ControlType ?? 0, u?.Focusable ?? false);
    private static UiaOutcome TryUia(Target target)
    {
        int controlType = 0;
        bool focusable = false;
        try
        {
            _uia ??= CreateUia();
            UIA.IUIAutomationElement? el = null;
            string source = "<zh>";
            try { el = _uia.GetFocusedElement(); } catch { }
            uint elPid = 0;
            try { if (el != null) elPid = (uint)el.CurrentProcessId; } catch { el = null; }
            if (el == null || (target.Pid != 0 && elPid != target.Pid))
            {
                IntPtr h = target.Focus != IntPtr.Zero && Native.IsWindow(target.Focus) ? target.Focus : target.Window;
                try { el = _uia.ElementFromHandle(h); source = "<zh>"; } catch { el = null; }
                if (el == null) return new UiaOutcome(new Result(null, "UIA", "<zh>"), 0, false);
            }
            try { controlType = el.CurrentControlType; focusable = el.CurrentIsKeyboardFocusable != 0; } catch { }
            string who = "<zh>";
            var walker = _uia.RawViewWalker;
            var probe = el;
            for (int depth = 0; probe != null && depth < 4; depth++)
            {
                var r = TryTextPattern(probe, who + "<zh>");
                if (r != null) return new UiaOutcome(r, controlType, focusable);
                try { probe = walker.GetParentElement(probe); } catch { probe = null; }
            }
            if (source != "<zh>")
            {
                try
                {
                    var cond = _uia.CreatePropertyCondition(UIA.UIA_PropertyIds.UIA_IsTextPatternAvailablePropertyId, true);
                    var child = el.FindFirst(UIA.TreeScope.TreeScope_Descendants, cond);
                    if (child != null)
                    {
                        var r = TryTextPattern(child, who + "<zh>");
                        if (r != null) return new UiaOutcome(r, controlType, focusable);
                    }
                }
                catch { }
            }
            return new UiaOutcome(new Result(null, "UIA", who + "<zh>"), controlType, focusable);
        }
        catch (Exception ex)
        {
            return new UiaOutcome(new Result(null, "UIA", "<zh>" + ex.Message), controlType, focusable);
        }
    }
    private static Result? TryTextPattern(UIA.IUIAutomationElement el, string who)
    {
        if (el.GetCurrentPattern(UIA.UIA_PatternIds.UIA_TextPatternId) is not UIA.IUIAutomationTextPattern tp) return null;
        UIA.IUIAutomationTextRange? start = null;
        try
        {
            var sel = tp.GetSelection();
            if (sel != null && sel.Length > 0)
            {
                start = sel.GetElement(0);
                string? selected = ReadSelection(sel);
                if (selected != null)
                    return new Result(selected, "UIA", who + "<zh>") { Selection = true };
            }
        }
        catch { /* 有的控件不支持 GetSelection */ }
        if (start == null && el.GetCurrentPattern(UIA.UIA_PatternIds.UIA_TextPattern2Id) is UIA.IUIAutomationTextPattern2 tp2)
        {
            try { start = tp2.GetCaretRange(out _); } catch { }
        }
        if (start == null) return null;
        var range = start.Clone();
        range.MoveEndpointByRange(UIA.TextPatternRangeEndpoint.TextPatternRangeEndpoint_End, tp.DocumentRange,
                                  UIA.TextPatternRangeEndpoint.TextPatternRangeEndpoint_End);
        return ReadInTwoSteps(range, who);
    }
    private static string SafePid(UIA.IUIAutomationElement el)
    {
        try { return el.CurrentProcessId.ToString(); } catch { return "?"; }
    }
    private static string? ReadSelection(UIA.IUIAutomationTextRangeArray sel)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < sel.Length && i < 64 && sb.Length < MaxChars; i++)
        {
            string t;
            try { t = sel.GetElement(i).GetText(MaxChars - sb.Length) ?? ""; }
            catch { continue; }
            if (t.Length == 0) continue;
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(t);
        }
        string text = Clean(sb.ToString());
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }
    private static Result ReadInTwoSteps(UIA.IUIAutomationTextRange range, string who)
    {
        string first = range.GetText(FirstChunkChars) ?? "";
        if (first.Length != FirstChunkChars)
        {
            if (first.Length > MaxChars) first = first.Substring(0, MaxChars);
            return new Result(Clean(first), "UIA", who);
        }
        int cut = first.LastIndexOfAny(new[] { '。', '！', '？', '!', '?', '\n', '\r' });
        string head = cut >= 200 ? first.Substring(0, cut + 1) : first;
        string? Rest()
        {
            try
            {
                string all = range.GetText(MaxChars) ?? "";
                if (!all.StartsWith(head, StringComparison.Ordinal))
                {
                    Log.Write("<zh>");
                    return null;
                }
                return Clean(all.Substring(head.Length));
            }
            catch (Exception ex)
            {
                Log.Write("<zh>" + ex.Message);
                return null;
            }
        }
        return new Result(Clean(head), "UIA", who + "<zh>", Rest);
    }
    private static UIA.IUIAutomation CreateUia()
    {
        foreach (var clsid in new[] { "e22ad333-b25f-460c-83d0-0581107395c9", "ff48dba4-60ef-4201-aa87-54103eef594e" })
        {
            try
            {
                var t = Type.GetTypeFromCLSID(new Guid(clsid), throwOnError: true)!;
                return (UIA.IUIAutomation)Activator.CreateInstance(t)!;
            }
            catch { }
        }
        throw new InvalidOperationException("<zh>");
    }
    private static string SafeClass(UIA.IUIAutomationElement el)
    {
        try { return el.CurrentClassName ?? "?"; } catch { return "?"; }
    }
    private static readonly HashSet<string> LineCopyClasses = new(StringComparer.OrdinalIgnoreCase) { "Scintilla" };
    private static readonly HashSet<string> LineCopyProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "notepad++", "sublime_text", "Code", "devenv", "idea64", "pycharm64", "webstorm64", "rider64", "atom", "zed",
    };
    private static async Task<Result> TryClipboardAsync(Target target, bool caretToEnd)
    {
        if (!await Native.WaitModifiersReleasedAsync(1200))
            return new Result(null, "<zh>", "<zh>");
        if (Native.GetForegroundWindow() != target.Window && !await Native.RestoreForegroundAsync(target.Window, 600))
            return new Result(null, "<zh>", "<zh>") { Unsupported = true };
        var sw = Stopwatch.StartNew();
        var (backupOk, backup) = BackupClipboard();
        if (!backupOk && Native.CountClipboardFormats() > 0)
            return new Result(null, "<zh>", "<zh>");
        uint seq0 = Native.GetClipboardSequenceNumber();
        if (!LineCopyClasses.Contains(target.FocusClass) && !LineCopyProcesses.Contains(target.Process))
        {
            Native.SendChord(Native.VK_CONTROL, Native.VK_C);
            sw.Restart();
            while (Native.GetClipboardSequenceNumber() == seq0 && sw.ElapsedMilliseconds < 600)
                await Task.Delay(20);
            if (Native.GetClipboardSequenceNumber() != seq0)
            {
                await Task.Delay(30);
                string? sel = ReadClipboardText();
                if (!string.IsNullOrWhiteSpace(sel))
                {
                    await RestoreWithLateCheckAsync(backup);
                    if (sel.Length > MaxChars) sel = sel.Substring(0, MaxChars);
                    return new Result(Clean(sel), "<zh>", "<zh>") { Selection = true };
                }
            }
            seq0 = Native.GetClipboardSequenceNumber();
        }
        if (!caretToEnd)
        {
            await RestoreWithLateCheckAsync(backup);
            return new Result(null, "<zh>", "<zh>") { NoCaret = true };
        }
        Native.SendChord(Native.VK_CONTROL, Native.VK_SHIFT, Native.VK_END);
        await Task.Delay(80);
        Native.SendChord(Native.VK_CONTROL, Native.VK_C);
        sw.Restart();
        while (Native.GetClipboardSequenceNumber() == seq0 && sw.ElapsedMilliseconds < 2000)
            await Task.Delay(20);
        bool copied = Native.GetClipboardSequenceNumber() != seq0;
        if (!copied) Log.Write("<zh>");
        string? text = null;
        if (copied)
        {
            await Task.Delay(30);
            text = ReadClipboardText();
        }
        if (!(copied && text == ""))
            Native.SendChord(Native.VK_LEFT);
        await RestoreWithLateCheckAsync(backup);
        if (!copied) return new Result(null, "<zh>", "<zh>");
        text ??= "";
        if (text.Length > MaxChars) text = text.Substring(0, MaxChars);
        return new Result(Clean(text), "<zh>", null);
    }
    private static async Task RestoreWithLateCheckAsync(DataObject? backup)
    {
        RestoreClipboard(backup);
        uint seqRestored = Native.GetClipboardSequenceNumber();
        await Task.Delay(300);
        if (Native.GetClipboardSequenceNumber() != seqRestored)
        {
            Log.Write("<zh>");
            RestoreClipboard(backup);
        }
    }
    private static string? ReadClipboardText()
    {
        for (int i = 0; i < 5; i++)
        {
            try { return Clipboard.ContainsText() ? Clipboard.GetText(TextDataFormat.UnicodeText) : ""; }
            catch { Thread.Sleep(30); }
        }
        return null;
    }
    private static readonly string[] BackupFormats =
    {
        DataFormats.UnicodeText, DataFormats.Text, DataFormats.Rtf, DataFormats.Html, DataFormats.FileDrop, DataFormats.Bitmap,
    };
    private const int MaxBackupChars = 8_000_000;
    private static (bool ok, DataObject? data) BackupClipboard()
    {
        try
        {
            var src = Clipboard.GetDataObject();
            if (src == null) return (true, null);
            var copy = new DataObject();
            int n = 0;
            var skipped = new List<string>();
            foreach (var fmt in src.GetFormats(false))
            {
                if (Array.IndexOf(BackupFormats, fmt) < 0) { skipped.Add(fmt); continue; }
                try
                {
                    var v = src.GetData(fmt, false);
                    if (v is string str && str.Length > MaxBackupChars) { skipped.Add(fmt + "<zh>"); continue; }
                    if (v != null) { copy.SetData(fmt, false, v); n++; }
                }
                catch { skipped.Add(fmt + "<zh>"); }
            }
            if (skipped.Count > 0) Log.Write("<zh>" + string.Join(", ", skipped.Take(12)));
            return (true, n > 0 ? copy : null);
        }
        catch (Exception ex)
        {
            Log.Write("<zh>" + ex.Message);
            return (false, null);
        }
    }
    private static void RestoreClipboard(DataObject? backup)
    {
        try
        {
            if (backup != null)
            {
                MarkPrivate(backup);
                Clipboard.SetDataObject(backup, copy: true, retryTimes: 5, retryDelay: 50);
            }
            else Clipboard.Clear();
        }
        catch (Exception ex)
        {
            Log.Write("<zh>" + ex.Message);
        }
    }
    private static void MarkPrivate(DataObject d)
    {
        static MemoryStream Dword(int v) => new(BitConverter.GetBytes(v));
        d.SetData("ExcludeClipboardContentFromMonitorProcessing", false, Dword(0));
        d.SetData("CanIncludeInClipboardHistory", false, Dword(0));
        d.SetData("CanUploadToCloudClipboard", false, Dword(0));
    }
    private static string Clean(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char c in s)
        {
            if (c is '\v' or '\a' or '\f') sb.Append('\n');
            else if (c == '\uFFFC') continue;
            else if (char.IsControl(c) && c is not '\n' and not '\r' and not '\t') continue;
            else sb.Append(c);
        }
        return sb.ToString();
    }
}

===== MainForm.cs =====
using System.Diagnostics;
using System.Reflection;
using CaretReader.App.Ui;
using CaretReader.Core;
namespace CaretReader.App;
internal sealed class MainForm : Form
{
    private enum State { NoModel, Loading, Idle, Grabbing, Reading, Paused }
    private State _state = State.NoModel;
    private readonly string _appDir = AppContext.BaseDirectory;
    private readonly string _settingsPath;
    private AppSettings _settings;
    private List<VoicePack> _packs = new();
    private string _edition = "";
    private string _lastScanProblems = "";
    private readonly TtsEngine _engine = new();
    private readonly SamplePlayer _player = new();
    private bool _importing;
    private bool _readingSelection;
    private readonly CaretTextGrabber _grabber = new();
    private Hotkey _hotkey = Hotkey.Default;
    private Hotkey _stopHotkey = Hotkey.DefaultStop;
    private bool _mainRegistered, _stopRegistered;
    private readonly HotkeyWindow _hotkeys = new();
    private CancellationTokenSource? _readCts;
    private Task _readTask = Task.CompletedTask;
    private int _readSession;
    private NAudioSink? _sink;
    private bool _pauseRequested;
    private FocusTracker? _tracker;
    private readonly System.Windows.Forms.Timer _anim = new();
    private readonly System.Windows.Forms.Timer _clickTimer = new();
    private readonly System.Windows.Forms.Timer _hoverTimer = new() { Interval = 700 };
    private readonly System.Windows.Forms.Timer _progressTimer = new() { Interval = 80 };
    private readonly Stopwatch _loadClock = new();
    private double _loadExpectSeconds = 3;
    private double? _progress;
    private readonly BallRenderer _render = new();
    private readonly Bubble _bubble = new();
    private readonly InkDropDown _voiceMenu = new();
    private readonly ContextMenuStrip _menu = new() { Renderer = new InkMenuRenderer(), ShowImageMargin = false, ShowCheckMargin = false };
    private NotifyIcon? _tray;
    private string _petFrame = "sleep";
    private int _lastDrawnFrame = -1;
    private string _lastDrawnKey = "";
    private long _nextIdleEvent;
    private int _idleSeq;
    private int _idleStep;
    private readonly Random _rnd = new();
    private bool _hover;
    private bool _sessionLocked;
    private float _mouthLevel;
    private bool _leftDown, _dragging;
    private Point _downScreen, _downWindow;
    private bool _singlePending;
    private CaretTextGrabber.Target? _clickTarget;
    public MainForm()
    {
        _settingsPath = AppSettings.ResolvePath(_appDir);
        _settings = AppSettings.Load(_settingsPath);
        Text = "<zh>";
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        ShowInTaskbar = false;
        TopMost = _settings.TopMost;
        Font = Ink.UiFont;
        try { Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!); } catch { }
        _voiceMenu.SelectedIndexChanged += async (_, _) => await OnVoiceChangedAsync();
        _voiceMenu.Opening += (_, _) => RefreshVoices(tidy: false);
        _voiceMenu.SpeakerClicked += PreviewVoice;
        _voiceMenu.HasSample = o => o is VoicePack p && p.SamplePath != null;
        _voiceMenu.IsPlaying = o => o is VoicePack p && _player.PlayingId == p.Id;
        _voiceMenu.Tooltip = o => o is VoicePack p ? (string.IsNullOrWhiteSpace(p.Description) ? "" : p.Description + "\n") + "<zh>" : null;
        _voiceMenu.MenuClosed += (_, _) => ReturnFocus();
        _player.StateChanged += () => _voiceMenu.RefreshSpeakers();
        _player.Failed += msg => FlashStatus(msg);
        _hotkeys.Pressed += OnHotkey;
        _anim.Tick += (_, _) => AnimTick();
        _clickTimer.Tick += (_, _) => { _clickTimer.Stop(); if (_singlePending) { _singlePending = false; OnSingleClick(); } };
        _hoverTimer.Tick += (_, _) => { _hoverTimer.Stop(); if (_hover && !_leftDown) ShowStatusBubble(); };
        _progressTimer.Tick += (_, _) => UpdateLoadProgress();
        _menu.Font = Ink.UiFont;
        _menu.Closed += (_, _) => { ReturnFocus(); Redraw(); };
        Microsoft.Win32.SystemEvents.SessionSwitch += OnSessionSwitch;
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
    }
    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW | Native.WS_EX_LAYERED;
            cp.ExStyle &= ~Native.WS_EX_APPWINDOW;
            return cp;
        }
    }
    protected override bool ShowWithoutActivation => true;
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyBallSize();
        RestoreBallPosition();
    }
    private int BallDiameter => Ink.S(this, 60);
    private void ApplyBallSize()
    {
        _render.EnsureSize(BallDiameter);
        Size = new Size(_render.WindowSize, _render.WindowSize);
        _lastDrawnKey = "";
    }
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Native.WM_MOUSEACTIVATE)
        {
            m.Result = (IntPtr)Native.MA_NOACTIVATE;
            return;
        }
        if (m.Msg == Native.WM_DPICHANGED)
        {
            base.WndProc(ref m);
            BeginInvoke(() => { ApplyBallSize(); KeepOnScreen(); Redraw(); });
            return;
        }
        base.WndProc(ref m);
    }
    private void RestoreBallPosition()
    {
        var wa = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 720);
        int margin = Ink.S(this, 24);
        var pos = new Point(wa.Right - Width - margin, wa.Bottom - Height - margin);
        if (_settings.BallX is int x && _settings.BallY is int y)
        {
            var r = new Rectangle(x, y, Width, Height);
            if (Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(Rectangle.Inflate(r, -Width / 3, -Height / 3)))) pos = new Point(x, y);
        }
        Location = pos;
        KeepOnScreen();
    }
    private void KeepOnScreen()
    {
        var wa = Screen.FromRectangle(Bounds).WorkingArea;
        int x = Math.Clamp(Left, wa.Left, Math.Max(wa.Left, wa.Right - Width));
        int y = Math.Clamp(Top, wa.Top, Math.Max(wa.Top, wa.Bottom - Height));
        if (x != Left || y != Top) Location = new Point(x, y);
    }
    private void OnDisplayChanged(object? sender, EventArgs e)
    {
        if (IsDisposed) return;
        BeginInvoke(() => { ApplyBallSize(); KeepOnScreen(); Redraw(); });
    }
    private void OnSessionSwitch(object? sender, Microsoft.Win32.SessionSwitchEventArgs e)
    {
        _sessionLocked = e.Reason is Microsoft.Win32.SessionSwitchReason.SessionLock or Microsoft.Win32.SessionSwitchReason.ConsoleDisconnect
                                     or Microsoft.Win32.SessionSwitchReason.RemoteDisconnect;
        if (!_sessionLocked && !IsDisposed) BeginInvoke(() => ScheduleAnim());
    }
    private void ReassertTopMost()
    {
        if (!_settings.TopMost || !IsHandleCreated) return;
        Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }
    private BallRenderer.Look CurrentLook => _settings.Appearance == "ball" || !_render.HasPet ? BallRenderer.Look.Ball : BallRenderer.Look.Pet;
    private void Redraw()
    {
        if (!IsHandleCreated || IsDisposed) return;
        string frameName = _state switch
        {
            State.NoModel or State.Loading => "sleep",
            State.Grabbing => "look",
            State.Paused => "look",
            State.Reading => _petFrame.StartsWith("talk") ? _petFrame : "talk0",
            _ => _petFrame is "idle" or "blink" or "page1" or "page2" or "page3" ? _petFrame : "idle",
        };
        string glyph = _state switch
        {
            State.NoModel or State.Loading => "sleep",
            State.Reading or State.Grabbing => "wave",
            _ => "caret",
        };
        string? badge = _state == State.Paused ? "pause" : null;
        int frame = _render.FrameOf(frameName);
        string key = $"{CurrentLook}|{frame}|{glyph}|{badge}|{_hover}|{(_progress is double p ? (int)(p * 100) : -1)}|{Left},{Top}";
        if (key == _lastDrawnKey) return;
        _lastDrawnKey = key;
        _lastDrawnFrame = frame;
        _render.Present(Handle, Location, CurrentLook, frame, _progress, badge, glyph, _hover);
    }
    protected override void OnMove(EventArgs e)
    {
        base.OnMove(e);
        Redraw();
    }
    private void ScheduleAnim()
    {
        _anim.Stop();
        if (CurrentLook != BallRenderer.Look.Pet || _sessionLocked || !Visible) { Redraw(); return; }
        if (_state == State.Reading) { _anim.Interval = 100; _anim.Start(); return; }
        if (_state == State.Idle)
        {
            long now = Environment.TickCount64;
            if (_nextIdleEvent <= now) _nextIdleEvent = now + _rnd.Next(3000, 7000);
            _anim.Interval = (int)Math.Clamp(_nextIdleEvent - now, 50, 10_000);
            _anim.Start();
        }
        Redraw();
    }
    private void AnimTick()
    {
        if (_sessionLocked || Native.ForegroundIsFullscreen()) { _petFrame = _state == State.Reading ? "talk0" : "idle"; Redraw(); _anim.Interval = 1000; return; }
        if (_state == State.Reading)
        {
            float lv = _sink?.Level ?? 0f;
            _mouthLevel = Math.Max(lv, _mouthLevel * 0.55f);
            _petFrame = _mouthLevel < 0.06f ? "talk0" : _mouthLevel < 0.22f ? "talk1" : "talk2";
            Redraw();
            return;
        }
        if (_state != State.Idle) { _anim.Stop(); return; }
        if (_idleSeq == 0)
        {
            _idleSeq = _rnd.Next(5) == 0 ? 2 : 1;
            _idleStep = 0;
        }
        string[] seq = _idleSeq == 1 ? new[] { "blink", "idle" } : new[] { "page1", "page2", "page3", "idle" };
        _petFrame = seq[_idleStep];
        Redraw();
        _idleStep++;
        if (_idleStep >= seq.Length)
        {
            _idleSeq = 0;
            _nextIdleEvent = Environment.TickCount64 + (_rnd.Next(4) == 0 ? _rnd.Next(15000, 30000) : _rnd.Next(3000, 7000));
            ScheduleAnim();
        }
        else _anim.Interval = _idleSeq == 1 ? 150 : 140;
    }
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        _hoverTimer.Stop();
        if (e.Button == MouseButtons.Left)
        {
            _leftDown = true;
            _dragging = false;
            _downScreen = Cursor.Position;
            _downWindow = Location;
            _clickTarget = CurrentTarget("<zh>");
            Capture = true;
        }
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_hover) { _hover = true; Redraw(); _hoverTimer.Start(); }
        if (!_leftDown) return;
        var p = Cursor.Position;
        var drag = SystemInformation.DragSize;
        if (!_dragging && (Math.Abs(p.X - _downScreen.X) > Math.Max(4, drag.Width) || Math.Abs(p.Y - _downScreen.Y) > Math.Max(4, drag.Height)))
        {
            _dragging = true;
            _bubble.HideNow();
            _singlePending = false; _clickTimer.Stop();
        }
        if (_dragging) Location = new Point(_downWindow.X + p.X - _downScreen.X, _downWindow.Y + p.Y - _downScreen.Y);
    }
    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button == MouseButtons.Left && _leftDown)
        {
            _leftDown = false;
            Capture = false;
            if (_dragging)
            {
                _dragging = false;
                KeepOnScreen();
                _settings.BallX = Left; _settings.BallY = Top;
                try { _settings.Save(_settingsPath); } catch { }
                return;
            }
            OnLeftClick(Cursor.Position);
        }
        else if (e.Button == MouseButtons.Right)
        {
            ShowMenu(Cursor.Position);
        }
    }
    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = false;
        _hoverTimer.Stop();
        if (_state is not State.Loading && !_importing && _bubble.Showing && _bubbleIsHover) _bubble.HideNow();
        Redraw();
    }
    private void OnLeftClick(Point pt)
    {
        var dbl = SystemInformation.DoubleClickSize;
        _clicks ??= new ClickClassifier(SystemInformation.DoubleClickTime, Math.Max(dbl.Width, dbl.Height));
        switch (_clicks.OnClick(Environment.TickCount64, pt.X, pt.Y, reading: _state == State.Reading))
        {
            case ClickClassifier.Action.Double:
                _singlePending = false; _clickTimer.Stop();
                OnDoubleClickBall();
                return;
            case ClickClassifier.Action.PauseNow:
                Log.Write("<zh>");
                PauseReading();
                return;
            default:
                _singlePending = true;
                _clickTimer.Interval = _clicks.DeferMs;
                _clickTimer.Stop(); _clickTimer.Start();
                return;
        }
    }
    private ClickClassifier? _clicks;
    private void OnSingleClick()
    {
        Log.Write("<zh>");
        var target = _clickTarget ?? CurrentTarget("<zh>");
        _ = OnMainActionAsync(Stopwatch.GetTimestamp(), target);
    }
    private void OnDoubleClickBall()
    {
        Log.Write("<zh>");
        if (_state is State.Grabbing or State.Reading or State.Paused) { StopReading(); FlashStatus("<zh>"); }
        else FlashStatus("<zh>");
    }
    private void ShowMenu(Point screen)
    {
        _bubble.HideNow();
        BuildMenu();
        _menu.Show(screen);
    }
    private void BuildMenu()
    {
        _menu.Items.Clear();
        int w = Ink.S(this, 260), h = Ink.S(this, 34);
        ToolStripMenuItem Item(string text, Action? onClick, bool enabled = true, bool check = false)
        {
            var it = new ToolStripMenuItem((check ? "■  " : "    ") + text) { AutoSize = false, Width = w, Height = h, Enabled = enabled, TextAlign = ContentAlignment.MiddleLeft };
            if (onClick != null) it.Click += (_, _) => onClick();
            return it;
        }
        var head = Item(StatusText(), null, enabled: false);
        _menu.Items.Add(head);
        _menu.Items.Add(new ToolStripSeparator());
        string act = _state switch { State.Reading or State.Grabbing => "<zh>", State.Paused => "<zh>", _ => "<zh>" };
        bool canAct = _state is State.Idle or State.Reading or State.Paused or State.Grabbing;
        _menu.Items.Add(Item(act, () => _ = OnMainActionAsync(Stopwatch.GetTimestamp(), CurrentTarget("<zh>")), canAct));
        _menu.Items.Add(Item("<zh>", () => { StopReading(); FlashStatus("<zh>"); }, _state is State.Reading or State.Paused or State.Grabbing));
        _menu.Items.Add(new ToolStripSeparator());
        string voiceName = (_voiceMenu.SelectedItem as VoicePack)?.Name ?? "<zh>";
        _menu.Items.Add(Item("<zh>", () => BeginInvoke(() => _voiceMenu.ShowAt(new Point(Left, Top))),
                             _state != State.Loading && !_importing && _packs.Count > 0));
        _menu.Items.Add(Item("<zh>", ImportVoices, _state != State.Loading && !_importing));
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(Item("<zh>", ToggleTopMost, check: _settings.TopMost));
        _menu.Items.Add(Item("<zh>", () => SetAppearance("pet"), _render.HasPet, check: CurrentLook == BallRenderer.Look.Pet));
        _menu.Items.Add(Item("<zh>", () => SetAppearance("ball"), check: CurrentLook == BallRenderer.Look.Ball));
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(Item("<zh>", OpenSettings, _state != State.Loading && !_importing));
        _menu.Items.Add(Item("<zh>", () => { using var f = new AboutForm(); f.ShowDialog(this); ReturnFocus(); }));
        _menu.Items.Add(Item("<zh>", Close));
    }
    private void ToggleTopMost()
    {
        _settings.TopMost = !_settings.TopMost;
        TopMost = _settings.TopMost;
        if (!_settings.TopMost) Native.SetWindowPos(Handle, Native.HWND_NOTOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        else ReassertTopMost();
        try { _settings.Save(_settingsPath); } catch { }
        FlashStatus(_settings.TopMost ? "<zh>" : "<zh>");
    }
    private void SetAppearance(string look)
    {
        _settings.Appearance = look;
        try { _settings.Save(_settingsPath); } catch { }
        _lastDrawnKey = "";
        ScheduleAnim();
        Log.Write("<zh>" + look);
    }
    private void SetupTray()
    {
        try
        {
            _tray = new NotifyIcon { Icon = Icon ?? SystemIcons.Application, Text = "<zh>", Visible = true };
            _tray.MouseUp += (_, e) =>
            {
                if (e.Button == MouseButtons.Right) ShowMenu(Cursor.Position);
                else if (e.Button == MouseButtons.Left)
                {
                    if (!Visible) Show();
                    _settings.BallX = null; _settings.BallY = null;
                    RestoreBallPosition();
                    ReassertTopMost();
                    FlashStatus("<zh>");
                }
            };
        }
        catch (Exception ex) { Log.Write("<zh>" + ex.Message); }
    }
    private bool _bubbleIsHover;
    private void ShowStatusBubble()
    {
        if (_bubble.Showing && !_bubbleIsHover) return;
        _bubbleIsHover = true;
        _bubble.ShowNear(Bounds, StatusText() + "<zh>", 6000);
    }
    private void FlashStatus(string text, int ms = 4000)
    {
        if (IsDisposed) return;
        _bubbleIsHover = false;
        _bubble.ShowNear(Bounds, text, ms);
    }
    private string StopHint => _stopRegistered ? "<zh>" : "<zh>";
    private string StatusText()
    {
        string ed = string.IsNullOrEmpty(_edition) ? "" : _edition + " · ";
        return _state switch
        {
            State.NoModel => _packs.Count == 0 ? "<zh>" : "<zh>",
            State.Loading => _progressText ?? "<zh>",
            State.Idle => _mainRegistered ? "<zh>" : "<zh>",
            State.Grabbing => "<zh>",
            State.Reading => $"{(_readingSelection ? "朗读选中" : "朗读中"<zh>",
            State.Paused => "<zh>",
            _ => "",
        };
    }
    private string? _progressText;
    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        ReassertTopMost();
        _tracker = new FocusTracker();
        _tracker.ForegroundChanged += ReassertTopMost;
        CaretTextGrabber.WarmUp();
        SetupTray();
        Redraw();
        if (CrashGuard.PreviousCrashStage is string lastStage &&
            !lastStage.Contains("<zh>") && !lastStage.Contains("<zh>") && !lastStage.Contains("<zh>"))
        {
            ShowError("<zh>" + lastStage + "）。\n\n" +
                      "<zh>" +
                      "<zh>");
        }
        CrashGuard.Mark("<zh>");
        try { _edition = File.ReadAllText(Path.Combine(_appDir, "edition.txt")).Trim(); } catch { _edition = ""; }
        if (_edition.Length > 12) _edition = _edition.Substring(0, 12);
        if (_edition != "") { Text = "<zh>" + _edition; if (_tray != null) _tray.Text = Text; }
        VoiceImporter.CleanupTemp(_appDir);
        RefreshVoices(tidy: true);
        CrashGuard.Mark("<zh>");
        ApplyHotkeys(_settings.Hotkey, _settings.StopHotkey, showErrors: true);
        if (_packs.Count == 0)
        {
            SetState(State.NoModel);
            ShowError("<zh>" +
                      "<zh>");
            return;
        }
        await LoadSelectedVoiceAsync();
        if (_state == State.Idle) FlashStatus("<zh>", 5000);
    }
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        base.OnFormClosing(e);
        _anim.Stop(); _progressTimer.Stop(); _clickTimer.Stop(); _hoverTimer.Stop();
        Microsoft.Win32.SystemEvents.SessionSwitch -= OnSessionSwitch;
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
        UnregisterHotkeys();
        StopReading();
        _player.Stop();
        _bubble.HideNow();
        if (_tray != null) { _tray.Visible = false; _tray.Dispose(); _tray = null; }
        _tracker?.Dispose();
        try
        {
            _settings.BallX = Left; _settings.BallY = Top;
            _settings.Save(_settingsPath);
        }
        catch (Exception ex) { Log.Write("<zh>" + ex.Message); }
        CrashGuard.Mark("<zh>");
        try { _readTask.Wait(3000); } catch { }
        try { Task.Run(_engine.Dispose).Wait(3000); } catch { }
        Log.Write("<zh>");
        CrashGuard.CleanExit();
    }
    private void OnHotkey(int id, long pressedAt)
    {
        if (id == Hotkey.StopId) { Log.Write("<zh>"); OnStopKey(); return; }
        var target = CurrentTarget("<zh>");
        Log.Write("<zh>");
        _ = OnMainActionAsync(pressedAt, target);
    }
    private CaretTextGrabber.Target CurrentTarget(string trigger)
    {
        var fg = Native.GetForegroundWindow();
        if (fg != IntPtr.Zero && !FocusTracker.IsOwn(fg))
        {
            _tracker?.Update(fg);
            return CaretTextGrabber.Target.Describe(fg, Native.ThreadFocusOf(fg).focus, trigger);
        }
        var last = _tracker?.LastWindow ?? IntPtr.Zero;
        return CaretTextGrabber.Target.Describe(last, _tracker?.LastFocus ?? IntPtr.Zero, trigger + (fg == IntPtr.Zero ? "<zh>" : "<zh>"));
    }
    private void UnregisterHotkeys()
    {
        if (_mainRegistered) Hotkey.Unregister(_hotkeys.Handle, Hotkey.MainId);
        if (_stopRegistered) Hotkey.Unregister(_hotkeys.Handle, Hotkey.StopId);
        _mainRegistered = _stopRegistered = false;
    }
    private static Hotkey Sanitize(string text, Hotkey fallback, string name, List<string> errors)
    {
        var hk = Hotkey.Parse(text);
        if (hk == null)
        {
            Log.Write("<zh>");
            errors.Add("<zh>");
            return fallback;
        }
        if (hk.Value.Problem is string why)
        {
            Log.Write("<zh>");
            errors.Add("<zh>");
            return fallback;
        }
        return hk.Value;
    }
    private bool ApplyHotkeys(string mainText, string stopText, bool showErrors)
    {
        var errors = new List<string>();
        var main = Sanitize(mainText, Hotkey.Default, "<zh>", errors);
        var stop = Sanitize(stopText, Hotkey.DefaultStop, "<zh>", errors);
        UnregisterHotkeys();
        _mainRegistered = TryRegister(main, Hotkey.MainId, _hotkey, "<zh>", errors, out _hotkey);
        if (stop.Equals(_hotkey))
        {
            Log.Write("<zh>");
            errors.Add("<zh>");
            _stopRegistered = false;
        }
        else
        {
            var oldStop = _stopHotkey.Equals(_hotkey) ? stop : _stopHotkey;
            _stopRegistered = TryRegister(stop, Hotkey.StopId, oldStop, "<zh>", errors, out _stopHotkey);
        }
        if (errors.Count > 0 && showErrors) ShowError(string.Join("\n\n", errors));
        return errors.Count == 0;
    }
    private bool TryRegister(Hotkey wanted, int id, Hotkey previous, string name, List<string> errors, out Hotkey result)
    {
        if (wanted.Register(_hotkeys.Handle, id))
        {
            Log.Write("<zh>");
            result = wanted;
            return true;
        }
        Log.Write("<zh>");
        if (!wanted.Equals(previous) && previous.Register(_hotkeys.Handle, id))
        {
            errors.Add("<zh>");
            result = previous;
            return true;
        }
        errors.Add("<zh>");
        result = previous;
        return false;
    }
    private async Task OnMainActionAsync(long pressedAt, CaretTextGrabber.Target target)
    {
        switch (_state)
        {
            case State.Idle:
                await StartReadingAsync(pressedAt, target);
                return;
            case State.Reading:
                PauseReading();
                return;
            case State.Paused:
                ResumeReading();
                return;
            case State.Grabbing:
                _pauseRequested = !_pauseRequested;
                FlashStatus(_pauseRequested ? "<zh>" : "<zh>", 2000);
                return;
            case State.Loading:
                FlashStatus("<zh>");
                return;
            case State.NoModel:
                FlashStatus(_packs.Count == 0 ? "<zh>" : "<zh>");
                return;
        }
    }
    private void OnStopKey()
    {
        if (_state is State.Grabbing or State.Reading or State.Paused)
        {
            StopReading();
            FlashStatus("<zh>");
        }
    }
    private void PauseReading()
    {
        _sink?.Pause();
        SetState(State.Paused);
        Log.Write("<zh>");
    }
    private void ResumeReading()
    {
        _player.Stop();
        _sink?.Resume();
        SetState(State.Reading);
        Log.Write("<zh>");
    }
    private async Task StartReadingAsync(long pressedAt, CaretTextGrabber.Target target)
    {
        int session = ++_readSession;
        var cts = new CancellationTokenSource();
        _readCts = cts;
        _pauseRequested = false;
        _readingSelection = false;
        _player.Stop();
        SetState(State.Grabbing);
        CaretTextGrabber.Result grab;
        try { grab = await _grabber.GrabAsync(target, cts.Token); }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            Log.Write("<zh>" + ex);
            grab = new CaretTextGrabber.Result(null, "?", ex.Message);
        }
        if (session != _readSession || cts.IsCancellationRequested) return;
        double grabSeconds = Stopwatch.GetElapsedTime(pressedAt).TotalSeconds;
        Log.Write("<zh>"（选中的文字）" : ""<zh>"（其余后台读取）" : "")}，" +
                  "<zh>");
        var pauses = _settings.Pauses;
        string? text = grab.Text;
        var first = await Task.Run(() => TextSplitter.Split(text, pauses, firstChunk: true, CaretTextGrabber.MaxChars));
        if (session != _readSession || cts.IsCancellationRequested) return;
        if (first.Count == 0 && grab.Rest == null)
        {
            SetState(State.Idle);
            FlashStatus(grab.Unsupported ? (grab.Detail ?? "<zh>")
                      : grab.NoCaret ? "<zh>"
                      : grab.Text == null ? "<zh>"
                      : grab.Selection ? "<zh>" : "<zh>", 6000);
            return;
        }
        var queue = new ClauseQueue();
        queue.Add(first);
        if (grab.Rest == null) queue.Complete();
        var voice = (VoicePack)_voiceMenu.SelectedItem!;
        float speed = (float)Math.Clamp(voice.Speed * _settings.Speed, 0.5, 2.0);
        _readingSelection = grab.Selection;
        var sink = new NAudioSink();
        _sink = sink;
        if (_pauseRequested) { sink.Pause(); SetState(State.Paused); }
        else SetState(State.Reading);
        _pauseRequested = false;
        CrashGuard.Mark("<zh>");
        var task = SpeechPipeline.RunAsync(queue, _engine, voice.Sid, speed, sink, cts.Token,
                                           onClause: null, hotkeyTimestamp: pressedAt, gain: (float)voice.Gain)
            .ContinueWith(t =>
            {
                var st = t.Result;
                Log.Write("<zh>" +
                          "<zh>"无"}");
                sink.Dispose();
                return st;
            }, TaskScheduler.Default);
        _readTask = task;
        if (grab.Rest != null) _ = FetchRestAsync(grab.Rest, queue, pauses, cts.Token);
        PipelineStats? stats = null;
        try { stats = await task; }
        catch (Exception ex) { Log.Write("<zh>" + ex); }
        finally
        {
            if (session == _readSession)
            {
                _sink = null;
                _readingSelection = false;
                SetState(State.Idle);
            }
        }
        if (session == _readSession && stats?.Error != null) FlashStatus("<zh>" + stats.Error, 6000);
    }
    private static async Task FetchRestAsync(Func<string?> rest, ClauseQueue queue, PauseSettings pauses, CancellationToken token)
    {
        try
        {
            string? more = await Task.Run(rest);
            if (token.IsCancellationRequested || string.IsNullOrEmpty(more)) return;
            var clauses = await Task.Run(() => TextSplitter.Split(more, pauses, firstChunk: false, CaretTextGrabber.MaxChars));
            queue.Add(clauses);
            Log.Write("<zh>");
        }
        catch (Exception ex) { Log.Write("<zh>" + ex.Message); }
        finally { queue.Complete(); }
    }
    private void StopReading()
    {
        _readSession++;
        _readCts?.Cancel();
        _readCts = null;
        _sink = null;
        _pauseRequested = false;
        if (_state is State.Reading or State.Grabbing or State.Paused) SetState(State.Idle);
    }
    private async Task OnVoiceChangedAsync()
    {
        if (_voiceMenu.SelectedItem is not VoicePack v) return;
        _settings.VoiceId = v.Id;
        try { _settings.Save(_settingsPath); } catch { }
        StopReading();
        await LoadSelectedVoiceAsync();
    }
    private async Task LoadSelectedVoiceAsync(bool forceReload = false)
    {
        if (_voiceMenu.SelectedItem is not VoicePack voice) return;
        if (!forceReload && _engine.IsLoaded(voice, _settings.NumThreads))
        {
            Log.Write("<zh>");
            SetState(State.Idle);
            FlashStatus("<zh>", 2500);
            return;
        }
        if (!_readTask.IsCompleted) { _progressText = "<zh>"; SetState(State.Loading); }
        try { await _readTask; } catch { }
        _progressText = "<zh>";
        SetState(State.Loading);
        CrashGuard.Mark("<zh>");
        _loadExpectSeconds = Math.Max(1, voice.ApproxLoadSeconds);
        _loadClock.Restart();
        _progress = 0;
        _progressTimer.Start();
        try
        {
            int threads = _settings.NumThreads;
            await Task.Run(() =>
            {
                if (forceReload) _engine.Unload();
                _engine.Load(voice, threads, _appDir);
            });
            CrashGuard.Mark("<zh>");
            Log.Write("<zh>");
            _progressTimer.Stop();
            _progress = 1; Redraw();
            await Task.Delay(300);
            _progress = null;
            _bubble.HideNow();
            SetState(State.Idle);
        }
        catch (Exception ex)
        {
            _progressTimer.Stop();
            _progress = null;
            _bubble.HideNow();
            Log.Write("<zh>" + ex);
            SetState(State.NoModel);
            ShowError(FriendlyLoadError(ex));
        }
    }
    private static string FriendlyLoadError(Exception ex)
    {
        var e = ex;
        while (e is TypeInitializationException or TargetInvocationException or AggregateException && e.InnerException != null)
            e = e.InnerException!;
        return e switch
        {
            DllNotFoundException =>
                "<zh>" +
                "<zh>",
            BadImageFormatException => "<zh>",
            EntryPointNotFoundException => "<zh>",
            OutOfMemoryException => "<zh>",
            _ => e.Message,
        };
    }
    private void UpdateLoadProgress()
    {
        double t = _loadClock.Elapsed.TotalSeconds;
        double p = Math.Min(0.95, 1 - Math.Exp(-t / (_loadExpectSeconds * 0.6)));
        _progress = p;
        if (_voiceMenu.SelectedItem is VoicePack v) _progressText = "<zh>";
        Redraw();
        if (_hover || _bubble.Showing) { _bubbleIsHover = false; _bubble.ShowNear(Bounds, _progressText ?? "", 0); }
    }
    private void RefreshVoices(bool tidy)
    {
        VoiceLibrary.ScanResult scan;
        try { scan = VoiceLibrary.Scan(_appDir, tidy); }
        catch (Exception ex) { Log.Write("<zh>" + ex); return; }
        string problems = string.Join("\n", scan.Problems);
        if (problems != _lastScanProblems)
        {
            foreach (var pr in scan.Problems) Log.Write("<zh>" + pr);
            _lastScanProblems = problems;
        }
        var current = _voiceMenu.SelectedItem as VoicePack;
        string? wantId = current?.Id ?? _settings.VoiceId;
        var list = scan.Packs;
        if (current != null && !list.Any(p => p.Id.Equals(current.Id, StringComparison.OrdinalIgnoreCase)) && _engine.LoadedPackId == current.Id)
            list.Add(current);
        bool same = list.Count == _packs.Count && list.Zip(_packs).All(t => t.First.Id == t.Second.Id && t.First.Name == t.Second.Name
                                                                        && t.First.SamplePath == t.Second.SamplePath);
        if (same && !tidy) return;
        _packs = list;
        _voiceMenu.SetItems(_packs);
        int idx = wantId == null ? -1 : _packs.FindIndex(p => p.Id.Equals(wantId, StringComparison.OrdinalIgnoreCase));
        _voiceMenu.SelectedIndex = idx >= 0 ? idx : (_packs.Count > 0 ? 0 : -1);
        Log.Write("<zh>"，"<zh>");
    }
    private void PreviewVoice(int index)
    {
        if (index < 0 || index >= _packs.Count) return;
        var p = _packs[index];
        if (p.SamplePath == null) { FlashStatus("<zh>"); return; }
        if (_player.PlayingId != p.Id && _state == State.Reading) PauseReading();
        Log.Write("<zh>");
        _player.Toggle(p.Id, p.SamplePath);
    }
    private async void ImportVoices()
    {
        if (_importing || _state == State.Loading) return;
        StopReading();
        _player.Stop();
        string? folder;
        using (var dlg = new FolderBrowserDialog
        {
            Description = "<zh>",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false,
        })
        {
            var r = dlg.ShowDialog(this);
            folder = r == DialogResult.OK ? dlg.SelectedPath : null;
        }
        ReturnFocus();
        if (string.IsNullOrEmpty(folder)) return;
        var before = _state;
        _importing = true;
        _progress = 0;
        FlashStatus("<zh>", 0);
        var progress = new Progress<(string text, double frac)>(x =>
        {
            if (!_importing || IsDisposed) return;
            _progress = x.frac;
            Redraw();
            _bubble.ShowNear(Bounds, $"{x.text} {(int)(x.frac * 100)}%", 0);
        });
        IProgress<(string, double)> rep = progress;
        VoiceImporter.Result result;
        try
        {
            Log.Write("<zh>" + folder);
            result = await Task.Run(() => VoiceImporter.Import(folder, _appDir, (t, f) => rep.Report((t, f)), CancellationToken.None));
        }
        catch (Exception ex)
        {
            Log.Write("<zh>" + ex);
            result = new VoiceImporter.Result(new() { new VoiceImporter.Item(folder, null, null, ex.Message) });
        }
        finally
        {
            _importing = false;
            _progress = null;
            _bubble.HideNow();
            Redraw();
        }
        RefreshVoices(tidy: false);
        SetState(before == State.NoModel && _engine.LoadedModelId != null ? State.Idle : before);
        var ok = result.Imported.ToList();
        var skipped = result.Items.Where(i => i.Skipped).ToList();
        var bad = result.Items.Where(i => i.Error != null).ToList();
        var msg = new List<string>();
        if (ok.Count > 0) msg.Add("<zh>" + string.Join("\n  ", ok.Select(i => i.Name)) + "<zh>");
        if (skipped.Count > 0) msg.Add("<zh>" + string.Join("\n  ", skipped.Select(i => i.Name)));
        if (bad.Count > 0) msg.Add("<zh>" + string.Join("\n  ", bad.Select(i => (i.Name != null ? i.Name + "：" : "") + i.Error)));
        if (msg.Count > 0) ShowError(string.Join("\n\n", msg), info: bad.Count == 0);
        if (before == State.NoModel && _engine.LoadedModelId == null && _packs.Count > 0)
        {
            var first = ok.Select(i => _packs.FindIndex(p => p.Id == i.Id)).FirstOrDefault(i => i >= 0, 0);
            _voiceMenu.SelectedIndex = first;
            await OnVoiceChangedAsync();
        }
    }
    private async void OpenSettings()
    {
        StopReading();
        _player.Stop();
        UnregisterHotkeys();
        using var dlg = new SettingsForm(_settings.Clone(), _settingsPath);
        var result = dlg.ShowDialog(this);
        ReturnFocus();
        if (result != DialogResult.OK)
        {
            ApplyHotkeys(_settings.Hotkey, _settings.StopHotkey, showErrors: true);
            return;
        }
        var n = dlg.Result;
        bool threadsChanged = n.NumThreads != _settings.NumThreads;
        n.WindowX = _settings.WindowX; n.WindowY = _settings.WindowY; n.VoiceId = _settings.VoiceId;
        n.BallX = Left; n.BallY = Top; n.Appearance = _settings.Appearance;
        _settings = n;
        TopMost = _settings.TopMost;
        if (!_settings.TopMost) Native.SetWindowPos(Handle, Native.HWND_NOTOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        else ReassertTopMost();
        ApplyHotkeys(_settings.Hotkey, _settings.StopHotkey, showErrors: true);
        _settings.Hotkey = _hotkey.ToString();
        _settings.StopHotkey = _stopHotkey.ToString();
        try { _settings.Save(_settingsPath); }
        catch (Exception ex) { ShowError("<zh>" + ex.Message); }
        Log.Write("<zh>");
        if (threadsChanged && _state != State.NoModel)
        {
            StopReading();
            await LoadSelectedVoiceAsync(forceReload: true);
        }
    }
    private void SetState(State s)
    {
        var old = _state;
        _state = s;
        if (s != State.Reading) _mouthLevel = 0;
        if (old != s) { _petFrame = s == State.Reading ? "talk0" : "idle"; _idleSeq = 0; }
        if (_tray != null) { string t = "<zh>" + StatusText(); _tray.Text = t.Length > 63 ? t.Substring(0, 63) : t; }
        ScheduleAnim();
    }
    private void ShowError(string message, bool info = false)
    {
        _bubble.HideNow();
        MessageBox.Show(this, message, "<zh>", MessageBoxButtons.OK, info ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        ReturnFocus();
    }
    private void ReturnFocus()
    {
        var h = _tracker?.LastWindow ?? IntPtr.Zero;
        if (h != IntPtr.Zero && Native.IsWindow(h) && FocusTracker.IsOwn(Native.GetForegroundWindow()))
            Native.SetForegroundWindow(h);
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _anim.Dispose(); _clickTimer.Dispose(); _hoverTimer.Dispose(); _progressTimer.Dispose();
            _readCts?.Dispose();
            _player.Dispose();
            _render.Dispose();
            _bubble.Dispose();
            _menu.Dispose();
            _voiceMenu.Dispose();
            _hotkeys.Dispose();
        }
        base.Dispose(disposing);
    }
}

===== BallRenderer.cs =====
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Reflection;
using System.Text.Json;
using CaretReader.Core;
namespace CaretReader.App.Ui;
internal sealed class BallRenderer : IDisposable
{
    public enum Look { Pet, Ball }
    private readonly Bitmap? _sheet;
    private readonly int _cell;
    private readonly Dictionary<string, int> _index = new(StringComparer.OrdinalIgnoreCase);
    private Bitmap[]? _scaled;
    private int _size;
    private int _pad;
    private IntPtr _memDc, _dib, _oldBmp, _bits;
    private Bitmap? _canvas;
    private int _canvasW;
    public int WindowSize => _size + _pad * 2;
    public BallRenderer()
    {
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            using var png = asm.GetManifestResourceStream("sprites.png");
            using var json = asm.GetManifestResourceStream("sprites.json");
            if (png != null && json != null)
            {
                using var tmp = new Bitmap(png);
                _sheet = new Bitmap(tmp.Width, tmp.Height, PixelFormat.Format32bppPArgb);
                using (var g = Graphics.FromImage(_sheet)) g.DrawImage(tmp, 0, 0, tmp.Width, tmp.Height);
                using var doc = JsonDocument.Parse(json);
                _cell = doc.RootElement.GetProperty("cell").GetInt32();
                foreach (var p in doc.RootElement.GetProperty("frames").EnumerateObject()) _index[p.Name] = p.Value.GetInt32();
            }
        }
        catch (Exception ex) { Log.Write("<zh>" + ex.Message); _sheet = null; }
    }
    public bool HasPet => _sheet != null;
    public void EnsureSize(int diameter)
    {
        int pad = Math.Max(3, diameter / 14);
        if (diameter == _size && pad == _pad && _canvas != null) return;
        _size = diameter; _pad = pad;
        if (_scaled != null) foreach (var b in _scaled) b.Dispose();
        _scaled = null;
        if (_sheet != null)
        {
            int n = _sheet.Width / _cell;
            _scaled = new Bitmap[n];
            for (int i = 0; i < n; i++)
            {
                var b = new Bitmap(diameter, diameter, PixelFormat.Format32bppPArgb);
                using var g = Graphics.FromImage(b);
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.CompositingMode = CompositingMode.SourceCopy;
                g.DrawImage(_sheet, new Rectangle(0, 0, diameter, diameter), new Rectangle(i * _cell, 0, _cell, _cell), GraphicsUnit.Pixel);
                _scaled[i] = b;
            }
        }
        CreateCanvas(WindowSize);
    }
    public int FrameOf(string name) => _index.TryGetValue(name, out int i) ? i : 0;
    private void CreateCanvas(int w)
    {
        FreeCanvas();
        var bmi = new Native.BITMAPINFOHEADER
        {
            biSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.BITMAPINFOHEADER>(),
            biWidth = w, biHeight = -w,
            biPlanes = 1, biBitCount = 32,
        };
        IntPtr screen = Native.GetDC(IntPtr.Zero);
        _memDc = Native.CreateCompatibleDC(screen);
        _dib = Native.CreateDIBSection(screen, ref bmi, 0, out _bits, IntPtr.Zero, 0);
        Native.ReleaseDC(IntPtr.Zero, screen);
        _oldBmp = Native.SelectObject(_memDc, _dib);
        _canvas = new Bitmap(w, w, w * 4, PixelFormat.Format32bppPArgb, _bits);
        _canvasW = w;
    }
    private void FreeCanvas()
    {
        _canvas?.Dispose(); _canvas = null;
        if (_memDc != IntPtr.Zero)
        {
            Native.SelectObject(_memDc, _oldBmp);
            Native.DeleteObject(_dib);
            Native.DeleteDC(_memDc);
            _memDc = _dib = IntPtr.Zero;
        }
    }
    public void Present(IntPtr hwnd, Point screenPos, Look look, int frame, double? progress, string? badge, string glyph, bool hover)
    {
        if (_canvas == null) return;
        using (var g = Graphics.FromImage(_canvas))
        {
            g.CompositingMode = CompositingMode.SourceCopy;
            g.Clear(Color.Transparent);
            g.CompositingMode = CompositingMode.SourceOver;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var ball = new Rectangle(_pad, _pad, _size, _size);
            if (look == Look.Pet && _scaled != null && frame >= 0 && frame < _scaled.Length)
                g.DrawImageUnscaled(_scaled[frame], _pad, _pad);
            else
                DrawSimpleBall(g, ball, glyph);
            if (hover)
            {
                using var hp = new Pen(Color.FromArgb(150, 128, 128, 128), Math.Max(1.5f, _pad * 0.45f));
                g.DrawEllipse(hp, _pad * 0.45f, _pad * 0.45f, _canvasW - _pad * 0.9f - 1, _canvasW - _pad * 0.9f - 1);
            }
            if (progress is double p)
            {
                float w = Math.Max(2f, _pad * 0.8f);
                var rr = new RectangleF(w / 2, w / 2, _canvasW - w - 1, _canvasW - w - 1);
                using var bg = new Pen(Color.FromArgb(200, 232, 232, 232), w);
                using var fg = new Pen(Ink.InkBlack, w) { StartCap = LineCap.Round, EndCap = LineCap.Round };
                g.DrawEllipse(bg, rr);
                g.DrawArc(fg, rr, -90, (float)(360 * Math.Clamp(p, 0.02, 1)));
            }
            if (badge == "pause") DrawPauseBadge(g);
        }
        var size = new Native.SIZE { cx = _canvasW, cy = _canvasW };
        var dst = new Native.POINT { X = screenPos.X, Y = screenPos.Y };
        var src = new Native.POINT();
        var blend = new Native.BLENDFUNCTION { BlendOp = Native.AC_SRC_OVER, SourceConstantAlpha = 255, AlphaFormat = Native.AC_SRC_ALPHA };
        IntPtr screen = Native.GetDC(IntPtr.Zero);
        Native.UpdateLayeredWindow(hwnd, screen, ref dst, ref size, _memDc, ref src, 0, ref blend, Native.ULW_ALPHA);
        Native.ReleaseDC(IntPtr.Zero, screen);
    }
    private void DrawSimpleBall(Graphics g, Rectangle r, string glyph)
    {
        using (var fill = new SolidBrush(Ink.PaperPure)) g.FillEllipse(fill, r);
        float lw = Math.Max(1.5f, r.Width * 0.034f);
        using (var pen = new Pen(Ink.InkBlack, lw)) g.DrawEllipse(pen, r.X + lw / 2, r.Y + lw / 2, r.Width - lw, r.Height - lw);
        float cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f, u = r.Width / 100f;
        using var ink = new SolidBrush(Ink.InkBlack);
        using var stroke = new Pen(Ink.InkBlack, 7 * u) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        switch (glyph)
        {
            case "wave":   // 朗读中：三道声波竖条
                foreach (var (dx, h) in new[] { (-16f, 18f), (0f, 34f), (16f, 24f) })
                    g.DrawLine(stroke, cx + dx * u, cy - h * u / 2, cx + dx * u, cy + h * u / 2);
                break;
            case "sleep":  // 加载中 / 没有音色：横线
                g.DrawLine(stroke, cx - 18 * u, cy, cx + 18 * u, cy);
                break;
            default:
                g.DrawLine(stroke, cx, cy - 22 * u, cx, cy + 22 * u);
                g.DrawLine(stroke, cx - 10 * u, cy - 22 * u, cx + 10 * u, cy - 22 * u);
                g.DrawLine(stroke, cx - 10 * u, cy + 22 * u, cx + 10 * u, cy + 22 * u);
                break;
        }
    }
    private void DrawPauseBadge(Graphics g)
    {
        float d = _size * 0.36f;
        float x = _pad + _size - d * 0.92f, y = _pad + _size - d * 0.92f;
        using (var b = new SolidBrush(Ink.InkBlack)) g.FillEllipse(b, x, y, d, d);
        using (var w = new Pen(Color.White, Math.Max(1.2f, d * 0.06f))) g.DrawEllipse(w, x, y, d, d);
        float bw = d * 0.13f, bh = d * 0.42f, cy = y + d / 2;
        using var wb = new SolidBrush(Color.White);
        g.FillRectangle(wb, x + d * 0.33f - bw / 2, cy - bh / 2, bw, bh);
        g.FillRectangle(wb, x + d * 0.67f - bw / 2, cy - bh / 2, bw, bh);
    }
    public void Dispose()
    {
        FreeCanvas();
        if (_scaled != null) foreach (var b in _scaled) b.Dispose();
        _sheet?.Dispose();
    }
}

===== Bubble.cs =====
namespace CaretReader.App.Ui;
internal sealed class Bubble : Form
{
    private string _text = "";
    private readonly System.Windows.Forms.Timer _hide = new();
    private static Font? _font;
    private static Font BubbleFont => _font ??= new Font(Ink.FontFamilyName, 11f, FontStyle.Regular, GraphicsUnit.Point);
    public Bubble()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Color.White;
        DoubleBuffered = true;
        TopMost = true;
        _hide.Tick += (_, _) => { _hide.Stop(); HideNow(); };
    }
    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW | Native.WS_EX_TOPMOST | Native.WS_EX_TRANSPARENT;
            return cp;
        }
    }
    protected override bool ShowWithoutActivation => true;
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Native.WM_MOUSEACTIVATE) { m.Result = (IntPtr)Native.MA_NOACTIVATE; return; }
        base.WndProc(ref m);
    }
    public bool Showing => Visible && _text.Length > 0;
    public string CurrentText => _text;
    public void ShowNear(Rectangle anchor, string text, int ms)
    {
        _text = text;
        int pad = Ink.S(this, 12);
        int maxW = Ink.S(this, 380);
        var sz = TextRenderer.MeasureText(text, BubbleFont, new Size(maxW - pad * 2, 2000), TextFormatFlags.WordBreak);
        var size = new Size(Math.Min(maxW, sz.Width + pad * 2 + 2), sz.Height + pad * 2);
        var wa = Screen.FromRectangle(anchor).WorkingArea;
        int gap = Ink.S(this, 8);
        var pos = new Point(anchor.Left - gap - size.Width, anchor.Bottom - size.Height);
        if (pos.X < wa.Left) pos.X = anchor.Right + gap;
        if (pos.X + size.Width > wa.Right) { pos.X = Math.Max(wa.Left, anchor.Right - size.Width); pos.Y = anchor.Top - gap - size.Height; }
        if (pos.Y < wa.Top) pos.Y = anchor.Bottom + gap;
        pos.Y = Math.Clamp(pos.Y, wa.Top, Math.Max(wa.Top, wa.Bottom - size.Height));
        pos.X = Math.Clamp(pos.X, wa.Left, Math.Max(wa.Left, wa.Right - size.Width));
        bool sizeChanged = Size != size;
        Bounds = new Rectangle(pos, size);
        if (sizeChanged) Ink.RoundWindow(this);
        if (!Visible) Native.SetWindowPos(Handle, Native.HWND_TOPMOST, pos.X, pos.Y, size.Width, size.Height,
                                          Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
        Visible = true;
        Invalidate();
        _hide.Stop();
        if (ms > 0) { _hide.Interval = ms; _hide.Start(); }
    }
    public void HideNow()
    {
        _hide.Stop();
        _text = "";
        if (Visible) Hide();
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var r = ClientRectangle;
        g.Clear(Color.White);
        Ink.Border(g, r, Ink.InkBlack);
        int pad = Ink.S(this, 12);
        TextRenderer.DrawText(g, _text, BubbleFont, Rectangle.Inflate(r, -pad, -pad), Ink.InkBlack,
                              TextFormatFlags.WordBreak | TextFormatFlags.Left | TextFormatFlags.Top);
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) _hide.Dispose();
        base.Dispose(disposing);
    }
}

===== NAudioSink.cs =====
using System.Diagnostics;
using CaretReader.Core;
using NAudio.Wave;
namespace CaretReader.App;
internal sealed class NAudioSink : IAudioSink, IDisposable
{
    private readonly object _gate = new();
    private WaveOutEvent? _out;
    private BufferedWaveProvider? _buf;
    private readonly Stopwatch _emptySince = new();
    private bool _stopped;
    private bool _paused;
    public bool IsPaused { get { lock (_gate) return _paused; } }
    private LevelMeter? _meter;
    public float Level => IsPaused ? 0f : (_meter?.Level ?? 0f);
    private sealed class LevelMeter : IWaveProvider
    {
        private readonly IWaveProvider _src;
        private volatile float _level;
        public LevelMeter(IWaveProvider src) => _src = src;
        public WaveFormat WaveFormat => _src.WaveFormat;
        public float Level => _level;
        public int Read(byte[] buffer, int offset, int count)
        {
            int n = _src.Read(buffer, offset, count);
            int peak = 0;
            for (int i = offset; i + 1 < offset + n; i += 2)
            {
                int v = Math.Abs((int)(short)(buffer[i] | (buffer[i + 1] << 8)));
                if (v > peak) peak = v;
            }
            _level = peak / 32768f;
            return n;
        }
    }
    public void Begin(int sampleRate)
    {
        lock (_gate)
        {
            if (_stopped) return;
            _buf = new BufferedWaveProvider(new WaveFormat(sampleRate, 16, 1))
            {
                BufferDuration = TimeSpan.FromSeconds(90),
                DiscardOnBufferOverflow = false,
                ReadFully = true,
            };
            var output = new WaveOutEvent { DesiredLatency = 200, NumberOfBuffers = 3 };
            try
            {
                _meter = new LevelMeter(_buf);
                output.Init(_meter);
                if (!_paused) output.Play();
            }
            catch (Exception ex)
            {
                try { output.Dispose(); } catch { }
                _buf = null;
                throw new InvalidOperationException(FriendlyAudioError(ex), ex);
            }
            _out = output;
        }
    }
    private static string FriendlyAudioError(Exception ex)
    {
        string m = ex.Message ?? "";
        if (m.Contains("NoDriver", StringComparison.OrdinalIgnoreCase) || m.Contains("BadDeviceId", StringComparison.OrdinalIgnoreCase))
            return "<zh>";
        if (m.Contains("Allocated", StringComparison.OrdinalIgnoreCase))
            return "<zh>";
        return "<zh>" + m;
    }
    public void Pause()
    {
        lock (_gate)
        {
            if (_stopped || _paused) return;
            _paused = true;
            try { _out?.Pause(); } catch { }
        }
    }
    public void Resume()
    {
        lock (_gate)
        {
            if (_stopped || !_paused) return;
            _paused = false;
            _emptySince.Reset();
            try { _out?.Play(); } catch { }
        }
    }
    public void Write(float[] samples)
    {
        var bytes = new byte[samples.Length * 2];
        for (int i = 0; i < samples.Length; i++)
        {
            float f = samples[i];
            if (f > 1f) f = 1f; else if (f < -1f) f = -1f;
            short v = (short)(f * 32767f);
            bytes[2 * i] = (byte)v;
            bytes[2 * i + 1] = (byte)(v >> 8);
        }
        lock (_gate)
        {
            if (_stopped || _buf == null) return;
            _buf.AddSamples(bytes, 0, bytes.Length);
            _emptySince.Reset();
        }
    }
    public double BufferedSeconds
    {
        get { lock (_gate) return _buf?.BufferedDuration.TotalSeconds ?? 0; }
    }
    public bool IsDrained
    {
        get
        {
            lock (_gate)
            {
                if (_stopped || _buf == null) return true;
                if (_paused) { _emptySince.Reset(); return false; }
                if (_buf.BufferedBytes > 0) { _emptySince.Reset(); return false; }
                if (!_emptySince.IsRunning) _emptySince.Start();
                return _emptySince.ElapsedMilliseconds > 400;
            }
        }
    }
    public void Stop()
    {
        lock (_gate)
        {
            _stopped = true;
            try { _out?.Stop(); } catch { }
            try { _out?.Dispose(); } catch { }
            _out = null;
            _buf?.ClearBuffer();
            _buf = null;
        }
    }
    public void Dispose() => Stop();
}

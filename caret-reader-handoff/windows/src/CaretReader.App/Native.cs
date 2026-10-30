using System.Runtime.InteropServices;

namespace CaretReader.App;

/// <summary>
/// 调用 Windows 系统 API 的声明（P/Invoke）。
/// 初学者可以把这里理解为“系统函数的电话簿”：其他文件通过 Native.xxx(...) 来调用它们。
/// </summary>
internal static partial class Native
{
    // ---------- 全局快捷键 ----------
    public const int WM_HOTKEY = 0x0312;
    public const uint MOD_ALT = 0x0001, MOD_CONTROL = 0x0002, MOD_SHIFT = 0x0004, MOD_WIN = 0x0008;
    public const uint MOD_NOREPEAT = 0x4000; // 按住不放时不重复触发

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    // ---------- 窗口 ----------
    public const int WS_EX_NOACTIVATE = 0x08000000; // 点击窗口时不抢走焦点（光标留在记事本里）
    public const int WS_EX_APPWINDOW = 0x00040000;  // 在任务栏显示
    public const int WM_NCLBUTTONDOWN = 0xA1;
    public const int HTCAPTION = 0x2;
    public const int WM_MOUSEACTIVATE = 0x0021;
    public const int MA_NOACTIVATE = 3;

    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] public static extern bool ReleaseCapture();
    [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    // ---------- 键盘状态 / 模拟按键 ----------
    [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vKey);

    public const ushort VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12 /* Alt */,
                        VK_LWIN = 0x5B, VK_RWIN = 0x5C, VK_END = 0x23, VK_LEFT = 0x25, VK_C = 0x43;

    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_EXTENDEDKEY = 0x0001, KEYEVENTF_KEYUP = 0x0002;

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT { public uint type; public InputUnion U; }

    // 联合体必须包含最大的成员 (MOUSEINPUT)，否则 64 位下结构体大小不对，SendInput 会失败
    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    private static INPUT Key(ushort vk, bool up)
    {
        // 方向键、End 等属于“扩展键”，要带上 EXTENDEDKEY 标志，否则可能被当成小键盘按键
        bool extended = vk is VK_END or VK_LEFT;
        uint flags = (up ? KEYEVENTF_KEYUP : 0) | (extended ? KEYEVENTF_EXTENDEDKEY : 0);
        return new INPUT { type = INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT { wVk = vk, dwFlags = flags } } };
    }

    /// <summary>
    /// 模拟按下组合键，例如 SendChord(VK_CONTROL, VK_SHIFT, VK_END) = Ctrl+Shift+End。
    /// 按顺序按下，再倒序松开。
    /// </summary>
    public static bool SendChord(params ushort[] keys)
    {
        var list = new List<INPUT>();
        foreach (var k in keys) list.Add(Key(k, false));
        for (int i = keys.Length - 1; i >= 0; i--) list.Add(Key(keys[i], true));
        uint sent = SendInput((uint)list.Count, list.ToArray(), Marshal.SizeOf<INPUT>());
        return sent == list.Count;
    }

    /// <summary>当前是否还有 Ctrl / Alt / Shift / Win 被按着。</summary>
    public static bool AnyModifierDown()
    {
        foreach (int vk in new[] { VK_SHIFT, VK_CONTROL, VK_MENU, VK_LWIN, VK_RWIN })
            if ((GetAsyncKeyState(vk) & 0x8000) != 0) return true;
        return false;
    }

    // ---------- 剪贴板 ----------
    /// <summary>剪贴板每变化一次，这个数字就加 1。用来判断“复制”是否真的发生了。</summary>
    [DllImport("user32.dll")] public static extern uint GetClipboardSequenceNumber();
    /// <summary>剪贴板里现在有几种格式（0 = 空）。</summary>
    [DllImport("user32.dll")] public static extern int CountClipboardFormats();

    // ---------- 判断目标窗口是什么程序（剪贴板备用方案只在“文字编辑器”里使用） ----------
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder name, int max);

    public static string ClassNameOf(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero) return "";
        var sb = new System.Text.StringBuilder(256);
        return GetClassName(hWnd, sb, sb.Capacity) > 0 ? sb.ToString() : "";
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct GUITHREADINFO
    {
        public int cbSize, flags;
        public IntPtr hwndActive, hwndFocus, hwndCapture, hwndMenuOwner, hwndMoveSize, hwndCaret;
        public RECT rcCaret;
    }

    [DllImport("user32.dll")] private static extern bool GetGUIThreadInfo(uint idThread, ref GUITHREADINFO info);

    /// <summary>前台程序里真正拥有键盘焦点的子窗口（拿不到时返回前台窗口本身）。</summary>
    public static IntPtr FocusedWindowOf(IntPtr foreground)
    {
        if (foreground == IntPtr.Zero) return IntPtr.Zero;
        uint tid = GetWindowThreadProcessId(foreground, out _);
        var info = new GUITHREADINFO { cbSize = Marshal.SizeOf<GUITHREADINFO>() };
        return GetGUIThreadInfo(tid, ref info) && info.hwndFocus != IntPtr.Zero ? info.hwndFocus : foreground;
    }
}

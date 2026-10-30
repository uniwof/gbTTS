namespace CaretReader.Core;

/// <summary>
/// v0.6：悬浮球的单击 / 双击判定（纯逻辑，不依赖界面，方便在测试程序里验证）。
///
///   * 两次点击间隔 ≤ 系统双击时间、位置相差不超过双击范围 → 第二次算“双击”（停止）。
///   * 正在朗读时的单击立即生效（暂停），不用等双击时间——暂停不会丢东西，紧接着的第二下照样算双击 → 停止。
///   * 其它状态的单击要等双击时间过去、确认没有第二下，才执行（避免双击“停止”时先误开始读一下）。
/// </summary>
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

    /// <summary>延迟单击要等多久（毫秒）。</summary>
    public int DeferMs => Math.Min(_doubleMs, 700);

    /// <summary>鼠标左键松开（不是拖动）时调用。reading = 当前是否正在出声朗读。</summary>
    public Action OnClick(long nowMs, int x, int y, bool reading)
    {
        bool dbl = nowMs - _lastAt <= _doubleMs && Math.Abs(x - _lastX) <= _slop && Math.Abs(y - _lastY) <= _slop;
        if (dbl)
        {
            _lastAt = long.MinValue / 2;   // 三连击不会被当成两次双击
            return Action.Double;
        }
        _lastAt = nowMs; _lastX = x; _lastY = y;
        return reading ? Action.PauseNow : Action.DeferSingle;
    }
}

/// <summary>
/// v0.6：剪贴板备用方案能在哪些窗口里用（纯逻辑）。
///   allowed    = 能不能发 Ctrl+C（“选中探测”）——终端、资源管理器、远程桌面等一律不行；
///   caretToEnd = 能不能再发 Ctrl+Shift+End（“光标到文末”）——只限文字编辑窗口，浏览器网页不算。
/// </summary>
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
            return (false, false, "这个窗口不支持朗读（控制台 / 终端 / 文件夹窗口不能用复制方式）");
        bool browser = Browsers.Contains(process);
        bool uiaEditor = uiaFocusable && (uiaControlType == UiaEdit || (uiaControlType == UiaDocument && !browser));
        bool editor = uiaEditor || EditorClasses.Contains(focusClass) || EditorProcesses.Contains(process);
        return (true, editor, null);
    }
}

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

using System.Diagnostics;
using System.Text;
using CaretReader.Core;
using UIA = Interop.UIAutomationClient;

namespace CaretReader.App;

/// <summary>
/// 获取要朗读的文字：
///   * 文档里有选中的文字 → 只取选中部分（Result.Selection = true，读完就停）；
///   * 没有选中 → “当前光标位置到文末”的文字。
///
/// v0.6 重写了“读哪个窗口”的部分（修复“点软件 / 按快捷键后提示这个窗口不支持朗读”）：
///   * 目标窗口由调用方在触发那一刻确定（Target）：快捷键 = 当时的前台窗口；
///     点击悬浮球 = FocusTracker 记下的最后一个外部窗口（悬浮球不抢焦点，通常它仍在前台）。
///   * 本程序万一在前台（打开过菜单 / 设置窗口），先把前台还给目标窗口，等它真的回到前台再读。
///   * UIA 的“焦点控件”如果不属于目标程序（例如属于本程序），改为从目标窗口的焦点子窗口
///     （GetGUIThreadInfo）找 TextPattern，不再直接放弃。
///   * UIA 调用放在一条专用的后台 MTA 线程上，带超时；启动时预热（第一次调用最慢）。
///   * 目标程序以管理员身份运行而本程序不是：Windows 不允许普通程序读它的文字、给它发按键（UIPI），
///     直接提示“请以管理员身份运行光标朗读”，不再显示含糊的“不支持”。
///   * 剪贴板备用方案：先等 Ctrl/Alt 松开；“选中探测”（只按 Ctrl+C）扩大到浏览器、PDF 阅读器等所有
///     非终端程序；“光标到文末”（Ctrl+Shift+End）仍然只在文字编辑窗口里用。
///
/// 方法 1：UI Automation（首选，完全不碰剪贴板、不改动文档）
/// 方法 2：剪贴板（备用）——见下方 TryClipboardAsync 的说明。
/// 同一时间只允许一次取字（_gate）。日志只记方法、长度、窗口类名，不记正文
/// （测试时设置环境变量 CARETREADER_TESTLOG=1 才会额外记录前 30 个字）。
/// </summary>
internal sealed class CaretTextGrabber
{
    /// <summary>最多取多少个字符（防止超大文档一次读进太多）。</summary>
    public const int MaxChars = 100_000;   // 约等于朗读 5 小时以上

    /// <summary>UIA 第一步读多少字（读完就开始朗读，剩下的后台再读）。</summary>
    public const int FirstChunkChars = 2000;

    /// <summary>UIA 最多等多久（毫秒）。第一次调用（还没预热好）放宽到 3 秒。</summary>
    public const int UiaTimeoutMs = 1500, UiaFirstTimeoutMs = 3000;

    private static UIA.IUIAutomation? _uia;   // UIA 对象只创建一次（在 UIA 专用线程上），之后重复使用
    private static bool _uiaWarm;

    private const int UIA_EditControlTypeId = 50004, UIA_DocumentControlTypeId = 50030;

    /// <summary>
    /// 取字结果。Text = 第一部分文字；Rest（可选）= 在后台线程调用、返回“剩下的文字”的函数。
    /// </summary>
    public sealed record Result(string? Text, string Method, string? Detail, Func<string?>? Rest = null)
    {
        /// <summary>true = 这个窗口读不了（界面上直接显示 Detail 里的原因）。</summary>
        public bool Unsupported { get; init; }

        /// <summary>v0.4：true = 取到的是用户选中的文字（只读这些，读完自动停）。</summary>
        public bool Selection { get; init; }

        /// <summary>v0.6：true = 这个程序没有文字光标（浏览器网页、PDF），只能读选中的文字。</summary>
        public bool NoCaret { get; init; }
    }

    private sealed record UiaOutcome(Result Result, int ControlType, bool Focusable);

    /// <summary>朗读的目标：触发那一刻确定的外部程序窗口。</summary>
    public sealed record Target(IntPtr Window, IntPtr Focus, string Trigger)
    {
        public string ForegroundClass { get; init; } = "";
        public string FocusClass { get; init; } = "";
        public string Process { get; init; } = "?";
        public uint Pid { get; init; }
        public override string ToString() => $"{Process} [{ForegroundClass} / {FocusClass}]（{Trigger}）";

        /// <summary>补全类名、进程名（很快，在界面线程调用）。</summary>
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

    // ---------- UIA 专用线程 ----------
    // UIA 客户端建议在 MTA 线程上调用（不能在界面 STA 线程上调用：目标程序卡住会连累界面）。
    // 用一条长期存在的线程处理所有 UIA 请求：COM 对象始终在同一线程上使用；某次调用卡死时，
    // 界面这边超时返回，后面的请求排队等它（_uiaBusy 期间直接判超时，不越积越多）。
    private static readonly System.Collections.Concurrent.BlockingCollection<Action> UiaQueue = new();
    private static volatile bool _uiaBusy;
    static CaretTextGrabber()
    {
        var t = new Thread(() =>
        {
            foreach (var job in UiaQueue.GetConsumingEnumerable())
            {
                _uiaBusy = true;
                try { job(); } catch (Exception ex) { Log.Write("UIA 线程异常: " + ex.Message); }
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

    /// <summary>启动时调用一次：提前创建 UIA 对象并做一次查询（第一次调用要几百毫秒），按快捷键时就快了。</summary>
    public static void WarmUp()
    {
        _ = RunOnUia(() =>
        {
            var sw = Stopwatch.StartNew();
            try { _uia ??= CreateUia(); _uia.GetRootElement(); _ = _uia.GetFocusedElement(); _uiaWarm = true; }
            catch (Exception ex) { Log.Write("UIA 预热失败: " + ex.Message); }
            Log.Write($"UIA 预热完成，用时 {sw.ElapsedMilliseconds} ms");
            return 0;
        });
    }

    /// <summary>
    /// 取文字。必须在界面线程调用（剪贴板只能在 STA 线程操作）。
    /// </summary>
    public async Task<Result> GrabAsync(Target target, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            if (target.Window == IntPtr.Zero || !Native.IsWindow(target.Window))
                return new Result(null, "?", "没有找到要朗读的窗口：请先点一下文档，把光标放到要开始读的地方") { Unsupported = true };

            // 0) 权限：目标程序是“管理员”而我们不是 → Windows 不让读（UIPI）
            int own = Native.IntegrityLevelOf((uint)Environment.ProcessId);
            int theirs = target.Pid == 0 ? 0 : Native.IntegrityLevelOf(target.Pid);
            if (theirs == -1 || (own > 0 && theirs > own))
            {
                Log.Write($"目标程序权限更高（对方 {theirs:X} / 本程序 {own:X}）：{target}");
                return new Result(null, "权限", $"「{target.Process}」是以管理员身份运行的，Windows 不允许普通程序读取它。\n" +
                                                "请右键 CaretReader.exe →「以管理员身份运行」光标朗读后再试。") { Unsupported = true };
            }

            // 1) 本程序万一在前台（菜单 / 对话框刚关）：先把前台还给目标窗口
            if (FocusTracker.IsOwn(Native.GetForegroundWindow()))
            {
                bool ok = await Native.RestoreForegroundAsync(target.Window, 600);
                Log.Write($"本程序在前台，把前台还给目标窗口：{(ok ? "成功" : "失败")}");
            }
            if (Native.GetForegroundWindow() != target.Window)
                Log.Write($"注意：目标窗口当前不在前台（前台是 {Native.ClassNameOf(Native.GetForegroundWindow())}），按目标窗口读取");

            // 2) UI Automation（专用线程 + 超时）
            int timeout = _uiaWarm ? UiaTimeoutMs : UiaFirstTimeoutMs;
            UiaOutcome? u = null;
            if (_uiaBusy)
                Log.Write("UIA 线程还在处理上一次（目标程序可能卡住了），这次跳过 UIA");
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
                    Log.Write($"UIA 未取到文字（{u.Result.Detail}）");
                }
                else Log.Write($"UIA 超时（{timeout / 1000.0:0.0} 秒）");
            }

            // 3) 剪贴板（只做安全的部分）
            var (allowed, caretToEnd, refuse) = ClipboardPolicy(target, u);
            if (!allowed)
            {
                Log.Write($"不使用剪贴板方式：{refuse}；窗口 {target}");
                return new Result(null, "剪贴板", refuse) { Unsupported = true };
            }
            Log.Write($"改用剪贴板方式（{(caretToEnd ? "选中 / 光标到文末" : "只探测选中")}）；窗口 {target}");
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
        Log.Write($"TEST 方法={r.Method} 选中={r.Selection} 长度={r.Text?.Length ?? -1} 前30字=「{(t.Length > 30 ? t.Substring(0, 30) : t)}」");
    }

    // =====================================================================
    // 判断：这个窗口能不能用剪贴板方式？
    // =====================================================================

    // 控制台 / 终端 / 资源管理器 / 桌面 / 任务栏：Ctrl+C 会中断程序或复制文件，绝不能模拟按键
    private static readonly HashSet<string> BlockedClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "ConsoleWindowClass", "CASCADIA_HOSTING_WINDOW_CLASS", "PseudoConsoleWindow", "mintty", "VirtualConsoleClass",
        "PuTTY", "CabinetWClass", "ExploreWClass", "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd",
        "SysListView32", "SysTreeView32", "DirectUIHWND",
    };
    private static readonly HashSet<string> BlockedProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "cmd", "powershell", "pwsh", "WindowsTerminal", "OpenConsole", "conhost", "wt", "explorer", "putty", "kitty",
        "mintty", "ConEmu", "ConEmu64", "alacritty", "wezterm-gui", "Taskmgr", "wsl", "bash", "ssh", "MobaXterm", "Tabby",
        "Hyper", "WinSCP", "mstsc", "vmconnect", "VirtualBoxVM", "vmware-vmx", "TeamViewer", "AnyDesk", "SunloginClient",
    };
    // 已知的文字编辑控件 / 编辑器（可以用 Ctrl+Shift+End 选到文末）
    private static readonly HashSet<string> EditorClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Edit", "RichEdit20A", "RichEdit20W", "RichEdit50W", "RICHEDIT60W", "RichEditD2DPT", "_WwG", "Scintilla",
    };
    private static readonly HashSet<string> EditorProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "notepad", "winword", "wordpad", "wps", "et", "notepad++", "sublime_text", "Code", "Typora", "Obsidian",
    };

    /// <summary>
    /// 剪贴板方式的规则：allowed = 能不能发 Ctrl+C；caretToEnd = 能不能再发 Ctrl+Shift+End（只限文字编辑窗口）。
    /// </summary>
    private static (bool allowed, bool caretToEnd, string? refuse) ClipboardPolicy(Target t, UiaOutcome? u)
    {
        if (BlockedClasses.Contains(t.ForegroundClass) || BlockedClasses.Contains(t.FocusClass) || BlockedProcesses.Contains(t.Process))
            return (false, false, "这个窗口不支持朗读（控制台 / 终端 / 文件夹窗口不能用复制方式）");
        bool editor = (u != null && u.Focusable && u.ControlType is UIA_EditControlTypeId or UIA_DocumentControlTypeId && !IsBrowser(t))
                      || EditorClasses.Contains(t.FocusClass) || EditorProcesses.Contains(t.Process);
        return (true, editor, null);
    }

    private static readonly HashSet<string> Browsers = new(StringComparer.OrdinalIgnoreCase)
    {
        "msedge", "chrome", "firefox", "360se", "360chrome", "QQBrowser", "SogouExplorer", "brave", "opera", "vivaldi",
    };
    // 浏览器网页里 Ctrl+Shift+End 会选中整页剩余内容（包括菜单、广告），只在可编辑框（Edit 类型）里才算“编辑器”
    private static bool IsBrowser(Target t) => Browsers.Contains(t.Process);

    // =====================================================================
    // 方法 1：UI Automation（在 UIA 专用线程上执行）
    // =====================================================================
    private static UiaOutcome TryUia(Target target)
    {
        int controlType = 0;
        bool focusable = false;
        try
        {
            _uia ??= CreateUia();
            UIA.IUIAutomationElement? el = null;
            string source = "系统焦点";
            try { el = _uia.GetFocusedElement(); } catch { }

            // v0.6：系统焦点不属于目标程序（例如在本程序上，或者目标窗口暂时没被激活），
            // 就从目标窗口自己的焦点子窗口开始找
            uint elPid = 0;
            try { if (el != null) elPid = (uint)el.CurrentProcessId; } catch { el = null; }
            if (el == null || (target.Pid != 0 && elPid != target.Pid))
            {
                IntPtr h = target.Focus != IntPtr.Zero && Native.IsWindow(target.Focus) ? target.Focus : target.Window;
                try { el = _uia.ElementFromHandle(h); source = "目标窗口的焦点控件"; } catch { el = null; }
                if (el == null) return new UiaOutcome(new Result(null, "UIA", $"焦点不在目标程序上（焦点 pid {elPid}）"), 0, false);
            }

            try { controlType = el.CurrentControlType; focusable = el.CurrentIsKeyboardFocusable != 0; } catch { }
            string who = $"{SafeClass(el)} / 类型 {controlType} / pid {SafePid(el)} / {source}";
            var walker = _uia.RawViewWalker;

            // 有时焦点在文本控件的子元素上，所以最多往上找 3 层
            var probe = el;
            for (int depth = 0; probe != null && depth < 4; depth++)
            {
                var r = TryTextPattern(probe, who + $" / 层级 {depth}");
                if (r != null) return new UiaOutcome(r, controlType, focusable);
                try { probe = walker.GetParentElement(probe); } catch { probe = null; }
            }

            // 从窗口句柄找到的元素本身可能是外框（例如 Word 的文档窗口），往下找一个支持 TextPattern 的子元素
            if (source != "系统焦点")
            {
                try
                {
                    var cond = _uia.CreatePropertyCondition(UIA.UIA_PropertyIds.UIA_IsTextPatternAvailablePropertyId, true);
                    var child = el.FindFirst(UIA.TreeScope.TreeScope_Descendants, cond);
                    if (child != null)
                    {
                        var r = TryTextPattern(child, who + " / 子元素");
                        if (r != null) return new UiaOutcome(r, controlType, focusable);
                    }
                }
                catch { }
            }
            return new UiaOutcome(new Result(null, "UIA", who + " 不支持 TextPattern"), controlType, focusable);
        }
        catch (Exception ex)
        {
            return new UiaOutcome(new Result(null, "UIA", "异常: " + ex.Message), controlType, focusable);
        }
    }

    /// <summary>在一个元素上用 TextPattern 取“选中的文字”或“光标到文末”；不支持时返回 null。</summary>
    private static Result? TryTextPattern(UIA.IUIAutomationElement el, string who)
    {
        if (el.GetCurrentPattern(UIA.UIA_PatternIds.UIA_TextPatternId) is not UIA.IUIAutomationTextPattern tp) return null;
        UIA.IUIAutomationTextRange? start = null;

        // (a) 先看“选区”：没有选中文字时，选区就是光标所在的那个“空范围”
        try
        {
            var sel = tp.GetSelection();
            if (sel != null && sel.Length > 0)
            {
                start = sel.GetElement(0);
                string? selected = ReadSelection(sel);
                if (selected != null)
                    return new Result(selected, "UIA", who + $" / 选中 {sel.Length} 段") { Selection = true };
            }
        }
        catch { /* 有的控件不支持 GetSelection */ }

        // (b) 再试 TextPattern2 的 GetCaretRange（较新的控件支持）
        if (start == null && el.GetCurrentPattern(UIA.UIA_PatternIds.UIA_TextPattern2Id) is UIA.IUIAutomationTextPattern2 tp2)
        {
            try { start = tp2.GetCaretRange(out _); } catch { }
        }
        if (start == null) return null;

        var range = start.Clone();
        // 把范围的终点移到整个文档的终点 → 得到“光标（或选区开头）到文末”
        range.MoveEndpointByRange(UIA.TextPatternRangeEndpoint.TextPatternRangeEndpoint_End, tp.DocumentRange,
                                  UIA.TextPatternRangeEndpoint.TextPatternRangeEndpoint_End);
        var res = ReadInTwoSteps(range, who);
        // 浏览器的只读网页里“光标”其实不存在（UIA 给的是页面开头），读出来的会是整页：不当真
        return res;
    }

    private static string SafePid(UIA.IUIAutomationElement el)
    {
        try { return el.CurrentProcessId.ToString(); } catch { return "?"; }
    }


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
            if (theirs == -1) Log.Write($"查不到目标程序的权限级别：{target}");
            if (own > 0 && theirs > own)
            {
                Log.Write($"目标程序权限更高（对方 {theirs:X} / 本程序 {own:X}）：{target}");
                return ElevatedResult(target);
            }
            // 普通权限的程序打不开管理员程序的令牌 → 查不到（-1）。这时不直接下结论（也可能是系统保护进程），
            // 先照常试 UIA；UIA 也读不到时，就按“对方是管理员程序”提示（剪贴板方式发的按键同样会被 Windows 拦下）。
            bool maybeElevated = theirs == -1 && own > 0 && own < 0x3000;

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

            if (maybeElevated)
            {
                Log.Write($"UIA 读不到且查不到对方权限级别，按管理员程序处理：{target}");
                return ElevatedResult(target);
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

    private static Result ElevatedResult(Target target) =>
        new(null, "权限", $"「{target.Process}」是以管理员身份运行的，Windows 不允许普通程序读取它。\n" +
                          "请右键 CaretReader.exe →「以管理员身份运行」光标朗读后再试。") { Unsupported = true };

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

    /// <summary>剪贴板方式的规则（见 Core/InteractionRules.cs 的 ClipboardRules，测试程序里有用例）。</summary>
    private static (bool allowed, bool caretToEnd, string? refuse) ClipboardPolicy(Target t, UiaOutcome? u)
        => ClipboardRules.Decide(t.ForegroundClass, t.FocusClass, t.Process, u?.ControlType ?? 0, u?.Focusable ?? false);

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
        // 浏览器网页：点击处会留下一个“空选区”，从那里读到页尾
        return ReadInTwoSteps(range, who);
    }

    private static string SafePid(UIA.IUIAutomationElement el)
    {
        try { return el.CurrentProcessId.ToString(); } catch { return "?"; }
    }

    /// <summary>
    /// v0.4：读出选区里的文字（最多 MaxChars 字、最多 64 段）。全是空白 / 没有选中时返回 null。
    /// </summary>
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

    /// <summary>
    /// 先读前 FirstChunkChars 字；如果文档还有更多，就在最后一个句末（。！？换行）处截断，
    /// 剩下的部分交给 Rest 在后台读取（重新读一次“光标到文末”，去掉已经读过的开头）。
    /// </summary>
    private static Result ReadInTwoSteps(UIA.IUIAutomationTextRange range, string who)
    {
        string first = range.GetText(FirstChunkChars) ?? "";
        if (first.Length != FirstChunkChars)
        {
            // 文档本来就短（或者控件不理会长度参数、一次全给了）：一步完成
            if (first.Length > MaxChars) first = first.Substring(0, MaxChars);
            return new Result(Clean(first), "UIA", who);
        }

        // 在最后一个句末处截断，避免把一句话切成两半分别处理
        int cut = first.LastIndexOfAny(new[] { '。', '！', '？', '!', '?', '\n', '\r' });
        string head = cut >= 200 ? first.Substring(0, cut + 1) : first;
        string? Rest()
        {
            try
            {
                string all = range.GetText(MaxChars) ?? "";
                if (!all.StartsWith(head, StringComparison.Ordinal))
                {
                    Log.Write("UIA 第二步：文档在朗读开始后被修改，只读第一部分");
                    return null;
                }
                return Clean(all.Substring(head.Length));
            }
            catch (Exception ex)
            {
                Log.Write("UIA 第二步读取失败: " + ex.Message);
                return null;
            }
        }
        return new Result(Clean(head), "UIA", who + " / 分两步", Rest);
    }

    private static UIA.IUIAutomation CreateUia()
    {
        // 优先用较新的 CUIAutomation8（Win8+），失败再用旧版 CUIAutomation
        foreach (var clsid in new[] { "e22ad333-b25f-460c-83d0-0581107395c9", "ff48dba4-60ef-4201-aa87-54103eef594e" })
        {
            try
            {
                var t = Type.GetTypeFromCLSID(new Guid(clsid), throwOnError: true)!;
                var uia = (UIA.IUIAutomation)Activator.CreateInstance(t)!;
                // v0.6（审查意见采纳）：系统默认连接超时 2 秒、单次调用超时 20 秒。目标程序卡死时，
                // UIA 专用线程会被一次调用卡住很久，后面几次朗读都只能跳过 UIA。改短，让卡住的调用自己报错返回。
                try { if (uia is UIA.IUIAutomation2 u2) { u2.ConnectionTimeout = 1500; u2.TransactionTimeout = 3000; } } catch { }
                return uia;
            }
            catch { }
        }
        throw new InvalidOperationException("无法创建 UI Automation 对象");
    }

    private static string SafeClass(UIA.IUIAutomationElement el)
    {
        try { return el.CurrentClassName ?? "?"; } catch { return "?"; }
    }

    // =====================================================================
    // 方法 2：剪贴板
    // =====================================================================
    // 这些编辑器在“什么都没选中”时按 Ctrl+C 会复制整行，没法用 Ctrl+C 判断有没有选中文字 → 不做“选中”探测
    private static readonly HashSet<string> LineCopyClasses = new(StringComparer.OrdinalIgnoreCase) { "Scintilla" };
    private static readonly HashSet<string> LineCopyProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "notepad++", "sublime_text", "Code", "devenv", "idea64", "pycharm64", "webstorm64", "rider64", "atom", "zed",
    };

    private static async Task<Result> TryClipboardAsync(Target target, bool caretToEnd)
    {
        // 用户按快捷键时 Ctrl/Alt 往往还没松开；这时模拟 Ctrl+C 实际会变成 Ctrl+Alt+C（很多程序里什么都不做，
        // 或者触发别的功能），这正是 v0.5 “后台按快捷键读不到”的原因之一。
        // v0.6：先等修饰键松开（最多 1.2 秒）；还没松开就由程序发送“松开”（带防 Alt 菜单的掩码键）。
        if (!await Native.WaitModifiersReleasedAsync(1200))
            return new Result(null, "剪贴板", "修饰键一直没有松开");

        // 发按键之前，目标程序必须在前台（按键只会发给前台窗口）
        if (Native.GetForegroundWindow() != target.Window && !await Native.RestoreForegroundAsync(target.Window, 600))
            return new Result(null, "剪贴板", "目标窗口不在前台，没有发送按键") { Unsupported = true };

        var sw = Stopwatch.StartNew();

        // 1) 备份剪贴板原有内容。备份失败、而剪贴板里又有东西 → 宁可不读，也不毁掉用户的剪贴板
        var (backupOk, backup) = BackupClipboard();
        if (!backupOk && Native.CountClipboardFormats() > 0)
            return new Result(null, "剪贴板", "无法备份剪贴板（可能被其它程序占用），为保护剪贴板内容已放弃");
        uint seq0 = Native.GetClipboardSequenceNumber();

        // 1.5) v0.4 选中朗读：先只按 Ctrl+C。复制出了文字 = 用户选中了文字 → 只读这些，
        //      不按 ←（保留用户的选区），还原剪贴板后直接返回。没复制出东西 = 没有选中 → 照旧从光标读。
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
                    return new Result(Clean(sel), "剪贴板", "选中的文字") { Selection = true };
                }
            }
            seq0 = Native.GetClipboardSequenceNumber();
        }

        // v0.6：浏览器、PDF 阅读器等没有“光标”的程序只做上面的“选中”探测，不按 Ctrl+Shift+End
        if (!caretToEnd)
        {
            await RestoreWithLateCheckAsync(backup);
            return new Result(null, "剪贴板", "没有选中的文字（这个程序没有可用的文字光标）") { NoCaret = true };
        }

        // 2) 选中“光标到文末”
        Native.SendChord(Native.VK_CONTROL, Native.VK_SHIFT, Native.VK_END);
        await Task.Delay(80);

        // 3) 复制
        Native.SendChord(Native.VK_CONTROL, Native.VK_C);

        // 4) 等剪贴板真的变化（最多 2 秒，Word 复制很长的文档会慢一些）
        sw.Restart();
        while (Native.GetClipboardSequenceNumber() == seq0 && sw.ElapsedMilliseconds < 2000)
            await Task.Delay(20);
        bool copied = Native.GetClipboardSequenceNumber() != seq0;
        if (!copied) Log.Write("剪贴板方式：2 秒内没有检测到复制");

        string? text = null;
        if (copied)
        {
            await Task.Delay(30);
            text = ReadClipboardText();
        }

        // 5) 按 ← 取消选中：选区会收缩到开头，也就是原来光标的位置。
        //    v0.2：复制失败时也要按（否则文字一直被选中，用户一打字就会把它们替换掉）。
        //    唯一例外：复制成功但内容为空 = 确定什么都没选中（光标本来就在文末），这时按 ← 反而会移动光标。
        if (!(copied && text == ""))
            Native.SendChord(Native.VK_LEFT);

        // 6) 还原剪贴板（带“不记入剪贴板历史”的标记）+ 7) 防“迟到的复制”
        await RestoreWithLateCheckAsync(backup);

        if (!copied) return new Result(null, "剪贴板", "复制没有生效（目标程序可能不支持 Ctrl+C）");
        text ??= "";
        if (text.Length > MaxChars) text = text.Substring(0, MaxChars);
        return new Result(Clean(text), "剪贴板", null);
    }

    /// <summary>
    /// 还原剪贴板；目标程序的复制可能“迟到”（超时之后才写入剪贴板），覆盖掉刚还原的内容：过 300 毫秒再检查一次。
    /// </summary>
    private static async Task RestoreWithLateCheckAsync(DataObject? backup)
    {
        RestoreClipboard(backup);
        uint seqRestored = Native.GetClipboardSequenceNumber();
        await Task.Delay(300);
        if (Native.GetClipboardSequenceNumber() != seqRestored)
        {
            Log.Write("剪贴板在还原后又被改动（迟到的复制），再还原一次");
            RestoreClipboard(backup);
        }
    }

    private static string? ReadClipboardText()
    {
        // 剪贴板可能暂时被别的程序占用，重试几次
        for (int i = 0; i < 5; i++)
        {
            try { return Clipboard.ContainsText() ? Clipboard.GetText(TextDataFormat.UnicodeText) : ""; }
            catch { Thread.Sleep(30); }
        }
        return null;
    }

    // 只备份这些常见格式（v0.2）：全部格式都备份会迫使 Excel 等程序“现做”大量延迟数据，可能卡几秒、占几十 MB
    private static readonly string[] BackupFormats =
    {
        DataFormats.UnicodeText, DataFormats.Text, DataFormats.Rtf, DataFormats.Html, DataFormats.FileDrop, DataFormats.Bitmap,
    };
    private const int MaxBackupChars = 8_000_000;   // 超过这个长度的文字格式不备份（约 16MB）

    /// <summary>
    /// 备份剪贴板里常见的格式。返回 (是否成功, 备份)；剪贴板本来为空时返回 (true, null)。
    /// </summary>
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
                    if (v is string str && str.Length > MaxBackupChars) { skipped.Add(fmt + "(过大)"); continue; }
                    if (v != null) { copy.SetData(fmt, false, v); n++; }
                }
                catch { skipped.Add(fmt + "(读取失败)"); }
            }
            if (skipped.Count > 0) Log.Write("剪贴板备份跳过的格式: " + string.Join(", ", skipped.Take(12)));
            return (true, n > 0 ? copy : null);
        }
        catch (Exception ex)
        {
            Log.Write("备份剪贴板失败: " + ex.Message);
            return (false, null);
        }
    }

    /// <summary>
    /// 还原剪贴板。附带三个系统认可的标记，让 Windows 剪贴板历史 (Win+V)、云剪贴板和剪贴板管理软件
    /// 忽略这次“还原”，不再多记一条。
    /// </summary>
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
            Log.Write("还原剪贴板失败: " + ex.Message);
        }
    }

    private static void MarkPrivate(DataObject d)
    {
        // https://learn.microsoft.com/windows/win32/dataxchg/clipboard-formats#cloud-clipboard-and-clipboard-history-formats
        static MemoryStream Dword(int v) => new(BitConverter.GetBytes(v));
        d.SetData("ExcludeClipboardContentFromMonitorProcessing", false, Dword(0));
        d.SetData("CanIncludeInClipboardHistory", false, Dword(0));
        d.SetData("CanUploadToCloudClipboard", false, Dword(0));
    }

    // =====================================================================

    /// <summary>
    /// 清理 Word 等程序返回的特殊字符：
    ///   \v（Word 的软回车）、\a（表格单元格结束）、\f（分页）→ 换行；
    ///   \uFFFC（嵌入对象占位符）和其它控制字符 → 删除。
    /// </summary>
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

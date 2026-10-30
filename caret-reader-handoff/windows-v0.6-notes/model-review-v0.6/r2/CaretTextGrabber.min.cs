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
                return ElevatedResult(target);
            }
            bool maybeElevated = theirs == -1 && own > 0 && own < 0x3000;
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
            if (maybeElevated)
            {
                Log.Write("<zh>");
                return ElevatedResult(target);
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
    private static Result ElevatedResult(Target target) =>
        new(null, "<zh>", "<zh>" +
                          "<zh>") { Unsupported = true };
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

## Critical  

| Area | Problem (what can be reproduced on a real Win10/11 PC) | Why it matters for the target audience (low‑end PC, weak eyes) | Fix (high‑level) |
|------|-------------------------------------------------------|--------------------------------------------------------------|------------------|
| **FocusTracker – thread‑safety** | `OnEvent` runs on a thread‑pool thread (because of `WINEVENT_OUTOFCONTEXT`). It writes `LastWindow`, `LastFocus` and `LastAt` without any synchronization while the UI thread (or the hot‑key handler) reads them. A race can produce a *stale* or *partially‑written* value, causing the “窗口不支持朗读” message even though the target window still has focus. | The UI may appear to “lose” the document after a single click, which is exactly the bug the user reported (focus lost after the ball is clicked). On a low‑end machine the race is more likely because the UI thread is already busy rendering the mascot. | Store the three fields in a `struct` and update them atomically (`Volatile.Write` or a `lock`). Example:  

```csharp
private readonly object _lock = new();
private (IntPtr window, IntPtr focus, DateTime at) _state;
...
lock(_lock){ _state = (root, hwnd, DateTime.Now); }
...
public (IntPtr window, IntPtr focus) GetState()
{
    lock(_lock) return (_state.window, _state.focus);
}
```  

| **Layered‑window rendering – BLENDFUNCTION not initialised** | `UpdateLayeredWindow` is called from the ball renderer with a `BLENDFUNCTION` that is never filled (`BlendOp`, `BlendFlags`, `SourceConstantAlpha`, `AlphaFormat` all zero). The result is a **fully transparent** window on the first paint, or a window that flickers when the struct is later overwritten by garbage. Users will see the ball disappear or flash, which is a show‑stopper for a “always‑on‑top” accessibility aid. | A transparent or flickering ball defeats the purpose of a constant visual cue for a user with weak eyes. | Initialise the struct before the first call and reuse the same instance:  

```csharp
private static readonly BLENDFUNCTION _blend = new()
{
    BlendOp = AC_SRC_OVER,
    BlendFlags = 0,
    SourceConstantAlpha = 255,   // opaque
    AlphaFormat = AC_SRC_ALPHA   // per‑pixel alpha
};
...
UpdateLayeredWindow(hwnd, hdcDst, ref pptDst, ref size, hdcSrc,
                   ref pprSrc, 0, ref _blend, ULW_ALPHA);
```  

| **Foreground restoration – missing `AllowSetForegroundWindow`** | `Native.RestoreForegroundAsync` calls `SetForegroundWindow` directly. When the caret‑reader is **background** (the usual case) Windows will reject the request unless the *foreground* process has previously called `AllowSetForegroundWindow` for our PID. The call silently fails, leaving the target window behind and the UI reports “焦点不在文字编辑框里”. | The hot‑key therefore works only after the user manually clicks the target window first – exactly the failure the user sees when the ball is hidden behind other windows. | Before calling `SetForegroundWindow` invoke `AllowSetForegroundWindow(ASFW_ANY)` (or the PID of the foreground process if you can obtain it). Example:  

```csharp
public static async Task<bool> RestoreForegroundAsync(IntPtr h, int maxMs)
{
    if (h == IntPtr.Zero || !IsWindow(h)) return false;
    if (GetForegroundWindow() == h) return true;
    if (IsIconic(h)) return false;

    // Give the foreground process permission to let us take focus.
    AllowSetForegroundWindow(-1); // ASFW_ANY

    SetForegroundWindow(h);
    …
}
```  

| **Hot‑key registration – handle recreation** | `HotkeyWindow` derives from `NativeWindow` and creates a **message‑only** window. If any code later calls `Form.CreateHandle` (e.g. the main form is recreated after a DPI change) the WinForms infrastructure may destroy and recreate the hidden window, causing the previously registered hot‑keys to be silently unregistered. Subsequent hot‑key presses do nothing. | Users who change DPI scaling or whose main form is recreated (e.g. after a theme change) lose the global hot‑keys, which is a severe regression. | Keep the hot‑key window alive for the whole process lifetime and **prevent** WinForms from recreating it:  

```csharp
protected override void OnHandleCreated(EventArgs e) => base.OnHandleCreated(e);
// never call CreateHandle again; expose a static singleton.
```  

or register the hot‑keys **on a real message‑only window created via `CreateWindowEx`** and never let WinForms touch it.  

| **UI Automation thread model** | The UIA client runs on a **dedicated long‑lived MTA thread**. Microsoft’s UIA documentation states that UIA *should not* be used on the UI STA because it can dead‑lock with UI thread COM pumps. However, some UIA providers (e.g. older Office components) **require an STA** for certain calls (e.g. `GetCurrentPattern`). Running exclusively on MTA can cause those calls to return `E_NOTIMPL` or time‑out, leading to the “不支持朗读” message for Office documents. | The user’s most common target – Word/PowerPoint – may be incorrectly rejected. | Keep the MTA queue **but** create a **fallback STA worker** for providers that demand STA. Detect `RPC_E_WRONG_THREAD` and retry on an STA thread.  

```csharp
if (comException.HResult == unchecked((int)0x8001010E)) // RPC_E_WRONG_THREAD
    RunOnStaThread(() => uiaCall(...));
```  

---

## Important  

| Area | Issue | Impact | Suggested fix |
|------|-------|--------|---------------|
| **WinEventProc delegate lifetime** | The delegate is stored in a readonly field (`_proc = OnEvent;`). This is safe **as long as the `FocusTracker` instance lives**. If the object is disposed but the hook is still active (e.g. an exception prevented `Dispose` from being called), the delegate could be collected and the callback will be a dangling pointer, causing a crash. | Crash on shutdown or when the user disables the tracker. | Ensure `Dispose` is always called (e.g. via `using` in `Program.cs`) and add a finaliser that calls `Dispose` as a safety net. |
| **Missing P/Invoke signatures** | The snippet does not contain `GetForegroundWindow`, `SetForegroundWindow`, `IsWindow`, `GetWindowThreadProcessId`, `GetAsyncKeyState`. If they are defined in another partial class they must have `SetLastError = true` and the correct `ExactSpelling`. A mismatch will make `Marshal.GetLastWin32Error()` return garbage and hide failures. | Hard to debug why foreground restoration fails. | Verify all missing imports are present, add `SetLastError = true`, and wrap calls with a helper that throws on failure. |
| **`WaitModifiersReleasedAsync` uses `keybd_event`** |
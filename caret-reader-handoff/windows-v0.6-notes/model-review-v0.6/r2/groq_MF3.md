## Critical  

| # | File / Method | Problem | Why it matters | Confidence |
|---|---------------|---------|----------------|------------|
| C1 | `FocusTracker` (not shown) – WinEventProc delegate | The delegate that is passed to `SetWinEventHook` **must stay alive** for the lifetime of the hook. If the delegate is stored only in a local variable or a property that can be overwritten, the GC may collect it and the hook will stop firing, leaving the tracker with a stale “last window”. The comment in the bug‑report says “we store it in a readonly field `_proc`”, but the posted code does **not** show that field. If the field is missing or is re‑assigned when the form is recreated, the tracker will silently stop working and the user will see “窗口不支持朗读”. | **High** – a missing delegate is a classic source of “focus lost” bugs that only appear after a few minutes of use. |
| C2 | `MainForm.OnHotkey` → `CurrentTarget` → `Native.GetForegroundWindow` / `Native.ThreadFocusOf` | The P/Invoke signatures for `GetForegroundWindow`, `GetWindowThreadProcessId`, `GetGUIThreadInfo`, `SetForegroundWindow`, etc. are declared in `Native.cs` (not shown). If any of those signatures use the wrong calling convention, `IntPtr` size, or `bool` return type, the calls will return garbage on x64 and the whole target‑selection logic will be wrong. The reviewer cannot assume they are correct without seeing them. | **Medium** – a single wrong signature can make the whole “grab” step fail on real Windows even though it works on the developer’s Linux‑cross‑compiled test harness. |
| C3 | `BallRenderer.Present` (layered‑window drawing) | `BLENDFUNCTION` must be zero‑initialised (`BlendOp = AC_SRC_OVER`, `SourceConstantAlpha = 255`, `AlphaFormat = AC_SRC_ALPHA`). The code snippet does not show the struct being cleared; if the struct is allocated on the stack without `new BLENDFUNCTION()` the fields contain random data, causing `UpdateLayeredWindow` to fail silently (the ball disappears) or to draw with wrong opacity. | **High** – layered windows are fragile; a single un‑initialised field will break the UI on the first run on a real PC. |
| C4 | `MainForm.OnHotkey` → `SetForegroundWindow` (implicit) | When the app is **background** and wants to bring the target window to the foreground, Windows’ *foreground‑lock timeout* blocks the call unless the background process has been granted permission via `AllowSetForegroundWindow`. The current code never calls `AllowSetForegroundWindow`, so the `SetForegroundWindow` call will often be ignored, leaving the target window unfocused and the subsequent `SendInput`/clipboard grab will fail. | **High** – this explains the reported “global hotkey also fails when our app is not in front”. |
| C5 | `Hotkey` registration on the form’s handle | The form can recreate its handle (e.g. when DPI changes, when the user toggles “Always on top”, or when the form is hidden/shown). If the handle changes, the previously‑registered hotkeys remain attached to the **old** HWND and stop working. The code registers hotkeys on `_hotkeys.Handle` (a dedicated `NativeWindow`), but the `NativeWindow` is created **after** the form’s handle is created? The snippet does not show its lifetime. If `_hotkeys` is disposed or its handle is recreated, hotkeys will be lost. | **Medium** – intermittent hot‑key loss is exactly what the user observed after clicking the UI or after a DPI change. |
| C6 | `StartReadingAsync` → `Stopwatch.GetElapsedTime(pressedAt)` | `pressedAt` is supplied by `OnHotkey` as a `long` timestamp, presumably from `Stopwatch.GetTimestamp()`. However `Stopwatch.GetElapsedTime` expects a *timestamp* obtained from the **same** `Stopwatch` instance or from `Stopwatch.GetTimestamp()`. If the value passed is not a raw timestamp (e.g. a `DateTime.UtcNow.Ticks`), the elapsed‑time calculation will be wildly incorrect, possibly causing negative or huge `grabSeconds`. The value is logged but never used, so the bug is hidden now but may surface if the log is later parsed for performance metrics. | **Low** – not a functional break, but a latent bug that could mislead developers. |

### Immediate fixes  

1. **WinEventProc lifetime** – Add a private readonly field in `FocusTracker`:

   ```csharp
   private readonly WinEventDelegate _proc;
   public FocusTracker()
   {
       _proc = WinEventProc;
       _hook = SetWinEventHook(..., _proc, ...);
   }
   ```

   Ensure the `FocusTracker` instance lives for the whole process (e.g. a static singleton) or is disposed only after `UnhookWinEvent`.  

2. **Validate all P/Invoke signatures** in `Native.cs`. For x64 they must be declared with `CallingConvention.Winapi` (default) and `IntPtr` for handles. Add unit‑tests that call each imported function on a real Windows machine (e.g. via a small console harness).  

3. **Zero‑initialise `BLENDFUNCTION`** before calling `UpdateLayeredWindow`:

   ```csharp
   var bf = new BLENDFUNCTION
   {
       BlendOp = AC_SRC_OVER,
       SourceConstantAlpha = 255,
       AlphaFormat = AC_SRC_ALPHA
   };
   ```

4. **Call `AllowSetForegroundWindow`** before trying to bring the target to the foreground:

   ```csharp
   uint pid = Native.GetWindowThreadProcessId(targetHwnd, out uint procId);
   Native.AllowSetForegroundWindow(procId);
   Native.SetForegroundWindow(targetHwnd);
   ```

   This should be done in the code path that handles a click‑triggered start (see `OnMainActionAsync`).  

5. **Make hot‑key registration robust** – create the dedicated message‑only window **once** at program start and never let it recreate its handle. Store its handle in a static readonly field and pass that same handle to every `Register`/`Unregister`.  

6. **Clarify timestamp handling** – either rename `pressedAt` to `timestamp` and document that it must be `Stopwatch.GetTimestamp()`, or replace the elapsed‑time calculation with a proper `Stopwatch` instance:

   ```csharp
   var sw = Stopwatch.StartNew();
   // later:
   double grabSeconds = sw.Elapsed.TotalSeconds;
   ```

---

## Important  

| # | File / Method | Issue | Reason | Confidence |
|---|---------------|-------|--------|------------|
| I1 | `MainForm.CurrentTarget` | **Self‑targeting** – If the ball window is the foreground window (`GetForegroundWindow` returns our own layered window), the code falls back to the *last* external window. However the `FocusTracker.IsOwn(fg)` check only excludes windows that belong to **our process**, not the ball itself (which is also ours). When the user clicks the ball while a document is still focused, the ball becomes foreground, `IsOwn` returns `true`, and the method returns the *last* window **even if the last window is stale** (e.g. the user switched apps after the last poll). This can cause “unsupported window” messages. | **Medium** – a subtle race that appears only when the user clicks the ball quickly after switching apps. |
| I2 | `OnFormClosing` → `_readTask.Wait(3000)` / `Task.Run(_engine.Dispose).Wait(3000)` | **Potential deadlock** – If `_engine.Dispose` internally blocks on the UI thread (e.g. COM shutdown) while the UI thread is waiting for the task to complete, the process can hang on exit. The 3‑second timeout mitigates but does not guarantee a clean shutdown. | **Medium** – on low‑end PCs the engine may take longer than 3 s, leaving resources undisposed. |
| I3 | `ApplyHotkeys` → `TryRegister` | **Duplicate hotkey handling** – If both main and stop hotkeys are the same, the code logs an error but still registers the *main* hotkey and leaves the *stop* hotkey unregistered (`_stopRegistered = false`). The user will have no way to stop reading via hotkey. The UI should either reject the duplicate at the settings UI level or automatically pick a different stop key. | **Low** – user‑experience issue, not a crash. |
| I4 | `StartReadingAsync` → `SpeechPipeline.RunAsync(..., onClause: null, hotkeyTimestamp: pressedAt, ...)` | **Unused callback** – `onClause` is always `null`. If future extensions want to update UI per clause (e.g. highlight text), the current design forces a `null` delegate. Not a bug now, but a design limitation that will require refactoring later. | **Low** – future‑proofing. |
| I5 | `FetchRestAsync` – `Task.Run(rest)` | **Thread‑affinity for UIA** – The `rest` delegate may call UIA again (e.g. to fetch hidden text). Running it on a thread‑pool thread bypasses the dedicated UIA MTA thread, potentially causing COM marshaling overhead or deadlocks. All UIA calls should be routed through the single UIA worker. | **Medium** – could cause occasional hangs when the “rest” fetch is slow. |

### Suggested mitigations  

* **Self‑targeting** – In `CurrentTarget`, after the `IsOwn(fg)` check, also verify that `fg` is not the ball’s own top‑level window (`if (fg == this.Handle) …`). If it is, treat it as “no foreground” and fall back to the tracker’s last external window *only if* that window is still alive (`IsWindow(last)`).  

* **Graceful shutdown** – Replace the blocking `Wait` calls with `await` and a cancellation token that is signalled from `FormClosing`. Example:

  ```csharp
  protected override async void OnFormClosing(FormClosingEventArgs e)
  {
      // …
      _readCts?.Cancel();
      await Task.WhenAny(_readTask, Task.Delay(3000));
      await Task.Run(() => _engine.Dispose()).WaitAsync(TimeSpan.FromSeconds(3));
      // …
  }
  ```

* **Hotkey conflict UI** – In the settings dialog, disable the stop‑key picker when it matches the main key, or auto‑increment to the next free key and inform the user.  

* **UIA thread enforcement** – Wrap the `rest` delegate in a call to the UIA worker queue:

  ```csharp
  var more = await _uiaWorker.Enqueue(() => rest());
  ```

* **Add defensive checks** – Before calling `SetForegroundWindow`, verify that the target window still belongs to the expected process (`GetWindowThreadProcessId`). If it has changed, abort the grab and show a user‑friendly message.  

---

## Minor  

| # | File / Method | Observation | Impact |
|---|---------------|-------------|--------|
| M1 | `StartReadingAsync` – unused variable `grabSeconds` | Likely left from debugging; harmless but noisy. |
| M2 | `OnHotkey` – fire‑and‑forget `OnMainActionAsync` | If the user spams the hotkey, multiple overlapping reads could be queued before the session check cancels them. The UI will still be responsive, but CPU usage may spike on very low‑end PCs. |
| M3 | `ApplyHotkeys` – `errors.Add("<zh>")` strings are duplicated | Not a functional bug, just a localisation maintenance issue. |
| M4 | `OnFormClosing` – swallow exceptions from `_settings.Save` | If saving fails (e.g. disk full) the user never knows; the app will silently lose the ball position. |
| M5 | `PauseReading` / `ResumeReading` – no check for `_sink == null` | If the user hits pause before any reading started, `_sink` is null and `PauseReading` does nothing (fine). No crash. |
| M6 | `FlashStatus` called with hard‑coded 5000 ms in `LoadAsync` | Might interfere with UI animations on very slow machines; not critical. |

---

## Design objections  

| # | Issue | Why it’s problematic for the target audience (beginners, low‑end PCs, eye‑condition) | Suggested alternative |
|---|-------|---------------------------------------------------|----------------------|
| D1 | **Layered‑window + no‑activate + custom mouse handling** – The ball intercepts mouse clicks to start/pause/stop, but because it never receives activation messages, standard accessibility tools (e.g. screen readers, high‑contrast mode) cannot focus it. Users with visual impairments may rely on keyboard navigation; the ball is invisible to the keyboard. | Provide an optional **keyboard‑only** control (e.g. a hidden `ToolStripMenuItem` with the same hotkeys) and expose the ball’s actions via `AccessibleObject` so that UI Automation can invoke them. |
| D2 | **Polling‑timer removed in favour of WinEventHook** – `SetWinEventHook` with `WINEVENT_OUTOFCONTEXT|SKIPOWNPROCESS` is correct, but the hook runs on a thread‑pool thread that may be throttled on a 4 GB‑RAM PC, causing the “last window” to be stale for up to a second. The original 300 ms timer gave a more deterministic update cadence. | Keep the timer as a fallback that forces a refresh every 300 ms if


## Critical

**1. NAudioSink: 90 s buffer overflows on long pause → unhandled exception kills TTS**

```csharp
_buf = new BufferedWaveProvider(new WaveFormat(sampleRate, 16, 1))
{
    BufferDuration = TimeSpan.FromSeconds(90),
    DiscardOnBufferOverflow = false,   // ← throws when full
    ReadFully = true,
};
```

`Pause()` stops the WaveOutEvent consumer. Sherpa-onnx keeps calling `Write()` (or the caller queues ahead). After 90 s of paused reading the buffer is full; the next `AddSamples` throws `InvalidOperationException`. On a 4 GB RAM beginner PC the user pauses, walks away, comes back → crash or silent TTS death.

**Fix:** either set `DiscardOnBufferOverflow = true` (drop oldest audio, acceptable for a reader) or cap the buffer to a realistic sentence length (e.g. 15 s) and have the synthesis loop check `BufferedSeconds` before producing more. Also wrap `AddSamples` in a try/catch that logs and drops the chunk rather than propagating.

**2. NAudioSink.Begin: double-call leaks the previous WaveOutEvent**

```csharp
public void Begin(int sampleRate)
{
    lock (_gate)
    {
        if (_stopped) return;
        _buf = new BufferedWaveProvider(...);   // old _buf reference lost
        var output = new WaveOutEvent(...);     // old _out reference lost
        ...
        _out = output;
    }
}
```

If `Begin` is called while a previous session is still live (e.g. rapid start→stop→start race, or a bug in the caller), the old `_out` is never `Dispose()`d. `WaveOutEvent` holds a native audio endpoint handle; leaking it exhausts the audio subsystem after a few cycles.

**Fix:** at the top of `Begin`, if `_out != null`, `Stop()` it first (or at minimum `Dispose()` it).

## Important

**3. BallRenderer.Present: GetDC/ReleaseDC without try/finally**

```csharp
IntPtr screen = Native.GetDC(IntPtr.Zero);
Native.UpdateLayeredWindow(hwnd, screen, ref dst, ref size, _memDc, ref src, 0, ref blend, Native.ULW_ALPHA);
Native.ReleaseDC(IntPtr.Zero, screen);
```

If `UpdateLayeredWindow` throws (e.g. `hwnd` was destroyed between the null-check and the call — possible if the form is closing on another thread), the screen DC leaks. Over thousands of frames this exhausts the per-process GDI handle limit (10 000 on Win10/11).

**Fix:**
```csharp
IntPtr screen = Native.GetDC(IntPtr.Zero);
try { Native.UpdateLayeredWindow(...); }
finally { Native.ReleaseDC(IntPtr.Zero, screen); }
```

**4. Bubble.ShowNear: Screen.FromRectangle can return null**

```csharp
var wa = Screen.FromRectangle(anchor).WorkingArea;
```

If `anchor` is at coordinates that no monitor covers (e.g. after a monitor is unplugged while the bubble is being positioned, or a DPI-change race leaves a stale rectangle), `FromRectangle` returns `null` → `NullReferenceException` on the UI thread → app crash.

**Fix:**
```csharp
var scr = Screen.FromRectangle(anchor) ?? Screen.PrimaryScreen;
if (scr == null) return;
var wa = scr.WorkingArea;
```

**5. Bubble static font: non-atomic lazy init**

```csharp
private static Font? _font;
private static Font BubbleFont => _font ??= new Font(...);
```

`??=` compiles to a read-check-write sequence, not an atomic operation. If two threads ever touch `BubbleFont` simultaneously (unlikely today but possible if the bubble is shown from a non-UI path), two `Font` objects are allocated and one leaks a GDI handle.

**Fix:** use `Lazy<Font>` or a `lock`, or simply make it an instance field (the bubble is a single form).

**6. BallRenderer.Present: UpdateLayeredWindow moves the window, but WinForms Location is stale**

```csharp
var dst = new Native.POINT { X = screenPos.X, Y = screenPos.Y };
Native.UpdateLayeredWindow(hwnd, screen, ref dst, ref size, _memDc, ...);
```

`UpdateLayeredWindow` repositions the window at the OS level. The owning `Form.Location` property is **not** updated. If any code later reads `this.Location` (e.g. for drag math, for `Bubble.ShowNear(anchor: ballBounds)`, or for `Screen.FromControl`), it gets the stale value. The caller must maintain its own position variable and never trust `Form.Location` after a `Present`.

**Fix:** document this contract on `Present`, or call `SetWindowPos` with `SWP_NOMOVE | SWP_NOSIZE` after `UpdateLayeredWindow` to force WinForms to re-sync (costly), or better: have the owning form store `_ballPos` and pass it to `Bubble.ShowNear` instead of reading `Location`.

**7. NAudioSink.Write: per-call byte[] allocation**

```csharp
public void Write(float[] samples)
{
    var bytes = new byte[samples.Length * 2];
    ...
}
```

At 22 050 Hz mono, sherpa-onnx typically emits ~10 ms chunks → 220 samples → 440 bytes, ~100 allocations/s. On a 4 GB RAM machine this is Gen0 churn that adds GC pauses. Not a crash, but it makes the "low-end PC" promise shaky.

**Fix:** keep a reusable `byte[] _scratch` sized to the max chunk, or use `stackalloc` for small chunks.

**8. PerfTest.Sample: Process.GetCurrentProcess() leaks handles**

```csharp
private void Sample()
{
    var p = Process.GetCurrentProcess();   // new object + internal handle each call
    _ws.Add(p.WorkingSet64); _priv.Add(p.PrivateMemorySize64);
}
```

Called every 1 000 ms for up to 130 s → ~130 undisposed `Process` objects. Each holds a `SafeProcessHandle`. This is test-only code, but if someone reuses the pattern in production it's a handle leak.

**Fix:** cache the `Process` object in a field, or use `Environment.WorkingSet` / `GC.GetTotalMemory` instead.

## Minor

**9. BallRenderer.FreeCanvas: order of operations is correct but fragile**

```csharp
_canvas?.Dispose();   // GDI+ releases its reference to _bits
Native.SelectObject(_memDc, _oldBmp);
Native.DeleteObject(_dib);   // frees the DIB section (and _bits)
Native.DeleteDC(_memDc);
```

If `_canvas.Dispose()` throws (it shouldn't, but GDI+ can throw on corrupted state), `_dib` and `_memDc` leak. Wrap in try/finally or at least null-check after dispose.

**10. Bubble: WS_EX_TRANSPARENT means the bubble is fully click-through**

This is correct for a notification, but the design doc says the bubble shows error messages like "please select text first". A user with an eye condition who wants to re-read the message cannot hover to keep it visible or right-click to copy. Consider removing `WS_EX_TRANSPARENT` and adding a small "×" or auto-dismiss on click.

**11. BallRenderer: `_canvas` and `_memDc` are not thread
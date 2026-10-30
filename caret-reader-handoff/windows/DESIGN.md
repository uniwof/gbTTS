# CaretReader (光标朗读) v0.4 — Design notes

> v0.4 (voice packs, + import, speaker preview, selection reading) is described in §14; §1–§13 are the v0.2/v0.3 notes and still apply except where §14 says otherwise (voices.json/models\ were replaced by voices\<pack>\voice.json).

Status: v0.2 (pause/resume + round-2 review fixes, see §12) of the Windows 10/11 x64 prototype. It was cross-compiled on Linux and has **not yet been run on Windows**. The core pipeline (text splitting, pauses, sherpa-onnx synthesis, streaming, cancel, model switching) was exercised on Linux through a console harness, using the linux-x64 natives from the same NuGet package.

## 1. Goal and hard requirements (from the user, a programming beginner)

- A tiny, offline Windows app with a horizontal bar UI: voice dropdown, Start/Stop, Settings, and a model-loading progress bar. UI text is Chinese.
- Two global hotkeys (v0.2, user-approved). **Main** (default `Ctrl+Alt+R`): start → pause → resume from the paused spot → pause … **Stop** (default `Ctrl+Alt+S`): stop completely and reset; the next main press re-grabs from the caret. Both are configurable and persisted.
- Caret text comes from UI Automation first, with a clipboard fallback. The method used is logged.
- Pacing comes from **pauses at punctuation**, not from slowing the whole voice. Each clause is synthesized at speed 1.0, followed by silence: ，、 0.15 s / 。！？ 0.5 s / ； 0.3 s / ： 0.25 s / newline = 。. All pause values and the speed are adjustable and persisted.
- Streaming: the first sound should come within about 1 s, playback should be smooth, and Stop cancels everything.
- Standard edition: 7 voices from 3 models, with only **one model in RAM at a time** (target ≤ ~360 MB, for 4 GB PCs). Lite edition: Matcha only. One codebase builds both.
- `num_threads` is configurable (default 2). No network calls. Models live in `models\` next to the exe, and a clear Chinese error appears if one is missing.
- UI style: Japanese minimalist, black, white and greys only, with an ink/print feel (see §7).

## 2. Solution layout

```
app/
  src/CaretReader.Core/     net8.0, cross-platform. No UI code.
    TextSplitter.cs         clause splitting + pause assignment + length control + engine-text normalisation
    AppSettings.cs          settings.json model, lenient load, atomic save
    VoiceCatalog.cs         voices.json (models[] + voices[]), validation, missing-file check
    TtsEngine.cs            sherpa-onnx OfflineTts wrapper; one model at a time; global lock
    SpeechPipeline.cs       producer/consumer streaming; IAudioSink abstraction; startup buffering
    Log.cs                  tiny rolling log file
  src/CaretReader.App/      net8.0-windows WinForms, win-x64, self-contained single-file
    Program.cs              single-instance mutex, global exception handler
    MainForm.cs             the bar; state machine; hotkey; voice switching; load progress
    SettingsForm.cs         settings dialog (edits a clone; Save/Cancel/Reset)
    CaretTextGrabber.cs     UIA (COM) primary, clipboard fallback
    NAudioSink.cs           IAudioSink over NAudio WaveOutEvent + BufferedWaveProvider
    Hotkey.cs               parse/format "Ctrl+Alt+R", RegisterHotKey wrapper
    Native.cs               P/Invoke (RegisterHotKey, SendInput, clipboard seq no., etc.)
    Ui/Ink.cs, Ui/InkControls.cs   owner-drawn monochrome controls
  tests/CaretReader.TestHarness/   console: `split`, `dump`, `synth`, `switch`
  config/voices.standard.json, voices.lite.json
  build.sh                  publish + stage + zip both editions
```

## 3. Key decisions

| Decision | Why |
|---|---|
| **WinForms on .NET 8, self-contained single-file** | The user installs nothing. WinForms is the smallest .NET desktop stack and cross-compiles from Linux with `EnableWindowsTargeting`. Trimming isn't supported for WinForms, so instead the unused **WPF parts of the WindowsDesktop runtime pack are stripped** (both `RuntimePackAsset` → clean deps.json, and `ResolvedFileToPublish`). The list was confirmed by a static reference-closure scan of the publish set. Result: exe 62.8 MB → **43.8 MB** (compressed bundle). An escape hatch keeps everything: `-p:KeepAllFrameworkFiles=true`. |
| Native DLLs next to the exe (`IncludeNativeLibrariesForSelfExtract=false`) | Avoids extracting to %TEMP% on every start, and is less likely to trip AV heuristics. |
| **sherpa-onnx 1.13.8 C# NuGet** (`org.k2fsa.sherpa.onnx`) | Ships win-x64 `onnxruntime.dll` (17.8 MB) and `sherpa-onnx-c-api.dll` (4.6 MB). It supports both VITS and Matcha, and is the same engine the user's approved demos used. |
| **UIA via COM** (`Interop.UIAutomationClient` 10.19041) rather than managed `System.Windows.Automation` | The COM client is the modern UIA client and handles Word and other modern controls better. It also avoids pulling in WPF (`UIAutomationClient.dll` belongs to WPF, which we strip). |
| **NAudio.WinMM `WaveOutEvent`** | About 250 KB total, works on any Windows sound device, needs no sync context, and is thread-agnostic. `BufferedWaveProvider` with `ReadFully=true`, so an empty buffer plays silence instead of stopping. That means an underrun can only lengthen a pause at a clause boundary, never cut a word. |
| 16-bit PCM output, model-native sample rate | Universally supported. A new WaveOutEvent is created per reading session at the model's rate (Melo 44.1 k, Matcha 22.05 k, zh-ll and fanchen 16 k), so there is no resampling. |
| `voices.json` drives editions | Standard and Lite are the same binary. Only `voices.json` and `models\` differ, and display names can be edited in Notepad. |

## 4. Text pipeline

1. **Punctuation split** (`TextSplitter.SplitAtPunctuation`): a single pass over the text. Pause marks: ，、,—— → Comma; 。！？.!?…… → Period; ；; → Semicolon; ：: → Colon; newline (runs collapsed) → Newline. Runs of punctuation and closing quotes/brackets are merged into the previous clause, and the pause is the **max** of the run. A clause with nothing speakable is dropped and its pause is merged into the previous clause.
   - Guards: `. , : ， ： ．` between digits (ASCII or full-width) are not split points. URLs (`https?://…`, `www.…`) are masked, with trailing sentence punctuation excluded from the mask. `.` followed by a letter or digit, or after a known abbreviation (Mr, Dr, e.g, i.e, etc, vs…), is not a split point. An ASCII `"` attaches to the previous clause only when it closes an open quote. `·` (as in 马克·吐温) does not pause.
2. **Length control** (`SplitLong` / `CutOnce`):
   - The **first clause** is cut to ≤ 20 chars, and each later clause to ≤ 36. This bounds both first-audio latency and the worst-case wait after Stop, because synthesis holds the engine lock and cannot be interrupted mid-clause.
   - **v0.2 forced cut:** if no safe cut point exists, the text is hard-cut at the limit (stepping back only to avoid splitting a surrogate pair). The first clause may extend to 36 chars only to keep an unbreakable token (e.g. a short URL) whole; beyond that it is hard-cut at 20. `SpeechPipeline` additionally refuses to send more than `HardMaxClauseChars = 80` chars to the engine in one call (last guard). Before v0.2 an unbreakable string became one clause of unbounded size (a 1,200-char clause measured 63 s / 1.5 GB).
   - Cut points are scored: space 3 > after a particle (的了着过和与及或在是就也都而并但把被从向对给让) 2 > CJK↔Latin boundary 1 > any CJK gap 0. The cut is never made inside a surrogate pair, a Latin word or number (ASCII or full-width), a number with separators, a URL, before a closer, or before a combining mark. Each piece is at least 4 chars, and the search range starts at 8. If no safe point exists, see the forced cut above.
   - Only the last piece carries the original punctuation pause. Inner cuts get no extra silence.
0. **Unreadable-token normalisation (v0.2, `NormalizeUnreadable`, runs first):** URLs longer than 36 chars → "链接" (trailing sentence punctuation kept); a ≥ 16-char alphanumeric run that is hex with a digit, or has ≥ 3 digits, ≥ 3 letters and ≥ 4 letter/digit transitions (IDs, Base64, serials) → "一串字符"; ≥ 16 pure digits → "一串数字"; a single letter run ≥ 32 → "一串字符". Ordinary words, short URLs and hyphenated English are unchanged.
3. **Engine text** (`TextForEngine`): `；：;:` become a comma, **except** between digits (10:30 and 10：30 stay as they are and go to the model's own number FSTs) and inside URLs. This is done because some lexicons (Melo) treat `；：` as OOV. Pause length is unaffected.

A test harness covers 61 splitter/settings assertions (+5 in `pause`) plus a 45-case corpus (round-1 cases + round-2 long-string cases). Output is in `tests/splitter_output_v3.txt`.

## 5. Streaming and cancellation (`SpeechPipeline`)

- There is one LongRunning producer thread per reading session. It synthesizes clause *k*, appends `pause × sr` zeros, writes to the sink, and then immediately synthesizes *k+1* while *k* plays. Back-pressure: the producer sleeps while `sink.BufferedSeconds > 8 s`.
- **Startup buffering (only when needed):** after clause 1 is synthesized, the time to synthesize clause 2 is forecast as `chars₂ × (speech-seconds per char so far) × RTF so far`. If clause 1's audio covers that forecast plus 0.1 s, playback starts at once. Otherwise chunks are held until they do, and a timer **forces playback at 1.0 s** at the latest, even while the producer is busy in `Generate`.
  - Measured on the sandbox with Melo, where the first clause is the 3-char "雨停了，": first audio at 1.02 s with 0 gaps. Without buffering it was 0.45 s with one 0.1–0.55 s gap.
  - Fast models (Matcha) start at ~0.1 s.
- **Stop:** `CancellationToken.Register(() => sink.Stop())` makes the sound stop synchronously (measured 0–5 ms in the harness). The producer finishes whichever clause it is inside (≤ ~1 s for a 36-char clause on Melo), discards it, and exits. `MainForm` awaits that task before any `Load` or `Unload`, so a model is never disposed under an active `Generate`.
- Session counter: a stale completion (for example, the hotkey pressed again quickly) never overwrites the UI state of a newer session.
- **Pause / resume (v0.2):** handled entirely in the sink. `NAudioSink.Pause()` → `WaveOutEvent.Pause()`; `Resume()` → `Play()`. The `BufferedWaveProvider` keeps its content, so playback resumes exactly where it stopped. Because the device no longer drains the buffer, `BufferedSeconds` rises to the 8 s back-pressure limit and the producer sleeps (measured: ≤ 8.2 s ahead, 0 samples lost). `IsDrained` returns false while paused so the session cannot "finish" with the last ~0.2 s still queued in the device. A pause pressed during the grab is remembered and applied as soon as the sink exists (the sink starts paused).
- **Clause queue (v0.2):** `RunAsync` takes a `ClauseQueue` that can grow while reading. The UIA grab reads the first ~2,000 chars, cut back to the last 。！？!?/newline, speaks those immediately, and reads/splits the rest in the background (`firstChunk=false`, no 20-char first-clause cut) before appending and `Complete()`. The startup buffer peeks the next clause without blocking (assumes 36 chars if not yet known).
- **Latency measured from the hotkey (v0.2):** `WM_HOTKEY` takes a `Stopwatch` timestamp that is passed to the pipeline. The 1 s startup budget is `max(0.3 s, 1.0 s − time already spent grabbing)`, and the log records hotkey→sound plus the grab share.
- The start timer is disposed in `finally`; a faulted pipeline task returns a `PipelineStats` with `Error` instead of throwing; `MainForm` awaits it in try/finally so the UI always returns to Idle.

## 6. Engine and memory (`TtsEngine`)

- A single `OfflineTts` instance sits behind one lock, held by `Load`, `Synthesize` and `Unload`. `MaxNumSentences = 1`, because we feed one clause at a time. The model's `RuleFsts` are date/phone/number (+ `new_heteronym` where present).
- **Switching to a voice in another model:** missing files are checked first (a Chinese error lists them), then the **old model is disposed before the new one loads**. Peak RAM therefore stays at about one model, at the cost of being left with no voice if the new load fails. That failure is shown in the UI. Switching between speakers of the same model costs nothing.
- A warm-up `Generate("你好")` runs after load.
- Load progress: sherpa exposes no progress callback, so the bar is an estimate, `1 − e^(−t / (0.6 × approxLoadSeconds))`, capped at 95 %, then jumps to 100 % on completion. `approxLoadSeconds` is set per model in voices.json.

## 7. UI and UX

- **Style (user requirement):** Japanese minimalist, black, white and greys only (R=G=B everywhere), with a manga/print texture.
  - Paper-white background (#FAFAFA) with a faint screentone dot pattern: a staggered 6-px grid in #E2E2E2, made with a generated `TextureBrush`.
  - Crisp 1-px #111 borders and flat fills. No gradients or shadows. Apple-style continuous-curvature rounded corners on buttons, inputs and the windows (user request 2026-10-13; `Ink.Round`, `Ink.RoundBox`, `Ink.RoundWindow`).
  - An 8-px black ink bar on the left edge doubles as the drag handle. A thin grey rule separates the two rows.
  - Buttons: white with a black border; hover is a medium screentone; pressed or active (reading = "停止") is inverted black with white text; disabled is grey.
  - Dropdown: an owner-drawn button plus a `ContextMenuStrip` with a custom `ToolStripRenderer` (white, 1-px black border, selected item inverted, ■/□ marks the current voice).
  - The progress bar is 8 px tall, with a black border and black fill. The checkbox is a square with a filled inner square.
  - Font: Microsoft YaHei UI → Yu Gothic UI → JhengHei UI → Segoe UI. Everything sits on an 8-px grid and is scaled manually by DPI (`SystemAware`; `Ink.S(px)`).
- **Bar window** (456 × 80 dip): `WS_EX_NOACTIVATE` + `WM_MOUSEACTIVATE → MA_NOACTIVATE`, so clicking Start or the dropdown never steals focus from Notepad or Word, and the caret stays where it was. `WS_EX_APPWINDOW` keeps it in the taskbar. TopMost is optional. The position is saved and validated against the current screens.
- **States:** NoModel / Loading / Idle / Grabbing / Reading / Paused. Main key: Idle → Grabbing → Reading ⇄ Paused (Grabbing: toggles a pause request). Stop key: Grabbing/Reading/Paused → Idle. During Loading it shows a hint instead. The start button reads 开始 / 暂停 / 继续 (inverted while reading). The status line shows both hotkeys ("就绪 · Ctrl+Alt+R 朗读 · Ctrl+Alt+S 停止", "已暂停 · Ctrl+Alt+R 继续 · …"); it spans the full row and shrinks only while the progress bar is visible.
- **Settings:** opening the dialog stops reading (otherwise it could not be stopped while the hotkeys are unregistered) and unregisters both hotkeys; they are re-registered on close and focus is handed back to the last external window. Two `HotkeyBox`es (main / stop). **Deny-list** (`Hotkey.Problem`): Ctrl+C/V/X/Z/Y/A/S/Insert, Alt+F4/Tab/Esc/Space/Enter, Ctrl+Esc, Ctrl+Alt+Del/Esc/Tab, Ctrl+Shift+Esc, Ctrl/Shift + editing/navigation keys (End, Home, arrows, PgUp/PgDn, Del, Back, Enter, Tab, Space — covers the keys the clipboard fallback injects), letter/digit/symbol keys with fewer than two modifiers; main == stop is rejected. Problems are shown inline in Chinese and Save is refused. If `RegisterHotKey` fails, a Chinese message names the key, the previous key is restored, and **the failed key is not saved** (settings.json stores the key actually registered). At startup a failed key is not overwritten, so it is retried next launch. Changing the thread count reloads the model (Unload + Load both on a worker thread). Tab now moves focus out of a HotkeyBox.
- **settings.json:** stored next to the exe if that folder is writable, otherwise in `%AppData%\CaretReader\`. v0.2 adds `StopHotkey` (default `Ctrl+Alt+S`), covered by the same per-field recovery; a v0.1 file without it gets the default. Loading is lenient: an invalid field resets only that field, the file is copied to `settings.json.bad`, and the problem is logged. Saving is atomic (write to tmp, then move). `log.txt` sits beside it and rolls over at 1 MB.

## 8. Caret text acquisition (`CaretTextGrabber`)

0. **One grab at a time (v0.2):** `SemaphoreSlim _gate` wraps the whole grab, so a new grab waits for a previous clipboard routine instead of interleaving backup/SendInput/restore.
1. **UIA** runs on a thread-pool (MTA) thread with a **1.5 s** timeout (was 2.5 s), so a hung target can't freeze the UI.
   - Steps: `GetFocusedElement` (rejected if it belongs to our own process), then up to 3 ancestors are tried. For each: `TextPattern.GetSelection()[0]`, or failing that `TextPattern2.GetCaretRange`; clone the range, `MoveEndpointByRange(End, DocumentRange, End)`.
   - **Two-step read (v0.2):** `GetText(2000)` first. If exactly 2,000 chars came back, the text is cut at the last sentence end (if ≥ 200 chars in) and reading starts; a `Rest` callback later re-reads `GetText(100 000)` on a worker thread and appends whatever follows the already-spoken prefix (if the document changed so the prefix no longer matches, only the first part is read and this is logged).
   - The focused element's ControlType and IsKeyboardFocusable are recorded for the fallback decision.
   - Word control characters are cleaned (`\v \a \f` → newline; U+FFFC and other control characters dropped). With a non-empty selection, reading starts at the selection start.
2. **Clipboard fallback** runs on the UI (STA) thread, **only in text editors (v0.2)**.
   - Refused when the foreground/focus window class or process is a console/terminal/shell (ConsoleWindowClass, CASCADIA_HOSTING_WINDOW_CLASS, PseudoConsoleWindow, mintty, PuTTY, ConEmu, CabinetWClass/ExploreWClass, Progman/WorkerW, Shell_TrayWnd, SysListView32/SysTreeView32/DirectUIHWND; cmd, powershell, pwsh, WindowsTerminal, OpenConsole, conhost, explorer, …). Allowed only if UIA reported a keyboard-focusable Edit/Document, or the focus class is a known edit control (Edit, RichEdit20/50/60, RichEditD2DPT, _WwG, Scintilla), or the process is a known editor (notepad, winword, wordpad, wps, notepad++, sublime_text). Otherwise the bar shows "这个窗口不支持朗读".
   - Steps: wait for modifiers to be released (up to 1.5 s); back up **only** UnicodeText/Text/RTF/HTML/FileDrop/Bitmap (text formats > 8 M chars skipped; skipped formats logged) — if the backup throws while the clipboard is non-empty, the fallback is abandoned rather than destroying the user's clipboard; SendInput Ctrl+Shift+End, then Ctrl+C; poll `GetClipboardSequenceNumber` (up to **2 s**); read Unicode text (truncated to 100 000 chars); press **Left** — v0.2: **always**, except when the copy succeeded with empty text (proof that nothing was selected, so Left would move the caret); restore the clipboard; re-check the sequence number 300 ms later and restore again if a late copy overwrote it.
   - **Clipboard history:** the restored DataObject carries `ExcludeClipboardContentFromMonitorProcessing`, `CanIncludeInClipboardHistory = 0` and `CanUploadToCloudClipboard = 0`, so the restore itself adds no Win+V/cloud entry. The copy performed by the target app can still be recorded by Windows; deleting it needs WinRT `Clipboard.DeleteItemFromHistory`, which was rejected (see §12). The README tells the user.
   - Once started, this sequence is deliberately not cancellable.
3. The method, text length, grab time and detail (target class names, process name, control type, pid, depth, or failure reason) are written to `log.txt` — never the text itself.

## 9. Test results (Linux sandbox, 2 vCPU shared, num_threads = 2)

Command: `dotnet tests/bin/CaretReader.TestHarness.dll synth <voices.json> <voice> out.wav`. The test text (126 chars) mixes ，。；：！？, a newline, a blank line, a date, 3.14, 1,000 and English. The sink is a simulated real-time player that records gaps.

| Voice | Load | RTF | First audio | Gaps | Peak RSS (VmHWM) |
|---|---|---|---|---|---|
| Melo (44.1 k) | 2.7 s | 0.54–0.57 | 1.02 s | 0 | 358 MB |
| zh-ll #2 / #3 (16 k) | 0.7 s | 0.38–0.49 | 0.36–1.0 s | 0 | 249–253 MB |
| fanchen-C #29 (16 k) | 1.1 s | 0.50–0.54 | ≤1.0 s | 0 | 266–275 MB |
| Matcha + vocos (22.05 k) | 1.5 s | 0.10 | 0.10 s | 0 | 248–261 MB |

- Note: the sandbox CPU is shared. One run made while another build was compiling showed RTF ≈ 0.9–1.0 for zh-ll/fanchen, with one 0.3–0.7 s gap. The numbers above are best case for 2 vCPUs; real low-end PCs must be measured.
- **Model switching across all 7 voices** in one process (`switch`): peak 368 MB (while Melo was loaded). After switching to zh-ll, RSS dropped to 310 MB, which confirms disposal. Same-model speaker switches take 0 s.
- **Stop:** sound stops 0–5 ms after Cancel, and the producer exits 15–160 ms after.
- Output WAVs (proof of clause split + silences): `tests/run/test_melo.wav`, `test_matcha.wav`, `test_zhll2.wav`, `test_zhll3.wav`, `test_fanchen29.wav`.
- The packaged `voices.json` and model sets were validated by running the harness against `stage/<edition>/光标朗读/voices.json`.
- Matcha ignores English (character lexicon) and logs "Unknown token shei2" warnings from its lexicon. Both are harmless and known.

**v0.2 additions (same sandbox, CPU shared with another agent's build — absolute RTFs are inflated):**
- `split`: all 61 assertions pass, incl. 4,000-char hex → "一串字符" (2 clauses), 1,828-char URL → "请访问链接。", hyphen chain first clause 20 chars, 200 k random mixed chars truncated to 100 k → 11,188 clauses, max 36 chars, no split surrogates, 24–29 ms.
- `pause` (new): 2 clauses queued, the rest appended 1.5 s later, pause 4 s, resume. Matcha: 48/48 clauses, hotkey→sound 0.38 s (incl. 0.22 s simulated grab), 0 samples lost, 8.21 s synthesized ahead while paused, 0 gaps. zh-ll: 48/48, hotkey→sound 1.01 s (incl. 0.22 s grab), 5.6 s ahead, 0 gaps.
- `synth` Melo regression: peak 358 MB (unchanged), stop 1 ms / producer exits 225 ms; RTF 1.6 under contention (0.55 in v0.1 on an idle CPU, same code path).

## 10. Untested / risks (need a real Windows PC)

1. Nothing in the WinForms layer has been executed: startup, painting, DPI scaling, owner-drawn controls, the settings dialog.
2. The **stripped WPF files**: the static closure scan says they are unused, but this must be confirmed on Windows. The fallback is `-p:KeepAllFrameworkFiles=true` (+~19 MB).
3. **`WS_EX_NOACTIVATE` + `ContextMenuStrip`**: the dropdown menu should open and close correctly on a non-activating window. If not, temporarily activate on dropdown click.
4. **UIA coverage:** classic Notepad (Edit control proxy), Win11 Notepad (RichEditD2DPT), Word (doc pane, possibly TextPattern on an ancestor), WPS, browsers. Range semantics for `GetSelection` on a degenerate caret need checking per app.
5. **Clipboard fallback:** apps where Left doesn't collapse the selection to its start, delayed-render or private clipboard formats that can't be restored, and IME interference.
6. SmartScreen/AV on an unsigned single-file exe.
7. Hotkey conflicts (`Ctrl+Alt+R` may be taken, for example by some GPU tools). A message is shown and the user can rebind.
8. Low-end CPU performance: Melo's RTF on a 2-core laptop may exceed 1, in which case the zh-ll/fanchen voices are recommended. The startup buffer only covers the first two clauses.
9. Peak RAM on Windows may differ from Linux RSS. Measure Melo against the ≤ 360 MB target in Task Manager.
10. v0.2: `WaveOutEvent.Pause()/Play()` after a long pause (device sleep, headset unplugged while paused); whether the clipboard-history exclusion formats are honoured by Win10 21H2+/Win11 and by third-party managers (Ditto); class/process allow-list coverage (WPS class names unverified); two-step UIA read in Word on long documents; focus hand-back after Settings.

## 11. Not done (by design or deferred)

- Win7 / x86 build (planned separately: .NET Framework 4.8 + older onnxruntime).
- No tray icon, autostart, per-app profiles, volume control, or resampling. This follows the user's "no extra features" requirement.
- Voice display names ("女声 1 · 自然 …") are placeholders. They are editable in voices.json.

## 12. v0.2 change log (round-2 review) and rejected items

| Review item | v0.2 |
|---|---|
| P1 unbounded clause | Forced cut (20 / 36), 80-char pipeline guard, long URL → 链接, hex/ID → 一串字符, ≥16 digits → 一串数字 |
| P2 fallback types into any window | Editor-only allow-list + UIA Edit/Document check; consoles/terminals/Explorer/desktop blocked |
| P3 clipboard history leak | Restore marked exclude/no-history/no-cloud; README privacy note. Target app's own copy still recorded (see below) |
| P4 selection left highlighted | Left always sent (except proven-empty copy); 2 s wait; late-copy re-check |
| P5 dangerous hotkeys | Deny-list + ≥2 modifiers for letter keys + main≠stop |
| P6 failed hotkey saved | Only the actually registered key is saved |
| P7 overlapping grabs | `SemaphoreSlim` around the grab |
| P8 latency measured from pipeline | Timestamp at `WM_HOTKEY`, budget reduced by grab time, two-step UIA read, UIA timeout 1.5 s |
| P9 restart waits | "正在停止…" shown while waiting before a model load; clause cap kept at 36 (not lowered) |
| P10.1 split on UI thread | `Task.Run` + 100 k cap |
| P10.2 backup failure clears clipboard | Fallback abandoned if backup fails on a non-empty clipboard |
| P10.3 all formats backed up | Whitelist of 6 formats + size limit |
| P10.4 can't stop while Settings open | Opening Settings stops reading |
| P10.5 focus after dialogs | `SetForegroundWindow(last external)` after Settings and message boxes |
| P10.6 UI blocked by Unload/Dispose | Unload+Load on worker; Dispose on worker with 3 s bound at exit |
| P10.7 English errors | Chinese for DllNotFound / BadImageFormat / EntryPointNotFound / OOM / no audio device / device busy |
| P10.8 start timer leak | Disposed in `finally` |
| P10.9 no try/finally on `_readTask` | try/finally → Idle |

**Rejected / deferred:**
- *Deleting the copied item from clipboard history via WinRT* (`Clipboard.GetHistoryItemsAsync/DeleteItemFromHistory`): needs TFM `net8.0-windows10.0.17763.0`, which pulls the CsWinRT projection (`Microsoft.Windows.SDK.NET.dll`, ~25 MB) into a single-file app whose main goal is small size, and it only works when history is on. Hand-rolled WinRT COM interop was judged too risky to ship untested. Mitigation: editor-only fallback, exclusion flags on restore, README note.
- *Default voice change (B.1)*: left to the user, as instructed.
- *Lowering `MaxClauseChars` to 24–28 (P9)*: not done — would add more inter-clause boundaries for every voice; to be decided after real-PC timing.
- *Setting "允许剪贴板方式" (B.5), earcons / larger font (B.3)*: not added under the "no extra features" rule; flagged for the user.
- *Disabling single-file compression (B.2)*: needs a real low-end PC measurement first.
- *A separate on-bar Stop button*: not requested; mouse users can stop by switching voice or opening Settings. Flagged for the user.

## 13. v0.3 change log (crash fix, 2026-10-13)

**User report (Win11 25H2):** v0.2 standard edition vanished at 52 % of the load progress on first launch, instantly on later launches; `log.txt` only had the start line and the two "hotkey registered" lines. App path: `C:\Users\Oland\Downloads\光标朗读_标准版_win10x64_v0.2\光标朗读\`.

**Root cause (confirmed from sources + Linux repro):** sherpa-onnx's C# wrapper marshals every config path as `[MarshalAs(UnmanagedType.LPStr)]`, i.e. in the Windows ANSI code page (936 / 1252 …), while sherpa-onnx 1.13.8 (`csrc/file-utils.cc`) decodes the bytes as UTF-8 (`ToWideString` → `GetFileAttributesW`). Any non-ASCII character in the path (our own zip puts the app in a folder called `光标朗读`) → `Validate()` fails → `SherpaOnnxCreateOfflineTts` returns `nullptr` → the wrapper doesn't check → `tts.SampleRate` calls `SherpaOnnxOfflineTtsSampleRate(nullptr)` → access violation → process killed, no managed exception, nothing logged. Linux repro: same call with an unreadable path → SIGSEGV (exit 139). The 52 % is just the time-based progress estimate (Melo, ~1.8 s into a 4 s estimate) while `onnxruntime.dll`/`sherpa-onnx-c-api.dll` were being loaded (and virus-scanned) the first time; on later launches the DLLs are cached, so it dies before the bar is painted. Ruled out: VC++ runtime (both DLLs import only KERNEL32/ADVAPI32, static CRT), single-instance mutex (would show a message box and log nothing different), hotkeys (registered fine).

**Fixes:**
1. `NativePaths.ForNative`: CWD is set to the app folder (wide API) and the engine gets relative ASCII paths (`models\vits-melo-tts-zh_en\model.onnx`); fallback 8.3 short path; then raw path if ACP = UTF-8; else a Chinese error dialog instead of a crash.
2. `app.manifest` with `activeCodePage = UTF-8` (Win10 1903+), so LPStr marshals UTF-8, which is what sherpa-onnx expects.
3. `TtsEngine`: reflection check that `OfflineTts._handle` is non-zero before any call (field verified on 1.13.8 by `tests … nullguard`); null `GeneratedAudio` guard.
4. Diagnostics: log initialised first in `Main`; environment line (OS, code page, RAM, AVX, path ASCII or not); `[步骤]` line + `run_state.txt` before each load stage; on the next start an unclean previous run is logged with its last step and the matching `Application Error` / `.NET Runtime` / `WER` events (wevtutil, XML) are appended to `log.txt`; a dialog is shown only if it died during startup/loading. `TaskScheduler.UnobservedTaskException` handler; full exception in all handlers. Second instance: message now also says how to recover (Task Manager) and is logged.
5. Hidden `CaretReader.exe --selftest`: no UI; loads every model, synthesises one sentence each, writes `selftest.txt` + `selftest_<model>.wav`; exit code 0 = pass.

No UI or feature changes otherwise. Groq (gpt-oss-120b) reviewed the diff in 3 parts (8k TPM limit forced stripping strings/comments, which caused several bogus "does not compile" findings; build is clean). Adopted: no crash dialog when the last step was idle/reading/exiting (Task Manager kill, shutdown). Rejected with reason: "CWD change breaks other components" (Explorer already starts the exe with CWD = its folder; app uses absolute paths elsewhere); "Stage callback on UI thread" (it only writes files under a lock); "-1 fallback hides errors" (deliberate: if the private field ever disappears the app behaves exactly like v0.2 instead of refusing to load). Opus review: not possible (no credit).


## 14. v0.4 change log (voice packs, 2026-10-13)

**Layout.** `<app>\CaretReader.exe`, 2 native DLLs, `edition.txt` (标准版/轻量版, window title + status), `说明.txt`, `voices\<ascii-id>\` (UI name 「音色」). `voices.json` and `models\` are gone.

**Voice pack** (`VoicePack.cs`): folder = `voice.json` + model files + `sample.mp3` (+ optional `icon.png`, reserved for the animal mascots). `voice.json`: `formatVersion, id (suggested ASCII folder name), name, engine (vits|melo|piper|matcha), sid, gain (0.1–5), speed (0.5–2, multiplied by the global speed), description, order, sample, icon, approxLoadSeconds, modelBytes, files{model,tokens,lexicon?,dictDir?,dataDir?,vocoder? (matcha),ruleFsts[]}`. Lenient JSON (comments, trailing commas, numbers as strings, case-insensitive). Validation: engine known, required files exist **inside** the pack (`Inside()` rejects rooted/`..` escapes), model ≥ 1 KB, `modelBytes` matches if given (incomplete unzip), model not open for writing by another process (Explorer still copying), missing rule FSTs only warn. Voice id = folder name (old v0.3 ids `melo-0`, `zhll-2` … kept as folder names, so settings carry over).
- **Shared models:** every pack is self-contained (delete folder = remove exactly one voice). `ModelKey` = engine + sizes + SHA-256 of 1 MB head/middle/tail → `TtsEngine.IsLoaded/Load` reuse the loaded engine across packs with the same model (instant switch, measured 0.00 s).
- **Scan** (`VoiceLibrary.Scan`): at startup with `tidy` (moves a pack nested one level deeper up into `voices\`, renames non-ASCII folders to ASCII ids) and on every dropdown open without tidy (new folders appear without restart). Broken packs → `log.txt` line, never a crash. Sorted by `order`, then name. The currently loaded pack stays in the list even if its folder was deleted.
- **Native paths:** unchanged v0.3 mechanism: CWD = app dir, engine gets `voices\<id>\file` (ASCII because folder names are forced ASCII), 8.3/UTF-8 fallbacks, null-handle guard.

**Import** (`VoiceImporter.cs`, 「+」 button): FolderBrowserDialog (Unicode, any drive) → find packs (selected folder, or 2 levels down) → validate → free-space check → copy to `voices\.import-xxxx` (managed Unicode file APIs, so Chinese source paths are fine) → re-validate → rename to unique ASCII name (`id`, then folder name, then `voice-<hash>`; Windows reserved names avoided). Duplicate (same ModelKey + sid) → skipped. Files ≥ 1 MB identical (size + SHA-256) to a file already in `voices` → NTFS hard link (saves 120–130 MB per extra zh-ll/fanchen pack), falls back to copy (FAT32/exFAT). Reparse points skipped. Leftover temp folders deleted at next start. Read-only app folder → Chinese message. If the app had no voices, the first imported voice is selected and loaded.

**Preview** (`SamplePlayer.cs`): speaker glyph at the right of each menu item (owner-drawn in `VoiceMenuItem.OnPaint`; grey without waves when the pack has no sample; inverted round background while playing). Click on the speaker area → `Closing` is cancelled so the menu stays open; click again → stop. MP3 decoded with **NLayer 3.0.0** (managed, 77 KB). NLayer 1.16 was tried first and **crashed on 16/22.05 kHz MPEG-2 files** (IndexOutOfRange in LayerIIIDecoder); 3.0.0 decodes all 12 samples, bit-identical to ffmpeg (corr 0.99999999, only encoder delay). Decode capped at 30 s; WAV also accepted. Preview pauses an active reading; resuming stops the preview.

**Selection reading** (`CaretTextGrabber`): UIA `GetSelection()`; if any range has non-blank text → read only that (up to 64 ranges, 100k chars), `Result.Selection = true`, no `Rest`, the queue completes → stops at the end. Clipboard fallback: first a plain Ctrl+C probe (600 ms); non-blank → selection (no Left key, selection preserved, clipboard restored with late-copy check); skipped in editors that copy the whole line when nothing is selected (Scintilla/Notepad++, VS Code, Sublime, JetBrains). Bar already non-activating (WS_EX_NOACTIVATE + MA_NOACTIVATE) so clicking Start keeps the selection. Status shows 「朗读选中」.

**Loudness:** gains measured with ffmpeg ebur128 on the unified sample text and set to reach ≈ −24 LUFS (cap 5): melo 1.84, matcha 0.78, zhll-0 1.27 / 1 1.46 / 2 1.2 / 3 0.94 / 4 1.4, fanchen-29 5 / 7 1.78 / 24 2.75 / 119 3.8, chaowen 0.52 (`config/gains.json`). `SpeechPipeline.ApplyGain` soft-clips above 0.9 (tanh knee). After gain all voices measure −23.4…−25.9 dB RMS.

**Samples:** unified text 「你好，欢迎使用光标朗读。把光标放在文章里，按下快捷键，我就从这里开始，一句一句读给你听；读累了，随时可以暂停。」 synthesized by the real pipeline (TextSplitter pauses ，、0.15 。！？0.5 ；0.3 ：0.25, gain), 0.15 s lead-in, ffmpeg `loudnorm=I=-24:TP=-2`, LAME VBR q4 at the model's native rate (50–93 KB each). Stored in `config/samples/`.

**Editions:** 标准版 = melo-0 + zhll-1; 轻量版 = matcha-0 + chaowen-0. Default voice logic unchanged (saved VoiceId, else first by `order` → the female voice). Pack zips: all 12 voices, each `音色_<label>_<id>.zip` containing one ASCII folder.

**Measured (sandbox, 2 vCPU shared, 2 threads):** 12/12 packs load and synthesize through `CaretReader.Core`; RTF melo 0.54, matcha 0.10, zh-ll 0.37–0.43, fanchen 0.43–0.52, chaowen 0.08–0.14; peak RSS across all switches 372 MB (Melo). Lite zip 163.9 MB → 220.4 MB (+56.5 MB for chaowen). Standard zip 425 MB (v0.3, 7 voices) → 313 MB.

**Tests:** `packtest` 35 assertions (validation, scan/tidy, import from Chinese/nested paths, duplicates, name collisions, ModelKey, reserved names, modelBytes); `split` 59 assertions (the 2 old voices.json checks were removed), all pass. `tests/mp3check` (NLayer decode check).

**Review:** Groq gpt-oss-120b, 5 parts + rebuttal round (`review/win_v04/`). Adopted: 1 MB×3 ModelKey, modelBytes + still-being-written check, reserved names / bounded UniqueDir, skip reparse points, IsDisposed guard in import progress, 30 s decode cap, 64 selection ranges. Rejected with reasons (rebuttal sent, reviewer's counter-arguments checked against code): `..` traversal (already blocked), remove CWD change (would re-introduce v0.3 crash), TryLoad semantics/CreateNew/hard-link fallback/null compare/GDI leak/Progress marshalling (misreadings), %LOCALAPPDATA% voices (non-ASCII user names), FileSystemWatcher/background scan (scan is ~ms), drop hard links (saves 0.5–0.9 GB), symlinks (need admin), speaker in menu item (user requirement). Opus review not done (no credit; no new accounts per user rule).

**Not verified (no Windows):** everything in the WinForms layer: menu speaker hit-testing and Closing-cancel on the non-activating window, FolderBrowserDialog ownership/focus return, NLayer+WaveOutEvent playback, UIA selection in Notepad/Word/browsers, the Ctrl+C probe, hard links on NTFS, DPI.

## 15. v0.5 — polyphone frontend ("lexicon A", user-approved 2026-10-14)

Only change in v0.5. Everything else (UI, hotkeys, voices, no auto model selection) is v0.4.

- **`CaretReader.Core/PolyphoneFrontend.cs`** — C# port of the experiment frontend `work/polylex/newfront.py`:
  user dict (`用户自定义词库.txt`, highest) > merged lexicon A (`frontend/polylex_a.txt`, 276,693 polyphone words, MIT/Apache sources + `frontend/lexicon_fixes.txt`, 35 manual fixes incl. 总行) > pypinyin phrases/chars + the old context rules (hfront.rules, ported line by line, including the Python quirk that `'' in '...'` is True at sentence start). Segmentation = port of jieba 0.42.1 (DAG + max route + HMM) over `frontend/dict.txt` (jieba main dict + lexicon words). Sandhi (一/不/3-3) = port of inject.sandhi. PaddleSpeech neutral-tone list not enabled. Supplementary-plane chars (pinyin-data supplement, 2,504 of the 2,512) get their single-char reading in `ToEngineText`.
  - Verified byte-identical to the Python experiment on 147k hanzi (workspace corpus + 4,000 random lines): `work/polylex/integ/`.
- **How readings reach sherpa-onnx without changing it:** each voice pack's `lexicon.txt` gets ~2,000 extra lines, one Private-Use-Area char per toned syllable (`frontend/make_pua.py`, same algorithm as the clarity experiment, plus a tokens.txt validity check — Matcha neutral tone = bare syllable). `pua_map.txt` maps pinyin → PUA char; `voice.json files.puaMap` points to it. `TtsEngine.Synthesize` runs `ToEngineText` when the pack has a map and the frontend is loaded; otherwise the old raw text path (old packs keep working).
- **Loading:** `PolyphoneFrontend.StartLoading(appDir, settingsDir)` in `Program.Main` (background, ~0.7 s, warm-up + GC), `Shared()` waits ≤5 s on first use; missing/broken data → logged, old behaviour.
- **About:** Settings → 「关于 / 第三方许可」 (`AboutForm.cs`) shows `THIRD_PARTY_NOTICES.txt` (file next to exe, embedded copy as fallback); license texts in `licenses\`.
- **Data build:** `frontend/build_data.py` (run by build.sh step 1b) → `frontend/data/*.txt`; packs.py adds the PUA lexicon/map per model (cached in `pk/pua/`).
- **Tests (sandbox, 2 cores, heavily shared CPU):** split 59/59, packtest 35/35, paths 8/8, nullguard ok (crash_v02 still segfaults by design — it reproduces the v0.2 bug), pause ok, switch ok, `frontendtest` 12/12. Polyphone errors 1/5/8 = 14 (experiment-equivalent data: 1/5/9 = 15). Frontend alone: peak 118–127 MB process (≈100 MB data), 3–4 ms/1000 chars. App peak: Melo ≈ 476–493 MB, Matcha ≈ 350–363, zh-ll ≈ 344–351, chaowen ≈ 325–331 (+≈100–130 MB vs v0.4). Same-process A/B (`abcpu`): CPU per second of speech equal within ±4 % for all four built-ins.
- Harness additions: `g2p`, `fbench`, `frontendtest`, `abcpu`; env `CR_APPDIR`, `CR_NOFRONTEND`, `CR_WAITFRONTEND`, `CR_LOGCONSOLE`, `CR_ABCHARS`.

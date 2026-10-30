# gbTTS 进度与线间接口

（各线只改自己的小节；新接口先写在这里再用。）

## C 线（第 2 轮：节奏统一 + 转换脚本，分支 pace/）

### 已提供的接口（CaretReader.Core.Pacing，PR pace/trim-cpm）
- 默认开启，界面不改也生效：`SpeechPipeline` 每句合成后去首尾静音；停顿 = 标点表秒数 × 本音色停顿倍率（≤3 秒）；长句无标点切开处固定 0.04 秒。
- 语速：`Pacer.Current.ResolveSpeed(packId, 旧speed)`。音色 cpm 来源优先级：voice.json `pacing.cpmAtSpeed1` → data/bench.json 固定句子测的 → 真实朗读累计满 200 字（自动，不额外占 CPU）。不知道 cpm 前沿用旧语速倍数。
- 首次启用时旧「语速倍数」迁移：倍数≈1 → 250 字/分；否则 倍数×cpm（取整到 10，150～400）。
- 设置存储（升级不丢，和 settings.json 同目录的 data\）：
  - `data/pacing.json`：`{"targetCpm":250,"voices":{"chaowen-0":{"speed":1.05,"pause":1.2}}}`
  - `data/bench.json`：每个音色的 cpmAtSpeed1。
- 去静音参数：`rules/pacing.tsv`（错行跳过）；音色包 `pacing.trimDb` 可覆盖相对阈值。
- 关掉全部新行为：`Pacer.Enabled = false`。

### 给 A 线设置页的约定（界面归 A 线，C 线不改 SettingsForm/MainForm）
- 「语速：字/分」：数字框 150～400 步长 10，读写 `Pacer.Current.Store.TargetCpm`（null 时显示 250），保存时 `Store.Save()`。原来的「语速倍数」框可以隐藏。
- 「本音色微调」：语速 0.8～1.2（显示为 −20%～+20%）、停顿 0.5～2.0；读 `Store.GetOverride(voiceId)`，写 `Store.SetOverride(voiceId, o)`；「恢复跟随全局」= `SetOverride(voiceId, null)`。状态文字：`IsDefault` ? "跟随全局" : "已单独调整"。
- 失真提示：`PacingResolver.IsClamped(target, Pacer.Current.CpmFor(id) ?? 0, o.Speed)` 为 true 时显示「此音色在该语速下可能失真」。
- 第 3 轮测速排序可直接用 `PaceCalibrator.Measure(t => engine.Synthesize(t, sid, 1f), sr, Pacer.Current.Trim)` 和 `Pacer.Current.RecordFixed(id, r)`。

### 自动检查
- xUnit `PacingTests`：去静音首尾 ≤ 阈值、字/分换算、微调设置中文路径保存 + 升级不丢 + 坏文件跳过、pacing.tsv 错行跳过。
- CI `TestHarness pace <音色包>`（build.ps1 第 4b 步，清朗）：中文路径加载合成、真实模型去静音（沙盒实测 190/174ms → 30/50ms）、目标 200/250/320 字/分实测误差 ≤ ±8%（沙盒 +3.4/+1.5/−2.9%）。多个音色时再比彼此误差。

### 转换脚本（PR pace/onnx2voicepack）
- `caret-reader-handoff/windows/tools/onnx2voicepack.py <朋友文件夹> <输出> --name 名字`；依赖 onnx/numpy/pypinyin。
- CI：官方 chaowen onnx 去掉元数据 → 转换到中文路径 → 和清朗一起跑 pace（sherpa 加载 + 跨音色 ±8%）；.ckpt 文件夹必须退出码 2。
- 沙盒实测：转换出的 tokens 与官方完全一致，sherpa 1.13.8 可加载，统一 250 字/分后两音色相差 0.4%。

### 下一步
- CI 再加一个不同快慢的真实音色（如 fanchen 某 sid）凑满 3 个音色。
- 第 1 轮 v0.7-r1 发布后：测试清单_第2轮 + Release v0.7-r2。

## A 线（第 1 轮界面 + 第 3 轮，分支 feat/）

### 第 3 轮已做（PR #23、#24）
- 上一句 / 下一句 Ctrl+Alt+PageUp / PageDown，朗读剪贴板 Ctrl+Alt+C：固定键位，注册在 HotkeyWindow（Hotkey.PrevId/NextId/ClipId），被占用时只提示不影响其它。右键 / 托盘菜单同样有这三项。
- `SpeechPipeline.RunAsync` 新增可选参数 `startIndex`、`tracker`（PlaybackTracker：此刻播到第几句）；原有参数和行为不变。
- 朗读中换音色：记下当前句 → 停 → 加载新音色 → 从这句继续（MainForm.OnVoiceChangedAsync）。
- 本机测速：`Core/VoiceSpeed.cs`；首次用某音色时测一次，结果存 data/speed.json（带电脑名，换电脑作废）。voice.json 新字段 `rank`（小 = 音质好，packs.py 暂取 order）。
- 托盘：v0.6 已有，左键显隐悬浮球，右键菜单即悬浮球右键菜单（含开始/暂停、停止、音色、设置、退出）。

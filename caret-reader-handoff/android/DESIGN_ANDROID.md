# 光标朗读 · 安卓版 v0.1 — 设计与构建说明

## 结果
- `/workspace/work/tts/release/光标朗读_安卓_标准版_v0.1.apk` — 461,241,568 B（≈440 MiB），包名 `app.caretreader.tts`，7 个音色。
  SHA-256 `eb81f5143f3d4adf0a1b19a925da14fa5f4a6062bf21baee84eeb79761174ae3`
- `/workspace/work/tts/release/光标朗读_安卓_轻量版_v0.1.apk` — 176,109,456 B（≈168 MiB），包名 `app.caretreader.tts.lite`，Matcha 1 个音色。
  SHA-256 `b42a104cf82283c2ee1bbe4d2404179969b8bc60dd56e12e46c57369120916bd`
- minSdk 23（Android 6.0），targetSdk/compileSdk 36，ABI arm64-v8a + armeabi-v7a，无任何权限（无 INTERNET）。
- 签名：v1+v2+v3，`keystore/release.p12`（别名 caretreader，密码在 `keystore/keystore.password`，权限 600）。

## 架构（app/src/main/kotlin/app/caretreader/tts/）
| 文件 | 作用 |
|---|---|
| TextSplitter.kt | 逐行移植 Windows 版 TextSplitter.cs（v0.2，md5 ec9894b8…）。纯 Kotlin，可在 JVM 上测试。 |
| SpeechRenderer.kt | 逐句合成 → 变调重采样 → 音量 gain → 16 位 PCM → 插入停顿静音 → 送出。启动缓冲（第一句太短且等一句不超过 1 秒时先攒一句）。80 字硬上限保险。 |
| VoiceCatalog.kt | 读 assets/voices.json（与 Windows 同格式），生成 sherpa-onnx 配置。 |
| Engine.kt | 全进程唯一引擎：同时只加载一个模型（先释放旧的再加载新的），从 APK assets 直接读模型（AssetManager，不复制到存储），加载后预热；Prefs（SharedPreferences）。 |
| CaretTtsService.kt | `TextToSpeechService`：语言（zho；标准版另报 eng 可用）、Voice API（7 个 Voice）、逐句 `SynthesisCallback.audioAvailable` 流式输出、onStop 代号机制、App 的 speechRate/pitch、onDestroy/内存紧张时释放模型。 |
| EngineActivities.kt | CHECK_TTS_DATA / GET_SAMPLE_TEXT。 |
| SettingsActivity.kt | 唯一界面：纯代码构建，黑白灰、细线、网点纸底、圆角按钮。音色、5 个停顿、基础语速、线程数、恢复默认、打开系统语音设置。 |

基于 sherpa-onnx 官方 `android/SherpaOnnxTtsEngine` 示例的结构（服务 + 三个 Activity + tts_engine.xml），去掉了 Compose/AppCompat，界面改成原生 View，APK 代码只有 ~360 KB dex。
原生库与 Kotlin 接口来自官方 `sherpa-onnx-1.13.8.aar`（与 Windows 版 NuGet 同版本），只打包 `libsherpa-onnx-jni.so` + `libonnxruntime.so`（JNI 库只依赖这两个）。

## 与 Windows 版的差异（有意的）
- 语速 = 设置里的基础语速 × App 传来的 speechRate；音高 = App 传来的 pitch（先按 speed/pitch 合成再整体重采样，时长不变）。App 调快语速时停顿同比例缩短。
- fanchen-C #29（女声 5）天生音量小（峰值约 -24 dBFS，Windows 版同样），安卓版在 voices.json 里加了 `"gain": 2.5`。
- 模型的 jieba `dict/` 目录不打包：sherpa-onnx 1.13.x 已不用（Kotlin API 注释 dictDir unused；JVM 实测无 dict 正常），每个模型省 14 MB。

## 构建（不用 Gradle）
`bash build.sh`（或分步：`code` / `test` / `standard|lite [assets|link|pack|align|sign|verify]`）。
工具链在 `/workspace/work/android-tools/`：JDK 17 aarch64、Kotlin 2.4.21、SDK platform 36 + 23、build-tools 36.0.0（aapt2/zipalign/dexdump 是 x86_64，用 `box64root/usr/bin/box64` + `x86lib/libgcc_s.so.1` 运行；沙盒重启后系统里的 box64 会消失，所以放在 workspace 里），R8 去掉没用到的 Kotlin 标准库。
兼容性：代码会再用 Android 6.0 的 android.jar 编译一遍，确保没有调用 API 23 以上的方法。

## 测试（沙盒里做到的）
1. 切分器 JVM 单元测试：56 项断言全部通过（Windows 版全部断言 + v0.2 长串断言 + PCM/重采样）；45 条审查用例输出与 Windows v0.2 的 `splitter_output_v3.txt` 逐字一致。
2. 真实合成（JVM + sherpa-onnx 1.13.8 官方 linux-aarch64 JNI，同版本同源码；assets 是从最终 APK 里解出来的）：7 个音色 + Matcha 全部合成成功，停顿静音长度正确，停止 1 句内生效，输出口报错立即停止，音高/语速生效。结果 WAV：`test-results/wav/`，日志 `test-results/apk_*.log`。
   沙盒 2 核（且与 Windows 构建争 CPU）：加载 Melo 2.6–3.4 s / zh-ll 0.6–2 s / fanchen 0.6–0.7 s / Matcha 2.4–3.1 s；RTF 0.46–0.99（Matcha 0.10–0.17）；进程 RSS 峰值 Melo ≈373 MB、zh-ll ≈295 MB、fanchen ≈318 MB、Matcha ≈247 MB。
3. APK：apksigner 验证 v1/v2/v3 通过；aapt2 badging：minSdk 23、target 36、无权限、ABI 两种；manifest 里服务 TTS_SERVICE + meta-data、三个 Activity 的 intent-filter 正确；zipalign -P 16 检查通过；dexdump 确认所有类在 dex 里。

## 没有测到（需要真机）
- 没有在任何安卓设备或模拟器上运行（沙盒是 ARM64 Linux、无 KVM，官方模拟器没有 linux-aarch64 版本）。所以以下全部未经实测：安装、出现在“文字转语音输出”里、设为默认、微信读书/静读天下调用、界面外观、`AssetManager` 读模型这条路径（JVM 测试走的是文件路径；读 assets 的 C++ 代码是官方示例 App 使用的同一条路径）、手机上的加载时间/RTF/内存、armeabi-v7a 上的速度。
- Android lint 的 Kotlin 源码分析在这套无 Gradle 环境下跑不起来（用 API 23 android.jar 编译代替了 NewApi 检查）。

## v0.2（2026-10-13，依据 review/android_round1/review_summary.md + Phonebox 真机报告）
- 标准版 `光标朗读_安卓_标准版_v0.2.apk` 461,241,568 B，SHA-256 380451cd…4453；轻量版 `光标朗读_安卓_轻量版_v0.2.apk` 176,109,456 B，SHA-256 a04d733a…62a0；versionCode 2 / versionName 0.2；同一签名（证书 SHA-256 4f337334…8764），可覆盖升级。
- 修复：start() 之前出错（如模型加载失败）时补调 done()，阅读 App 不再卡住；空文本/只有标点不加载模型直接完成；onLoadVoice 预加载 App 请求的音色；英文请求默认给 Melo；两个版本都接受 eng（eng-USA = LANG_COUNTRY_AVAILABLE），英文系统语言下系统 TTS 页不再变灰；打开设置页即预加载；新增“试听当前音色”；步进按钮 48×44dp + 读屏描述；版本号读 PackageInfo；线程数上限 = CPU 核数；打开 TTS 设置时多一个“无障碍”兜底。
- 测试：`build.sh test` 全部通过（切分器 + 8 个音色真实合成，log 在 test-results/test_v02.log）；服务层改动（Android 类）无法在 JVM 测，需真机。
- v0.1 源码备份：/workspace/work/tts/backup/android_v0.1_src/

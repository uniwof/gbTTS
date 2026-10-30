# 光标朗读 · Android v0.5 — 设计与功能对齐

目标：功能对齐 Windows v0.5（离线中文朗读：从光标处/选中文字朗读、开始/暂停/继续/停止、音色包+导入+试听、标点停顿、语速、线程、多音字前端 A 版、用户词库、关于/许可）。
同时保留 Android v0.2 已有的「系统 TTS 引擎」（阅读 App 可直接用）。

## 技术栈（沙盒里能真正编出 APK 的方案）
- Kotlin 2.4 + 纯 Android SDK（无 AndroidX/Compose），不用 Gradle：kotlinc → R8 → aapt2 → 打包 → zipalign -P 16 → apksigner v1+v2+v3。
- sherpa-onnx 1.13.8 官方 AAR（与 Windows NuGet 同版本），arm64-v8a + armeabi-v7a。
- minSdk 23（Android 6.0），targetSdk 36。高版本 API 都用 SDK_INT 判断保护；构建时用 API 23 的 android.jar 再编译一遍，所有“API 23 不存在”的调用必须在 tools/api_allow.txt 登记（静态检查）。
- 同一签名（keystore/release.p12），可覆盖升级 v0.1/v0.2。

## 借鉴的同类开源项目（动手前调研）
| 项目 | 借鉴点 |
|---|---|
| k2-fsa/sherpa-onnx `android/SherpaOnnxTtsEngine` | TextToSpeechService 结构（v0.1 起已用） |
| HayaiApp/HayaiTTS（GPL-3） | SAF 导入自定义音色包（.tar.bz2 → 我们用 .zip/文件夹）、音色放 filesDir/voices、单色设计 |
| maxwhipw/marmalade-tts-android（MIT） | 前台播放服务（锁屏继续读）+ 分享文本 + 系统「朗读选中」入口 + 应用内许可页 |
| NachiketaVadera/Speak、T2S | ACTION_PROCESS_TEXT：选中文字菜单里加“朗读” |
| chiara-select2speak、Orion、Google Select to Speak | 无障碍服务 + 悬浮按钮（TYPE_ACCESSIBILITY_OVERLAY，不需要“悬浮窗”权限） |
| jing332/tts-server-android、SherpaTTS | 中文用户习惯：系统 TTS 引擎 + 阅读 App（legado 等）调用 |

## 功能对齐表（Windows v0.5 → Android v0.5）
| Windows v0.5 | Android v0.5 | 说明 |
|---|---|---|
| 全局快捷键 Ctrl+Alt+R：开始→暂停→继续 | ① 悬浮朗读条主按钮 ② 实体键盘同款快捷键 Ctrl+Alt+R（无障碍服务过滤按键）③ 系统无障碍按钮/手势（Android 8+）④ 通知栏「暂停/继续」 | 手机没有全局热键；无键盘时用悬浮条 |
| 停止快捷键 Ctrl+Alt+S | 悬浮条「停止」/长按主按钮、键盘 Ctrl+Alt+S、通知栏「停止」 | |
| 快捷键可改 + 禁用列表 | 设置里按键录入，同一套禁用规则（常用编辑键、少于两个修饰键的字母键、两键相同） | |
| UI Automation 取光标后文字 | 无障碍服务：当前输入框 textSelectionStart → 读到结尾 | 只能拿到“输入框”里的光标；网页正文没有光标 |
| 选中朗读（UIA GetSelection / Ctrl+C 探测） | 输入框选区 → 任意窗口里有选区的节点 → 最近一次“选中文字”事件（内存，2 分钟）→ **选中菜单「光标朗读」(PROCESS_TEXT)** → 分享 | 剪贴板方式在 Android 10+ 后台读不了剪贴板，不做 |
| 小横条窗口（不抢焦点，可拖动，记住位置） | 悬浮朗读条：TYPE_ACCESSIBILITY_OVERLAY + FLAG_NOT_FOCUSABLE（点它不会让输入框丢光标/选区），左侧黑条拖动，位置保存 | 关掉“显示悬浮朗读条”= Windows 的取消置顶 |
| 加载进度条 | 悬浮条底部细进度条 + 主界面百分比（与 Windows 同一估算公式） | |
| 状态：未加载/加载/就绪/取文字/朗读/暂停 | IDLE/LOADING/READING/PAUSED；加载中按主键 = 加载完先暂停（同 Windows） | |
| 标点停顿 ，、0.15 。！？0.5 ；0.3 ：0.25 换行0.5 | 相同默认值、0~3 秒可调 | |
| 语速 0.5~2 ×音色自带 speed | 相同 | 系统 TTS 时再乘阅读 App 的语速 |
| 线程数（默认 2） | 相同（上限=CPU 核数，最多 4） | |
| 切分器（20/36 字、长串→“链接/一串字符”） | 同一份算法（逐字一致的移植，45 条审查用例） | |
| 流式合成、启动缓冲 ≤1 秒、暂停在输出口 | 同；AudioTrack 暂停/继续，缓冲不丢，从停下的字接着读 | |
| 同时只加载一个模型；同模型音色瞬切 | 同（模型指纹相同即复用） | |
| 音色包 voices\<id>\voice.json（同格式） | 内置音色在 APK；导入的在应用私有目录或 Android/data/<包名>/files/voices（数据线拷入也能识别） | 电脑版音色包 zip 直接能导入 |
| 「+」导入音色（任意盘、中文路径） | 「＋导入音色包」(.zip) 和「＋导入文件夹」(系统文件选择器 SAF)；校验同 Windows；重复跳过；同模型硬链接 | 与内置同模型的包只存 voice.json+样音（省 120 MB） |
| 删除音色 = 手动删文件夹 | 长按导入的音色 → 删除（内置不能删） | 手机上用户看不到应用私有目录，只能在应用里删 |
| 试听：音色旁小喇叭播 sample.mp3，试听暂停朗读 | 同；外加“试听当前音色（实时合成）” | |
| 音量统一（gain + tanh 软削波） | 同一算法 | |
| 多音字前端 A 版（jieba+词库A+规则+变调→PUA） | 逐行移植，147k 汉字与 Windows C# 逐字一致 | 约 105 MB Java 堆，largeHeap |
| 用户自定义词库.txt（重启生效） | 应用内编辑器，保存后立即重新加载 | 手机上没有 exe 旁边的文件夹 |
| 关于/第三方许可 | 同（THIRD_PARTY_NOTICES + licenses） | |
| settings.json 宽松读取、原子保存 | SharedPreferences（系统保证原子性），数值有范围限制 | |
| 崩溃日志 log.txt / run_state | 只用 logcat（不写文件、不记录文字内容） | |
| 版本：标准版（Melo+zh-ll#1）/轻量版（Matcha+chaowen） | 同；另出“全音色版”（12 个音色全内置，按 2026-10-13 的安卓双版本决定） | |
| — | 系统 TTS 引擎（v0.1 起），也用多音字前端 | Android 独有 |

## 架构（app/src/main/kotlin/app/caretreader/tts/）
- TextSplitter.kt / SpeechRenderer.kt：与 Windows 相同的切分与合成循环（纯 Kotlin，JVM 测试）。
- PolyphoneFrontend.kt：多音字前端移植（纯 Kotlin）；App.kt 启动时后台加载；OOM 时自动退回旧方式。
- VoicePacks.kt / VoiceLibrary.kt：音色包读取、模型指纹、扫描、zip/文件夹导入、删除。
- Engine.kt / SherpaSynth.kt：全进程唯一引擎（系统 TTS、朗读器共用），一次一个模型，PUA 替换。
- Reader.kt + ReaderService.kt：朗读状态机 + AudioTrack + 前台服务（媒体播放类型，通知栏按钮）+ 音频焦点（来电自动暂停）。
- CaretAccessibilityService.kt + TextGrab.kt：悬浮朗读条、键盘快捷键、无障碍按钮、取文字（密码框不读）。
- ReadTextActivity.kt：选中菜单「光标朗读」与分享入口（无界面，立即关闭）。
- SettingsActivity.kt：主界面（黑白灰、网点纸、圆角）。
- CaretTtsService.kt：系统 TTS 引擎。

## 权限
FOREGROUND_SERVICE、FOREGROUND_SERVICE_MEDIA_PLAYBACK（锁屏继续读）、POST_NOTIFICATIONS（Android 13+ 可选，通知栏按钮）。无障碍服务由用户手动开启。没有 INTERNET、没有存储权限（导入用系统文件选择器）、没有悬浮窗权限（无障碍覆盖层不需要）。

## 外部审查（2026-10-15）
step-5-preview + nemotron-3-ultra 两轮审查与反驳，采纳/驳回见 review/审查与辩论纪要_r1.md。
静态检查：kotlinc API 23 兼容编译（登记表 tools/api_allow.txt）+ Android Lint 命令行（tools/lint.sh，0 错误）。

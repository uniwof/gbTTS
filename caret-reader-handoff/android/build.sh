#!/usr/bin/env bash
# 光标朗读 · 安卓版 一键构建（不用 Gradle：kotlinc → R8/d8 → aapt2 → 打包 → zipalign → apksigner）
#
# 用法:
#   bash build.sh                 # 编译代码 + 标准版 + 轻量版，全部步骤
#   bash build.sh code            # 只编译 Kotlin + 生成 classes.dex
#   bash build.sh test            # JVM 测试（切分器 + 真实 sherpa-onnx 合成）
#   bash build.sh standard [步骤] # 只打标准版；步骤可选 assets|link|pack|align|sign|verify（不写=全部）
#   bash build.sh lite [步骤]
#
# 工具链（在 Linux ARM64 沙盒里）：
#   JDK 17 (aarch64)、Kotlin 2.4 编译器、Android SDK platform 36 + build-tools 36.0.0。
#   build-tools 里的 aapt2 / zipalign 是 x86_64 程序，用 box64 运行；d8/R8/apksigner 是 Java，直接运行。
#   原生库与 Kotlin 接口来自 sherpa-onnx 1.13.8 官方 AAR（sherpa-onnx-1.13.8.aar）。
set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
T="${ANDROID_TOOLS:-/workspace/work/android-tools}"
export JAVA_HOME="$T/jdk-17.0.20.1+1"
export PATH="$JAVA_HOME/bin:$T/kotlinc/bin:$PATH"
BT="$T/sdk/build-tools/36.0.0"
AJAR="$T/sdk/platforms/android-36/android.jar"
AAR="$T/aar"                                  # sherpa-onnx-1.13.8.aar 解压目录
BOX=""; if [ "$(uname -m)" != "x86_64" ]; then BOX="$(command -v box64 || echo "$T/box64root/usr/bin/box64")"; export BOX64_LOG=0 BOX64_NOBANNER=1 BOX64_LD_LIBRARY_PATH="$T/x86lib"; fi
AAPT2="$BOX $BT/aapt2"
ZIPALIGN="$BOX $BT/zipalign"
B="$HERE/build"
REL="$HERE/release"
VERSION_NAME=0.5
VERSION_CODE=5
MIN_SDK=23
TARGET_SDK=36
KS="$HERE/keystore/release.p12"
KSPASS="$HERE/keystore/keystore.password"
ABIS="arm64-v8a armeabi-v7a"

step_code() {
  echo "== 编译 Kotlin"
  rm -rf "$B/classes" "$B/dex"; mkdir -p "$B/classes" "$B/dex"
  kotlinc -J-Xmx1200m -jvm-target 1.8 -no-reflect -no-jdk -nowarn \
    -cp "$AJAR:$AAR/classes.jar" "$HERE"/app/src/main/kotlin/app/caretreader/tts/*.kt -d "$B/classes"
  echo "== 兼容性检查：用 Android 6.0 (API 23) 的 android.jar 再编译一遍，列出所有 API 23 没有的调用，"
  echo "   每一处都必须在 tools/api_allow.txt 里登记（= 已人工确认被 Build.VERSION.SDK_INT 判断保护）"
  rm -rf "$B/api23"; mkdir -p "$B/api23"
  kotlinc -J-Xmx1200m -jvm-target 1.8 -no-reflect -no-jdk -nowarn \
    -cp "$T/sdk/platforms/android-6.0/android.jar:$AAR/classes.jar" "$HERE"/app/src/main/kotlin/app/caretreader/tts/*.kt -d "$B/api23" > "$B/api23.log" 2>&1 || true
  python3 "$HERE/tools/api_check.py" "$B/api23.log" "$HERE/tools/api_allow.txt"
  rm -rf "$B/api23"
  (cd "$B/classes" && jar cf "$B/app-classes.jar" .)
  echo "== R8 生成 classes.dex（去掉用不到的 Kotlin 标准库代码，不混淆）"
  java -Xmx1200m -cp "$BT/lib/d8.jar" com.android.tools.r8.R8 --release --min-api $MIN_SDK \
    --lib "$AJAR" --pg-conf "$HERE/app/proguard-rules.pro" --output "$B/dex" \
    "$B/app-classes.jar" "$AAR/classes.jar" "$T/kotlinc/lib/kotlin-stdlib.jar"
  ls -la "$B/dex"
}

step_test() {
  echo "== JVM 单元测试：切分器（和 Windows 版的 35 条审查用例逐字比对）"
  local TB="$B/test"; rm -rf "$TB"; mkdir -p "$TB"
  local SRC="$HERE/app/src/main/kotlin/app/caretreader/tts"
  kotlinc -J-Xmx1200m -nowarn "$SRC/TextSplitter.kt" "$SRC/SpeechRenderer.kt" "$HERE/app/src/test/kotlin/SplitterTest.kt" -d "$TB/splitter.jar"
  java -cp "$TB/splitter.jar:$T/kotlinc/lib/kotlin-stdlib.jar" SplitterTestKt "$HERE/tests/split_cases.txt" "$TB/splitter_output_android.txt" | tail -3
  diff "$HERE/tests/splitter_output_v3.txt" "$TB/splitter_output_android.txt" && echo "审查用例输出与 Windows 版 (v0.2 切分器) 逐字一致"
  echo "== JVM 测试：多音字前端与 Windows v0.5 (C#) 逐字比对（147k 汉字；有/无用户词库）"
  kotlinc -J-Xmx1200m -nowarn "$SRC/PolyphoneFrontend.kt" "$HERE/app/src/test/kotlin/FrontendTest.kt" -d "$TB/ft.jar"
  local D=/workspace/work/tts/app/frontend/data I=/workspace/work/polylex/integ U=/workspace/work/tts/app/frontend/用户自定义词库.txt
  local H=/workspace/work/tts/app/tests/CaretReader.TestHarness/bin/Release/net8.0/CaretReader.TestHarness.dll
  for inp in corpus rand; do
    for u in "" "$U"; do
      java -Xmx600m -cp "$TB/ft.jar:$T/kotlinc/lib/kotlin-stdlib.jar" FrontendTestKt $D $I/$inp.txt "$TB/kt.txt" $u 2>/dev/null
      DOTNET_ROOT=/workspace/work/dotnet /workspace/work/dotnet/dotnet $H g2p $D $I/$inp.txt $u > "$TB/cs.txt"
      cmp -s "$TB/kt.txt" "$TB/cs.txt" && echo "  [通过] $inp.txt 用户词库=${u:+有}${u:-无}：与 Windows v0.5 逐字一致 ($(wc -c < "$TB/cs.txt") 字节)" || { echo "  [失败] $inp $u"; return 1; }
    done
  done
  echo "== JVM 集成测试：真实 sherpa-onnx 1.13.8 (linux-aarch64 JNI) + 多音字前端，合成每个内置音色"
  kotlinc -J-Xmx1200m -nowarn -cp "$AAR/classes.jar:$T/json.jar" "$HERE/app/src/test/kotlin/stub/AssetManager.kt" \
    "$SRC/TextSplitter.kt" "$SRC/SpeechRenderer.kt" "$SRC/PolyphoneFrontend.kt" "$SRC/VoicePacks.kt" "$SRC/SherpaSynth.kt" \
    "$HERE/app/src/test/kotlin/SynthTest.kt" -d "$TB/synth.jar"
  local N="$T/native-linux/sherpa-onnx/native/linux-aarch64"
  for ed in ${TEST_EDITIONS:-lite standard}; do
    [ -f "$B/$ed/assets/voices/index.json" ] || step_assets "$ed"
    LD_LIBRARY_PATH="$N" java -Xmx700m -Djava.library.path="$N" \
      -cp "$TB/synth.jar:$AAR/classes.jar:$T/json.jar:$T/kotlinc/lib/kotlin-stdlib.jar" \
      SynthTestKt "$B/$ed/assets" "$HERE/test-results/wav_$ed" 2>&1 | grep -E "^(==|  \[|全部|.*项失败)"
  done
}

label_for() { case "$1" in standard) echo "光标朗读";; lite) echo "光标朗读 轻量版";; full) echo "光标朗读 全音色版";; esac; }
pkg_for()   { case "$1" in standard) echo "app.caretreader.tts";; lite) echo "app.caretreader.tts.lite";; full) echo "app.caretreader.tts.full";; esac; }
zh_for()    { case "$1" in standard) echo "标准版";; lite) echo "轻量版";; full) echo "全音色版";; esac; }
out_for()   { echo "$REL/光标朗读_安卓_$(zh_for "$1")_v$VERSION_NAME.apk"; }

step_assets() { python3 "$HERE/tools/assemble_assets.py" "$1" "$B/$1/assets"; }

step_link() {
  local ed="$1" D="$B/$1"
  echo "== aapt2 编译资源 / 链接清单 ($ed)"
  rm -rf "$D/res" "$D/res.zip" "$D/base.apk"; mkdir -p "$D"
  cp -r "$HERE/app/src/main/res" "$D/res"
  cat > "$D/res/values/strings.xml" <<EOF
<?xml version="1.0" encoding="utf-8"?>
<resources>
    <string name="app_name">$(label_for "$ed")</string>
    <string name="read_action">光标朗读</string>
    <string name="a11y_label">$(label_for "$ed") · 悬浮朗读条</string>
    <string name="a11y_summary">悬浮朗读条、键盘快捷键：有选中读选中，没有就从光标处读</string>
    <string name="a11y_desc">开启后屏幕上会出现「光标朗读」悬浮条，点「开始」就朗读：有选中的文字只读选中，没有就从输入框的光标处读到结尾；再点暂停/继续，长按或点「停止」结束。接实体键盘时也可以用快捷键（默认 Ctrl+Alt+R 开始/暂停/继续，Ctrl+Alt+S 停止）。\n\n本服务只在你按下开始或快捷键时读取当前输入框/选中的文字用来朗读；完全离线，不保存、不上传任何内容。</string>
</resources>
EOF
  $AAPT2 compile --dir "$D/res" -o "$D/res.zip"
  local rename=()
  [ "$ed" = standard ] || rename=(--rename-manifest-package "$(pkg_for "$ed")")
  $AAPT2 link -o "$D/base.apk" -I "$AJAR" --manifest "$HERE/app/src/main/AndroidManifest.xml" \
    --min-sdk-version $MIN_SDK --target-sdk-version $TARGET_SDK \
    --version-code $VERSION_CODE --version-name $VERSION_NAME "${rename[@]}" "$D/res.zip"
}

step_pack() {
  local ed="$1" D="$B/$1"
  echo "== 组装 APK ($ed)"
  rm -rf "$D/jni"; mkdir -p "$D/jni"
  for a in $ABIS; do
    mkdir -p "$D/jni/$a"
    # 只需要 JNI 库和 onnxruntime（JNI 库只依赖 libonnxruntime.so，c-api / cxx-api 用不到）
    cp "$AAR/jni/$a/libsherpa-onnx-jni.so" "$AAR/jni/$a/libonnxruntime.so" "$D/jni/$a/"
  done
  python3 "$HERE/tools/package_apk.py" "$D/base.apk" "$B/dex" "$D/jni" "$D/assets" "$D/unsigned.apk"
}

step_align() {
  local D="$B/$1"
  echo "== zipalign（.so 按 16 KB 页对齐，兼容 16 KB 页大小的新手机）"
  rm -f "$D/aligned.apk"
  $ZIPALIGN -P 16 -f 4 "$D/unsigned.apk" "$D/aligned.apk"
  $ZIPALIGN -c -P 16 4 "$D/aligned.apk" && echo "对齐检查通过"
  rm -f "$D/unsigned.apk"
}

step_sign() {
  local ed="$1" D="$B/$1" out; out="$(out_for "$1")"
  echo "== apksigner 签名 (v1+v2+v3)"
  mkdir -p "$REL"
  java -Xmx1g -jar "$BT/lib/apksigner.jar" sign --ks "$KS" --ks-key-alias caretreader \
    --ks-pass "file:$KSPASS" --min-sdk-version $MIN_SDK \
    --out "$out" "$D/aligned.apk"
  rm -f "$D/aligned.apk" "$out.idsig"
  ls -la "$out"
}

step_verify() {
  local out; out="$(out_for "$1")"
  echo "== 验证 $out"
  java -jar "$BT/lib/apksigner.jar" verify --verbose --min-sdk-version $MIN_SDK "$out" | grep -E "Verifies|Verified using|Number of signers"
  $AAPT2 dump badging "$out" 2>/dev/null | grep -iE "^package|sdkVersion|application-label:|native-code|uses-permission" || true
  $ZIPALIGN -c -P 16 4 "$out" && echo "zipalign 检查通过"
}

edition() {
  local ed="$1" only="${2:-}"
  if [ -n "$only" ]; then "step_$only" "$ed"; return; fi
  step_assets "$ed"; step_link "$ed"; step_pack "$ed"; step_align "$ed"; step_sign "$ed"; step_verify "$ed"
}

case "${1:-all}" in
  code) step_code ;;
  test) step_test ;;
  standard|lite|full) edition "$1" "${2:-}" ;;
  all) step_code; edition lite; edition standard; edition full ;;
  *) echo "用法见文件开头"; exit 2 ;;
esac

#!/usr/bin/env bash
# Android Lint（命令行版，不用 Gradle）：检查 Kotlin 源码 + 清单 + 资源（NewApi、权限、无障碍、前台服务类型等）
set -uo pipefail
HERE="$(cd "$(dirname "$0")/.." && pwd)"
T=/workspace/work/android-tools
export JAVA_HOME="$T/jdk-17.0.20.1+1" PATH="$T/jdk-17.0.20.1+1/bin:$PATH" ANDROID_HOME="$T/sdk" ANDROID_SDK_ROOT="$T/sdk"
P="$HERE/build/lint/proj"; rm -rf "$P"; mkdir -p "$P"
cp "$HERE/app/src/main/AndroidManifest.xml" "$P/"
cp -r "$HERE/app/src/main/res" "$P/res"
cp "$HERE/build/standard/res/values/strings.xml" "$P/res/values/strings.xml" 2>/dev/null || true
"$T/sdk/cmdline-tools/latest/bin/lint" --sdk-home "$T/sdk" --compile-sdk-version 36 \
  --sources "$HERE/app/src/main/kotlin" --resources "$P/res" \
  --classpath "$HERE/build/classes" --libraries "$T/aar/classes.jar:$T/kotlinc/lib/kotlin-stdlib.jar" \
  --disable GradleDependency,OldTargetApi,ExpiredTargetSdkVersion,MissingApplicationIcon,IconMissingDensityFolder,SetTextI18n,HardcodedText,RtlHardcoded,ObsoleteSdkInt \
  --text "$HERE/test-results/lint.txt" --exitcode "$P"
echo "lint exit=$?"

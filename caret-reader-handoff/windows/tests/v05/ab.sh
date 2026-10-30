#!/bin/bash
# A/B：v0.4（备份源码编译的测试程序）vs v0.5（多音字前端开启），2 核（taskset -c 0,1），2 线程
# 用法：bash ab.sh <音色包 id> <轮次标记>
export DOTNET_ROOT=/workspace/work/dotnet PATH=/workspace/work/dotnet:$PATH
cd /workspace/work/tts/app
H=tests/CaretReader.TestHarness/bin/Release/net8.0/CaretReader.TestHarness
H4=/workspace/work/tts/app/tests/v05/v04src/tests/CaretReader.TestHarness/bin/Release/net8.0/CaretReader.TestHarness
v=$1; r=$2
for ver in v04 v05; do
  if [ $ver = v04 ]; then out=$(timeout 100 taskset -c 0,1 $H4 synth pk/voices/$v /tmp/ab_$v.wav 2 2>/dev/null)
  else out=$(CR_WAITFRONTEND=${WAITFE:-0} CR_APPDIR=/workspace/work/tts/app/tests/v05/crapp timeout 100 taskset -c 0,1 $H synth pk/voices/$v tests/v05/wav/${v}_v05.wav 2 2>/dev/null); fi
  rtf=$(echo "$out" | grep -o 'RTF [0-9.]*' | cut -d' ' -f2)
  first=$(echo "$out" | grep -o '首句出声延迟 [0-9.]*s' | grep -o '[0-9.]*')
  gaps=$(echo "$out" | grep -o '卡顿) [0-9]* 次 / 共 [0-9.]*s' )
  peak=$(echo "$out" | grep -o 'VmHWM [0-9]*MB' | grep -o '[0-9]*')
  load=$(echo "$out" | grep -o '加载 [0-9.]*s' | head -1 | grep -o '[0-9.]*')
  echo "$r $v $ver load=${load}s RTF=$rtf first=${first}s gaps='$gaps' peakMB=$peak loadavg=$(cut -d' ' -f1 /proc/loadavg)" | tee -a tests/v05/ab_results.txt
done

#!/usr/bin/env bash
# 组装某个版本的 assets 目录：voices.json + models/...
# 用法: assemble_assets.sh <standard|lite> <输出目录>
# 只放 sherpa-onnx 实际会读的文件（模型、tokens、lexicon、规则 fst）；
# jieba 的 dict 目录在 sherpa-onnx 1.13.x 里已不再使用（Kotlin API 注释 "dictDir unused"，JVM 实测无 dict 也正常），不打包，每个模型省 14 MB。
set -euo pipefail
ED="$1"; OUT="$2"
HERE="$(cd "$(dirname "$0")/.." && pwd)"
TTS="$(cd "$HERE/.." && pwd)"           # /workspace/work/tts，模型原件都在这里
rm -rf "$OUT"; mkdir -p "$OUT/models"
put() {  # put <源文件> <目标文件>：优先硬链接，省磁盘
  mkdir -p "$(dirname "$2")"
  ln "$1" "$2" 2>/dev/null || cp "$1" "$2"
}
FST="date.fst phone.fst number.fst"
if [ "$ED" = standard ]; then
  cp "$HERE/config/voices.standard.json" "$OUT/voices.json"
  for f in model.onnx tokens.txt lexicon.txt $FST new_heteronym.fst LICENSE; do put "$TTS/vits-melo-tts-zh_en/$f" "$OUT/models/vits-melo-tts-zh_en/$f"; done
  for f in model.onnx tokens.txt lexicon.txt $FST new_heteronym.fst; do put "$TTS/sherpa-onnx-vits-zh-ll/$f" "$OUT/models/sherpa-onnx-vits-zh-ll/$f"; done
  for f in vits-zh-hf-fanchen-C.onnx tokens.txt lexicon.txt $FST new_heteronym.fst; do put "$TTS/vits-zh-hf-fanchen-C/$f" "$OUT/models/vits-zh-hf-fanchen-C/$f"; done
else
  cp "$HERE/config/voices.lite.json" "$OUT/voices.json"
  for f in model-steps-3.onnx tokens.txt lexicon.txt $FST; do put "$TTS/matcha-icefall-zh-baker/$f" "$OUT/models/matcha-icefall-zh-baker/$f"; done
  put "$TTS/vocos-22khz-univ.onnx" "$OUT/models/matcha-icefall-zh-baker/vocos-22khz-univ.onnx"
fi
# 所有 voices.json 里提到的文件都必须存在
python3 - "$OUT" <<'PY'
import json, os, sys
out = sys.argv[1]; v = json.load(open(os.path.join(out, "voices.json")))
miss = []
for m in v["models"]:
    for k in ("model", "vocoder", "tokens", "lexicon"):
        if m.get(k): 
            p = os.path.join(out, m["dir"], m[k]); 
            if not os.path.isfile(p): miss.append(p)
    for f in m.get("ruleFsts", []):
        p = os.path.join(out, m["dir"], f)
        if not os.path.isfile(p): miss.append(p)
ids = {m["id"] for m in v["models"]}
bad = [x["id"] for x in v["voices"] if x["model"] not in ids]
if miss or bad: sys.exit(f"缺少文件: {miss} / 音色指向不存在的模型: {bad}")
print("assets ok:", out)
PY
du -sh "$OUT"

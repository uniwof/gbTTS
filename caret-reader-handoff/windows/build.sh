#!/usr/bin/env bash
# 一键（v0.5）：编译 Windows 版 → 组装音色包 → 生成试听 sample.mp3（如缺）→ 组装标准版 / 轻量版 → 打 zip
# 用法：bash build.sh        （在 Linux 沙盒里运行）
#   程序文件夹里的内部路径全部是英文：CaretReader.exe、voices\melo-0\model.onnx ……
set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
TTS="$(cd "$HERE/.." && pwd)"            # 模型所在目录 /workspace/work/tts
export DOTNET_ROOT=/workspace/work/dotnet
export PATH="$DOTNET_ROOT:$PATH"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
VER=v0.5
REL="$TTS/release/$VER"
STAGE="$HERE/stage"
PACKS="$HERE/pk/voices"                   # 全部音色包（模型文件是硬链接）
SAMPLES="$HERE/config/samples"            # 预先做好的试听 mp3（统一试听文本，见 tests 里的 V04Tests.SampleText）
HARNESS="$HERE/tests/CaretReader.TestHarness/bin/Release/net8.0/CaretReader.TestHarness"

echo "== 1. 编译 Windows x64 版本 + 测试程序"
rm -rf "$HERE/publish/win-x64"
dotnet publish "$HERE/src/CaretReader.App" -c Release -r win-x64 -o "$HERE/publish/win-x64" -nologo -v q
dotnet build "$HERE/tests/CaretReader.TestHarness" -c Release -nologo -v q

echo "== 1b. v0.5 多音字前端数据（词库 A + jieba 词典 + 单字 / 词组读音；见 frontend/build_data.py）"
python3 "$HERE/frontend/build_data.py"

echo "== 2. 组装音色包（v0.5：每个包的 lexicon.txt 追加私用区拼音条目 + pua_map.txt）"
python3 "$HERE/packs.py" assemble "$PACKS"

echo "== 3. 试听 sample.mp3（缺的才生成：真实切分 + 停顿 ，、0.15 。！？0.5 ；0.3 ：0.25 + 增益，再 loudnorm）"
mkdir -p "$SAMPLES" "$HERE/pk/wav"
for v in $(python3 "$HERE/packs.py" list); do
  if [ ! -s "$SAMPLES/$v.mp3" ]; then
    "$HARNESS" sample "$PACKS/$v" "$HERE/pk/wav/$v.wav"
    sr=$(ffprobe -v error -show_entries stream=sample_rate -of csv=p=0 "$HERE/pk/wav/$v.wav")
    ffmpeg -nostdin -loglevel error -y -i "$HERE/pk/wav/$v.wav" -af loudnorm=I=-24:TP=-2:LRA=11 -ar "$sr" -ac 1 \
      -c:a libmp3lame -q:a 4 "$SAMPLES/$v.mp3"
  fi
  cp "$SAMPLES/$v.mp3" "$PACKS/$v/sample.mp3"
done
"$HARNESS" scan "$HERE/pk"

echo "== 4. 组装标准版 / 轻量版"
rm -rf "$STAGE"; mkdir -p "$STAGE" "$REL/音色包"

make_edition() {
  local name="$1"; shift
  local dir="$STAGE/$name/光标朗读"
  mkdir -p "$dir/voices"
  cp "$HERE/publish/win-x64/"* "$dir/"
  printf '%s' "$name" > "$dir/edition.txt"
  # 说明文件：加 UTF-8 BOM，旧版记事本打开也不会乱码
  printf '\xEF\xBB\xBF' > "$dir/说明.txt"; cat "$HERE/README_zh.md" >> "$dir/说明.txt"
  # v0.5：多音字前端数据、用户自定义词库、第三方许可
  mkdir -p "$dir/frontend" "$dir/licenses"
  cp "$HERE/frontend/data/"*.txt "$dir/frontend/"
  cp "$HERE/frontend/licenses/"*.txt "$dir/licenses/"
  cp "$HERE/frontend/用户自定义词库.txt" "$dir/用户自定义词库.txt"
  printf '\xEF\xBB\xBF' > "$dir/THIRD_PARTY_NOTICES.txt"; cat "$HERE/frontend/THIRD_PARTY_NOTICES.txt" >> "$dir/THIRD_PARTY_NOTICES.txt"
  for v in "$@"; do cp -rL "$PACKS/$v" "$dir/voices/$v"; done
  "$HARNESS" scan "$dir" > /dev/null
}
make_edition 标准版 melo-0 zhll-1
make_edition 轻量版 matcha-0 chaowen-0

echo "== 5. 打包 zip（UTF-8 文件名；程序 zip + 每个音色一个 zip）"
python3 - "$STAGE" "$REL" "$VER" "$PACKS" "$HERE/packs.py" <<'EOF'
import os, sys, zipfile, importlib.util
stage, rel, ver, packs, pdef = sys.argv[1:]
spec = importlib.util.spec_from_file_location("packs", pdef); P = importlib.util.module_from_spec(spec); spec.loader.exec_module(P)

def zipdir(out, root, prefix=""):
    with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED, compresslevel=6) as z:
        for d, _, files in os.walk(root):
            for f in sorted(files):
                p = os.path.join(d, f)
                z.write(p, os.path.join(prefix, os.path.relpath(p, root)))
    print(f"{os.path.basename(out)}  {os.path.getsize(out)/1048576:.1f} MB")

for ed in ("标准版", "轻量版"):
    zipdir(os.path.join(rel, f"光标朗读_{ed}_win10x64_{ver}.zip"), os.path.join(stage, ed))
for vid, mk, sid, name, desc, order in P.VOICES:
    label = name.split(" (")[0].replace(" · ", "-")          # 女声-清亮
    zipdir(os.path.join(rel, "音色包", f"音色_{label}_{vid}.zip"), os.path.join(packs, vid), prefix=vid)
EOF
echo "== 完成"

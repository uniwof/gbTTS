#!/usr/bin/env python3
"""把 aapt2 生成的 base.apk + classes.dex + 原生库 + assets 组装成一个未签名、未对齐的 APK。

用法: package_apk.py <base.apk> <dex目录> <jniLibs目录> <assets目录> <输出.apk>

- resources.arsc、原生库 .so、模型 .onnx 不压缩（STORED）：
  * Android 11+ 要求 resources.arsc 不压缩；
  * .so 不压缩 + 页对齐 → 手机直接从 APK 里加载，不再解压一份（AndroidManifest 里 extractNativeLibs=false）；
  * .onnx 本来就几乎压不动，不压缩可以让 AssetManager 直接映射读取，加载更快。
- 其它文件（dex、词典 lexicon、fst 等）正常压缩。
之后必须再跑 zipalign -P 16 -f 4 和 apksigner。
"""
import os
import sys
import zipfile

base, dexdir, jnidir, assetsdir, out = sys.argv[1:6]
STORE_EXT = (".so", ".onnx", ".arsc", ".png", ".mp3")   # .mp3 不压缩：MediaPlayer 用 openFd 直接从 APK 里播样音

def add(z, src, arc):
    ct = zipfile.ZIP_STORED if arc.endswith(STORE_EXT) else zipfile.ZIP_DEFLATED
    zi = zipfile.ZipInfo(arc, date_time=(2026, 10, 13, 0, 0, 0))
    zi.compress_type = ct
    zi.external_attr = 0o644 << 16
    with open(src, "rb") as f, z.open(zi, "w", force_zip64=False) as w:
        while True:
            b = f.read(1 << 20)
            if not b:
                break
            w.write(b)

with zipfile.ZipFile(out, "w", allowZip64=True) as z:
    with zipfile.ZipFile(base) as b:
        for info in b.infolist():          # 保持 aapt2 的压缩方式（resources.arsc 是 STORED）
            data = b.read(info.filename)
            zi = zipfile.ZipInfo(info.filename, date_time=(2026, 10, 13, 0, 0, 0))
            zi.compress_type = info.compress_type
            zi.external_attr = 0o644 << 16
            z.writestr(zi, data)
    for name in sorted(os.listdir(dexdir)):
        if name.endswith(".dex"):
            add(z, os.path.join(dexdir, name), name)
    for root, _, files in sorted(os.walk(jnidir)):
        for name in sorted(files):
            p = os.path.join(root, name)
            add(z, p, "lib/" + os.path.relpath(p, jnidir).replace(os.sep, "/"))
    for root, _, files in sorted(os.walk(assetsdir)):
        for name in sorted(files):
            p = os.path.join(root, name)
            add(z, p, "assets/" + os.path.relpath(p, assetsdir).replace(os.sep, "/"))
print(f"{out}: {os.path.getsize(out) / 1048576:.1f} MB (未签名)")

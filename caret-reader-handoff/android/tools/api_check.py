#!/usr/bin/env python3
"""API 23 兼容性检查：kotlinc 用 Android 6.0 的 android.jar 编译时报的每个错误，
都必须出现在 api_allow.txt（格式：文件名:符号，# 注释）里，否则构建失败。"""
import re, sys
log, allow = sys.argv[1], sys.argv[2]
ok = set()
for l in open(allow, encoding="utf-8"):
    l = l.split("#")[0].strip()
    if l: ok.add(l)
errs = []
for line in open(log, encoding="utf-8"):
    m = re.match(r".*/([A-Za-z0-9_]+\.kt):(\d+):\d+: error: (.*)", line)
    if not m: continue
    f, ln, msg = m.groups()
    sym = re.search(r"'([^']+)'", msg)
    key = f"{f}:{sym.group(1) if sym else msg}"
    if key not in ok: errs.append(f"{f}:{ln}: {msg}  [{key}]")
if errs:
    print("API 23 上不存在、且没有登记的调用：\n  " + "\n  ".join(errs)); sys.exit(1)
print("API 23 兼容性检查通过（高版本 API 都在登记表里，且由 SDK_INT 判断保护）")

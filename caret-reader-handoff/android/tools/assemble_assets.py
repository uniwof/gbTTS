#!/usr/bin/env python3
"""组装某个版本的 assets：内置音色包 + 共用模型 + 多音字前端数据 + 许可说明。
用法: assemble_assets.py <standard|lite|full> <输出目录>
- 音色包来源：Windows v0.5 的音色包 /workspace/work/tts/app/pk/voices/<id>（含 PUA 词典和 pua_map）。
- 同一模型的多个音色只放一份模型（assets/models/<模型>/），voices/<id>/ 只放 voice.json + sample.mp3。
- index.json 里预先算好每个内置模型的“模型指纹”（算法与 VoicePacks.kt 的 modelKey 完全一致），
  导入同一模型的音色包时就能认出来，不再重复保存 120 MB 的模型。
"""
import hashlib, json, os, re, shutil, sys

ED, OUT = sys.argv[1], sys.argv[2]
APP = "/workspace/work/tts/app"
PK = f"{APP}/pk/voices"
EDITIONS = {
    "standard": ("标准版", ["melo-0", "zhll-1"]),
    "lite": ("轻量版", ["matcha-0", "chaowen-0"]),
    "full": ("全音色版", ["melo-0", "zhll-0", "zhll-1", "zhll-2", "zhll-3", "zhll-4", "fanchen-29", "fanchen-7",
                        "fanchen-24", "fanchen-119", "matcha-0", "chaowen-0"]),
}
name, ids = EDITIONS[ED]
shutil.rmtree(OUT, ignore_errors=True)
os.makedirs(OUT)

def put(src, dst):
    os.makedirs(os.path.dirname(dst), exist_ok=True)
    try: os.link(src, dst)
    except OSError: shutil.copy2(src, dst)

def model_key(engine, files, pack):
    size = lambda r: os.path.getsize(os.path.join(pack, r))
    mp = os.path.join(pack, files["model"]); ln = os.path.getsize(mp)
    chunk = 1 << 20
    poss = []
    for p in (0, max(0, ln // 2 - chunk // 2), max(0, ln - chunk)):
        if p not in poss: poss.append(p)
    h = hashlib.sha256()
    with open(mp, "rb") as f:
        for p in poss:
            f.seek(p); h.update(f.read(min(chunk, ln - p)))
    sz = lambda r: "-" if not r else str(size(r))
    kind = "matcha" if engine == "matcha" else "vits"
    voc = sz(files.get("vocoder")) if engine == "matcha" else ""
    return f"{kind}|{ln}|{h.hexdigest()[:24]}|{sz(files.get('tokens'))}|{sz(files.get('lexicon'))}|{voc}|{','.join(files['ruleFsts'])}|{sz(files.get('puaMap'))}"

index = {"edition": name, "voices": []}
models = {}   # modelKey -> modelDir
for vid in ids:
    pack = f"{PK}/{vid}"
    vj = json.load(open(f"{pack}/voice.json", encoding="utf-8"))
    f = vj["files"]
    fsts = [x for x in f.get("ruleFsts", []) if os.path.isfile(os.path.join(pack, x))]
    files = {"model": f["model"], "tokens": f.get("tokens", "tokens.txt"), "lexicon": f.get("lexicon"),
             "vocoder": f.get("vocoder"), "ruleFsts": fsts, "puaMap": f.get("puaMap")}
    key = model_key(vj["engine"], files, pack)
    if key not in models:
        mdir = "models/" + vid.split("-")[0]
        models[key] = mdir
        for r in [files["model"], files["tokens"], files["lexicon"], files["vocoder"], files["puaMap"], *fsts]:
            if r: put(os.path.join(pack, r), os.path.join(OUT, mdir, r))
    mdir = models[key]
    sizes = {r: os.path.getsize(os.path.join(OUT, mdir, r)) for r in os.listdir(os.path.join(OUT, mdir))}
    os.makedirs(f"{OUT}/voices/{vid}")
    shutil.copy2(f"{pack}/voice.json", f"{OUT}/voices/{vid}/voice.json")
    has = os.path.isfile(f"{pack}/sample.mp3")
    if has: shutil.copy2(f"{pack}/sample.mp3", f"{OUT}/voices/{vid}/sample.mp3")
    index["voices"].append({"id": vid, "modelDir": mdir, "modelKey": key, "sizes": sizes, "hasSample": has})
json.dump(index, open(f"{OUT}/voices/index.json", "w", encoding="utf-8"), ensure_ascii=False, indent=1)

# 多音字前端数据（与 Windows v0.5 相同）
for n in ["dict.txt", "hmm.txt", "polylex_a.txt", "phrases.txt", "char_pinyin.txt"]:
    put(f"{APP}/frontend/data/{n}", f"{OUT}/frontend/{n}")
ud = open(f"{APP}/frontend/用户自定义词库.txt", encoding="utf-8-sig").read()
ud = ud.replace("# 用记事本打开、修改、保存后，重新启动光标朗读就会生效。", "# 在「光标朗读」主界面 → 多音字 → 编辑用户自定义词库，保存后立即生效。")
ud = ud.replace("（记在 log.txt 里）", "")
open(f"{OUT}/frontend/user_dict_default.txt", "w", encoding="utf-8").write(ud)
shutil.copy2(f"{APP}/frontend/THIRD_PARTY_NOTICES.txt", f"{OUT}/THIRD_PARTY_NOTICES.txt")
shutil.copytree(f"{APP}/frontend/licenses", f"{OUT}/licenses")
tot = sum(os.path.getsize(os.path.join(r, x)) for r, _, fs in os.walk(OUT) for x in fs)
print(f"assets ok: {OUT}  {ED} {name}  voices={ids}  models={list(models.values())}  {tot/1048576:.1f} MB")

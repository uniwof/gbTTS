# 分步打包（沙盒单条命令 120 秒限制）：python3 _zip.py <标准版|轻量版|voice-id>
import os, sys, zipfile, importlib.util
H='/workspace/work/tts/app'; stage=f'{H}/stage'; ver='v0.5'; rel=f'/workspace/work/tts/release/{ver}'; packs=f'{H}/pk/voices'
spec = importlib.util.spec_from_file_location("packs", f'{H}/packs.py'); P = importlib.util.module_from_spec(spec); spec.loader.exec_module(P)
os.makedirs(f'{rel}/音色包', exist_ok=True)
def zipdir(out, root, prefix=""):
    with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED, compresslevel=6) as z:
        for d, _, files in os.walk(root):
            for f in sorted(files):
                p = os.path.join(d, f); z.write(p, os.path.join(prefix, os.path.relpath(p, root)))
    print(f"{os.path.basename(out)}  {os.path.getsize(out)/1048576:.1f} MB")
a = sys.argv[1]
if a in ("标准版", "轻量版"): zipdir(os.path.join(rel, f"光标朗读_{a}_win10x64_{ver}.zip"), os.path.join(stage, a))
else:
    for vid, mk, sid, name, desc, order in P.VOICES:
        if vid == a:
            label = name.split(" (")[0].replace(" · ", "-")
            zipdir(os.path.join(rel, "音色包", f"音色_{label}_{vid}.zip"), os.path.join(packs, vid), prefix=vid)

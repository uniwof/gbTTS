import json,sys,urllib.request,os,time
part=sys.argv[1]; files=sys.argv[2:]
ctx=open('context.txt',encoding='utf-8').read()
code="\n\n".join(f"===== {f} =====\n"+open(f,encoding='utf-8').read() for f in files)
msgs=[{"role":"system","content":"You are a meticulous, skeptical senior Windows/Win32/C#/.NET reviewer. Never flatter; report only what you can point to in the code. All the given files belong to one program; check cross-file facts before claiming something is missing."},{"role":"user","content":ctx+f"\n\nPART {part}:\n"+code}]
body={"model":os.environ.get("NVMODEL","moonshotai/kimi-k3"),"messages":msgs,"temperature":0.3,"max_tokens":int(os.environ.get("MAXTOK","7000")),"stream":True}
req=urllib.request.Request("https://integrate.api.nvidia.com/v1/chat/completions",data=json.dumps(body).encode(),headers={"Content-Type":"application/json","User-Agent":"curl/8","Accept":"text/event-stream"})
out=open(f'nv_{part}.md','w',encoding='utf-8'); t0=time.time()
try:
    with urllib.request.urlopen(req,timeout=int(os.environ.get("TMO","108"))) as r:
        for raw in r:
            line=raw.decode('utf-8','ignore').strip()
            if not line.startswith('data:'): continue
            d=line[5:].strip()
            if d=='[DONE]': break
            try: j=json.loads(d)
            except: continue
            ch=j.get('choices') or []
            if ch:
                c=ch[0].get('delta',{}).get('content')
                if c: out.write(c); out.flush()
except Exception as e:
    out.write(f"\n\n[ERROR {e}]")
out.write(f"\n\n[done in {time.time()-t0:.0f}s]"); out.close()

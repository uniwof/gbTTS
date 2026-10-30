import json,sys,urllib.request,os,time
prov,model,part=sys.argv[1:4]; files=sys.argv[4:]
ctx=open('ctx.txt',encoding='utf-8').read()
code="\n\n".join(f"===== {f} =====\n"+open(f,encoding='utf-8').read() for f in files)
msgs=[{"role":"system","content":"You are a meticulous, skeptical senior Windows/Win32/C#/.NET reviewer. Never flatter; report only what you can point to in the code. All files belong to one program; Native.* imports not shown are declared in Native.cs. Do not claim something is missing unless you are sure."},{"role":"user","content":ctx+f"\n\nPART {part}:\n"+code}]
url={"nvidia":"https://integrate.api.nvidia.com/v1/chat/completions","agnes":"https://apihub.agnes-ai.com/v1/chat/completions","groq":"https://api.groq.com/openai/v1/chat/completions"}[prov]
body={"model":model,"messages":msgs,"temperature":0.3,"max_tokens":int(os.environ.get("MAXTOK","6000"))}
req=urllib.request.Request(url,data=json.dumps(body).encode(),headers={"Content-Type":"application/json","User-Agent":"curl/8"})
t=time.time()
try:
    r=json.load(urllib.request.urlopen(req,timeout=int(os.environ.get("TMO","110"))))
    txt=r["choices"][0]["message"].get("content") or ""; u=r.get("usage")
except urllib.error.HTTPError as e: txt=f"HTTP {e.code} {e.read().decode()[:500]}"; u=None
except Exception as e: txt=f"ERR {e}"; u=None
open(f'{prov}_{part}.md','w',encoding='utf-8').write(txt); print(prov,model,part,round(time.time()-t),u)

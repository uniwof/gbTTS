import json,sys,urllib.request,os
prov=sys.argv[1]; part=sys.argv[2]; files=sys.argv[3:]
ctx=open(os.environ.get('CTX','context.txt'),encoding='utf-8').read()
code="\n\n".join(f"===== {f} =====\n"+open(f,encoding='utf-8').read() for f in files)
msgs=[{"role":"system","content":"You are a meticulous, skeptical senior Windows/Win32/C#/.NET reviewer. Never flatter; report only what you can point to in the code."},{"role":"user","content":ctx+f"\n\nPART {part}:\n"+code}]
if prov=='groq':
    url="https://api.groq.com/openai/v1/chat/completions"; body={"model":"openai/gpt-oss-120b","messages":msgs,"temperature":0.3,"max_completion_tokens":int(os.environ.get("MAXTOK","4000")),"reasoning_effort":"medium"}
else:
    url="https://integrate.api.nvidia.com/v1/chat/completions"; body={"model":os.environ.get("NVMODEL","moonshotai/kimi-k3"),"messages":msgs,"temperature":0.3,"max_tokens":int(os.environ.get("MAXTOK","6000"))}
req=urllib.request.Request(url,data=json.dumps(body).encode(),headers={"Content-Type":"application/json","User-Agent":"curl/8"})
try: r=json.load(urllib.request.urlopen(req,timeout=115))
except urllib.error.HTTPError as e: print("HTTP",e.code,e.read().decode()[:800]); sys.exit(1)
c=r["choices"][0]; open(f'{prov}_{part}.md','w',encoding='utf-8').write(c["message"].get("content") or ""); print(c.get("finish_reason"),r.get("usage"))

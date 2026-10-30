import json,sys,urllib.request,time
m=sys.argv[1]
body={"model":m,"messages":[{"role":"user","content":"Reply with OK"}],"max_tokens":20}
req=urllib.request.Request("https://integrate.api.nvidia.com/v1/chat/completions",data=json.dumps(body).encode(),headers={"Content-Type":"application/json","User-Agent":"curl/8"})
t=time.time()
try:
  r=json.load(urllib.request.urlopen(req,timeout=40)); print(m,"OK",round(time.time()-t,1),r["choices"][0]["message"].get("content","")[:30])
except Exception as e: print(m,"ERR",e)

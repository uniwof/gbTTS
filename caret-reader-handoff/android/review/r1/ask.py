import json, sys, time, requests, concurrent.futures as cf
tag, msgfile, models = sys.argv[1], sys.argv[2], sys.argv[3:]
SYS = ("You are a blunt, senior Android engineer and code reviewer. Do NOT agree to be polite, and do not simply agree with the author or the user. "
       "Find real bugs, platform pitfalls and design gaps; propose concrete better alternatives. If something is right, say so briefly and move on. Answer in Chinese.")
msgs = json.load(open(msgfile))
def run(m):
    body = {"model": m, "messages": [{"role": "system", "content": SYS}] + msgs, "max_tokens": 12000, "temperature": 0.5, "stream": True}
    if "nemotron" in m: body["chat_template_kwargs"] = {"enable_thinking": False, "thinking": False}
    out, t0 = [], time.time()
    try:
        r = requests.post("https://integrate.api.nvidia.com/v1/chat/completions", json=body, stream=True, timeout=(15, 900))
        if r.status_code != 200: return m, f"HTTP {r.status_code}: {r.text[:300]}"
        for line in r.iter_lines():
            if time.time() - t0 > 900: out.append("[TRUNCATED-time]"); break
            if not line.startswith(b"data:"): continue
            d = line[5:].strip()
            if d == b"[DONE]": break
            try:
                c = json.loads(d)["choices"][0]["delta"].get("content")
                if c: out.append(c)
            except Exception: pass
    except Exception as e: out.append(f"[ERR {type(e).__name__}: {str(e)[:150]}]")
    txt = "".join(out)
    open(f"{tag}_{m.replace('/','_')}.txt", "w").write(txt)
    return m, txt
with cf.ThreadPoolExecutor(len(models)) as ex:
    for m, t in ex.map(run, models): print("=====", m, len(t)); print(t[:300])

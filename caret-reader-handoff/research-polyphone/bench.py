"""python bench.py <old|A|AB>  : peak RSS and ms per 1000 hanzi for the frontend alone (run under taskset -c 0,1)."""
import sys, os, time, resource, json, re
sys.path.insert(0, '/workspace/work/tts/v2/pylib'); sys.path.insert(0, '/workspace/work/tts/clarity'); sys.path.insert(0, '/workspace/work/polylex')
mode = sys.argv[1]
base = resource.getrusage(resource.RUSAGE_SELF).ru_maxrss // 1024
import jieba, pypinyin, hfront  # both frontends import these
after_imports = resource.getrusage(resource.RUSAGE_SELF).ru_maxrss // 1024
txt = ''.join(open(f, encoding='utf-8').read() for f in ['/workspace/work/tts/v2/full.txt', '/workspace/work/tts/clarity/text2.txt', '/workspace/work/tts/v2/news.txt'])
txt += re.sub(r'\{[^}]*\}', '', ''.join(l for l in open('/workspace/work/polylex/blind60.txt', encoding='utf-8') if not l.startswith('#')))
lines = [l for l in txt.split('\n') if l.strip()] * 6
t0 = time.time()
if mode == 'old': g2p = hfront.g2p
else:
    import newfront; newfront.load('/workspace/work/polylex/' + {'A': 'merged_A.txt', 'AB': 'merged_AB.txt'}[mode]); g2p = newfront.g2p
g2p('预热一下')
load = time.time() - t0
n = sum(len(re.findall(r'[\u4e00-\u9fff]', l)) for l in lines)
t1 = time.time(); c1 = time.process_time()
for l in lines: g2p(l)
wall = time.time() - t1; cpu = time.process_time() - c1
r = dict(mode=mode, hanzi=n, load_s=round(load, 2), ms_per_1000=round(wall / n * 1e6, 1), cpu_ms_per_1000=round(cpu / n * 1e6, 1),
         rss_after_imports_mb=after_imports, peak_rss_mb=resource.getrusage(resource.RUSAGE_SELF).ru_maxrss // 1024)
print(json.dumps(r, ensure_ascii=False)); open('/workspace/work/polylex/bench.jsonl', 'a').write(json.dumps(r, ensure_ascii=False) + '\n')

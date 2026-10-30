"""python evalall.py dumpgold | python evalall.py <old|A|AB|...>  -> res_<mode>.json
Gold for 自测(text.txt)/盲测(text2.txt) is the existing gold.py (pypinyin + manual fixes), dumped in a clean
process so lexicon loading cannot change it. 新盲测60 scores only the marked polyphones."""
import sys, os, json, re, time, resource
sys.path.insert(0, '/workspace/work/tts/v2/pylib'); sys.path.insert(0, '/workspace/work/tts/clarity'); sys.path.insert(0, '/workspace/work/polylex')
os.chdir('/workspace/work/tts/clarity')
SETS = [('自测', 'text.txt'), ('盲测', 'text2.txt')]
B60 = '/workspace/work/polylex/blind60.txt'
def split_t(p):
    m = re.match(r'([a-zv]+)(\d)?$', p.replace('ü', 'v')); return (m.group(1), m.group(2) or '5') if m else (p, '?')
def judge(c, g, alt, r, nxt_gold_tone):
    if r == g or r in alt: return True
    gs, gt = split_t(g); rs, rt = split_t(r)
    if gs == rs and gt == '5': return True          # missing neutral tone: not counted (same as evalpy)
    if gs == rs and c in '一不': return True         # yi/bu sandhi handled later
    if gs == rs and gt == '3' and rt == '2' and nxt_gold_tone == '3': return True
    return False
def parse60():
    S = []
    for line in open(B60, encoding='utf-8'):
        line = line.strip()
        if not line or line.startswith('#'): continue
        plain = ''; marks = {}
        for m in re.finditer(r'(.)(\{[^}]*\})?', line):
            ch, mk = m.group(1), m.group(2)
            if mk: marks[len([x for x in plain if '\u4e00' <= x <= '\u9fff'])] = mk[1:-1].split('|')
            plain += ch
        S.append((plain, marks))
    return S
if __name__ == '__main__':
    mode = sys.argv[1]
    if mode == 'dumpgold':
        from gold import gold
        G = {}
        for nm, f in SETS:
            os.environ['GOLD'] = f; G[nm] = [(l, [(c, g, sorted(a)) for c, g, a in row]) for l, row in gold(f)]
        json.dump(G, open('/workspace/work/polylex/gold.json', 'w'), ensure_ascii=False); sys.exit()
    G = json.load(open('/workspace/work/polylex/gold.json'))
    r0 = resource.getrusage(resource.RUSAGE_SELF).ru_maxrss
    t0 = time.time()
    if mode == 'old':
        import hfront; g2p = hfront.g2p; hfront.g2p('预热')
    else:
        import newfront
        lex = {'A': 'merged_A.txt', 'AB': 'merged_AB.txt', 'Aneu': 'merged_A.txt', 'ABneu': 'merged_AB.txt'}[mode]
        newfront.load('/workspace/work/polylex/' + lex, neutral=('/workspace/work/polylex/paddle_neutral.txt' if 'neu' in mode else None))
        g2p = newfront.g2p; g2p('预热')
    load_s = time.time() - t0
    res = dict(mode=mode, load_s=round(load_s, 2), rss_mb=resource.getrusage(resource.RUSAGE_SELF).ru_maxrss // 1024, sets={})
    for nm, rows in G.items():
        errs = []; n = 0
        for li, (line, row) in enumerate(rows):
            rd = g2p(line); assert len(rd) == len(row)
            for k, ((c, g, alt), (c2, r)) in enumerate(zip(row, rd)):
                n += 1; nt = split_t(row[k + 1][1])[1] if k + 1 < len(row) else ''
                if not judge(c, g, set(alt), r, nt): errs.append(dict(句=li + 1, 原文=line, 字=c, 正确=g, 读成=r))
        res['sets'][nm] = dict(chars=n, errors=len(errs), list=errs)
    errs = []; n = 0
    for li, (line, marks) in enumerate(parse60()):
        rd = g2p(line)
        for k, gl in marks.items():
            n += 1; c, r = rd[k]
            if not judge(c, gl[0], set(gl[1:]), r, ''): errs.append(dict(句=li + 1, 原文=line, 字=c, 正确='/'.join(gl), 读成=r))
    res['sets']['新盲测60'] = dict(chars=n, errors=len(errs), list=errs)
    json.dump(res, open(f'/workspace/work/polylex/res_{mode}.json', 'w'), ensure_ascii=False, indent=1)
    print(mode, res['load_s'], res['rss_mb'], {k: v['errors'] for k, v in res['sets'].items()})

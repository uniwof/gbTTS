"""Build merged polyphone lexicon (word -> tone3 pinyin per char) from open-source sources.
Outputs: merged_A.txt (clean-licence sources only), merged_AB.txt (+ provenance-risk sources),
         jieba_user_*.txt, sources.json (counts)."""
import sys, re, json, ast
sys.path.insert(0, '/workspace/work/tts/v2/pylib')
from pypinyin.contrib.tone_convert import to_tone3
from pypinyin import pinyin_dict
HZ = re.compile(r'^[\u3400-\u9fff\U00020000-\U0002ffff]+$')
S = 'src/'
def norm(py):
    out = []
    for p in py.split():
        p = p.strip().lower().replace('ü', 'v').replace('u:', 'v')
        t = to_tone3(p, neutral_tone_with_five=True).replace('ü', 'v')
        if not re.match(r'^[a-z]+[1-5]$', t): return None
        out.append(t)
    return out
def ok(w, py): return py is not None and HZ.match(w) and len(w) == len(py) and len(w) >= 2
def read_ppd(f):
    D = {}
    for line in open(S + f, encoding='utf-8'):
        line = line.split('#')[0].strip()
        if not line or ':' not in line: continue
        w, p = line.split(':', 1); w = w.strip(); py = norm(p)
        if ok(w, py) and w not in D: D[w] = py
    return D
def read_pp(f):
    D = {}
    for w, v in json.load(open(S + f, encoding='utf-8')).items():
        py = norm(v[0])
        if ok(w, py): D[w] = py
    return D
def read_xinhua():
    D = {}
    for it in json.load(open(S + 'xinhua_idiom.json', encoding='utf-8')):
        w = it['word']; py = norm(re.sub(r'[，,·\-]', ' ', it['pinyin']))
        if ok(w, py) and w not in D: D[w] = py
    return D
def read_paddle():
    D = {}
    for line in open(S + 'paddle_polyphonic.yaml', encoding='utf-8'):
        m = re.match(r"\s+(\S+):\s*(\[.*\])", line)
        if m:
            w = m.group(1); py = [x.strip() for x in ast.literal_eval(m.group(2))]
            if ok(w, py): D[w] = py
    return D
def paddle_neutral():
    src = open(S + 'paddle_tone_sandhi.py', encoding='utf-8').read()
    m = re.search(r'self\.must_neural_tone_words = (\{.*?\})', src, re.S)
    return sorted(ast.literal_eval(m.group(1)))
SOURCES = [  # low -> high priority ; (name, tier, loader)
    ('pinyin-pro complete(jieba词表)', 'A', lambda: read_pp('pp_complete.json')),
    ('phrase-pinyin-data pinyin.txt', 'A', lambda: read_ppd('ppd_pinyin.txt')),
    ('phrase-pinyin-data zdic_cibs', 'B', lambda: read_ppd('ppd_zdic_cibs.txt')),
    ('phrase-pinyin-data zdic_cybs(成语)', 'B', lambda: read_ppd('ppd_zdic_cybs.txt')),
    ('chinese-xinhua idiom.json(成语)', 'B', read_xinhua),
    ('pinyin-pro modern(现汉7)', 'B', lambda: read_pp('pp_modern.json')),
    ('PaddleSpeech polyphonic.yaml', 'A', read_paddle),
    ('phrase-pinyin-data di.txt', 'A', lambda: read_ppd('ppd_di.txt')),
    ('phrase-pinyin-data overwrite.txt', 'A', lambda: read_ppd('ppd_overwrite.txt')),
]
def poly(w): return any(len(pinyin_dict.pinyin_dict.get(ord(c), '').split(',')) > 1 for c in w)
if __name__ == '__main__':
    stats = {}; loaded = []
    for name, tier, fn in SOURCES:
        D = fn(); loaded.append((name, tier, D)); stats[name] = dict(tier=tier, entries=len(D), poly_entries=sum(poly(w) for w in D))
        print(name, tier, len(D), flush=True)
    for tag, tiers in (('A', 'A'), ('AB', 'AB')):
        M = {}; who = {}
        for name, tier, D in loaded:
            if tier in tiers:
                for w, p in D.items(): M[w] = p; who[w] = name
        Mp = {w: p for w, p in M.items() if poly(w)}  # keep only words containing a polyphonic char
        with open(f'merged_{tag}.txt', 'w', encoding='utf-8') as f:
            f.write(f'# 合并多音字词库 {tag}：词 拼音(数字调,5=轻声)；只保留含多音字的词。来源见 来源与许可证.txt\n')
            for w in sorted(Mp): f.write(f"{w} {' '.join(Mp[w])}\n")
        stats[f'merged_{tag}'] = dict(all_words=len(M), kept_poly_words=len(Mp))
        print(tag, len(M), len(Mp))
    neu = paddle_neutral(); open('paddle_neutral.txt', 'w', encoding='utf-8').write('\n'.join(neu) + '\n')
    stats['PaddleSpeech must_neural_tone_words'] = dict(tier='A', entries=len(neu))
    # single-char table (rare chars): pinyin-data
    n = 0
    for line in open(S + 'pd_pinyin.txt', encoding='utf-8'):
        if line.startswith('U+'): n += 1
    stats['pinyin-data pinyin.txt(单字)'] = dict(tier='A', entries=n)
    json.dump(stats, open('sources.json', 'w'), ensure_ascii=False, indent=1)

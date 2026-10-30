#!/usr/bin/env python3
"""v0.5：为一个音色包的发音词典生成“私用区拼音字符”（PUA）条目，供 C# 多音字前端指定读音。
用法：python3 make_pua.py <原 lexicon.txt> <格式 melo|zy|py|piper> <输出 lexicon.txt> <输出 pua_map.txt>
  输出 lexicon = 原词典原样 + 每个带调拼音一行（U+E000 起的私用区字符 → 该拼音在这个模型里的音素写法）
  pua_map.txt  = 一行“拼音 字符”，程序读它把定好音的汉字换成对应字符。
算法与实验版 work/tts/clarity/inject.py + evalpy.py 相同（已在 Melo / zh-ll / fanchen / Matcha / Piper 上验证）。"""
import sys
from collections import Counter, defaultdict
sys.path.insert(0, '/workspace/work/tts/v2/pylib')
from pypinyin import pinyin_dict
from pypinyin.contrib.tone_convert import to_tone3
ZY = "ˉˊˇˋ˙"
TM = {'1': 'ˉ', '2': 'ˊ', '3': 'ˇ', '4': 'ˋ', '5': '˙'}

def load_lex(p):
    L = {}
    for line in open(p, encoding='utf-8'):
        a = line.rstrip('\n').split()
        if len(a) >= 2 and a[0] not in L: L[a[0]] = a[1:]
    return L

def syll(fmt, toks):
    if fmt == 'zy':
        out = []; cur = []
        for t in toks:
            cur.append(t)
            if t in ZY: out.append(' '.join(cur)); cur = []
        if cur: out.append(' '.join(cur) + ' ˉ')
        return out
    if fmt == 'melo':
        n = len(toks) // 2; ph, tn = toks[:n], toks[n:]
        return [f"{ph[i]} {ph[i+1]} {tn[i]}" for i in range(0, n - 1, 2)]
    if fmt == 'py': return toks
    if fmt == 'piper':
        out = []; cur = []
        for t in toks:
            if t == '_': out.append(' '.join(cur)); cur = []
            else: cur.append(t)
        if cur: out.append(' '.join(cur))
        return out
    raise ValueError(fmt)

def revmap(L, fmt):
    V = defaultdict(Counter)
    for cp, v in pinyin_dict.pinyin_dict.items():
        c = chr(cp); rs = v.split(',')
        if c in L:
            s = syll(fmt, L[c])
            if len(s) == 1:
                for r in rs: V[s[0]][to_tone3(r, neutral_tone_with_five=True)] += (3 if len(rs) == 1 else 1)
    return {k: c.most_common(1)[0][0] for k, c in V.items()}

def with_tone(fmt, key, tone):
    t = key.split()
    if fmt == 'zy': return ' '.join(t[:-1] + [TM[tone]]) if t[-1] in 'ˉˊˇˋ˙' else None
    if fmt == 'melo': return ' '.join(t[:2] + [tone])
    if fmt == 'py': return key[:-1] + tone if key[-1].isdigit() else None
    if fmt == 'piper': return ' '.join(t[:-1] + [tone]) if t[-1].isdigit() else None

def entry(fmt, key):
    t = key.split()
    if fmt == 'melo': return f"{t[0]} {t[1]} {t[2]} {t[2]}"
    if fmt == 'piper': return key + " _"
    return key

def build_map(lex, fmt):
    L = load_lex(lex); R = revmap(L, fmt); P = {}
    for k, p in R.items(): P.setdefault(p, k)
    for p, k in list(P.items()):
        for tn in '12345':
            q = p[:-1] + tn
            if q not in P:
                kk = with_tone(fmt, k, tn)
                if kk: P[q] = kk
    return P

def load_tokens(lex):
    import os
    t = os.path.join(os.path.dirname(os.path.abspath(lex)), 'tokens.txt')
    if not os.path.exists(t): return None
    return {l.split()[0] for l in open(t, encoding='utf-8') if l.split()}

def validate(P, fmt, tokens):
    """去掉模型 tokens.txt 里没有的音素组合（否则 sherpa 会报 Unknown token 并读不出这个字）。
    Matcha 的轻声是不带数字的音节（de、le、me），x5 先换成它，没有就去掉（该字交回模型自己的词典）。"""
    if tokens is None: return P, []
    out, dropped = {}, []
    for p, k in P.items():
        e = entry(fmt, k).split()
        if fmt == 'melo': e = e[:len(e) // 2]          # Melo：后一半是声调编号，不在 tokens.txt 里
        if all(t in tokens for t in e): out[p] = k; continue
        if fmt == 'py' and p.endswith('5') and p[:-1] in tokens: out[p] = p[:-1]; continue
        dropped.append(p)
    return out, dropped

def make(lex, fmt, out_lex, out_map):
    P, dropped = validate(build_map(lex, fmt), fmt, load_tokens(lex))
    if dropped: print(f'  {lex}: {len(dropped)} 个带调拼音在模型音素表里不存在，已跳过（例：{" ".join(dropped[:8])}）')
    with open(out_lex, 'w', encoding='utf-8') as f, open(out_map, 'w', encoding='utf-8') as m:
        f.write(open(lex, encoding='utf-8').read().rstrip('\n') + '\n')
        m.write('# 光标朗读 v0.5 多音字前端：拼音 → 本音色包 lexicon.txt 里的私用区字符\n')
        for i, (p, k) in enumerate(sorted(P.items())):
            ch = chr(0xE000 + i); f.write(f"{ch} {entry(fmt, k)}\n"); m.write(f"{p} {ch}\n")
    return len(P)

if __name__ == '__main__':
    print(make(*sys.argv[1:5]))

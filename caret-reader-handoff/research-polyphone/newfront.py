"""Experimental polyphone frontend v3 = merged open-source phrase lexicon + jieba user dict + existing
context rules (hfront.rules) for function words + user dict (highest priority). Does not modify the app.
Usage: newfront.load(lex='merged_A.txt', user='用户自定义词库.txt'); newfront.g2p(text) -> [(char, tone3)]"""
import os, re, sys, math, logging
sys.path.insert(0, '/workspace/work/tts/v2/pylib'); sys.path.insert(0, '/workspace/work/tts/clarity')
import jieba
jieba.setLogLevel(logging.ERROR)
from pypinyin import lazy_pinyin, Style
from pypinyin.constants import PHRASES_DICT
from pypinyin.contrib.tone_convert import to_tone3
import hfront  # reuse the existing context rules unchanged
HZ = re.compile(r'[\u3400-\u9fff\U00020000-\U0003134f]')
HZR = r'\u3400-\u9fff\U00020000-\U0003134f'
HERE = os.path.dirname(os.path.abspath(__file__))
W = {}; U = {}; NEU = set(); TOK = None
def _py(s):
    out = []
    for p in s.split():
        t = to_tone3(p.lower().replace('ü', 'v'), neutral_tone_with_five=True)
        if not re.match(r'^[a-z]+[1-5]$', t): raise ValueError(p)
        out.append(t)
    return ' '.join(out)
def read_user(path):
    """用户自定义词库：一行“词 拼音”，拼音可用数字调(hang2)或带调符号(háng)，5 或不写调号=轻声；# 开头为注释。"""
    D = {}
    if path and os.path.exists(path):
        for n, line in enumerate(open(path, encoding='utf-8-sig'), 1):
            line = line.split('#')[0].strip()
            if not line: continue
            w, _, p = line.partition(' ')
            try:
                py = _py(p)
                if len(py.split()) == len(w): D[w] = py
                else: print(f'用户词库第{n}行字数与拼音数不一致，已跳过：{line}')
            except ValueError: print(f'用户词库第{n}行拼音无法识别，已跳过：{line}')
    return D
def build_jieba_dict(words, out):
    """jieba main dict + every lexicon word, with the smallest frequency that still lets jieba cut it whole."""
    base = jieba.Tokenizer(); base.initialize(); F = base.FREQ; total = base.total
    lines = open(os.path.join(os.path.dirname(jieba.__file__), 'dict.txt'), encoding='utf-8').read().rstrip('\n').split('\n')
    add = 0
    for w in words:
        if F.get(w): continue
        lp = 0.0
        for seg in base.cut(w, HMM=False): lp += math.log(max(F.get(seg) or 1, 1) / total)
        lines.append(f'{w} {max(3, int(math.exp(lp) * total) + 1)}'); add += 1
    open(out, 'w', encoding='utf-8').write('\n'.join(lines) + '\n')
    return add
def load(lex=os.path.join(HERE, 'merged_A.txt'), user=os.path.join(HERE, '用户自定义词库.txt'), neutral=None):
    global W, U, NEU, TOK
    W = {}
    for line in open(lex, encoding='utf-8'):
        if line.startswith('#'): continue
        w, _, p = line.rstrip('\n').partition(' '); W[w] = p
    sup = os.path.join(os.path.dirname(lex), '单字补充表.txt')
    if os.path.exists(sup):
        from pypinyin import load_single_dict
        load_single_dict({ord(l[0]): l[2:].strip() for l in open(sup, encoding='utf-8') if not l.startswith('#') and len(l) > 2})
    U = read_user(user)
    if neutral: NEU = set(open(neutral, encoding='utf-8').read().split())
    jd = lex + '.jieba.txt'
    if not os.path.exists(jd): build_jieba_dict(list(W) + list(U), jd)
    TOK = jieba.Tokenizer(dictionary=jd); TOK.tmp_dir = HERE; TOK.initialize()
    for w in U: TOK.add_word(w, 10 ** 7)  # user words always win segmentation
def g2p(text):
    out = []
    for seg in re.split(r'([^\u3400-\u9fff]+)', text):
        if not seg or not HZ.match(seg[0]): continue
        chars = list(seg); pys = []; locked = []
        for w in TOK.cut(seg):
            if w in U: p = U[w].split(); lk = True
            elif len(w) > 1 and w in W: p = W[w].split(); lk = True
            else:
                p = lazy_pinyin(w, style=Style.TONE3, neutral_tone_with_five=True); lk = len(w) > 1 and w in PHRASES_DICT
            if len(w) > 1 and w in NEU and w not in U and p[-1][-1] != '5': p = p[:-1] + [p[-1][:-1] + '5']
            pys += p; locked += [lk] * len(w)
        for i, c in enumerate(chars):
            p = pys[i]
            if not locked[i]: p = hfront.rules(chars, i, p) or p
            out.append((c, p))
    return out
if __name__ == '__main__':
    load(*(sys.argv[1:2] or []))
    for l in sys.stdin: print(g2p(l.strip()))

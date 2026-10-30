#!/usr/bin/env python3
r"""v0.5 多音字前端：把实验版（work/polylex/newfront.py，"新词库 A 版"）用到的数据导出成 C# 程序读的纯文本。
输出到 frontend/data/（构建时复制进程序文件夹的 frontend\ 子目录）：
  dict.txt         jieba 主词典 + 词库 A 新增词（= 实验用的 merged_A.txt.jieba.txt，原样）
  hmm.txt          jieba 未登录词 HMM 概率（jieba.finalseg，MIT）
  polylex_a.txt    合并多音字词库 A（MIT/Apache 来源）+ 少量人工修正（lexicon_fixes.txt）
  char_pinyin.txt  单字默认读音（pypinyin 内置表 + pinyin-data 生僻字补充 2,512 字），数字调
  phrases.txt      pypinyin 内置词组（phrase-pinyin-data，MIT），数字调
不使用汉典/新华/现汉来源的任何数据；不导出 PaddleSpeech 必读轻声表（未启用）。"""
import os, sys, shutil
sys.path.insert(0, '/workspace/work/tts/v2/pylib')
HERE = os.path.dirname(os.path.abspath(__file__)); OUT = os.path.join(HERE, 'data'); PL = '/workspace/work/polylex'
os.makedirs(OUT, exist_ok=True)
from pypinyin import load_single_dict, lazy_pinyin, Style
from pypinyin.contrib.tone_convert import to_tone3
sup = {ord(l[0]): l[2:].strip() for l in open(f'{PL}/单字补充表.txt', encoding='utf-8') if not l.startswith('#') and len(l) > 2}
load_single_dict(sup)
from pypinyin.constants import PINYIN_DICT
from pypinyin.constants import PHRASES_DICT
def t3(p): return to_tone3(p, neutral_tone_with_five=True).replace('ü', 'v')
# 1) jieba dict
shutil.copyfile(f'{PL}/merged_A.txt.jieba.txt', f'{OUT}/dict.txt')
# 2) HMM
from jieba.finalseg import start_P, trans_P, emit_P
with open(f'{OUT}/hmm.txt', 'w', encoding='utf-8') as f:
    f.write('# jieba finalseg HMM (MIT, https://github.com/fxsjy/jieba)\n')
    for s, p in start_P.items(): f.write(f'S {s} {p!r}\n')
    for a, d in trans_P.items():
        for b, p in d.items(): f.write(f'T {a}{b} {p!r}\n')
    for s, d in emit_P.items():
        for c, p in d.items(): f.write(f'E {s} {c} {p!r}\n')
# 3) lexicon A + fixes
fixes = {}
for l in open(os.path.join(HERE, 'lexicon_fixes.txt'), encoding='utf-8'):
    l = l.split('#')[0].strip()
    if l: w, _, p = l.partition(' '); assert len(p.split()) == len(w), l; fixes[w] = p
n = 0
with open(f'{OUT}/polylex_a.txt', 'w', encoding='utf-8') as f:
    f.write('# 合并多音字词库 A：mozillazg/phrase-pinyin-data (MIT) + zh-lx/pinyin-pro (MIT) + PaddleSpeech polyphonic (Apache-2.0)，含人工修正\n')
    for l in open(f'{PL}/merged_A.txt', encoding='utf-8'):
        if l.startswith('#'): continue
        w, _, p = l.rstrip('\n').partition(' ')
        if w in fixes: p = fixes.pop(w); n += 1
        f.write(f'{w} {p}\n')
    for w, p in fixes.items(): f.write(f'{w} {p}\n'); n += 1
print('lexicon fixes applied:', n)
# 4) single chars (first reading = what lazy_pinyin uses), checked against lazy_pinyin
bad = 0
with open(f'{OUT}/char_pinyin.txt', 'w', encoding='utf-8') as f:
    f.write('# 单字默认读音：pypinyin 0.55 内置表（mozillazg/pinyin-data, MIT）+ pinyin-data 0.15.0 生僻字补充\n')
    for cp in sorted(PINYIN_DICT):
        p = t3(PINYIN_DICT[cp].split(',')[0])
        if cp < 0x10000 and 0x4e00 <= cp <= 0x9fff and chr(cp) not in PHRASES_DICT:
            q = lazy_pinyin(chr(cp), style=Style.TONE3, neutral_tone_with_five=True)[0]
            if q != p: bad += 1
        f.write(f'{chr(cp)} {p}\n')
print('chars', len(PINYIN_DICT), 'mismatch vs lazy_pinyin', bad)
# 5) pypinyin phrases
with open(f'{OUT}/phrases.txt', 'w', encoding='utf-8') as f:
    f.write('# pypinyin 0.55 内置词组（mozillazg/phrase-pinyin-data, MIT）\n')
    for w, v in PHRASES_DICT.items(): f.write(w + ' ' + ' '.join(t3(x[0]) for x in v) + '\n')
print('phrases', len(PHRASES_DICT), 'single-char keys', sum(1 for w in PHRASES_DICT if len(w) == 1))
for fn in sorted(os.listdir(OUT)): print(fn, os.path.getsize(f'{OUT}/{fn}'))

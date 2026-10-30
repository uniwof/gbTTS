"""python synth.py <sid> <old|new> <textfile> <outwav> : AISHELL-3 Piper, 2 threads, PUA-lexicon injection.
old = existing hfront (dict+rules); new = newfront (merged lexicon A + rules + user dict). App code untouched."""
import sys, os, time, json, numpy as np, soundfile as sf
sys.path.insert(0, '/workspace/work/tts/v2/pylib'); sys.path.insert(0, '/workspace/work/tts/clarity'); sys.path.insert(0, '/workspace/work/polylex')
import sherpa_onnx as so, inject, hfront, run
sid, front, textf, outw = int(sys.argv[1]), sys.argv[2], sys.argv[3], sys.argv[4]
D = '/workspace/work/tts/v2/models/piper-aishell3/'
lex = '/workspace/work/polylex/fixlex_a3.txt'
pua = inject.make_lexicon(D + 'lexicon.txt', 'piper', lex)
if front == 'new':
    import newfront; newfront.load('/workspace/work/polylex/merged_A.txt'); g2p = newfront.g2p
else: g2p = hfront.g2p
def convert(text):
    rd = inject.sandhi(g2p(text)); out = ''; k = 0
    for c in text:
        if k < len(rd) and c == rd[k][0]: out += pua.get(rd[k][1], c); k += 1
        else: out += c
    return out
mc = so.OfflineTtsModelConfig(num_threads=2)
mc.vits = so.OfflineTtsVitsModelConfig(model=D + 'model.onnx', lexicon=lex, tokens=D + 'tokens.txt', noise_scale=0.333, noise_scale_w=0.333)
tts = so.OfflineTts(so.OfflineTtsConfig(model=mc, rule_fsts=','.join(D + f for f in ('phone.fst', 'date.fst', 'number.fst')), max_num_sentences=1))
sr = tts.sample_rate; parts = []
for line in open(textf, encoding='utf-8').read().split('\n'):
    if not line.strip(): continue
    for c, p in run.clauses(line):
        a = np.array(tts.generate(convert(c), sid=sid, speed=1.0).samples, dtype=np.float32)
        parts += [a, np.zeros(int(p * sr), np.float32)]
sf.write(outw, np.concatenate(parts), sr); print('ok', outw)

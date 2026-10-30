#!/usr/bin/env python3
"""v0.4 音色包定义 + 组装。
用法：python3 packs.py assemble <输出文件夹>    组装全部音色包（模型文件用硬链接，省沙盒空间）
      python3 packs.py list                    打印包 id
每个包 = 一个英文名文件夹：voice.json + 模型文件 (+ sample.mp3，由 build.sh 生成后放入)。
"""
import json, os, shutil, sys

TTS = '/workspace/work/tts'
FST4 = ['date.fst', 'phone.fst', 'number.fst', 'new_heteronym.fst']
FST3 = ['date.fst', 'phone.fst', 'number.fst']

MODELS = {
    'melo':    dict(src=f'{TTS}/vits-melo-tts-zh_en', engine='melo', pua='melo', model='model.onnx', lexicon='lexicon.txt', dict='dict',
                    fsts=FST4, extra=['LICENSE', 'README.md'], load=4),
    'matcha':  dict(src=f'{TTS}/matcha-icefall-zh-baker', engine='matcha', pua='py', model='model-steps-3.onnx', lexicon='lexicon.txt',
                    dict='dict', fsts=FST3, vocoder=(f'{TTS}/vocos-22khz-univ.onnx', 'vocos-22khz-univ.onnx'), extra=['README.md'], load=2),
    'zhll':    dict(src=f'{TTS}/sherpa-onnx-vits-zh-ll', engine='vits', pua='zy', model='model.onnx', lexicon='lexicon.txt', dict='dict',
                    fsts=FST4, extra=['README.md'], load=2),
    'fanchen': dict(src=f'{TTS}/vits-zh-hf-fanchen-C', engine='vits', pua='zy', model='vits-zh-hf-fanchen-C.onnx', lexicon='lexicon.txt',
                    dict='dict', fsts=FST4, extra=[], load=2),
    'chaowen': dict(src=f'{TTS}/dl/vits-piper-zh_CN-chaowen-medium', engine='piper', pua='piper', model='zh_CN-chaowen-medium.onnx',
                    lexicon='lexicon.txt', dict=None, fsts=FST3, extra=['MODEL_CARD'], load=1.5),
}

# id, 模型, sid, 显示名, 简介, 排序, 增益（按实测响度拉齐，见 build 日志）
VOICES = [
    ('melo-0',      'melo',    0,   '女声 · 自然 (Melo)',           '最接近真人的自然女声，吐字清楚，适合长文章。', 10),
    ('matcha-0',    'matcha',  0,   '女声 · 标准 (Matcha)',         '反应最快的标准女声，老电脑也很流畅。', 11),
    ('zhll-2',      'zhll',    2,   '女声 · 柔和 (zh-ll #2)',       '声音柔和、语气平稳，听久了不累。', 12),
    ('zhll-3',      'zhll',    3,   '女声 · 低沉 (zh-ll #3)',       '偏低沉的女声，沉稳安静。', 13),
    ('zhll-0',      'zhll',    0,   '女声 · 清亮 (zh-ll #0)',       '明亮清晰的女声，适合资讯和短文。', 14),
    ('fanchen-29',  'fanchen', 29,  '女声 · 温婉 (fanchen-C #29)',  '温婉细腻的女声，适合散文和小说。', 15),
    ('fanchen-7',   'fanchen', 7,   '女声 · 沉静 (fanchen-C #7)',   '沉静克制的女声，适合说明文和学习资料。', 16),
    ('fanchen-24',  'fanchen', 24,  '女声 · 知性 (fanchen-C #24)',  '知性大方的女声，适合新闻和评论。', 17),
    ('zhll-1',      'zhll',    1,   '男声 · 沉稳 (zh-ll #1)',       '低沉稳重的男声，平静从容。', 20),
    ('zhll-4',      'zhll',    4,   '男声 · 磁性 (zh-ll #4)',       '略低的磁性男声，语调平稳自然。', 21),
    ('fanchen-119', 'fanchen', 119, '男声 · 温和 (fanchen-C #119)', '温和冷静的男声，像娓娓道来。', 22),
    ('chaowen-0',   'chaowen', 0,   '男声 · 清朗 (Piper chaowen)',  '年轻清朗的男声，合成最快，老电脑首选。', 23),
]

GAINS_FILE = os.path.join(os.path.dirname(os.path.abspath(__file__)), 'config', 'gains.json')


def gains():
    try:
        return json.load(open(GAINS_FILE))
    except Exception:
        return {}


def link(src, dst):
    if os.path.isdir(src):
        shutil.copytree(src, dst, copy_function=link)
        return dst
    try:
        os.link(src, dst)
    except OSError:
        shutil.copy2(src, dst)
    return dst


HERE = os.path.dirname(os.path.abspath(__file__))
PUA_CACHE = os.path.join(HERE, 'pk', 'pua')


def pua_files(mk):
    """v0.5：每个模型生成一次“词典 + 私用区拼音条目”和 pua_map.txt（多音字前端用），缓存在 pk/pua/<模型>/。"""
    m = MODELS[mk]
    d = os.path.join(PUA_CACHE, mk)
    lex, pm = os.path.join(d, 'lexicon.txt'), os.path.join(d, 'pua_map.txt')
    src = os.path.join(m['src'], m['lexicon'])
    if not (os.path.exists(lex) and os.path.exists(pm) and os.path.getmtime(lex) >= os.path.getmtime(src)):
        os.makedirs(d, exist_ok=True)
        sys.path.insert(0, os.path.join(HERE, 'frontend'))
        import make_pua
        n = make_pua.make(src, m['pua'], lex, pm)
        print(f'pua {mk}: {n} 个带调拼音')
    return lex, pm


def assemble(out):
    os.makedirs(out, exist_ok=True)
    g = gains()
    for vid, mk, sid, name, desc, order in VOICES:
        m = MODELS[mk]
        d = os.path.join(out, vid)
        if os.path.exists(d):
            shutil.rmtree(d)
        os.makedirs(d)
        files = [m['model'], 'tokens.txt', m['lexicon']] + m['fsts'] + m['extra']
        for f in files:
            link(os.path.join(m['src'], f), os.path.join(d, f))
        if m['dict']:
            link(os.path.join(m['src'], m['dict']), os.path.join(d, m['dict']))
        # v0.5：词典换成“原词典 + 私用区拼音条目”，并放 pua_map.txt
        plex, ppm = pua_files(mk)
        os.remove(os.path.join(d, m['lexicon']))
        link(plex, os.path.join(d, m['lexicon']))
        link(ppm, os.path.join(d, 'pua_map.txt'))
        if 'vocoder' in m:
            link(m['vocoder'][0], os.path.join(d, m['vocoder'][1]))
        fj = dict(model=m['model'], tokens='tokens.txt', lexicon=m['lexicon'])
        if m['dict']:
            fj['dictDir'] = m['dict']
        if 'vocoder' in m:
            fj['vocoder'] = m['vocoder'][1]
        fj['ruleFsts'] = m['fsts']
        fj['puaMap'] = 'pua_map.txt'
        vj = dict(formatVersion=1, id=vid, name=name, engine=m['engine'], sid=sid,
                  gain=round(g.get(vid, 1.0), 2), speed=1.0, description=desc, order=order,
                  sample='sample.mp3', icon='icon.png', approxLoadSeconds=m['load'],
                  modelBytes=os.path.getsize(os.path.join(d, m['model'])), files=fj)
        with open(os.path.join(d, 'voice.json'), 'w', encoding='utf-8') as fp:
            json.dump(vj, fp, ensure_ascii=False, indent=2)
    print('assembled', len(VOICES), 'packs ->', out)


if __name__ == '__main__':
    if sys.argv[1] == 'assemble':
        assemble(sys.argv[2])
    elif sys.argv[1] == 'list':
        print(' '.join(v[0] for v in VOICES))

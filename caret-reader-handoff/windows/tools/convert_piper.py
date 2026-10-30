#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
光标朗读 第 2 轮：把朋友导出的 Piper/VITS onnx 文件夹转换成朗读器能直接加载的音色包。

用法（一句话）：
    python convert_piper.py <朋友的模型文件夹> <输出文件夹> [--name 显示名] [--id 英文id] [--check] [--no-audition]

输入文件夹：一个 .onnx 文件 + 同名 .onnx.json（Piper 导出时自带的配置）。
输出：<输出文件夹>/<id>/ 里有 voice.json（含 cpm 和 license.commercialOk）、sample.wav 试听（装了 sherpa-onnx 才有）、model.onnx（已写入 sherpa-onnx 需要的元数据）、tokens.txt、
      lexicon.txt（+ pua_map.txt 多音字前端），把这个文件夹放进朗读器的 voices\\ 里即可。

依赖：onnx、numpy（Apache-2.0 / BSD）；中文词典用 pypinyin（MIT）生成。
安全：只读取 .onnx 和 .json；永远不加载 .ckpt / .pth（那种文件可能含可执行代码）。
不认识的格式：打印中文原因，退出码 2，不崩溃。
"""
import argparse
import json
import os
import re
import shutil
import sys
import tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
FRONTEND = os.path.join(os.path.dirname(HERE), 'frontend')
INITIALS = ['zh', 'ch', 'sh', 'b', 'p', 'm', 'f', 'd', 't', 'n', 'l', 'g', 'k', 'h',
            'j', 'q', 'x', 'r', 'z', 'c', 's', 'y', 'w']
BIG_MODEL_INPUTS = ('bert', 'ssl', 'prompt', 'hubert', 'wav2vec', 'cnhubert', 'ref_')


class Refuse(Exception):
    """不能转换：消息是给用户看的中文原因。"""


def say(msg):
    try:
        print(msg, flush=True)
    except UnicodeEncodeError:   # Windows 旧控制台
        sys.stdout.buffer.write((msg + '\n').encode('utf-8', 'replace'))


# ---------------------------------------------------------------- 体检

def find_model(src):
    if not os.path.isdir(src):
        raise Refuse(f'找不到文件夹：{src}')
    names = os.listdir(src)
    onnx = [n for n in names if n.lower().endswith('.onnx')]
    unsafe = [n for n in names if n.lower().endswith(('.ckpt', '.pth', '.pt', '.bin', '.safetensors'))]
    if not onnx:
        if unsafe:
            raise Refuse('文件夹里只有训练用的权重文件（' + '、'.join(unsafe[:3]) + '），没有 .onnx。\n'
                         '为了安全，本脚本不加载 .ckpt/.pth。请让朋友在训练软件里“导出 onnx”后再发来。')
        raise Refuse('文件夹里没有 .onnx 文件。')
    if len(onnx) > 1:
        raise Refuse('文件夹里有多个 .onnx（' + '、'.join(sorted(onnx)[:5]) + '）。\n'
                     '这通常是 GPT-SoVITS / CosyVoice / Bert-VITS2 这类大型模型，无法在低配电脑上实时朗读，暂不支持。\n'
                     '如果只是多放了文件，请只留一个模型和它的 .onnx.json 再试。')
    model = os.path.join(src, onnx[0])
    cfg = None
    for cand in (model + '.json', os.path.splitext(model)[0] + '.json', os.path.join(src, 'config.json')):
        if os.path.isfile(cand):
            cfg = cand
            break
    return model, cfg


def inspect(model_path, cfg_path):
    """返回 (kind, info)。kind: piper-pinyin / piper-espeak / sherpa-ready。"""
    try:
        import onnx
    except ImportError:
        raise Refuse('缺少 onnx 库。请先运行：pip install onnx numpy pypinyin')
    try:
        m = onnx.load(model_path, load_external_data=False)
    except Exception as e:
        raise Refuse(f'这个 .onnx 文件读不出来（可能没下载完整或不是模型文件）：{e}')
    inputs = [i.name for i in m.graph.input]
    if not inputs:
        raise Refuse('这个 .onnx 文件是空的或已损坏（里面没有模型），请重新导出或重新下载。')
    meta = {p.key: p.value for p in m.metadata_props}
    low = ' '.join(inputs).lower()
    if any(k in low for k in BIG_MODEL_INPUTS):
        raise Refuse(f'模型输入里有 {inputs}，这是需要参考音频/BERT 的大型模型，无法在低配电脑实时朗读，暂不支持。')
    info = dict(inputs=inputs, meta=meta, model=m)
    if {'input', 'input_lengths', 'scales'} <= set(inputs):
        if not cfg_path:
            raise Refuse('这是 Piper 模型，但缺少同目录的 .onnx.json 配置文件（导出时会一起生成）。请把它一起发来。')
        try:
            cfg = json.load(open(cfg_path, encoding='utf-8'))
        except Exception as e:
            raise Refuse(f'配置文件 {os.path.basename(cfg_path)} 不是合法的 JSON：{e}')
        if not isinstance(cfg.get('phoneme_id_map'), dict) or not cfg['phoneme_id_map']:
            raise Refuse('配置文件里没有 phoneme_id_map（音素表），不像 Piper 导出的配置。')
        info['cfg'] = cfg
        ptype = str(cfg.get('phoneme_type', 'espeak')).lower()
        if ptype == 'pinyin':
            return 'piper-pinyin', info
        raise Refuse(f'这是 Piper 的 {ptype} 音素版（靠 espeak 发音），中文多音字准确率低，'
                     '而且朗读器的轻量版不带 espeak 数据，暂不转换。\n'
                     '建议让朋友用 Piper 的 pinyin（拼音）音素训练/导出，例如从 zh_CN xiao_ya / chaowen 微调。')
    if meta.get('model_type') in ('vits', 'melo-vits') and {'x', 'x_length'} <= set(inputs):
        return 'sherpa-ready', info
    raise Refuse(f'不认识的模型结构（输入：{inputs}）。目前只支持 Piper 拼音版 onnx。')


# ---------------------------------------------------------------- 生成

def write_tokens(cfg, out):
    with open(os.path.join(out, 'tokens.txt'), 'w', encoding='utf-8') as f:
        for sym, ids in cfg['phoneme_id_map'].items():
            if not ids:
                continue
            f.write(f'{sym} {ids[0]}\n')   # 与 sherpa-onnx 官方脚本相同（含空格符号）


def split_syllable(s):
    """'zhong1' → ['zh','ong','1','_']；零声母用 Ø（与 sherpa 官方 chaowen 词典一致）。"""
    tone = s[-1] if s and s[-1].isdigit() else '5'
    body = s[:-1] if s and s[-1].isdigit() else s
    for i in INITIALS:
        if body.startswith(i) and len(body) > len(i):
            return [i, body[len(i):], tone, '_']
    return ['Ø', body, tone, '_']


def write_lexicon(cfg, out):
    try:
        from pypinyin import pinyin_dict, phrases_dict
        from pypinyin.contrib.tone_convert import to_tone3
    except ImportError:
        raise Refuse('生成中文词典需要 pypinyin（MIT 许可）。请先运行：pip install pypinyin')
    known = set(cfg['phoneme_id_map'].keys())

    def t3(p):
        return to_tone3(p, v_to_u=False, neutral_tone_with_five=True).replace('ü', 'v')

    def ok(ph):
        return all(x in known for x in ph)

    n = skipped = 0
    with open(os.path.join(out, 'lexicon.txt'), 'w', encoding='utf-8') as f:
        for cp, ps in sorted(pinyin_dict.pinyin_dict.items()):
            ph = split_syllable(t3(ps.split(',')[0]))
            if ok(ph):
                f.write(chr(cp) + ' ' + ' '.join(ph) + '\n'); n += 1
            else:
                skipped += 1
        for w, pl in sorted(phrases_dict.phrases_dict.items()):
            if ' ' in w or not pl:
                continue
            ph = sum((split_syllable(t3(p[0])) for p in pl), [])
            if ok(ph):
                f.write(w + ' ' + ' '.join(ph) + '\n'); n += 1
            else:
                skipped += 1
    if n < 1000:
        raise Refuse('这个模型的音素表和中文拼音对不上（生成的词典几乎是空的），可能不是中文拼音模型。')
    return n, skipped


def add_pua(out):
    """多音字前端：词典末尾加私用区拼音条目 + pua_map.txt（借用朗读器仓库里的 frontend/make_pua.py；没有就跳过）。"""
    if not os.path.isfile(os.path.join(FRONTEND, 'make_pua.py')):
        return None
    sys.path.insert(0, FRONTEND)
    try:
        import make_pua
        lex = os.path.join(out, 'lexicon.txt')
        tmp = os.path.join(out, 'lexicon.pua.tmp')
        n = make_pua.make(lex, 'piper', tmp, os.path.join(out, 'pua_map.txt'))
        os.replace(tmp, lex)
        return n
    except Exception as e:
        say(f'  （多音字前端表没生成，不影响朗读：{e}）')
        return None


def write_model(info, src_model, out, kind):
    import onnx
    m = info['model']
    if kind == 'sherpa-ready':
        shutil.copy2(src_model, os.path.join(out, 'model.onnx'))
        return int(info['meta'].get('sample_rate', '0') or 0), int(info['meta'].get('n_speakers', '1') or 1)
    cfg = info['cfg']
    sr = int(cfg.get('audio', {}).get('sample_rate', 22050))
    ns = int(cfg.get('num_speakers', 1) or 1)
    meta = {
        'model_type': 'vits', 'comment': 'piper', 'language': 'Chinese',
        'voice': str(cfg.get('espeak', {}).get('voice', 'zh')), 'version': '1',
        'has_espeak': '0', 'n_speakers': str(ns), 'sample_rate': str(sr),
    }
    del m.metadata_props[:]
    for k, v in meta.items():
        p = m.metadata_props.add(); p.key = k; p.value = v
    onnx.save(m, os.path.join(out, 'model.onnx'))
    return sr, ns


def ascii_id(s):
    s = re.sub(r'[^A-Za-z0-9_-]+', '-', s).strip('-').lower()
    return s[:40] or 'my-voice'


# ---------------------------------------------------------------- 试听 + 测 cpm

AUDITION_TEXT = '你好，这是我的声音。今天天气很好，我们一起出去走走吧。'
FALLBACK_CLAUSES = ['清晨的阳光照进小院', '奶奶坐在门口择菜', '一边哼着年轻时学会的老歌', '院子里的桂花开得正好',
                    '风一吹，香气就飘满了整条小巷', '隔壁的孩子背着书包跑过', '回头冲奶奶喊了一声早上好']


def fixed_clauses():
    """与 C# PaceCalibrator.FixedClauses 同一段固定文本（读源码；找不到就用精简版，cpm 略有偏差，朗读器会按实际朗读校正）。"""
    p = os.path.join(os.path.dirname(HERE), 'src', 'CaretReader.Core', 'Pacing', 'Pacer.cs')
    try:
        txt = open(p, encoding='utf-8').read()
        blk = txt.split('FixedClauses =', 1)[1].split('};', 1)[0]
        cl = re.findall(r'"([^"]+)"', blk)
        if len(cl) >= 10:
            return cl
    except Exception:
        pass
    return FALLBACK_CLAUSES


def trim_silence(samples, sr):
    """与 C# SilenceTrimmer 同一规则：10ms 帧、阈值 max(-50dB, 峰值-35dB)，开头留 20ms、结尾留 50ms。"""
    import numpy as np
    x = np.asarray(samples, dtype=np.float32)
    frame = max(1, int(sr * 0.010))
    n = len(x) // frame
    if n == 0 or not x.size:
        return x
    peak = float(np.max(np.abs(x)))
    if peak <= 1e-6:
        return x
    db = lambda v: 20 * np.log10(max(v, 1e-9))
    thr = max(-50.0, db(peak) - 35.0)
    rms = np.sqrt(np.mean(x[:n * frame].reshape(n, frame) ** 2, axis=1))
    loud = [i for i in range(n) if db(float(rms[i])) > thr]
    if not loud:
        return x
    a = max(0, loud[0] * frame - int(sr * 0.020))
    b = min(len(x), (loud[-1] + 1) * frame + int(sr * 0.050))
    return x[a:b]


def audition(pack_dir, files, ns):
    """用 sherpa-onnx（可选依赖，Apache-2.0）合成试听 WAV，并测 speed=1 的字/分。没装就跳过，返回 (None, False)。"""
    try:
        import sherpa_onnx
        import numpy as np
        import wave
    except ImportError:
        say('（没装 sherpa-onnx，跳过试听 WAV 和语速测量；朗读器首次朗读时会自己测。需要的话：pip install sherpa-onnx==1.13.8）')
        return None, False
    cwd = os.getcwd()
    try:
        os.chdir(pack_dir)    # 相对路径，避开中文路径问题
        vits = sherpa_onnx.OfflineTtsVitsModelConfig(
            model=files['model'], tokens=files['tokens'], lexicon=files.get('lexicon', ''))
        cfg = sherpa_onnx.OfflineTtsConfig(
            model=sherpa_onnx.OfflineTtsModelConfig(vits=vits, num_threads=2, provider='cpu'), max_num_sentences=1)
        tts = sherpa_onnx.OfflineTts(cfg)
        chars = 0
        secs = 0.0
        for c in fixed_clauses():
            a = tts.generate(c, sid=0, speed=1.0)
            secs += len(trim_silence(a.samples, a.sample_rate)) / a.sample_rate
            chars += len(re.findall(r'[\u4e00-\u9fff]', c))
        cpm = round(chars / secs * 60, 1) if secs > 0 else None
        a = tts.generate(AUDITION_TEXT, sid=0, speed=1.0)
        pcm = (np.clip(np.asarray(a.samples, dtype=np.float32), -1, 1) * 32767).astype('<i2')
        with wave.open(os.path.join(pack_dir, 'sample.wav'), 'wb') as w:
            w.setnchannels(1); w.setsampwidth(2); w.setframerate(a.sample_rate); w.writeframes(pcm.tobytes())
        say(f'  试听 WAV 已生成；速度 1.0 时约 {cpm} 字/分')
        return cpm, True
    except Exception as e:
        say(f'  （试听/测速失败，已跳过：{type(e).__name__}: {e}）')
        return None, False
    finally:
        os.chdir(cwd)


def convert(src, dst_root, name=None, vid=None, check_only=False, audition_on=True):
    model, cfg = find_model(src)
    kind, info = inspect(model, cfg)
    say(f'识别结果：{ {"piper-pinyin": "Piper 拼音版（推荐）", "sherpa-ready": "已经是 sherpa-onnx 格式"}[kind] }')
    if check_only:
        say('体检通过，可以转换（去掉 --check 即开始转换）。')
        return None
    base = os.path.basename(os.path.splitext(model)[0])
    vid = ascii_id(vid or base)
    out = os.path.join(dst_root, vid)
    work = tempfile.mkdtemp(prefix='voicepack_', dir=dst_root if os.path.isdir(dst_root) else None)
    try:
        files = {'model': 'model.onnx', 'tokens': 'tokens.txt'}
        if kind == 'piper-pinyin':
            write_tokens(info['cfg'], work)
            n, skipped = write_lexicon(info['cfg'], work)
            say(f'  词典：{n} 条' + (f'（{skipped} 条音素对不上已跳过）' if skipped else ''))
            files['lexicon'] = 'lexicon.txt'
            if add_pua(work):
                files['puaMap'] = 'pua_map.txt'
        else:
            for f in ('tokens.txt', 'lexicon.txt'):
                p = os.path.join(src, f)
                if os.path.isfile(p):
                    shutil.copy2(p, os.path.join(work, f))
            if not os.path.isfile(os.path.join(work, 'tokens.txt')):
                raise Refuse('这个 sherpa 格式模型缺少 tokens.txt。')
            if os.path.isfile(os.path.join(work, 'lexicon.txt')):
                files['lexicon'] = 'lexicon.txt'
        sr, ns = write_model(info, model, work, kind)
        files['ruleFsts'] = []
        vj = {
            'formatVersion': 1, 'id': vid, 'name': (name or base)[:40], 'engine': 'piper' if kind == 'piper-pinyin' else 'vits',
            'sid': 0, 'gain': 1.0, 'speed': 1.0, 'description': '由 convert_piper.py 转换', 'order': 50,
            'approxLoadSeconds': 2, 'modelBytes': os.path.getsize(os.path.join(work, 'model.onnx')),
            'files': files,
            'caps': {'speakers': ns, 'sampleRate': sr},
            'license': {'commercialOk': False, 'note': '朋友自训模型，许可请向作者确认'},
        }
        cpm, has_wav = audition(work, files, ns) if audition_on else (None, False)
        if has_wav:
            vj['sample'] = 'sample.wav'
        if cpm:
            vj['cpm'] = cpm
            vj['pacing'] = {'cpmAtSpeed1': cpm}
        with open(os.path.join(work, 'voice.json'), 'w', encoding='utf-8') as f:
            json.dump(vj, f, ensure_ascii=False, indent=2)
        if os.path.exists(out):
            shutil.rmtree(out)
        os.replace(work, out)
        work = None
        say(f'完成：{out}\n把这个文件夹复制到朗读器的 voices 文件夹里，重新打开音色列表即可。')
        return out
    finally:
        if work and os.path.isdir(work):
            shutil.rmtree(work, ignore_errors=True)


def main(argv=None):
    ap = argparse.ArgumentParser(description='把 Piper/VITS onnx 文件夹转换成光标朗读的音色包')
    ap.add_argument('src', help='朋友的模型文件夹（.onnx + .onnx.json）')
    ap.add_argument('dst', nargs='?', default='.', help='输出文件夹（会在里面建 <id> 子文件夹）')
    ap.add_argument('--name', help='显示名，例如 “小明的声音”')
    ap.add_argument('--id', help='英文 id（文件夹名），默认用模型文件名')
    ap.add_argument('--check', action='store_true', help='只体检，不转换')
    ap.add_argument('--no-audition', action='store_true', help='不生成试听 WAV、不测语速（没装 sherpa-onnx 时自动跳过）')
    a = ap.parse_args(argv)
    try:
        if not a.check:
            os.makedirs(a.dst, exist_ok=True)
        convert(a.src, a.dst, a.name, a.id, a.check, not a.no_audition)
        return 0
    except Refuse as e:
        say('无法转换：' + str(e))
        return 2
    except KeyboardInterrupt:
        say('已取消。')
        return 130
    except Exception as e:   # 兜底：不崩溃，给出原因
        say(f'转换时出了意外错误：{type(e).__name__}: {e}\n请把这段文字和模型文件夹一起发给 Hark。')
        return 1


if __name__ == '__main__':
    sys.exit(main())

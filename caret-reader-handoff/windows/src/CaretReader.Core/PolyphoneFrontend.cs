using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace CaretReader.Core;

/// <summary>
/// v0.5 多音字前端（“新词库 A 版”，用户 2026-10-14 批准）。
///
/// 为什么需要它：sherpa-onnx 自己只按固定发音表逐词查，不看上下文，多音字（银行/行走、还钱/还是……）经常读错。
/// 这里先把每个汉字的正确拼音算出来，再把汉字换成音色包词典里“只代表这个拼音”的私用区字符（PUA，见 PuaMap），
/// 这样不用改 sherpa-onnx 就能让模型按我们给的读音读。
///
/// 定音优先级（从高到低），和实验版 work/polylex/newfront.py 完全一致：
///   ① 用户自定义词库（用户自定义词库.txt，一行“词 拼音”，数字调或调号，轻声写 5）
///   ② 合并词库 A（polylex_a.txt，约 27.7 万个含多音字的词；来源 MIT/Apache，见 THIRD_PARTY_NOTICES.txt）
///   ③ pypinyin 内置词组 + 单字读音，再由上下文规则（还/了/得/着/地/的/调/背/数/弹/朝/为）修正没被①②定音的字
/// 分词：jieba 算法的 C# 移植（词典 = jieba 主词典 + 词库 A 新增词，未登录词用 HMM），用户词一定能整切出来。
/// 生僻字：单字表含 pinyin-data 补充的 2,512 个字（多为扩展 B 区以后的字，按单字读音处理）。
/// 不启用 PaddleSpeech 必读轻声表。最后做“一/不”变调和三声连读变调。
///
/// 线程安全：加载完成后只读，可多线程调用。
/// </summary>
public sealed class PolyphoneFrontend
{
    public const string DataDirName = "frontend";
    public const string UserDictFileName = "用户自定义词库.txt";

    // ---------- 数据 ----------
    private readonly Dictionary<string, int> _freq = new(600_000, StringComparer.Ordinal);   // jieba 词频（前缀 = 0）
    private double _logTotal;
    private readonly Dictionary<string, string> _lexA = new(290_000, StringComparer.Ordinal); // 词库 A
    private readonly Dictionary<string, string> _phrases = new(50_000, StringComparer.Ordinal); // pypinyin 词组
    private readonly HashSet<string> _phrasePrefix = new(StringComparer.Ordinal);
    private readonly Dictionary<int, string> _chars = new(45_000);                              // 单字默认读音
    private readonly Dictionary<string, string> _user = new(StringComparer.Ordinal);            // 用户词库
    // HMM（B M E S）
    private readonly double[] _start = new double[4];
    private readonly double[,] _trans = new double[4, 4];
    private readonly Dictionary<char, double>[] _emit = { new(), new(), new(), new() };
    private const double MinFloat = -3.14e100;
    private const string States = "BMES";

    /// <summary>用户词库里被跳过的行（中文说明，写进日志）。</summary>
    public List<string> UserDictWarnings { get; } = new();
    public string? UserDictPath { get; private set; }
    public int UserWordCount => _user.Count;
    public double LoadSeconds { get; private set; }

    // ====================================================================================
    //  加载
    // ====================================================================================

    /// <summary>从 dataDir（程序文件夹\frontend）加载；userDictPath 可为 null 或不存在。出错抛异常。</summary>
    public static PolyphoneFrontend Load(string dataDir, string? userDictPath)
    {
        var sw = Stopwatch.StartNew();
        var f = new PolyphoneFrontend();
        f.LoadJiebaDict(Path.Combine(dataDir, "dict.txt"));
        f.LoadHmm(Path.Combine(dataDir, "hmm.txt"));
        f.LoadLexA(Path.Combine(dataDir, "polylex_a.txt"));
        f.LoadPhrases(Path.Combine(dataDir, "phrases.txt"));
        f.LoadChars(Path.Combine(dataDir, "char_pinyin.txt"));
        if (!string.IsNullOrEmpty(userDictPath) && File.Exists(userDictPath))
        {
            f.UserDictPath = userDictPath;
            f.LoadUserDict(userDictPath);
        }
        f.LoadSeconds = sw.Elapsed.TotalSeconds;
        return f;
    }

    private static IEnumerable<string> Lines(string path)
    {
        using var r = new StreamReader(path, new UTF8Encoding(false), true, 1 << 16);
        string? l;
        while ((l = r.ReadLine()) != null) yield return l;
    }

    private void LoadJiebaDict(string path)
    {
        long total = 0;
        foreach (var line in Lines(path))
        {
            int a = line.IndexOf(' ');
            if (a <= 0) continue;
            int b = line.IndexOf(' ', a + 1);
            string w = line.Substring(0, a);
            int fq = int.Parse(b < 0 ? line.AsSpan(a + 1) : line.AsSpan(a + 1, b - a - 1), CultureInfo.InvariantCulture);
            _freq[w] = fq; total += fq;
            for (int k = 1; k < w.Length; k++)
            {
                string pre = w.Substring(0, k);
                if (!_freq.ContainsKey(pre)) _freq[pre] = 0;
            }
        }
        _total = total;
    }
    private long _total;

    private void LoadHmm(string path)
    {
        foreach (var line in Lines(path))
        {
            if (line.Length == 0 || line[0] == '#') continue;
            var p = line.Split(' ');
            double v = double.Parse(p[^1], CultureInfo.InvariantCulture);
            switch (p[0])
            {
                case "S": _start[States.IndexOf(p[1][0])] = v; break;
                case "T": _trans[States.IndexOf(p[1][0]), States.IndexOf(p[1][1])] = v; break;
                case "E": _emit[States.IndexOf(p[1][0])][p[2][0]] = v; break;
            }
        }
        // 没有出现的转移 = MIN_FLOAT（jieba 用 dict.get(y, MIN_FLOAT)）
        var seen = new bool[4, 4];
        foreach (var line in Lines(path))
            if (line.StartsWith("T ")) { var p = line.Split(' '); seen[States.IndexOf(p[1][0]), States.IndexOf(p[1][1])] = true; }
        for (int i = 0; i < 4; i++) for (int j = 0; j < 4; j++) if (!seen[i, j]) _trans[i, j] = MinFloat;
    }

    private static (string w, string p)? SplitWordLine(string line)
    {
        if (line.Length == 0 || line[0] == '#') return null;
        int a = line.IndexOf(' ');
        if (a <= 0) return null;
        return (line.Substring(0, a), line.Substring(a + 1));
    }

    private void LoadLexA(string path)
    {
        foreach (var line in Lines(path))
            if (SplitWordLine(line) is var (w, p)) _lexA[w] = p;
    }

    private void LoadPhrases(string path)
    {
        foreach (var line in Lines(path))
            if (SplitWordLine(line) is var (w, p))
            {
                _phrases[w] = p;
                for (int k = 1; k <= w.Length; k++) _phrasePrefix.Add(w.Substring(0, k));
            }
    }

    private void LoadChars(string path)
    {
        foreach (var line in Lines(path))
        {
            if (line.Length == 0 || line[0] == '#') continue;
            int cp = char.ConvertToUtf32(line, 0);
            int sp = line.IndexOf(' ');
            if (sp > 0) _chars[cp] = string.Intern(line.Substring(sp + 1).Trim());
        }
    }

    /// <summary>用户自定义词库：一行“词 拼音”；拼音可写数字调（hang2）或调号（háng），轻声写 5 或不写调；# 后面是注释。</summary>
    private void LoadUserDict(string path)
    {
        int n = 0;
        foreach (var raw in Lines(path))
        {
            n++;
            string line = raw.TrimStart('\uFEFF');
            int hash = line.IndexOf('#');
            if (hash >= 0) line = line.Substring(0, hash);
            line = line.Trim();
            if (line.Length == 0) continue;
            int sp = line.IndexOfAny(new[] { ' ', '\t', '\u3000' });
            if (sp <= 0) { UserDictWarnings.Add($"第{n}行没有拼音，已跳过：{raw}"); continue; }
            string w = line.Substring(0, sp);
            var syl = line.Substring(sp + 1).Split(new[] { ' ', '\t', '\u3000' }, StringSplitOptions.RemoveEmptyEntries);
            var outp = new List<string>();
            bool ok = true;
            foreach (var s in syl)
            {
                string? t = ToTone3(s);
                if (t == null) { ok = false; break; }
                outp.Add(t);
            }
            if (!ok) { UserDictWarnings.Add($"第{n}行拼音无法识别，已跳过：{raw}"); continue; }
            if (outp.Count != w.Length || w.Any(c => !IsSegChar(c)))
            { UserDictWarnings.Add($"第{n}行字数与拼音数不一致（或含非汉字），已跳过：{raw}"); continue; }
            _user[w] = string.Join(' ', outp);
        }
        // 和 jieba.add_word(w, 10**7) 一样：词频 1000 万（一定能整切），总词频累加，补前缀
        foreach (var w in _user.Keys)
        {
            _freq[w] = 10_000_000; _total += 10_000_000;
            for (int k = 1; k < w.Length; k++) { string pre = w.Substring(0, k); if (!_freq.ContainsKey(pre)) _freq[pre] = 0; }
        }
        _logTotal = 0;
    }

    private static readonly string[] ToneVowels =
    {
        "āáǎà a", "ēéěè e", "īíǐì i", "ōóǒò o", "ūúǔù u", "ǖǘǚǜ v", "ńňǹ n", "ḿ m",
    };

    /// <summary>“háng”/“hang2”/“lü4”/“lv4”/“de”/“de5” → 数字调 tone3（轻声 = 5）。识别不了返回 null。</summary>
    public static string? ToTone3(string s)
    {
        s = s.Trim().ToLowerInvariant().Replace("u:", "v");
        if (s.Length == 0) return null;
        // 调号可能是“组合附加符号”（如 a + U+0301），先换成数字调；ü 的两点 U+0308 → v
        if (s.Any(ch => ch >= '\u0300' && ch <= '\u036F'))
        {
            char t0 = '\0';
            var b = new StringBuilder();
            foreach (char ch in s)
            {
                switch (ch)
                {
                    case '\u0304': t0 = '1'; break;
                    case '\u0301': t0 = '2'; break;
                    case '\u030C': t0 = '3'; break;
                    case '\u0300': t0 = '4'; break;
                    case '\u0308': if (b.Length > 0 && b[^1] == 'u') b[^1] = 'v'; break;
                    default: if (ch >= '\u0300' && ch <= '\u036F') return null; b.Append(ch); break;
                }
            }
            s = b.ToString() + (t0 == '\0' ? "" : t0.ToString());
        }
        var sb = new StringBuilder();
        char tone = '\0';
        bool digit = false;
        foreach (char c0 in s)
        {
            char c = c0;
            if (c == 'ü') { sb.Append('v'); continue; }
            bool mapped = false;
            foreach (var row in ToneVowels)
            {
                int sp = row.IndexOf(' ');
                int idx = row.IndexOf(c);
                if (idx >= 0 && idx < sp)
                {
                    string baseCh = row.Substring(sp + 1);
                    // ńňǹ 的位置是 2/3/4 声；ḿ 是 2 声
                    int t = baseCh == "n" ? idx + 2 : baseCh == "m" ? 2 : idx + 1;
                    if (tone != '\0') return null;
                    tone = (char)('0' + t);
                    sb.Append(baseCh);
                    mapped = true;
                    break;
                }
            }
            if (mapped) continue;
            if (c >= '1' && c <= '5') { if (tone != '\0') return null; tone = c; digit = true; continue; }
            if (c >= 'a' && c <= 'z') { if (digit) return null; sb.Append(c); continue; }   // 数字调只能写在最后
            return null;
        }
        if (sb.Length == 0) return null;
        return sb.ToString() + (tone == '\0' ? '5' : tone);
    }

    // ====================================================================================
    //  jieba 分词（jieba 0.42.1 默认精确模式 + HMM 的移植）
    // ====================================================================================

    private static bool IsSegChar(char c) => c >= '\u3400' && c <= '\u9fff';
    private static bool IsJiebaHan(char c) => c >= '\u4E00' && c <= '\u9FD5';

    /// <summary>对一段纯汉字（U+3400–U+9FFF）分词。</summary>
    public List<string> Cut(string seg)
    {
        var res = new List<string>();
        // jieba.cut：re_han_default 只认 \u4E00-\u9FD5，其余字符逐个输出
        int i = 0;
        while (i < seg.Length)
        {
            if (IsJiebaHan(seg[i]))
            {
                int j = i;
                while (j < seg.Length && IsJiebaHan(seg[j])) j++;
                CutDag(seg.Substring(i, j - i), res);
                i = j;
            }
            else { res.Add(seg[i].ToString()); i++; }
        }
        return res;
    }

    private void CutDag(string s, List<string> res)
    {
        int n = s.Length;
        if (_logTotal == 0) _logTotal = Math.Log(_total);
        // DAG
        var dag = new List<int>[n];
        for (int k = 0; k < n; k++)
        {
            var tmp = new List<int>(4);
            int i = k;
            string frag = s.Substring(k, 1);
            while (i < n && _freq.TryGetValue(frag, out int fq))
            {
                if (fq != 0) tmp.Add(i);
                i++;
                if (i < n) frag = s.Substring(k, i - k + 1);
            }
            if (tmp.Count == 0) tmp.Add(k);
            dag[k] = tmp;
        }
        // route：从后往前取最大概率；概率相同取更大的 x（和 Python 元组比较一致）
        var rp = new double[n + 1];
        var rx = new int[n + 1];
        for (int idx = n - 1; idx >= 0; idx--)
        {
            double best = double.NegativeInfinity; int bx = -1;
            foreach (int x in dag[idx])
            {
                _freq.TryGetValue(s.Substring(idx, x - idx + 1), out int fq);
                double v = Math.Log(fq == 0 ? 1 : fq) - _logTotal + rp[x + 1];
                if (v > best || (v == best && x > bx)) { best = v; bx = x; }
            }
            rp[idx] = best; rx[idx] = bx;
        }
        // 输出（单字缓冲 → HMM）
        var buf = new StringBuilder();
        void Flush()
        {
            if (buf.Length == 0) return;
            string b = buf.ToString(); buf.Clear();
            if (b.Length == 1) { res.Add(b); return; }
            if (!_freq.TryGetValue(b, out int fq) || fq == 0) HmmCut(b, res);
            else foreach (char c in b) res.Add(c.ToString());
        }
        int p = 0;
        while (p < n)
        {
            int y = rx[p] + 1;
            if (y - p == 1) buf.Append(s[p]);
            else { Flush(); res.Add(s.Substring(p, y - p)); }
            p = y;
        }
        Flush();
    }

    private static readonly int[][] PrevStatus = { new[] { 3, 2 }, new[] { 1, 0 }, new[] { 0, 1 }, new[] { 2, 3 } }; // B:ES M:MB E:BM S:SE

    private void HmmCut(string s, List<string> res)
    {
        // 这里 s 全部是 \u4E00-\u9FD5（CutDag 的输入），所以 finalseg.cut 的 re_han 切块只剩一块
        int n = s.Length;
        var V = new double[n, 4];
        var path = new int[n, 4];
        for (int y = 0; y < 4; y++)
            V[0, y] = _start[y] + (_emit[y].TryGetValue(s[0], out var e) ? e : MinFloat);
        for (int t = 1; t < n; t++)
            for (int y = 0; y < 4; y++)
            {
                double em = _emit[y].TryGetValue(s[t], out var e) ? e : MinFloat;
                double best = double.NegativeInfinity; int bs = -1;
                foreach (int y0 in PrevStatus[y])
                {
                    double v = V[t - 1, y0] + _trans[y0, y] + em;
                    // Python: max((prob, state))，概率相同比较状态字母
                    if (bs < 0 || v > best || (v == best && States[y0] > States[bs])) { best = v; bs = y0; }
                }
                V[t, y] = best; path[t, y] = bs;
            }
        // 结尾只能是 E 或 S
        int last = (V[n - 1, 2] > V[n - 1, 3] || (V[n - 1, 2] == V[n - 1, 3] && 'E' > 'S')) ? 2 : 3;
        var pos = new int[n];
        pos[n - 1] = last;
        for (int t = n - 1; t > 0; t--) pos[t - 1] = path[t, pos[t]];
        int begin = 0, next = 0;
        for (int i = 0; i < n; i++)
        {
            switch (pos[i])
            {
                case 0: begin = i; break;
                case 2: res.Add(s.Substring(begin, i - begin + 1)); next = i + 1; break;
                case 3: res.Add(s[i].ToString()); next = i + 1; break;
            }
        }
        if (next < n) res.Add(s.Substring(next));
    }

    // ====================================================================================
    //  pypinyin lazy_pinyin（TONE3、轻声写 5）的移植：正向最大匹配（严格词组模式）+ 词组 / 单字读音
    // ====================================================================================

    private void LazyPinyin(string w, List<string> outp)
    {
        string remain = w;
        while (remain.Length > 0)
        {
            string lastValid = ""; int lastIdx = 0;
            bool broke = false;
            for (int index = 0; index < remain.Length; index++)
            {
                string word = remain.Substring(0, index + 1);
                if (_phrasePrefix.Contains(word))
                {
                    if (_phrases.ContainsKey(word)) { lastValid = word; lastIdx = index + 1; }
                }
                else
                {
                    if (lastValid.Length > 0) { PhraseOrChars(lastValid, outp); remain = remain.Substring(lastIdx); }
                    else { PhraseOrChars(remain.Substring(0, 1), outp); remain = remain.Substring(1); }
                    broke = true;
                    break;
                }
            }
            if (broke) continue;
            if (lastValid.Length > 0) { PhraseOrChars(lastValid, outp); remain = remain.Substring(lastIdx); }
            else
            {
                if (!_phrases.ContainsKey(remain)) foreach (char c in remain) PhraseOrChars(c.ToString(), outp);
                else PhraseOrChars(remain, outp);
                break;
            }
        }
    }

    private void PhraseOrChars(string piece, List<string> outp)
    {
        if (_phrases.TryGetValue(piece, out var p)) { outp.AddRange(p.Split(' ')); return; }
        foreach (char c in piece) outp.Add(_chars.TryGetValue(c, out var q) ? q : c + "5");   // 没有读音的字：pypinyin 原样返回再补轻声 5（与实验版一致；之后不会命中拼音表，按原字朗读）
    }

    // ====================================================================================
    //  主流程
    // ====================================================================================

    /// <summary>一个汉字的定音结果：在原文里的位置（UTF-16 下标）、字（可能是代理对）、数字调拼音。</summary>
    public readonly record struct Reading(int Index, string Char, string Pinyin);

    /// <summary>
    /// 与实验版 newfront.g2p 完全相同的结果（只含 U+3400–U+9FFF 的汉字，未做变调）。
    /// </summary>
    public List<Reading> G2P(string text)
    {
        var res = new List<Reading>(text.Length);
        int i = 0;
        while (i < text.Length)
        {
            if (!IsSegChar(text[i])) { i++; continue; }
            int j = i;
            while (j < text.Length && IsSegChar(text[j])) j++;
            string seg = text.Substring(i, j - i);
            G2PSegment(seg, i, res);
            i = j;
        }
        return res;
    }

    private void G2PSegment(string seg, int offset, List<Reading> res)
    {
        var pys = new List<string>(seg.Length);
        var locked = new List<bool>(seg.Length);
        foreach (var w in Cut(seg))
        {
            bool lk;
            if (_user.TryGetValue(w, out var up)) { pys.AddRange(up.Split(' ')); lk = true; }
            else if (w.Length > 1 && _lexA.TryGetValue(w, out var ap)) { pys.AddRange(ap.Split(' ')); lk = true; }
            else
            {
                int before = pys.Count;
                LazyPinyin(w, pys);
                // 防御：拼音数和字数对不上时按单字补齐
                while (pys.Count < before + w.Length) pys.Add(w[pys.Count - before].ToString());
                if (pys.Count > before + w.Length) pys.RemoveRange(before + w.Length, pys.Count - before - w.Length);
                lk = w.Length > 1 && _phrases.ContainsKey(w);
            }
            for (int k = 0; k < w.Length; k++) locked.Add(lk);
        }
        for (int k = 0; k < seg.Length; k++)
        {
            string p = pys[k];
            if (!locked[k]) p = Rules(seg, k, p);
            res.Add(new Reading(offset + k, seg[k].ToString(), p));
        }
    }

    // ---------- 原上下文规则（hfront.rules 的逐行移植，未改动） ----------
    private const string Verbs = "去来走做说看写买吃干交还付买拿找打学讲想办跑回睡用补花加问";

    private static bool Hz(char c) => c >= '\u4e00' && c <= '\u9fff';

    private static string Rules(string chars, int i, string py)
    {
        char c = chars[i];
        char prev = i > 0 ? chars[i - 1] : '\0';
        string nxtS = i + 1 < chars.Length ? chars[i + 1].ToString() : "#";
        char nxt = nxtS[0];
        string nxt2 = chars.Substring(Math.Min(i + 1, chars.Length), Math.Max(0, Math.Min(2, chars.Length - i - 1)));
        bool In(char ch, string set) => ch != '\0' && set.IndexOf(ch) >= 0;
        // 原 Python 规则里句首 prev = ''，而 '' in '...' 恒为 True；为了与实验结果逐字一致，这里保留同样的行为
        bool InPrev(string set) => prev == '\0' || set.IndexOf(prev) >= 0;
        bool end = nxtS == "#" || !Hz(nxt) || In(nxt, "了吧呢吗啊呀");
        switch (c)
        {
            case '调':
                if (In(nxt, "走去往任入出换动派职岗档研包头子门号运兵度用查")) return "diao4";
                return "tiao2";
            case '还':
                if (In(nxt, "钱书债款给清原手价嘴击贷礼") || end) return "huan2";
                return "hai2";
            case '得':
                if (InPrev("你我他她它们就还也都必总") && (In(nxt, Verbs) || nxt2 == "赶紧" || nxt2 == "好好" || nxt2 == "马上")) return "dei3";
                if (nxt == '到' || nxt == '分' || nxt == '奖') return "de2";
                if (prev != '\0' && Hz(prev)) return "de5";
                break;
            case '着':
                if (In(nxt, "急凉火迷魔陆落想手实数")) return "zhao2";
                if (InPrev("睡找猜打点烧够") && (end || nxt == '了')) return "zhao2";
                return "zhe5";
            case '背':
                if (In(nxt, "着起上包负带篓走")) return "bei1";
                return "bei4";
            case '数':
                if (In(nxt, "一钱着到不过清") || nxt == '数') return "shu3";
                break;
            case '弹':
                if (In(nxt, "琴奏唱跳性簧指劾射起开出力幕钢吉乐") || InPrev("会在要能来去再")) return "tan2";
                break;
            case '朝':
                if (nxt == '阳' && (InPrev("的早晨") || chars.Substring(Math.Max(0, i - 4), i - Math.Max(0, i - 4)).Contains('早'))) return "zhao1";
                break;
            case '地':
                if (i > 1 && prev != '\0' && prev == chars[i - 2]) return "de5";
                break;
            case '为':
                if (In(nxt, "了他她你我们人大")) return "wei4";
                break;
        }
        return py;
    }

    // ---------- 变调（inject.sandhi 的移植） ----------
    public static void Sandhi(List<Reading> rd)
    {
        for (int i = 0; i < rd.Count; i++)
        {
            var (idx, c, p) = rd[i];
            string nt = i + 1 < rd.Count ? rd[i + 1].Pinyin[^1].ToString() : "";
            if (c == "不" && p == "bu4" && nt == "4") rd[i] = new Reading(idx, c, "bu2");
            if (c == "一" && p == "yi1" && i + 1 < rd.Count && nt == "4") rd[i] = new Reading(idx, c, "yi2");
            else if (c == "一" && p == "yi1" && i + 1 < rd.Count && nt.Length == 1 && "123".Contains(nt) && i > 0 && rd[i - 1].Char != "第")
                rd[i] = new Reading(idx, c, "yi4");
        }
        for (int i = 0; i < rd.Count - 1; i++)
            if (rd[i].Pinyin.EndsWith('3') && rd[i + 1].Pinyin.EndsWith('3'))
                rd[i] = rd[i] with { Pinyin = rd[i].Pinyin[..^1] + "2" };
    }

    /// <summary>
    /// 把一个小句转成送给引擎的文字：每个汉字换成音色包 PUA 表里对应拼音的私用区字符。
    /// 表里没有的拼音保持原字（交给模型自己的词典）。扩展 B 区以后的生僻字按单字读音处理。
    /// </summary>
    public string ToEngineText(string text, PuaMap map)
    {
        var rd = G2P(text);
        Sandhi(rd);
        var sb = new StringBuilder(text.Length);
        int k = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (k < rd.Count && rd[k].Index == i)
            {
                sb.Append(map.Get(rd[k].Pinyin) ?? c.ToString());
                k++;
                continue;
            }
            // 生僻字（代理对）：pinyin-data 补充表
            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                int cp = char.ConvertToUtf32(c, text[i + 1]);
                if (cp >= 0x20000 && cp <= 0x3134F && _chars.TryGetValue(cp, out var py) && map.Get(py) is string pua)
                    sb.Append(pua);
                else sb.Append(c).Append(text[i + 1]);
                i++;
                continue;
            }
            sb.Append(c);
        }
        return sb.ToString();
    }

    // ====================================================================================
    //  程序里共用的一份（后台加载）
    // ====================================================================================

    private static Task<PolyphoneFrontend?>? _shared;
    private static readonly object _sharedGate = new();

    /// <summary>程序启动时调用一次：后台加载（约 1 秒），不阻塞界面。appDir = 程序文件夹。</summary>
    public static void StartLoading(string appDir, string? altUserDictDir = null)
    {
        lock (_sharedGate)
        {
            if (_shared != null) return;
            _shared = Task.Run(() =>
            {
                try
                {
                    string data = Path.Combine(appDir, DataDirName);
                    if (!File.Exists(Path.Combine(data, "dict.txt")))
                    { Log.Write("多音字前端：没有 frontend 文件夹，按旧方式朗读"); return null; }
                    string? user = Path.Combine(appDir, UserDictFileName);
                    if (!File.Exists(user) && altUserDictDir != null) user = Path.Combine(altUserDictDir, UserDictFileName);
                    var f = Load(data, user);
                    // 预热（让第一次朗读不用等 JIT 编译），再回收加载时的临时对象，降低常驻内存
                    var probe = new PuaMap();
                    f.ToEngineText("银行行长调到总行，一样不对。", probe);
                    GC.Collect(2, GCCollectionMode.Forced, true, true);
                    Log.Write($"多音字前端：加载完成 {f.LoadSeconds:0.00}s，用户词 {f.UserWordCount} 个（{(f.UserDictPath ?? "无用户词库")}）");
                    foreach (var w in f.UserDictWarnings) Log.Write("用户自定义词库：" + w);
                    return f;
                }
                catch (Exception ex) { Log.Write("多音字前端加载失败，按旧方式朗读：" + ex.Message); return null; }
            });
        }
    }

    /// <summary>取共用前端；还没加载完就等（最多 timeoutMs）。没有 / 失败 / 超时返回 null（按旧方式朗读）。</summary>
    public static PolyphoneFrontend? Shared(int timeoutMs = 5000)
    {
        var t = _shared;
        if (t == null) return null;
        try { return t.Wait(timeoutMs) ? t.Result : null; } catch { return null; }
    }
}

/// <summary>
/// 音色包里的 pua_map.txt：一行“拼音 字符”（例如 “hang2 \uE123”），
/// 表示音色包词典 (lexicon.txt) 里这个私用区字符读作这个拼音。由 packs.py 按模型词典生成。
/// </summary>
public sealed class PuaMap
{
    private readonly Dictionary<string, string> _m = new(StringComparer.Ordinal);
    public int Count => _m.Count;
    public string? Get(string pinyin) => _m.TryGetValue(pinyin, out var s) ? s : null;

    public static PuaMap? TryLoad(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var m = new PuaMap();
            foreach (var line in File.ReadLines(path, Encoding.UTF8))
            {
                if (line.Length == 0 || line[0] == '#') continue;
                int sp = line.IndexOf(' ');
                if (sp <= 0 || sp + 1 >= line.Length) continue;
                string ch = line.Substring(sp + 1).Trim();
                if (ch.Length == 1 && ch[0] >= '\uE000' && ch[0] <= '\uF8FF') m._m[line.Substring(0, sp)] = ch;
            }
            return m.Count > 0 ? m : null;
        }
        catch { return null; }
    }
}

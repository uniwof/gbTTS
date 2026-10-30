using System.Text;
using System.Text.RegularExpressions;

namespace CaretReader.Core;

/// <summary>
/// 一个“小句”：要朗读的文字 + 读完之后要停顿多少秒。
/// </summary>
public sealed record Clause(string Text, double PauseAfterSeconds, CaretReader.Core.Text.PauseKind Kind = CaretReader.Core.Text.PauseKind.None);

/// <summary>P1 新停顿模型的切句选项。</summary>
public sealed class SplitOptions
{
    /// <summary>标题识别（文档 5.6.1）：一行 ≤ TitleMaxChars 字、不以句末标点结尾、下一行比它长 → 标题停顿。</summary>
    public bool TitleDetection { get; set; } = true;
    public int TitleMaxChars { get; set; } = 25;
    /// <summary>MarkdownCleaner 给出的每行类别（标题 / 列表项），可为 null。下标是第几行（从 0 开始）。</summary>
    public IReadOnlyList<CaretReader.Core.Text.PauseKind>? LineKinds { get; set; }
}

/// <summary>
/// 把一大段文字按标点切成小句，并给每个小句配上“停顿时长”。
///
/// 为什么要这样做？
///   用户要的是“在标点处停顿”，而不是“整体放慢语速”。
///   所以我们每个小句都用正常语速 (1.0) 合成，再在小句之间插入一段静音。
///   逗号停得短，句号停得长，听起来就像真人在换气。
///
/// 规则（数值都来自设置，可以调）：
///   ，、 ,  ——      → 逗号停顿（默认 0.15 秒）
///   。！？ . ! ? …… → 句号停顿（默认 0.5 秒）
///   ； ;            → 分号停顿（默认 0.3 秒）
///   ： :            → 冒号停顿（默认 0.25 秒）
///   换行             → 换行停顿（默认 0.5 秒，和句号一样）
///
/// 不会被切开的情况：
///   - 数字里的 3.14、1,000、10:30、１，０００、10：30、３．１４
///   - 网址（http://… https://… www.…）整个保留，里面的 ? . : 都不算标点
///   - 英文缩写 Mr. Mrs. Dr. e.g. i.e. etc. vs. 等后面不停顿
///   - 标点后面紧跟的右引号/右括号（” 」 ） 以及成对的 " 的后一个）跟着前一句走
///   - 连在一起的多个标点（例如 "！？"）只停一次，取最长的那个停顿
///
/// 长度控制（为了“按下快捷键后 1 秒内出声”和“停止后能马上重新开始”）：
///   - 第一句如果超过 FirstClauseMaxChars（20 字），在 8~20 字之间找一个自然断点切开，
///     这样第一句很快就能合成好开始出声。
///   - 其它小句超过 MaxClauseChars（36 字）也会在自然断点切开。
///   - 自然断点优先级：空格 > “的/了/着…”等虚词之后 > 中英文交界 > 任意汉字之间。
///   - 尽量不切在：一个英文单词或数字中间、emoji 等“代理对”字符中间、网址中间。
///   - v0.2：如果实在找不到安全切点（例如一个超长的英文连字符串），就在长度上限处强行切开
///     （只避开 emoji 代理对）：
///       第一句：前 20 字里没有安全切点时，最多放宽到 36 字去找（例如让一个短网址保持完整），
///               还找不到就在 20 字处硬切；
///       后面的小句：没有安全切点就在 36 字处硬切。
///     流水线另有一道保险：送进引擎的文字绝不超过 HardMaxClauseChars（80 字）。
///
/// “读不出来”的字符串（v0.2）：
///   - 超过 36 个字符的长网址 → 读作“链接”（短网址照常保留，且一定能放进一个小句）。
///   - 16 个字符以上、字母数字混杂的十六进制串 / 哈希 / ID → 读作“一串字符”；
///     16 位以上的纯数字 → 读作“一串数字”；32 个字母以上、中间没有连字符的“单词” → “一串字符”。
///   这样既不会让模型逐个字母念几千个字符，也不会产生一个几千字的超长小句（内存暴涨、停不下来）。
/// </summary>
public static class TextSplitter
{
    /// <summary>一般小句的最大长度（字符）。</summary>
    public const int MaxClauseChars = 36;
    /// <summary>第一句的最大长度（字符），越短出声越快。</summary>
    public const int FirstClauseMaxChars = 20;
    /// <summary>软切分时，切出来的前半段至少要有多少字（避免切出一两个字的碎句）。</summary>
    public const int MinPieceChars = 8;
    /// <summary>送进引擎的文字的绝对上限（字符），由 SpeechPipeline 最后把关。</summary>
    public const int HardMaxClauseChars = 80;
    /// <summary>超过这个长度的网址读作“链接”（= MaxClauseChars，保证保留下来的网址能放进一个小句）。</summary>
    public const int LongUrlChars = MaxClauseChars;
    /// <summary>字母数字串达到这个长度才可能被当作“一串字符”。</summary>
    public const int IdRunChars = 16;

    // 小句“结束”后可以继续粘在后面的字符：右引号、右括号等（ASCII " 另外按成对规则处理）。
    private const string Closers = "”’」』）)】]》〉";

    // 在这些字后面切开，听起来比较自然
    private const string SoftBreakAfter = "的了着过和与及或在是就也都而并但把被从向对给让";

    // 英文缩写（小写，不含最后的点）：后面的 "." 不当作句号
    private static readonly HashSet<string> Abbreviations = new(StringComparer.OrdinalIgnoreCase)
    {
        "mr", "mrs", "ms", "dr", "st", "prof", "sr", "jr", "vs", "etc", "e.g", "i.e", "no", "fig", "approx", "inc", "ltd", "co",
    };

    // 网址：http(s):// 或 www. 开头，到空白或中文/全角标点为止
    private static readonly Regex UrlRegex = new(
        @"(?:https?://|www\.)[^\s\u3000-\u303F\uFF00-\uFFEF\u4E00-\u9FFF""“”‘’<>]+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// 切分文字。返回的小句一定含有至少一个“能读出来的字符”（字母、数字或汉字）。
    /// </summary>
    public static List<Clause> Split(string? text, PauseSettings pauses) => Split(text, pauses, firstChunk: true);

    /// <summary>
    /// P1：用标点表（rules/punctuation.tsv + 用户标点表）切分。支持前停顿（《 “ （ —— 前面停一下）、
    /// 段落 / 换行 / 标题 / 列表项停顿、长句中间切开处的衔接间隙（&lt;切分&gt;）。
    /// 合并规则：连在一起的停顿取最大值，不相加；前停顿与前一个标点的后停顿相遇也取最大值。
    /// 返回的停顿是基础值（还没乘全局倍率）。
    /// </summary>
    public static List<Clause> Split(string? text, CaretReader.Core.Text.PunctuationTable table, SplitOptions? options = null,
        bool firstChunk = true, int maxChars = 0)
    {
        options ??= new SplitOptions();
        text = Truncate(text, maxChars);
        var raw = SplitAtTable(NormalizeUnreadable(text), table, options);
        return ControlLength(raw, firstChunk, table.Special(CaretReader.Core.Text.PunctuationTable.InnerCut).After);
    }

    private static string? Truncate(string? text, int maxChars)
    {
        if (text != null && maxChars > 0 && text.Length > maxChars)
        {
            int cut = maxChars;
            if (char.IsHighSurrogate(text[cut - 1])) cut--;
            text = text.Substring(0, cut);
        }
        return text;
    }

    /// <summary>
    /// 切分文字。firstChunk=false 表示这是“后续取到的文字”（接在已经在读的内容后面），
    /// 这时第一句不需要特别缩短到 20 字。maxChars &gt; 0 时先截断到这么多字符（防止超大文本）。
    /// </summary>
    public static List<Clause> Split(string? text, PauseSettings pauses, bool firstChunk, int maxChars = 0)
    {
        text = Truncate(text, maxChars);
        var raw = SplitAtPunctuation(NormalizeUnreadable(text), pauses);
        return ControlLength(raw, firstChunk, 0.0);
    }

    /// <summary>第二步：控制长度。innerCut = 长句中间切开处插入的衔接间隙（旧模型为 0）。</summary>
    private static List<Clause> ControlLength(List<Clause> raw, bool firstChunk, double innerCut)
    {
        var result = new List<Clause>(raw.Count + 4);
        for (int i = 0; i < raw.Count; i++)
        {
            var c = raw[i];
            int before = result.Count;
            if (firstChunk && result.Count == 0 && c.Text.Length > FirstClauseMaxChars)
            {
                // 第一句太长：先切出一个 ≤20 字的开头（找不到安全切点最多放宽到 36 字，再不行就硬切），
                // 其余部分按普通规则处理
                var (head, tail) = CutOnce(c.Text, FirstClauseMaxChars, MaxClauseChars);
                if (tail.Length > 0)
                {
                    if (HasSpeakable(head)) result.Add(new Clause(head, innerCut, innerCut > 0 ? CaretReader.Core.Text.PauseKind.InnerCut : CaretReader.Core.Text.PauseKind.None));
                    c = c with { Text = tail };
                }
            }
            foreach (var piece in SplitLong(c.Text, MaxClauseChars))
                if (HasSpeakable(piece)) result.Add(new Clause(piece, innerCut, innerCut > 0 ? CaretReader.Core.Text.PauseKind.InnerCut : CaretReader.Core.Text.PauseKind.None));
            if (result.Count == before)
            {
                // 这一句切完后没有能读的部分（极少见，例如一长串符号）：停顿并入上一句
                if (result.Count > 0 && c.PauseAfterSeconds > result[^1].PauseAfterSeconds)
                    result[^1] = result[^1] with { PauseAfterSeconds = c.PauseAfterSeconds, Kind = c.Kind };
                continue;
            }
            // 原小句的停顿只加在最后一段后面
            result[^1] = result[^1] with { PauseAfterSeconds = c.PauseAfterSeconds, Kind = c.Kind };
        }
        return result;
    }

    // =====================================================================
    // 第零步：把“读不出来”的长字符串换成简短的说法
    // =====================================================================

    // 连续的字母、数字以及 ID / Base64 里常见的 _ - + / =
    private static readonly Regex TokenRegex = new(@"[A-Za-z0-9][A-Za-z0-9_\-+/=]{15,}", RegexOptions.Compiled);

    /// <summary>
    /// 长网址 → “链接”；十六进制串 / 哈希 / ID → “一串字符”；16 位以上纯数字 → “一串数字”。
    /// 普通英文单词、短网址、带连字符的英文词组保持不变。
    /// </summary>
    public static string NormalizeUnreadable(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        // 1) 长网址
        text = UrlRegex.Replace(text, m =>
        {
            string v = m.Value;
            int end = v.Length;
            while (end > 0 && ".,;:!?)".IndexOf(v[end - 1]) >= 0) end--;   // 句末标点不算网址
            if (end <= LongUrlChars) return v;
            return "链接" + v.Substring(end);
        });
        // 2) 长字母数字串
        return TokenRegex.Replace(text, m =>
        {
            int s = m.Index, e = m.Index + m.Length;
            // 前后紧挨着字母数字（说明这是更长的串的一部分，正则已经整体匹配了），这里只看整个匹配
            if (s > 0 && char.IsAsciiLetterOrDigit(text[s - 1])) return m.Value;
            string v = m.Value.TrimEnd('-', '_', '+', '/', '=');
            string tail = m.Value.Substring(v.Length);
            string? spoken = ClassifyToken(v);
            return spoken == null ? m.Value : spoken + tail;
        });
    }

    /// <summary>返回“一串字符”/“一串数字”，或 null（= 这是正常单词，照常朗读）。</summary>
    private static string? ClassifyToken(string v)
    {
        if (v.Length < IdRunChars) return null;
        int digits = 0, letters = 0, transitions = 0, hex = 0, seps = 0, longestWord = 0, word = 0;
        int prevKind = 0; // 1=字母 2=数字
        foreach (char ch in v)
        {
            int kind = char.IsAsciiDigit(ch) ? 2 : char.IsAsciiLetter(ch) ? 1 : 0;
            if (kind == 2) digits++;
            else if (kind == 1) letters++;
            else seps++;
            if (char.IsAsciiHexDigit(ch)) hex++;
            if (kind != 0 && prevKind != 0 && kind != prevKind) transitions++;
            if (kind != 0) prevKind = kind;
            if (kind == 1) { word++; longestWord = Math.Max(longestWord, word); } else word = 0;
        }
        if (digits == v.Length) return "一串数字";                          // 1234567890123456
        if (seps == 0 && hex == v.Length && digits > 0) return "一串字符";   // a3f9c0… 哈希
        if (digits >= 3 && letters >= 3 && transitions >= 4) return "一串字符"; // ID / Base64 / 序列号
        if (longestWord >= 32) return "一串字符";                            // 没有这么长的英文单词
        return null;
    }

    // =====================================================================
    // 第一步：按标点切
    // =====================================================================
    private static List<Clause> SplitAtPunctuation(string? text, PauseSettings pauses)
    {
        var result = new List<Clause>();
        if (string.IsNullOrEmpty(text)) return result;

        // 统一换行符：Windows 的 \r\n 和老式的 \r 都变成 \n
        text = text.Replace("\r\n", "\n").Replace('\r', '\n');

        // 标出网址所在的位置：这些字符永远不当作标点
        var inUrl = new bool[text.Length];
        foreach (Match m in UrlRegex.Matches(text))
        {
            int end = m.Index + m.Length;
            // 网址末尾的英文句号/逗号等通常是句子的标点，不算网址
            while (end > m.Index && ".,;:!?)".IndexOf(text[end - 1]) >= 0) end--;
            for (int k = m.Index; k < end; k++) inUrl[k] = true;
        }

        var current = new StringBuilder();
        int quotes = 0;   // 到目前为止遇到的 ASCII 双引号 " 的个数（奇数 = 引号还没关上）
        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];

            // ---------- 1. 换行 ----------
            if (c == '\n')
            {
                // 连续多个换行（空行）只算一次
                while (i < text.Length && (text[i] == '\n' || text[i] == ' ' || text[i] == '\t' || text[i] == '\u3000'))
                    i++;
                Flush(result, current, pauses.Newline);
                continue;
            }

            // ---------- 2. 标点 ----------
            double p = inUrl[i] ? -1 : PauseFor(text, i, pauses);
            if (p >= 0)
            {
                // 把这一串连续的标点（以及右引号/右括号）都吃进当前小句，停顿取最大值
                double maxPause = p;
                current.Append(c);
                i++;
                while (i < text.Length && !inUrl[i])
                {
                    char n = text[i];
                    double np = PauseFor(text, i, pauses);
                    if (np >= 0) { maxPause = Math.Max(maxPause, np); current.Append(n); i++; }
                    else if (Closers.IndexOf(n) >= 0) { current.Append(n); i++; }
                    else if (n == '"' && quotes % 2 == 1) { quotes++; current.Append(n); i++; } // 关闭的 "
                    else break;
                }
                Flush(result, current, maxPause);
                continue;
            }

            // ---------- 3. 普通字符 ----------
            if (c == '"') quotes++;
            current.Append(c);
            i++;
        }
        // 最后剩下的文字（没有以标点结尾）
        Flush(result, current, 0.0);
        return result;
    }

    // =====================================================================
    // 第一步（P1 新模型）：按标点表切
    // =====================================================================
    private const string SentenceEnders = "。！？.!?…；;：:，,、~～—";

    private static List<Clause> SplitAtTable(string? text, CaretReader.Core.Text.PunctuationTable table, SplitOptions opt)
    {
        var result = new List<Clause>();
        if (string.IsNullOrEmpty(text)) return result;
        text = text.Replace("\r\n", "\n").Replace('\r', '\n');

        var inUrl = new bool[text.Length];
        foreach (Match m in UrlRegex.Matches(text))
        {
            int end = m.Index + m.Length;
            while (end > m.Index && ".,;:!?)".IndexOf(text[end - 1]) >= 0) end--;
            for (int k = m.Index; k < end; k++) inUrl[k] = true;
        }
        var ann = CaretReader.Core.Text.PinyinAnnotation.Mask(text);
        for (int k = 0; k < text.Length; k++) if (ann[k]) inUrl[k] = true;   // 注释里的字符不当标点
        var lineEnd = LineEndPauses(text, table, opt);

        var current = new StringBuilder();
        int quotes = 0, line = 0, i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            if (c == '\n')
            {
                int nl = 0;
                while (i < text.Length && (text[i] == '\n' || text[i] == ' ' || text[i] == '\t' || text[i] == '\u3000'))
                { if (text[i] == '\n') nl++; i++; }
                var (p, k) = lineEnd[line];
                if (nl >= 2)
                {
                    var para = table.Special(CaretReader.Core.Text.PunctuationTable.Paragraph);
                    if (para.After >= p) { p = para.After; k = para.Kind; }
                }
                line += nl;
                FlushK(result, current, p, k);
                continue;
            }

            var e = inUrl[i] ? null : PunctAt(text, i, table);
            if (e != null)
            {
                if (e.Before > 0)
                    FlushK(result, current, e.Before, e.Kind);      // 前停顿：在符号前面切开（与上一句的停顿取最大值）
                if (e.After <= 0)
                {
                    // 只有前停顿（《 “ （）：符号跟着下一句走
                    current.Append(text, i, e.Symbol.Length);
                    i += e.Symbol.Length;
                    continue;
                }
                double maxPause = e.After;
                var kind = e.Kind;
                current.Append(text, i, e.Symbol.Length);
                i += e.Symbol.Length;
                while (i < text.Length && !inUrl[i])
                {
                    var n = PunctAt(text, i, table);
                    if (n != null && n.Before > 0 && n.After <= 0)
                    {
                        // 例如“。《”：《 留给下一句，停顿取 max(0.45, 0.08)
                        if (n.Before > maxPause) { maxPause = n.Before; kind = n.Kind; }
                        break;
                    }
                    if (n != null)
                    {
                        if (Math.Max(n.Before, n.After) > maxPause) { maxPause = Math.Max(n.Before, n.After); kind = n.Kind; }
                        current.Append(text, i, n.Symbol.Length); i += n.Symbol.Length;
                    }
                    else if (Closers.IndexOf(text[i]) >= 0) { current.Append(text[i]); i++; }
                    else if (text[i] == '"' && quotes % 2 == 1) { quotes++; current.Append('"'); i++; }
                    else break;
                }
                FlushK(result, current, maxPause, kind);
                continue;
            }
            if (c == '"') quotes++;
            current.Append(c);
            i++;
        }
        var (lp, lk) = lineEnd[line];
        FlushK(result, current, lp, lk);
        return result;
    }

    /// <summary>text[i] 处的标点（已排除数字中间的 . , : 、网址、英文缩写等）；不需要停顿就返回 null。</summary>
    private static CaretReader.Core.Text.PunctEntry? PunctAt(string text, int i, CaretReader.Core.Text.PunctuationTable table)
    {
        var e = table.MatchAt(text, i);
        if (e == null || (e.Before <= 0 && e.After <= 0)) return null;
        if (e.Symbol.Length > 1) return e;
        char c = text[i];
        char prev = i > 0 ? text[i - 1] : '\0';
        char next = i + 1 < text.Length ? text[i + 1] : '\0';
        bool betweenDigits = char.IsDigit(prev) && char.IsDigit(next);
        switch (c)
        {
            case '，': case ',': case '：': case '．': case '~': case '～':
                if (betweenDigits) return null;
                break;
            case '.':
                if (betweenDigits || char.IsLetterOrDigit(next) || IsAbbreviation(text, i)) return null;
                break;
            case ':':
                if (betweenDigits || next == '/') return null;
                break;
        }
        return e;
    }

    /// <summary>
    /// 每一行行尾的停顿：普通换行 / 标题（MarkdownCleaner 标出的，或自动识别的）/ 列表项。
    /// 下标 = 行号；最后一行（没有换行结尾）只在是标题时给停顿。
    /// </summary>
    private static (double pause, CaretReader.Core.Text.PauseKind kind)[] LineEndPauses(string text,
        CaretReader.Core.Text.PunctuationTable table, SplitOptions opt)
    {
        var lines = text.Split('\n');
        var res = new (double, CaretReader.Core.Text.PauseKind)[lines.Length + 1];
        var nlE = table.Special(CaretReader.Core.Text.PunctuationTable.Newline);
        var titleE = table.Special(CaretReader.Core.Text.PunctuationTable.Title);
        var listE = table.Special(CaretReader.Core.Text.PunctuationTable.ListItem);
        for (int k = 0; k < lines.Length; k++)
        {
            bool last = k == lines.Length - 1;
            var mk = opt.LineKinds != null && k < opt.LineKinds.Count ? opt.LineKinds[k] : CaretReader.Core.Text.PauseKind.None;
            bool title = mk == CaretReader.Core.Text.PauseKind.Title || (!last && opt.TitleDetection && LooksLikeTitle(lines, k, opt.TitleMaxChars));
            if (title) res[k] = (titleE.After, titleE.Kind);
            else if (mk == CaretReader.Core.Text.PauseKind.ListItem) res[k] = (listE.After, listE.Kind);
            else res[k] = last ? (0.0, CaretReader.Core.Text.PauseKind.None) : (nlE.After, nlE.Kind);
        }
        res[lines.Length] = (0.0, CaretReader.Core.Text.PauseKind.None);
        return res;
    }

    /// <summary>一行 ≤ 25 字、不以句末标点结尾、后面紧跟换行，且下一行（跳过空行）比它长 → 标题。</summary>
    private static bool LooksLikeTitle(string[] lines, int k, int maxChars)
    {
        string t = lines[k].Trim(' ', '\t', '\u3000');
        if (t.Length == 0 || t.Length > maxChars || !HasSpeakable(t)) return false;
        if (SentenceEnders.IndexOf(t[^1]) >= 0) return false;
        for (int j = k + 1; j < lines.Length; j++)
        {
            string n = lines[j].Trim(' ', '\t', '\u3000');
            if (n.Length == 0) continue;
            return n.Length > t.Length;
        }
        return false;
    }

    private static void FlushK(List<Clause> result, StringBuilder current, double pause, CaretReader.Core.Text.PauseKind kind)
    {
        string s = current.ToString().Trim(' ', '\t', '\u3000', '\n');
        current.Clear();
        if (HasSpeakable(s)) result.Add(new Clause(s, pause, kind));
        else if (result.Count > 0 && pause > result[^1].PauseAfterSeconds)
            result[^1] = result[^1] with { PauseAfterSeconds = pause, Kind = kind };
        else if (s.Length > 0 && result.Count == 0) { /* 开头只有符号：丢弃 */ }
    }

    /// <summary>
    /// 如果 text[i] 是一个“需要停顿的标点”，返回停顿秒数；否则返回 -1。
    /// </summary>
    private static double PauseFor(string text, int i, PauseSettings pz)
    {
        char c = text[i];
        char prev = i > 0 ? text[i - 1] : '\0';
        char next = i + 1 < text.Length ? text[i + 1] : '\0';
        bool betweenDigits = char.IsDigit(prev) && char.IsDigit(next);   // 全角数字 ０-９ 也算

        switch (c)
        {
            // ----- 中文 / 全角标点 -----
            case '，':
                return betweenDigits ? -1 : pz.Comma;                 // １，０００
            case '、': case '﹐': case '､':
                return pz.Comma;
            case '—': case '―':                                        // —— 破折号：短停顿
                return pz.Comma;
            case '。': case '！': case '？': case '｡': case '…': case '‥':
                return pz.Period;
            case '．':
                return betweenDigits ? -1 : pz.Period;                // ３．１４
            case '；':
                return pz.Semicolon;
            case '：':
                return betweenDigits ? -1 : pz.Colon;                 // 10：30

            // ----- 英文标点：要小心数字、缩写 -----
            case ',':
                return betweenDigits ? -1 : pz.Comma;                 // 1,000
            case '.':
                if (betweenDigits) return -1;                          // 3.14
                if (char.IsLetterOrDigit(next)) return -1;             // example.com / e.g / v1.2
                if (IsAbbreviation(text, i)) return -1;                // Mr. / e.g. / etc.
                return pz.Period;
            case '!': case '?':
                return pz.Period;
            case ';':
                return pz.Semicolon;
            case ':':
                if (betweenDigits) return -1;                          // 10:30
                if (next == '/') return -1;                            // xxx://
                return pz.Colon;
            default:
                return -1;
        }
    }

    /// <summary>text[dot] 前面的单词是不是常见英文缩写（如 Mr、e.g）。</summary>
    private static bool IsAbbreviation(string text, int dot)
    {
        int s = dot;
        while (s > 0 && (IsAsciiLetter(text[s - 1]) || text[s - 1] == '.')) s--;
        if (s == dot) return false;
        string word = text.Substring(s, dot - s).Trim('.');
        return word.Length > 0 && Abbreviations.Contains(word);
    }

    private static void Flush(List<Clause> result, StringBuilder current, double pause)
    {
        string s = current.ToString();
        current.Clear();
        AddClause(result, s, pause);
    }

    /// <summary>
    /// 加入一个小句。如果这个小句里没有“能读出来的字”（只有空白或标点），
    /// 就不单独朗读，而是把它的停顿加到上一个小句上（取较大值）。
    /// </summary>
    private static void AddClause(List<Clause> result, string s, double pause)
    {
        s = s.Trim(' ', '\t', '\u3000', '\n');
        if (HasSpeakable(s))
        {
            result.Add(new Clause(s, pause));
        }
        else if (result.Count > 0)
        {
            var last = result[^1];
            if (pause > last.PauseAfterSeconds)
                result[^1] = last with { PauseAfterSeconds = pause };
        }
    }

    /// <summary>文字里有没有字母、数字或汉字（即真正能读出声音的内容）。</summary>
    public static bool HasSpeakable(string s)
    {
        foreach (char ch in s)
            if (char.IsLetterOrDigit(ch)) return true;   // 汉字也算 Letter
        return false;
    }

    // =====================================================================
    // 第二步：长度控制（在自然断点切开）
    // =====================================================================

    /// <summary>
    /// 把过长的文字反复切开，直到每段都不超过 maxLen。
    /// 找不到安全切点时在 maxLen 处硬切。
    /// </summary>
    private static IEnumerable<string> SplitLong(string s, int maxLen)
    {
        while (s.Length > maxLen)
        {
            var (head, tail) = CutOnce(s, maxLen, maxLen);
            if (tail.Length == 0) break;      // 不会发生（s 比 maxLen 长时 CutOnce 一定会切），只是保险
            yield return head;
            s = tail;
        }
        if (s.Length > 0) yield return s;
    }

    /// <summary>
    /// 在 s 里找一个最好的切点，切成 (前段, 后段)。前段长度尽量在 [MinPieceChars, maxLen] 之间。
    /// 找不到安全切点时：先往后找（最多到 stretch 字），再往前找，最后在 maxLen 处硬切。
    /// 只有 s 不超过 stretch 且无法安全切开时才返回 (s, "")（长度已经有上限，不会失控）。
    /// </summary>
    private static (string head, string tail) CutOnce(string s, int maxLen, int stretch)
    {
        var url = UrlMask(s);
        int best = -1, bestScore = -1;
        const int MinEdge = 4;                       // 前段、后段都至少 4 个字符，避免切出一两个字的碎句
        int hi = Math.Min(maxLen, s.Length - MinEdge);
        int lo = Math.Min(MinPieceChars, hi);

        // 1) 在 [MinPieceChars, maxLen] 范围里找分数最高的切点；同分时取靠后的（让前段尽量长）
        for (int c = Math.Max(MinEdge, lo); c <= hi; c++)
        {
            int score = CutScore(s, c, url);
            if (score >= bestScore && score >= 0) { best = c; bestScore = score; }
        }
        // 2) 范围内一个安全切点都没有（例如很长的英文单词、网址）：往后找第一个安全切点，但最多到 stretch 字
        if (best < 0)
            for (int c = Math.Max(hi + 1, MinEdge); c <= Math.Min(stretch, s.Length - MinEdge); c++)
                if (CutScore(s, c, url) >= 0) { best = c; break; }
        // 3) 再不行就往前找
        if (best < 0)
            for (int c = Math.Min(lo, s.Length - MinEdge); c >= MinEdge; c--)
                if (CutScore(s, c, url) >= 0) { best = c; break; }

        if (best > 0 && best < s.Length)
        {
            string head = s.Substring(0, best).TrimEnd(' ', '\t', '\u3000');
            string tail = s.Substring(best).TrimStart(' ', '\t', '\u3000');
            if (HasSpeakable(head) && HasSpeakable(tail)) return (head, tail);
        }
        // 4) 实在没有安全切点：不超过 stretch 的就整段保留，否则在 maxLen 处硬切（只避开代理对）
        if (s.Length <= stretch) return (s, "");
        return HardCut(s, maxLen);
    }

    /// <summary>在第 n 个字符处硬切（如果正好切在 emoji 代理对中间，就往前挪一个字符）。</summary>
    public static (string head, string tail) HardCut(string s, int n)
    {
        if (n >= s.Length) return (s, "");
        if (n > 1 && char.IsHighSurrogate(s[n - 1])) n--;
        return (s.Substring(0, n), s.Substring(n));
    }

    /// <summary>
    /// 在 s[c-1] 和 s[c] 之间切开好不好？
    ///   -1 = 不允许；0 = 可以；1 = 中英文交界；2 = 虚词之后；3 = 空格处。
    /// </summary>
    private static int CutScore(string s, int c, bool[] url)
    {
        char a = s[c - 1], b = s[c];
        if (url[c - 1] && url[c]) return -1;                         // 网址中间
        if (char.IsLowSurrogate(b) || char.IsHighSurrogate(a)) return -1; // emoji 等代理对中间
        if (IsWordChar(a) && IsWordChar(b)) return -1;               // 英文单词 / 数字中间
        // 数字分隔符两边（3.|14、1,|000、10:|30）
        if (IsNumSep(a) && c >= 2 && char.IsDigit(s[c - 2]) && char.IsDigit(b)) return -1;
        if (IsNumSep(b) && char.IsDigit(a) && c + 1 < s.Length && char.IsDigit(s[c + 1])) return -1;
        // 不要把右引号/右括号切到下一句开头
        if (Closers.IndexOf(b) >= 0) return -1;
        if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(b) is System.Globalization.UnicodeCategory.NonSpacingMark or System.Globalization.UnicodeCategory.EnclosingMark) return -1; // 组合附加符号

        if (a == ' ' || a == '\t' || a == '\u3000') return 3;
        if (SoftBreakAfter.IndexOf(a) >= 0 && IsCjk(b)) return 2;
        if (IsCjk(a) != IsCjk(b)) return 1;
        return 0;
    }

    private static bool[] UrlMask(string s)
    {
        var mask = CaretReader.Core.Text.PinyinAnnotation.Mask(s);   // P1：拼音注释中间也不能切
        foreach (Match m in UrlRegex.Matches(s))
            for (int k = m.Index; k < m.Index + m.Length; k++) mask[k] = true;
        return mask;
    }

    private static bool IsAsciiLetter(char ch) => (ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z');

    /// <summary>英文字母、数字（含全角）、以及单词里的 ' - _ 。</summary>
    private static bool IsWordChar(char ch) =>
        ch < 0x2E80 ? (char.IsLetterOrDigit(ch) || ch == '\'' || ch == '-' || ch == '_')
                    : (ch >= '０' && ch <= '９') || (ch >= 'Ａ' && ch <= 'Ｚ') || (ch >= 'ａ' && ch <= 'ｚ');

    private static bool IsNumSep(char ch) => ch is '.' or ',' or ':' or '：' or '，' or '．';

    private static bool IsCjk(char ch) =>
        (ch >= 0x4E00 && ch <= 0x9FFF) || (ch >= 0x3400 && ch <= 0x4DBF) || (ch >= 0xF900 && ch <= 0xFAFF) ||
        char.IsSurrogate(ch);

    // =====================================================================
    // 送进引擎前的文字处理
    // =====================================================================

    // 不在两个数字之间的 ；：;: （10:30、10：30 这类时间/比分保持原样）
    private static readonly Regex SemiColonOutsideDigits = new(@"(?<!\d)[；：;:]|[；：;:](?!\d)", RegexOptions.Compiled);

    /// <summary>
    /// 送进引擎前的小处理：有些模型的词典里没有 ；： 这两个符号（会被忽略并打印警告），
    /// 换成逗号后小句末尾的语调更自然。停顿长度由我们自己插入的静音决定，不受影响。
    /// 注意：两个数字之间的冒号（10:30）和网址（https://…）保持原样，交给引擎自己的数字规则处理。
    /// </summary>
    public static string TextForEngine(string text)
    {
        var sb = new StringBuilder(text.Length);
        int pos = 0;
        foreach (Match m in UrlRegex.Matches(text))
        {
            sb.Append(ReplaceSemi(text.Substring(pos, m.Index - pos)));
            sb.Append(m.Value);              // 网址原样保留
            pos = m.Index + m.Length;
        }
        sb.Append(ReplaceSemi(text.Substring(pos)));
        return sb.ToString();

        static string ReplaceSemi(string part) =>
            SemiColonOutsideDigits.Replace(part, m => m.Value is "；" or "：" ? "，" : ",");
    }
}

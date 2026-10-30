namespace CaretReader.Core.Text;

/// <summary>
/// 读法改写引擎（文档 5.3.2）。
///  - 所有启用的“替换”“读音”规则建成一棵 Trie（英文按小写折叠，大小写敏感的规则在命中后再逐字核对）。
///  - 从左到右一次扫描，取最长匹配；一样长时按优先级（用户 &gt; 网络用语包 &gt; 其他内置包），再按文件里靠后的优先。
///  - “整词”：命中的前后一个字符不能是 [A-Za-z0-9]。
///  - 替换出来的文字不会再被规则扫描（不递归、不会死循环）。
///  - 模板只支持“左定界符*右定界符”：* 非贪婪、不跨行、最多 50 字、不嵌套；
///    * 里的文字仍然会做替换/读音改写，但不再参与模板匹配。
///  - “读音”规则不改文字，而是产生 ForcedPinyin 片段；同时通过 PinyinEntries 交给多音字用户词库。
///  - 每个输出片段都带着它在原文里的位置 SrcStart/SrcLen。
/// </summary>
public sealed class RulesEngine
{
    private sealed class Node
    {
        public Dictionary<char, Node>? Next;
        public List<Rule>? Rules;
    }

    private readonly Node _root = new();
    private readonly List<Rule> _templates = new();
    private readonly Dictionary<string, string> _pinyin = new(StringComparer.Ordinal);

    public int RuleCount { get; private set; }
    public IReadOnlyList<RuleError> Errors { get; }

    /// <summary>所有启用的“读音”规则：词 → 带调拼音（空格分隔），给 PolyphoneFrontend 的用户词库用。</summary>
    public IReadOnlyDictionary<string, string> PinyinEntries => _pinyin;

    public RulesEngine(IEnumerable<RuleFile> files)
    {
        var errs = new List<RuleError>();
        // 低优先级先放，高优先级后放：同词“读音”以高优先级为准
        foreach (var f in files.OrderBy(f => f.Priority))
        {
            errs.AddRange(f.Errors);
            foreach (var r in f.Rules) Add(r);
        }
        Errors = errs;
    }

    public static RulesEngine FromText(string tsv, int priority = RuleFile.PriorityUser, string name = "test.tsv") =>
        new(new[] { RuleFile.Parse(tsv, name, priority) });

    private static char Fold(char c) => c is >= 'A' and <= 'Z' ? (char)(c + 32) : c;

    private void Add(Rule r)
    {
        if (!r.Enabled) return;
        RuleCount++;
        if (r.Type == RuleType.Template) { _templates.Add(r); return; }
        if (r.Type == RuleType.Pinyin) _pinyin[r.Source] = r.Target;
        var n = _root;
        foreach (char c0 in r.Source)
        {
            char c = Fold(c0);
            n.Next ??= new Dictionary<char, Node>();
            if (!n.Next.TryGetValue(c, out var nx)) { nx = new Node(); n.Next[c] = nx; }
            n = nx;
        }
        (n.Rules ??= new List<Rule>()).Add(r);
    }

    private static bool IsAsciiWordChar(char c) =>
        c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9';

    private static bool BoundaryOk(string s, int start, int end) =>
        (start == 0 || !IsAsciiWordChar(s[start - 1])) && (end >= s.Length || !IsAsciiWordChar(s[end]));

    /// <summary>在 pos 处找最长的“替换/读音”命中。</summary>
    private (Rule? rule, int len) MatchTrie(string s, int pos)
    {
        Rule? best = null; int bestLen = 0;
        var n = _root;
        for (int i = pos; i < s.Length; i++)
        {
            if (n.Next == null || !n.Next.TryGetValue(Fold(s[i]), out var nx)) break;
            n = nx;
            if (n.Rules == null) continue;
            int len = i - pos + 1;
            foreach (var r in n.Rules)
            {
                if (!r.IgnoreCase && string.CompareOrdinal(s, pos, r.Source, 0, len) != 0) continue;
                if (r.WholeWord && !BoundaryOk(s, pos, pos + len)) continue;
                if (best == null || len > bestLen || (len == bestLen && r.Priority >= best.Priority))
                { best = r; bestLen = len; }
            }
        }
        return (best, bestLen);
    }

    /// <summary>在 pos 处找模板命中（非贪婪，不跨行，最多 50 字）。</summary>
    private (Rule? rule, int len, int innerStart, int innerLen) MatchTemplate(string s, int pos)
    {
        Rule? best = null; int bestLen = 0, bi = 0, bl = 0;
        foreach (var t in _templates)
        {
            int star = t.Source.IndexOf('*');
            string left = t.Source[..star], right = t.Source[(star + 1)..];
            var cmp = t.IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (pos + left.Length > s.Length || string.Compare(s, pos, left, 0, left.Length, cmp) != 0) continue;
            int innerStart = pos + left.Length;
            int limit = Math.Min(s.Length, innerStart + RuleFile.TemplateMaxInner + right.Length);
            int found = -1;
            for (int j = innerStart; j + right.Length <= limit; j++)
            {
                if (s[j] == '\n' || s[j] == '\r') break;
                if (string.Compare(s, j, right, 0, right.Length, cmp) == 0) { found = j; break; }
            }
            if (found < 0) continue;
            int innerLen = found - innerStart;
            if (innerLen > RuleFile.TemplateMaxInner) continue;
            int len = found + right.Length - pos;
            if (t.WholeWord && !BoundaryOk(s, pos, pos + len)) continue;
            if (best == null || len > bestLen || (len == bestLen && t.Priority >= best.Priority))
            { best = t; bestLen = len; bi = innerStart; bl = innerLen; }
        }
        return (best, bestLen, bi, bl);
    }

    /// <summary>改写一段文字，返回片段列表（相邻的普通文字已合并）。</summary>
    public List<Segment> Rewrite(string text)
    {
        var res = new List<Segment>();
        if (string.IsNullOrEmpty(text)) return res;
        Scan(text, 0, text.Length, allowTemplates: true, res);
        return Segments.MergeText(res);
    }

    /// <summary>只要文字（读音片段保留原文），给“测试框”和不关心拼音的调用方用。</summary>
    public string RewriteToText(string text) =>
        string.Concat(Rewrite(text).Select(s => s.Kind == SegKind.ForcedPinyin ? text.Substring(s.SrcStart, s.SrcLen) : s.Text));

    private void Scan(string s, int from, int to, bool allowTemplates, List<Segment> outp)
    {
        int plainStart = from;
        int i = from;
        void FlushPlain(int upTo)
        {
            if (upTo > plainStart) outp.Add(Segment.Plain(s[plainStart..upTo], plainStart, upTo - plainStart));
        }
        while (i < to)
        {
            var (tr, tl) = MatchTrie(s, i);
            if (tr != null && i + tl > to) { tr = null; tl = 0; }
            Rule? tp = null; int tpl = 0, ist = 0, iln = 0;
            if (allowTemplates && _templates.Count > 0)
            {
                (tp, tpl, ist, iln) = MatchTemplate(s, i);
                if (tp != null && i + tpl > to) tp = null;
            }
            if (tp != null && (tr == null || tpl > tl || (tpl == tl && tp.Priority >= tr.Priority)))
            {
                FlushPlain(i);
                int star = tp.Target.IndexOf('*');
                string pre = star < 0 ? tp.Target : tp.Target[..star];
                string post = star < 0 ? "" : tp.Target[(star + 1)..];
                if (pre.Length > 0) outp.Add(Segment.Plain(pre, i, ist - i));
                if (star >= 0) Scan(s, ist, ist + iln, allowTemplates: false, outp);
                if (post.Length > 0) outp.Add(Segment.Plain(post, ist + iln, i + tpl - ist - iln));
                i += tpl; plainStart = i;
                continue;
            }
            if (tr != null)
            {
                FlushPlain(i);
                outp.Add(tr.Type == RuleType.Pinyin
                    ? Segment.Pinyin(tr.Target, i, tl)
                    : Segment.Plain(tr.Target, i, tl));
                i += tl; plainStart = i;
                continue;
            }
            i++;
        }
        FlushPlain(to);
    }
}

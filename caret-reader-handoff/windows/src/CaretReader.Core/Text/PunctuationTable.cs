using System.Globalization;

namespace CaretReader.Core.Text;

/// <summary>标点表的一行：符号、前停顿、后停顿（秒，基础值）、类别。</summary>
public sealed record PunctEntry(string Symbol, double Before, double After, PauseKind Kind, string Note, bool Overridden = false);

/// <summary>
/// 标点表（文档 5.6.1）。内置 rules/punctuation.tsv，用户改动另存为 data/用户标点表.tsv（只存改过的行，覆盖内置值）。
/// 列：符号 前停顿 后停顿 类别 备注。符号可以是多个字符（…… ——）。
/// 特殊符号用尖括号名字：&lt;换行&gt; &lt;段落&gt; &lt;标题&gt; &lt;列表项&gt; &lt;切分&gt;。
/// 合并规则：连在一起的标点后停顿取最大值（不相加）；前停顿遇到前一个标点的后停顿也取最大值。
/// </summary>
public sealed class PunctuationTable
{
    public const string Newline = "<换行>";
    public const string Paragraph = "<段落>";
    public const string Title = "<标题>";
    public const string ListItem = "<列表项>";
    public const string InnerCut = "<切分>";

    private readonly Dictionary<string, PunctEntry> _map = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PunctEntry> _builtin = new(StringComparer.Ordinal);
    private int _maxLen = 1;

    public List<RuleError> Errors { get; } = new();

    /// <summary>当前生效的全部行（按符号）。</summary>
    public IReadOnlyCollection<PunctEntry> Entries => _map.Values;

    public static PunctuationTable Parse(string builtinTsv, string? userTsv = null)
    {
        var t = new PunctuationTable();
        foreach (var e in ParseRows(builtinTsv, "punctuation.tsv", t.Errors))
        { t._builtin[e.Symbol] = e; t._map[e.Symbol] = e; }
        if (userTsv != null)
            foreach (var e in ParseRows(userTsv, "用户标点表.tsv", t.Errors))
            {
                var kind = t._builtin.TryGetValue(e.Symbol, out var b) && e.Kind == PauseKind.None ? b.Kind : e.Kind;
                t._map[e.Symbol] = e with { Kind = kind, Overridden = true };
            }
        t._maxLen = t._map.Keys.Where(k => !k.StartsWith('<')).Select(k => k.Length).DefaultIfEmpty(1).Max();
        return t;
    }

    public static PunctuationTable Load(string builtinPath, string? userPath)
    {
        string b = File.Exists(builtinPath) ? File.ReadAllText(builtinPath) : "";
        string? u = userPath != null && File.Exists(userPath) ? File.ReadAllText(userPath) : null;
        var t = Parse(b, u);
        foreach (var e in t.Errors) Log.Write($"标点表写错 {e.FileName} 第{e.Line}行：{e.Reason}");
        return t;
    }

    private static IEnumerable<PunctEntry> ParseRows(string tsv, string name, List<RuleError> errors)
    {
        var lines = tsv.Replace("\r\n", "\n").Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            string raw = lines[i];
            if (i == 0 && raw.Length > 0 && raw[0] == '\uFEFF') raw = raw[1..];
            if (raw.Length == 0 || raw.StartsWith('#')) continue;
            var c = raw.Split('\t');
            if (c.Length < 3) { errors.Add(new RuleError(name, i + 1, "至少要有 3 列（符号、前停顿、后停顿）", raw)); continue; }
            string sym = c[0];
            if (sym.Length == 0) { errors.Add(new RuleError(name, i + 1, "符号是空的", raw)); continue; }
            if (!TryNum(c[1], out double before) || !TryNum(c[2], out double after))
            { errors.Add(new RuleError(name, i + 1, "停顿要写 0～5 之间的秒数，例如 0.15", raw)); continue; }
            var kind = PauseKind.None;
            if (c.Length > 3 && c[3].Trim().Length > 0 && !Enum.TryParse(c[3].Trim(), true, out kind))
            { errors.Add(new RuleError(name, i + 1, $"类别“{c[3].Trim()}”不认识", raw)); continue; }
            yield return new PunctEntry(sym, before, after, kind, c.Length > 4 ? c[4].Trim() : "");
        }
    }

    private static bool TryNum(string s, out double v) =>
        double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v) && v >= 0 && v <= 5;

    public bool TryGet(string symbol, out PunctEntry entry) => _map.TryGetValue(symbol, out entry!);

    /// <summary>取特殊行（&lt;换行&gt; 等）；表里没有就返回 0 停顿。</summary>
    public PunctEntry Special(string name) =>
        _map.TryGetValue(name, out var e) ? e : new PunctEntry(name, 0, 0, PauseKind.None, "");

    public PunctEntry? Builtin(string symbol) => _builtin.TryGetValue(symbol, out var e) ? e : null;

    /// <summary>在 pos 处取最长的标点符号；没有返回 null。</summary>
    public PunctEntry? MatchAt(string s, int pos)
    {
        for (int len = Math.Min(_maxLen, s.Length - pos); len >= 1; len--)
            if (_map.TryGetValue(s.Substring(pos, len), out var e)) return e;
        return null;
    }

    /// <summary>两个相邻停顿合并：取最大值，不相加。</summary>
    public static double Merge(double a, double b) => Math.Max(a, b);

    /// <summary>把用户改过的行（和内置值不同的）写成用户标点表文本。</summary>
    public static string ToUserTsv(IEnumerable<PunctEntry> changed) =>
        "# 用户标点表：只保存改过的行，覆盖内置值。列：符号\t前停顿\t后停顿\t类别\t备注\n" +
        string.Concat(changed.Select(e => string.Join('\t', e.Symbol,
            e.Before.ToString("0.###", CultureInfo.InvariantCulture),
            e.After.ToString("0.###", CultureInfo.InvariantCulture), e.Kind, e.Note) + "\n"));
}

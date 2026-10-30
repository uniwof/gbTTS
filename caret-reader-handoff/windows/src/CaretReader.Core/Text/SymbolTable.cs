namespace CaretReader.Core.Text;

/// <summary>rules/symbols.tsv 的内容：按类别分组的符号读法（文档 5.4）。</summary>
public sealed class SymbolTable
{
    public Dictionary<string, string> Units { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> Currency { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> Ratio { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> Math { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> Symbols { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> Ordinals { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> Topics { get; } = new(StringComparer.Ordinal);
    public HashSet<string> Measures { get; } = new(StringComparer.Ordinal);
    public string EmojiWord { get; private set; } = "";
    public List<RuleError> Errors { get; } = new();

    public static SymbolTable Parse(string tsv)
    {
        var t = new SymbolTable();
        var lines = tsv.Replace("\r\n", "\n").Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            string raw = lines[i];
            if (i == 0 && raw.Length > 0 && raw[0] == '\uFEFF') raw = raw[1..];
            if (raw.Length == 0 || raw.StartsWith('#')) continue;
            var c = raw.Split('\t');
            void Err(string why) => t.Errors.Add(new RuleError("symbols.tsv", i + 1, why, raw));
            if (c.Length < 2 || c[1].Length == 0) { Err("至少要有 类别 和 符号 两列"); continue; }
            string cat = c[0].Trim(), sym = c[1], say = c.Length > 2 ? c[2] : "";
            if (cat != "量词" && say.Length == 0) { Err("读作是空的"); continue; }
            switch (cat)
            {
                case "单位": t.Units[sym] = say; break;
                case "货币": t.Currency[sym] = say; break;
                case "比例": t.Ratio[sym] = say; break;
                case "数学": t.Math[sym] = say; break;
                case "符号": t.Symbols[sym] = say; break;
                case "序号": t.Ordinals[sym] = say; break;
                case "话题":
                    if (!say.Contains('*')) { Err("话题的读作里要有 *（代表中间的文字）"); continue; }
                    t.Topics[sym] = say; break;
                case "量词": t.Measures.Add(sym); break;
                case "表情": t.EmojiWord = say; break;
                default: Err($"类别“{cat}”不认识"); break;
            }
        }
        return t;
    }

    public static SymbolTable Load(string path)
    {
        var t = Parse(File.Exists(path) ? File.ReadAllText(path) : "");
        foreach (var e in t.Errors) Log.Write($"符号表写错 第{e.Line}行：{e.Reason}");
        return t;
    }
}

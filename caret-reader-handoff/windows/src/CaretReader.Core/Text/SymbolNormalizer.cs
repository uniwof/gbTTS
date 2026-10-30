using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace CaretReader.Core.Text;

/// <summary>emoji 怎么读：默认不读；可改为读 symbols.tsv 里“表情”那一行的词。</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter))]
public enum EmojiMode { Skip, Say }

/// <summary>
/// 符号读法（文档 5.4 后半）：&amp; @ 等符号、#话题#、带圈数字和罗马数字、emoji。全部来自 rules/symbols.tsv。
/// 在 NumberNormalizer 之前运行（序号先变成汉字）。数学符号、单位、货币、% 由 NumberNormalizer 处理。
/// </summary>
public sealed class SymbolNormalizer
{
    private readonly SymbolTable _sym;
    private readonly List<(Regex re, string tpl)> _topics = new();
    public EmojiMode Emoji { get; set; } = EmojiMode.Skip;

    public SymbolNormalizer(SymbolTable sym)
    {
        _sym = sym;
        foreach (var (mark, tpl) in sym.Topics)
        {
            string m = Regex.Escape(mark);
            // #话题#：中间 1~30 个字，不含空白和 # 本身
            _topics.Add((new Regex(m + @"([^\s" + m + @"]{1,30})" + m, RegexOptions.CultureInvariant), tpl));
        }
    }

    public static SymbolNormalizer FromRules(string rulesDir) =>
        new(SymbolTable.Load(Path.Combine(rulesDir, "symbols.tsv")));

    public string Normalize(string text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        string s = text;
        foreach (var (re, tpl) in _topics)
            s = re.Replace(s, m => tpl.Replace("*", m.Groups[1].Value));

        var sb = new StringBuilder(s.Length);
        var e = StringInfo.GetTextElementEnumerator(s);
        bool lastEmoji = false;
        while (e.MoveNext())
        {
            string el = e.GetTextElement();
            if (_sym.Ordinals.TryGetValue(el, out var ord)) { sb.Append(ord); lastEmoji = false; continue; }
            if (_sym.Symbols.TryGetValue(el, out var say))
            {
                // 符号前后补空格以外的东西不做，读法本身即可
                sb.Append(say); lastEmoji = false; continue;
            }
            if (IsEmoji(el))
            {
                // 连续多个 emoji 只读一次“表情”
                if (Emoji == EmojiMode.Say && !lastEmoji && _sym.EmojiWord.Length > 0) sb.Append(_sym.EmojiWord);
                lastEmoji = true;
                continue;
            }
            lastEmoji = false;
            sb.Append(el);
        }
        return sb.ToString();
    }

    /// <summary>判断一个字形（可能由多个码点组成）是不是 emoji。</summary>
    public static bool IsEmoji(string element)
    {
        if (Rune.DecodeFromUtf16(element, out var r, out _) != System.Buffers.OperationStatus.Done) return false;
        int cp = r.Value;
        if (cp is >= 0x1F000 and <= 0x1FAFF) return true;   // 表情、图形、交通、国旗、补充符号
        if (cp is >= 0x2600 and <= 0x27BF) return true;     // 杂项符号、装饰符号（☀ ✂ ❤ …）
        return cp is 0x2B50 or 0x2B55 or 0x231A or 0x231B or 0x23F0 or 0x23F3;
    }
}

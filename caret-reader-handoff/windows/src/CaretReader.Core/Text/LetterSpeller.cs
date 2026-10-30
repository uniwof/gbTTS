using System.Text;
using System.Text.RegularExpressions;

namespace CaretReader.Core.Text;

/// <summary>普通英文单词在“不支持英文的音色”下怎么读（文档 5.5，默认逐个字母读）。</summary>
public enum EnglishPolicy { Spell, Skip, SayEnglish }

/// <summary>
/// 英文与字母（文档 5.5）。字母读音来自 rules/letters.tsv（只允许标准带调拼音，产出 ForcedPinyin 片段）。
///  - 读法词库已在 RulesEngine 里先处理过。
///  - 全大写 2～6 个字母的缩写（GDP、PPT）：逐个字母读。
///  - 单个字母（A 计划、B 站）：按字母表读。
///  - 字母和数字混合（MP3、5G）：数字已被 NumberNormalizer 读成汉字，这里只读字母部分。
///  - 普通英文单词：音色支持英文就原样交给模型；不支持就按 Policy（逐个字母读 / 跳过 / 读作“英文”）。
/// </summary>
public sealed class LetterSpeller
{
    private static readonly Regex Word = new(@"[A-Za-z]+(?:['’][A-Za-z]+)*", RegexOptions.CultureInvariant);
    private static readonly Regex Syllable = new(@"^[a-zü]+[1-5]$", RegexOptions.CultureInvariant);

    private readonly Dictionary<char, string> _letters = new();
    public List<RuleError> Errors { get; } = new();

    public EnglishPolicy Policy { get; set; } = EnglishPolicy.Spell;
    /// <summary>当前音色是否能直接读英文（音色包 caps.english）。</summary>
    public bool VoiceSpeaksEnglish { get; set; }
    /// <summary>Policy=SayEnglish 时读的词。</summary>
    public string EnglishWord { get; set; } = "英文";

    public static LetterSpeller Parse(string tsv)
    {
        var l = new LetterSpeller();
        var lines = tsv.Replace("\r\n", "\n").Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            string raw = lines[i];
            if (i == 0 && raw.Length > 0 && raw[0] == '\uFEFF') raw = raw[1..];
            if (raw.Length == 0 || raw.StartsWith('#')) continue;
            var c = raw.Split('\t');
            void Err(string why) => l.Errors.Add(new RuleError("letters.tsv", i + 1, why, raw));
            if (c.Length < 2 || c[0].Trim().Length != 1 || !char.IsAsciiLetter(c[0].Trim()[0])) { Err("第一列要是一个英文字母"); continue; }
            var syl = c[1].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (syl.Length == 0 || syl.Any(x => !Syllable.IsMatch(x.Replace('v', 'ü')))) { Err("拼音要写成带调拼音，例如 ai2 fu1"); continue; }
            l._letters[char.ToUpperInvariant(c[0].Trim()[0])] = string.Join(' ', syl);
        }
        return l;
    }

    public static LetterSpeller Load(string path)
    {
        var l = Parse(File.Exists(path) ? File.ReadAllText(path) : "");
        foreach (var e in l.Errors) Log.Write($"字母表写错 第{e.Line}行：{e.Reason}");
        return l;
    }

    /// <summary>把一串字母读成拼音（缺的字母原样留下，不会丢字）。</summary>
    public string? SpellPinyin(string letters)
    {
        var parts = new List<string>();
        foreach (char ch in letters)
        {
            if (!char.IsAsciiLetter(ch)) continue;
            if (!_letters.TryGetValue(char.ToUpperInvariant(ch), out var py)) return null;
            parts.Add(py);
        }
        return parts.Count == 0 ? null : string.Join(' ', parts);
    }

    /// <summary>处理一段文字，返回 Text / ForcedPinyin 片段（位置相对于输入）。</summary>
    public List<Segment> Process(string text)
    {
        var res = new List<Segment>();
        if (string.IsNullOrEmpty(text)) return res;
        int last = 0;
        foreach (Match m in Word.Matches(text))
        {
            string w = m.Value;
            bool acronym = w.Length is >= 2 and <= 6 && w.All(char.IsAsciiLetterUpper);
            bool single = w.Length == 1;
            string? say = null; bool asPinyin = false; bool keep = false;
            if (acronym || single) { say = SpellPinyin(w); asPinyin = say != null; keep = say == null; }
            else if (VoiceSpeaksEnglish) keep = true;
            else switch (Policy)
            {
                case EnglishPolicy.Spell: say = SpellPinyin(w); asPinyin = say != null; keep = say == null; break;
                case EnglishPolicy.Skip: say = ""; break;
                case EnglishPolicy.SayEnglish: say = EnglishWord; break;
            }
            if (keep) continue;
            if (m.Index > last) res.Add(Segment.Plain(text[last..m.Index], last, m.Index - last));
            if (asPinyin) res.Add(Segment.Pinyin(say!, m.Index, m.Length));
            else if (!string.IsNullOrEmpty(say)) res.Add(Segment.Plain(say, m.Index, m.Length));
            else res.Add(Segment.Plain("", m.Index, m.Length));
            last = m.Index + m.Length;
        }
        if (last < text.Length) res.Add(Segment.Plain(text[last..], last, text.Length - last));
        return Segments.MergeText(res);
    }
}

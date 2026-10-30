using CaretReader.Core.Text;
using Xunit;

namespace CaretReader.Tests;

public class NumberNormalizerTests
{
    private static readonly SymbolTable Sym = SymbolTable.Parse(RulesTestData.Read("symbols.tsv"));
    private static readonly NumberNormalizer N = new(Sym);
    private static readonly SymbolNormalizer S = new(Sym);

    public static IEnumerable<object[]> Corpus()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "corpus", "numbers.tsv");
        foreach (var line in File.ReadAllLines(path))
        {
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var c = line.Split('\t');
            yield return new object[] { c[0], c[1] };
        }
    }

    [Fact]
    public void Symbol_table_loads_without_errors() => Assert.Empty(Sym.Errors);

    [Fact]
    public void Corpus_has_at_least_200_cases() => Assert.True(Corpus().Count() >= 200);

    [Theory]
    [MemberData(nameof(Corpus))]
    public void Numbers_corpus(string src, string expected) => Assert.Equal(expected, N.Normalize(S.Normalize(src)));

    [Fact]
    public void No_arabic_digits_left()
    {
        foreach (var o in Corpus())
            Assert.DoesNotContain(N.Normalize(S.Normalize((string)o[0])), c => c is >= '0' and <= '9');
    }

    [Theory]
    [InlineData("13800138000", "幺三八零零幺三八零零零")]
    [InlineData("100", "一零零")]
    public void Read_digits(string d, string expected) =>
        Assert.Equal(expected, NumberNormalizer.ReadDigits(d, yao: d.Length == 11));

    [Fact]
    public void Empty_and_no_digit_text_unchanged()
    {
        Assert.Equal("", N.Normalize(""));
        Assert.Equal("你好。", N.Normalize("你好。"));
    }
}

public class SymbolNormalizerTests
{
    private static readonly SymbolTable Sym = SymbolTable.Parse(RulesTestData.Read("symbols.tsv"));

    [Theory]
    [InlineData("你&我", "你和我")]
    [InlineData("请@小明", "请艾特小明")]
    [InlineData("#今日话题#很热", "「今日话题」很热")]
    [InlineData("①苹果②香蕉", "一苹果二香蕉")]
    [InlineData("第Ⅱ部分", "第二部分")]
    [InlineData("好开心😀😀！", "好开心！")]
    [InlineData("C#语言", "C#语言")]
    public void Symbols(string src, string expected) => Assert.Equal(expected, new SymbolNormalizer(Sym).Normalize(src));

    [Fact]
    public void Emoji_can_be_read_once_per_run()
    {
        var s = new SymbolNormalizer(Sym) { Emoji = EmojiMode.Say };
        Assert.Equal("好开心表情！", s.Normalize("好开心😀👍！"));
    }

    [Fact]
    public void Bad_rows_reported()
    {
        var t = SymbolTable.Parse("单位\tkm\n未知\tx\ty\n话题\t#\t没有星号\n单位\tkg\t千克\n");
        Assert.Equal(3, t.Errors.Count);
        Assert.Equal("千克", t.Units["kg"]);
    }
}

public class LetterSpellerTests
{
    private static LetterSpeller L() => LetterSpeller.Parse(RulesTestData.Read("letters.tsv"));

    [Fact]
    public void Letters_table_complete()
    {
        var l = L();
        Assert.Empty(l.Errors);
        for (char c = 'A'; c <= 'Z'; c++) Assert.NotNull(l.SpellPinyin(c.ToString()));
    }

    [Fact]
    public void Golden_sentence_acronyms_and_single_letters()
    {
        // 黄金句子：他把GDP、PPT和MP3都讲清楚了，A计划比B计划好。（数字先由 NumberNormalizer 读成汉字）
        var sym = SymbolTable.Parse(RulesTestData.Read("symbols.tsv"));
        string t = new NumberNormalizer(sym).Normalize("他把GDP、PPT和MP3都讲清楚了，A计划比B计划好。");
        Assert.Equal("他把GDP、PPT和MP三都讲清楚了，A计划比B计划好。", t);
        var segs = L().Process(t);
        Assert.Equal("他把[ji4 di4 pi1]、[pi1 pi1 ti4]和[ai2 mu1 pi1]三都讲清楚了，[ei1]计划比[bi4]计划好。", Segments.Debug(segs));
        var gdp = segs.First(s => s.Kind == SegKind.ForcedPinyin);
        Assert.Equal("GDP", t.Substring(gdp.SrcStart, gdp.SrcLen));
    }

    [Fact]
    public void English_words_follow_policy()
    {
        var l = L();
        Assert.Equal("[ai2 fu1 ei1 xi1 yi4]真好", Segments.Debug(l.Process("face真好")));
        l.Policy = EnglishPolicy.Skip;
        Assert.Equal("真好", Segments.Debug(l.Process("face真好")));
        l.Policy = EnglishPolicy.SayEnglish;
        Assert.Equal("英文真好", Segments.Debug(l.Process("face真好")));
        l.VoiceSpeaksEnglish = true;
        Assert.Equal("face真好", Segments.Debug(l.Process("face真好")));
        // 缩写和单个字母即使音色支持英文也逐个读
        Assert.Equal("[ji4 di4 pi1]", Segments.Debug(l.Process("GDP")));
    }

    [Fact]
    public void Long_all_caps_is_a_word_not_acronym()
    {
        var l = L(); l.VoiceSpeaksEnglish = true;
        Assert.Equal("HELLOWORLD", Segments.Debug(l.Process("HELLOWORLD")));
    }

    [Fact]
    public void Bad_letter_rows_reported()
    {
        var l = LetterSpeller.Parse("A\tei1\nBB\tbi4\nC\txi\n");
        Assert.Equal(2, l.Errors.Count);
        Assert.Equal("ei1", l.SpellPinyin("a"));
        Assert.Null(l.SpellPinyin("C"));
    }
}

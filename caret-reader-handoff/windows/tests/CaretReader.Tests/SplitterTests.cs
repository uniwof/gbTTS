using CaretReader.Core;
using Xunit;

namespace CaretReader.Tests;

/// <summary>切句的基本不变量（P1 改造 TextSplitter 时这些必须继续成立）。</summary>
public class SplitterTests
{
    private const string Sample =
        "雨停了，街上的灯一盏一盏亮起来。他站在门口，没有进去，也没有离开；" +
        "屋里传来熟悉的笑声：像很多年前一样。\n他深吸一口气，抬手敲了敲门！门开了吗？";

    [Fact]
    public void Empty_or_blank_text_gives_no_clauses()
    {
        Assert.Empty(TextSplitter.Split(null, new PauseSettings()));
        Assert.Empty(TextSplitter.Split("   \n\t ", new PauseSettings()));
    }

    [Fact]
    public void Clauses_respect_length_limits()
    {
        string longText = string.Concat(Enumerable.Repeat(Sample, 20));
        var cl = TextSplitter.Split(longText, new PauseSettings());
        Assert.NotEmpty(cl);
        Assert.True(cl[0].Text.Length <= TextSplitter.HardMaxClauseChars);
        Assert.All(cl, c => Assert.True(c.Text.Length <= TextSplitter.HardMaxClauseChars, $"子句过长：{c.Text.Length}"));
        Assert.All(cl, c => Assert.True(c.PauseAfterSeconds >= 0));
    }

    [Fact]
    public void No_text_is_lost_except_whitespace_and_punctuation()
    {
        var cl = TextSplitter.Split(Sample, new PauseSettings());
        static string Han(string s) => new(s.Where(ch => ch >= 0x4E00 && ch <= 0x9FFF).ToArray());
        Assert.Equal(Han(Sample), Han(string.Concat(cl.Select(c => c.Text))));
    }

    [Fact]
    public void Sentence_end_pauses_longer_than_comma()
    {
        var cl = TextSplitter.Split("今天天气很好，我们去公园。然后回家吃饭，再睡觉。", new PauseSettings());
        Assert.True(cl.Count >= 2);
        double max = cl.Max(c => c.PauseAfterSeconds), min = cl.Min(c => c.PauseAfterSeconds);
        Assert.True(max > min);
    }
}

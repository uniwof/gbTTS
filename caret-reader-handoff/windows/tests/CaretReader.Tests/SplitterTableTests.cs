using System.Globalization;
using CaretReader.Core;
using CaretReader.Core.Text;
using Xunit;

namespace CaretReader.Tests;

/// <summary>P1 新停顿模型（标点表 + 前停顿 + 标题识别）的快照测试（文档 14.1“标点停顿”）。</summary>
public class SplitterTableTests
{
    private static readonly PunctuationTable T = PunctuationTable.Parse(RulesTestData.Read("punctuation.tsv"));

    private static string Snap(string text, SplitOptions? o = null, bool first = false) =>
        string.Join(" | ", TextSplitter.Split(text, T, o, firstChunk: first)
            .Select(c => c.Text + "<" + c.PauseAfterSeconds.ToString("0.##", CultureInfo.InvariantCulture) + ">"));

    [Fact]
    public void Golden_yyds_sentence_book_title_and_dash()
    {
        // 《 前停 0.08 →“读了”后面停 0.08；》后停 0.12；—— 后停 0.25；…… 0.55
        // 》和——相连：取 max(0.12, 0.08, 0.25) = 0.25，不相加
        Assert.Equal("永远的神！<0.45> | 我昨天读了<0.08> | 《三体》——<0.25> | 真好看……<0.55>",
            Snap("永远的神！我昨天读了《三体》——真好看……"));
    }

    [Fact]
    public void Period_then_open_title_takes_max()
    {
        // 文档例：“。《”取 max(0.45, 0.08) = 0.45
        Assert.Equal("看完了。<0.45> | 《三体》<0.12> | 很好<0>", Snap("看完了。《三体》很好"));
    }

    [Fact]
    public void Consecutive_marks_take_max_not_sum()
    {
        Assert.Equal("真的吗？！<0.5> | 对<0>", Snap("真的吗？！对"));
        Assert.Equal("他说：<0.25> | “好。”<0.45> | 然后走了<0>", Snap("他说：“好。”然后走了"));
    }

    [Fact]
    public void Quote_open_has_pre_pause_and_close_sticks_to_clause()
    {
        Assert.Equal("他说<0.05> | “你好”<0.08> | 就走了<0>", Snap("他说“你好”就走了"));
    }

    [Fact]
    public void Brackets_pre_and_post()
    {
        Assert.Equal("苹果<0.1> | （红色的）<0.1> | 很甜<0>", Snap("苹果（红色的）很甜"));
    }

    [Fact]
    public void Newline_paragraph_and_title()
    {
        string text = "第三章 结论\n这是一段比标题长很多的正文内容。\n\n下一段开始了，这里也比较长。";
        Assert.Equal("第三章 结论<0.7> | 这是一段比标题长很多的正文内容。<0.8> | 下一段开始了，<0.15> | 这里也比较长。<0.45>", Snap(text));
        var off = new SplitOptions { TitleDetection = false };
        Assert.StartsWith("第三章 结论<0.45>", Snap(text, off));
    }

    [Fact]
    public void Title_rule_needs_longer_next_line_and_no_sentence_end()
    {
        Assert.StartsWith("短行<0.45>", Snap("短行\n也短"));            // 下一行不更长
        Assert.StartsWith("结束了。<0.45>", Snap("结束了。\n后面是很长很长很长的一行文字"));
        Assert.StartsWith("一二三四五六七八九十一二三四五六七八九十一二三四五六<0.45>",
            Snap("一二三四五六七八九十一二三四五六七八九十一二三四五六\n" + new string('长', 40)));   // 超过 25 字
    }

    [Fact]
    public void Markdown_line_kinds_drive_title_and_list_pauses()
    {
        var md = MarkdownCleaner.Parse(RulesTestData.Read("markdown.tsv"));
        var r = md.Clean("## 第三章 结论\n- 苹果\n- 香蕉\n完");
        var o = new SplitOptions { LineKinds = r.LineKinds, TitleDetection = false };
        Assert.Equal("第三章 结论<0.7> | 苹果<0.35> | 香蕉<0.35> | 完<0>", Snap(r.Text, o));
    }

    [Fact]
    public void Number_url_and_abbreviation_protection_kept()
    {
        Assert.Equal("圆周率3.14，<0.15> | 时间10:30<0>", Snap("圆周率3.14，时间10:30"));
        Assert.Equal("访问 https://example.com/a?b=1 了解<0>", Snap("访问 https://example.com/a?b=1 了解"));
        Assert.Equal("Mr. Smith came.<0.45>", Snap("Mr. Smith came."));
        Assert.Equal("1,000人<0>", Snap("1,000人"));
    }

    [Fact]
    public void Long_clause_inner_cut_gets_bridge_gap()
    {
        string longText = new string('字', 30) + "的" + new string('字', 30) + "。";
        var cl = TextSplitter.Split(longText, T, null, firstChunk: false);
        Assert.True(cl.Count >= 2);
        Assert.All(cl.Take(cl.Count - 1), c => { Assert.Equal(0.04, c.PauseAfterSeconds); Assert.Equal(PauseKind.InnerCut, c.Kind); });
        Assert.Equal(0.45, cl[^1].PauseAfterSeconds);
        Assert.All(cl, c => Assert.True(c.Text.Length <= TextSplitter.MaxClauseChars));
    }

    [Fact]
    public void User_table_override_changes_pause()
    {
        var t = PunctuationTable.Parse(RulesTestData.Read("punctuation.tsv"), "，\t0\t0.3\n");
        var cl = TextSplitter.Split("你好，世界", t, null, firstChunk: false);
        Assert.Equal(0.3, cl[0].PauseAfterSeconds);
        Assert.Equal(PauseKind.Comma, cl[0].Kind);
    }

    [Fact]
    public void Old_pause_settings_api_unchanged()
    {
        var cl = TextSplitter.Split("你好，世界。", new PauseSettings());
        Assert.Equal(2, cl.Count);
        Assert.Equal(0.15, cl[0].PauseAfterSeconds);
        Assert.Equal(0.5, cl[1].PauseAfterSeconds);
    }

    [Fact]
    public void Empty_input() => Assert.Empty(TextSplitter.Split("", T));
}

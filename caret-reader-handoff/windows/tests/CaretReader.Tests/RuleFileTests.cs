using CaretReader.Core.Text;
using Xunit;

namespace CaretReader.Tests;

public class RuleFileTests
{
    [Fact]
    public void Parses_three_types_options_and_comments()
    {
        var f = RuleFile.Parse(
            "# 注释\n" +
            "替换\tYYDS\t永远的神\t整词,不分大小写\t网络用语\n" +
            "模板\t《*》\t书名，*，\t停用\t示例\n" +
            "读音\t银行\tyin2 hang2\t\t多音字纠正\n" +
            "\n", "u.tsv", RuleFile.PriorityUser);
        Assert.Empty(f.Errors);
        Assert.Equal(3, f.Rules.Count);
        Assert.Equal(RuleType.Replace, f.Rules[0].Type);
        Assert.True(f.Rules[0].WholeWord && f.Rules[0].IgnoreCase);
        Assert.False(f.Rules[1].Enabled);
        Assert.Equal("yin2 hang2", f.Rules[2].Target);
        Assert.Equal(4, f.Rules[2].Line);
    }

    [Theory]
    [InlineData("改写\ta\tb", "类型")]
    [InlineData("替换\ta", "3 列")]
    [InlineData("替换\t\tb", "原文是空的")]
    [InlineData("替换\ta\tb\t加粗", "选项")]
    [InlineData("模板\t《》\tb", "模板")]
    [InlineData("模板\t*》\tb", "模板")]
    [InlineData("模板\t《**》\tb", "模板")]
    [InlineData("读音\t银行\tyinhang", "带调拼音")]
    [InlineData("读音\t银行\tyin2", "音节")]
    public void Bad_line_is_skipped_with_reason(string line, string reasonPart)
    {
        var f = RuleFile.Parse("替换\t好\t好的\n" + line + "\n读音\t重庆\tchong2 qing4\n", "u.tsv", 300);
        Assert.Equal(2, f.Rules.Count);              // 一行写错不影响其它行
        var e = Assert.Single(f.Errors);
        Assert.Equal(2, e.Line);
        Assert.Contains(reasonPart, e.Reason);
    }

    [Fact]
    public void Handles_bom_and_crlf()
    {
        var f = RuleFile.Parse("\uFEFF替换\tA\tB\r\n替换\tC\tD\r\n", "x.tsv", 100);
        Assert.Empty(f.Errors);
        Assert.Equal("A", f.Rules[0].Source);
        Assert.Equal("D", f.Rules[1].Target);
    }

    [Theory]
    [InlineData("slang.tsv")]
    [InlineData("templates.tsv")]
    public void Builtin_rule_files_have_no_errors(string name)
    {
        var f = RuleFile.Parse(RulesTestData.Read(name), name, RuleFile.PriorityBuiltin);
        Assert.Empty(f.Errors);
        Assert.NotEmpty(f.Rules);
    }

    [Fact]
    public void Slang_pack_has_at_least_50_whole_word_rules()
    {
        var f = RuleFile.Parse(RulesTestData.Read("slang.tsv"), "slang.tsv", RuleFile.PrioritySlang);
        Assert.True(f.Rules.Count >= 50);
        Assert.All(f.Rules, r => Assert.True(r.WholeWord, r.Source + " 必须整词匹配"));
    }
}

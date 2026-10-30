using CaretReader.Core.Text;
using Xunit;

namespace CaretReader.Tests;

/// <summary>文本层总装：文档 14.2 的黄金句子逐句过一遍（改写结果快照）。</summary>
public class TextPipelineTests
{
    private static TextPipeline P(string? user = null, TextPipelineOptions? o = null)
    {
        string? path = null;
        if (user != null)
        {
            path = Path.Combine(Path.GetTempPath(), "gbtts-user-" + Guid.NewGuid().ToString("N") + ".tsv");
            File.WriteAllText(path, user);
        }
        return TextPipeline.Load(RulesTestData.Dir, path, o);
    }

    private static string Run(TextPipeline p, string s) => p.Process(s).Display(s);

    [Fact]
    public void Builtin_rules_load_clean() => Assert.Empty(P().Errors);

    [Theory]
    [InlineData("YYDS！我昨天读了《三体》——真好看……", "永远的神！我昨天读了《三体》——真好看……")]
    [InlineData("2026年10月30日10:30，气温25℃，降水概率30%。", "二零二六年十月三十日十点三十分，气温二十五摄氏度，降水概率百分之三十。")]
    [InlineData("他把GDP、PPT和MP3都讲清楚了，A计划比B计划好。",
        "他把[ji4 di4 pi1]、[pi1 pi1 ti4]和[ai2 mu1 pi1]三都讲清楚了，[ei1]计划比[bi4]计划好。")]
    [InlineData("## 第三章 结论", "第三章 结论")]
    public void Golden_sentences(string src, string expected) => Assert.Equal(expected, Run(P(), src));

    [Fact]
    public void Golden_polyphone_sentence_with_user_rules()
    {
        var p = P("读音\t银行\tyin2 hang2\n读音\t重庆\tchong2 qing4\n");
        Assert.Equal("[yin2 hang2]行长在[chong2 qing4]重新开了一家[yin2 hang2]。", Run(p, "银行行长在重庆重新开了一家银行。"));
    }

    [Fact]
    public void Golden_phone_sentence()
    {
        // 网址由切句里的“读不出的长串”处理，这里只看数字
        string r = Run(P(), "请拨打13800138000，或访问 https://example.com 了解详情。");
        Assert.StartsWith("请拨打幺三八零零幺三八零零零，或访问 ", r);
    }

    [Fact]
    public void Markdown_line_kinds_returned_and_can_be_disabled()
    {
        var r = P().Process("# 标题\n正文");
        Assert.NotNull(r.LineKinds);
        Assert.Equal(PauseKind.Title, r.LineKinds![0]);
        var off = P(o: new TextPipelineOptions { MarkdownClean = false });
        Assert.StartsWith("#", Run(off, "# 标题"));
    }

    [Fact]
    public void Slang_pack_can_be_turned_off()
    {
        var o = new TextPipelineOptions { VoiceSpeaksEnglish = true };
        o.RulePacks["slang.tsv"] = false;
        Assert.Equal("[wai4 wai4 di4 ai2 si1]", Run(P(o: o), "YYDS"));
    }

    [Fact]
    public void User_rule_beats_slang()
    {
        Assert.Equal("永远滴神", Run(P("替换\tYYDS\t永远滴神\t整词\n"), "YYDS"));
    }

    [Fact]
    public void Replacement_output_still_gets_numbers_normalized()
    {
        // 规则产出的文字继续进入数字/字母阶段
        Assert.Equal("第五代", Run(P("替换\t5G\t第5代\n"), "5G"));
    }
}

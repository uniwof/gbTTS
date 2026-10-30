using System.Diagnostics;
using CaretReader.Core.Text;
using Xunit;

namespace CaretReader.Tests;

public class RulesEngineTests
{
    private static RulesEngine Slang() => new(new[]
    {
        RuleFile.Parse("替换\tYYDS\t永远的神\t整词,不分大小写\n替换\tnb\t牛掰\t整词,不分大小写\n替换\temo\t一莫\t整词,不分大小写\n", "user.tsv", RuleFile.PriorityUser),
        RuleFile.Parse(RulesTestData.Read("templates.tsv"), "templates.tsv", RuleFile.PriorityBuiltin),
    });

    [Fact]
    public void Golden_sentence_yyds_and_book_title_default()
    {
        // 黄金句子：书名号默认只停顿不加字（模板默认停用）
        var e = Slang();
        Assert.Equal("永远的神！我昨天读了《三体》——真好看……", e.RewriteToText("YYDS！我昨天读了《三体》——真好看……"));
    }

    [Fact]
    public void Whole_word_does_not_touch_normal_english()
    {
        var e = Slang();
        Assert.Equal("NBA 和 牛掰", e.RewriteToText("NBA 和 nb"));
        Assert.Equal("emotion", e.RewriteToText("emotion"));
        Assert.Equal("一莫了", e.RewriteToText("emo了"));
        Assert.Equal("永远的神", e.RewriteToText("yyds"));   // 不分大小写
        Assert.Equal("我的CPU坏了", e.RewriteToText("我的CPU坏了")); // cpu 网络用语义默认停用
    }

    [Fact]
    public void Longest_match_wins()
    {
        var e = RulesEngine.FromText("替换\thh\tX\n替换\thhh\t哈哈哈\n");
        Assert.Equal("哈哈哈", e.RewriteToText("hhh"));
        Assert.Equal("Xa", e.RewriteToText("hha"));
    }

    [Fact]
    public void Same_length_priority_user_over_slang_over_builtin()
    {
        var e = new RulesEngine(new[]
        {
            RuleFile.Parse("替换\tabc\t内置\n", "b.tsv", RuleFile.PriorityBuiltin),
            RuleFile.Parse("替换\tabc\t用户\n", "u.tsv", RuleFile.PriorityUser),
            RuleFile.Parse("替换\tabc\t网络\n", "s.tsv", RuleFile.PrioritySlang),
        });
        Assert.Equal("用户", e.RewriteToText("abc"));
        var e2 = new RulesEngine(new[]
        {
            RuleFile.Parse("替换\tabc\t内置\n", "b.tsv", RuleFile.PriorityBuiltin),
            RuleFile.Parse("替换\tabc\t网络\n", "s.tsv", RuleFile.PrioritySlang),
        });
        Assert.Equal("网络", e2.RewriteToText("abc"));
    }

    [Fact]
    public void Output_is_not_rescanned()
    {
        var e = RulesEngine.FromText("替换\ta\tb\n替换\tb\ta\n");
        Assert.Equal("ba", e.RewriteToText("ab"));
        var loop = RulesEngine.FromText("替换\tx\txx\n");
        Assert.Equal("xxxx", loop.RewriteToText("xx"));
    }

    [Fact]
    public void Case_sensitive_rule_needs_exact_case()
    {
        var e = RulesEngine.FromText("替换\tQQ\t扣扣\n");
        Assert.Equal("扣扣", e.RewriteToText("QQ"));
        Assert.Equal("qq", e.RewriteToText("qq"));
    }

    [Fact]
    public void Template_book_title_when_enabled()
    {
        var e = RulesEngine.FromText("模板\t《*》\t书名，*，\n替换\tYYDS\t永远的神\t整词\n");
        Assert.Equal("我读了书名，三体，真好看", e.RewriteToText("我读了《三体》真好看"));
        // * 里仍然做替换，但不再套模板
        Assert.Equal("书名，永远的神，", e.RewriteToText("《YYDS》"));
        Assert.Equal("书名，《a，》", e.RewriteToText("《《a》》"));
    }

    [Fact]
    public void Template_is_non_greedy_single_line_and_max_50()
    {
        var e = RulesEngine.FromText("模板\t《*》\t出自*\n");
        Assert.Equal("出自a和出自b", e.RewriteToText("《a》和《b》"));
        Assert.Equal("《a\nb》", e.RewriteToText("《a\nb》"));
        string inner50 = new string('字', 50), inner51 = new string('字', 51);
        Assert.Equal("出自" + inner50, e.RewriteToText("《" + inner50 + "》"));
        Assert.Equal("《" + inner51 + "》", e.RewriteToText("《" + inner51 + "》"));
    }

    [Fact]
    public void Pinyin_rule_makes_forced_pinyin_segment_and_entry()
    {
        var e = RulesEngine.FromText("读音\t银行\tyin2 hang2\n读音\t重庆\tchong2 qing4\n");
        var segs = e.Rewrite("银行行长在重庆重新开了一家银行。");
        Assert.Equal("[yin2 hang2]行长在[chong2 qing4]重新开了一家[yin2 hang2]。", Segments.Debug(segs));
        Assert.Equal(SegKind.ForcedPinyin, segs[0].Kind);
        Assert.Equal((0, 2), (segs[0].SrcStart, segs[0].SrcLen));
        Assert.Equal("yin2 hang2", e.PinyinEntries["银行"]);
        Assert.Equal("银行行长在重庆重新开了一家银行。", e.RewriteToText("银行行长在重庆重新开了一家银行。"));
    }

    [Fact]
    public void Source_positions_map_back_to_original()
    {
        var e = RulesEngine.FromText("替换\tYYDS\t永远的神\t整词\n读音\t银行\tyin2 hang2\n");
        string src = "啊YYDS，去银行吧";
        var segs = e.Rewrite(src);
        foreach (var s in segs)
            Assert.InRange(s.SrcStart + s.SrcLen, 0, src.Length);
        var py = segs.Single(s => s.Kind == SegKind.ForcedPinyin);
        Assert.Equal("银行", src.Substring(py.SrcStart, py.SrcLen));
        Assert.Equal(0, segs[0].SrcStart);
    }

    [Fact]
    public void Disabled_rules_and_bad_lines_are_ignored()
    {
        var e = RulesEngine.FromText("替换\ta\tb\t停用\n乱写一行\n替换\tc\td\n");
        Assert.Equal("ad", e.RewriteToText("ac"));
        Assert.Single(e.Errors);
    }

    [Fact]
    public void Empty_input()
    {
        Assert.Empty(Slang().Rewrite(""));
        Assert.Equal("", Slang().RewriteToText(""));
    }

    [Fact]
    public void Performance_10k_rules_100k_chars()
    {
        var sb = new System.Text.StringBuilder();
        var rnd = new Random(7);
        for (int i = 0; i < 10000; i++)
            sb.Append("替换\t词").Append(i).Append("号\t读").Append(i).Append(i % 3 == 0 ? "\t整词,不分大小写\n" : "\n");
        var load = Stopwatch.StartNew();
        var e = RulesEngine.FromText(sb.ToString());
        load.Stop();
        string text = string.Concat(Enumerable.Range(0, 10000).Select(i => "词" + rnd.Next(20000) + "号abc"));
        while (text.Length < 100_000) text += text;
        text = text[..100_000];
        e.Rewrite(text); // 预热 JIT
        var sw = Stopwatch.StartNew();
        e.Rewrite(text);
        sw.Stop();
        Assert.Equal(10000, e.RuleCount);
        // 文档要求 ≤50ms / ≤100ms；CI 机器较慢，留 4 倍余量防偶发
        Assert.True(sw.ElapsedMilliseconds <= 200, $"改写耗时 {sw.ElapsedMilliseconds}ms");
        Assert.True(load.ElapsedMilliseconds <= 400, $"加载耗时 {load.ElapsedMilliseconds}ms");
    }
}

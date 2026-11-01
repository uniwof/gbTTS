using CaretReader.Core.Text;
using Xunit;

namespace CaretReader.Tests;

public class UserLexiconTests
{
    private static string Tmp() => Path.Combine(Path.GetTempPath(), "gbtts-ul-" + Guid.NewGuid().ToString("N"), "用户读法词库.tsv");

    [Fact]
    public void Add_replace_then_pipeline_reads_it()
    {
        string p = Tmp();
        var r = UserLexicon.AddReading(p, "  K8s ", "库伯奈提斯");
        Assert.True(r.Ok);
        var pipe = TextPipeline.Load(RulesTestData.Dir, p);
        Assert.Equal("用库伯奈提斯部署", pipe.Process("用K8s部署").Display("用K8s部署"));
    }

    [Fact]
    public void Same_source_overwrites_and_keeps_other_lines()
    {
        string p = Tmp();
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, "# 注释\n替换\tabc\t甲\t\t\n");
        Assert.True(UserLexicon.AddReading(p, "xyz", "乙").Ok);
        var r = UserLexicon.AddReading(p, "xyz", "丙");
        Assert.True(r.Ok && r.Replaced);
        string t = File.ReadAllText(p);
        Assert.Contains("# 注释", t); Assert.Contains("abc", t);
        Assert.Contains("丙", t); Assert.DoesNotContain("乙", t);
    }

    [Fact]
    public void Pinyin_reading_becomes_pinyin_rule_and_checks_length()
    {
        string p = Tmp();
        Assert.True(UserLexicon.AddReading(p, "银行", "yin2 hang2").Ok);
        Assert.Contains("读音\t银行\tyin2 hang2", File.ReadAllText(p));
        var bad = UserLexicon.AddReading(p, "银行行", "yin2 hang2");
        Assert.False(bad.Ok);
    }

    [Theory]
    [InlineData(null, "x")]
    [InlineData("", "x")]
    [InlineData("a\nb", "x")]
    [InlineData("abc", "")]
    [InlineData("abc", "  ")]
    public void Bad_input_is_refused_and_writes_nothing(string? src, string rd)
    {
        string p = Tmp();
        Assert.False(UserLexicon.AddReading(p, src, rd).Ok);
        Assert.False(File.Exists(p));
    }

    [Fact]
    public void Too_long_selection_refused()
        => Assert.False(UserLexicon.AddReading(Tmp(), new string('字', 31), "x").Ok);

    [Fact]
    public void English_defaults_to_whole_word()
    {
        string p = Tmp();
        UserLexicon.AddReading(p, "cat", "猫");
        var pipe = TextPipeline.Load(RulesTestData.Dir, p);
        var plain = TextPipeline.Load(RulesTestData.Dir, null);
        Assert.Equal(plain.Process("category").Display("category"), pipe.Process("category").Display("category"));
        Assert.StartsWith("猫", pipe.Process("cat").Display("cat"));
    }
}

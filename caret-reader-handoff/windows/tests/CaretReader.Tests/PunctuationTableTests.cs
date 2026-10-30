using CaretReader.Core.Text;
using Xunit;

namespace CaretReader.Tests;

public class PunctuationTableTests
{
    private static PunctuationTable Builtin(string? user = null) =>
        PunctuationTable.Parse(RulesTestData.Read("punctuation.tsv"), user);

    [Fact]
    public void Builtin_table_loads_without_errors_and_has_doc_values()
    {
        var t = Builtin();
        Assert.Empty(t.Errors);
        Assert.True(t.TryGet("，", out var c)); Assert.Equal(0.15, c.After); Assert.Equal(PauseKind.Comma, c.Kind);
        Assert.True(t.TryGet("？", out var q)); Assert.Equal(0.50, q.After);
        Assert.True(t.TryGet("《", out var o)); Assert.Equal(0.08, o.Before); Assert.Equal(0, o.After);
        Assert.True(t.TryGet("》", out var cl)); Assert.Equal(0.12, cl.After);
        Assert.Equal(0.80, t.Special(PunctuationTable.Paragraph).After);
        Assert.Equal(0.70, t.Special(PunctuationTable.Title).After);
        Assert.Equal(0.35, t.Special(PunctuationTable.ListItem).After);
        Assert.Equal(0.45, t.Special(PunctuationTable.Newline).After);
        Assert.Equal(0.04, t.Special(PunctuationTable.InnerCut).After);
    }

    [Fact]
    public void Longest_symbol_matches_first()
    {
        var t = Builtin();
        Assert.Equal("……", t.MatchAt("好……", 1)!.Symbol);
        Assert.Equal("——", t.MatchAt("a——b", 1)!.Symbol);
        Assert.Equal("...", t.MatchAt("x...", 1)!.Symbol);
        Assert.Null(t.MatchAt("好", 0));
    }

    [Fact]
    public void Merge_takes_max_not_sum()
    {
        var t = Builtin();
        t.TryGet("。", out var p); t.TryGet("《", out var o);
        Assert.Equal(0.45, PunctuationTable.Merge(p.After, o.Before)); // 文档例：“。《” 取 0.45
        t.TryGet("！", out var ex); t.TryGet("？", out var q);
        Assert.Equal(0.50, PunctuationTable.Merge(ex.After, q.After));
    }

    [Fact]
    public void User_rows_override_builtin_and_keep_kind()
    {
        var t = Builtin("，\t0\t0.3\n《\t0.2\t0\tTitleOpen\n");
        t.TryGet("，", out var c);
        Assert.Equal(0.3, c.After);
        Assert.True(c.Overridden);
        Assert.Equal(PauseKind.Comma, c.Kind);           // 用户行没写类别时沿用内置
        Assert.Equal(0.15, t.Builtin("，")!.After);       // 内置值仍可用于“恢复默认”
        t.TryGet("。", out var p); Assert.False(p.Overridden);
    }

    [Fact]
    public void Bad_user_rows_are_skipped()
    {
        var t = Builtin("，\tabc\t0.3\n、\t0\t9\n；\t0\t0.4\tWrongKind\n");
        Assert.Equal(3, t.Errors.Count);
        t.TryGet("，", out var c); Assert.Equal(0.15, c.After);
    }

    [Fact]
    public void User_tsv_roundtrip()
    {
        var t = Builtin();
        t.TryGet("，", out var c);
        string tsv = PunctuationTable.ToUserTsv(new[] { c with { After = 0.2 } });
        var t2 = Builtin(tsv);
        Assert.Empty(t2.Errors);
        t2.TryGet("，", out var c2); Assert.Equal(0.2, c2.After);
    }
}

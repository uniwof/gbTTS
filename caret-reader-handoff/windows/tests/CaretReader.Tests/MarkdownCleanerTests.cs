using CaretReader.Core.Text;
using Xunit;

namespace CaretReader.Tests;

public class MarkdownCleanerTests
{
    private static MarkdownCleaner M() => MarkdownCleaner.Parse(RulesTestData.Read("markdown.tsv"));

    [Fact]
    public void Builtin_markdown_rules_load() => Assert.Empty(M().Errors);

    [Fact]
    public void Heading_marked_as_title()
    {
        var r = M().Clean("## 第三章 结论\n正文在这里。");
        Assert.Equal("第三章 结论\n正文在这里。", r.Text);
        Assert.Equal(new[] { PauseKind.Title, PauseKind.None }, r.LineKinds);
    }

    [Theory]
    [InlineData("这是**粗体**和*斜体*，还有__下划线粗体__与`代码`。", "这是粗体和斜体，还有下划线粗体与代码。")]
    [InlineData("请看[官方文档](https://example.com)。", "请看官方文档。")]
    [InlineData("![一只猫](a.png)", "一只猫")]
    [InlineData("> 引用的话", "引用的话")]
    [InlineData("2*3*4=24", "2*3*4=24")]
    [InlineData("~~删掉~~保留", "删掉保留")]
    [InlineData("没有任何标记的句子。", "没有任何标记的句子。")]
    public void Inline_and_prefix_cleanup(string src, string expected) =>
        Assert.Equal(expected, M().Clean(src).Text);

    [Fact]
    public void List_items_marked()
    {
        var r = M().Clean("- 苹果\n* 香蕉\n+ 梨\n1. 第一\n2) 第二\n- [x] 完成");
        Assert.Equal("苹果\n香蕉\n梨\n第一\n第二\n完成", r.Text);
        Assert.All(r.LineKinds, k => Assert.Equal(PauseKind.ListItem, k));
    }

    [Fact]
    public void Table_rows_read_cells_with_commas()
    {
        var r = M().Clean("| 名字 | 年龄 |\n|---|:---:|\n| 小明 | 12 |");
        Assert.Equal("名字，年龄\n小明，12", r.Text);
    }

    [Fact]
    public void Code_block_replaced_by_default_or_read_when_enabled()
    {
        string src = "看代码：\n```python\nprint('hi')\n```\n完了。";
        Assert.Equal("看代码：\n（代码略）\n完了。", M().Clean(src).Text);
        var m = M(); m.ReadCode = true;
        Assert.Equal("看代码：\nprint('hi')\n完了。", m.Clean(src).Text);
    }

    [Fact]
    public void Horizontal_rule_dropped_and_quote_heading_combo()
    {
        Assert.Equal("上\n下", M().Clean("上\n---\n下").Text);
        var r = M().Clean("> ## 引用里的标题");
        Assert.Equal("引用里的标题", r.Text);
        Assert.Equal(PauseKind.Title, r.LineKinds[0]);
    }

    [Fact]
    public void Bad_regex_line_skipped()
    {
        var m = MarkdownCleaner.Parse("行内\t(未闭合\t删除\n行内\t\\*\\*(.+?)\\*\\*\t替换\t$1\n");
        Assert.Single(m.Errors);
        Assert.Equal("粗", m.Clean("**粗**").Text);
    }
}

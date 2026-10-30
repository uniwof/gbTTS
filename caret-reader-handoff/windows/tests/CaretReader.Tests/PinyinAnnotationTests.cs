using CaretReader.Core;
using CaretReader.Core.Text;
using Xunit;

namespace CaretReader.Tests;

public class PinyinAnnotationTests
{
    private static readonly PunctuationTable T = PunctuationTable.Parse(RulesTestData.Read("punctuation.tsv"));

    [Fact]
    public void Encode_strip_roundtrip()
    {
        var p = TextPipeline.Load(RulesTestData.Dir, null);
        string src = "他把GDP讲清楚了。";
        var r = p.Process(src);
        string enc = PinyinAnnotation.Encode(r.Segments, src);
        Assert.Contains(PinyinAnnotation.Wrap("GDP", "ji4 di4 pi1"), enc);
        Assert.Equal(src, PinyinAnnotation.Strip(enc));
    }

    [Fact]
    public void Walk_handles_broken_annotation()
    {
        var pieces = new List<(string, string?)>();
        PinyinAnnotation.Walk("a\uFFF9GDP\uFFFAji4\uFFFBb\uFFF9X\uFFFAyi", (t, py) => pieces.Add((t, py)));
        Assert.Equal(("a", null), pieces[0]);
        Assert.Equal(("GDP", "ji4"), pieces[1]);
        Assert.Equal(("b", null), pieces[2]);
        Assert.Equal(("X", null), pieces[3]);
    }

    [Fact]
    public void Splitter_never_cuts_inside_annotation()
    {
        string ann = PinyinAnnotation.Wrap("GDP", "ji4 di4 pi1");
        string text = string.Concat(Enumerable.Repeat("这是一个比较长的句子里面有" + ann + "和更多的文字", 4)) + "。";
        foreach (var cl in TextSplitter.Split(text, T, null, firstChunk: true))
        {
            int open = cl.Text.Count(c => c == PinyinAnnotation.Anchor);
            int close = cl.Text.Count(c => c == PinyinAnnotation.Terminator);
            Assert.Equal(open, close);
        }
        Assert.Equal(PinyinAnnotation.Strip(text).Replace("。", ""),
            string.Concat(TextSplitter.Split(text, T, null, firstChunk: true).Select(c => PinyinAnnotation.Strip(c.Text))).Replace("。", ""));
    }

    [Fact]
    public void Punctuation_inside_annotation_is_ignored()
    {
        string text = "看" + PinyinAnnotation.Wrap("e.g.", "yi4 ji4") + "好";
        var cl = TextSplitter.Split(text, T, null, firstChunk: false);
        Assert.Single(cl);
    }
}

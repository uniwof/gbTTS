using CaretReader.Core.Text;
using Xunit;

namespace CaretReader.Tests;

/// <summary>黄金句子（精简版第 6 节）：tests/golden.tsv 每行“原文⇥期望文字”；用户规则每行以 # 用户 开头。</summary>
public class GoldenTests
{
    [Fact]
    public void Golden_file_lines_all_match()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "golden.tsv");
        var user = "替换\tYYDS\t永远的神\t整词\n读音\t银行\tyin2 hang2\n读音\t重庆\tchong2 qing4\n";
        string up = Path.Combine(Path.GetTempPath(), "gbtts-golden-" + Guid.NewGuid().ToString("N") + ".tsv");
        File.WriteAllText(up, user);
        var p = TextPipeline.Load(RulesTestData.Dir, up);
        int n = 0;
        var bad = new List<string>();
        foreach (var line in File.ReadAllLines(path))
        {
            if (line.Length == 0 || line[0] == '#') continue;
            var c = line.Split('\t');
            string got = p.Process(c[0]).Display(c[0]);
            n++;
            if (got != c[1]) bad.Add($"{c[0]}\n  期望 {c[1]}\n  实际 {got}");
        }
        Assert.True(n >= 6);
        Assert.True(bad.Count == 0, string.Join("\n", bad));
    }
}

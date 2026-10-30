using CaretReader.Core;
using CaretReader.Core.Settings;
using CaretReader.Core.Text;
using Xunit;

namespace CaretReader.Tests;

public class SettingsMigrationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gbtts-mig-" + Guid.NewGuid().ToString("N"));
    private static readonly PunctuationTable Builtin = PunctuationTable.Parse(RulesTestData.Read("punctuation.tsv"));

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void Unchanged_old_pauses_follow_new_defaults()
    {
        Assert.Empty(SettingsMigration.PausesToPunctRows(new PauseSettings(), Builtin));
    }

    [Fact]
    public void Changed_pauses_become_user_rows()
    {
        var old = new PauseSettings { Comma = 0.3, Newline = 0.9 };
        var rows = SettingsMigration.PausesToPunctRows(old, Builtin);
        Assert.Contains(rows, r => r.Symbol == "，" && r.After == 0.3 && r.Kind == PauseKind.Comma);
        Assert.Contains(rows, r => r.Symbol == "、" && r.After == 0.3);
        Assert.Contains(rows, r => r.Symbol == PunctuationTable.Newline && r.After == 0.9);
        Assert.DoesNotContain(rows, r => r.Symbol == "。");
        // 写成用户表再读回来，生效
        var t = PunctuationTable.Parse(RulesTestData.Read("punctuation.tsv"), PunctuationTable.ToUserTsv(rows));
        Assert.Empty(t.Errors);
        t.TryGet("，", out var c); Assert.Equal(0.3, c.After);
        t.TryGet("。", out var p); Assert.Equal(0.45, p.After);
    }

    [Fact]
    public void Legacy_dict_converts_to_pinyin_rules()
    {
        string legacy = "\uFEFF# 注释\n银行 yin2 hang2\n六安 lù ān\n睡着 shui4 zhao2 # 行尾注释\n坏行\n字数 zi4\n绿色 lü4 se4\n";
        string tsv = SettingsMigration.LegacyDictToRules(legacy, out int n, out int skipped);
        Assert.Equal(4, n);
        Assert.Equal(2, skipped);
        var f = RuleFile.Parse(tsv, "用户读法词库.tsv", RuleFile.PriorityUser);
        Assert.Empty(f.Errors);
        Assert.Equal("lu4 an1", f.Rules.Single(r => r.Source == "六安").Target);
        var e = new RulesEngine(new[] { f });
        Assert.Equal("[yin2 hang2]", Segments.Debug(e.Rewrite("银行")));
    }

    [Fact]
    public void Run_writes_once_and_keeps_legacy_file()
    {
        Directory.CreateDirectory(_dir);
        string legacy = Path.Combine(_dir, "用户自定义词库.txt");
        File.WriteAllText(legacy, "银行 yin2 hang2\n");
        var data = Path.Combine(_dir, "data");
        var rep = SettingsMigration.Run(data, new PauseSettings { Period = 0.8 }, legacy, Builtin);
        Assert.True(rep.WrotePunct && rep.WroteRules);
        Assert.Equal(1, rep.PinyinRules);
        Assert.True(File.Exists(legacy));                       // 原文件保留
        Assert.True(File.Exists(Path.Combine(data, SettingsMigration.UserPunctFile)));

        // 用户之后在新界面改了文件：再次迁移不覆盖
        string rulesPath = Path.Combine(data, SettingsMigration.UserRulesFile);
        File.WriteAllText(rulesPath, "替换\ta\tb\n");
        var rep2 = SettingsMigration.Run(data, new PauseSettings { Period = 0.8 }, legacy, Builtin);
        Assert.False(rep2.WrotePunct || rep2.WroteRules);
        Assert.Equal("替换\ta\tb\n", File.ReadAllText(rulesPath));
    }

    [Fact]
    public void Run_without_anything_to_migrate_writes_nothing()
    {
        var rep = SettingsMigration.Run(_dir, new PauseSettings(), null, Builtin);
        Assert.False(rep.WrotePunct || rep.WroteRules);
        Assert.False(File.Exists(Path.Combine(_dir, SettingsMigration.UserPunctFile)));
    }
}

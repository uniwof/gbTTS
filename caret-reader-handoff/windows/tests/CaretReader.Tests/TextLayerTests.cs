using CaretReader.Core;
using CaretReader.Core.Settings;
using CaretReader.Core.Text;
using Xunit;

namespace CaretReader.Tests;

[Collection("TextLayerStatic")]   // TextLayer.For 有全局缓存，用到它的测试不能并行
public class TextLayerTests
{
    private static (string appDir, string settings) Setup()
    {
        // 程序目录 = 测试输出目录（里面有 rules/），设置放到临时目录
        string dir = Path.Combine(Path.GetTempPath(), "gbtts-tl-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return (AppContext.BaseDirectory, Path.Combine(dir, "settings.json"));
    }

    [Fact]
    public void Golden_text_becomes_clauses_with_new_pauses()
    {
        var (app, set) = Setup();
        string ud = TextLayerSettings.UserDataDir(set);
        Directory.CreateDirectory(ud);
        File.WriteAllText(Path.Combine(ud, SettingsMigration.UserRulesFile), "替换\tYYDS\t永远的神\t整词\n");
        var tl = TextLayer.For(app, set, new TextLayerSettings());
        var cl = tl.Split("## 第三章 结论\nYYDS！他把GDP讲清楚了。", firstChunk: false);
        Assert.Equal("第三章 结论", cl[0].Text);
        Assert.Equal(0.7, cl[0].PauseAfterSeconds);
        Assert.Equal("永远的神！", cl[1].Text);
        Assert.Contains(PinyinAnnotation.Wrap("GDP", "ji4 di4 pi1"), cl[2].Text);
        Assert.Equal("他把GDP讲清楚了。", PinyinAnnotation.Strip(cl[2].Text));
        Assert.DoesNotContain(cl, c => c.Text.Any(char.IsAsciiDigit) && !PinyinAnnotation.HasAny(c.Text));
    }

    [Fact]
    public void User_rules_file_change_reloads_and_pause_scale_applies()
    {
        var (app, set) = Setup();
        var s = new TextLayerSettings();
        string data = TextLayerSettings.UserDataDir(set);
        Directory.CreateDirectory(data);
        var a = TextLayer.For(app, set, s);
        Assert.Same(a, TextLayer.For(app, set, s));
        File.WriteAllText(Path.Combine(data, SettingsMigration.UserRulesFile), "替换\t宝宝\t宝贝\n");
        var b = TextLayer.For(app, set, s);
        Assert.NotSame(a, b);
        var cl = b.Split("宝宝，好", firstChunk: false, pauseScale: 2.0);
        Assert.Equal("宝贝，", cl[0].Text);
        Assert.Equal(0.30, cl[0].PauseAfterSeconds, 3);
    }
}

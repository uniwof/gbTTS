using CaretReader.Core;
using CaretReader.Core.Settings;
using CaretReader.Core.Text;
using Xunit;

namespace CaretReader.Tests;

public class TextLayerSettingsTests
{
    [Fact]
    public void Roundtrip_in_settings_json_and_lenient_bad_field()
    {
        string dir = Path.Combine(Path.GetTempPath(), "gbtts-set-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "settings.json");
        var s = new AppSettings();
        s.Text.EnglishPolicy = EnglishPolicy.Skip;
        s.Text.TitleDetection = false;
        s.Text.RulePacks["slang.tsv"] = false;
        s.Save(path);
        Assert.Contains("\"Skip\"", File.ReadAllText(path));
        var l = AppSettings.Load(path);
        Assert.Equal(EnglishPolicy.Skip, l.Text.EnglishPolicy);
        Assert.False(l.Text.TitleDetection);
        Assert.False(l.Text.PackEnabled("slang.tsv"));
        Assert.True(l.Text.PackEnabled("templates.tsv"));

        // 某一项写坏：只重置那一项
        File.WriteAllText(path, File.ReadAllText(path).Replace("\"Skip\"", "\"乱写\""));
        var l2 = AppSettings.Load(path);
        Assert.Equal(EnglishPolicy.Spell, l2.Text.EnglishPolicy);
        Assert.False(l2.Text.TitleDetection);
        Directory.Delete(dir, true);
    }

    [Fact]
    public void Old_settings_without_text_section_get_defaults()
    {
        string dir = Path.Combine(Path.GetTempPath(), "gbtts-set-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "settings.json");
        File.WriteAllText(path, "{ \"Speed\": 1.0 }");
        var l = AppSettings.Load(path);
        Assert.NotNull(l.Text);
        Assert.True(l.Text.MarkdownClean);
        Assert.Equal(Path.Combine(dir, "data"), TextLayerSettings.UserDataDir(path));
        Directory.Delete(dir, true);
    }
}

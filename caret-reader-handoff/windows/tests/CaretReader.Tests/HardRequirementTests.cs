using CaretReader.Core;
using CaretReader.Core.Settings;
using CaretReader.Core.Text;
using Xunit;

namespace CaretReader.Tests;

/// <summary>精简版第 2 节硬要求 10、11 的自动检查（其余几条要真 Windows，在人工清单里）。</summary>
[Collection("TextLayerStatic")]
public class HardRequirementTests
{
    static string Tmp() { var d = Path.Combine(Path.GetTempPath(), "gbtts-hr-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(d); return d; }

    // ---- 硬要求 11：规则文件写错一行，只跳过那一行，不崩，记日志 ----
    [Fact]
    public void Req11_bad_lines_in_user_files_are_skipped_logged_and_do_not_crash()
    {
        string app = Tmp(), data = Path.Combine(app, "data"), log = Path.Combine(app, "log.txt");
        Directory.CreateDirectory(data);
        CopyDir(RulesTestData.Dir, Path.Combine(app, "rules"));
        File.WriteAllText(Path.Combine(data, "用户读法词库.tsv"),
            "替换\tYYDS\t永远的神\t整词\n乱写的一行\n替换\t\t没有原文\n模板\t《》\tx\n读音\t银行\tyin2 hang2\n替换\tabc\n");
        File.WriteAllText(Path.Combine(data, "用户标点表.tsv"), "，\t0\t0.3\n这行不是表\n。\tabc\tdef\n；\t0\t0.9\n");
        Log.Init(log);
        string settings = Path.Combine(app, "settings.json");
        var layer = TextLayer.For(app, settings, new TextLayerSettings());   // 不能抛异常
        // 好的行照常生效
        Assert.Equal("永远的神", layer.Pipeline.Process("YYDS").Display("YYDS"));
        Assert.True(layer.Punctuation.TryGet("；", out var semi) && Math.Abs(semi.After - 0.9) < 1e-9);
        Assert.True(layer.Punctuation.TryGet("，", out var comma) && Math.Abs(comma.After - 0.3) < 1e-9);
        // 出错信息进了日志，但日志里没有行内文字
        string text = File.ReadAllText(log);
        Assert.Contains("规则跳过", text);
        Assert.DoesNotContain("没有原文", text);
        Assert.DoesNotContain("乱写的一行", text);
        Assert.True(layer.Pipeline.Errors.Count >= 4);
    }

    [Fact]
    public void Req11_garbage_user_file_does_not_crash()
    {
        string up = Path.Combine(Tmp(), "u.tsv");
        File.WriteAllBytes(up, new byte[] { 0xFF, 0xFE, 0x00, 0x09, 0x09, 0x0A, 0x09, 0xE6, 0x9B });
        var p = TextPipeline.Load(RulesTestData.Dir, up);
        Assert.Equal("你好", p.Process("你好").Display("你好"));
    }

    // ---- 硬要求 10：用户数据只放 data\；exe 旁不可写时改用 %AppData%\gbTTS\ ----
    [Fact]
    public void Req10_writable_app_dir_keeps_settings_and_data_beside_exe()
    {
        string app = Tmp(), root = Tmp();
        string sp = AppSettings.ResolvePath(app, root);
        Assert.Equal(Path.Combine(app, "settings.json"), sp);
        Assert.Equal(Path.Combine(app, "data"), TextLayerSettings.UserDataDir(sp));
    }

    [Fact]
    public void Req10_unwritable_app_dir_falls_back_to_appdata_gbTTS()
    {
        string missing = Path.Combine(Tmp(), "no-such-dir"), root = Tmp();   // 不存在的文件夹 = 写不进去
        string sp = AppSettings.ResolvePath(missing, root);
        Assert.Equal(Path.Combine(root, "gbTTS", "settings.json"), sp);
        Assert.Equal(Path.Combine(root, "gbTTS", "data"), TextLayerSettings.UserDataDir(sp));
    }

    [Fact]
    public void Req10_legacy_appdata_settings_still_used_so_upgrade_keeps_them()
    {
        string missing = Path.Combine(Tmp(), "no-such-dir"), root = Tmp();
        Directory.CreateDirectory(Path.Combine(root, "CaretReader"));
        File.WriteAllText(Path.Combine(root, "CaretReader", "settings.json"), "{}");
        Assert.Equal(Path.Combine(root, "CaretReader", "settings.json"), AppSettings.ResolvePath(missing, root));
    }

    [Fact]
    public void Req10_release_package_never_contains_data_folder()
    {
        // build.ps1 打包后必须删掉自检产生的 data\ 与 settings.json
        string? dir = AppContext.BaseDirectory;
        string? ps = null;
        for (int i = 0; i < 8 && dir != null; i++, dir = Path.GetDirectoryName(dir))
            if (File.Exists(Path.Combine(dir, "build.ps1"))) { ps = Path.Combine(dir, "build.ps1"); break; }
        if (ps == null) return;   // 测试在仓库外运行时跳过
        string t = File.ReadAllText(ps);
        Assert.Contains("Remove-Item \"$App\\data\"", t);
        Assert.Contains("settings.json", t);
    }

    static void CopyDir(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.GetFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)), true);
    }
}

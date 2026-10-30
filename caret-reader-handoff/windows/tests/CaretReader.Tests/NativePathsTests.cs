using CaretReader.Core;
using Xunit;

namespace CaretReader.Tests;

/// <summary>v0.3 中文路径崩溃的修复：传给 sherpa 的路径必须是 ASCII（相对路径或 8.3 短路径）。</summary>
public class NativePathsTests
{
    private const string B = "/u/Oland/Downloads/光标朗读_标准版_win10x64_v0.2/光标朗读";
    private static string R(string full, int acp, Func<string, string?> sp) => NativePaths.ForNative(full, B, true, acp, sp);

    [Theory]
    [InlineData(936)]
    [InlineData(1252)]
    public void Chinese_app_folder_becomes_relative_ascii(int acp)
    {
        string got = R(B + "/models/vits-melo-tts-zh_en/model.onnx", acp, _ => null);
        Assert.Equal(Path.Combine("models", "vits-melo-tts-zh_en", "model.onnx"), got);
        Assert.True(NativePaths.IsAscii(got));
    }

    [Fact]
    public void Chinese_model_folder_uses_short_path()
        => Assert.Equal("/u/OLAND~1/MODEL~1.ONN", R(B + "/models/我的音色/model.onnx", 936, _ => "/u/OLAND~1/MODEL~1.ONN"));

    [Fact]
    public void Chinese_model_folder_without_short_path_on_gbk_throws_friendly_error()
        => Assert.ThrowsAny<IOException>(() => R(B + "/models/我的音色/model.onnx", 936, _ => null));

    [Fact]
    public void Utf8_codepage_keeps_path()
    {
        string cm = B + "/models/我的音色/model.onnx";
        Assert.Equal(cm, R(cm, 65001, _ => null));
    }

    [Fact]
    public void Non_windows_keeps_path()
    {
        string m = B + "/models/x/model.onnx";
        Assert.Equal(m, NativePaths.ForNative(m, B, false, 936, _ => null));
    }
}

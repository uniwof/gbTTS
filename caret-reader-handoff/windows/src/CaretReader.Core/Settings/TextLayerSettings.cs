using CaretReader.Core.Text;

namespace CaretReader.Core.Settings;

/// <summary>
/// 文本层设置（settings.json 里的 "Text" 一节，文档 10.3“节奏/读法”页与第 12 节）。
/// 用户规则本身不在这里，而在 data/用户读法词库.tsv 和 data/用户标点表.tsv。
/// </summary>
public sealed class TextLayerSettings
{
    public bool MarkdownClean { get; set; } = true;
    public bool ReadCodeBlocks { get; set; }
    public bool TitleDetection { get; set; } = true;
    public EnglishPolicy EnglishPolicy { get; set; } = EnglishPolicy.Spell;
    public EmojiMode Emoji { get; set; } = EmojiMode.Skip;
    /// <summary>内置规则包开关（文件名 → 开/关），没写的默认开。</summary>
    public Dictionary<string, bool> RulePacks { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public TextPipelineOptions ToPipelineOptions(bool voiceSpeaksEnglish) => new()
    {
        MarkdownClean = MarkdownClean,
        ReadCodeBlocks = ReadCodeBlocks,
        EnglishPolicy = EnglishPolicy,
        VoiceSpeaksEnglish = voiceSpeaksEnglish,
        Emoji = Emoji,
        RulePacks = new Dictionary<string, bool>(RulePacks ?? new(), StringComparer.OrdinalIgnoreCase),
    };

    public bool PackEnabled(string file) => RulePacks == null || !RulePacks.TryGetValue(file, out bool on) || on;

    /// <summary>用户数据文件夹：settings.json 所在文件夹下的 data\（升级包里永远不带它）。</summary>
    public static string UserDataDir(string settingsPath) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(settingsPath)) ?? ".", "data");
}

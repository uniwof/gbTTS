using CaretReader.Core.Settings;

namespace CaretReader.Core.Text;

/// <summary>
/// 朗读用的文本层入口（P1 接线）：取到的文字 → TextPipeline（Markdown/读法/符号/数字/字母）
/// → 拼音注释 → TextSplitter（标点表停顿 + 标题识别）→ 小句。
/// 规则文件或设置变了会自动重新加载（按文件修改时间和设置内容判断），平时复用同一份，不重复读盘。
/// </summary>
public sealed class TextLayer
{
    public TextPipeline Pipeline { get; }
    public PunctuationTable Punctuation { get; }
    public TextLayerSettings Settings { get; }

    private static readonly object Gate = new();
    private static TextLayer? _cached;
    private static string? _cachedKey;

    private TextLayer(TextPipeline p, PunctuationTable t, TextLayerSettings s) { Pipeline = p; Punctuation = t; Settings = s; }

    public static string RulesDir(string appDir) => Path.Combine(appDir, "rules");

    /// <summary>取（必要时重新加载）当前文本层。appDir = 程序文件夹；settingsPath = settings.json 路径。</summary>
    public static TextLayer For(string appDir, string settingsPath, TextLayerSettings s, bool voiceSpeaksEnglish = false)
    {
        string rules = RulesDir(appDir), data = TextLayerSettings.UserDataDir(settingsPath);
        string userRules = Path.Combine(data, SettingsMigration.UserRulesFile);
        string userPunct = Path.Combine(data, SettingsMigration.UserPunctFile);
        string key = string.Join("|", System.Text.Json.JsonSerializer.Serialize(s), voiceSpeaksEnglish,
            Stamp(rules), Stamp(userRules), Stamp(userPunct));
        lock (Gate)
        {
            if (_cached != null && _cachedKey == key) return _cached;
            var p = TextPipeline.Load(rules, File.Exists(userRules) ? userRules : null, s.ToPipelineOptions(voiceSpeaksEnglish));
            var t = PunctuationTable.Load(Path.Combine(rules, "punctuation.tsv"), File.Exists(userPunct) ? userPunct : null);
            _cached = new TextLayer(p, t, s);
            _cachedKey = key;
            Log.Write($"文本层：规则 {p.Rules.RuleCount} 条，写错 {p.Errors.Count + t.Errors.Count} 行");
            return _cached;
        }
    }

    private static string Stamp(string path)
    {
        try
        {
            if (Directory.Exists(path))
                return string.Join(",", Directory.GetFiles(path, "*.tsv").Select(f => File.GetLastWriteTimeUtc(f).Ticks));
            return File.Exists(path) ? File.GetLastWriteTimeUtc(path).Ticks.ToString() : "-";
        }
        catch { return "?"; }
    }

    /// <summary>把一段文字变成小句（停顿已乘 pauseScale）。maxChars &gt; 0 时先截断原文。</summary>
    public List<Clause> Split(string? text, bool firstChunk, int maxChars = 0, double pauseScale = 1.0)
    {
        if (string.IsNullOrEmpty(text)) return new List<Clause>();
        if (maxChars > 0 && text.Length > maxChars)
        {
            int cut = maxChars;
            if (char.IsHighSurrogate(text[cut - 1])) cut--;
            text = text[..cut];
        }
        var r = Pipeline.Process(text);
        // Markdown 清理会改变行，用清理后的文字作为原文来取拼音片段的显示字
        string basis = Pipeline.Options.MarkdownClean ? Pipeline.Markdown.Clean(text.Replace("\r\n", "\n").Replace('\r', '\n')).Text : text.Replace("\r\n", "\n").Replace('\r', '\n');
        string encoded = PinyinAnnotation.Encode(r.Segments, basis);
        var opt = new SplitOptions { TitleDetection = Settings.TitleDetection, LineKinds = r.LineKinds };
        var clauses = TextSplitter.Split(encoded, Punctuation, opt, firstChunk);
        if (Math.Abs(pauseScale - 1.0) > 1e-9)
            for (int i = 0; i < clauses.Count; i++)
                clauses[i] = clauses[i] with { PauseAfterSeconds = clauses[i].PauseAfterSeconds * pauseScale };
        return clauses;
    }
}

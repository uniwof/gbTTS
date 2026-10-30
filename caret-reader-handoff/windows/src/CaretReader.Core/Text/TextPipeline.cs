namespace CaretReader.Core.Text;

/// <summary>文本层的开关（来自设置）。</summary>
public sealed class TextPipelineOptions
{
    public bool MarkdownClean { get; set; } = true;
    public bool ReadCodeBlocks { get; set; }
    public EnglishPolicy EnglishPolicy { get; set; } = EnglishPolicy.Spell;
    public bool VoiceSpeaksEnglish { get; set; }
    public EmojiMode Emoji { get; set; } = EmojiMode.Skip;
    /// <summary>内置规则包开关：文件名（slang.tsv、templates.tsv …）→ 是否启用。没写的默认启用。</summary>
    public Dictionary<string, bool> RulePacks { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>文本层处理结果：片段列表（Text / ForcedPinyin）+ Markdown 给出的每行类别（交给切句）。</summary>
public sealed record TextResult(List<Segment> Segments, IReadOnlyList<PauseKind>? LineKinds)
{
    /// <summary>“改写后的文字”：拼音片段显示成原文的字母/词，给“读法”页测试框用。</summary>
    public string Display(string original) => string.Concat(Segments.Select(s =>
        s.Kind == SegKind.ForcedPinyin ? "[" + s.Text + "]" : s.Text));
}

/// <summary>
/// 文本层总装（文档 5 的顺序）：Markdown 清理 → 读法改写（用户规则 + 内置包）→ 符号 → 数字 → 英文与字母。
/// 切句（TextSplitter）和多音字（PolyphoneFrontend）在它后面。所有规则都从 rules/ 和 data/ 的 TSV 读。
/// </summary>
public sealed class TextPipeline
{
    /// <summary>内置“读法”规则包（按文件名决定优先级）。markdown/symbols/letters/punctuation 不是读法规则。</summary>
    public static readonly string[] RulePackFiles = { "slang.tsv", "templates.tsv" };

    public MarkdownCleaner Markdown { get; }
    public RulesEngine Rules { get; }
    public SymbolNormalizer Symbols { get; }
    public NumberNormalizer Numbers { get; }
    public LetterSpeller Letters { get; }
    public TextPipelineOptions Options { get; }
    public List<RuleError> Errors { get; } = new();

    public TextPipeline(MarkdownCleaner md, RulesEngine rules, SymbolTable sym, LetterSpeller letters, TextPipelineOptions? opt = null)
    {
        Options = opt ?? new TextPipelineOptions();
        Markdown = md; Rules = rules; Letters = letters;
        Symbols = new SymbolNormalizer(sym) { Emoji = Options.Emoji };
        Numbers = new NumberNormalizer(sym);
        Markdown.ReadCode = Options.ReadCodeBlocks;
        Letters.Policy = Options.EnglishPolicy;
        Letters.VoiceSpeaksEnglish = Options.VoiceSpeaksEnglish;
        Errors.AddRange(md.Errors); Errors.AddRange(rules.Errors); Errors.AddRange(sym.Errors); Errors.AddRange(letters.Errors);
    }

    /// <summary>从 rules/（内置）和 data/用户读法词库.tsv（用户）加载。</summary>
    public static TextPipeline Load(string rulesDir, string? userRulesPath, TextPipelineOptions? opt = null)
    {
        opt ??= new TextPipelineOptions();
        var files = new List<RuleFile>();
        foreach (var f in RulePackFiles)
        {
            if (opt.RulePacks.TryGetValue(f, out bool on) && !on) continue;
            int pri = f.Equals("slang.tsv", StringComparison.OrdinalIgnoreCase) ? RuleFile.PrioritySlang : RuleFile.PriorityBuiltin;
            files.Add(RuleFile.Load(Path.Combine(rulesDir, f), pri));
        }
        if (userRulesPath != null) files.Add(RuleFile.Load(userRulesPath, RuleFile.PriorityUser));
        return new TextPipeline(
            MarkdownCleaner.Load(Path.Combine(rulesDir, "markdown.tsv")),
            new RulesEngine(files),
            SymbolTable.Load(Path.Combine(rulesDir, "symbols.tsv")),
            LetterSpeller.Load(Path.Combine(rulesDir, "letters.tsv")),
            opt);
    }

    public TextResult Process(string? text)
    {
        if (string.IsNullOrEmpty(text)) return new TextResult(new List<Segment>(), null);
        IReadOnlyList<PauseKind>? kinds = null;
        string src = text.Replace("\r\n", "\n").Replace('\r', '\n');
        if (Options.MarkdownClean)
        {
            var md = Markdown.Clean(src);
            src = md.Text;
            if (md.LineKinds.Any(k => k != PauseKind.None)) kinds = md.LineKinds;
        }
        var segs = Rules.Rewrite(src);
        segs = Segments.MapText(segs, t => Single(Numbers.Normalize(Symbols.Normalize(t))));
        segs = Segments.MapText(segs, t => Letters.Process(t));
        return new TextResult(Segments.MergeText(segs), kinds);
    }

    private static IReadOnlyList<Segment> Single(string t) => new[] { Segment.Plain(t, 0, t.Length) };
}

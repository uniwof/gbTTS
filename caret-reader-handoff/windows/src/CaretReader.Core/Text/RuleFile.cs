using System.Text.RegularExpressions;

namespace CaretReader.Core.Text;

/// <summary>规则类型（文档 5.3.1）：替换 / 模板 / 读音。</summary>
public enum RuleType { Replace, Template, Pinyin }

[Flags]
public enum RuleOptions { None = 0, WholeWord = 1, IgnoreCase = 2, Disabled = 4 }

/// <summary>一条读法规则。Priority 越大越优先（用户 300 &gt; 网络用语包 200 &gt; 其他内置包 100）。</summary>
public sealed record Rule(RuleType Type, string Source, string Target, RuleOptions Options, string Note,
    string FileName, int Line, int Priority)
{
    public bool Enabled => (Options & RuleOptions.Disabled) == 0;
    public bool WholeWord => (Options & RuleOptions.WholeWord) != 0;
    public bool IgnoreCase => (Options & RuleOptions.IgnoreCase) != 0;
}

/// <summary>写错的一行：界面上标红并显示 Reason。</summary>
public sealed record RuleError(string FileName, int Line, string Reason, string RawLine);

/// <summary>
/// 读法规则文件（UTF-8、Tab 分隔，每行 5 列：类型 原文 读作 选项 备注）。
/// 解析很宽松：一行写错就跳过、记入 Errors，不影响其它行。以 # 开头的行是注释。
/// </summary>
public sealed class RuleFile
{
    public const int PriorityUser = 300;
    public const int PrioritySlang = 200;
    public const int PriorityBuiltin = 100;

    /// <summary>模板 * 最多匹配多少字（文档 5.3.2 第 13 条）。</summary>
    public const int TemplateMaxInner = 50;

    private static readonly Regex PinyinSyllable = new(@"^[a-zü]+[1-5]$", RegexOptions.CultureInvariant);

    public string Name { get; }
    public int Priority { get; }
    public List<Rule> Rules { get; } = new();
    public List<RuleError> Errors { get; } = new();

    public RuleFile(string name, int priority) { Name = name; Priority = priority; }

    public static RuleFile Load(string path, int priority)
    {
        string name = Path.GetFileName(path);
        if (!File.Exists(path)) return new RuleFile(name, priority);
        string text;
        try { text = File.ReadAllText(path, System.Text.Encoding.UTF8); }
        catch (Exception ex)
        {
            var f = new RuleFile(name, priority);
            f.Errors.Add(new RuleError(name, 0, "文件读不出来：" + ex.GetType().Name, ""));
            Log.Write($"规则文件读不出来 {name}: {ex.GetType().Name}");
            return f;
        }
        var rf = Parse(text, name, priority);
        foreach (var e in rf.Errors) Log.Write($"规则写错 {name} 第{e.Line}行：{e.Reason}");
        return rf;
    }

    public static RuleFile Parse(string text, string name, int priority)
    {
        var rf = new RuleFile(name, priority);
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            string raw = lines[i];
            if (i == 0 && raw.Length > 0 && raw[0] == '\uFEFF') raw = raw[1..];
            if (raw.Trim().Length == 0 || raw.TrimStart().StartsWith('#')) continue;
            var r = ParseLine(raw, name, i + 1, priority, out string? err);
            if (r != null) rf.Rules.Add(r);
            else rf.Errors.Add(new RuleError(name, i + 1, err ?? "格式不对", raw));
        }
        return rf;
    }

    /// <summary>解析一行；写错时返回 null 并给出中文原因（供界面标红）。</summary>
    public static Rule? ParseLine(string raw, string fileName, int line, int priority, out string? error)
    {
        error = null;
        var cols = raw.Split('\t');
        if (cols.Length < 3) { error = "至少要有 3 列（类型、原文、读作），用 Tab 分开"; return null; }
        string typeStr = cols[0].Trim();
        RuleType type;
        switch (typeStr.ToLowerInvariant())
        {
            case "替换": case "replace": type = RuleType.Replace; break;
            case "模板": case "template": type = RuleType.Template; break;
            case "读音": case "pinyin": type = RuleType.Pinyin; break;
            default: error = $"类型“{typeStr}”不认识，只能是 替换 / 模板 / 读音"; return null;
        }
        string src = cols[1];
        string dst = cols[2];
        if (type != RuleType.Template) src = src.Trim();
        if (src.Length == 0) { error = "原文是空的"; return null; }
        if (src.Contains('\n')) { error = "原文不能跨行"; return null; }

        var opts = RuleOptions.None;
        if (cols.Length > 3)
        {
            foreach (var o0 in cols[3].Split(',', '，', '、'))
            {
                string o = o0.Trim();
                if (o.Length == 0) continue;
                switch (o.ToLowerInvariant())
                {
                    case "整词": case "wholeword": opts |= RuleOptions.WholeWord; break;
                    case "不分大小写": case "ignorecase": opts |= RuleOptions.IgnoreCase; break;
                    case "停用": case "disabled": opts |= RuleOptions.Disabled; break;
                    default: error = $"选项“{o}”不认识，只能是 整词 / 不分大小写 / 停用"; return null;
                }
            }
        }
        string note = cols.Length > 4 ? cols[4].Trim() : "";

        if (type == RuleType.Template)
        {
            int star = src.IndexOf('*');
            if (star <= 0 || star == src.Length - 1 || src.IndexOf('*', star + 1) >= 0)
            { error = "模板原文要写成“左定界符*右定界符”，例如《*》，只能有一个 *"; return null; }
            if (dst.Count(c => c == '*') > 1) { error = "模板读作里最多一个 *"; return null; }
        }
        else if (type == RuleType.Pinyin)
        {
            dst = dst.Trim();
            var syl = dst.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (syl.Length == 0) { error = "读音是空的"; return null; }
            foreach (var s in syl)
                if (!PinyinSyllable.IsMatch(s.Replace('v', 'ü')))
                { error = $"“{s}”不是带调拼音（例如 hang2，声调写 1～5）"; return null; }
            int hanCount = src.Count(c => !char.IsWhiteSpace(c));
            if (syl.Length != hanCount) { error = $"原文有 {hanCount} 个字，读音却有 {syl.Length} 个音节"; return null; }
            dst = string.Join(' ', syl);
        }
        return new Rule(type, src, dst, opts, note, fileName, line, priority);
    }
}

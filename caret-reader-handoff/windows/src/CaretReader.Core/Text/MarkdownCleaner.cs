using System.Text;
using System.Text.RegularExpressions;

namespace CaretReader.Core.Text;

/// <summary>Markdown 清理的结果：清理后的文字 + 每一行的类别（标题行 / 列表项 / 普通）。</summary>
public sealed record MarkdownResult(string Text, IReadOnlyList<PauseKind> LineKinds);

/// <summary>
/// Markdown 清理（文档 5.2）。规则全部来自 rules/markdown.tsv，代码里不写死具体符号。
/// 列：范围(块/行/行内) 正则 动作 参数 备注。
///  动作：删除、替换（参数可用 $1）、标题（删掉并把这一行记为标题）、列表（删掉并记为列表项）、
///        删行、表格（去掉首尾竖线，单元格用参数连接）、代码块（三个反引号之间，默认读参数“（代码略）”）。
/// 规则按文件顺序执行；“行”规则每行只用第一条命中的。
/// </summary>
public sealed class MarkdownCleaner
{
    private enum Scope { Block, Line, Inline }
    private enum Act { Delete, Replace, Title, List, DropLine, Table, CodeFence }
    private sealed record MdRule(Scope Scope, Regex Re, Act Act, string Arg);

    private readonly List<MdRule> _rules = new();
    public List<RuleError> Errors { get; } = new();

    /// <summary>true：代码块照读；false（默认）：整块读成参数里的文字。</summary>
    public bool ReadCode { get; set; }

    public static MarkdownCleaner Parse(string tsv)
    {
        var m = new MarkdownCleaner();
        var lines = tsv.Replace("\r\n", "\n").Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            string raw = lines[i];
            if (i == 0 && raw.Length > 0 && raw[0] == '\uFEFF') raw = raw[1..];
            if (raw.Length == 0 || raw.StartsWith('#')) continue;
            var c = raw.Split('\t');
            string Err(string why) { m.Errors.Add(new RuleError("markdown.tsv", i + 1, why, raw)); return why; }
            if (c.Length < 3) { Err("至少要有 3 列（范围、正则、动作）"); continue; }
            Scope sc;
            switch (c[0].Trim()) { case "块": sc = Scope.Block; break; case "行": sc = Scope.Line; break; case "行内": sc = Scope.Inline; break;
                default: Err($"范围“{c[0]}”不认识，只能是 块 / 行 / 行内"); continue; }
            Act act;
            switch (c[2].Trim()) {
                case "删除": act = Act.Delete; break; case "替换": act = Act.Replace; break;
                case "标题": act = Act.Title; break; case "列表": act = Act.List; break;
                case "删行": act = Act.DropLine; break; case "表格": act = Act.Table; break;
                case "代码块": act = Act.CodeFence; break;
                default: Err($"动作“{c[2]}”不认识"); continue; }
            Regex re;
            try { re = new Regex(c[1], RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200)); }
            catch (ArgumentException) { Err("正则写错了"); continue; }
            m._rules.Add(new MdRule(sc, re, act, c.Length > 3 ? c[3] : ""));
        }
        return m;
    }

    public static MarkdownCleaner Load(string path)
    {
        var m = Parse(File.Exists(path) ? File.ReadAllText(path) : "");
        foreach (var e in m.Errors) Log.Write($"Markdown 规则写错 第{e.Line}行：{e.Reason}");
        return m;
    }

    public MarkdownResult Clean(string text)
    {
        var outLines = new List<string>();
        var kinds = new List<PauseKind>();
        if (string.IsNullOrEmpty(text)) return new MarkdownResult("", kinds);
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var fence = _rules.FirstOrDefault(r => r.Scope == Scope.Block && r.Act == Act.CodeFence);
        bool inCode = false;
        foreach (var line0 in lines)
        {
            if (fence != null && fence.Re.IsMatch(line0))
            {
                if (!inCode && !ReadCode && fence.Arg.Length > 0) { outLines.Add(fence.Arg); kinds.Add(PauseKind.None); }
                inCode = !inCode;
                continue;
            }
            if (inCode)
            {
                if (ReadCode) { outLines.Add(line0); kinds.Add(PauseKind.None); }
                continue;
            }
            string line = line0;
            var kind = PauseKind.None;
            bool drop = false;
            foreach (var r in _rules.Where(r => r.Scope == Scope.Line))
            {
                var mt = r.Re.Match(line);
                if (!mt.Success) continue;
                switch (r.Act)
                {
                    case Act.DropLine: drop = true; break;
                    case Act.Title: line = line.Remove(mt.Index, mt.Length); kind = PauseKind.Title; break;
                    case Act.List: line = line.Remove(mt.Index, mt.Length); kind = PauseKind.ListItem; break;
                    case Act.Delete: line = line.Remove(mt.Index, mt.Length); break;
                    case Act.Replace: line = r.Re.Replace(line, r.Arg, 1); break;
                    case Act.Table:
                        string inner = mt.Groups.Count > 1 ? mt.Groups[1].Value : mt.Value;
                        line = string.Join(r.Arg, inner.Split('|').Select(x => x.Trim()).Where(x => x.Length > 0));
                        break;
                }
                // “删除”类前缀（例如引用 >）之后，还允许一条标题/列表规则继续命中
                if (r.Act != Act.Delete) break;
            }
            if (drop) continue;
            foreach (var r in _rules.Where(r => r.Scope == Scope.Inline))
            {
                try { line = r.Act == Act.Delete ? r.Re.Replace(line, "") : r.Re.Replace(line, r.Arg); }
                catch (RegexMatchTimeoutException) { }
            }
            outLines.Add(line);
            kinds.Add(kind);
        }
        return new MarkdownResult(string.Join('\n', outLines), kinds);
    }
}

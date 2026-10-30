namespace CaretReader.Core.Text;

/// <summary>文本流水线里流动的片段种类（文档 4.1）。</summary>
public enum SegKind { Text, ForcedPinyin, Pause }

/// <summary>停顿类别：来自标点表的“类别”列，用于统计和调试。</summary>
public enum PauseKind
{
    None, Comma, Period, Semicolon, Colon, Ellipsis, Dash,
    TitleOpen, TitleClose, QuoteOpen, QuoteClose, BracketOpen, BracketClose,
    Newline, Paragraph, Title, ListItem, InnerCut,
}

/// <summary>
/// 文本流水线里流动的单位（文档 4.1）。
/// Text：要读的文字；ForcedPinyin：带调拼音，如 "yin2 hang2"；Pause：纯停顿。
/// PauseBefore/PauseAfter 是秒数基础值（还没乘倍率）。SrcStart/SrcLen 是在原文里的位置。
/// </summary>
public sealed record Segment(
    SegKind Kind,
    string Text,
    double PauseBefore,
    double PauseAfter,
    int SrcStart,
    int SrcLen,
    PauseKind PauseClass)
{
    public static Segment Plain(string text, int srcStart, int srcLen) =>
        new(SegKind.Text, text, 0, 0, srcStart, srcLen, PauseKind.None);

    public static Segment Pinyin(string pinyin, int srcStart, int srcLen) =>
        new(SegKind.ForcedPinyin, pinyin, 0, 0, srcStart, srcLen, PauseKind.None);
}

/// <summary>片段列表的小工具。</summary>
public static class Segments
{
    /// <summary>把相邻的 Text 片段合并（位置取并集），其它片段原样保留。线性时间。</summary>
    public static List<Segment> MergeText(IEnumerable<Segment> segs)
    {
        var res = new List<Segment>();
        var sb = new System.Text.StringBuilder();
        Segment? run = null; int runEnd = 0;
        void Flush()
        {
            if (run != null) res.Add(run with { Text = sb.ToString(), SrcLen = runEnd - run.SrcStart });
            run = null; sb.Clear();
        }
        foreach (var s in segs)
        {
            if (s.Kind == SegKind.Text && s.Text.Length == 0 && s.SrcLen == 0) continue;
            if (s.Kind == SegKind.Text && run != null && run.PauseAfter == 0 && s.PauseBefore == 0)
            {
                sb.Append(s.Text);
                runEnd = Math.Max(runEnd, s.SrcStart + s.SrcLen);
                run = run with { PauseAfter = s.PauseAfter, PauseClass = s.PauseClass,
                    SrcStart = Math.Min(run.SrcStart, s.SrcStart) };
                continue;
            }
            Flush();
            if (s.Kind == SegKind.Text) { run = s; sb.Append(s.Text); runEnd = s.SrcStart + s.SrcLen; }
            else res.Add(s);
        }
        Flush();
        return res;
    }

    /// <summary>调试用：把片段列表压成一行字，拼音片段用 [..] 包住。</summary>
    public static string Debug(IEnumerable<Segment> segs) =>
        string.Concat(segs.Select(s => s.Kind switch
        {
            SegKind.ForcedPinyin => "[" + s.Text + "]",
            SegKind.Pause => "<" + s.PauseAfter.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + ">",
            _ => s.Text,
        }));

    /// <summary>
    /// 对每个 Text 片段调用 f（返回新的片段列表，位置相对于该片段文字），其它片段原样保留。
    /// 子片段的位置会按比例映射回原文（落在父片段的范围内）。
    /// </summary>
    public static List<Segment> MapText(IEnumerable<Segment> segs, Func<string, IReadOnlyList<Segment>> f)
    {
        var res = new List<Segment>();
        foreach (var s in segs)
        {
            if (s.Kind != SegKind.Text) { res.Add(s); continue; }
            var sub = f(s.Text);
            bool sameLen = s.Text.Length == s.SrcLen;
            for (int i = 0; i < sub.Count; i++)
            {
                var c = sub[i];
                int st, ln;
                if (sameLen) { st = s.SrcStart + c.SrcStart; ln = c.SrcLen; }
                else { st = s.SrcStart; ln = s.SrcLen; }
                st = Math.Clamp(st, s.SrcStart, s.SrcStart + s.SrcLen);
                ln = Math.Clamp(ln, 0, s.SrcStart + s.SrcLen - st);
                double before = i == 0 ? Math.Max(s.PauseBefore, c.PauseBefore) : c.PauseBefore;
                double after = i == sub.Count - 1 ? Math.Max(s.PauseAfter, c.PauseAfter) : c.PauseAfter;
                var pk = c.PauseClass == PauseKind.None && i == sub.Count - 1 ? s.PauseClass : c.PauseClass;
                res.Add(c with { SrcStart = st, SrcLen = ln, PauseBefore = before, PauseAfter = after, PauseClass = pk });
            }
        }
        return res;
    }
}

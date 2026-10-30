using System.Text;

namespace CaretReader.Core.Text;

/// <summary>
/// 让 ForcedPinyin 片段（字母读音、读音规则）穿过“按字符串工作”的切句和流水线：
/// 用 Unicode 行间注释符包起来：U+FFF9 原文 U+FFFA 拼音 U+FFFB。
///  - TextSplitter 不会在注释中间切开；
///  - PolyphoneFrontend.ToEngineText 把拼音换成音色包的 PUA 字符；
///  - 没有 PUA 表的音色用 Strip() 退回原文（例如 GDP 交给会读英文的模型）。
/// </summary>
public static class PinyinAnnotation
{
    public const char Anchor = '\uFFF9', Separator = '\uFFFA', Terminator = '\uFFFB';

    public static string Wrap(string display, string pinyin) => $"{Anchor}{display}{Separator}{pinyin}{Terminator}";

    public static bool HasAny(string s) => s.IndexOf(Anchor) >= 0;

    /// <summary>把片段列表变成一串文字：Text 原样，ForcedPinyin 包成注释（原文取自 original 的 SrcStart/SrcLen）。</summary>
    public static string Encode(IEnumerable<Segment> segs, string original)
    {
        var sb = new StringBuilder();
        foreach (var s in segs)
        {
            if (s.Kind != SegKind.ForcedPinyin) { sb.Append(s.Text); continue; }
            string disp = s.SrcStart >= 0 && s.SrcStart + s.SrcLen <= original.Length && s.SrcLen > 0
                ? original.Substring(s.SrcStart, s.SrcLen) : s.Text;
            disp = disp.Replace(Anchor, ' ').Replace(Separator, ' ').Replace(Terminator, ' ');
            sb.Append(Wrap(disp, s.Text));
        }
        return sb.ToString();
    }

    /// <summary>去掉注释，只留原文（给没有 PUA 表的音色、日志长度统计、界面显示）。</summary>
    public static string Strip(string s)
    {
        if (!HasAny(s)) return s;
        var sb = new StringBuilder(s.Length);
        Walk(s, (t, py) => sb.Append(t));
        return sb.ToString();
    }

    /// <summary>逐段回调：(文字, 拼音或 null)。不完整的注释按普通文字处理（去掉注释符）。</summary>
    public static void Walk(string s, Action<string, string?> onPiece)
    {
        int i = 0;
        while (i < s.Length)
        {
            int a = s.IndexOf(Anchor, i);
            if (a < 0) { onPiece(Clean(s[i..]), null); return; }
            if (a > i) onPiece(Clean(s[i..a]), null);
            int sep = s.IndexOf(Separator, a + 1);
            int end = sep < 0 ? -1 : s.IndexOf(Terminator, sep + 1);
            int nextA = s.IndexOf(Anchor, a + 1);
            if (sep < 0 || end < 0 || (nextA >= 0 && nextA < end))
            {
                // 注释被切断：只留文字部分
                int stop = nextA >= 0 ? nextA : s.Length;
                string frag = s[(a + 1)..stop];
                int cut = frag.IndexOf(Separator);
                onPiece(Clean(cut >= 0 ? frag[..cut] : frag), null);
                i = stop;
                continue;
            }
            onPiece(s[(a + 1)..sep], s[(sep + 1)..end]);
            i = end + 1;
        }
    }

    private static string Clean(string t) => t.Replace(Anchor.ToString(), "").Replace(Separator.ToString(), "").Replace(Terminator.ToString(), "");

    /// <summary>s[i] 是否落在一个注释里面（Anchor 之后、Terminator 之前，含两端）。</summary>
    public static bool[] Mask(string s)
    {
        var m = new bool[s.Length];
        if (!HasAny(s)) return m;
        int open = -1;
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == Anchor) open = i;
            else if (s[i] == Terminator && open >= 0) { for (int k = open; k <= i; k++) m[k] = true; open = -1; }
        }
        return m;
    }
}

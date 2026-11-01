using System.Text;

namespace CaretReader.Core.Text;

/// <summary>添加结果。Ok=false 时 Error 是给界面显示的中文原因；Replaced=true 表示覆盖了同一原文的旧规则。</summary>
public sealed record AddReadingResult(bool Ok, string? Error, bool Replaced);

/// <summary>
/// Ctrl+Alt+D「选中文字一键添加读法」的引擎部分（B3）：把「原文 → 读作」写入用户读法词库。
/// 界面（A 线）流程：取选中文字 → 弹小窗填「读作」→ 调 AddReading → 文本层自动按文件时间重新加载。
/// 只写词库文件，不记日志文字（硬要求 11 / 日志不记朗读文字）。同一原文再次添加会覆盖旧行（用户最新的为准）。
/// </summary>
public static class UserLexicon
{
    /// <summary>选中文字最长多少字可当作词条（超出多半是误选整段）。</summary>
    public const int MaxSourceChars = 30;

    /// <summary>整理选中文字：去首尾空白，换行/制表符不允许。返回 null 表示不能作为词条。</summary>
    public static string? NormalizeSource(string? selected)
    {
        if (selected == null) return null;
        string s = selected.Trim();
        if (s.Length == 0 || s.Length > MaxSourceChars) return null;
        if (s.IndexOfAny(new[] { '\n', '\r', '\t' }) >= 0) return null;
        return s;
    }

    /// <summary>英文/数字词条默认整词匹配（避免 cat 改了 category）；含汉字的不整词。</summary>
    public static bool DefaultWholeWord(string source)
    {
        foreach (char c in source)
            if (!(c < 128 && (char.IsLetterOrDigit(c) || c == '_' || c == '\'' || c == '-' || c == ' '))) return false;
        return true;
    }

    /// <summary>
    /// 写入一条读法。reading 是汉字读法（替换），或带调拼音如“yin2 hang2”（读音，字数须对得上）。
    /// 先用 RuleFile.ParseLine 校验，错就不写；写文件用临时文件再替换，中途失败不会弄坏旧词库。
    /// </summary>
    public static AddReadingResult AddReading(string userRulesPath, string? selected, string? reading, bool? wholeWord = null)
    {
        string? src = NormalizeSource(selected);
        if (src == null) return new(false, "选中的文字太长或带换行，请只选一个词", false);
        string dst = (reading ?? "").Trim();
        if (dst.Length == 0) return new(false, "读作不能为空", false);
        if (dst.IndexOfAny(new[] { '\n', '\r', '\t' }) >= 0) return new(false, "读作不能带换行", false);

        bool pinyin = LooksLikePinyin(dst);
        bool ww = pinyin ? false : (wholeWord ?? DefaultWholeWord(src));
        string line = string.Join('\t', pinyin ? "读音" : "替换", src, dst, ww ? "整词" : "", "Ctrl+Alt+D 添加");
        var rule = RuleFile.ParseLine(line, Path.GetFileName(userRulesPath), 0, RuleFile.PriorityUser, out string? err);
        if (rule == null) return new(false, err ?? "格式不对", false);

        try
        {
            string? dir = Path.GetDirectoryName(userRulesPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var kept = new List<string>();
            bool replaced = false;
            if (File.Exists(userRulesPath))
            {
                foreach (var raw in File.ReadAllText(userRulesPath, Encoding.UTF8).Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
                {
                    var cols = raw.Split('\t');
                    if (cols.Length >= 3 && !raw.TrimStart().StartsWith('#') && cols[1].Trim() == rule.Source
                        && RuleFile.ParseLine(raw, "", 0, 0, out _)?.Type == rule.Type)
                    { replaced = true; continue; }
                    kept.Add(raw);
                }
            }
            while (kept.Count > 0 && kept[^1].Length == 0) kept.RemoveAt(kept.Count - 1);
            kept.Add(line);
            string tmp = userRulesPath + ".tmp";
            File.WriteAllText(tmp, string.Join("\n", kept) + "\n", new UTF8Encoding(false));
            if (File.Exists(userRulesPath)) File.Replace(tmp, userRulesPath, null); else File.Move(tmp, userRulesPath);
            return new(true, null, replaced);
        }
        catch (Exception ex)
        {
            Log.Write("添加读法失败：" + ex.GetType().Name);
            return new(false, "词库文件写不进去（可能被占用）", false);
        }
    }

    private static bool LooksLikePinyin(string s)
    {
        var parts = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return false;
        foreach (var p in parts)
        {
            if (p.Length < 2 || p[^1] < '1' || p[^1] > '5') return false;
            for (int i = 0; i < p.Length - 1; i++)
                if (!(p[i] is >= 'a' and <= 'z' || p[i] is 'ü' or 'v')) return false;
        }
        return true;
    }

    /// <summary>试听用：用当前文本层处理一句，看「原文」会被读成什么（界面「试听」前显示/朗读用）。</summary>
    public static string PreviewText(TextPipeline pipeline, string source)
    {
        var r = pipeline.Process(source);
        return r.Display(source);
    }
}

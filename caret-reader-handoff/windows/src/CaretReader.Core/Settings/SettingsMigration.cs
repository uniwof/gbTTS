using System.Globalization;
using System.Text;
using CaretReader.Core.Text;

namespace CaretReader.Core.Settings;

/// <summary>一次迁移的结果（给日志和测试用；不含任何用户朗读文字）。</summary>
public sealed record MigrationReport(int PunctRows, int PinyinRules, int SkippedLines, bool WrotePunct, bool WroteRules);

/// <summary>
/// 从 v0.6 迁移到 v1.0 的文本层设置（文档 12）：
///  - 旧的五档停顿（逗号/句号/分号/冒号/换行）→ data/用户标点表.tsv。只迁移用户改过的值（和 v0.6 默认值不同的），
///    没改过的跟随新的内置标点表，这样新默认值（例如句号 0.45）能生效。
///  - 旧的 用户自定义词库.txt（“词 拼音”）→ data/用户读法词库.tsv 里的“读音”规则。原文件保留不删。
///  - 目标文件已经存在就不覆盖（迁移只做一次，不会冲掉用户在新界面里的改动）。
/// “语速倍数 → 目标字/分”属于 P2 节奏系统，不在这里。
/// </summary>
public static class SettingsMigration
{
    public const string UserPunctFile = "用户标点表.tsv";
    public const string UserRulesFile = "用户读法词库.tsv";

    /// <summary>v0.6 的默认停顿值（PauseSettings 的初始值）。</summary>
    public static readonly PauseSettings V06Defaults = new();

    /// <summary>旧的每一档停顿对应新标点表里的哪些符号。</summary>
    private static readonly (Func<PauseSettings, double> get, string[] symbols)[] Map =
    {
        (p => p.Comma, new[] { "，", ",", "、" }),
        (p => p.Period, new[] { "。", ".", "！", "!", "？", "?", "……", "…", "..." }),
        (p => p.Semicolon, new[] { "；", ";" }),
        (p => p.Colon, new[] { "：", ":" }),
        (p => p.Newline, new[] { PunctuationTable.Newline }),
    };

    /// <summary>把旧的五档停顿变成用户标点表的行（只含改过的档）。</summary>
    public static List<PunctEntry> PausesToPunctRows(PauseSettings old, PunctuationTable builtin)
    {
        var rows = new List<PunctEntry>();
        foreach (var (get, symbols) in Map)
        {
            double v = get(old);
            if (Math.Abs(v - get(V06Defaults)) < 1e-9) continue;   // 没改过：跟随新内置值
            foreach (var sym in symbols)
            {
                var b = builtin.Builtin(sym);
                if (b == null) continue;
                rows.Add(b with { After = Math.Clamp(v, 0, 5), Note = "从旧版设置迁移" });
            }
        }
        return rows;
    }

    /// <summary>把旧用户词库文本变成“读音”规则（TSV 文本）。识别不了的行跳过并计数。</summary>
    public static string LegacyDictToRules(string legacyText, out int count, out int skipped)
    {
        var sb = new StringBuilder();
        sb.Append("# 用户读法词库。列：类型\t原文\t读作\t选项\t备注。可以在“读法”页编辑，也可以用记事本改。\n");
        count = 0; skipped = 0;
        foreach (var raw0 in legacyText.Replace("\r\n", "\n").Split('\n'))
        {
            string line = raw0.TrimStart('\uFEFF');
            int hash = line.IndexOf('#');
            if (hash >= 0) line = line[..hash];
            line = line.Trim();
            if (line.Length == 0) continue;
            var parts = line.Split(new[] { ' ', '\t', '\u3000' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) { skipped++; continue; }
            var syl = new List<string>();
            bool ok = true;
            foreach (var s in parts.Skip(1))
            {
                var t = PolyphoneFrontend.ToTone3(s);
                if (t == null) { ok = false; break; }
                syl.Add(t);
            }
            string word = parts[0];
            string tsvLine = $"读音\t{word}\t{string.Join(' ', syl)}\t\t从旧用户词库迁移";
            if (!ok || RuleFile.ParseLine(tsvLine, UserRulesFile, 0, RuleFile.PriorityUser, out _) == null)
            { skipped++; continue; }
            sb.Append(tsvLine).Append('\n');
            count++;
        }
        return sb.ToString();
    }

    /// <summary>
    /// 执行迁移：dataDir = 用户数据文件夹；legacyDictPath = 旧的 用户自定义词库.txt（可为 null）。
    /// 已经存在的目标文件不覆盖。写文件用“临时文件 + 替换”。
    /// </summary>
    public static MigrationReport Run(string dataDir, PauseSettings? oldPauses, string? legacyDictPath, PunctuationTable builtin)
    {
        Directory.CreateDirectory(dataDir);
        int punctRows = 0, rules = 0, skipped = 0;
        bool wroteP = false, wroteR = false;

        string punctPath = Path.Combine(dataDir, UserPunctFile);
        if (oldPauses != null && !File.Exists(punctPath))
        {
            var rows = PausesToPunctRows(oldPauses, builtin);
            if (rows.Count > 0)
            {
                AtomicWrite(punctPath, PunctuationTable.ToUserTsv(rows));
                punctRows = rows.Count; wroteP = true;
            }
        }

        string rulesPath = Path.Combine(dataDir, UserRulesFile);
        if (legacyDictPath != null && File.Exists(legacyDictPath) && !File.Exists(rulesPath))
        {
            string tsv = LegacyDictToRules(File.ReadAllText(legacyDictPath, Encoding.UTF8), out rules, out skipped);
            AtomicWrite(rulesPath, tsv);
            wroteR = true;
        }
        var rep = new MigrationReport(punctRows, rules, skipped, wroteP, wroteR);
        if (wroteP || wroteR)
            Log.Write($"设置迁移：标点表 {punctRows} 行，读音规则 {rules} 条，跳过 {skipped} 行");
        return rep;
    }

    private static void AtomicWrite(string path, string content)
    {
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, content, new UTF8Encoding(false));
        if (File.Exists(path)) File.Replace(tmp, path, null);
        else File.Move(tmp, path);
    }
}

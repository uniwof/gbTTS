using System.Text;
using System.Text.RegularExpressions;

namespace CaretReader.Core.Text;

/// <summary>
/// 数字读法统一（文档 5.4）。处理完以后文本里不再有阿拉伯数字，所以音色包自带的 number/date/phone.fst
/// 即使还在也不会重复处理。单位、货币、比例、数学符号、量词都来自 rules/symbols.tsv。
/// 处理顺序（每一步产出的都是汉字，后面的步骤不会再碰）：
///  单位 → 货币 → 手机号 → 日期 → 年份 → 时间 → 版本号 → 百分数/千分数 → 数学符号 → 分数 → 范围 → 序数 → 千位分隔 → 其余数字。
/// “两/二”：数字 2 后面紧跟量词时读“两”（2个→两个），2000 这类首位读“两”（两千）；序数、号码、小数里读“二”。
/// </summary>
public sealed class NumberNormalizer
{
    private const string Digits = "零一二三四五六七八九";
    private static readonly string[] SmallUnits = { "", "十", "百", "千" };
    private static readonly string[] BigUnits = { "", "万" };

    private readonly SymbolTable _sym;
    private readonly Regex _unitRe, _currencyRe, _ratioRe, _mathRe;
    private readonly string[] _measuresByLen;

    private const string Num = @"\d+(?:\.\d+)?";
    private static readonly RegexOptions Opt = RegexOptions.CultureInvariant;
    private static readonly Regex PhoneRe = new(@"(?<![\d.])1[3-9]\d{9}(?![\d.])", Opt);
    private static readonly Regex DateRe = new(@"(?<![\d.])(\d{4})([-/.])(\d{1,2})\2(\d{1,2})(?![\d.])", Opt);
    private static readonly Regex YearRe = new(@"(?<!\d)(\d{4})(?=年)", Opt);
    private static readonly Regex TimeRe = new(@"(?<![\d.:：])(\d{1,2})[:：](\d{2})(?:[:：](\d{2}))?(?![\d:：])", Opt);
    private static readonly Regex VersionRe = new(@"(?<![A-Za-z0-9])([vV])\s?(\d+(?:\.\d+)+)(?![\d.])", Opt);
    private static readonly Regex FractionRe = new(@"(?<![\d/.])(\d+)/(\d+)(?![\d/.])", Opt);
    private static readonly Regex RangeRe = new(@"(?<![\d.])(" + Num + @")\s?[-~～]\s?(" + Num + @")(?![\d.])", Opt);
    private static readonly Regex OrdinalRe = new(@"第(\d+)", Opt);
    private static readonly Regex ThousandsRe = new(@"(?<![\d,])\d{1,3}(?:,\d{3})+(?:\.\d+)?(?![\d,])", Opt);
    private static readonly Regex NumberRe = new(@"(-)?(\d+)(?:\.(\d+))?", Opt);
    private static readonly Regex MinusBeforeEquals = new(@"(?<=\d)\s?-\s?(?=\d+(?:\.\d+)?\s?[=＝])", Opt);

    public NumberNormalizer(SymbolTable sym)
    {
        _sym = sym;
        static string Alt(IEnumerable<string> keys) =>
            string.Join("|", keys.OrderByDescending(k => k.Length).Select(Regex.Escape));
        _unitRe = sym.Units.Count == 0 ? new Regex("(?!)") :
            new Regex(@"(?<=\d)\s?(" + Alt(sym.Units.Keys) + @")(?![A-Za-z])", Opt);
        _currencyRe = sym.Currency.Count == 0 ? new Regex("(?!)") :
            new Regex(@"(" + Alt(sym.Currency.Keys) + @")\s?(\d[\d,]*(?:\.\d+)?)", Opt);
        _ratioRe = sym.Ratio.Count == 0 ? new Regex("(?!)") :
            new Regex(@"(-)?(" + Num + @")\s?(" + Alt(sym.Ratio.Keys) + ")", Opt);
        var mathKeys = sym.Math.Keys.ToList();
        _mathRe = mathKeys.Count == 0 ? new Regex("(?!)") :
            new Regex(@"(?<=\d)\s?(" + Alt(mathKeys) + @")\s?(?=-?\d)", Opt);
        _measuresByLen = sym.Measures.OrderByDescending(m => m.Length).ToArray();
    }

    public static NumberNormalizer FromRules(string rulesDir) =>
        new(SymbolTable.Load(Path.Combine(rulesDir, "symbols.tsv")));

    // ───────── 基础读法 ─────────

    /// <summary>逐位读：0→零，one→“一”或“幺”。</summary>
    public static string ReadDigits(string digits, bool yao = false)
    {
        var sb = new StringBuilder(digits.Length);
        foreach (char c in digits)
            if (c is >= '0' and <= '9') sb.Append(yao && c == '1' ? '幺' : Digits[c - '0']);
        return sb.ToString();
    }

    /// <summary>整数读法（1234→一千二百三十四，10→十，2000→两千 当 liang=true）。超过 16 位逐位读。</summary>
    public static string ReadInteger(string digits, bool liang = false)
    {
        digits = digits.TrimStart('0');
        if (digits.Length == 0) return "零";
        if (digits.Length > 16) return ReadDigits(digits);
        if (digits.Length > 8)
        {
            // 亿以上：高位部分递归读，再接“亿”（1234567890123 → 一万二千三百四十五亿……）
            string high = digits[..^8], low = digits[^8..];
            string r = ReadInteger(high, liang) + "亿";
            string lowT = low.TrimStart('0');
            if (lowT.Length == 0) return r;
            string lr = ReadInteger(lowT);
            if (lr.StartsWith('十')) lr = "一" + lr;
            return r + (low[0] == '0' ? "零" : "") + lr;
        }
        var groups = new List<string>();
        for (int end = digits.Length; end > 0; end -= 4)
            groups.Insert(0, digits[Math.Max(0, end - 4)..end]);
        var sb = new StringBuilder();
        bool pendingZero = false;
        for (int g = 0; g < groups.Count; g++)
        {
            string grp = groups[g];
            int big = groups.Count - 1 - g;
            if (int.Parse(grp) == 0) { pendingZero = sb.Length > 0; continue; }
            if (sb.Length > 0 && (pendingZero || grp.Length < 4 || grp[0] == '0')) sb.Append('零');
            pendingZero = false;
            sb.Append(ReadGroup(grp.PadLeft(4, '0'), sb.Length == 0));
            sb.Append(BigUnits[big]);
        }
        string s = sb.ToString();
        if (s.StartsWith("一十")) s = s[1..];               // 10~19 读“十几”
        if (liang && s.Length > 1 && s[0] == '二' && "百千万亿".Contains(s[1])) s = "两" + s[1..];
        return s;
    }

    private static string ReadGroup(string four, bool leading)
    {
        var sb = new StringBuilder();
        bool zero = false;
        for (int i = 0; i < 4; i++)
        {
            int d = four[i] - '0';
            if (d == 0) { zero = sb.Length > 0 || !leading; continue; }
            if (zero && sb.Length > 0) sb.Append('零');
            zero = false;
            sb.Append(Digits[d]).Append(SmallUnits[3 - i]);
        }
        return sb.ToString();
    }

    /// <summary>小数（-3.14→负三点一四）。intPart 可带前导 0 的情况按整数读。</summary>
    public static string ReadDecimal(string intPart, string? frac, bool negative = false, bool liang = false)
    {
        string s = ReadInteger(intPart, liang && string.IsNullOrEmpty(frac));
        if (!string.IsNullOrEmpty(frac)) s += "点" + ReadDigits(frac);
        return negative ? "负" + s : s;
    }

    private static string ReadNum(string num)
    {
        int dot = num.IndexOf('.');
        return dot < 0 ? ReadInteger(num) : ReadDecimal(num[..dot], num[(dot + 1)..]);
    }

    private bool FollowedByMeasure(string s, int pos)
    {
        foreach (var m in _measuresByLen)
            if (string.CompareOrdinal(s, pos, m, 0, m.Length) == 0) return true;
        return false;
    }

    // ───────── 主流程 ─────────

    public string Normalize(string text)
    {
        if (string.IsNullOrEmpty(text) || !text.Any(char.IsAsciiDigit)) return text ?? "";
        string s = text;

        // 单位：5km → 5公里（数字留给后面读）
        s = _unitRe.Replace(s, m => _sym.Units[m.Groups[1].Value]);
        // 货币：￥100 → 一百元
        s = _currencyRe.Replace(s, m => ReadNum(m.Groups[2].Value.Replace(",", "")) + _sym.Currency[m.Groups[1].Value]);
        // 手机号：逐位，1 读“幺”
        s = PhoneRe.Replace(s, m => ReadDigits(m.Value, yao: true));
        // 日期：2026-10-30 → 二零二六年十月三十日
        s = DateRe.Replace(s, m =>
        {
            int mo = int.Parse(m.Groups[3].Value), d = int.Parse(m.Groups[4].Value);
            if (mo is < 1 or > 12 || d is < 1 or > 31) return m.Value;
            return ReadDigits(m.Groups[1].Value) + "年" + ReadInteger(m.Groups[3].Value) + "月" + ReadInteger(m.Groups[4].Value) + "日";
        });
        // 年份：2026年 → 二零二六年
        s = YearRe.Replace(s, m => ReadDigits(m.Groups[1].Value));
        // 时间：10:30 → 十点三十分；10:05 → 十点零五分
        s = TimeRe.Replace(s, m =>
        {
            int h = int.Parse(m.Groups[1].Value), mi = int.Parse(m.Groups[2].Value);
            int se = m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : 0;
            if (h > 24 || mi > 59 || se > 59) return m.Value;
            string r = (h == 2 ? "两" : ReadInteger(m.Groups[1].Value)) + "点";
            r += mi == 0 && !m.Groups[3].Success ? "整" : (mi < 10 ? "零" : "") + ReadInteger(m.Groups[2].Value) + "分";
            if (se > 0) r += (se < 10 ? "零" : "") + ReadInteger(m.Groups[3].Value) + "秒";
            return r;
        });
        // 版本号：v1.2.3 → V一点二点三
        s = VersionRe.Replace(s, m => "V" + string.Join("点", m.Groups[2].Value.Split('.').Select(p => ReadInteger(p))));
        // 百分数 / 千分数
        s = _ratioRe.Replace(s, m => (m.Groups[1].Success ? "负" : "") + _sym.Ratio[m.Groups[3].Value] + ReadNum(m.Groups[2].Value));
        // 数学符号（两边是数字）：1+1=2 → 1加1等于2；5-3=2 里的 - 读“减”
        if (_sym.Math.ContainsKey("−"))
            s = MinusBeforeEquals.Replace(s, "−");
        s = _mathRe.Replace(s, m => _sym.Math[m.Groups[1].Value]);
        // 分数：3/4 → 四分之三
        s = FractionRe.Replace(s, m => ReadInteger(m.Groups[2].Value) + "分之" + ReadInteger(m.Groups[1].Value));
        // 范围：3-5、3~5 → 三到五
        s = RangeRe.Replace(s, m =>
        {
            string b = m.Groups[2].Value;
            bool liang = b.IndexOf('.') < 0 && FollowedByMeasure(s, m.Index + m.Length);
            return ReadNum(m.Groups[1].Value) + "到" + (liang ? Liang(ReadNum(b)) : ReadNum(b));
        });
        // 序数：第2章 → 第二章
        s = OrdinalRe.Replace(s, m => "第" + ReadInteger(m.Groups[1].Value));
        // 千位分隔：1,000,000 → 一百万
        s = ThousandsRe.Replace(s, m => ReadNum(m.Value.Replace(",", "")));
        // 其余数字
        var sb = new StringBuilder();
        int last = 0;
        foreach (Match m in NumberRe.Matches(s))
        {
            sb.Append(s, last, m.Index - last);
            last = m.Index + m.Length;
            bool neg = m.Groups[1].Success && (m.Index == 0 || !char.IsLetterOrDigit(s[m.Index - 1]) || IsCjk(s[m.Index - 1]));
            if (m.Groups[1].Success && !neg) sb.Append('-');
            string ip = m.Groups[2].Value;
            string? fp = m.Groups[3].Success ? m.Groups[3].Value : null;
            if (fp == null && ip.Length > 1 && ip[0] == '0') { sb.Append((neg ? "负" : "") + ReadDigits(ip)); continue; }
            bool measure = fp == null && FollowedByMeasure(s, last);
            string r = ReadDecimal(ip, fp, neg, liang: fp == null);
            if (measure && ip == "2") r = neg ? "负两" : "两";
            if (!measure && fp == null && r.StartsWith("两") && ip.Length < 4) r = "二" + r[1..]; // 200 → 二百（不跟量词时）
            sb.Append(r);
        }
        sb.Append(s, last, s.Length - last);
        return sb.ToString();
    }

    private static string Liang(string s) => s == "二" ? "两" : s;
    private static bool IsCjk(char c) => c >= 0x2E80;
}

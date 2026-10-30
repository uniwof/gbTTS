using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CaretReader.Core;

/// <summary>
/// v0.4：音色包里 voice.json 的 "files" 部分（模型需要哪些文件，都是相对音色包文件夹的文件名）。
/// </summary>
public sealed class VoiceFiles
{
    /// <summary>主模型。vits / melo / piper = 模型文件；matcha = 声学模型。</summary>
    public string Model { get; set; } = "model.onnx";
    public string Tokens { get; set; } = "tokens.txt";
    /// <summary>发音词典（可省略）。</summary>
    public string? Lexicon { get; set; }
    /// <summary>jieba 分词词典文件夹（可省略）。</summary>
    public string? DictDir { get; set; }
    /// <summary>espeak-ng-data 文件夹（部分 piper 模型需要，可省略）。</summary>
    public string? DataDir { get; set; }
    /// <summary>仅 matcha：声码器文件（必须放在音色包里）。</summary>
    public string? Vocoder { get; set; }
    /// <summary>文本规范化规则（日期、电话、数字……），按顺序生效。缺失的会被跳过并记日志。</summary>
    public List<string>? RuleFsts { get; set; }
    /// <summary>v0.5：多音字前端用的“拼音 → 私用区字符”对照表（对应 lexicon 里追加的条目，可省略；省略 = 按旧方式朗读）。</summary>
    public string? PuaMap { get; set; }
}

/// <summary>
/// v0.4：一个“音色包” = voices 文件夹下的一个子文件夹，里面有：
///   voice.json   —— 显示名、引擎类型、所需文件、说话人编号 sid、音量增益、默认语速、一句话简介
///   模型文件     —— 每个包自带完整模型（多个 sid 共用一个模型时也各带一份：删文件夹就能移除，互不影响）
///   sample.mp3   —— 试听（预先做好的，不实时合成）
///   icon.png     —— 预留（以后放动物形象），可以没有
///
/// 音色的 id = 文件夹名（不区分大小写），settings.json 用它记住上次选的音色。
/// </summary>
public sealed class VoicePack
{
    public const string ManifestName = "voice.json";
    public static readonly string[] Engines = { "vits", "melo", "piper", "matcha" };

    // ---------- voice.json 里的字段 ----------
    public int FormatVersion { get; set; } = 1;
    /// <summary>建议的文件夹名（英文），导入时用它命名；可省略。</summary>
    [JsonPropertyName("id")] public string? SuggestedId { get; set; }
    public string Name { get; set; } = "";
    public string Engine { get; set; } = "vits";
    public VoiceFiles? Files { get; set; }
    public int Sid { get; set; }
    /// <summary>音量增益（1.0 = 原样）。有的模型天生声音小，用它拉齐。范围 0.1 ~ 5。</summary>
    public double Gain { get; set; } = 1.0;
    /// <summary>这个音色的默认语速（1.0 = 模型原速）；实际语速 = 它 × 设置里的语速。范围 0.5 ~ 2。</summary>
    public double Speed { get; set; } = 1.0;
    /// <summary>一句话简介（下拉框里鼠标停在音色上时显示）。</summary>
    public string? Description { get; set; }
    /// <summary>排序（小的在前），省略时 100。</summary>
    public int Order { get; set; } = 100;
    public string Sample { get; set; } = "sample.mp3";
    public string Icon { get; set; } = "icon.png";
    /// <summary>大致的加载时间（秒），只用于让进度条走得像样。</summary>
    public double ApproxLoadSeconds { get; set; } = 3;
    /// <summary>主模型文件的字节数（可省略）。填了就核对：大小不对 = 没下载 / 解压完整，拒绝加载（不完整的模型可能让原生库直接崩溃）。</summary>
    public long ModelBytes { get; set; }

    /// <summary>第 2 轮：voice.json 里可选的 "pacing"（cpmAtSpeed1 / trimDb / defaultPauseScale）。
    /// 用 JsonElement 接收：写错了只忽略这一项，不影响音色包加载。</summary>
    [JsonPropertyName("pacing")] public JsonElement? PacingRaw { get; set; }
    [JsonIgnore] public global::CaretReader.Core.Pacing.VoicePacing? Pacing { get; private set; }

    // ---------- 运行时信息 ----------
    /// <summary>音色包文件夹的完整路径。</summary>
    [JsonIgnore] public string Dir { get; private set; } = "";
    /// <summary>音色 id = 文件夹名。</summary>
    [JsonIgnore] public string Id => Path.GetFileName(Dir);
    [JsonIgnore] public string? SamplePath { get; private set; }
    [JsonIgnore] public string? IconPath { get; private set; }
    [JsonIgnore] public List<string> Warnings { get; } = new();

    public override string ToString() => Name;

    /// <summary>sherpa-onnx 里用哪种配置：matcha，或者 vits（melo / piper 也是 vits 结构）。</summary>
    [JsonIgnore] public bool IsMatcha => string.Equals(Engine, "matcha", StringComparison.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    /// <summary>
    /// 读取并检查一个音色包文件夹。成功返回音色包；失败时 error = 中文原因（给日志 / 用户看），不抛异常。
    /// </summary>
    public static VoicePack? TryLoad(string dir, out string? error)
    {
        error = null;
        try
        {
            dir = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string manifest = Path.Combine(dir, ManifestName);
            if (!File.Exists(manifest)) { error = "缺少 voice.json"; return null; }
            var fi = new FileInfo(manifest);
            if (fi.Length > 256 * 1024) { error = "voice.json 太大（不像音色说明文件）"; return null; }

            VoicePack? p;
            try { p = JsonSerializer.Deserialize<VoicePack>(File.ReadAllText(manifest), JsonOpts); }
            catch (Exception ex) { error = "voice.json 格式有误：" + ex.Message; return null; }
            if (p == null) { error = "voice.json 是空的"; return null; }

            p.Dir = dir;
            p.Engine = (p.Engine ?? "").Trim().ToLowerInvariant();
            if (Array.IndexOf(Engines, p.Engine) < 0) { error = $"不支持的引擎类型「{p.Engine}」（只支持 vits / melo / piper / matcha）"; return null; }
            if (p.Files == null || string.IsNullOrWhiteSpace(p.Files.Model) || string.IsNullOrWhiteSpace(p.Files.Tokens))
            { error = "voice.json 里缺少 files.model 或 files.tokens"; return null; }
            if (string.IsNullOrWhiteSpace(p.Name)) p.Name = p.Id;
            if (p.Name.Length > 40) p.Name = p.Name.Substring(0, 40);
            if (p.Sid < 0) { error = "sid 不能是负数"; return null; }
            if (p.IsMatcha && string.IsNullOrWhiteSpace(p.Files.Vocoder)) { error = "matcha 音色缺少 files.vocoder（声码器）"; return null; }
            if (double.IsNaN(p.Gain) || p.Gain <= 0) p.Gain = 1; else p.Gain = Math.Clamp(p.Gain, 0.1, 5);
            if (double.IsNaN(p.Speed) || p.Speed <= 0) p.Speed = 1; else p.Speed = Math.Clamp(p.Speed, 0.5, 2);
            p.ApproxLoadSeconds = double.IsNaN(p.ApproxLoadSeconds) ? 3 : Math.Clamp(p.ApproxLoadSeconds, 0.5, 30);

            // 所需文件都必须在音色包文件夹“里面”（不允许 ../ 指到外面去）
            var missing = new List<string>();
            void NeedFile(string? rel, bool required)
            {
                if (string.IsNullOrWhiteSpace(rel)) { if (required) missing.Add("(未填写)"); return; }
                string? f = p.Inside(rel);
                if (f == null) { missing.Add(rel + "（必须在音色包文件夹里）"); return; }
                if (!File.Exists(f)) missing.Add(rel);
            }
            void NeedDir(string? rel)
            {
                if (string.IsNullOrWhiteSpace(rel)) return;
                string? f = p.Inside(rel);
                if (f == null || !Directory.Exists(f)) missing.Add(rel + Path.DirectorySeparatorChar);
            }
            NeedFile(p.Files.Model, true);
            NeedFile(p.Files.Tokens, true);
            NeedFile(p.Files.Lexicon, false);
            NeedDir(p.Files.DictDir);
            NeedDir(p.Files.DataDir);
            if (p.IsMatcha) NeedFile(p.Files.Vocoder, true);
            if (missing.Count > 0) { error = "缺少文件：" + string.Join("，", missing); return null; }

            // 规则文件缺了不致命：跳过并提醒（只是日期 / 数字读法差一些）
            var fsts = new List<string>();
            foreach (var f in p.Files.RuleFsts ?? new())
            {
                if (string.IsNullOrWhiteSpace(f)) continue;
                string? full = p.Inside(f);
                if (full != null && File.Exists(full)) fsts.Add(f);
                else p.Warnings.Add("规则文件缺失，已跳过：" + f);
            }
            p.Files.RuleFsts = fsts;

            var model = new FileInfo(p.Inside(p.Files.Model)!);
            if (model.Length < 1024) { error = "模型文件太小，可能没有下载完整：" + p.Files.Model; return null; }
            if (p.ModelBytes > 0 && model.Length != p.ModelBytes)
            { error = $"模型文件不完整（应为 {p.ModelBytes} 字节，实际 {model.Length} 字节），请重新下载或解压"; return null; }
            // 审查采纳：别的程序（例如资源管理器正在复制）还在写这个文件 → 先跳过，复制完再点一次下拉框就会出现
            try { using var probe = new FileStream(model.FullName, FileMode.Open, FileAccess.Read, FileShare.Read, 1); }
            catch (IOException) { error = "模型文件正在被写入（可能还在复制中），请等复制完成"; return null; }
            catch (UnauthorizedAccessException) { error = "没有权限读取模型文件"; return null; }

            string? s = string.IsNullOrWhiteSpace(p.Sample) ? null : p.Inside(p.Sample);
            p.SamplePath = s != null && File.Exists(s) ? s : null;
            string? ic = string.IsNullOrWhiteSpace(p.Icon) ? null : p.Inside(p.Icon);
            p.IconPath = ic != null && File.Exists(ic) ? ic : null;
            p.Pacing = ReadPacing(p.PacingRaw, p.Warnings);
            if (p.Pacing != null) global::CaretReader.Core.Pacing.Pacer.PackPacing[p.Id] = p.Pacing;
            else global::CaretReader.Core.Pacing.Pacer.PackPacing.TryRemove(p.Id, out _);
            return p;
        }
        catch (Exception ex)
        {
            error = "读取失败：" + ex.Message;
            return null;
        }
    }

    private static global::CaretReader.Core.Pacing.VoicePacing? ReadPacing(JsonElement? raw, List<string> warnings)
    {
        if (raw is not JsonElement e || e.ValueKind != JsonValueKind.Object) return null;
        var v = new global::CaretReader.Core.Pacing.VoicePacing();
        bool any = false;
        if (e.TryGetProperty("cpmAtSpeed1", out var c) && c.ValueKind == JsonValueKind.Number && c.TryGetDouble(out var cpm))
        {
            if (cpm >= 60 && cpm <= 900) { v.CpmAtSpeed1 = cpm; any = true; }
            else warnings.Add("voice.json 的 pacing.cpmAtSpeed1 不合理，已忽略（改为本机测）");
        }
        if (e.TryGetProperty("trimDb", out var t) && t.ValueKind == JsonValueKind.Number && t.TryGetDouble(out var db) && db < -5 && db > -80)
        { v.TrimDb = db; any = true; }
        if (e.TryGetProperty("defaultPauseScale", out var ps) && ps.ValueKind == JsonValueKind.Number && ps.TryGetDouble(out var sc) && sc >= 0.5 && sc <= 2)
        { v.DefaultPauseScale = sc; any = true; }
        return any ? v : null;
    }

    /// <summary>音色包里的相对路径 → 完整路径；指到包外面时返回 null。</summary>
    public string? Inside(string rel)
    {
        if (string.IsNullOrWhiteSpace(rel) || Path.IsPathRooted(rel)) return null;
        string full = Path.GetFullPath(Path.Combine(Dir, rel));
        string root = Dir + Path.DirectorySeparatorChar;
        return full.StartsWith(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) ? full : null;
    }

    /// <summary>所有引用到的文件 / 文件夹的完整路径（不含 voice.json、sample、icon）。</summary>
    public IEnumerable<string> ModelPaths()
    {
        var f = Files!;
        foreach (var rel in new[] { f.Model, f.Tokens, f.Lexicon, f.DictDir, f.DataDir, f.Vocoder, f.PuaMap }.Concat(f.RuleFsts ?? new()))
            if (!string.IsNullOrWhiteSpace(rel) && Inside(rel!) is string full) yield return full;
    }

    private string? _modelKey;
    /// <summary>
    /// “模型指纹”：引擎 + 模型 / 词典的大小 + 模型开头、中间、结尾各 1MB 的哈希
    /// （审查采纳：只看开头 64KB 时，同一结构的两个微调模型大小相同、开头也可能相同，会被误认成同一个）。
    /// 两个音色包带的是同一个模型时指纹相同 → 切换时不用重新加载（瞬间完成），和以前同一模型换 sid 一样。
    /// </summary>
    public string ModelKey
    {
        get
        {
            if (_modelKey != null) return _modelKey;
            var f = Files!;
            string Size(string? rel) => rel != null && Inside(rel) is string p && File.Exists(p) ? new FileInfo(p).Length.ToString() : "-";
            string head = "-";
            try
            {
                using var fs = File.OpenRead(Inside(f.Model)!);
                using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                const int chunk = 1 << 20;
                var buf = new byte[chunk];
                long len = fs.Length;
                foreach (long pos in new[] { 0L, Math.Max(0, len / 2 - chunk / 2), Math.Max(0, len - chunk) }.Distinct())
                {
                    fs.Position = pos;
                    int n, got = 0;
                    while (got < chunk && (n = fs.Read(buf, got, chunk - got)) > 0) got += n;
                    sha.AppendData(buf, 0, got);
                }
                head = Convert.ToHexString(sha.GetHashAndReset()).Substring(0, 24);
            }
            catch { head = Dir; }   // 读不了就当成独一无二
            string vocoder = IsMatcha ? Size(f.Vocoder) : "";
            return _modelKey = $"{(IsMatcha ? "matcha" : "vits")}|{Size(f.Model)}|{head}|{Size(f.Tokens)}|{Size(f.Lexicon)}|{vocoder}|{string.Join(",", f.RuleFsts ?? new())}";
        }
    }
}

/// <summary>
/// v0.4：音色库 = 程序文件夹下的 voices 文件夹（界面上叫「音色」）。
/// 启动时（以及每次打开下拉框时）扫描：每个子文件夹是一个音色包，有效的进下拉框，坏的跳过并写日志，不崩溃。
/// </summary>
public static class VoiceLibrary
{
    /// <summary>voices 文件夹名（英文，避开 sherpa-onnx 的中文路径问题）。</summary>
    public const string FolderName = "voices";

    public sealed record ScanResult(List<VoicePack> Packs, List<string> Problems);

    public static string VoicesDir(string appDir) => Path.Combine(appDir, FolderName);

    /// <summary>
    /// 扫描 voices 文件夹。
    /// tidy = true 时顺便整理（只在启动时做）：
    ///   * 包在两层文件夹里（直接把解压出来的外层文件夹拖进来了）→ 把里面那层挪到 voices 下；
    ///   * 文件夹名有中文 → 改成英文名（原生库对英文路径最稳）。
    /// 整理失败也没关系，照样尝试加载。
    /// </summary>
    public static ScanResult Scan(string appDir, bool tidy)
    {
        var packs = new List<VoicePack>();
        var problems = new List<string>();
        string root = VoicesDir(appDir);
        if (!Directory.Exists(root))
        {
            problems.Add("找不到音色文件夹 voices");
            return new ScanResult(packs, problems);
        }

        string[] dirs;
        try { dirs = Directory.GetDirectories(root); }
        catch (Exception ex) { problems.Add("无法读取音色文件夹：" + ex.Message); return new ScanResult(packs, problems); }

        foreach (var d0 in dirs.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            string name = Path.GetFileName(d0);
            if (name.StartsWith('.') || name.StartsWith('_')) continue;   // 导入时的临时文件夹等
            string d = d0;
            try
            {
                if (!File.Exists(Path.Combine(d, VoicePack.ManifestName)))
                {
                    // 多套了一层文件夹？看看下一层
                    var inner = SafeDirs(d).Where(x => File.Exists(Path.Combine(x, VoicePack.ManifestName))).ToList();
                    if (inner.Count == 0) { problems.Add($"「{name}」不是音色包（里面没有 voice.json），已跳过"); continue; }
                    foreach (var i in inner)
                    {
                        string target = i;
                        if (tidy) target = TryMove(i, root, Path.GetFileName(i)) ?? i;
                        AddPack(target, packs, problems, tidy, root);
                    }
                    if (tidy) TryDeleteIfEmpty(d);
                    continue;
                }
                AddPack(d, packs, problems, tidy, root);
            }
            catch (Exception ex) { problems.Add($"「{name}」读取失败：{ex.Message}"); }
        }

        packs.Sort((a, b) => a.Order != b.Order ? a.Order.CompareTo(b.Order) : string.Compare(a.Name, b.Name, StringComparison.CurrentCulture));
        return new ScanResult(packs, problems);
    }

    private static void AddPack(string dir, List<VoicePack> packs, List<string> problems, bool tidy, string root)
    {
        if (tidy && !NativePaths.IsAscii(Path.GetFileName(dir)))
        {
            // 先读一下 voice.json 里建议的英文名
            var probe = VoicePack.TryLoad(dir, out _);
            string ascii = SafeFolderName(probe?.SuggestedId, Path.GetFileName(dir), dir);
            dir = TryMove(dir, root, ascii) ?? dir;
        }
        var p = VoicePack.TryLoad(dir, out var err);
        if (p == null) { problems.Add($"「{Path.GetFileName(dir)}」无法使用：{err}"); return; }
        if (packs.Any(x => string.Equals(x.Id, p.Id, StringComparison.OrdinalIgnoreCase))) { problems.Add($"「{p.Id}」重复，已跳过"); return; }
        foreach (var w in p.Warnings) problems.Add($"「{p.Id}」{w}");
        packs.Add(p);
    }

    private static IEnumerable<string> SafeDirs(string d)
    {
        try { return Directory.GetDirectories(d); } catch { return Array.Empty<string>(); }
    }

    /// <summary>把文件夹挪到 root 下、改成英文名。成功返回新路径，失败返回 null。</summary>
    private static string? TryMove(string dir, string root, string wanted)
    {
        try
        {
            string name = SafeFolderName(wanted, null, dir);
            string target = UniqueDir(root, name, except: dir);
            if (string.Equals(Path.GetFullPath(target), Path.GetFullPath(dir), StringComparison.OrdinalIgnoreCase)) return dir;
            Directory.Move(dir, target);
            Log.Write($"音色包整理：{dir} → {target}");
            return target;
        }
        catch (Exception ex)
        {
            Log.Write($"音色包整理失败（照常加载）：{dir}：{ex.Message}");
            return null;
        }
    }

    private static void TryDeleteIfEmpty(string d)
    {
        try { if (Directory.Exists(d) && !Directory.EnumerateFileSystemEntries(d).Any()) Directory.Delete(d); } catch { }
    }

    /// <summary>
    /// 生成一个只含英文字母、数字、- _ 的文件夹名：优先用 preferred（voice.json 的 id），其次 fallback（原文件夹名），
    /// 都不行时用 "voice-" + 路径的短哈希。
    /// </summary>
    public static string SafeFolderName(string? preferred, string? fallback, string seed)
    {
        foreach (var s in new[] { preferred, fallback })
        {
            if (string.IsNullOrWhiteSpace(s)) continue;
            var chars = s.Trim().Select(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' or '_' or '.' ? c : '-').ToArray();
            string t = new string(chars).Trim('-', '.', ' ');
            while (t.Contains("--")) t = t.Replace("--", "-");
            if (t.Length > 40) t = t.Substring(0, 40).TrimEnd('-', '.');
            // 有效字符太少（基本是中文名）或是 Windows 的保留名（CON、NUL、COM1……）就不用
            if (t.Length >= 2 && chars.Count(c => c != '-') * 2 >= s.Trim().Length && !IsReservedName(t)) return t;
        }
        return "voice-" + Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(seed))).Substring(0, 8).ToLowerInvariant();
    }

    private static bool IsReservedName(string n)
    {
        string b = n.Split('.')[0].ToUpperInvariant();
        return b is "CON" or "PRN" or "AUX" or "NUL" || (b.Length == 4 && (b.StartsWith("COM") || b.StartsWith("LPT")) && char.IsDigit(b[3]));
    }

    /// <summary>root 下一个还不存在的文件夹路径：name、name-2、name-3 ……</summary>
    public static string UniqueDir(string root, string name, string? except = null)
    {
        string p = Path.Combine(root, name);
        for (int i = 2; (Directory.Exists(p) || File.Exists(p)) && !(except != null && string.Equals(Path.GetFullPath(p), Path.GetFullPath(except), StringComparison.OrdinalIgnoreCase)); i++)
        {
            p = Path.Combine(root, $"{name}-{i}");
            if (i > 999) { p = Path.Combine(root, $"{name}-{Guid.NewGuid():N}".Substring(0, Math.Min(60, name.Length + 9))); break; }
        }
        return p;
    }
}

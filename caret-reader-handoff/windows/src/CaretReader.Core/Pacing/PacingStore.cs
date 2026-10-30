using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CaretReader.Core.Pacing;

/// <summary>每个音色单独微调（文档 6.4 / 精简版 4.3）。</summary>
public sealed class VoiceOverride
{
    /// <summary>语速微调倍数，1.0 = 跟随全局；范围 0.8～1.2（即 ±20%）。</summary>
    public double Speed { get; set; } = 1.0;
    /// <summary>停顿倍率，1.0 = 跟随全局；范围 0.5～2.0。</summary>
    public double Pause { get; set; } = 1.0;

    public bool IsDefault => Math.Abs(Speed - 1) < 1e-6 && Math.Abs(Pause - 1) < 1e-6;

    public void Clamp()
    {
        Speed = double.IsFinite(Speed) ? Math.Clamp(Speed, 0.8, 1.2) : 1;
        Pause = double.IsFinite(Pause) ? Math.Clamp(Pause, 0.5, 2.0) : 1;
    }
}

/// <summary>某个音色在 speed=1.0、去静音后的每分钟字数（本机测出或从真实朗读里累计）。</summary>
public sealed class CpmRecord
{
    public double CpmAtSpeed1 { get; set; }
    /// <summary>累计了多少字（真实朗读估计时用；≥ PacingResolver.MinCharsForCpm 才算数）。</summary>
    public int Chars { get; set; }
    /// <summary>累计的去静音语音秒数（已折算到 speed=1.0）。</summary>
    public double Seconds { get; set; }
    /// <summary>"fixed" = 固定句子测的；"reading" = 从真实朗读累计的。</summary>
    public string Source { get; set; } = "reading";
}

/// <summary>
/// 第 2 轮节奏设置的存储。和 settings.json 放在同一个文件夹下的 data\ 里，升级（覆盖程序文件）不会丢：
///   data\pacing.json  = { "targetCpm": 250, "voices": { "chaowen-0": { "speed": 1.05, "pause": 1.2 } } }
///   data\bench.json   = { "chaowen-0": { "cpmAtSpeed1": 268, ... } }
/// 读取宽松：整个文件坏了 → 默认值并留 .bad 副本；某一项坏了 → 只跳过那一项。写入先写 .tmp 再替换。
/// 线程安全（朗读线程累计 cpm，界面线程改设置）。
/// </summary>
public sealed class PacingStore
{
    public const int DefaultTargetCpm = 250;
    public const int MinTargetCpm = 150, MaxTargetCpm = 400;

    private readonly object _gate = new();
    private readonly Dictionary<string, VoiceOverride> _voices = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CpmRecord> _bench = new(StringComparer.OrdinalIgnoreCase);
    private int? _targetCpm;

    public string? DataDir { get; }
    public string? PacingPath => DataDir == null ? null : Path.Combine(DataDir, "pacing.json");
    public string? BenchPath => DataDir == null ? null : Path.Combine(DataDir, "bench.json");

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>dataDir = null 时只在内存里（测试用）。</summary>
    public PacingStore(string? dataDir)
    {
        DataDir = dataDir;
        Reload();
    }

    /// <summary>与 settings.json 同目录的 data 文件夹。</summary>
    public static PacingStore ForSettingsFile(string settingsPath)
        => new(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(settingsPath)) ?? ".", "data"));

    /// <summary>用户设置的统一目标语速（字/分）。还没设过时为 null（第一次知道音色 cpm 时由旧语速迁移得出）。</summary>
    public int? TargetCpm
    {
        get { lock (_gate) return _targetCpm; }
        set { lock (_gate) _targetCpm = value is int v ? ClampTarget(v) : null; }
    }

    public static int ClampTarget(int v) => Math.Clamp((int)Math.Round(v / 10.0) * 10, MinTargetCpm, MaxTargetCpm);

    public VoiceOverride GetOverride(string voiceId)
    {
        lock (_gate)
            return _voices.TryGetValue(voiceId, out var o) ? new VoiceOverride { Speed = o.Speed, Pause = o.Pause } : new VoiceOverride();
    }

    /// <summary>设置某个音色的微调；传 null 或默认值 = “恢复跟随全局”。</summary>
    public void SetOverride(string voiceId, VoiceOverride? o)
    {
        lock (_gate)
        {
            if (o == null) { _voices.Remove(voiceId); return; }
            var c = new VoiceOverride { Speed = o.Speed, Pause = o.Pause };
            c.Clamp();
            if (c.IsDefault) _voices.Remove(voiceId); else _voices[voiceId] = c;
        }
    }

    public IReadOnlyDictionary<string, VoiceOverride> AllOverrides()
    {
        lock (_gate) return new Dictionary<string, VoiceOverride>(_voices, StringComparer.OrdinalIgnoreCase);
    }

    public CpmRecord? GetCpm(string voiceId)
    {
        lock (_gate) return _bench.TryGetValue(voiceId, out var r) ? new CpmRecord { CpmAtSpeed1 = r.CpmAtSpeed1, Chars = r.Chars, Seconds = r.Seconds, Source = r.Source } : null;
    }

    public void SetCpm(string voiceId, CpmRecord r)
    {
        lock (_gate) _bench[voiceId] = r;
    }

    // ------------------------------------------------------------------

    public void Reload()
    {
        lock (_gate)
        {
            _voices.Clear(); _bench.Clear(); _targetCpm = null;
            if (ReadObj(PacingPath) is JsonObject p)
            {
                try
                {
                    var t = p["targetCpm"];
                    if (t != null) _targetCpm = ClampTarget(t.GetValue<int>());
                }
                catch { Log.Write("pacing.json 的 targetCpm 无效，已忽略"); KeepBad(PacingPath); }
                if (p["voices"] is JsonObject vs)
                    foreach (var (id, node) in vs)
                    {
                        try
                        {
                            var o = node.Deserialize<VoiceOverride>(JsonOpts);
                            if (o == null) continue;
                            o.Clamp();
                            if (!o.IsDefault) _voices[id] = o;
                        }
                        catch { Log.Write($"pacing.json 中音色 {id} 的微调无效，已跳过"); KeepBad(PacingPath); }
                    }
            }
            if (ReadObj(BenchPath) is JsonObject b)
                foreach (var (id, node) in b)
                {
                    try
                    {
                        var r = node.Deserialize<CpmRecord>(JsonOpts);
                        if (r != null && double.IsFinite(r.CpmAtSpeed1) && r.CpmAtSpeed1 >= 0) _bench[id] = r;
                    }
                    catch { Log.Write($"bench.json 中音色 {id} 的记录无效，已跳过"); }
                }
        }
    }

    /// <summary>保存两份文件（不抛异常，失败只记日志）。</summary>
    public void Save()
    {
        if (DataDir == null) return;
        string pacingJson, benchJson;
        lock (_gate)
        {
            var root = new JsonObject();
            if (_targetCpm is int t) root["targetCpm"] = t;
            var vs = new JsonObject();
            foreach (var (id, o) in _voices.OrderBy(k => k.Key, StringComparer.Ordinal))
                vs[id] = JsonSerializer.SerializeToNode(o, JsonOpts);
            root["voices"] = vs;
            pacingJson = root.ToJsonString(JsonOpts);
            var b = new JsonObject();
            foreach (var (id, r) in _bench.OrderBy(k => k.Key, StringComparer.Ordinal))
                b[id] = JsonSerializer.SerializeToNode(r, JsonOpts);
            benchJson = b.ToJsonString(JsonOpts);
        }
        try
        {
            Directory.CreateDirectory(DataDir);
            WriteAtomic(PacingPath!, pacingJson);
            WriteAtomic(BenchPath!, benchJson);
        }
        catch (Exception ex) { Log.Write("保存节奏设置失败：" + ex.Message); }
    }

    private static void WriteAtomic(string path, string text)
    {
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, text);
        File.Move(tmp, path, overwrite: true);
    }

    private static JsonObject? ReadObj(string? path)
    {
        if (path == null || !File.Exists(path)) return null;
        try
        {
            return JsonNode.Parse(File.ReadAllText(path), null,
                new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject;
        }
        catch (Exception ex)
        {
            Log.Write($"{Path.GetFileName(path)} 不是合法的 JSON，用默认值：{ex.Message}");
            KeepBad(path);
            return null;
        }
    }

    private static void KeepBad(string? path)
    {
        if (path == null) return;
        try { File.Copy(path, path + ".bad", overwrite: true); } catch { }
    }
}

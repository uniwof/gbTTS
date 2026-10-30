using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CaretReader.Core;

/// <summary>第 3 轮：本机测速（RTF = 合成耗时 ÷ 语音时长）。≤0.5 流畅；0.5～0.8 可用；&gt;0.8 可能卡顿。</summary>
public enum SpeedClass { Unknown, Smooth, Usable, Laggy }

public static class VoiceSpeed
{
    public const double SmoothMax = 0.5, UsableMax = 0.8;

    /// <summary>测速用的固定短句（约 35 字，不含数字和英文，避免文本层影响）。</summary>
    public const string BenchText = "今天天气很好，我们一起去公园散步，看看盛开的花朵，听听树上鸟儿的歌声。";

    public static SpeedClass Classify(double rtf) =>
        rtf <= 0 ? SpeedClass.Unknown : rtf <= SmoothMax ? SpeedClass.Smooth : rtf <= UsableMax ? SpeedClass.Usable : SpeedClass.Laggy;

    public static string Label(SpeedClass c) => c switch
    {
        SpeedClass.Smooth => "流畅", SpeedClass.Usable => "可用", SpeedClass.Laggy => "可能卡顿", _ => "",
    };

    /// <summary>
    /// 测一次：先合成一遍预热（丢弃），再正式合成一遍，RTF = 耗时 ÷ 语音秒数。
    /// synth 返回 -1~1 的采样；全程不读不写朗读文字以外的东西，也不记录文字。
    /// </summary>
    public static double Measure(Func<string, float[]> synth, int sampleRate)
    {
        if (sampleRate <= 0) return 0;
        synth(BenchText);
        var sw = Stopwatch.StartNew();
        var wav = synth(BenchText);
        sw.Stop();
        double speech = (double)wav.Length / sampleRate;
        return speech <= 0.2 ? 0 : sw.Elapsed.TotalSeconds / speech;
    }

    /// <summary>
    /// 排序并打标签：流畅 → 可用 → 还没测 → 可能卡顿；同一档里按 voice.json 的 rank（小的在前），再按原来的 order、名字。
    /// </summary>
    public static void Order(List<VoicePack> packs, SpeedStore store)
    {
        foreach (var p in packs)
            p.SpeedLabel = store.TryGet(p.Id, out double r) ? Label(Classify(r)) : null;
        static int Group(SpeedClass c) => c switch { SpeedClass.Smooth => 0, SpeedClass.Usable => 1, SpeedClass.Unknown => 2, _ => 3 };
        var sorted = packs
            .OrderBy(p => Group(store.TryGet(p.Id, out double r) ? Classify(r) : SpeedClass.Unknown))
            .ThenBy(p => p.Rank).ThenBy(p => p.Order).ThenBy(p => p.Name, StringComparer.CurrentCulture)
            .ToList();
        packs.Clear();
        packs.AddRange(sorted);
    }

    /// <summary>
    /// 首次启动的默认音色：在“流畅”和“还没测”的音色里取 rank 最靠前的（都没有时在全部里取）。
    /// 还没测过任何音色时，就是 rank 最靠前的那个；它加载后会测速，测出“可能卡顿”时界面会提示换音色。
    /// </summary>
    public static VoicePack? PickDefault(IReadOnlyList<VoicePack> packs, SpeedStore store)
    {
        if (packs.Count == 0) return null;
        var pool = packs.Where(p => !store.TryGet(p.Id, out double r) || Classify(r) == SpeedClass.Smooth).ToList();
        if (pool.Count == 0) pool = packs.ToList();
        return pool.OrderBy(p => p.Rank).ThenBy(p => p.Order).ThenBy(p => p.Name, StringComparer.CurrentCulture).First();
    }
}

/// <summary>
/// data\speed.json：每个音色在本机的 RTF。文件里记着电脑名和 CPU 核数，
/// 换了一台电脑（整个文件夹拷走）就作废重测。读写出错一律当作“没测过”，不影响朗读。
/// </summary>
public sealed class SpeedStore
{
    private readonly string _path;
    private readonly Dictionary<string, double> _rtf = new(StringComparer.OrdinalIgnoreCase);
    private static string Machine => $"{Environment.MachineName}/{Environment.ProcessorCount}";

    public SpeedStore(string path) { _path = path; Load(); }

    public bool TryGet(string voiceId, out double rtf) => _rtf.TryGetValue(voiceId, out rtf) && rtf > 0;

    public void Set(string voiceId, double rtf)
    {
        _rtf[voiceId] = Math.Round(rtf, 3);
        Save();
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var root = JsonNode.Parse(File.ReadAllText(_path)) as JsonObject;
            if (root == null || (string?)root["machine"] != Machine) return;
            if (root["rtf"] is JsonObject o)
                foreach (var kv in o)
                    if (kv.Value is JsonValue v && v.TryGetValue(out double d) && d > 0) _rtf[kv.Key] = d;
        }
        catch (Exception ex) { Log.Write("读取 speed.json 失败，当作没测过: " + ex.GetType().Name); _rtf.Clear(); }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
            var o = new JsonObject();
            foreach (var kv in _rtf.OrderBy(k => k.Key, StringComparer.Ordinal)) o[kv.Key] = kv.Value;
            var root = new JsonObject { ["machine"] = Machine, ["rtf"] = o };
            File.WriteAllText(_path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { Log.Write("保存 speed.json 失败: " + ex.GetType().Name); }
    }
}

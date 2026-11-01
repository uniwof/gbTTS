using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace CaretReader.Core;

/// <summary>
/// 各种标点后面的停顿时长（单位：秒）。
/// 默认值是用户试听后认可的：逗号 0.15 / 句号 0.5 / 分号 0.3 / 冒号 0.25。
/// </summary>
public sealed class PauseSettings
{
    public double Comma { get; set; } = 0.15;      // ，、 ,
    public double Period { get; set; } = 0.50;     // 。！？ . ! ?
    public double Semicolon { get; set; } = 0.30;  // ； ;
    public double Colon { get; set; } = 0.25;      // ： :
    public double Newline { get; set; } = 0.50;    // 换行，默认和句号一样

    /// <summary>把所有数值限制在 0~3 秒之间，防止设置文件被手动改坏。</summary>
    public void Clamp()
    {
        Comma = Math.Clamp(Comma, 0, 3);
        Period = Math.Clamp(Period, 0, 3);
        Semicolon = Math.Clamp(Semicolon, 0, 3);
        Colon = Math.Clamp(Colon, 0, 3);
        Newline = Math.Clamp(Newline, 0, 3);
    }
}

/// <summary>
/// 程序的全部设置。保存为 settings.json（UTF-8，人眼可读，可以用记事本改）。
///
/// 保存在哪里？
///   1) 如果程序所在文件夹可以写入 → 就放在 exe 旁边（方便“绿色版”整体拷走）。
///   2) 否则（比如装在 C:\Program Files 里）→ 放在 %AppData%\gbTTS\settings.json。
/// </summary>
public sealed class AppSettings
{
    /// <summary>全局快捷键，格式例如 "Ctrl+Alt+R"。</summary>
    public string Hotkey { get; set; } = "Ctrl+Alt+R";

    /// <summary>“停止”全局快捷键（v0.2），默认 "Ctrl+Alt+S"。主快捷键负责 开始 / 暂停 / 继续，这个负责彻底停止。</summary>
    public string StopHotkey { get; set; } = "Ctrl+Alt+S";

    /// <summary>当前选中的音色 id（对应 voices.json 里的 voices[].id）。空 = 用第一个。</summary>
    public string? VoiceId { get; set; }

    /// <summary>语速。1.0 = 模型原本的速度。范围 0.5 ~ 2.0。</summary>
    public double Speed { get; set; } = 1.0;

    /// <summary>合成用的 CPU 线程数。默认 2，低配电脑友好。范围 1 ~ 8。</summary>
    public int NumThreads { get; set; } = 2;

    /// <summary>标点停顿。</summary>
    public PauseSettings Pauses { get; set; } = new();

    /// <summary>P1 文本层设置（Markdown 清理、标题识别、英文策略、规则包开关）。</summary>
    public CaretReader.Core.Settings.TextLayerSettings Text { get; set; } = new();

    /// <summary>A2：本软件独立音量（百分比 10~150，100 = 不变），不影响系统音量。</summary>
    public int VolumePercent { get; set; } = 100;

    /// <summary>A2：开机自启（默认关，写当前用户的 Run 项）。</summary>
    public bool AutoStart { get; set; } = false;

    /// <summary>窗口是否总在最前面。</summary>
    public bool TopMost { get; set; } = true;

    /// <summary>上次关闭时窗口的位置（屏幕坐标）。null = 默认位置。</summary>
    public int? WindowX { get; set; }
    public int? WindowY { get; set; }

    /// <summary>v0.6：悬浮球的位置（屏幕坐标，左上角）。null = 屏幕右下角。</summary>
    public int? BallX { get; set; }
    public int? BallY { get; set; }

    /// <summary>v0.6：悬浮球外观："pet" = 看书的小人（默认），"ball" = 简单圆球（完全不动）。</summary>
    public string Appearance { get; set; } = "pet";

    // ------------------------------------------------------------------

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        // 让中文直接以中文保存，而不是 \u4E2D 这样的转义
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>把数值限制在合理范围内。</summary>
    public void Clamp()
    {
        if (Appearance is not ("pet" or "ball")) Appearance = "pet";
        Speed = Math.Clamp(Speed, 0.5, 2.0);
        VolumePercent = Math.Clamp(VolumePercent, 10, 150);
        NumThreads = Math.Clamp(NumThreads, 1, 8);
        Pauses ??= new PauseSettings();
        Text ??= new CaretReader.Core.Settings.TextLayerSettings();
        Text.RulePacks ??= new(StringComparer.OrdinalIgnoreCase);
        Pauses.Clamp();
        if (string.IsNullOrWhiteSpace(Hotkey)) Hotkey = "Ctrl+Alt+R";
        if (string.IsNullOrWhiteSpace(StopHotkey)) StopHotkey = "Ctrl+Alt+S";
    }

    /// <summary>
    /// 计算 settings.json 的完整路径（规则见类说明）。
    /// </summary>
    public static string ResolvePath(string appDir, string? appDataRoot = null)
    {
        string local = Path.Combine(appDir, "settings.json");
        if (File.Exists(local)) return local;
        if (IsDirWritable(appDir)) return local;

        // 程序文件夹不可写（例如装在 C:\Program Files）→ 用 %AppData%\gbTTS\（硬要求 10）
        string root = appDataRoot ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string legacy = Path.Combine(root, "CaretReader", "settings.json");   // 旧版放这里，有就继续用，升级不丢设置
        if (File.Exists(legacy)) return legacy;
        string appData = Path.Combine(root, "gbTTS");
        Directory.CreateDirectory(appData);
        return Path.Combine(appData, "settings.json");
    }

    private static bool IsDirWritable(string dir)
    {
        try
        {
            string probe = Path.Combine(dir, ".write_test_" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(probe, "x");
            File.Delete(probe);
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// 读取设置（不会抛异常）。
    ///
    /// 容错规则：
    ///   * 文件不存在 → 全部用默认值。
    ///   * 文件整个不是合法 JSON → 全部用默认值，原文件另存为 settings.json.bad 以便查看。
    ///   * 某一项写错了（例如 "Speed": "快"）→ 只有这一项恢复默认，其它项照常读取，
    ///     并在 log.txt 里记录是哪一项；原文件同样另存为 settings.json.bad。
    /// </summary>
    public static AppSettings Load(string path)
    {
        var s = new AppSettings();
        try
        {
            if (File.Exists(path))
            {
                string json = File.ReadAllText(path);
                JsonNode? root = null;
                try
                {
                    root = JsonNode.Parse(json, new JsonNodeOptions { PropertyNameCaseInsensitive = true },
                        new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
                }
                catch (Exception ex)
                {
                    Log.Write("settings.json 不是合法的 JSON，全部使用默认设置: " + ex.Message);
                    KeepBadCopy(path);
                }
                if (root is JsonObject obj)
                {
                    var errors = new List<string>();
                    ApplyLenient(s, obj, errors, "");
                    if (errors.Count > 0)
                    {
                        Log.Write("settings.json 中以下项目无效，已恢复默认: " + string.Join("、", errors));
                        KeepBadCopy(path);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Write("读取设置失败，使用默认设置: " + ex.Message);
        }
        s.Clamp();
        return s;
    }

    /// <summary>
    /// 把 JSON 里的每一项分别读进 target 的同名属性；读不了的项跳过（保留默认值）并记入 errors。
    /// 嵌套的对象（例如 Pauses）同样逐项读取。
    /// </summary>
    private static void ApplyLenient(object target, JsonObject obj, List<string> errors, string prefix)
    {
        var props = target.GetType().GetProperties()
            .Where(p => p.CanWrite && p.GetIndexParameters().Length == 0)
            .ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);

        foreach (var (key, node) in obj)
        {
            if (!props.TryGetValue(key, out var prop)) continue;   // 不认识的项：忽略
            try
            {
                if (prop.PropertyType == typeof(PauseSettings) || prop.PropertyType == typeof(CaretReader.Core.Settings.TextLayerSettings))
                {
                    var pz = Activator.CreateInstance(prop.PropertyType)!;
                    if (node is JsonObject sub) ApplyLenient(pz, sub, errors, prefix + prop.Name + ".");
                    else if (node != null) errors.Add(prefix + prop.Name);
                    prop.SetValue(target, pz);
                    continue;
                }
                if (node == null)
                {
                    // null 只对可空类型有效（例如 VoiceId / WindowX），其它类型保留默认
                    if (!prop.PropertyType.IsValueType || Nullable.GetUnderlyingType(prop.PropertyType) != null)
                        prop.SetValue(target, null);
                    else errors.Add(prefix + prop.Name);
                    continue;
                }
                prop.SetValue(target, node.Deserialize(prop.PropertyType, JsonOpts));
            }
            catch
            {
                errors.Add(prefix + prop.Name);
            }
        }
    }

    private static void KeepBadCopy(string path)
    {
        try { File.Copy(path, path + ".bad", overwrite: true); } catch { }
    }

    /// <summary>保存设置。先写临时文件再替换，避免写到一半断电把文件弄坏。</summary>
    public void Save(string path)
    {
        Clamp();
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonOpts));
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>深拷贝（设置窗口里编辑时用，点“取消”就丢掉拷贝）。</summary>
    public AppSettings Clone() =>
        JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(this, JsonOpts), JsonOpts)!;
}

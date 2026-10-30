using SherpaOnnx;

namespace CaretReader.Core;

/// <summary>
/// 对 sherpa-onnx 离线语音合成 (OfflineTts) 的简单封装。
///
/// 关键约定：
///   * 同一时间只加载“一个模型”。切换到另一个模型的音色时，先释放旧模型，再加载新模型。
///     这样内存峰值 ≈ 一个模型的大小（目标 ≤ 约 360MB），4GB 内存的电脑也能用。
///   * 同一个模型里的不同说话人 (sid) 切换不需要重新加载，瞬间完成。
///   * 所有对引擎的调用都用一把锁 (_gate) 保护：
///     “正在合成时释放模型”会导致程序崩溃，有了锁就不会发生。
/// </summary>
public sealed class TtsEngine : IDisposable
{
    private readonly object _gate = new();
    private OfflineTts? _tts;
    /// <summary>v0.5：当前模型的“拼音 → 私用区字符”表（音色包没有时为 null = 按旧方式朗读）。</summary>
    private PuaMap? _pua;
    private bool _loggedFirst;
    /// <summary>v0.5：是否用多音字前端（测试程序可以关掉，对比旧版）。</summary>
    public static bool UseFrontend { get; set; } = true;

    /// <summary>当前已加载模型的“指纹”（VoicePack.ModelKey）；没有加载时为 null。
    /// 两个音色包带的是同一个模型时指纹相同，切换不用重新加载。</summary>
    public string? LoadedModelId { get; private set; }
    /// <summary>当前模型来自哪个音色包（只用于日志）。</summary>
    public string? LoadedPackId { get; private set; }
    /// <summary>当前模型加载时使用的线程数。</summary>
    public int LoadedThreads { get; private set; }

    /// <summary>当前模型的采样率（例如 Melo=44100，Matcha=22050，zh-ll=16000）。</summary>
    public int SampleRate { get { lock (_gate) return _tts?.SampleRate ?? 0; } }

    public int NumSpeakers { get { lock (_gate) return _tts?.NumSpeakers ?? 0; } }

    /// <summary>这个音色包的模型是否已经（用同样的线程数）加载好了。</summary>
    public bool IsLoaded(VoicePack pack, int numThreads) => _tts != null && LoadedModelId == pack.ModelKey && LoadedThreads == numThreads;

    /// <summary>
    /// 加载音色包的模型（耗时操作，可能几秒钟，请在后台线程调用）。
    /// 如果同一个模型已经用同样的线程数加载过了（哪怕来自另一个音色包），直接返回。
    /// appDir = 程序文件夹：工作目录会设成它，原生库只拿到 voices\xxx\model.onnx 这样的英文相对路径。
    /// </summary>
    /// <exception cref="FileNotFoundException">模型文件缺失时，消息是给用户看的中文说明。</exception>
    public void Load(VoicePack pack, int numThreads, string appDir)
    {
        lock (_gate)
        {
            string key = pack.ModelKey;
            if (_tts != null && LoadedModelId == key && LoadedThreads == numThreads) { LoadedPackId = pack.Id; return; }

            // 1) 先检查文件（音色包可能在程序运行中被删改），缺了就给出清楚的中文提示
            Stage?.Invoke($"加载 {pack.Id}：第 1 步 检查文件");
            if (VoicePack.TryLoad(pack.Dir, out var why) == null)
                throw new FileNotFoundException(
                    $"音色「{pack.Name}」无法加载：{why}\n\n请确认「音色」(voices) 文件夹里的这个音色包完整，或重新导入。");

            // 2) 先释放旧模型（省内存）
            UnloadNoLock();

            // 3) 组装 sherpa-onnx 的配置
            //    v0.3：路径一律转成“原生库一定能打开”的形式（相对路径 / 短路径），
            //    否则路径里有中文时原生库会直接让进程闪退（详见 NativePaths 的说明）。
            Stage?.Invoke($"加载 {pack.Id}：第 2 步 准备路径");
            if (OperatingSystem.IsWindows())
            {
                try { Directory.SetCurrentDirectory(appDir); }
                catch (Exception ex) { Log.Write("设置工作目录失败: " + ex.Message); }
            }
            string cwd = Directory.GetCurrentDirectory();
            string P(string? rel) => string.IsNullOrEmpty(rel) || pack.Inside(rel) is not string full ? "" : NativePaths.ForNative(full, cwd);
            var f = pack.Files!;

            var config = new OfflineTtsConfig();
            config.Model.NumThreads = numThreads;
            config.Model.Provider = "cpu";
            config.Model.Debug = 0;
            config.RuleFsts = string.Join(",", (f.RuleFsts ?? new()).Select(x => P(x)));
            // 我们自己已经按标点切成了小句，每次只送一个小句进去
            config.MaxNumSentences = 1;

            if (pack.IsMatcha)
            {
                config.Model.Matcha.AcousticModel = P(f.Model);
                config.Model.Matcha.Vocoder = P(f.Vocoder);
                config.Model.Matcha.Tokens = P(f.Tokens);
                config.Model.Matcha.Lexicon = P(f.Lexicon);
                config.Model.Matcha.DictDir = P(f.DictDir);
                config.Model.Matcha.DataDir = P(f.DataDir);
            }
            else
            {
                // vits / melo / piper 在 sherpa-onnx 里都是 VITS 结构
                config.Model.Vits.Model = P(f.Model);
                config.Model.Vits.Tokens = P(f.Tokens);
                config.Model.Vits.Lexicon = P(f.Lexicon);
                config.Model.Vits.DictDir = P(f.DictDir);
                config.Model.Vits.DataDir = P(f.DataDir);
            }

            Log.Write($"加载 {pack.Id}（{pack.Engine}）：模型路径 = {config.Model.Vits.Model}{config.Model.Matcha.AcousticModel}（工作目录 {cwd}，代码页 {NativePaths.AnsiCodePage}）");

            // 4) 真正加载（读取几百 MB 的 onnx 文件，最耗时的一步）
            Stage?.Invoke($"加载 {pack.Id}：第 3 步 创建语音引擎（sherpa-onnx 读取模型）");
            var tts = new OfflineTts(config);
            // v0.3：sherpa-onnx 配置有误时返回空指针，而它的 C# 封装不检查；
            // 此时再调用任何方法都会访问空指针、让整个程序闪退。所以先自己检查一下。
            if (GetNativeHandle(tts) == IntPtr.Zero)
            {
                GC.SuppressFinalize(tts);   // 空句柄，不能调用 Dispose（会传空指针给原生库）
                throw new InvalidDataException(
                    $"语音引擎无法打开音色「{pack.Name}」（模型文件路径或文件内容有问题）。\n\n" +
                    "请重新导入这个音色包；如果还不行，把整个程序文件夹放在纯英文路径下（例如 C:\\CaretReader）后再试。");
            }
            Stage?.Invoke($"加载 {pack.Id}：第 4 步 读取采样率");
            if (tts.SampleRate <= 0)
            {
                tts.Dispose();
                throw new InvalidDataException($"模型加载失败：{pack.Name}（文件可能已损坏）");
            }
            _tts = tts;
            _loggedFirst = false;
            _pua = pack.Files!.PuaMap is string pm && pack.Inside(pm) is string pf ? PuaMap.TryLoad(pf) : null;
            Log.Write(_pua != null ? $"多音字前端：音色包 {pack.Id} 的拼音表 {_pua.Count} 条" : $"多音字前端：音色包 {pack.Id} 没有拼音表，按旧方式朗读");
            LoadedModelId = key;
            LoadedPackId = pack.Id;
            LoadedThreads = numThreads;
            if (pack.Sid >= Math.Max(1, tts.NumSpeakers))
                Log.Write($"注意：音色包 {pack.Id} 的 sid={pack.Sid} 超出模型的说话人数 {tts.NumSpeakers}，将使用 0 号");

            // 5) 预热：合成一个很短的词，让第一次真正朗读更快开始
            Stage?.Invoke($"加载 {pack.Id}：第 5 步 预热合成");
            try { var w = _tts.Generate("你好", 1.0f, Math.Min(pack.Sid, Math.Max(0, tts.NumSpeakers - 1))); w.Dispose(); } catch { /* 预热失败不要紧 */ }
        }
    }

    /// <summary>
    /// 合成一个小句，返回 -1~1 之间的浮点采样。线程安全。
    /// </summary>
    public float[] Synthesize(string text, int sid, float speed)
    {
        lock (_gate)
        {
            if (_tts == null) throw new InvalidOperationException("模型还没有加载。");
            if (sid < 0 || sid >= Math.Max(1, _tts.NumSpeakers)) sid = 0;
            text = ForEngine(text);
            var audio = _tts.Generate(text, speed, sid);
            if (audio.Handle == IntPtr.Zero)   // v0.3：原生库合成失败返回空指针，不能再去读它
            {
                GC.SuppressFinalize(audio);
                throw new InvalidOperationException("语音引擎没能合成这一句。");
            }
            try { return audio.Samples ?? Array.Empty<float>(); }
            finally { audio.Dispose(); }   // 立即释放原生内存
        }
    }

    /// <summary>v0.5：多音字前端（有拼音表、前端已加载时）把汉字换成指定读音；任何问题都退回原文。</summary>
    private string ForEngine(string text)
    {
        if (_pua == null || !UseFrontend) return text;
        var f = PolyphoneFrontend.Shared();
        if (f == null) return text;
        try
        {
            string r = f.ToEngineText(text, _pua);
            if (!_loggedFirst)
            {
                _loggedFirst = true;
                Log.Write($"多音字前端：已启用（本句 {r.Count(ch => ch >= '\uE000' && ch <= '\uF8FF')}/{text.Length} 个字按定好的读音朗读）");
            }
            return r;
        }
        catch (Exception ex) { Log.Write("多音字前端出错，这一句按原文朗读：" + ex.Message); return text; }
    }

    /// <summary>释放当前模型。会等正在进行的合成结束后再释放。</summary>
    public void Unload()
    {
        lock (_gate) UnloadNoLock();
    }

    /// <summary>
    /// v0.3：每个加载步骤开始前调用（界面把它写进 log.txt 和“上次运行到哪一步”的记录里）。
    /// 万一原生库让进程直接闪退，下次启动时就能知道死在哪一步。
    /// </summary>
    public static Action<string>? Stage { get; set; }

    /// <summary>读出 OfflineTts 内部的原生句柄（它的 C# 封装没有公开这个字段）。读不到时返回 -1（视为有效）。</summary>
    private static IntPtr GetNativeHandle(OfflineTts tts)
    {
        try
        {
            var f = typeof(OfflineTts).GetField("_handle",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (f?.GetValue(tts) is System.Runtime.InteropServices.HandleRef h) return h.Handle;
        }
        catch { }
        return new IntPtr(-1);
    }

    private void UnloadNoLock()
    {
        _tts?.Dispose();
        _tts = null;
        _pua = null;
        LoadedModelId = null;
        LoadedPackId = null;
        LoadedThreads = 0;
        // 主动回收一次，尽快把旧模型的内存还给系统（低内存电脑上很有用）
        GC.Collect();
        GC.WaitForPendingFinalizers();
    }

    public void Dispose() => Unload();
}

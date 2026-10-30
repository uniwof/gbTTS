using System.Diagnostics;
using System.Runtime.InteropServices;
using CaretReader.Core;

namespace CaretReader.App;

/// <summary>
/// v0.3：保证“程序闪退”也能留下线索。
///
///   * 每到一个关键步骤（尤其是加载模型的每一步），先把“正在做什么”写进 log.txt 和 run_state.txt。
///     原生库（sherpa-onnx / onnxruntime）如果让进程直接崩溃，.NET 来不及记录任何错误，
///     但这一行已经写进文件了 —— 下次看 log.txt 就知道死在哪一步。
///   * 正常退出时删除 run_state.txt。下次启动时如果它还在，说明上次是“意外退出”：
///     把上次停在哪一步写进日志，再从 Windows 的“事件查看器 → 应用程序”里
///     找出最近与 CaretReader.exe 有关的错误记录（崩溃的模块名、错误代码），一并写进 log.txt。
/// </summary>
internal static class CrashGuard
{
    private static string? _statePath;
    private static readonly object Gate = new();

    /// <summary>上次运行是否意外退出；是的话返回上次最后的步骤，否则 null。</summary>
    public static string? PreviousCrashStage { get; private set; }

    public static void Init(string dir)
    {
        _statePath = Path.Combine(dir, "run_state.txt");
        try
        {
            if (File.Exists(_statePath))
            {
                string last = File.ReadAllText(_statePath).Trim();
                PreviousCrashStage = string.IsNullOrEmpty(last) ? "（未知步骤）" : last;
            }
        }
        catch { }
        Mark("启动");
    }

    /// <summary>记录“现在正在做什么”（写入 run_state.txt + log.txt）。</summary>
    public static void Mark(string stage)
    {
        Log.Write("[步骤] " + stage);
        if (_statePath == null) return;
        lock (Gate)
        {
            try { File.WriteAllText(_statePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {stage}"); } catch { }
        }
    }

    /// <summary>正常退出：删除 run_state.txt。</summary>
    public static void CleanExit()
    {
        if (_statePath == null) return;
        lock (Gate)
        {
            try { File.Delete(_statePath); } catch { }
            _statePath = null;   // 之后不再写（退出过程中的步骤不算）
        }
    }

    /// <summary>把电脑环境信息写进日志，方便排查。</summary>
    public static void LogEnvironment(string appDir)
    {
        try
        {
            var mem = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1048576;
            Log.Write($"环境: {RuntimeInformation.OSDescription} ({Environment.OSVersion.Version})，" +
                      $"{RuntimeInformation.ProcessArchitecture}，.NET {Environment.Version}，" +
                      $"系统代码页 {NativePaths.AnsiCodePage}，内存约 {mem} MB，CPU 核心 {Environment.ProcessorCount}，" +
                      $"AVX {System.Runtime.Intrinsics.X86.Avx.IsSupported} / AVX2 {System.Runtime.Intrinsics.X86.Avx2.IsSupported}");
            Log.Write($"程序文件夹: {appDir}（{(NativePaths.IsAscii(appDir) ? "纯英文路径" : "路径含中文或特殊字符，v0.3 已用相对路径处理")}）");
        }
        catch (Exception ex) { Log.Write("读取环境信息失败: " + ex.Message); }
    }

    /// <summary>
    /// 上次意外退出时：从 Windows 事件日志里找最近 3 天内和 CaretReader.exe 有关的错误，写进 log.txt。
    /// 在后台线程运行，最多 8 秒，失败也不影响程序。
    /// </summary>
    public static void LogRecentWindowsErrorsAsync()
    {
        if (!OperatingSystem.IsWindows()) return;
        Task.Run(() =>
        {
            try
            {
                const string query =
                    "*[System[(Provider[@Name='Application Error'] or Provider[@Name='.NET Runtime'] or Provider[@Name='Windows Error Reporting'])" +
                    " and TimeCreated[timediff(@SystemTime) <= 259200000]]]";
                var psi = new ProcessStartInfo
                {
                    FileName = Path.Combine(Environment.SystemDirectory, "wevtutil.exe"),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                foreach (var a in new[] { "qe", "Application", "/q:" + query, "/c:30", "/rd:true", "/f:xml" })
                    psi.ArgumentList.Add(a);
                using var p = Process.Start(psi);
                if (p == null) return;
                var outTask = p.StandardOutput.ReadToEndAsync();
                if (!p.WaitForExit(8000)) { try { p.Kill(); } catch { } Log.Write("读取 Windows 事件日志超时"); return; }
                string text = outTask.Result;

                // XML 输出不受系统语言影响；每条事件以 </Event> 结尾。只保留提到 CaretReader 的（最多 3 条）
                var blocks = text.Split("</Event>", StringSplitOptions.RemoveEmptyEntries)
                    .Where(b => b.Contains("CaretReader", StringComparison.OrdinalIgnoreCase))
                    .Select(b => System.Text.RegularExpressions.Regex.Replace(b, @"\s+", " "))
                    .Take(3).ToList();
                if (blocks.Count == 0) { Log.Write("Windows 事件日志里没有找到 CaretReader 的崩溃记录"); return; }
                foreach (var b in blocks)
                {
                    string s = b.Trim();
                    if (s.Length > 4000) s = s[..4000] + " …（已截断）";
                    Log.Write("Windows 事件日志（上次崩溃）:\n" + s);
                }
            }
            catch (Exception ex) { Log.Write("读取 Windows 事件日志失败: " + ex.Message); }
        });
    }
}

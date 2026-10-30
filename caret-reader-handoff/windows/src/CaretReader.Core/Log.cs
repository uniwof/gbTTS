namespace CaretReader.Core;

/// <summary>
/// 极简日志：把信息追加写到 log.txt（和 settings.json 放在同一个文件夹）。
/// 例如每次朗读会记下“这次是用 UI Automation 还是剪贴板取到的文字”，方便排查问题。
/// 文件超过 1MB 时会自动改名为 log.old.txt，重新开始写，不会无限变大。
/// </summary>
public static class Log
{
    private static readonly object Gate = new();
    private static string? _path;

    /// <summary>设置日志文件路径。不设置的话就只输出到调试窗口。</summary>
    public static void Init(string path) => _path = path;

    /// <summary>同时把日志打印到控制台（测试程序用）。</summary>
    public static bool AlsoConsole { get; set; }

    public static void Write(string message)
    {
        string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {message}";
        System.Diagnostics.Debug.WriteLine(line);
        if (AlsoConsole) Console.WriteLine(line);
        if (_path == null) return;
        lock (Gate)
        {
            try
            {
                var fi = new FileInfo(_path);
                if (fi.Exists && fi.Length > 1_000_000)
                {
                    string old = Path.Combine(fi.DirectoryName!, "log.old.txt");
                    File.Move(_path, old, overwrite: true);
                }
                File.AppendAllText(_path, line + Environment.NewLine);
            }
            catch { /* 写日志失败不影响主功能 */ }
        }
    }
}

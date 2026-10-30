using System.Runtime.InteropServices;
using System.Text;

namespace CaretReader.Core;

/// <summary>
/// v0.3：把模型文件路径变成 sherpa-onnx（原生 C++ 库）一定能打开的形式。
///
/// 为什么需要它（v0.2 闪退的根本原因）：
///   sherpa-onnx 的 C# 封装用 [MarshalAs(UnmanagedType.LPStr)] 传路径，
///   在 Windows 上这会按“系统 ANSI 代码页”（中文系统是 GBK/936，英文系统是 1252）编码；
///   而 sherpa-onnx 内部却把收到的字节当成 UTF-8 去解码再打开文件。
///   所以只要路径里有中文（例如解压出来的文件夹“光标朗读”），原生库就“找不到文件”，
///   创建引擎返回空指针，随后读取采样率时访问空指针 —— 进程被系统直接结束，
///   .NET 来不及抛出任何异常，所以 log.txt 里什么错误都没有。
///
/// 解决办法（按顺序尝试）：
///   1. 把“当前工作目录”设为程序所在文件夹（这一步 Windows 用宽字符 API，中文没问题），
///      然后只给原生库传相对路径，例如 models\vits-melo-tts-zh_en\model.onnx —— 全是英文字母。
///   2. 相对路径里仍有非英文字符（用户自己改了 voices.json）时，试 Windows 的 8.3 短路径。
///   3. 系统代码页本身就是 UTF-8（65001）时，任何路径都能正确传过去。
///   4. 都不行：抛出中文说明的异常（界面会弹窗提示），而不是让程序闪退。
/// </summary>
public static class NativePaths
{
    /// <summary>系统 ANSI 代码页（非 Windows 返回 65001）。</summary>
    public static int AnsiCodePage => OperatingSystem.IsWindows() ? (int)GetACP() : 65001;

    public static bool IsAscii(string s)
    {
        foreach (char c in s) if (c > 0x7E || c < 0x20) return false;
        return true;
    }

    /// <summary>
    /// 原生库能否“原样”收到这个字符串：
    /// 非 Windows（.NET 用 UTF-8）或系统代码页是 UTF-8 时任何字符都行；否则只能是英文字符。
    /// </summary>
    public static bool SafeForNative(string s) => !OperatingSystem.IsWindows() || AnsiCodePage == 65001 || IsAscii(s);

    /// <summary>
    /// 把完整路径 full 转成可以安全传给原生库的路径。baseDir = 当前工作目录（程序文件夹）。
    /// 无法转换时抛出 IOException（中文说明）。
    /// </summary>
    public static string ForNative(string full, string baseDir)
        => ForNative(full, baseDir, OperatingSystem.IsWindows(), AnsiCodePage, GetShortPath);

    /// <summary>同上，参数可注入，方便在 Linux 上做单元测试。</summary>
    public static string ForNative(string full, string baseDir, bool windows, int acp, Func<string, string?> shortPath)
    {
        if (string.IsNullOrEmpty(full)) return full;
        if (!windows) return full;                               // Linux / macOS：UTF-8，原样传

        // 1) 相对路径（相对于当前工作目录 = 程序文件夹）
        string rel = Path.GetRelativePath(baseDir, full);
        if (!Path.IsPathRooted(rel) && !rel.StartsWith("..") && IsAscii(rel)) return rel;

        // 2) 完整路径本身就是英文
        if (IsAscii(full)) return full;

        // 3) 8.3 短路径（大多数 C 盘默认开启）
        string? sp = shortPath(full);
        if (sp != null && IsAscii(sp)) return sp;

        // 4) 系统代码页是 UTF-8：原样传也没问题
        if (acp == 65001) return full;

        throw new IOException(
            "模型文件的路径里有中文或特殊字符，语音引擎无法打开：\n  " + full +
            "\n\n请把 voices.json 里的模型文件夹名、文件名改成英文，" +
            "或者把整个程序文件夹放到纯英文路径下（例如 C:\\CaretReader）。");
    }

    private static string? GetShortPath(string path)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            var sb = new StringBuilder(1024);
            uint n = GetShortPathNameW(path, sb, (uint)sb.Capacity);
            if (n == 0 || n >= sb.Capacity) return null;
            return sb.ToString();
        }
        catch { return null; }
    }

    [DllImport("kernel32.dll")] private static extern uint GetACP();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetShortPathNameW(string lpszLongPath, StringBuilder lpszShortPath, uint cchBuffer);
}

using Microsoft.Win32;

namespace CaretReader.App;

/// <summary>A2：开机自启。只写当前用户的 Run 项，可随时关掉；失败静默忽略。</summary>
internal static class AutoStart
{
    private const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Name = "CaretReader";

    public static void Apply(bool on)
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(Key, true);
            if (k == null) return;
            if (on) k.SetValue(Name, "\"" + Application.ExecutablePath + "\"");
            else if (k.GetValue(Name) != null) k.DeleteValue(Name, false);
        }
        catch { }
    }
}

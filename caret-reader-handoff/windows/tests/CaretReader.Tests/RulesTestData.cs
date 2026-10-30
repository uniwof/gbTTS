namespace CaretReader.Tests;

/// <summary>测试用：找到随程序复制的 rules/ 文件夹。</summary>
internal static class RulesTestData
{
    public static string Dir
    {
        get
        {
            string d = Path.Combine(AppContext.BaseDirectory, "rules");
            if (Directory.Exists(d)) return d;
            throw new DirectoryNotFoundException("rules 文件夹没有被复制到测试输出目录");
        }
    }
    public static string Read(string name) => File.ReadAllText(Path.Combine(Dir, name));
}

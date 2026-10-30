using System.Reflection;
using CaretReader.App.Ui;

namespace CaretReader.App;

/// <summary>
/// v0.5：“关于 / 第三方许可”窗口。显示程序版本和 THIRD_PARTY_NOTICES.txt（多音字词库等开源数据的版权与许可证）。
/// 文字优先读程序文件夹里的 THIRD_PARTY_NOTICES.txt，没有就用嵌在程序里的那份。
/// </summary>
internal sealed class AboutForm : Form
{
    private int S(int px) => Ink.S(this, px);

    public AboutForm()
    {
        Text = "关于 / 第三方许可 · 光标朗读";
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Ink.Paper;
        Font = Ink.UiFont;
        TopMost = true;
        KeyPreview = true;

        int width = S(520);
        var title = new Label
        {
            Text = $"光标朗读 {Program.Version} · 本地离线朗读",
            Font = Ink.TitleFont, ForeColor = Ink.InkBlack, BackColor = Color.Transparent,
            AutoSize = false, Bounds = new Rectangle(S(24), S(16), width - S(48), S(22)),
        };
        var box = new TextBox
        {
            Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, WordWrap = true,
            BorderStyle = BorderStyle.FixedSingle, BackColor = Ink.PaperPure, ForeColor = Ink.InkBlack,
            Font = Ink.SmallFont, Text = LoadNotices(),
            Bounds = new Rectangle(S(24), S(46), width - S(48), S(380)),
        };
        var ok = new InkButton { Text = "关闭", Bounds = new Rectangle(width - S(24) - S(80), S(46) + S(380) + S(12), S(80), S(32)), Active = true };
        ok.Click += (_, _) => Close();
        Controls.AddRange(new Control[] { title, box, ok });
        ClientSize = new Size(width, ok.Bottom + S(16));
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) Close(); };
        Shown += (_, _) => { box.SelectionStart = 0; box.SelectionLength = 0; ok.Focus(); };
    }

    internal static string LoadNotices()
    {
        try
        {
            string f = Path.Combine(AppContext.BaseDirectory, "THIRD_PARTY_NOTICES.txt");
            if (File.Exists(f)) return Normalize(File.ReadAllText(f));
        }
        catch { }
        try
        {
            using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("THIRD_PARTY_NOTICES.txt");
            if (s != null) using (var r = new StreamReader(s)) return Normalize(r.ReadToEnd());
        }
        catch { }
        return "（找不到第三方许可说明文件 THIRD_PARTY_NOTICES.txt）";
    }

    // TextBox 只认 \r\n 换行
    private static string Normalize(string t) => t.TrimStart('\uFEFF').Replace("\r\n", "\n").Replace("\n", "\r\n");
}

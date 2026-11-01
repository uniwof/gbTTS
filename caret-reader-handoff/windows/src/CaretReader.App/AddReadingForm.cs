using System.Drawing;
using System.Text;
using System.Windows.Forms;
using CaretReader.App.Ui;
using CaretReader.Core;
using CaretReader.Core.Settings;
using CaretReader.Core.Text;
namespace CaretReader.App;

/// <summary>Ctrl+Alt+D：选中读错的文字 → 弹小窗，原文已填，填「读作」，试听，保存到 data\用户读法词库.tsv。</summary>
internal sealed class AddReadingForm : Form
{
    private readonly TextBox _src = new(), _dst = new();
    private readonly Label _msg = new() { AutoSize = true };
    private readonly string _userFile;
    private readonly Action<string> _preview;

    public const int MaxSourceChars = 50;

    public AddReadingForm(string selected, string dataDir, Action<string> preview)
    {
        _userFile = Path.Combine(dataDir, SettingsMigration.UserRulesFile);
        _preview = preview;
        Text = "添加读法 · 光标朗读";
        FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterScreen; TopMost = true;
        Font = new Font(Ink.FontFamilyName, 13f);
        ClientSize = new Size(520, 250);
        BackColor = Ink.Paper;
        Controls.Add(new Label { Text = "原文", Bounds = new Rectangle(16, 18, 80, 30) });
        _src.Bounds = new Rectangle(100, 14, 400, 34); _src.Text = selected; Controls.Add(_src);
        Controls.Add(new Label { Text = "读作", Bounds = new Rectangle(16, 66, 80, 30) });
        _dst.Bounds = new Rectangle(100, 62, 400, 34); Controls.Add(_dst);
        _msg.Location = new Point(16, 112); _msg.MaximumSize = new Size(488, 0);
        _msg.Text = "例：原文「行长」读作「航长」。读作里写汉字就行。"; Controls.Add(_msg);
        var listen = new Button { Text = "试听", Bounds = new Rectangle(100, 190, 100, 40) };
        var save = new Button { Text = "保存", Bounds = new Rectangle(300, 190, 90, 40) };
        var cancel = new Button { Text = "取消", Bounds = new Rectangle(400, 190, 90, 40) };
        listen.Click += (_, _) => { var t = _dst.Text.Trim(); if (t.Length > 0) _preview(t); else _msg.Text = "先填「读作」。"; };
        save.Click += (_, _) => { if (Save()) { DialogResult = DialogResult.OK; Close(); } };
        cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        Controls.AddRange(new Control[] { listen, save, cancel });
        CancelButton = cancel;
        Shown += (_, _) => { _dst.Focus(); };
    }

    private bool Save()
    {
        string s = _src.Text.Trim(), d = _dst.Text.Trim();
        var err = Validate(s, d);
        if (err != null) { _msg.Text = err; return false; }
        try { AppendRule(_userFile, s, d); }
        catch (Exception ex) { Log.Write("保存读法失败: " + ex.GetType().Name); _msg.Text = "保存失败，请稍后再试。"; return false; }
        Log.Write($"已添加读法（原文 {s.Length} 字，读作 {d.Length} 字）");
        return true;
    }

    public static string? Validate(string src, string dst)
    {
        if (src.Length == 0) return "原文不能是空的。";
        if (src.Length > MaxSourceChars) return $"原文最多 {MaxSourceChars} 个字，请只选读错的那几个字。";
        if (dst.Length == 0) return "请填「读作」。";
        if (src.IndexOfAny(new[] { '\t', '\n', '\r' }) >= 0 || dst.IndexOfAny(new[] { '\t', '\n', '\r' }) >= 0) return "不能包含换行或制表符。";
        return null;
    }

    /// <summary>追加一条「替换」规则；同一原文已有替换规则则覆盖。其余行原样保留。</summary>
    public static void AppendRule(string path, string src, string dst)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var lines = new List<string>();
        if (File.Exists(path)) lines.AddRange(File.ReadAllText(path, Encoding.UTF8).Replace("\r\n", "\n").Split('\n'));
        else lines.Add("# 用户读法词库。列：类型\t原文\t读作\t选项\t备注");
        while (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        lines.RemoveAll(l => { var c = l.Split('\t'); return c.Length >= 3 && c[0] == "替换" && c[1] == src; });
        lines.Add($"替换\t{src}\t{dst}\t\tCtrl+Alt+D 添加");
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, string.Join("\n", lines) + "\n", new UTF8Encoding(false));
        File.Move(tmp, path, true);
    }
}

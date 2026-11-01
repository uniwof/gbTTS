using System.Drawing;
using System.Windows.Forms;
using CaretReader.App.Ui;
namespace CaretReader.App;

/// <summary>A2：首次使用引导，3 步，可随时跳过，只出现一次。</summary>
internal sealed class GuideForm : Form
{
    private readonly string[] _steps;
    private int _i;
    private readonly Label _title = new(), _body = new();
    private readonly Button _next = new() { Text = "下一步" }, _skip = new() { Text = "跳过" };

    public GuideForm(string hotkey, string stopHotkey)
    {
        _steps = new[]
        {
            "第 1 步：选中文字\n在任意程序里，用鼠标选中想听的文字（或把光标放在文字里）。",
            $"第 2 步：开始朗读\n按 {hotkey}，或点横条上的「朗读」。读的过程中再按一次可暂停。",
            $"第 3 步：停止与设置\n按 {stopHotkey}，或点「■」停止。音色在横条的下拉列表里选，更多选项点「设置」。",
        };
        Text = "欢迎使用 · 光标朗读";
        FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterScreen; TopMost = true;
        AutoScaleMode = AutoScaleMode.None;
        Font = new Font(Ink.FontFamilyName, 13f * (float)Ink.Zoom);
        BackColor = Ink.Paper;
        ClientSize = new Size(Ink.S(this, 520), Ink.S(this, 230));
        _title.Bounds = new Rectangle(Ink.S(this, 20), Ink.S(this, 16), Ink.S(this, 480), Ink.S(this, 32));
        _body.Bounds = new Rectangle(Ink.S(this, 20), Ink.S(this, 56), Ink.S(this, 480), Ink.S(this, 110));
        _next.Bounds = new Rectangle(Ink.S(this, 390), Ink.S(this, 180), Ink.S(this, 110), Ink.S(this, 38));
        _skip.Bounds = new Rectangle(Ink.S(this, 270), Ink.S(this, 180), Ink.S(this, 110), Ink.S(this, 38));
        Controls.AddRange(new Control[] { _title, _body, _next, _skip });
        _next.Click += (_, _) => { if (++_i >= _steps.Length) Close(); else Show1(); };
        _skip.Click += (_, _) => Close();
        AcceptButton = _next;
        Show1();
    }

    private void Show1()
    {
        var parts = _steps[_i].Split("\n");
        _title.Text = parts[0];
        _body.Text = parts[1];
        _next.Text = _i == _steps.Length - 1 ? "开始使用" : "下一步";
    }
}

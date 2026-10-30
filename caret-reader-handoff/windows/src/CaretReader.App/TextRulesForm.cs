using System.Globalization;
using System.Text;
using CaretReader.App.Ui;
using CaretReader.Core;
using CaretReader.Core.Settings;
using CaretReader.Core.Text;

namespace CaretReader.App;

/// <summary>
/// P1 第 50 项：“读法”页 + “节奏（标点表部分）”页（文档 10.3 第 34、35 条）。
///  - 读法：用户规则表格（启用/类型/原文/读作/选项/备注），搜索、添加、删除、导入、导出；
///          内置规则包开关；英文策略；测试框（改写后的文字 + 拼音 + 试听）。写错的行标红并说明原因。
///  - 标点：符号/前停/后停/类别（只读），改过的行加粗，可单行恢复默认；标题识别、Markdown 清理开关。
/// 规则保存到 data/用户读法词库.tsv、data/用户标点表.tsv；开关写回 Settings（随 settings.json 保存）。
/// 以后 A 线把设置窗口改成 6 页时，可以直接把这两个 TabPage 搬过去。
/// </summary>
internal sealed class TextRulesForm : Form
{
    public TextLayerSettings Settings { get; }

    private readonly string _rulesDir, _dataDir;
    private readonly Action<string>? _preview;
    private readonly DataGridView _rules = new();
    private readonly DataGridView _punct = new();
    private readonly TextBox _search = new();
    private readonly TextBox _test = new();
    private readonly TextBox _testOut = new();
    private readonly ComboBox _english = new();
    private readonly CheckedListBox _packs = new();
    private readonly CheckBox _title = new() { Text = "标题识别（短行后多停一会儿）" };
    private readonly CheckBox _markdown = new() { Text = "Markdown 清理（AI 回答里的 # * 等符号不读）" };
    private readonly Label _status = new();
    private PunctuationTable _builtinPunct;

    private static readonly string[] Types = { "替换", "模板", "读音" };

    public TextRulesForm(TextLayerSettings working, string rulesDir, string dataDir, Action<string>? preview = null)
    {
        Settings = working;
        _rulesDir = rulesDir; _dataDir = dataDir; _preview = preview;
        _builtinPunct = PunctuationTable.Load(Path.Combine(rulesDir, "punctuation.tsv"), null);

        Text = "读法与标点 · 光标朗读";
        StartPosition = FormStartPosition.CenterScreen;
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = Ink.Paper;
        Font = new Font(Ink.FontFamilyName, 11f);   // 大一点：所有者视力不好
        MinimumSize = new Size(S(760), S(560));
        Size = new Size(S(900), S(680));

        var tabs = new TabControl { Dock = DockStyle.Fill };
        tabs.TabPages.Add(BuildRulesPage());
        tabs.TabPages.Add(BuildPunctPage());

        var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = S(48), Padding = new Padding(S(8)) };
        var save = new Button { Text = "保存", AutoSize = true };
        var cancel = new Button { Text = "取消", AutoSize = true };
        save.Click += (_, _) => { if (SaveAll()) { DialogResult = DialogResult.OK; Close(); } };
        cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        _status.AutoSize = true; _status.ForeColor = Ink.Grey4; _status.Padding = new Padding(0, S(6), S(16), 0);
        bottom.Controls.AddRange(new Control[] { save, cancel, _status });
        Controls.Add(tabs);
        Controls.Add(bottom);
        AcceptButton = null; CancelButton = cancel;

        LoadRules();
        LoadPunct();
    }

    private int S(int px) => Ink.S(this, px);
    private string UserRulesPath => Path.Combine(_dataDir, SettingsMigration.UserRulesFile);
    private string UserPunctPath => Path.Combine(_dataDir, SettingsMigration.UserPunctFile);

    // ───────────── 读法页 ─────────────

    private TabPage BuildRulesPage()
    {
        var page = new TabPage("读法") { BackColor = Ink.Paper, Padding = new Padding(S(8)) };
        var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = S(40), WrapContents = false };
        _search.Width = S(200); _search.PlaceholderText = "搜索原文或读作";
        _search.TextChanged += (_, _) => ApplyFilter();
        var add = new Button { Text = "添加", AutoSize = true };
        var del = new Button { Text = "删除", AutoSize = true };
        var imp = new Button { Text = "导入…", AutoSize = true };
        var exp = new Button { Text = "导出…", AutoSize = true };
        add.Click += (_, _) => { int r = _rules.Rows.Add(true, "替换", "", "", "", ""); _rules.CurrentCell = _rules.Rows[r].Cells[2]; _rules.BeginEdit(true); };
        del.Click += (_, _) => { foreach (DataGridViewRow r in _rules.SelectedRows) if (!r.IsNewRow) _rules.Rows.Remove(r); };
        imp.Click += (_, _) => Import();
        exp.Click += (_, _) => Export();
        top.Controls.AddRange(new Control[] { _search, add, del, imp, exp });

        _rules.Dock = DockStyle.Fill;
        _rules.AllowUserToAddRows = false;
        _rules.RowHeadersVisible = false;
        _rules.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _rules.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _rules.BackgroundColor = Ink.PaperPure;
        _rules.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText = "启用", FillWeight = 10 });
        var type = new DataGridViewComboBoxColumn { HeaderText = "类型", FillWeight = 12, FlatStyle = FlatStyle.Flat };
        type.Items.AddRange(Types);
        _rules.Columns.Add(type);
        _rules.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "原文", FillWeight = 22 });
        _rules.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "读作", FillWeight = 22 });
        _rules.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "选项", FillWeight = 16, ToolTipText = "整词、不分大小写，用逗号分开" });
        _rules.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "备注", FillWeight = 18 });
        _rules.CellValueChanged += (_, e) => { if (e.RowIndex >= 0) Validate(_rules.Rows[e.RowIndex]); };
        _rules.CurrentCellDirtyStateChanged += (_, _) => { if (_rules.IsCurrentCellDirty && _rules.CurrentCell is DataGridViewCheckBoxCell) _rules.CommitEdit(DataGridViewDataErrorContexts.Commit); };
        _rules.DataError += (_, e) => e.ThrowException = false;

        // 右侧：规则包、英文策略
        var side = new FlowLayoutPanel { Dock = DockStyle.Right, Width = S(240), FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(S(8), 0, 0, 0) };
        side.Controls.Add(new Label { Text = "内置规则包", AutoSize = true, Font = new Font(Font, FontStyle.Bold) });
        _packs.Width = S(220); _packs.Height = S(70); _packs.CheckOnClick = true; _packs.BorderStyle = BorderStyle.None; _packs.BackColor = Ink.Paper;
        _packs.Items.Add("书名号示例（templates.tsv）", Settings.PackEnabled("templates.tsv"));
        side.Controls.Add(_packs);
        side.Controls.Add(new Label { Text = "英文单词（音色不会英文时）", AutoSize = true, Font = new Font(Font, FontStyle.Bold) });
        _english.DropDownStyle = ComboBoxStyle.DropDownList; _english.Width = S(220);
        _english.Items.AddRange(new object[] { "逐个字母读", "跳过不读", "读作“英文”" });
        _english.SelectedIndex = (int)Settings.EnglishPolicy;
        side.Controls.Add(_english);
        side.Controls.Add(new Label { Text = "缩写（GDP）和单个字母总是逐个读。", AutoSize = true, MaximumSize = new Size(S(220), 0), ForeColor = Ink.Grey3 });

        // 底部：测试框
        var testBox = new TableLayoutPanel { Dock = DockStyle.Bottom, Height = S(150), ColumnCount = 2, RowCount = 2 };
        testBox.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        testBox.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, S(100)));
        testBox.RowStyles.Add(new RowStyle(SizeType.Absolute, S(40)));
        testBox.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _test.Dock = DockStyle.Fill; _test.PlaceholderText = "测试：输入一句话，例如 YYDS！银行在2026年10月30日开门";
        _test.TextChanged += (_, _) => RunTest();
        _testOut.Dock = DockStyle.Fill; _testOut.Multiline = true; _testOut.ReadOnly = true; _testOut.ScrollBars = ScrollBars.Vertical; _testOut.BackColor = Ink.PaperPure;
        var listen = new Button { Text = "试听", Dock = DockStyle.Fill, Enabled = _preview != null };
        listen.Click += (_, _) => { if (_test.Text.Trim().Length > 0) _preview?.Invoke(_test.Text); };
        testBox.Controls.Add(_test, 0, 0); testBox.Controls.Add(listen, 1, 0);
        testBox.Controls.Add(_testOut, 0, 1); testBox.SetColumnSpan(_testOut, 2);
        _english.SelectedIndexChanged += (_, _) => RunTest();
        _packs.ItemCheck += (_, _) => BeginInvoke(RunTest);

        page.Controls.Add(_rules);
        page.Controls.Add(side);
        page.Controls.Add(testBox);
        page.Controls.Add(top);
        return page;
    }

    private void LoadRules()
    {
        _rules.Rows.Clear();
        if (!File.Exists(UserRulesPath)) return;
        foreach (var raw in File.ReadAllLines(UserRulesPath, Encoding.UTF8))
        {
            string line = raw.TrimStart('\uFEFF');
            if (line.Trim().Length == 0 || line.TrimStart().StartsWith('#')) continue;
            var c = line.Split('\t');
            string opts = c.Length > 3 ? c[3] : "";
            bool disabled = opts.Contains("停用");
            opts = string.Join(",", opts.Split(',', '，').Select(o => o.Trim()).Where(o => o.Length > 0 && o != "停用"));
            int r = _rules.Rows.Add(!disabled, Types.Contains(c[0].Trim()) ? c[0].Trim() : "替换",
                c.Length > 1 ? c[1] : "", c.Length > 2 ? c[2] : "", opts, c.Length > 4 ? c[4] : "");
            Validate(_rules.Rows[r]);
        }
    }

    private string RowToTsv(DataGridViewRow r)
    {
        string Cell(int i) => (r.Cells[i].Value?.ToString() ?? "").Replace("\t", " ").Replace("\n", " ");
        bool on = r.Cells[0].Value is true;
        var opts = Cell(4).Split(',', '，').Select(o => o.Trim()).Where(o => o.Length > 0).ToList();
        if (!on) opts.Add("停用");
        return string.Join('\t', Cell(1), Cell(2), Cell(3), string.Join(',', opts), Cell(5));
    }

    /// <summary>写错的行标红，并把原因放在行提示里。</summary>
    private bool Validate(DataGridViewRow r)
    {
        var rule = RuleFile.ParseLine(RowToTsv(r), SettingsMigration.UserRulesFile, r.Index + 1, RuleFile.PriorityUser, out var err);
        r.ErrorText = rule == null ? err ?? "格式不对" : "";
        r.DefaultCellStyle.BackColor = rule == null ? Color.FromArgb(255, 220, 220) : Ink.PaperPure;
        return rule != null;
    }

    private void ApplyFilter()
    {
        string q = _search.Text.Trim();
        foreach (DataGridViewRow r in _rules.Rows)
            r.Visible = q.Length == 0 || (r.Cells[2].Value?.ToString() ?? "").Contains(q, StringComparison.OrdinalIgnoreCase)
                                      || (r.Cells[3].Value?.ToString() ?? "").Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    private string CurrentRulesTsv()
    {
        var sb = new StringBuilder("# 用户读法词库。列：类型\t原文\t读作\t选项\t备注\n");
        foreach (DataGridViewRow r in _rules.Rows) sb.Append(RowToTsv(r)).Append('\n');
        return sb.ToString();
    }

    private void Import()
    {
        using var dlg = new OpenFileDialog { Filter = "读法规则 (*.tsv;*.txt)|*.tsv;*.txt" };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        var f = RuleFile.Parse(File.ReadAllText(dlg.FileName, Encoding.UTF8), Path.GetFileName(dlg.FileName), RuleFile.PriorityUser);
        foreach (var rule in f.Rules)
        {
            var opts = new List<string>();
            if (rule.WholeWord) opts.Add("整词");
            if (rule.IgnoreCase) opts.Add("不分大小写");
            _rules.Rows.Add(rule.Enabled, Types[(int)rule.Type], rule.Source, rule.Target, string.Join(',', opts), rule.Note);
        }
        _status.Text = $"导入 {f.Rules.Count} 条" + (f.Errors.Count > 0 ? $"，跳过写错的 {f.Errors.Count} 行" : "");
        RunTest();
    }

    private void Export()
    {
        using var dlg = new SaveFileDialog { Filter = "读法规则 (*.tsv)|*.tsv", FileName = "我的读法规则.tsv" };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        File.WriteAllText(dlg.FileName, CurrentRulesTsv(), new UTF8Encoding(false));
        _status.Text = "已导出";
    }

    private void CollectSettings()
    {
        Settings.RulePacks["templates.tsv"] = _packs.GetItemChecked(0);
        Settings.EnglishPolicy = (EnglishPolicy)Math.Max(0, _english.SelectedIndex);
        Settings.TitleDetection = _title.Checked;
        Settings.MarkdownClean = _markdown.Checked;
    }

    /// <summary>测试框：用当前表格（还没保存）的规则跑一遍文本层，显示改写后的文字和拼音。</summary>
    private void RunTest()
    {
        string text = _test.Text;
        if (text.Trim().Length == 0) { _testOut.Text = ""; return; }
        try
        {
            CollectSettings();
            string tmp = Path.Combine(Path.GetTempPath(), "gbtts-test-rules.tsv");
            File.WriteAllText(tmp, CurrentRulesTsv(), new UTF8Encoding(false));
            var pipe = TextPipeline.Load(_rulesDir, tmp, Settings.ToPipelineOptions(voiceSpeaksEnglish: false));
            var res = pipe.Process(text);
            var sb = new StringBuilder();
            sb.Append("改写后：").Append(string.Concat(res.Segments.Select(s => s.Kind == SegKind.ForcedPinyin ? "「" + s.Text + "」" : s.Text)));
            var fe = PolyphoneFrontend.Shared(300);
            if (fe != null)
            {
                sb.Append("\r\n拼音：");
                foreach (var s in res.Segments)
                    sb.Append(s.Kind == SegKind.ForcedPinyin ? s.Text : string.Join(' ', fe.G2P(s.Text).Select(x => x.Pinyin))).Append(' ');
            }
            _testOut.Text = sb.ToString().TrimEnd();
        }
        catch (Exception ex) { _testOut.Text = "测试出错：" + ex.GetType().Name; }
    }

    // ───────────── 标点页 ─────────────

    private TabPage BuildPunctPage()
    {
        var page = new TabPage("节奏（标点）") { BackColor = Ink.Paper, Padding = new Padding(S(8)) };
        var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = S(72), FlowDirection = FlowDirection.TopDown };
        _title.AutoSize = true; _markdown.AutoSize = true;
        _title.Checked = Settings.TitleDetection; _markdown.Checked = Settings.MarkdownClean;
        top.Controls.AddRange(new Control[] { _title, _markdown });

        _punct.Dock = DockStyle.Fill;
        _punct.AllowUserToAddRows = false; _punct.AllowUserToDeleteRows = false;
        _punct.RowHeadersVisible = false;
        _punct.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _punct.BackgroundColor = Ink.PaperPure;
        _punct.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "符号", ReadOnly = true, FillWeight = 18 });
        _punct.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "前停（秒）", FillWeight = 18 });
        _punct.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "后停（秒）", FillWeight = 18 });
        _punct.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "类别", ReadOnly = true, FillWeight = 18 });
        _punct.Columns.Add(new DataGridViewButtonColumn { HeaderText = "", Text = "恢复默认", UseColumnTextForButtonValue = true, FillWeight = 16 });
        _punct.CellValueChanged += (_, e) => { if (e.RowIndex >= 0) MarkPunct(_punct.Rows[e.RowIndex]); };
        _punct.CellContentClick += (_, e) =>
        {
            if (e.RowIndex < 0 || e.ColumnIndex != 4) return;
            var row = _punct.Rows[e.RowIndex];
            var b = _builtinPunct.Builtin(row.Cells[0].Value?.ToString() ?? "");
            if (b == null) return;
            row.Cells[1].Value = Fmt(b.Before); row.Cells[2].Value = Fmt(b.After);
        };
        page.Controls.Add(_punct);
        page.Controls.Add(top);
        return page;
    }

    private static string Fmt(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    private void LoadPunct()
    {
        var t = PunctuationTable.Load(Path.Combine(_rulesDir, "punctuation.tsv"), File.Exists(UserPunctPath) ? UserPunctPath : null);
        _punct.Rows.Clear();
        foreach (var b in _builtinPunct.Entries)
        {
            t.TryGet(b.Symbol, out var cur);
            cur ??= b;
            int r = _punct.Rows.Add(b.Symbol, Fmt(cur.Before), Fmt(cur.After), b.Kind.ToString());
            MarkPunct(_punct.Rows[r]);
        }
    }

    private bool TryRow(DataGridViewRow r, out double before, out double after) =>
        double.TryParse(r.Cells[1].Value?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out before) &
        double.TryParse(r.Cells[2].Value?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out after);

    /// <summary>改过的行加粗；写错（不是 0～5 的数字）标红。</summary>
    private void MarkPunct(DataGridViewRow r)
    {
        var b = _builtinPunct.Builtin(r.Cells[0].Value?.ToString() ?? "");
        bool ok = TryRow(r, out double be, out double af) && be is >= 0 and <= 5 && af is >= 0 and <= 5;
        bool changed = ok && b != null && (Math.Abs(be - b.Before) > 1e-9 || Math.Abs(af - b.After) > 1e-9);
        r.DefaultCellStyle.Font = changed ? new Font(Font, FontStyle.Bold) : Font;
        r.DefaultCellStyle.BackColor = ok ? Ink.PaperPure : Color.FromArgb(255, 220, 220);
        r.ErrorText = ok ? "" : "停顿要写 0～5 之间的秒数，例如 0.15";
    }

    // ───────────── 保存 ─────────────

    private bool SaveAll()
    {
        _rules.EndEdit(); _punct.EndEdit();
        int bad = _rules.Rows.Cast<DataGridViewRow>().Count(r => !Validate(r));
        var changed = new List<PunctEntry>();
        foreach (DataGridViewRow r in _punct.Rows)
        {
            var b = _builtinPunct.Builtin(r.Cells[0].Value?.ToString() ?? "");
            if (b == null) continue;
            if (!TryRow(r, out double be, out double af) || be is < 0 or > 5 || af is < 0 or > 5)
            { _status.Text = "标点表有写错的行（红色），请改正后再保存"; return false; }
            if (Math.Abs(be - b.Before) > 1e-9 || Math.Abs(af - b.After) > 1e-9)
                changed.Add(b with { Before = be, After = af });
        }
        try
        {
            Directory.CreateDirectory(_dataDir);
            Atomic(UserRulesPath, CurrentRulesTsv());
            if (changed.Count > 0) Atomic(UserPunctPath, PunctuationTable.ToUserTsv(changed));
            else if (File.Exists(UserPunctPath)) Atomic(UserPunctPath, PunctuationTable.ToUserTsv(changed));
        }
        catch (Exception ex)
        {
            Log.Write("保存读法/标点表失败: " + ex.GetType().Name);
            _status.Text = "保存失败：文件夹不能写入";
            return false;
        }
        CollectSettings();
        if (bad > 0) Log.Write($"读法规则有 {bad} 行写错，已保存但不会生效");
        return true;
    }

    private static void Atomic(string path, string content)
    {
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, content, new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
    }
}

using System.Drawing;
using System.Windows.Forms;
using CaretReader.Core.Pacing;
namespace CaretReader.App;

/// <summary>语速（字/分）与本音色微调。数据读写 Pacer.Current.Store（C 线接口）。</summary>
internal sealed class PacingForm : Form
{
    private readonly NumericUpDown _cpm = new() { Minimum = PacingStore.MinTargetCpm, Maximum = PacingStore.MaxTargetCpm, Increment = 10 };
    private readonly NumericUpDown _spd = new() { Minimum = -20, Maximum = 20, Increment = 1 };
    private readonly NumericUpDown _pause = new() { Minimum = 0.5m, Maximum = 2.0m, Increment = 0.1m, DecimalPlaces = 1 };
    private readonly Label _state = new() { AutoSize = true };
    private readonly string _voice;

    public PacingForm(string voiceId)
    {
        _voice = voiceId;
        var st = Pacer.Current.Store;
        Text = "语速与停顿"; FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent; ClientSize = new Size(360, 230); Font = new Font("Microsoft YaHei UI", 10f);
        _cpm.Value = st.TargetCpm ?? PacingStore.DefaultTargetCpm;
        var o = st.GetOverride(voiceId);
        _spd.Value = (decimal)Math.Clamp(Math.Round((o.Speed - 1) * 100), -20, 20);
        _pause.Value = (decimal)Math.Clamp(o.Pause, 0.5, 2.0);
        Add(0, "语速（字/分）", _cpm); Add(1, "本音色语速微调 %", _spd); Add(2, "本音色停顿倍率", _pause);
        _state.Location = new Point(16, 120); Controls.Add(_state); Refresh2();
        var reset = new Button { Text = "恢复跟随全局", Bounds = new Rectangle(16, 170, 120, 32) };
        reset.Click += (_, _) => { st.SetOverride(_voice, null); _spd.Value = 0; _pause.Value = 1; Refresh2(); };
        var ok = new Button { Text = "保存", Bounds = new Rectangle(250, 170, 90, 32), DialogResult = DialogResult.OK };
        ok.Click += (_, _) =>
        {
            st.TargetCpm = (int)_cpm.Value;
            var v = new VoiceOverride { Speed = 1 + (double)_spd.Value / 100, Pause = (double)_pause.Value };
            v.Clamp(); st.SetOverride(_voice, v.IsDefault ? null : v); st.Save();
        };
        Controls.AddRange(new Control[] { reset, ok }); AcceptButton = ok;
    }
    private void Add(int i, string t, Control c)
    {
        int y = 20 + i * 32;
        Controls.Add(new Label { Text = t, AutoSize = true, Location = new Point(16, y + 4) });
        c.Bounds = new Rectangle(220, y, 120, 28); Controls.Add(c);
    }
    private void Refresh2() => _state.Text = Pacer.Current.Store.GetOverride(_voice).IsDefault ? "本音色：跟随全局" : "本音色：已单独调整";
}

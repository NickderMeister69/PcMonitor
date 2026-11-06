namespace PcMonitor;

public class SettingsForm : Form
{
    private readonly AppSettings _s;
    private readonly NumericUpDown _cpu, _gpu, _disk, _ram, _cool;
    private readonly CheckBox _warn, _tray, _startMin, _autostart;
    private readonly ComboBox _interval, _source;
    private readonly bool _autostartWas;
    private static readonly int[] Intervals = { 500, 1000, 2000, 5000 };

    public SettingsForm(AppSettings s)
    {
        _s = s;
        Text = "Einstellungen";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = Theme.Card;
        ForeColor = Theme.Text;
        Font = new Font(Theme.FontName, 10f);
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(18);

        var t = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Fill, BackColor = Theme.Card };
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        void Head(string text)
        {
            var l = new Label { Text = text, AutoSize = true, Font = new Font(Theme.FontNameSemi, 11f), ForeColor = Theme.Cpu, Margin = new Padding(0, 12, 0, 6) };
            t.Controls.Add(l); t.SetColumnSpan(l, 2);
        }
        NumericUpDown Num(string label, int val, int min, int max, string unit)
        {
            t.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 24, 6) });
            var p = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0), WrapContents = false };
            var n = new NumericUpDown { Minimum = min, Maximum = max, Value = Math.Clamp(val, min, max), Width = 80, BackColor = Theme.Bg, ForeColor = Theme.Text, BorderStyle = BorderStyle.FixedSingle };
            p.Controls.Add(n);
            p.Controls.Add(new Label { Text = unit, AutoSize = true, Margin = new Padding(6, 6, 0, 0), ForeColor = Theme.Muted });
            t.Controls.Add(p);
            return n;
        }
        CheckBox Chk(string label, bool val)
        {
            var c = new CheckBox { Text = label, Checked = val, AutoSize = true, Margin = new Padding(0, 6, 0, 6) };
            t.Controls.Add(c); t.SetColumnSpan(c, 2);
            return c;
        }

        Head("Sensor-Quelle");
        t.Controls.Add(new Label { Text = "Werte lesen über", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 24, 6) });
        _source = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260, BackColor = Theme.Bg, ForeColor = Theme.Text, FlatStyle = FlatStyle.Flat };
        _source.Items.AddRange(new object[] { "Automatisch (HWiNFO, sonst intern)", "Nur intern (LibreHardwareMonitor)", "Nur HWiNFO" });
        _source.SelectedIndex = Math.Clamp((int)s.Source, 0, 2);
        t.Controls.Add(_source);
        var help = new LinkLabel { Text = "Wie verbinde ich HWiNFO?", AutoSize = true, LinkColor = Theme.Cpu, ActiveLinkColor = Theme.Text, Margin = new Padding(0, 2, 0, 4) };
        help.LinkClicked += (_, _) => MessageBox.Show(this, MainForm.HwinfoHelpText, "HWiNFO verbinden", MessageBoxButtons.OK, MessageBoxIcon.Information);
        t.Controls.Add(new Label { AutoSize = true });
        t.Controls.Add(help);

        Head("Warngrenzen");
        _cpu = Num("CPU-Temperatur", s.CpuTempLimit, 40, 110, "°C");
        _gpu = Num("GPU-Temperatur", s.GpuTempLimit, 40, 110, "°C");
        _disk = Num("SSD/HDD-Temperatur", s.DiskTempLimit, 30, 90, "°C");
        _ram = Num("RAM-Auslastung", s.RamLimit, 50, 100, "%");

        Head("Benachrichtigungen");
        _warn = Chk("Windows-Benachrichtigung bei Überschreitung", s.WarningsEnabled);
        _cool = Num("Höchstens alle", s.WarningCooldownMin, 1, 120, "Minuten pro Sensor");

        Head("Allgemein");
        t.Controls.Add(new Label { Text = "Aktualisierung", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 24, 6) });
        _interval = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 140, BackColor = Theme.Bg, ForeColor = Theme.Text, FlatStyle = FlatStyle.Flat };
        _interval.Items.AddRange(new object[] { "0,5 Sekunden", "1 Sekunde", "2 Sekunden", "5 Sekunden" });
        _interval.SelectedIndex = Math.Max(0, Array.IndexOf(Intervals, s.IntervalMs));
        t.Controls.Add(_interval);
        _tray = Chk("Beim Schließen (X) im Infobereich (Tray) weiterlaufen", s.MinimizeToTray);
        _startMin = Chk("Minimiert starten", s.StartMinimized);
        _autostartWas = Autostart.IsEnabled();
        _autostart = Chk("Mit Windows starten (ohne UAC-Abfrage)", _autostartWas);

        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 18, 0, 0) };
        var ok = new Button { Text = "Speichern", DialogResult = DialogResult.OK, AutoSize = true, FlatStyle = FlatStyle.Flat, BackColor = Theme.Cpu, ForeColor = Theme.Bg, Padding = new Padding(10, 4, 10, 4) };
        ok.FlatAppearance.BorderSize = 0;
        var cancel = new Button { Text = "Abbrechen", DialogResult = DialogResult.Cancel, AutoSize = true, FlatStyle = FlatStyle.Flat, BackColor = Theme.Track, ForeColor = Theme.Text, Padding = new Padding(10, 4, 10, 4) };
        cancel.FlatAppearance.BorderSize = 0;
        buttons.Controls.Add(ok); buttons.Controls.Add(cancel);
        t.Controls.Add(buttons); t.SetColumnSpan(buttons, 2);

        Controls.Add(t);
        AcceptButton = ok; CancelButton = cancel;
        ok.Click += (_, _) => Apply();
    }

    private void Apply()
    {
        _s.CpuTempLimit = (int)_cpu.Value;
        _s.GpuTempLimit = (int)_gpu.Value;
        _s.DiskTempLimit = (int)_disk.Value;
        _s.RamLimit = (int)_ram.Value;
        _s.WarningsEnabled = _warn.Checked;
        _s.WarningCooldownMin = (int)_cool.Value;
        _s.MinimizeToTray = _tray.Checked;
        _s.StartMinimized = _startMin.Checked;
        _s.Source = (SensorSource)Math.Max(0, _source.SelectedIndex);
        _s.IntervalMs = Intervals[Math.Max(0, _interval.SelectedIndex)];
        if (_autostart.Checked != _autostartWas && !Autostart.Set(_autostart.Checked))
            MessageBox.Show(this, "Autostart konnte nicht geändert werden.", "PC Monitor", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }
}

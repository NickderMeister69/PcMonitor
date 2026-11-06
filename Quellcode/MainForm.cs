using System.Diagnostics;
using System.Media;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace PcMonitor;

public class MainForm : Form
{
    private const int HistLen = 180;
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly DashboardView _view = new() { Dock = DockStyle.Fill };
    private readonly NotifyIcon _tray = new();
    private readonly HardwareService _hw = new();
    private readonly Dictionary<string, DateTime> _lastWarn = new();
    private readonly bool _startHidden;
    private bool _reallyExit;
    private IntPtr _trayIconHandle = IntPtr.Zero;
    private string _lastTrayText = "";
    private SensorsForm _sensors;
    private readonly System.Windows.Forms.Timer _frameTimer = new() { Interval = 15 };

    private readonly History _cpuT = new(HistLen), _gpuT = new(HistLen), _cpuL = new(HistLen),
        _gpuL = new(HistLen), _ramL = new(HistLen), _netD = new(HistLen), _netU = new(HistLen);

    public MainForm(bool startHidden)
    {
        _startHidden = startHidden || _settings.StartMinimized;
        Text = "PC Monitor";
        Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath);
        BackColor = Theme.Bg;
        AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(LogicalToDeviceUnits(1280), LogicalToDeviceUnits(860));
        MinimumSize = new Size(LogicalToDeviceUnits(1120), LogicalToDeviceUnits(780));

        _view.Settings = _settings;
        _view.DriverMissing = !IsPawnIoInstalled();
        _view.CpuTemp = _cpuT; _view.GpuTemp = _gpuT; _view.CpuLoad = _cpuL; _view.GpuLoad = _gpuL;
        _view.RamLoad = _ramL; _view.NetDown = _netD; _view.NetUp = _netU;
        _view.SettingsClicked += (_, _) => OpenSettings();
        _view.BannerClicked += (_, _) => { if (_view.BannerKind == 2) ShowHwinfoHelp(); else InstallPawnIo(); };
        _view.SensorsClicked += (_, _) => OpenSensors();
        Controls.Add(_view);

        // Tray
        _tray.Icon = Icon;
        _tray.Text = "PC Monitor";
        _tray.Visible = true;
        var menu = new ContextMenuStrip { Renderer = new ToolStripProfessionalRenderer(new DarkColors()) };
        menu.ForeColor = Theme.Text;
        menu.Items.Add("Öffnen", null, (_, _) => ShowFromTray());
        menu.Items.Add("Einstellungen", null, (_, _) => { ShowFromTray(); OpenSettings(); });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Beenden", null, (_, _) => { _reallyExit = true; Close(); });
        _tray.ContextMenuStrip = menu;
        _tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowFromTray(); };

        _hw.IntervalMs = _settings.IntervalMs;
        _hw.Mode = _settings.Source;
        _view.IntervalMs = _settings.IntervalMs;
        // ~60 Bilder/s für flüssige Animationen – nur solange das Fenster sichtbar ist
        _frameTimer.Tick += (_, _) =>
        {
            if (Visible && WindowState != FormWindowState.Minimized) _view.Invalidate();
        };
        _frameTimer.Start();

        // Zweiter Start der exe holt dieses Fenster nach vorne
        var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, Program.ShowEventName);
        new Thread(() =>
        {
            while (true)
            {
                showEvent.WaitOne();
                try { if (IsHandleCreated) BeginInvoke(ShowFromTray); } catch { }
            }
        }) { IsBackground = true, Name = "ShowSignal" }.Start();
        _hw.Updated += s =>
        {
            if (IsDisposed || !IsHandleCreated) return;
            try { BeginInvoke(() => OnData(s)); } catch { }
        };
    }

    protected override void SetVisibleCore(bool value)
    {
        if (_startHidden && !IsHandleCreated) { CreateHandle(); value = false; }
        base.SetVisibleCore(value);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // Dunkle Titelleiste (Windows 10 20H1+ / 11)
        int on = 1;
        DwmSetWindowAttribute(Handle, 20, ref on, 4);
        int caption = ColorTranslator.ToWin32(Theme.Bg);
        DwmSetWindowAttribute(Handle, 35, ref caption, 4);
        _hw.Start();
    }

    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int val, int size);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr handle);

    private void OnData(Snapshot s)
    {
        _cpuT.Add(s.CpuTemp); _gpuT.Add(s.GpuTemp);
        _cpuL.Add(s.CpuLoad); _gpuL.Add(s.GpuLoad); _ramL.Add((float)s.RamPercent);
        _netD.Add((float)s.NetDownBps); _netU.Add((float)s.NetUpBps);
        _view.Data = s;
        if (_sensors != null && !_sensors.IsDisposed && _sensors.Visible) _sensors.UpdateData(s);
        CheckWarnings(s);
        UpdateTray(s);
        _view.NotifySample();
    }

    // ------------------------------------------------------------ Warnungen
    private void CheckWarnings(Snapshot s)
    {
        var st = _settings;
        Check("cpu", s.CpuTemp, st.CpuTempLimit, v => $"CPU-Temperatur: {v:0} °C (Grenze {st.CpuTempLimit} °C)");
        Check("gpu", s.GpuTemp, st.GpuTempLimit, v => $"GPU-Temperatur: {v:0} °C (Grenze {st.GpuTempLimit} °C)");
        Check("ram", (float)s.RamPercent, st.RamLimit, v => $"Arbeitsspeicher zu {v:0} % belegt (Grenze {st.RamLimit} %)");
        var hottest = s.StorageTemps.OrderByDescending(t => t.Temp).FirstOrDefault();
        Check("disk", hottest?.Temp, st.DiskTempLimit, v => $"{hottest?.Name}: {v:0} °C (Grenze {st.DiskTempLimit} °C)");
    }

    private void Check(string key, float? value, int limit, Func<float, string> text)
    {
        var alarms = _view.ActiveAlarms;
        if (value == null) { alarms.Remove(key); return; }
        if (value >= limit) alarms.Add(key);
        else if (value < limit - 3) alarms.Remove(key); // Hysterese gegen Flackern

        if (!_settings.WarningsEnabled || value < limit) return;
        if (_lastWarn.TryGetValue(key, out var last) && (DateTime.Now - last).TotalMinutes < _settings.WarningCooldownMin) return;
        _lastWarn[key] = DateTime.Now;
        _tray.ShowBalloonTip(8000, "PC Monitor – Warnung", text(value.Value), ToolTipIcon.Warning);
        SystemSounds.Exclamation.Play();
    }

    // ------------------------------------------------------------ Tray
    private void UpdateTray(Snapshot s)
    {
        var txt = $"CPU {(s.CpuTemp == null ? "–" : s.CpuTemp.Value.ToString("0") + "°C")} · " +
                  $"GPU {(s.GpuTemp == null ? "–" : s.GpuTemp.Value.ToString("0") + "°C")} · RAM {s.RamPercent:0}%";
        if (txt.Length > 120) txt = txt[..120];
        if (txt == _lastTrayText) return;
        _lastTrayText = txt;
        _tray.Text = txt;

        // Tray-Icon zeigt die höchste Temperatur als Zahl
        float? t = new[] { s.CpuTemp, s.GpuTemp }.Where(v => v != null).DefaultIfEmpty(null).Max();
        if (t == null) return;
        int lim = Math.Min(_settings.CpuTempLimit, _settings.GpuTempLimit);
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            using var bg = new SolidBrush(Theme.Card);
            using var path = Theme.Rounded(new RectangleF(0, 0, 32, 32), 7);
            g.FillPath(bg, path);
            using var f = new Font("Segoe UI", t >= 100 ? 15 : 19, FontStyle.Bold, GraphicsUnit.Pixel);
            using var b = new SolidBrush(Theme.TempColor(t, lim));
            using var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString(t.Value.ToString("0"), f, b, new RectangleF(-2, 0, 36, 33), sf);
        }
        var h = bmp.GetHicon();
        var old = _trayIconHandle;
        _tray.Icon = Icon.FromHandle(h);
        _trayIconHandle = h;
        if (old != IntPtr.Zero) DestroyIcon(old);
    }

    private void ShowFromTray()
    {
        ShowInTaskbar = true;
        Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
        _view.Invalidate();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        // Minimieren bleibt ganz normal in der Taskleiste – nur das X schickt die App in den Tray.
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_reallyExit && e.CloseReason == CloseReason.UserClosing && _settings.MinimizeToTray)
        {
            e.Cancel = true;
            Hide();
            if (!_settings.StartMinimized && !_lastWarn.ContainsKey("_hint"))
            {
                _lastWarn["_hint"] = DateTime.Now;
                _tray.ShowBalloonTip(4000, "PC Monitor läuft weiter", "Die App überwacht im Hintergrund. Beenden über Rechtsklick auf das Tray-Symbol.", ToolTipIcon.Info);
            }
            return;
        }
        _frameTimer.Stop();
        _tray.Visible = false;
        _hw.Dispose();
        if (_trayIconHandle != IntPtr.Zero) DestroyIcon(_trayIconHandle);
        base.OnFormClosing(e);
    }

    // ------------------------------------------------------------ Einstellungen / Treiber
    private void OpenSettings()
    {
        using var f = new SettingsForm(_settings);
        if (f.ShowDialog(this) == DialogResult.OK)
        {
            _settings.Save();
            _hw.IntervalMs = _settings.IntervalMs;
            _hw.Mode = _settings.Source;
            _view.IntervalMs = _settings.IntervalMs;
            _lastWarn.Clear();
            _view.Invalidate();
        }
    }

    private void OpenSensors()
    {
        if (_sensors == null || _sensors.IsDisposed)
        {
            _sensors = new SensorsForm { Icon = Icon };
            _sensors.Show(this);
            if (_view.Data != null) _sensors.UpdateData(_view.Data);
        }
        else _sensors.Activate();
    }

    public static string HwinfoHelpText =>
        "So nutzt PC Monitor die Sensorwerte von HWiNFO:\n\n" +
        "1. HWiNFO (kostenlos, hwinfo.com) installieren und starten – Modus „Sensors-only“.\n" +
        "2. Im Sensorfenster unten auf das Zahnrad (Einstellungen) klicken.\n" +
        "3. Haken bei „Shared Memory Support“ setzen und mit OK bestätigen.\n" +
        "4. HWiNFO geöffnet lassen (kann minimiert im Tray laufen).\n\n" +
        "PC Monitor erkennt HWiNFO dann automatisch – oben steht „Quelle: HWiNFO“.\n\n" +
        "Hinweis: In der kostenlosen HWiNFO-Version schaltet sich Shared Memory nach 12 Stunden ab " +
        "und muss dann wieder aktiviert werden. In der Zwischenzeit nutzt PC Monitor (Einstellung „Automatisch“) die internen Sensoren.";

    private void ShowHwinfoHelp()
    {
        var r = MessageBox.Show(this, HwinfoHelpText + "\n\nDownload-Seite von HWiNFO jetzt öffnen?", "HWiNFO verbinden",
            MessageBoxButtons.YesNo, MessageBoxIcon.Information);
        if (r == DialogResult.Yes)
            try { Process.Start(new ProcessStartInfo("https://www.hwinfo.com/download/") { UseShellExecute = true }); } catch { }
    }

    private static bool IsPawnIoInstalled()
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO");
            if (k != null) return true;
            using var svc = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\PawnIO");
            return svc != null;
        }
        catch { return true; }
    }

    private void InstallPawnIo()
    {
        var r = MessageBox.Show(this,
            "Für CPU-Temperatur und Mainboard-Lüfter wird der kostenlose, signierte Treiber „PawnIO“ benötigt " +
            "(den nutzt auch LibreHardwareMonitor).\n\n" +
            "Ja = automatisch über winget installieren\nNein = Download-Seite pawnio.eu öffnen\n\n" +
            "Danach PC Monitor bitte neu starten.",
            "PawnIO-Treiber installieren", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Information);
        try
        {
            if (r == DialogResult.Yes)
                Process.Start(new ProcessStartInfo("cmd.exe",
                    "/k winget install --id namazso.PawnIO -e --accept-package-agreements --accept-source-agreements && echo. && echo Fertig! Bitte PC Monitor neu starten.")
                { UseShellExecute = true });
            else if (r == DialogResult.No)
                Process.Start(new ProcessStartInfo("https://pawnio.eu") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "Konnte nicht gestartet werden:\n" + ex.Message, "PC Monitor");
        }
    }

    private class DarkColors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Theme.Card;
        public override Color ImageMarginGradientBegin => Theme.Card;
        public override Color ImageMarginGradientMiddle => Theme.Card;
        public override Color ImageMarginGradientEnd => Theme.Card;
        public override Color MenuItemSelected => Theme.Track;
        public override Color MenuItemBorder => Theme.Track;
        public override Color MenuBorder => Theme.CardBorder;
        public override Color SeparatorDark => Theme.CardBorder;
        public override Color SeparatorLight => Theme.CardBorder;
    }
}

using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace PcMonitor;

/// <summary>Zeichnet das komplette Dashboard (selbst gezeichnet, flackerfrei, DPI-skaliert).</summary>
public class DashboardView : Control
{
    public Snapshot Data;
    public AppSettings Settings;
    public bool DriverMissing;
    public History CpuTemp, GpuTemp, CpuLoad, GpuLoad, RamLoad, NetDown, NetUp;
    public HashSet<string> ActiveAlarms = new();

    public event EventHandler SettingsClicked;
    public event EventHandler BannerClicked;
    public event EventHandler SensorsClicked;
    public int BannerKind; // 1 = PawnIO, 2 = HWiNFO

    private RectangleF _btnSettings, _btnSensors, _banner;
    private bool _hoverSettings, _hoverSensors, _hoverBanner;
    private float k = 1f;
    private readonly Dictionary<string, Font> _fonts = new();
    private float _fontK = -1;

    // ------------------------------------------------------------ Animation
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private double _lastFrame, _sampleAt = -10;
    private float _dt;
    private readonly Dictionary<string, float> _anim = new();
    public int IntervalMs = 1000;

    /// <summary>Wird bei jedem neuen Messwert aufgerufen (für das weiche Scrollen der Graphen).</summary>
    public void NotifySample() => _sampleAt = _clock.Elapsed.TotalSeconds;

    private double ScrollFrac => Math.Clamp((_clock.Elapsed.TotalSeconds - _sampleAt) / (IntervalMs / 1000.0), 0, 1);

    /// <summary>Gleitet weich (exponentiell) zum Zielwert. Jeder Schlüssel nur einmal pro Frame verwenden.</summary>
    private float? A(string key, float? target, float tau = 0.35f)
    {
        if (target == null || float.IsNaN(target.Value)) { _anim.Remove(key); return null; }
        if (!_anim.TryGetValue(key, out var cur)) { _anim[key] = target.Value; return target; }
        cur += (target.Value - cur) * (1 - MathF.Exp(-_dt / tau));
        if (Math.Abs(target.Value - cur) < 0.0005f * Math.Max(1, Math.Abs(target.Value))) cur = target.Value;
        _anim[key] = cur;
        return cur;
    }
    private double Ad(string key, double target, float tau = 0.35f) => A(key, (float)target, tau) ?? target;

    /// <summary>Erzeugt eine geglättete Kopie des aktuellen Messwerts für die Anzeige.</summary>
    private Snapshot Smooth(Snapshot d)
    {
        var o = new Snapshot
        {
            CpuName = d.CpuName, GpuName = d.GpuName, CpuTempLabel = d.CpuTempLabel, Source = d.Source, HwinfoMissing = d.HwinfoMissing,
            CpuTemp = A("cpuT", d.CpuTemp, 0.8f), CpuLoad = A("cpuL", d.CpuLoad), CpuClock = A("cpuC", d.CpuClock), CpuPower = A("cpuP", d.CpuPower),
            GpuTemp = A("gpuT", d.GpuTemp), GpuHotSpot = A("gpuH", d.GpuHotSpot), GpuLoad = A("gpuL", d.GpuLoad), GpuClock = A("gpuC", d.GpuClock),
            GpuMemClock = A("gpuM", d.GpuMemClock), GpuPower = A("gpuP", d.GpuPower), GpuVramUsed = A("gpuV", d.GpuVramUsed), GpuVramTotal = d.GpuVramTotal,
            RamUsedGb = Ad("ramU", d.RamUsedGb), RamTotalGb = d.RamTotalGb, RamPercent = Ad("ramP", d.RamPercent),
            NetDownBps = Ad("netD", d.NetDownBps, 0.45f), NetUpBps = Ad("netU", d.NetUpBps, 0.45f),
            Raw = d.Raw,
        };
        foreach (var f in d.Fans)
            o.Fans.Add(new FanInfo { Name = f.Name, Rpm = A("fan" + f.Name, f.Rpm) ?? f.Rpm, Percent = A("fanp" + f.Name, f.Percent) });
        foreach (var t in d.StorageTemps)
            o.StorageTemps.Add(new StorageTemp { Name = t.Name, Temp = A("st" + t.Name, t.Temp) ?? t.Temp });
        foreach (var dr in d.Drives)
            o.Drives.Add(new DriveUsage { Letter = dr.Letter, Label = dr.Label, TotalGb = dr.TotalGb, UsedGb = Ad("dr" + dr.Letter, dr.UsedGb) });
        return o;
    }

    public DashboardView()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        BackColor = Theme.Bg;
    }

    // ------------------------------------------------------------ Hilfen
    private float S(float v) => v * k;

    private Font F(float px, bool bold = false)
    {
        if (_fontK != k) { foreach (var f in _fonts.Values) f.Dispose(); _fonts.Clear(); _fontK = k; }
        var key = px + (bold ? "b" : "");
        if (!_fonts.TryGetValue(key, out var font))
        {
            font = new Font(bold ? Theme.FontNameSemi : Theme.FontName, px * k, GraphicsUnit.Pixel);
            _fonts[key] = font;
        }
        return font;
    }

    private static void Txt(Graphics g, string s, Font f, Color c, RectangleF r,
        StringAlignment h = StringAlignment.Near, StringAlignment v = StringAlignment.Center)
    {
        using var sf = new StringFormat { Alignment = h, LineAlignment = v, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
        using var b = new SolidBrush(c);
        g.DrawString(s, f, b, r, sf);
    }

    private void CardBg(Graphics g, RectangleF r, bool alarm = false)
    {
        using var path = Theme.Rounded(r, S(14));
        using var b = new SolidBrush(Theme.Card);
        g.FillPath(b, path);
        using var p = new Pen(alarm ? Theme.Hot : Theme.CardBorder, alarm ? S(2) : S(1));
        g.DrawPath(p, path);
    }

    private void CardTitle(Graphics g, RectangleF r, string title, Color accent, string sub = null)
    {
        using (var b = new SolidBrush(accent))
            g.FillEllipse(b, r.X + S(18), r.Y + S(21), S(8), S(8));
        Txt(g, title, F(14, true), Theme.Text, new RectangleF(r.X + S(32), r.Y + S(12), r.Width - S(44), S(26)));
        if (sub != null)
            Txt(g, sub, F(11.5f), Theme.Muted, new RectangleF(r.X + S(32), r.Y + S(34), r.Width - S(44), S(18)));
    }

    private void Bar(Graphics g, RectangleF r, double pct, Color c)
    {
        using (var path = Theme.Rounded(r, r.Height / 2))
        using (var b = new SolidBrush(Theme.Track)) g.FillPath(b, path);
        var w = (float)(r.Width * Math.Clamp(pct, 0, 100) / 100);
        if (w < r.Height) w = pct > 0 ? r.Height : 0;
        if (w <= 0) return;
        using var path2 = Theme.Rounded(new RectangleF(r.X, r.Y, w, r.Height), r.Height / 2);
        using var b2 = new SolidBrush(c);
        g.FillPath(b2, path2);
    }

    private void Ring(Graphics g, RectangleF r, double frac, Color c, string big, string unit, string caption)
    {
        float th = S(11);
        var rr = new RectangleF(r.X + th / 2, r.Y + th / 2, r.Width - th, r.Height - th);
        using (var p = new Pen(Theme.Track, th) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            g.DrawArc(p, rr, 135, 270);
        frac = Math.Clamp(frac, 0, 1);
        if (frac > 0.003)
        {
            using var p = new Pen(c, th) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawArc(p, rr, 135, (float)(270 * frac));
        }
        var bigF = F(r.Width * 0.24f / k, true);
        var sz = g.MeasureString(big, bigF);
        var uF = F(r.Width * 0.11f / k);
        var usz = g.MeasureString(unit, uF);
        float total = sz.Width + usz.Width - S(8);
        float x = r.X + (r.Width - total) / 2;
        float y = r.Y + r.Height / 2 - sz.Height / 2 - S(4);
        using (var b = new SolidBrush(Theme.Text)) g.DrawString(big, bigF, b, x, y);
        using (var b = new SolidBrush(Theme.Muted)) g.DrawString(unit, uF, b, x + sz.Width - S(8), y + sz.Height * 0.18f);
        Txt(g, caption, F(11), Theme.Muted, new RectangleF(r.X, r.Bottom - S(30), r.Width, S(18)), StringAlignment.Center);
    }

    private float StatRow(Graphics g, float x, float y, float w, string label, string value, Color? vc = null)
    {
        Txt(g, label, F(12), Theme.Muted, new RectangleF(x, y, w, S(20)));
        Txt(g, value, F(13, true), vc ?? Theme.Text, new RectangleF(x, y, w, S(20)), StringAlignment.Far);
        return y + S(24);
    }

    private static string N(float? v, string fmt, string unit) => v == null ? "–" : v.Value.ToString(fmt) + unit;

    public static string Speed(double bps)
    {
        double bits = bps * 8;
        if (bits >= 1e9) return (bits / 1e9).ToString("0.00") + " Gbit/s";
        if (bits >= 1e6) return (bits / 1e6).ToString(bits >= 1e8 ? "0" : "0.0") + " Mbit/s";
        if (bits >= 1e3) return (bits / 1e3).ToString("0") + " kbit/s";
        return bits.ToString("0") + " bit/s";
    }

    // ------------------------------------------------------------ Zeichnen
    protected override void OnPaint(PaintEventArgs e)
    {
        try { PaintAll(e); }
        catch (Exception ex) { Log.Write("Zeichnen", ex); }
    }

    private void PaintAll(PaintEventArgs e)
    {
        k = DeviceDpi / 96f;
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        g.Clear(Theme.Bg);

        double now = _clock.Elapsed.TotalSeconds;
        _dt = (float)Math.Clamp(now - _lastFrame, 0, 0.1);
        _lastFrame = now;
        var d = Data != null ? Smooth(Data) : new Snapshot();
        var st = Settings ?? new AppSettings();
        float pad = S(20), gap = S(14);
        float W = ClientSize.Width, H = ClientSize.Height;
        float y = pad;

        // Kopfzeile
        var titleF = F(22, true);
        Txt(g, "PC Monitor", titleF, Theme.Text, new RectangleF(pad, y, S(300), S(32)));
        float tx = pad + g.MeasureString("PC Monitor", titleF).Width + S(10);
        if (Data != null)
        {
            bool hwi = d.Source == "HWiNFO";
            string pill = hwi ? "Quelle: HWiNFO" : "Quelle: intern";
            var pf = F(11.5f, true);
            var pr = new RectangleF(tx, y + S(6), g.MeasureString(pill, pf).Width + S(26), S(24));
            var pc = hwi ? Theme.Good : Theme.Muted;
            using (var path = Theme.Rounded(pr, S(12)))
            using (var b = new SolidBrush(Theme.Alpha(pc, 36))) g.FillPath(b, path);
            using (var b = new SolidBrush(pc)) g.FillEllipse(b, pr.X + S(10), pr.Y + S(9), S(6), S(6));
            Txt(g, pill, pf, pc, new RectangleF(pr.X + S(20), pr.Y, pr.Width - S(20), pr.Height));
        }
        Txt(g, $"{d.CpuName}   ·   {d.GpuName}   ·   {d.RamTotalGb:0} GB RAM", F(12.5f), Theme.Muted,
            new RectangleF(pad, y + S(32), W - pad * 2 - S(340), S(20)));
        _btnSettings = new RectangleF(W - pad - S(150), y + S(6), S(150), S(38));
        _btnSensors = new RectangleF(_btnSettings.X - S(10) - S(150), y + S(6), S(150), S(38));
        HeaderButton(g, _btnSensors, "Alle Sensoren", _hoverSensors);
        HeaderButton(g, _btnSettings, "Einstellungen", _hoverSettings);
        y += S(62);

        // Hinweis-Banner
        _banner = RectangleF.Empty;
        BannerKind = 0;
        string msg = null;
        if (Data != null && d.HwinfoMissing)
        {
            BannerKind = 2;
            msg = "Hinweis:  HWiNFO läuft nicht oder „Shared Memory Support“ ist aus.  Hier klicken für die Anleitung.";
        }
        else if (Data != null && d.Source != "HWiNFO" && (DriverMissing || d.CpuTemp == null))
        {
            BannerKind = 1;
            msg = DriverMissing
                ? "Hinweis:  CPU-Temperatur & Mainboard-Lüfter brauchen den Treiber „PawnIO“ – oder du nutzt HWiNFO als Quelle.  Hier klicken."
                : "Hinweis:  CPU-Temperatur wird nicht geliefert.  Tipp: HWiNFO als Quelle nutzen oder PawnIO prüfen – hier klicken.";
        }
        if (msg != null)
        {
            _banner = new RectangleF(pad, y, W - pad * 2, S(40));
            using (var path = Theme.Rounded(_banner, S(10)))
            {
                using var b = new SolidBrush(Theme.Alpha(Theme.Warn, _hoverBanner ? 46 : 30));
                g.FillPath(b, path);
                using var p = new Pen(Theme.Alpha(Theme.Warn, 120), S(1));
                g.DrawPath(p, path);
            }
            Txt(g, msg, F(12.5f, true), Theme.Warn, new RectangleF(_banner.X + S(14), _banner.Y, _banner.Width - S(28), _banner.Height));
            y += S(40) + gap;
        }

        // Zeile 1: vier Karten
        float row1H = S(206);
        float cw = (W - pad * 2 - gap * 3) / 4;
        DrawCpu(g, new RectangleF(pad, y, cw, row1H), d, st);
        DrawGpu(g, new RectangleF(pad + (cw + gap), y, cw, row1H), d, st);
        DrawRam(g, new RectangleF(pad + (cw + gap) * 2, y, cw, row1H), d, st);
        DrawNet(g, new RectangleF(pad + (cw + gap) * 3, y, cw, row1H), d);
        y += row1H + gap;

        // Zeile 3 (unten) feste Höhe, Zeile 2 (Graphen) bekommt den Rest
        float row3H = S(214);
        float row2H = Math.Max(S(170), H - pad - row3H - gap - y);
        float hw = (W - pad * 2 - gap) / 2;
        int mins = Math.Max(1, (int)Math.Round((CpuTemp?.Capacity ?? 180) * st.IntervalMs / 60000.0));
        DrawChart(g, new RectangleF(pad, y, hw, row2H), $"Temperaturen · letzte {mins} Min.", "°C", 20, 100,
            new[] { (CpuTemp, Theme.Cpu, "CPU"), (GpuTemp, Theme.Gpu, "GPU") }, st);
        DrawChart(g, new RectangleF(pad + hw + gap, y, hw, row2H), $"Auslastung · letzte {mins} Min.", "%", 0, 100,
            new[] { (CpuLoad, Theme.Cpu, "CPU"), (GpuLoad, Theme.Gpu, "GPU"), (RamLoad, Theme.Ram, "RAM") }, null);
        y += row2H + gap;

        float tw = (W - pad * 2 - gap * 2) / 3;
        DrawFans(g, new RectangleF(pad, y, tw, row3H), d);
        DrawDrives(g, new RectangleF(pad + tw + gap, y, tw, row3H), d);
        DrawStorageTemps(g, new RectangleF(pad + (tw + gap) * 2, y, tw, row3H), d, st);
    }

    private void HeaderButton(Graphics g, RectangleF r, string text, bool hover)
    {
        using (var path = Theme.Rounded(r, S(10)))
        {
            using var b = new SolidBrush(hover ? Theme.Track : Theme.Card);
            g.FillPath(b, path);
            using var p = new Pen(Theme.CardBorder, S(1));
            g.DrawPath(p, path);
        }
        Txt(g, text, F(13, true), Theme.Text, r, StringAlignment.Center);
    }

    private void DrawCpu(Graphics g, RectangleF r, Snapshot d, AppSettings st)
    {
        bool alarm = ActiveAlarms.Contains("cpu");
        CardBg(g, r, alarm);
        CardTitle(g, r, "Prozessor", Theme.Cpu);
        float ring = Math.Min(r.Height - S(60), r.Width * 0.46f);
        var rr = new RectangleF(r.X + S(14), r.Y + S(48), ring, ring);
        var c = Theme.TempColor(d.CpuTemp, st.CpuTempLimit);
        Ring(g, rr, ((d.CpuTemp ?? 0) - 20) / 80.0, c, N(d.CpuTemp, "0", ""), "°C", d.CpuTempLabel);
        float x = rr.Right + S(12), w = r.Right - x - S(16), yy = r.Y + S(58);
        yy = StatRow(g, x, yy, w, "Auslastung", N(d.CpuLoad, "0", " %"));
        Bar(g, new RectangleF(x, yy - S(2), w, S(6)), d.CpuLoad ?? 0, Theme.PercentColor(d.CpuLoad ?? 0, Theme.Cpu));
        yy += S(14);
        yy = StatRow(g, x, yy, w, "Takt", d.CpuClock == null ? "–" : (d.CpuClock.Value / 1000).ToString("0.00") + " GHz");
        yy = StatRow(g, x, yy, w, "Leistung", N(d.CpuPower, "0", " W"));
        StatRow(g, x, yy, w, "Spitze", N(CpuTemp?.Max(), "0", " °C"), Theme.Muted);
    }

    private void DrawGpu(Graphics g, RectangleF r, Snapshot d, AppSettings st)
    {
        bool alarm = ActiveAlarms.Contains("gpu");
        CardBg(g, r, alarm);
        CardTitle(g, r, "Grafikkarte", Theme.Gpu);
        float ring = Math.Min(r.Height - S(60), r.Width * 0.46f);
        var rr = new RectangleF(r.X + S(14), r.Y + S(48), ring, ring);
        var c = Theme.TempColor(d.GpuTemp, st.GpuTempLimit);
        Ring(g, rr, ((d.GpuTemp ?? 0) - 20) / 80.0, c, N(d.GpuTemp, "0", ""), "°C",
            d.GpuHotSpot != null ? $"Hotspot {d.GpuHotSpot:0}°" : "Temperatur");
        float x = rr.Right + S(12), w = r.Right - x - S(16), yy = r.Y + S(58);
        yy = StatRow(g, x, yy, w, "Auslastung", N(d.GpuLoad, "0", " %"));
        Bar(g, new RectangleF(x, yy - S(2), w, S(6)), d.GpuLoad ?? 0, Theme.PercentColor(d.GpuLoad ?? 0, Theme.Gpu));
        yy += S(14);
        yy = StatRow(g, x, yy, w, "Takt", N(d.GpuClock, "0", " MHz"));
        yy = StatRow(g, x, yy, w, "Leistung", N(d.GpuPower, "0", " W"));
        string vram = d.GpuVramUsed != null && d.GpuVramTotal is > 0
            ? $"{d.GpuVramUsed / 1024:0.0}/{d.GpuVramTotal / 1024:0} GB" : "–";
        StatRow(g, x, yy, w, "VRAM", vram);
    }

    private void DrawRam(Graphics g, RectangleF r, Snapshot d, AppSettings st)
    {
        bool alarm = ActiveAlarms.Contains("ram");
        CardBg(g, r, alarm);
        CardTitle(g, r, "Arbeitsspeicher", Theme.Ram);
        float ring = Math.Min(r.Height - S(60), r.Width * 0.46f);
        var rr = new RectangleF(r.X + S(14), r.Y + S(48), ring, ring);
        Ring(g, rr, d.RamPercent / 100, Theme.PercentColor(d.RamPercent, Theme.Ram), d.RamPercent.ToString("0"), "%", "belegt");
        float x = rr.Right + S(12), w = r.Right - x - S(16), yy = r.Y + S(58);
        yy = StatRow(g, x, yy, w, "Belegt", $"{d.RamUsedGb:0.0} GB");
        yy = StatRow(g, x, yy, w, "Frei", $"{Math.Max(0, d.RamTotalGb - d.RamUsedGb):0.0} GB");
        yy = StatRow(g, x, yy, w, "Gesamt", $"{d.RamTotalGb:0.0} GB");
        StatRow(g, x, yy, w, "Spitze", N(RamLoad?.Max(), "0", " %"), Theme.Muted);
    }

    private void DrawNet(Graphics g, RectangleF r, Snapshot d)
    {
        CardBg(g, r);
        CardTitle(g, r, "Netzwerk", Theme.Net);
        float x = r.X + S(20), w = r.Width - S(40);
        Txt(g, "↓  Download", F(12), Theme.Muted, new RectangleF(x, r.Y + S(48), w, S(18)));
        Txt(g, Speed(d.NetDownBps), F(22, true), Theme.Net, new RectangleF(x, r.Y + S(64), w, S(32)));
        Txt(g, "↑  Upload", F(12), Theme.Muted, new RectangleF(x, r.Y + S(100), w, S(18)));
        Txt(g, Speed(d.NetUpBps), F(22, true), Theme.Text, new RectangleF(x, r.Y + S(116), w, S(32)));
        // Mini-Verlauf
        var cr = new RectangleF(x, r.Bottom - S(46), w, S(32));
        float max = Math.Max(1024 * 64, Math.Max(NetDown?.Max() ?? 0, NetUp?.Max() ?? 0));
        Spark(g, cr, NetUp, Theme.Alpha(Theme.Text, 150), max, false);
        Spark(g, cr, NetDown, Theme.Net, max, true);
    }

    /// <summary>Punkte eines Verlaufs mit weichem Scrollen (Versatz innerhalb des Messintervalls).</summary>
    private List<PointF> SeriesPoints(History h, RectangleF r, float yMin, float yMax)
    {
        var pts = new List<PointF>();
        if (h == null || h.Count < 2) return pts;
        var vals = h.Values().ToList();
        float step = r.Width / (h.Capacity - 2);
        float shift = (float)(1 - ScrollFrac) * step;     // neuester Punkt gleitet von rechts herein
        float x0 = r.Right + shift - step * (vals.Count - 1);
        for (int i = 0; i < vals.Count; i++)
        {
            if (vals[i] == null) continue;
            float v = Math.Clamp(vals[i].Value, yMin, yMax);
            pts.Add(new PointF(x0 + i * step, r.Bottom - r.Height * (v - yMin) / (yMax - yMin)));
        }
        return pts;
    }

    private void DrawSeries(Graphics g, List<PointF> pts, RectangleF r, Color c, int fillAlpha, float width)
    {
        if (pts.Count < 2) return;
        var arr = pts.ToArray();
        if (fillAlpha > 0)
        {
            using var gp = new GraphicsPath();
            gp.AddCurve(arr, 0.4f);
            gp.AddLine(arr[^1], new PointF(arr[^1].X, r.Bottom));
            gp.AddLine(new PointF(arr[^1].X, r.Bottom), new PointF(arr[0].X, r.Bottom));
            gp.CloseFigure();
            using var b = new LinearGradientBrush(new RectangleF(r.X, r.Y - 1, r.Width, r.Height + 2), Theme.Alpha(c, fillAlpha), Theme.Alpha(c, 0), 90f);
            g.FillPath(b, gp);
        }
        using var p = new Pen(c, width) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawCurve(p, arr, 0.4f);
    }

    private void Spark(Graphics g, RectangleF r, History h, Color c, float max, bool fill)
    {
        var state = g.Save();
        g.SetClip(new RectangleF(r.X, r.Y - S(3), r.Width, r.Height + S(6)));
        DrawSeries(g, SeriesPoints(h, r, 0, max), r, c, fill ? 90 : 0, S(1.6f));
        g.Restore(state);
    }

    private void DrawChart(Graphics g, RectangleF r, string title, string unit, float yMin, float yMax,
        (History h, Color c, string name)[] series, AppSettings st)
    {
        CardBg(g, r);
        CardTitle(g, r, title, Theme.Muted);

        // Legende mit aktuellen Werten (weich animiert)
        float lx = r.Right - S(16);
        foreach (var s in series.Reverse())
        {
            var cur = A("leg" + unit + s.name, s.h?.Values().LastOrDefault());
            string label = $"{s.name}  {(cur == null ? "–" : cur.Value.ToString("0"))}{(unit == "%" ? " %" : "°")}";
            var sz = g.MeasureString(label, F(12, true));
            lx -= sz.Width;
            Txt(g, label, F(12, true), Theme.Text, new RectangleF(lx, r.Y + S(14), sz.Width + S(4), S(22)));
            using (var b = new SolidBrush(s.c)) g.FillEllipse(b, lx - S(12), r.Y + S(21), S(8), S(8));
            lx -= S(26);
        }

        // Skala dynamisch erweitern – auch das gleitet
        foreach (var s in series)
        {
            var mx = s.h?.Max();
            if (mx != null && mx > yMax) yMax = (float)Math.Ceiling(mx.Value / 10) * 10;
        }
        yMax = A("ymax" + unit, yMax, 0.5f) ?? yMax;

        var plot = new RectangleF(r.X + S(46), r.Y + S(50), r.Width - S(64), r.Height - S(68));
        using (var gp = new Pen(Theme.Alpha(Theme.CardBorder, 255), S(1)) { DashStyle = DashStyle.Dot })
        {
            for (int i = 0; i <= 4; i++)
            {
                float yy = plot.Bottom - plot.Height * i / 4f;
                g.DrawLine(gp, plot.X, yy, plot.Right, yy);
                float val = yMin + (yMax - yMin) * i / 4f;
                Txt(g, val.ToString("0"), F(11), Theme.Muted, new RectangleF(r.X + S(6), yy - S(9), S(34), S(18)), StringAlignment.Far);
            }
        }

        // Grenzwertlinien (nur Temperaturgraph)
        if (st != null)
        {
            void Limit(int lim, Color c)
            {
                if (lim < yMin || lim > yMax) return;
                float yy = plot.Bottom - plot.Height * (lim - yMin) / (yMax - yMin);
                using var p = new Pen(Theme.Alpha(c, 120), S(1)) { DashStyle = DashStyle.Dash };
                g.DrawLine(p, plot.X, yy, plot.Right, yy);
            }
            Limit(st.CpuTempLimit, Theme.Hot);
            if (st.GpuTempLimit != st.CpuTempLimit) Limit(st.GpuTempLimit, Theme.Hot);
        }

        var state = g.Save();
        g.SetClip(new RectangleF(plot.X, plot.Y - S(4), plot.Width, plot.Height + S(8)));
        foreach (var s in series)
            DrawSeries(g, SeriesPoints(s.h, plot, yMin, yMax), plot, s.c, 55, S(2));
        g.Restore(state);
    }

    private void DrawFans(Graphics g, RectangleF r, Snapshot d)
    {
        CardBg(g, r);
        CardTitle(g, r, "Lüfter", Theme.Cpu);
        float x = r.X + S(20), w = r.Width - S(40), yy = r.Y + S(50);
        if (d.Fans.Count == 0)
        {
            Txt(g, DriverMissing && d.Source != "HWiNFO" ? "Keine Daten – PawnIO-Treiber fehlt oder HWiNFO nutzen." : "Keine Lüftersensoren gefunden.",
                F(12.5f), Theme.Muted, new RectangleF(x, yy, w, S(22)));
            return;
        }
        float rowH = S(30);
        int max = (int)((r.Bottom - yy - S(10)) / rowH);
        var fans = d.Fans.OrderByDescending(f => f.Rpm > 0).ThenBy(f => f.Name).ToList();
        foreach (var f in fans.Take(max))
        {
            bool off = f.Rpm < 1;
            Txt(g, f.Name, F(12.5f), off ? Theme.Muted : Theme.Text, new RectangleF(x, yy, w * 0.42f, S(20)));
            Txt(g, off ? "aus" : $"{f.Rpm:0} U/min", F(12.5f, true), off ? Theme.Muted : Theme.Text,
                new RectangleF(x, yy, w, S(20)), StringAlignment.Far);
            double pct = f.Percent ?? Math.Min(100, f.Rpm / 30.0); // ohne %-Wert: 3000 U/min = 100 %
            Bar(g, new RectangleF(x + w * 0.42f, yy + S(7), w * 0.30f, S(6)), pct, Theme.Cpu);
            yy += rowH;
        }
        if (fans.Count > max)
            Txt(g, $"+ {fans.Count - max} weitere", F(11), Theme.Muted, new RectangleF(x, r.Bottom - S(22), w, S(16)), StringAlignment.Far);
    }

    private void DrawDrives(Graphics g, RectangleF r, Snapshot d)
    {
        CardBg(g, r);
        CardTitle(g, r, "Laufwerke", Theme.Ram);
        float x = r.X + S(20), w = r.Width - S(40), yy = r.Y + S(50);
        float rowH = S(40);
        int max = Math.Max(1, (int)((r.Bottom - yy - S(6)) / rowH));
        foreach (var dr in d.Drives.Take(max))
        {
            Txt(g, $"{dr.Letter}  {dr.Label}", F(12.5f, true), Theme.Text, new RectangleF(x, yy, w * 0.6f, S(20)));
            Txt(g, $"{dr.TotalGb - dr.UsedGb:0} GB frei von {dr.TotalGb:0} GB", F(11.5f), Theme.Muted,
                new RectangleF(x, yy, w, S(20)), StringAlignment.Far);
            Bar(g, new RectangleF(x, yy + S(23), w, S(7)), dr.Percent, Theme.PercentColor(dr.Percent, Theme.Ram));
            yy += rowH;
        }
    }

    private void DrawStorageTemps(Graphics g, RectangleF r, Snapshot d, AppSettings st)
    {
        CardBg(g, r, ActiveAlarms.Contains("disk"));
        CardTitle(g, r, "SSD / HDD Temperatur", Theme.Warn);
        float x = r.X + S(20), w = r.Width - S(40), yy = r.Y + S(50);
        if (d.StorageTemps.Count == 0)
        {
            Txt(g, "Keine Temperaturwerte verfügbar.", F(12.5f), Theme.Muted, new RectangleF(x, yy, w, S(22)));
            return;
        }
        float rowH = S(30);
        int max = (int)((r.Bottom - yy - S(6)) / rowH);
        foreach (var t in d.StorageTemps.Take(max))
        {
            var c = Theme.TempColor(t.Temp, st.DiskTempLimit);
            using (var b = new SolidBrush(c)) g.FillEllipse(b, x, yy + S(6), S(8), S(8));
            Txt(g, t.Name, F(12.5f), Theme.Text, new RectangleF(x + S(16), yy, w - S(70), S(20)));
            Txt(g, $"{t.Temp:0} °C", F(13, true), c, new RectangleF(x, yy, w, S(20)), StringAlignment.Far);
            yy += rowH;
        }
    }

    // ------------------------------------------------------------ Maus
    protected override void OnMouseMove(MouseEventArgs e)
    {
        bool hs = _btnSettings.Contains(e.Location), hb = _banner.Contains(e.Location), hn = _btnSensors.Contains(e.Location);
        if (hs != _hoverSettings || hb != _hoverBanner || hn != _hoverSensors)
        {
            _hoverSettings = hs; _hoverBanner = hb; _hoverSensors = hn;
            Cursor = hs || hb || hn ? Cursors.Hand : Cursors.Default;
            Invalidate();
        }
        base.OnMouseMove(e);
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            if (_btnSettings.Contains(e.Location)) SettingsClicked?.Invoke(this, EventArgs.Empty);
            else if (_btnSensors.Contains(e.Location)) SensorsClicked?.Invoke(this, EventArgs.Empty);
            else if (_banner.Contains(e.Location)) BannerClicked?.Invoke(this, EventArgs.Empty);
        }
        base.OnMouseClick(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) foreach (var f in _fonts.Values) f.Dispose();
        base.Dispose(disposing);
    }
}

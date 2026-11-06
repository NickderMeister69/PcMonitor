using System.Drawing.Drawing2D;

namespace PcMonitor;

public static class Theme
{
    public static readonly Color Bg = Color.FromArgb(13, 16, 23);
    public static readonly Color Card = Color.FromArgb(22, 27, 38);
    public static readonly Color CardBorder = Color.FromArgb(36, 43, 58);
    public static readonly Color Track = Color.FromArgb(38, 45, 61);
    public static readonly Color Text = Color.FromArgb(232, 236, 244);
    public static readonly Color Muted = Color.FromArgb(132, 143, 165);
    public static readonly Color Cpu = Color.FromArgb(56, 189, 248);    // Cyan
    public static readonly Color Gpu = Color.FromArgb(132, 204, 22);    // NVIDIA-Grün
    public static readonly Color Ram = Color.FromArgb(168, 139, 250);   // Violett
    public static readonly Color Net = Color.FromArgb(251, 146, 60);    // Orange
    public static readonly Color Good = Color.FromArgb(52, 211, 153);
    public static readonly Color Warn = Color.FromArgb(250, 204, 21);
    public static readonly Color Hot = Color.FromArgb(248, 82, 82);

    public const string FontName = "Segoe UI";
    public const string FontNameSemi = "Segoe UI Semibold";

    /// <summary>Farbe abhängig von Temperatur relativ zum Grenzwert.</summary>
    public static Color TempColor(float? t, int limit)
    {
        if (t == null) return Muted;
        if (t >= limit) return Hot;
        if (t >= limit - 12) return Warn;
        return Good;
    }

    public static Color PercentColor(double p, Color normal)
    {
        if (p >= 90) return Hot;
        if (p >= 75) return Warn;
        return normal;
    }

    public static GraphicsPath Rounded(RectangleF r, float rad)
    {
        var p = new GraphicsPath();
        float d = rad * 2;
        if (d > r.Width) d = r.Width;
        if (d > r.Height) d = r.Height;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    public static Color Alpha(Color c, int a) => Color.FromArgb(a, c);
}

/// <summary>Ringpuffer für Verlaufsgraphen.</summary>
public class History
{
    private readonly float?[] _data;
    private int _pos, _count;
    public History(int capacity) { _data = new float?[capacity]; }
    public int Capacity => _data.Length;
    public void Add(float? v) { _data[_pos] = v; _pos = (_pos + 1) % _data.Length; if (_count < _data.Length) _count++; }
    public IEnumerable<float?> Values()
    {
        int start = (_pos - _count + _data.Length) % _data.Length;
        for (int i = 0; i < _count; i++) yield return _data[(start + i) % _data.Length];
    }
    public int Count => _count;
    public float? Max() => Values().Where(v => v != null).DefaultIfEmpty(null).Max();
    public float? Min() => Values().Where(v => v != null).DefaultIfEmpty(null).Min();
}

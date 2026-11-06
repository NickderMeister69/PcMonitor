using System.Runtime.InteropServices;

namespace PcMonitor;

/// <summary>Zeigt alle Rohwerte der aktuellen Quelle – zum Vergleichen mit HWiNFO.</summary>
public class SensorsForm : Form
{
    private readonly ListView _list;
    private readonly TextBox _filter;
    private readonly Label _info;
    private string _layoutKey = "";
    private Snapshot _last;

    public SensorsForm()
    {
        Text = "Alle Sensoren – PC Monitor";
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = Theme.Bg;
        ForeColor = Theme.Text;
        Font = new Font(Theme.FontName, 9.75f);
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(LogicalToDeviceUnits(760), LogicalToDeviceUnits(820));
        MinimumSize = new Size(LogicalToDeviceUnits(520), LogicalToDeviceUnits(400));

        var top = new Panel { Dock = DockStyle.Top, Height = LogicalToDeviceUnits(46), BackColor = Theme.Bg, Padding = new Padding(LogicalToDeviceUnits(12), LogicalToDeviceUnits(10), LogicalToDeviceUnits(12), 0) };
        _filter = new TextBox { Dock = DockStyle.Left, Width = LogicalToDeviceUnits(260), BackColor = Theme.Card, ForeColor = Theme.Text, BorderStyle = BorderStyle.FixedSingle, PlaceholderText = "Suchen, z. B. Temperatur, Fan, GPU …" };
        _filter.TextChanged += (_, _) => { _layoutKey = ""; if (_last != null) UpdateData(_last); };
        _info = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleRight, ForeColor = Theme.Muted };
        top.Controls.Add(_info);
        top.Controls.Add(_filter);

        _list = new ListView
        {
            Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HeaderStyle = ColumnHeaderStyle.Nonclickable,
            BackColor = Theme.Card, ForeColor = Theme.Text, BorderStyle = BorderStyle.None, ShowGroups = true,
        };
        _list.Columns.Add("Sensor", LogicalToDeviceUnits(330));
        _list.Columns.Add("Aktuell", LogicalToDeviceUnits(120), HorizontalAlignment.Right);
        _list.Columns.Add("Minimum", LogicalToDeviceUnits(120), HorizontalAlignment.Right);
        _list.Columns.Add("Maximum", LogicalToDeviceUnits(120), HorizontalAlignment.Right);
        typeof(ListView).GetProperty("DoubleBuffered", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.SetValue(_list, true);

        Controls.Add(_list);
        Controls.Add(top);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int on = 1;
        DwmSetWindowAttribute(Handle, 20, ref on, 4);
        SetWindowTheme(_list.Handle, "DarkMode_Explorer", null);
    }

    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int val, int size);
    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)] private static extern int SetWindowTheme(IntPtr hwnd, string app, string id);

    private static string Fmt(double v, string unit)
    {
        if (double.IsNaN(v) || double.IsInfinity(v)) return "–";
        string num = Math.Abs(v) >= 100 ? v.ToString("0") : Math.Abs(v) >= 10 ? v.ToString("0.0") : v.ToString("0.00");
        return string.IsNullOrEmpty(unit) ? num : num + " " + unit;
    }

    public void UpdateData(Snapshot s)
    {
        _last = s;
        var f = _filter.Text.Trim();
        var rows = s.Raw.Where(r => f.Length == 0 ||
                                    r.Name.Contains(f, StringComparison.OrdinalIgnoreCase) ||
                                    r.Group.Contains(f, StringComparison.OrdinalIgnoreCase) ||
                                    r.Unit.Contains(f, StringComparison.OrdinalIgnoreCase)).ToList();
        _info.Text = $"Quelle: {(s.Source == "HWiNFO" ? "HWiNFO" : "intern (LibreHardwareMonitor)")}  ·  {rows.Count} Werte";

        var key = s.Source + "|" + string.Join("|", rows.Select(r => r.Group + r.Name));
        if (key != _layoutKey)
        {
            _layoutKey = key;
            _list.BeginUpdate();
            _list.Items.Clear();
            _list.Groups.Clear();
            var groups = new Dictionary<string, ListViewGroup>();
            foreach (var r in rows)
            {
                if (!groups.TryGetValue(r.Group, out var grp))
                {
                    grp = new ListViewGroup(r.Group);
                    groups[r.Group] = grp;
                    _list.Groups.Add(grp);
                }
                var it = new ListViewItem(new[] { r.Name, Fmt(r.Value, r.Unit), Fmt(r.Min, r.Unit), Fmt(r.Max, r.Unit) }, grp);
                _list.Items.Add(it);
            }
            _list.EndUpdate();
            return;
        }

        for (int i = 0; i < rows.Count && i < _list.Items.Count; i++)
        {
            var it = _list.Items[i];
            var r = rows[i];
            SetText(it.SubItems[1], Fmt(r.Value, r.Unit));
            SetText(it.SubItems[2], Fmt(r.Min, r.Unit));
            SetText(it.SubItems[3], Fmt(r.Max, r.Unit));
        }
    }

    private static void SetText(ListViewItem.ListViewSubItem sub, string t)
    {
        if (sub.Text != t) sub.Text = t;
    }
}

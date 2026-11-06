using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using LibreHardwareMonitor.Hardware;

namespace PcMonitor;

public class FanInfo
{
    public string Name;
    public float Rpm;
    public float? Percent;
}

public class DriveUsage
{
    public string Letter;
    public string Label;
    public double UsedGb, TotalGb;
    public double Percent => TotalGb > 0 ? UsedGb / TotalGb * 100 : 0;
}

public class StorageTemp
{
    public string Name;
    public float Temp;
}

public class RawReading
{
    public string Group, Name, Unit;
    public double Value, Min, Max;
}

public enum SensorSource { Automatisch = 0, Intern = 1, HWiNFO = 2 }

/// <summary>Ein Messzeitpunkt mit allen Werten.</summary>
public class Snapshot
{
    public string CpuName = "CPU", GpuName = "GPU";
    public float? CpuTemp, CpuLoad, CpuClock, CpuPower;
    public string CpuTempLabel = "Temperatur";
    public float? GpuTemp, GpuHotSpot, GpuLoad, GpuClock, GpuMemClock, GpuPower, GpuVramUsed, GpuVramTotal;
    public double RamUsedGb, RamTotalGb, RamPercent;
    public double NetDownBps, NetUpBps;
    public List<FanInfo> Fans = new();
    public List<DriveUsage> Drives = new();
    public List<StorageTemp> StorageTemps = new();
    public List<RawReading> Raw = new();
    public string Source = "Intern";
    public bool HwinfoMissing;
}

/// <summary>Liest alle Sensoren über LibreHardwareMonitorLib in einem Hintergrund-Thread aus.</summary>
public sealed class HardwareService : IDisposable
{
    private readonly Computer _computer;
    private readonly Thread _thread;
    private volatile bool _running = true;
    private long _lastRx, _lastTx;
    private DateTime _lastNet = DateTime.MinValue;

    public event Action<Snapshot> Updated;
    public int IntervalMs { get; set; } = 1000;
    public SensorSource Mode { get; set; } = SensorSource.Automatisch;
    private bool _opened;

    public HardwareService()
    {
        _computer = new Computer
        {
            IsCpuEnabled = true,
            IsGpuEnabled = true,
            IsMemoryEnabled = true,
            IsMotherboardEnabled = true,
            IsStorageEnabled = true,
            IsControllerEnabled = true,
            IsNetworkEnabled = false,
        };
        _thread = new Thread(Loop) { IsBackground = true, Name = "Sensoren" };
    }

    public void Start() => _thread.Start();

    private void Loop()
    {
        while (_running)
        {
            var t0 = Environment.TickCount64;
            Snapshot s;
            try { s = Read(); } catch (Exception ex) { Log.Write("Sensoren", ex); s = new Snapshot(); }
            try { Updated?.Invoke(s); } catch (Exception ex) { Log.Write("Update", ex); }
            var wait = IntervalMs - (int)(Environment.TickCount64 - t0);
            if (wait > 0) Thread.Sleep(wait);
        }
    }

    private Snapshot Read()
    {
        var s = new Snapshot();
        bool fromHwinfo = false;
        if (Mode != SensorSource.Intern)
        {
            fromHwinfo = HwinfoReader.TryRead(s);
            if (!fromHwinfo && Mode == SensorSource.HWiNFO) s.HwinfoMissing = true;
        }
        if (fromHwinfo) s.Source = "HWiNFO";
        else if (Mode != SensorSource.HWiNFO) ReadLhm(s);

        ReadRam(s);
        ReadDrives(s);
        ReadNetwork(s);
        return s;
    }

    private void ReadLhm(Snapshot s)
    {
        s.Source = "Intern";
        if (!_opened)
        {
            try { _computer.Open(); } catch { /* einzelne Sensoren können fehlen */ }
            _opened = true;
        }
        foreach (var hw in _computer.Hardware)
        {
            try { hw.Update(); foreach (var sub in hw.SubHardware) sub.Update(); } catch { }
        }

        IHardware gpu = _computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.GpuNvidia)
                     ?? _computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.GpuAmd)
                     ?? _computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.GpuIntel);

        foreach (var hw in _computer.Hardware)
        {
            switch (hw.HardwareType)
            {
                case HardwareType.Cpu: ReadCpu(hw, s); break;
                case HardwareType.Motherboard: ReadMotherboard(hw, s); break;
                case HardwareType.Storage: ReadStorage(hw, s); break;
            }
        }
        if (gpu != null) ReadGpu(gpu, s);

        foreach (var hw in _computer.Hardware)
        {
            AddRaw(hw, s);
            foreach (var sub in hw.SubHardware) AddRaw(sub, s);
        }
    }

    private static void AddRaw(IHardware hw, Snapshot s)
    {
        foreach (var x in hw.Sensors)
        {
            if (V(x) == null) continue;
            s.Raw.Add(new RawReading
            {
                Group = hw.Name + "  (" + hw.HardwareType + ")",
                Name = $"{x.Name}  [{x.SensorType}]",
                Unit = UnitOf(x.SensorType),
                Value = V(x).Value,
                Min = x.Min ?? double.NaN,
                Max = x.Max ?? double.NaN,
            });
        }
    }

    private static string UnitOf(SensorType t) => t switch
    {
        SensorType.Temperature => "°C",
        SensorType.Load or SensorType.Control or SensorType.Level => "%",
        SensorType.Clock => "MHz",
        SensorType.Power => "W",
        SensorType.Fan => "RPM",
        SensorType.Voltage => "V",
        SensorType.Current => "A",
        SensorType.Data => "GB",
        SensorType.SmallData => "MB",
        SensorType.Throughput => "B/s",
        SensorType.Frequency => "Hz",
        SensorType.Energy => "mWh",
        _ => "",
    };

    private static float? V(ISensor x) => x?.Value is float f && !float.IsNaN(f) ? f : null;

    private static ISensor Find(IHardware hw, SensorType type, params string[] names)
    {
        var list = hw.Sensors.Where(x => x.SensorType == type).ToList();
        foreach (var n in names)
        {
            var m = list.FirstOrDefault(x => x.Name.Equals(n, StringComparison.OrdinalIgnoreCase) && V(x) != null);
            if (m != null) return m;
        }
        foreach (var n in names)
        {
            var m = list.FirstOrDefault(x => x.Name.Contains(n, StringComparison.OrdinalIgnoreCase) && V(x) != null);
            if (m != null) return m;
        }
        return null;
    }

    private static void ReadCpu(IHardware hw, Snapshot s)
    {
        if (s.CpuTemp != null) return; // nur erste CPU
        s.CpuName = hw.Name;
        // Bevorzugt: Durchschnitt aller P-Kerne, sonst aller Kerne
        float? Avg(string pattern)
        {
            var t = hw.Sensors.Where(x => x.SensorType == SensorType.Temperature && !x.Name.Contains("Distance")
                                          && System.Text.RegularExpressions.Regex.IsMatch(x.Name, pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase)
                                          && V(x) > 0).Select(x => V(x).Value).ToList();
            return t.Count > 0 ? t.Average() : null;
        }
        s.CpuTemp = Avg(@"^(CPU )?P-Core #\d+$");
        if (s.CpuTemp != null) { s.CpuTempLabel = "Ø P-Kerne"; goto rest; }
        s.CpuTemp = Avg(@"^(CPU )?Core #\d+$");
        if (s.CpuTemp != null) { s.CpuTempLabel = "Ø Kerne"; goto rest; }
        var temp = Find(hw, SensorType.Temperature, "Core Average", "CPU Package", "Core (Tctl/Tdie)", "Core (Tctl)", "Core (Tdie)", "Core Max", "Core Average");
        s.CpuTemp = V(temp) ?? hw.Sensors
            .Where(x => x.SensorType == SensorType.Temperature && !x.Name.Contains("Distance") && V(x) > 0)
            .Select(x => V(x)).DefaultIfEmpty(null).Max();
        if (s.CpuTemp <= 0) s.CpuTemp = null;
        rest:
        s.CpuLoad = V(Find(hw, SensorType.Load, "CPU Total"));
        var clocks = hw.Sensors.Where(x => x.SensorType == SensorType.Clock && x.Name.StartsWith("Core #")
                                          && !x.Name.Contains("Effective") && V(x) > 0).Select(x => V(x).Value).ToList();
        s.CpuClock = clocks.Count > 0 ? clocks.Average() : null;
        s.CpuPower = V(Find(hw, SensorType.Power, "CPU Package", "Package"));
    }

    private static void ReadGpu(IHardware hw, Snapshot s)
    {
        s.GpuName = hw.Name;
        s.GpuTemp = V(Find(hw, SensorType.Temperature, "GPU Core"));
        s.GpuHotSpot = V(Find(hw, SensorType.Temperature, "GPU Hot Spot"));
        s.GpuLoad = V(Find(hw, SensorType.Load, "GPU Core"));
        s.GpuClock = V(Find(hw, SensorType.Clock, "GPU Core"));
        s.GpuMemClock = V(Find(hw, SensorType.Clock, "GPU Memory"));
        s.GpuPower = V(Find(hw, SensorType.Power, "GPU Package", "GPU Power", "GPU"));
        s.GpuVramUsed = V(Find(hw, SensorType.SmallData, "GPU Memory Used"));
        s.GpuVramTotal = V(Find(hw, SensorType.SmallData, "GPU Memory Total"));

        var controls = hw.Sensors.Where(x => x.SensorType == SensorType.Control).ToList();
        int i = 0;
        foreach (var f in hw.Sensors.Where(x => x.SensorType == SensorType.Fan && V(x) != null))
        {
            var ctl = controls.ElementAtOrDefault(i++);
            s.Fans.Add(new FanInfo { Name = "Grafikkarte" + (i > 1 || hw.Sensors.Count(x => x.SensorType == SensorType.Fan) > 1 ? $" {i}" : ""), Rpm = V(f).Value, Percent = V(ctl) });
        }
    }

    private static void ReadMotherboard(IHardware hw, Snapshot s)
    {
        foreach (var sub in hw.SubHardware)
        {
            var controls = sub.Sensors.Where(x => x.SensorType == SensorType.Control).ToList();
            foreach (var f in sub.Sensors.Where(x => x.SensorType == SensorType.Fan && V(x) != null))
            {
                var ctl = controls.FirstOrDefault(c => c.Index == f.Index);
                s.Fans.Add(new FanInfo { Name = PrettyFan(f.Name), Rpm = V(f).Value, Percent = V(ctl) });
            }
        }
    }

    private static string PrettyFan(string n) => n.Replace("Fan #", "Lüfter ").Replace("CPU Fan", "CPU-Lüfter");

    private static void ReadStorage(IHardware hw, Snapshot s)
    {
        var t = V(Find(hw, SensorType.Temperature, "Temperature", "Composite Temperature", "Temperature 1"));
        if (t is > 0) s.StorageTemps.Add(new StorageTemp { Name = hw.Name, Temp = t.Value });
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);

    private static void ReadRam(Snapshot s)
    {
        var m = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (!GlobalMemoryStatusEx(ref m)) return;
        const double GB = 1024.0 * 1024 * 1024;
        s.RamTotalGb = m.ullTotalPhys / GB;
        s.RamUsedGb = (m.ullTotalPhys - m.ullAvailPhys) / GB;
        s.RamPercent = s.RamTotalGb > 0 ? s.RamUsedGb / s.RamTotalGb * 100 : 0;
    }

    private static void ReadDrives(Snapshot s)
    {
        const double GB = 1024.0 * 1024 * 1024;
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (!d.IsReady || d.DriveType != DriveType.Fixed) continue;
                s.Drives.Add(new DriveUsage
                {
                    Letter = d.Name.TrimEnd('\\'),
                    Label = string.IsNullOrWhiteSpace(d.VolumeLabel) ? "Lokaler Datenträger" : d.VolumeLabel,
                    TotalGb = d.TotalSize / GB,
                    UsedGb = (d.TotalSize - d.TotalFreeSpace) / GB,
                });
            }
            catch { }
        }
    }

    private void ReadNetwork(Snapshot s)
    {
        long rx = 0, tx = 0;
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                var st = ni.GetIPStatistics();
                rx += st.BytesReceived; tx += st.BytesSent;
            }
        }
        catch { return; }
        var now = DateTime.UtcNow;
        if (_lastNet != DateTime.MinValue)
        {
            var sec = (now - _lastNet).TotalSeconds;
            if (sec > 0)
            {
                s.NetDownBps = Math.Max(0, (rx - _lastRx) / sec);
                s.NetUpBps = Math.Max(0, (tx - _lastTx) / sec);
            }
        }
        _lastRx = rx; _lastTx = tx; _lastNet = now;
    }

    public void Dispose()
    {
        _running = false;
        try { _thread.Join(2000); } catch { }
        try { if (_opened) _computer.Close(); } catch { }
    }
}

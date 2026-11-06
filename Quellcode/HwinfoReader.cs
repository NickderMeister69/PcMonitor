using System.IO.MemoryMappedFiles;
using System.Text;
using System.Text.RegularExpressions;

namespace PcMonitor;

/// <summary>
/// Liest die Sensoren aus HWiNFO über dessen "Shared Memory Support"
/// (Speicherbereich Global\HWiNFO_SENS_SM2, Format laut HWiNFO-SDK, pack(1)).
/// </summary>
public static class HwinfoReader
{
    private const string MapName = "Global\\HWiNFO_SENS_SM2";
    private const uint SigActive = 0x53695748; // "HWiS"

    private enum RType : uint { None = 0, Temp = 1, Volt = 2, Fan = 3, Current = 4, Power = 5, Clock = 6, Usage = 7, Other = 8 }

    private class Reading
    {
        public RType Type;
        public string Sensor, SensorOrig, Label, LabelOrig, Unit;
        public double Value, Min, Max;
    }

    private static readonly Encoding Enc = Encoding.Latin1;

    private static string Str(byte[] buf, int off, int len)
    {
        if (off < 0 || off + len > buf.Length) return "";
        int end = Array.IndexOf(buf, (byte)0, off, len);
        if (end < 0) end = off + len;
        return Enc.GetString(buf, off, end - off).Trim();
    }

    private static List<Reading> ReadAll()
    {
        MemoryMappedFile mmf;
        try { mmf = MemoryMappedFile.OpenExisting(MapName, MemoryMappedFileRights.Read); }
        catch { return null; }
        using (mmf)
        using (var acc = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read))
        {
            if (acc.Capacity < 44) return null;
            if (acc.ReadUInt32(0) != SigActive) return null;
            uint offS = acc.ReadUInt32(20), sizeS = acc.ReadUInt32(24), numS = acc.ReadUInt32(28);
            uint offR = acc.ReadUInt32(32), sizeR = acc.ReadUInt32(36), numR = acc.ReadUInt32(40);
            long need = Math.Max(offS + (long)sizeS * numS, offR + (long)sizeR * numR);
            if (need > acc.Capacity || sizeR < 316 || sizeS < 264 || numR > 100000) return null;

            var buf = new byte[need];
            acc.ReadArray(0, buf, 0, buf.Length);

            var sensors = new List<(string orig, string user)>();
            for (int i = 0; i < numS; i++)
            {
                int b = (int)(offS + i * sizeS);
                var orig = Str(buf, b + 8, 128);
                var user = Str(buf, b + 136, 128);
                sensors.Add((orig, string.IsNullOrEmpty(user) ? orig : user));
            }

            var list = new List<Reading>((int)numR);
            for (int i = 0; i < numR; i++)
            {
                int b = (int)(offR + i * sizeR);
                uint sIdx = BitConverter.ToUInt32(buf, b + 4);
                var lo = Str(buf, b + 12, 128);
                var lu = Str(buf, b + 140, 128);
                var sn = sIdx < sensors.Count ? sensors[(int)sIdx] : ("", "");
                list.Add(new Reading
                {
                    Type = (RType)BitConverter.ToUInt32(buf, b),
                    SensorOrig = sn.Item1, Sensor = sn.Item2,
                    LabelOrig = lo, Label = string.IsNullOrEmpty(lu) ? lo : lu,
                    Unit = Str(buf, b + 268, 16),
                    Value = BitConverter.ToDouble(buf, b + 284),
                    Min = BitConverter.ToDouble(buf, b + 292),
                    Max = BitConverter.ToDouble(buf, b + 300),
                });
            }
            return list;
        }
    }

    /// <summary>Füllt den Snapshot aus HWiNFO. false = HWiNFO nicht verfügbar.</summary>
    public static bool TryRead(Snapshot s)
    {
        List<Reading> all;
        try { all = ReadAll(); } catch { return false; }
        if (all == null || all.Count == 0) return false;

        static bool IsCpu(Reading r) => r.SensorOrig.StartsWith("CPU [#0]");
        static bool IsGpu(Reading r) => r.SensorOrig.StartsWith("GPU [#0]");

        float? Pick(Func<Reading, bool> sensor, RType type, params string[] labels)
        {
            var c = all.Where(r => r.Type == type && sensor(r)).ToList();
            foreach (var l in labels)
            {
                var m = c.FirstOrDefault(r => r.LabelOrig.Equals(l, StringComparison.OrdinalIgnoreCase));
                if (m != null) return (float)m.Value;
            }
            foreach (var l in labels)
            {
                var m = c.FirstOrDefault(r => r.LabelOrig.Contains(l, StringComparison.OrdinalIgnoreCase));
                if (m != null) return (float)m.Value;
            }
            return null;
        }

        // Namen
        var cpuS = all.Where(IsCpu).Select(r => r.SensorOrig).OrderBy(n => n.Length).FirstOrDefault();
        if (cpuS != null) s.CpuName = CleanName(cpuS);
        var gpuS = all.Where(IsGpu).Select(r => r.SensorOrig).OrderBy(n => n.Length).FirstOrDefault();
        if (gpuS != null) s.GpuName = CleanName(gpuS);

        // CPU
        // CPU-Temperatur: Durchschnitt aller P-Kerne (ruhiger als Package/Max),
        // ohne Hybrid-CPU Durchschnitt aller Kerne, sonst Package/Tctl.
        float? CoreAvg(string pattern)
        {
            var temps = all.Where(r => r.Type == RType.Temp && IsCpu(r) && Regex.IsMatch(r.LabelOrig, pattern, RegexOptions.IgnoreCase)
                                       && !r.LabelOrig.Contains("Distance", StringComparison.OrdinalIgnoreCase) && r.Value > 0)
                           .GroupBy(r => Regex.Match(r.LabelOrig, @"\d+").Value)   // gleicher Kern aus mehreren Sensoren nur einmal
                           .Select(gr => gr.First().Value).ToList();
            return temps.Count > 0 ? (float)temps.Average() : null;
        }
        s.CpuTemp = CoreAvg(@"^P-core \d+");
        if (s.CpuTemp != null) s.CpuTempLabel = "Ø P-Kerne";
        else
        {
            s.CpuTemp = CoreAvg(@"^Core \d+(\s|$|:)");
            if (s.CpuTemp != null) s.CpuTempLabel = "Ø Kerne";
            else
            {
                s.CpuTemp = Pick(IsCpu, RType.Temp, "Core Average", "CPU Package", "CPU (Tctl/Tdie)", "CPU Die (average)", "CPU (Tctl)", "Core Max", "CPU Die");
                s.CpuTempLabel = "Temperatur";
            }
        }
        s.CpuLoad = Pick(IsCpu, RType.Usage, "Total CPU Usage", "Total CPU Utility");
        s.CpuClock = Pick(IsCpu, RType.Clock, "Core Clocks (avg)", "Average Effective Clock");
        if (s.CpuClock == null)
        {
            var clocks = all.Where(r => r.Type == RType.Clock && IsCpu(r) && Regex.IsMatch(r.LabelOrig, @"^(P-)?Core \d+ Clock"))
                            .Select(r => r.Value).ToList();
            if (clocks.Count > 0) s.CpuClock = (float)clocks.Average();
        }
        s.CpuPower = Pick(IsCpu, RType.Power, "CPU Package Power");

        // GPU
        s.GpuTemp = Pick(IsGpu, RType.Temp, "GPU Temperature");
        s.GpuHotSpot = Pick(IsGpu, RType.Temp, "GPU Hot Spot Temperature", "GPU Hotspot Temperature", "Hot Spot");
        s.GpuLoad = Pick(IsGpu, RType.Usage, "GPU Core Load", "GPU Utilization", "GPU D3D Usage");
        s.GpuClock = Pick(IsGpu, RType.Clock, "GPU Clock", "GPU Core Clock");
        s.GpuMemClock = Pick(IsGpu, RType.Clock, "GPU Memory Clock");
        var pw = all.FirstOrDefault(r => IsGpu(r) && r.Type == RType.Power && r.Unit == "W" &&
                     (r.LabelOrig == "GPU Power" || r.LabelOrig == "Total Board Power" || r.LabelOrig == "GPU Power (Total)"))
              ?? all.FirstOrDefault(r => IsGpu(r) && r.Type == RType.Power && r.Unit == "W");
        if (pw != null) s.GpuPower = (float)pw.Value;
        var alloc = Pick(IsGpu, RType.Other, "GPU Memory Allocated", "GPU D3D Memory Dedicated");
        var memPct = Pick(IsGpu, RType.Usage, "GPU Memory Usage");
        if (alloc != null)
        {
            s.GpuVramUsed = alloc;
            if (memPct is > 0.5f) s.GpuVramTotal = alloc / memPct * 100;
        }

        // Lüfter (alle Sensoren)
        foreach (var r in all.Where(r => r.Type == RType.Fan && r.Unit.Contains("RPM", StringComparison.OrdinalIgnoreCase)))
        {
            var name = r.Label;
            if (IsGpu(r) && !name.StartsWith("GPU")) name = "GPU " + name;
            if (s.Fans.Any(f => f.Name == name)) continue;
            s.Fans.Add(new FanInfo { Name = name, Rpm = (float)r.Value });
        }

        // Laufwerkstemperaturen
        foreach (var grp in all.Where(r => r.Type == RType.Temp &&
                     (r.SensorOrig.StartsWith("S.M.A.R.T.") || r.SensorOrig.StartsWith("Drive:")))
                     .GroupBy(r => r.Sensor))
        {
            var t = grp.FirstOrDefault(r => r.LabelOrig.StartsWith("Drive Temperature")) ?? grp.First();
            var name = Regex.Replace(grp.Key, @"^(S\.M\.A\.R\.T\.|Drive):\s*", "");
            if (t.Value > 0) s.StorageTemps.Add(new StorageTemp { Name = name, Temp = (float)t.Value });
        }

        // Rohwerte für "Alle Sensoren"
        foreach (var r in all)
            s.Raw.Add(new RawReading { Group = r.Sensor, Name = r.Label, Unit = r.Unit, Value = r.Value, Min = r.Min, Max = r.Max });

        return true;
    }

    private static string CleanName(string n)
    {
        n = Regex.Replace(n, @"^(CPU|GPU) \[#\d+\]:\s*", "");
        int i = n.IndexOf(':');
        if (i > 0) n = n[..i];
        return n.Trim();
    }
}

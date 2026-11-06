using System.Diagnostics;
using System.Text.Json;

namespace PcMonitor;

public class AppSettings
{
    public int CpuTempLimit { get; set; } = 85;
    public int GpuTempLimit { get; set; } = 83;
    public int DiskTempLimit { get; set; } = 60;
    public int RamLimit { get; set; } = 90;
    public bool WarningsEnabled { get; set; } = true;
    public int WarningCooldownMin { get; set; } = 5;
    public bool MinimizeToTray { get; set; } = true;
    public bool StartMinimized { get; set; } = false;
    public int IntervalMs { get; set; } = 1000;
    public SensorSource Source { get; set; } = SensorSource.Automatisch;

    private static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PcMonitor");
    private static string FilePath => Path.Combine(Dir, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch { }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}

/// <summary>Autostart über die Aufgabenplanung (nötig, weil die App Admin-Rechte braucht).</summary>
public static class Autostart
{
    private const string TaskName = "PC Monitor (Autostart)";

    public static bool IsEnabled()
    {
        var (code, _) = Run("schtasks", $"/Query /TN \"{TaskName}\"");
        return code == 0;
    }

    public static bool Set(bool enable)
    {
        if (enable)
        {
            var exe = Environment.ProcessPath;
            var (code, _) = Run("schtasks", $"/Create /F /TN \"{TaskName}\" /TR \"\\\"{exe}\\\" --tray\" /SC ONLOGON /RL HIGHEST /DELAY 0000:20");
            return code == 0;
        }
        else
        {
            var (code, _) = Run("schtasks", $"/Delete /F /TN \"{TaskName}\"");
            return code == 0;
        }
    }

    private static (int, string) Run(string file, string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(file, args)
            {
                CreateNoWindow = true, UseShellExecute = false,
                RedirectStandardOutput = true, RedirectStandardError = true
            });
            var o = p.StandardOutput.ReadToEnd();
            p.WaitForExit(5000);
            return (p.ExitCode, o);
        }
        catch { return (-1, ""); }
    }
}

namespace PcMonitor;

/// <summary>Einfaches Fehlerprotokoll: %APPDATA%\PcMonitor\log.txt</summary>
public static class Log
{
    private static readonly object L = new();
    private static readonly Dictionary<string, DateTime> _last = new();
    public static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PcMonitor", "log.txt");

    public static void Write(string where, Exception ex = null, string msg = null)
    {
        try
        {
            lock (L)
            {
                // gleiche Meldung höchstens einmal pro Minute
                var key = where + (ex?.GetType().Name ?? "") + (ex?.Message ?? msg);
                if (_last.TryGetValue(key, out var t) && (DateTime.Now - t).TotalSeconds < 60) return;
                _last[key] = DateTime.Now;
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                var fi = new FileInfo(FilePath);
                if (fi.Exists && fi.Length > 2_000_000) fi.Delete();
                File.AppendAllText(FilePath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {where}: {msg} {ex}\r\n");
            }
        }
        catch { }
    }
}

namespace PcMonitor;

internal static class Program
{
    public const string ShowEventName = "PcMonitor_ShowWindow_7F3A";

    [STAThread]
    private static void Main(string[] args)
    {
        using var mutex = new Mutex(true, "PcMonitor_SingleInstance_7F3A", out bool first);
        if (!first)
        {
            // Läuft schon (z. B. versteckt im Tray) -> vorhandenes Fenster nach vorne holen
            try { using var ev = EventWaitHandle.OpenExisting(ShowEventName); ev.Set(); } catch { }
            return;
        }

        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Write("Absturz", e.ExceptionObject as Exception, "UnhandledException");
        TaskScheduler.UnobservedTaskException += (_, e) => { Log.Write("Task", e.Exception); e.SetObserved(); };

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        bool shown = false;
        Application.ThreadException += (_, e) =>
        {
            Log.Write("UI", e.Exception);
            if (shown) return; // nur einmal anzeigen, App läuft weiter
            shown = true;
            MessageBox.Show("Es ist ein Fehler aufgetreten, PC Monitor läuft aber weiter.\n\n" + e.Exception.Message +
                            "\n\nDetails: " + Log.FilePath, "PC Monitor", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        };

        try
        {
            bool tray = args.Any(a => a.Equals("--tray", StringComparison.OrdinalIgnoreCase));
            Application.Run(new MainForm(tray));
        }
        catch (Exception ex)
        {
            Log.Write("Main", ex);
            MessageBox.Show("PC Monitor wurde wegen eines Fehlers beendet:\n" + ex.Message + "\n\nDetails: " + Log.FilePath,
                "PC Monitor", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}

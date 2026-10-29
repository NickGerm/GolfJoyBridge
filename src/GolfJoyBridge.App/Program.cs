using System;
using System.IO;
using System.Linq;
using System.Threading;

namespace GolfJoyBridge.App;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        bool restart = args.Contains("--restart");

        // Only one copy at a time. After "Restart" the new copy waits for the old one to exit.
        using var mutex = new Mutex(false, @"Local\GolfJoyBridge");
        bool owned;
        try { owned = mutex.WaitOne(restart ? 10000 : 0); }
        catch (AbandonedMutexException) { owned = true; }
        if (!owned) return 0;

        try
        {
            string baseDir = AppContext.BaseDirectory;
            Log.Init(Path.Combine(baseDir, "logs"));
            Log.Info("---- GolfJoyBridge starting ----");

            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
                Log.Info("Unhandled error: " + e.ExceptionObject);

            string iniPath = Path.Combine(baseDir, "GolfJoyBridge.ini");
            var warnings = new System.Collections.Generic.List<string>();
            var settings = Settings.Load(iniPath, w => { warnings.Add(w); Log.Info("Settings: " + w); });

            var app = new System.Windows.Application
            {
                ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown
            };
            app.DispatcherUnhandledException += (_, e) =>
            {
                Log.Info("Unhandled UI error: " + e.Exception);
                e.Handled = true;
            };

            var controller = new AppController(app, settings, iniPath, baseDir, warnings);
            controller.Start();
            app.Run();
            controller.Dispose();
            return 0;
        }
        finally
        {
            try { mutex.ReleaseMutex(); } catch { }
        }
    }
}

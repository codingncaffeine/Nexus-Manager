using Avalonia;
using NexusManager.Device;

namespace NexusManager.Editor;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Trace the whole startup path. A silent failure anywhere between here
        // and the window's Opened event presents identically: a window frame
        // with nothing in it and nothing on stderr.
        Console.Error.WriteLine("[boot] Main entered");
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Console.Error.WriteLine($"[boot] UNHANDLED: {e.ExceptionObject}");
        TaskScheduler.UnobservedTaskException += (_, e) =>
            Console.Error.WriteLine($"[boot] UNOBSERVED TASK: {e.Exception}");
        try
        {
            Console.Error.WriteLine("[boot] building Avalonia app");
            App.StartHidden = args.Contains("--tray") || args.Contains("--minimized");
            int viewAt = Array.IndexOf(args, "--view");
            if (viewAt >= 0 && viewAt + 1 < args.Length) App.StartView = args[viewAt + 1];
            App.SelfTest = args.Contains("--selftest");
            App.NoDevice = args.Contains("--no-device");
            App.ProbeDrag = args.Contains("--probe-drag");
            App.ProbeAction = args.Contains("--probe-action");
            if (App.ProbeDrag || App.ProbeAction) { App.NoDevice = true; App.StartHidden = true; }
            if (App.SelfTest) App.StartHidden = true;
            // ⛔ SINGLE INSTANCE, NO EXEMPTIONS. Decided before Avalonia starts.
            //
            // Two copies do not merely duplicate a tray icon: they both drive the
            // panel (hidraw interleaves the writes, so nothing errors and the
            // strip just flickers) and, since edits autosave, they both write
            // screens.json and can lose each other's work.
            //
            // --selftest, --probe-drag and --no-device USED to be exempt so they
            // could run beside a live instance. That convenience was mine, and it
            // is exactly how a second app appeared in the user's tray. A
            // diagnostic that needs the app stopped can stop it.
            try
            {
                App.Instance = SingleInstance.TryAcquire("editor");
            }
            catch (IOException ex)
            {
                // The runtime directory is unusable - a configuration fault, not
                // another instance. Say which, or the user hunts for a process
                // that does not exist.
                Console.Error.WriteLine($"Cannot take the panel lock: {ex.Message}");
                Environment.ExitCode = 2;
                return;
            }
            if (App.Instance is null)
            {
                string holder = SingleInstance.DescribeHolder();
                // Launching again is how people ask for the window back, so treat
                // it as exactly that rather than as an error.
                if (!App.SelfTest && !App.ProbeDrag && !App.ProbeAction && SingleInstance.Signal("show"))
                {
                    Console.Error.WriteLine(
                        "Nexus Manager is already running - bringing its window to the front.");
                    return;      // the intent was satisfied, so this is success
                }
                Console.Error.WriteLine(
                    $"Nexus Manager is already running as {holder}. Stop it first.");
                // ⛔ A diagnostic that never RAN must not report success. Main
                // returns void, so this path used to exit 0 - meaning
                // `--selftest` launched while the app was open printed one line
                // and handed back a clean exit, indistinguishable from a full
                // pass. That is the same defect as the exit code `timeout` was
                // supplying, in a different place.
                Environment.Exit(App.SelfTest || App.ProbeDrag || App.ProbeAction ? 2 : 1);
                return;
            }

            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            Console.Error.WriteLine("[boot] lifetime exited normally");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[boot] FATAL: {ex}");
            throw;
        }
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}

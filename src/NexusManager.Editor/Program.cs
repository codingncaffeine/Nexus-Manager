using Avalonia;

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

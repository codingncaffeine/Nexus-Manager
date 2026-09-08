using System.Text.Json;
using System.Text.Json.Serialization;
using NexusManager.Render;
using NexusManager.Sensors;

namespace NexusManager.Editor;

/// <summary>
/// Application preferences, separate from the screen configuration and from the
/// dashboard layout. Kept apart because they answer different questions: this
/// file is about the application's own behaviour, screens.json is about what the
/// panel draws.
/// </summary>
public sealed class AppSettings
{
    public TemperatureScale Scale { get; set; } = TemperatureScale.Celsius;

    /// <summary>Come up with only the tray icon showing. Paired with autostart
    /// this is what keeps the panel alive from login without a window in the way.
    /// </summary>
    public bool StartMinimised { get; set; }

    /// <summary>Closing the window hides it instead of quitting, so dismissing the
    /// window does not blank the panel.</summary>
    public bool CloseToTray { get; set; } = true;

    /// <summary>
    /// Window geometry, so the window comes back the size and place it was left.
    /// Zero width or height means "never saved" and the defaults apply.
    /// </summary>
    public double WindowWidth { get; set; }
    public double WindowHeight { get; set; }
    public int WindowX { get; set; } = int.MinValue;
    public int WindowY { get; set; } = int.MinValue;
    public bool WindowMaximized { get; set; }

    public int Brightness { get; set; } = 100;
    public int TargetFps { get; set; } = 24;

    /// <summary>Music visualizer preferences. Kept here rather than in
    /// screens.json because the visualizer is not a screen: it has no
    /// modules, no buttons and no layout to save.</summary>
    public MusicSettings Music { get; set; } = new();

    public LogSettings Logging { get; set; } = new();

    [JsonIgnore] public string Path { get; set; } = DefaultPath;

    public static string DefaultPath
    {
        get
        {
            string home = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") ?? "";
            if (string.IsNullOrWhiteSpace(home))
                home = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
            return System.IO.Path.Combine(home, "nexus-manager", "settings.json");
        }
    }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(DefaultPath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(DefaultPath), Json)
                       ?? new AppSettings();
        }
        catch (Exception) { /* corrupt settings must not block startup */ }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            File.WriteAllText(Path, JsonSerializer.Serialize(this, Json));
        }
        catch (Exception ex) { Console.Error.WriteLine($"[settings] save failed: {ex.Message}"); }
    }
}

/// <summary>
/// XDG autostart. A desktop entry in ~/.config/autostart is the portable way to
/// come up at login on every desktop the user might run - it needs no systemd
/// unit, no session integration, and works the same on KDE, GNOME and the rest.
/// </summary>
public static class Autostart
{
    private static string Dir
    {
        get
        {
            string home = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") ?? "";
            if (string.IsNullOrWhiteSpace(home))
                home = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
            return Path.Combine(home, "autostart");
        }
    }

    private static string File_ => Path.Combine(Dir, "nexus-manager.desktop");

    public static bool IsEnabled => File.Exists(File_);

    /// <summary>Path of the running executable, so the entry keeps working
    /// whether the app was installed or is being run from a build directory.
    /// </summary>
    private static string Command
    {
        get
        {
            string? exe = Environment.ProcessPath;
            return string.IsNullOrEmpty(exe) ? "nexus-manager-editor" : exe;
        }
    }

    public static void Set(bool enabled)
    {
        try
        {
            if (!enabled) { if (File.Exists(File_)) File.Delete(File_); return; }

            Directory.CreateDirectory(Dir);
            File.WriteAllText(File_, string.Join('\n',
                "[Desktop Entry]",
                "Type=Application",
                "Name=Nexus Manager",
                "Comment=Keeps the Corsair iCUE NEXUS panel running",
                // --tray so login does not throw a window in the user's face.
                $"Exec={Command} --tray",
                "Icon=nexus-manager",
                "Terminal=false",
                "X-GNOME-Autostart-enabled=true",
                "") );
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[autostart] {ex.Message}");
        }
    }
}

/// <summary>
/// What the music visualizer draws. Separate from <see cref="ScreenSpec"/> on
/// purpose: a visualizer has no modules, no buttons and no cell layout, so
/// storing it as a screen would mean a screen whose every layout field is
/// meaningless.
/// </summary>
public sealed class MusicSettings
{
    /// <summary>Defaults to the classic analyser: it is the one every listener
    /// already recognises, so a first run looks like something rather than like
    /// a configuration screen.</summary>
    public VisualizerKind Kind { get; set; } = VisualizerKind.ClassicSpectrum;

    public int BandCount { get; set; } = 32;
    public int Gap { get; set; } = 1;
    public bool ShowPeaks { get; set; } = true;
    public VisualizerPalette Palette { get; set; } = VisualizerPalette.Frequency;
    public string Color { get; set; } = "#3B9AE1";

    /// <summary>Cap colour, independent of the bars. White by default;
    /// green bars under a red cap was the other classic combination.</summary>
    public string PeakColor { get; set; } = "#E8E8F0";

    /// <summary>Frame rate while the visualizer owns the panel. 30 rather than
    /// the 24 the sensor screens use: audio transients read as chunky below
    /// that, and per-mode timing showed 30.0 fps held exactly with the frame
    /// budget half spent.</summary>
    public int TargetFps { get; set; } = 30;
}

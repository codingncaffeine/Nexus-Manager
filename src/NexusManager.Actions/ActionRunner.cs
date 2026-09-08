using System.Diagnostics;

namespace NexusManager.Actions;

/// <summary>
/// Carries out a <see cref="SystemAction"/>.
///
/// Everything runs OFF the caller's thread and nothing is ever waited on from a
/// render loop: launching a program can block for hundreds of milliseconds, and
/// the panel has a 41 ms frame budget.
///
/// Volume and media go through the desktop's own mechanisms rather than through
/// synthetic media keys. On Wayland there is no XTEST, so a media key would need
/// the uinput path for no benefit - and MPRIS reaches the actual player, which
/// media keys only do by luck.
/// </summary>
public sealed class ActionRunner
{
    /// <summary>Host hooks for the two actions this class cannot do alone.</summary>
    public Func<string, bool>? GoToScreen { get; set; }
    public Action<double>? AdjustBrightness { get; set; }

    /// <summary>Last thing that went wrong, for the editor to show.</summary>
    public string? LastError { get; private set; }

    private readonly UinputKeyboard _keyboard = new();

    public void Run(SystemAction action)
    {
        if (action.Kind == ActionKind.None) return;

        // Screen and brightness are immediate and cheap; the rest shells out.
        switch (action.Kind)
        {
            case ActionKind.Screen:
                if (action.Target is { Length: > 0 } name && GoToScreen?.Invoke(name) != true)
                    LastError = $"no screen named '{name}'";
                return;

            case ActionKind.Brightness:
                AdjustBrightness?.Invoke(
                    string.Equals(action.Target, "down", StringComparison.OrdinalIgnoreCase)
                        ? -action.Amount : action.Amount);
                return;
        }

        _ = Task.Run(() =>
        {
            try { RunOffThread(action); }
            catch (Exception ex) { LastError = ex.Message; }
        });
    }

    private void RunOffThread(SystemAction action)
    {
        switch (action.Kind)
        {
            case ActionKind.Launch:
                if (!string.IsNullOrWhiteSpace(action.Target))
                    // Through a shell so a config can use pipes, arguments and
                    // environment the way the user would type it.
                    Start("/bin/sh", ["-c", action.Target]);
                break;

            case ActionKind.Volume:
                Volume(action);
                break;

            case ActionKind.Media:
                Media(action.Target ?? "play-pause");
                break;

            case ActionKind.Key:
                if (!string.IsNullOrWhiteSpace(action.Target))
                {
                    string? err = _keyboard.Press(action.Target);
                    if (err is not null) LastError = err;
                }
                break;
        }
    }

    private void Volume(SystemAction action)
    {
        string dir = (action.Target ?? "up").ToLowerInvariant();
        int step = (int)Math.Clamp(action.Amount, 1, 50);

        if (dir == "mute")
        {
            if (Which("wpctl") is not null)
                Start("wpctl", ["set-mute", "@DEFAULT_AUDIO_SINK@", "toggle"]);
            else if (Which("pactl") is not null)
                Start("pactl", ["set-sink-mute", "@DEFAULT_SINK@", "toggle"]);
            else LastError = "no wpctl or pactl for volume";
            return;
        }

        string sign = dir == "down" ? "-" : "+";
        if (Which("wpctl") is not null)
            // wpctl caps at 1.0 by default only with --limit; without it a
            // repeated press can push a sink past 100% and distort.
            Start("wpctl", ["set-volume", "-l", "1.0", "@DEFAULT_AUDIO_SINK@", $"{step}%{sign}"]);
        else if (Which("pactl") is not null)
            Start("pactl", ["set-sink-volume", "@DEFAULT_SINK@", $"{sign}{step}%"]);
        else
            LastError = "no wpctl or pactl for volume";
    }

    /// <summary>
    /// MPRIS over D-Bus. The bus name of the player is not fixed, so it is looked
    /// up each time - the player that was running when the config was written is
    /// very unlikely to be the one running now.
    /// </summary>
    private void Media(string what)
    {
        string method = what.ToLowerInvariant() switch
        {
            "next"      => "Next",
            "previous"  => "Previous",
            "prev"      => "Previous",
            "stop"      => "Stop",
            "play"      => "Play",
            "pause"     => "Pause",
            _           => "PlayPause",
        };

        string? player = FindMprisPlayer();
        if (player is null) { LastError = "no MPRIS media player is running"; return; }

        if (Which("gdbus") is not null)
            Start("gdbus", ["call", "--session", "--dest", player,
                            "--object-path", "/org/mpris/MediaPlayer2",
                            "--method", $"org.mpris.MediaPlayer2.Player.{method}"]);
        else if (Which("busctl") is not null)
            Start("busctl", ["--user", "call", player, "/org/mpris/MediaPlayer2",
                             "org.mpris.MediaPlayer2.Player", method]);
        else
            LastError = "no gdbus or busctl for media control";
    }

    private static string? FindMprisPlayer()
    {
        try
        {
            if (Which("busctl") is null) return null;
            string output = Capture("busctl", ["--user", "list", "--no-pager", "--no-legend"]);
            foreach (string line in output.Split('\n'))
            {
                string name = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
                if (name.StartsWith("org.mpris.MediaPlayer2.", StringComparison.Ordinal))
                    return name;
            }
        }
        catch (Exception) { }
        return null;
    }

    // --- process helpers ------------------------------------------------------

    private static readonly Dictionary<string, string?> WhichCache = new(StringComparer.Ordinal);

    /// <summary>Cached, because a button can be pressed many times a second and
    /// each miss would otherwise walk PATH again.</summary>
    private static string? Which(string tool)
    {
        lock (WhichCache)
        {
            if (WhichCache.TryGetValue(tool, out string? hit)) return hit;
            string? found = null;
            foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':'))
            {
                if (dir.Length == 0) continue;
                string candidate = Path.Combine(dir, tool);
                if (File.Exists(candidate)) { found = candidate; break; }
            }
            WhichCache[tool] = found;
            return found;
        }
    }

    private static void Start(string file, string[] args)
    {
        var psi = new ProcessStartInfo(file) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi);
        // Drained and abandoned: not waiting for a launched program to exit is
        // the whole point, but a child whose pipes fill up would block forever.
        p?.StandardOutput.ReadToEnd();
        p?.StandardError.ReadToEnd();
    }

    private static string Capture(string file, string[] args)
    {
        var psi = new ProcessStartInfo(file)
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (string a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi);
        if (p is null) return "";
        string output = p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        p.WaitForExit(3000);
        return output;
    }
}

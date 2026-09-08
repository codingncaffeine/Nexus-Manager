using NexusManager.Render;

namespace NexusManager.Editor;

public sealed partial class MainWindow
{
    /// <summary>
    /// Answers one question the self-tests cannot: does the EDITOR'S OWN RENDER
    /// LOOP put a visualizer on the strip?
    ///
    /// ⛔ Every other check passed while a visualizer screen drew nothing in the
    /// editor. The renderer was fine - a screen rendered through ScreenRenderer
    /// with an audio frame produced pixels - and the config was fine. Only the
    /// live path was broken, and nothing measured the live path. This runs the
    /// real Tick against a real capture and compares the frame it produces to the
    /// same frame drawn without audio.
    /// </summary>
    private async Task RunVisualizerProbe()
    {
        Console.Out.WriteLine("[vis] probing the editor's own render loop");

        // ⛔ The probe is launched with a discarded Task, so an exception in
        // Tick would be unobserved: no message, no crash, the window simply
        // stops updating. That is indistinguishable from "the visualizer does
        // not draw", which is the report that started this.
        try { await Probe(); }
        catch (Exception ex)
        {
            Console.Out.WriteLine($"[vis] THREW: {ex.GetType().Name}: {ex.Message}");
            Console.Out.WriteLine(ex.StackTrace);
            EndDiagnostic(1);
        }
    }

    private async Task Probe()
    {

        int index = _set.Screens.FindIndex(s => s.NeedsAudio);
        if (index < 0)
        {
            Console.Out.WriteLine("[vis] no screen in this config carries a visualizer");
            EndDiagnostic(2);
            return;
        }

        _screenIndex = index;
        var screen = _set.Screens[index];
        Console.Out.WriteLine($"[vis] screen '{screen.Name}' has {screen.Visualizers.Count} "
                            + $"visualizer(s): {string.Join(", ", screen.Visualizers.Select(v => v.Kind))}");
        // ⛔ The SAME sequence the screen list uses. Setting the index and
        // rebuilding state alone leaves the Name field bound to the old screen
        // while its handler writes to the CURRENT one - which renamed a screen
        // to its neighbour's name. An instrument that corrupts the thing it
        // measures is worse than no instrument.
        StructureChanged();
        RefreshThemePanel();

        // Let the capture open and the render loop settle.
        for (int i = 0; i < 60; i++)
        {
            Tick();
            await Task.Delay(50);
            if (_panelAudio?.Current.Sequence > 5) break;
        }

        var cap = _panelAudio;
        Console.Out.WriteLine($"[vis] capture: {(cap is null ? "NOT OPEN" : "open")}"
                            + (cap is null ? "" : $", seq {cap.Current.Sequence}, "
                                               + $"peak {cap.Current.PeakDbfs:F1} dBFS, "
                                               + $"{(cap.Current.Silent ? "gate shut" : "gate open")}"));
        Console.Out.WriteLine($"[vis] tick interval: {_timer.Interval.TotalMilliseconds:0} ms");

        // Ink in the frame the loop actually produced.
        Tick();
        long withAudio = Ink(_preview.Frame);

        // The same screen with the capture removed, as a control: if these agree,
        // the visualizer contributed nothing.
        var keep = _panelAudio;
        _panelAudio = null;
        Tick();
        long without = Ink(_preview.Frame);
        _panelAudio = keep;

        Console.Out.WriteLine($"[vis] frame ink with audio: {withAudio}, without: {without}");
        bool drew = withAudio > without + 2000;
        Console.Out.WriteLine(drew
            ? "[vis] PASS - the editor draws the visualizer"
            : "[vis] FAIL - the editor's frame is the same with and without audio");

        EndDiagnostic(drew ? 0 : 1);
    }

    /// <summary>Total non-background subpixel value, as a cheap "is anything
    /// drawn here" measure.</summary>
    private static long Ink(byte[] bgra)
    {
        long sum = 0;
        for (int i = 0; i + 3 < bgra.Length; i += 4)
            sum += bgra[i] + bgra[i + 1] + bgra[i + 2];
        return sum;
    }
}

using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using NexusManager.Actions;
using NexusManager.Render;

namespace NexusManager.Editor;

public sealed partial class MainWindow
{
    /// <summary>Set by --probe-action so the action handlers say when they fire.
    /// A handler that runs when nobody clicked anything is the whole suspicion
    /// here, and it cannot be seen from outside.</summary>
    public static bool TraceActions;

    /// <summary>
    /// Ends a diagnostic run with a real exit code.
    ///
    /// ⛔ Posting Shutdown() is not enough once the window has been Show()n: the
    /// process stays alive, so every diagnostic in here only ever terminated
    /// because it was wrapped in `timeout` - and `timeout` then supplied 124 as
    /// the exit code, whatever the run had concluded. HANDOFF.md documents
    /// --selftest as "exits non-zero on failure". It never did, so nothing could
    /// gate on it, and a green run and a red one were indistinguishable to any
    /// caller that looked at $?.
    /// </summary>
    private void EndDiagnostic(int code)
    {
        Console.Out.Flush();
        _quitting = true;
        Dispatcher.UIThread.Post(() =>
        {
            if (Avalonia.Application.Current?.ApplicationLifetime
                is IClassicDesktopStyleApplicationLifetime desktop)
                desktop.Shutdown(code);
        });
        _ = Task.Run(async () =>
        {
            await Task.Delay(1500);
            Console.Out.Flush();
            Environment.Exit(code);
        });
    }

    /// <summary>
    /// The last open question about the Volume buttons, answered by measurement.
    ///
    /// The stored config has a button labelled "Volume Down", carrying the
    /// VolumeDown icon, whose action is still "up". Label and Icon are edited in
    /// the same panel by the same mechanism and both persisted, so either the
    /// Direction dropdown fails to write, or something rewrites Target after it
    /// does. Reading the wiring says it writes. Reading the wiring has been
    /// wrong twice on this project.
    ///
    /// Three links are reported separately, so a break is located and not
    /// inferred:
    ///   1. does merely REBUILDING the property panel change Target?
    ///   2. does driving the dropdown reach the model?
    ///   3. does it reach the file?
    ///
    /// Test 1 is the one worth having. RefreshThemePanel holds _building for the
    /// whole rebuild and releases it at the end; the Action-kind dropdown's
    /// handler unconditionally resets Target to "up" for Volume. If that handler
    /// can fire after the guard drops, a rebuild silently undoes the edit and
    /// leaves Label and Icon untouched - which is exactly the state on disk.
    /// </summary>
    private async Task RunActionProbe()
    {
        try
        {
            TraceActions = true;
            ShowView("panel");
            Show();
            UpdateLayout();
            Dispatcher.UIThread.RunJobs(DispatcherPriority.Loaded);

            Console.Out.WriteLine($"[action] config file     {Config.Path}");

            int si = _set.Screens.FindIndex(s => s.Buttons.Count > 0);
            if (si < 0)
            {
                Console.Out.WriteLine("[action] no screen carries a button; nothing to probe");
                return;
            }
            _screenIndex = si;
            _moduleIndex = 0;
            RebuildScreenState();
            RefreshModuleList();
            RefreshThemePanel();
            UpdateLayout();
            Console.Out.WriteLine(
                $"[action] screen          [{si}] '{Screen.Name}', {Screen.Buttons.Count} button(s)");

            for (int i = 0; i < Screen.Buttons.Count; i++)
            {
                var spec = Screen.Buttons[i];
                _btnIndex = i;
                RefreshThemePanel();
                UpdateLayout();
                Dispatcher.UIThread.RunJobs(DispatcherPriority.Loaded);

                Console.Out.WriteLine("");
                Console.Out.WriteLine(
                    $"[action] button [{i}]       '{spec.Label}'  icon={spec.Icon}  action={spec.Action}");

                if (spec.Action.Kind is not (ActionKind.Volume or ActionKind.Brightness))
                {
                    Console.Out.WriteLine("[action]   kind carries no Direction dropdown; skipped");
                    continue;
                }

                string[] opts = spec.Action.Kind == ActionKind.Volume
                    ? ["up", "down", "mute"] : ["up", "down"];
                string want = spec.Action.Target == "down" ? "up" : "down";

                // --- 1. does a bare rebuild change it? --------------------------
                spec.Action.Target = want;
                RefreshThemePanel();
                UpdateLayout();
                Dispatcher.UIThread.RunJobs(DispatcherPriority.Loaded);
                bool stomped = spec.Action.Target != want;
                Console.Out.WriteLine(
                    $"[action]   rebuild        set '{want}' in the model, then rebuilt the panel "
                    + $"-> '{spec.Action.Target}'  {(stomped ? "*** REBUILD STOMPED IT ***" : "survived")}");

                // --- 2. does the dropdown reach the model? ----------------------
                var combo = FindChoiceBox(opts);
                if (combo is null)
                {
                    Console.Out.WriteLine("[action]   dropdown       *** NOT FOUND in the property panel ***");
                    continue;
                }
                Console.Out.WriteLine(
                    $"[action]   dropdown       shows '{combo.SelectedItem}', model holds '{spec.Action.Target}'"
                    + $"  visible={combo.IsVisible} enabled={combo.IsEnabled}");
                if (!Equals(combo.SelectedItem?.ToString(), spec.Action.Target))
                    Console.Out.WriteLine("[action]   MISMATCH       the dropdown and the model disagree");

                string other = spec.Action.Target == "down" ? "up" : "down";
                combo.SelectedItem = other;      // raises the real SelectionChanged
                Dispatcher.UIThread.RunJobs();
                Console.Out.WriteLine(
                    $"[action]   drove it       picked '{other}' -> model '{spec.Action.Target}'  "
                    + $"{(spec.Action.Target == other ? "OK" : "*** MODEL NOT UPDATED ***")}");

                // --- 3. does it reach the file? ---------------------------------
                await SaveScreensAsync();
                var back = await Config.LoadAsync(null);
                string? disk = back?.Screens.ElementAtOrDefault(si)
                                   ?.Buttons.ElementAtOrDefault(i)?.Action.Target;
                Console.Out.WriteLine(
                    $"[action]   on disk        '{disk}'  "
                    + $"{(disk == spec.Action.Target ? "OK" : "*** NOT PERSISTED ***")}");
            }
        }
        catch (Exception ex)
        {
            Console.Out.WriteLine($"[action] FAILED: {ex}");
        }
        finally
        {
            TraceActions = false;
            EndDiagnostic(0);
        }
    }

    /// <summary>
    /// The dropdown carrying exactly these options. Found by its CONTENTS rather
    /// than by walking to a label, because the label is a sibling in a Row and
    /// the contents identify it unambiguously.
    /// </summary>
    private ComboBox? FindChoiceBox(string[] options)
    {
        foreach (var cb in _themeProps.GetLogicalDescendants().OfType<ComboBox>())
        {
            var items = (cb.ItemsSource as System.Collections.IEnumerable)?
                        .Cast<object?>().Select(o => o?.ToString()).ToArray();
            if (items is null || items.Length != options.Length) continue;
            if (items.Zip(options).All(p => p.First == p.Second)) return cb;
        }
        return null;
    }
}

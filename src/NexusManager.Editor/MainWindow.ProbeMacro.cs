using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using NexusManager.Actions;
using NexusManager.Render;

namespace NexusManager.Editor;

public sealed partial class MainWindow
{
    /// <summary>
    /// Measures whether the macro editor exists, renders, commits and is
    /// REACHABLE - the last of which is the whole point, since the model, the
    /// runner and the .cuescreens importer all worked for a day while there was
    /// no case for Macro in the Action switch and no way in from the app.
    ///
    /// Run: nexus-manager-editor --probe-macro [pack.cuescreens]
    ///
    /// ⛔ Every check below has a negative control that is actually RUN, not
    /// described. Two checks in this project have shipped green against code
    /// that could not fail them - one on an unreachable path, one asserting a
    /// counter the probe had just written.
    ///
    /// ⛔ NOTHING HERE TYPES. The one macro this runs consists of a single 1 ms
    /// wait, which never reaches the keyboard code at all - /dev/uinput is not
    /// even opened. A probe that ran a real macro would type into whatever
    /// window happened to be focused on this machine.
    /// </summary>
    private async Task RunMacroProbe()
    {
        int fail = 0;
        void Check(string name, bool ok, string detail)
        {
            if (!ok) fail++;
            Console.Out.WriteLine($"[macro] {name,-14} {(ok ? "OK  " : "FAIL")} {detail}");
        }
        void Control(string name, bool caught) =>
            Console.Out.WriteLine($"[macro] {name,-14} {(caught ? "ctl " : "CTL!")} "
                + (caught ? "negative control fails as it must"
                          : "*** NEGATIVE CONTROL PASSED - this check cannot fail ***"));

        try
        {
            ShowView("panel");
            Show();
            UpdateLayout();
            Dispatcher.UIThread.RunJobs(DispatcherPriority.Loaded);

            // --- 1. a 3-step macro renders 3 rows --------------------------------
            // Read off the realised tree, never off a count the probe wrote.
            var three = Sample();
            var dialog = MacroEditorWindow.ForTest(three, _actions);
            (dialog.Content as Control)?.Measure(new Size(640, 560));
            int rows = dialog.Rows.Count;
            Check("rows", rows == 3, $"3-step macro drew {rows} row(s)");

            var four = Sample();
            four.Steps.Add(new MacroStep { Kind = MacroStepKind.Tap, Key = "v" });
            var fourDialog = MacroEditorWindow.ForTest(four, _actions);
            (fourDialog.Content as Control)?.Measure(new Size(640, 560));
            Control("rows", fourDialog.Rows.Count != 3);

            // --- 2. a hold/release pair reads as a hold/release pair -------------
            // This is the shape the .cuescreens importer produces: iCUE has no
            // Tap, so an imported macro is two rows, and D70 says we must NOT
            // collapse them. If a pack was named on the command line its own
            // first macro is rendered too.
            var pair = new MacroSpec
            {
                Steps =
                [
                    new MacroStep { Kind = MacroStepKind.KeyDown, Key = "q" },
                    new MacroStep { Kind = MacroStepKind.KeyUp,   Key = "q" },
                    new MacroStep { Kind = MacroStepKind.Delay,   DelayMs = 400 },
                ],
            };
            var pairDialog = MacroEditorWindow.ForTest(pair, _actions);
            (pairDialog.Content as Control)?.Measure(new Size(640, 560));
            string row2 = pairDialog.Rows.ElementAtOrDefault(1)?.Describe() ?? "(no row)";
            string row3 = pairDialog.Rows.ElementAtOrDefault(2)?.Describe() ?? "(no row)";
            Check("row text", row2 == "2 q release", $"row 2 drew '{row2}'");
            Check("delay text", row3 == "3 400 ms", $"row 3 drew '{row3}'");
            Control("row text", row2 != "2 q hold");

            ImportedPack();

            // --- 3. Cancel cannot touch the original -----------------------------
            var original = Sample();
            string before = JsonSerializer.Serialize(original);
            var copy = MacroEditorWindow.Clone(original);
            copy.Steps[0].Key = "z";
            copy.Steps.Add(new MacroStep { Kind = MacroStepKind.Delay, DelayMs = 9999 });
            copy.RepeatCount = 42;
            copy.Repeat = MacroRepeat.UntilPressedAgain;
            Check("cancel", JsonSerializer.Serialize(original) == before,
                  "editing a clone left the original byte-identical");
            original.Steps[0].Key = "z";
            Control("cancel", JsonSerializer.Serialize(original) != before);

            // --- 4. the route into the dialog EXISTS -----------------------------
            int si = _set.Screens.FindIndex(s => s.Buttons.Count > 0);
            bool temporary = si < 0;
            if (temporary)
            {
                // A config with no buttons must still be probeable, and adding
                // one to the user's own screens.json is not acceptable - so it
                // is added, measured, and taken away again with no save.
                si = 0;
                _set.Screens[0].Buttons.Add(new ButtonSpec { Label = "probe" });
            }
            _screenIndex = si;
            _btnIndex = _set.Screens[si].Buttons.Count - 1;
            RebuildScreenState();
            RefreshThemePanel();

            var button = _set.Screens[si].Buttons[_btnIndex];
            var kept = new SystemAction
            {
                Kind = button.Action.Kind, Target = button.Action.Target,
                Macro = button.Action.Macro, Amount = button.Action.Amount,
            };

            button.Action.Kind = ActionKind.Volume;
            button.Action.Target = "up";
            RefreshThemePanel();
            _themeProps.Measure(new Size(400, 3000));
            bool onVolume = HasEditMacroButton();

            button.Action.Kind = ActionKind.Macro;
            button.Action.Macro = null;
            RefreshThemePanel();
            _themeProps.Measure(new Size(400, 3000));
            bool onMacro = HasEditMacroButton();
            Check("route", onMacro, "'Edit macro…' appears the moment Macro is chosen");
            Control("route", !onVolume);

            // --- 5. an empty macro READS as empty, and Save changes that ---------
            string emptyLabel = ButtonListEntry(_btnIndex);
            Check("empty label", emptyLabel.Contains("macro (empty)"),
                  $"buttons list reads '{emptyLabel}'");

            button.Action.Macro = Sample();          // what Save assigns
            // ⛔ NOT Changed(): that queues an autosave, and a probe that
            // rewrites the user's screens.json has changed the thing it was
            // measuring. RefreshThemePanel alone rebuilds the Buttons list.
            RefreshThemePanel();
            string savedLabel = ButtonListEntry(_btnIndex);
            Check("save label", savedLabel.Contains("3 steps"),
                  $"buttons list now reads '{savedLabel}'");
            Control("save label", !emptyLabel.Contains("3 steps"));

            // --- 6. the key picker offers every code, once -----------------------
            var codes = KeyCatalog.All.Select(e => e.Code).ToList();
            int distinct = codes.Distinct().Count();
            Check("keys unique", codes.Count == distinct,
                  $"{codes.Count} entries over {distinct} codes");
            Check("keys covered", KeyCatalog.Missing.Count == 0,
                  KeyCatalog.Missing.Count == 0
                      ? $"all {distinct} codes in UinputKeyboard are listed"
                      : $"not listed: {string.Join(", ", KeyCatalog.Missing)}");
            // Without the dedupe the same list is built straight from the name
            // table - 103 names over 94 codes - so the check must reject it.
            var undeduped = UinputKeyboard.KeyCodes.Values.ToList();
            Control("keys unique", undeduped.Count != undeduped.Distinct().Count());

            // --- 7. a macro button works on EVERY press, not every other one ------
            // ⛔ Found while wiring this up: the run's CancellationTokenSource was
            // never cleared, so RunMacro's "already running, so stop it" branch
            // swallowed the next press. One 1 ms WAIT, no keys - see the note on
            // this method.
            var wait = new MacroSpec { Steps = [new MacroStep { Kind = MacroStepKind.Delay, DelayMs = 1 }] };
            var action = new SystemAction { Kind = ActionKind.Macro, Macro = wait };
            _actions.Run(action);
            var first = _actions.MacroCompletion;
            await first;
            bool idle = !_actions.MacroRunning;
            _actions.Run(action);
            var second = _actions.MacroCompletion;
            await second;
            Check("toggle", !ReferenceEquals(first, second) && idle,
                  idle ? "a second press started a second run"
                       : "the runner still reported a finished macro as running");


            // --- 8. a tab round trip, WITH THE DIALOG ROOTED ---------------------
            // ⛔ This is the check that was missing, and its absence cost a crash
            // in the user's hands. The self test switched tabs on an UNROOTED
            // dialog and passed: an unrooted control does not apply its template
            // the same way, so the ScrollViewer never adopted the row panel and
            // the second one never collided with the first. The dialog has to be
            // SHOWN for this to mean anything - the same lesson as probing a
            // control theme on an unrooted control, which also reported a false
            // result here once.
            var tabbed = MacroEditorWindow.ForTest(Sample(), _actions);
            string tabError = "";
            object? firstBody = null, secondBody = null;
            try
            {
                tabbed.Show();
                tabbed.UpdateLayout();
                Dispatcher.UIThread.RunJobs(DispatcherPriority.Loaded);
                firstBody = tabbed.TabBody;
                tabbed.SelectTab(1);
                tabbed.UpdateLayout();
                Dispatcher.UIThread.RunJobs(DispatcherPriority.Loaded);
                tabbed.SelectTab(0);
                tabbed.UpdateLayout();
                Dispatcher.UIThread.RunJobs(DispatcherPriority.Loaded);
                secondBody = tabbed.TabBody;
            }
            catch (Exception ex) { tabError = $"{ex.GetType().Name}: {ex.Message}"; }
            int afterTrip = tabbed.Rows.Count;
            try { tabbed.Close(); } catch (Exception) { }

            Check("tab trip", tabError.Length == 0 && afterTrip == 3,
                  tabError.Length > 0 ? tabError : $"Events -> General -> Events, {afterTrip} rows intact");
            // The structural rule behind it, asserted directly so a future
            // rebuild of the tab body is caught without needing a window: the
            // row panel has exactly ONE owner for the life of the dialog.
            Check("tab identity", firstBody is not null && ReferenceEquals(firstBody, secondBody),
                  "the Events body is the same control after a round trip");

            // --- put the config back exactly as it was ---------------------------
            if (temporary) _set.Screens[si].Buttons.RemoveAt(_btnIndex);
            else
            {
                button.Action.Kind = kept.Kind;
                button.Action.Target = kept.Target;
                button.Action.Macro = kept.Macro;
                button.Action.Amount = kept.Amount;
            }
            _btnIndex = 0;
            RebuildScreenState();
            RefreshThemePanel();
            // ⛔ Belt and braces: anything earlier in the run that queued a save
            // is cancelled here, so the exit path has nothing to flush.
            _autosave.Stop();
            Console.Out.WriteLine("[macro] config restored; nothing was saved");
        }
        catch (Exception ex)
        {
            fail++;
            Console.Out.WriteLine($"[macro] FAILED: {ex}");
        }

        // On the failure path too: an exception must not leave a queued write.
        _autosave.Stop();
        Console.Out.WriteLine(fail == 0 ? "[macro] PASS" : $"[macro] FAIL ({fail})");
        EndDiagnostic(fail == 0 ? 0 : 1);
    }

    /// <summary>Three steps that exercise all three chip kinds.</summary>
    private static MacroSpec Sample() => new()
    {
        Steps =
        [
            new MacroStep { Kind = MacroStepKind.KeyDown, Key = "ctrl" },
            new MacroStep { Kind = MacroStepKind.Tap,     Key = "c" },
            new MacroStep { Kind = MacroStepKind.KeyUp,   Key = "ctrl" },
        ],
    };

    /// <summary>Renders the first macro out of a real pack, when one is named.
    /// The six official packs live outside the repository, so this cannot be a
    /// required check - but when they are to hand it is the only end-to-end
    /// read of importer output through the editor.</summary>
    private void ImportedPack()
    {
        if (App.ProbeMacroPack is not { } pack) return;
        if (!File.Exists(pack))
        {
            Console.Out.WriteLine($"[macro] pack           skipped: no file at {pack}");
            return;
        }
        try
        {
            var result = CueScreensImport.Load(pack, Path.GetTempPath());
            var macro = result.Screens
                .SelectMany(s => s.Buttons)
                .Select(b => b.Action.Macro)
                .FirstOrDefault(m => m is { Steps.Count: > 0 });
            if (macro is null)
            {
                Console.Out.WriteLine("[macro] pack           no macro in that pack");
                return;
            }
            var dialog = MacroEditorWindow.ForTest(macro, _actions);
            (dialog.Content as Control)?.Measure(new Size(640, 560));
            Console.Out.WriteLine(
                $"[macro] pack           {dialog.Rows.Count} rows from {Path.GetFileName(pack)}");
            foreach (var row in dialog.Rows.Take(6))
                Console.Out.WriteLine($"[macro]   {row.Describe()}");
        }
        catch (Exception ex)
        {
            Console.Out.WriteLine($"[macro] pack           FAILED: {ex.Message}");
        }
    }

    /// <summary>Looks for the entry point in the REALISED property panel, by its
    /// content, so it cannot be satisfied by a field the probe can see but a
    /// user cannot.</summary>
    private bool HasEditMacroButton() =>
        _themeProps.GetLogicalDescendants().OfType<Button>()
            .Any(b => (b.Content as string)?.StartsWith("Edit macro") == true);

    private string ButtonListEntry(int index) =>
        _btnList.ItemsSource?.Cast<object>().ElementAtOrDefault(index)?.ToString() ?? "(none)";
}

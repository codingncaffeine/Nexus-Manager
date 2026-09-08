using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;

namespace NexusManager.Editor;

public sealed partial class MainWindow
{
    /// <summary>
    /// Builds every view once and reports which ones threw, then quits.
    ///
    /// This exists because the Panel tab shipped broken: a control was added to
    /// two parents, Avalonia threw on the second Add, and the only way to hit it
    /// was to click that tab. A crash that needs a mouse cannot be checked from a
    /// script, so nothing caught it.
    ///
    /// Run: nexus-manager-editor --selftest   (exit code 0 = every view built)
    /// </summary>
    private void RunSelfTest()
    {
        int failures = 0;

        foreach (string id in new[] { "home", "dashboard", "panel" })
        {
            try
            {
                ShowView(id);
                // ShowView only assigns content; forcing a layout pass is what
                // actually realises the controls, and some failures only appear
                // there rather than at construction.
                (_viewHost.Content as Control)?.Measure(new Avalonia.Size(1200, 800));
                Console.Out.WriteLine($"[selftest] {id,-10} OK");
            }
            catch (Exception ex)
            {
                failures++;
                Console.Out.WriteLine($"[selftest] {id,-10} FAILED: {ex.GetType().Name}: {ex.Message}");
            }
        }

        // The panel preview IS the resize control now. Assert the interaction
        // actually exists: the bug this replaces was a widget that rendered
        // NOTHING on a screen with no readouts, under a caption promising
        // dividers. Constructing the view proved nothing about that.
        try
        {
            var probe = new PanelPreview(scale: 1, interactive: true);
            var twoCells = new NexusManager.Render.ScreenSpec();
            twoCells.Modules.Add(new NexusManager.Render.ModuleSpec());
            twoCells.Modules.Add(new NexusManager.Render.ModuleSpec());
            probe.SetScreen(twoCells);
            // Spacers bracket the real cells, so N cells give N+1 boundaries:
            // the outer two are the gaps before and after.
            int two = probe.DividerCount;

            // ⛔ The case the user hit: ONE button and nothing else. A lone cell
            // has no neighbour, so before spacers existed it showed NO handles at
            // all and could not be sized.
            var oneCell = new NexusManager.Render.ScreenSpec();
            oneCell.Buttons.Add(new NexusManager.Render.ButtonSpec());
            probe.SetScreen(oneCell);
            int lone = probe.DividerCount;

            probe.SetScreen(new NexusManager.Render.ScreenSpec());   // no cells
            int none = probe.DividerCount;

            var flat = new PanelPreview(scale: 1);                   // Home card

            // Tap dispatch: the user reported two buttons where every tap fired
            // the FIRST one. The cause turned out to be both buttons configured
            // with the same action, not a bad hit test - so pin the hit test down
            // here, and never chase it again.
            var twoButtons = new NexusManager.Render.ScreenSpec();
            twoButtons.Buttons.Add(new NexusManager.Render.ButtonSpec { Label = "L" });
            twoButtons.Buttons.Add(new NexusManager.Render.ButtonSpec { Label = "R" });
            var (_, btnLayout) = NexusManager.Render.ScreenLayout.ComputeAll(twoButtons, out _);
            int leftHit = NexusManager.Render.ScreenLayout.HitTest(btnLayout, 100);
            int rightHit = NexusManager.Render.ScreenLayout.HitTest(btnLayout, 500);
            if (leftHit != 0 || rightHit != 1)
            {
                failures++;
                Console.Out.WriteLine($"[selftest] taps       FAILED: x=100 -> {leftHit} (want 0), "
                                      + $"x=500 -> {rightHit} (want 1)");

            }
            else Console.Out.WriteLine("[selftest] taps       OK");

            // ⛔ The grab zone must be expressed in SCREEN pixels. It was 6 units
            // of a 1280-unit control drawn at ~0.7 scale - four real pixels - and
            // that alone made drag-to-resize feel completely dead.
            probe.SetScreen(twoCells);
            double grip = probe.Overlay.GripScreenPixels;

            if (two != 3 || lone != 2 || none != 0 || !probe.Interactive
                || flat.Interactive || grip < 8)
            {
                failures++;
                Console.Out.WriteLine($"[selftest] resize     FAILED: dividers 2-cell={two} "
                                      + $"(want 3), 1-cell={lone} (want 2), "
                                      + $"empty={none} (want 0), "
                                      + $"interactive={probe.Interactive} (want True), "
                                      + $"home-card interactive={flat.Interactive} (want False), "
                                      + $"grip={grip:0.#}px (want >= 8)");
            }
            else Console.Out.WriteLine("[selftest] resize     OK");
        }
        catch (Exception ex)
        {
            failures++;
            Console.Out.WriteLine($"[selftest] resize     FAILED: {ex.Message}");
        }

        // The colour wheel lives in a separate package. A templated control with
        // no control theme applies NO template and draws an empty box, silently.
        //
        // Ask the RESOURCE SYSTEM whether the theme is registered, rather than
        // templating a probe control: a control that is not rooted in a visual
        // tree has no styling parent, so it finds no theme even when one exists.
        // The first version of this check did that and reported a false failure.
        try
        {
            bool found = Avalonia.Application.Current is IResourceHost host
                         && host.TryFindResource(typeof(ColorSpectrum), out object? theme)
                         && theme is not null;
            if (!found)
            {
                failures++;
                Console.Out.WriteLine("[selftest] colourwheel FAILED: no ColorSpectrum control "
                                      + "theme registered (App must StyleInclude "
                                      + "avares://Avalonia.Controls.ColorPicker/Themes/Fluent/Fluent.xaml)");
            }
            else Console.Out.WriteLine("[selftest] colourwheel OK");
        }
        catch (Exception ex)
        {
            failures++;
            Console.Out.WriteLine($"[selftest] colourwheel FAILED: {ex.Message}");
        }

        // Switching back and forth catches state that only breaks on re-entry -
        // a cached view being re-parented, a handler added twice.
        foreach (string id in new[] { "dashboard", "panel", "home", "panel" })
        {
            try { ShowView(id); }
            catch (Exception ex)
            {
                failures++;
                Console.Out.WriteLine($"[selftest] revisit {id,-8} FAILED: {ex.Message}");
            }
        }
        if (failures == 0) Console.Out.WriteLine("[selftest] revisits  OK");

        // ⛔ A LIST TEMPLATE MUST TOLERATE A NULL ITEM. Avalonia hands a
        // recycling template `null` while it reuses containers, and a template
        // that dereferenced it crashed the application the moment the Panel tab
        // was looked at - AFTER this self test had printed PASS, because
        // building a view never renders a single list item. A check that blesses
        // a build which dies on sight is worse than no check.
        try
        {
            ApplyModuleListTemplate();
            var tmpl = _moduleList.ItemTemplate;
            if (tmpl is null)
            {
                failures++;
                Console.Out.WriteLine("[selftest] itemtemplate FAILED: no template on the module list");
            }
            else
            {
                tmpl.Build(null);                       // the recycling case
                var real = tmpl.Build(new ModuleRow(
                    "1. probe", Avalonia.Media.Colors.White,
                    NexusManager.Sensors.SensorKind.Temperature));
                if (real is null)
                {
                    failures++;
                    Console.Out.WriteLine("[selftest] itemtemplate FAILED: built nothing for a real row");
                }
                else Console.Out.WriteLine("[selftest] itemtemplate OK");
            }
        }
        catch (Exception ex)
        {
            failures++;
            Console.Out.WriteLine($"[selftest] itemtemplate FAILED: {ex.GetType().Name}: {ex.Message}");
        }

        Console.Out.WriteLine(failures == 0
            ? "[selftest] PASS"
            : $"[selftest] FAIL ({failures})");
        EndDiagnostic(failures == 0 ? 0 : 1);
    }
}

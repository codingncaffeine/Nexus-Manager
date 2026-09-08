using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using NexusManager.Actions;
using NexusManager.Cli;
using NexusManager.Device;
using NexusManager.Render;
using NexusManager.Sensors;
using SkiaSharp;

JsonSerializerOptions JsonOpts = Config.Json;

// Instruments write through a pipe to tee often enough that buffering has
// already cost two captures: .NET buffers stdout to a pipe, so a process that
// is killed rather than exited loses its report entirely and reads as "it
// printed nothing".
Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });

string cmd = args.Length > 0 ? args[0] : "help";
bool fahrenheit = args.Contains("--fahrenheit") || args.Contains("-f");

switch (cmd)
{
    case "sensors":
    {
        // Mirrors iCUE's "Add Home Sensors" dialog: grouped by device, each with
        // a friendly name and a category, not a flat list of driver names.
        using var reg = SensorRegistry.CreateDefault();
        reg.Scale = fahrenheit ? TemperatureScale.Fahrenheit : TemperatureScale.Celsius;
        reg.Sample();
        await Task.Delay(1100);          // let the delta-based and GPU sources fill in
        reg.Sample();

        int total = 0;
        foreach (var group in reg.ByDevice())
        {
            Console.WriteLine();
            Console.WriteLine($"  {group.Key.Name.ToUpperInvariant()}");
            Console.WriteLine($"  {CategoryNames.Display(group.Key.Category)}");
            foreach (var s in group.OrderBy(x => x.Label, StringComparer.OrdinalIgnoreCase))
            {
                double v = reg.Read(s.Key);
                string shown = double.IsNaN(v)
                    ? "--"
                    : v.ToString(s.Unit is Unit.Celsius or Unit.Volt ? "F2" : "F0",
                                 CultureInfo.InvariantCulture);
                Console.WriteLine($"      {s.Label,-22} {shown,10} {Units.Symbol(s.Unit, reg.Scale),-5} {s.Key}");
                total++;
            }
        }
        Console.WriteLine();
        Console.WriteLine($"  {total} sensors across {reg.ByDevice().Count()} devices.");
        break;
    }

    case "watch":
    {
        // Is the data actually live? Print the same readings the renderer sees,
        // once a second, so a static display can be told apart from static data.
        using var reg = SensorRegistry.CreateDefault();
        reg.Scale = fahrenheit ? TemperatureScale.Fahrenheit : TemperatureScale.Celsius;
        string[] keys = args.Length > 1
            ? args[1..].Where(a => !a.StartsWith('-')).ToArray()
            : ["hwmon.k10temp.Tctl", "cpu.load", "gpu.0.temp", "mem.used.percent"];

        Console.WriteLine("t     " + string.Join("  ", keys.Select(k => k.PadLeft(12))));
        for (int t = 0; t < 12; t++)
        {
            Console.WriteLine($"{t,-5} " + string.Join("  ",
                keys.Select(k => reg.Read(k).ToString("F2", CultureInfo.InvariantCulture).PadLeft(12))));
            await Task.Delay(1000);
        }
        break;
    }

    case "profile":
    {
        // Where does a frame actually go? Measured, because the run loop is
        // hitting 15 fps against a device ceiling of 65.
        using var dev2 = NexusDevice.Open();
        using var reg = SensorRegistry.CreateDefault();
        var screen = JsonSerializer.Deserialize<ScreenSpec>(
            await File.ReadAllTextAsync("screen.json"), JsonOpts) ?? new ScreenSpec();
        var layout = ScreenLayout.Compute(screen, out _);
        reg.SetActive(screen.Modules.Select(m => m.Source));   // as the run loop does
        var hists = layout.Select(_ => new History(160)).ToList();
        using var canvas = new NexusCanvas();
        using var renderer = new ScreenRenderer(screen.Theme);
        var frame = new byte[NexusDevice.FrameBytes];

        double tSample = 0, tDraw = 0, tCopy = 0, tPush = 0;
        const int N = 60;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < N; i++)
        {
            double a = sw.Elapsed.TotalMilliseconds;
            reg.Sample();
            double b = sw.Elapsed.TotalMilliseconds;
            renderer.Draw(canvas, layout, hists, k => { double v = reg.Read(k); return double.IsNaN(v) ? 0 : v; });
            double c = sw.Elapsed.TotalMilliseconds;
            canvas.CopyTo(frame);
            double d = sw.Elapsed.TotalMilliseconds;
            dev2.PushFrame(frame);
            double e = sw.Elapsed.TotalMilliseconds;
            tSample += b - a; tDraw += c - b; tCopy += d - c; tPush += e - d;
        }
        double total = tSample + tDraw + tCopy + tPush;
        Console.WriteLine($"  per frame, averaged over {N}:");
        Console.WriteLine($"    reg.Sample()   {tSample / N,7:0.00} ms   {tSample / total * 100,5:0.0}%");
        Console.WriteLine($"    renderer.Draw  {tDraw / N,7:0.00} ms   {tDraw / total * 100,5:0.0}%");
        Console.WriteLine($"    canvas.CopyTo  {tCopy / N,7:0.00} ms   {tCopy / total * 100,5:0.0}%");
        Console.WriteLine($"    dev.PushFrame  {tPush / N,7:0.00} ms   {tPush / total * 100,5:0.0}%");
        Console.WriteLine($"    TOTAL          {total / N,7:0.00} ms  -> {1000.0 / (total / N):0.0} fps ceiling");
        Console.WriteLine($"    24 fps needs the total under 41.7 ms");
        break;
    }

    case "info":
    {
        using var dev = NexusDevice.Open();
        var (w, h, bpp) = dev.ReadGeometry();
        Console.WriteLine($"Firmware  : {dev.ReadFirmware()}");
        Console.WriteLine($"Geometry  : {w}x{h}, {bpp} bytes/pixel (reported by the device)");
        break;
    }

    case "init":
    {
        string? path = args.Length > 1 && !args[1].StartsWith('-') ? args[1] : null;
        using var reg = SensorRegistry.CreateDefault();
        var set = Config.Discover(reg, fahrenheit);
        await Config.SaveAsync(set, path);
        Console.WriteLine($"Wrote {path ?? Config.Path}");
        foreach (var s in set.Screens)
            Console.WriteLine($"  screen '{s.Name}': {string.Join(", ", s.Modules.Select(m => m.Label))}");
        Console.WriteLine($"\nRun it with:  nexus-manager daemon");
        break;
    }

    case "key":
    {
        // Fires a synthetic keypress through the uinput virtual keyboard, so
        // the macro-button path can be proven WITHOUT a panel or a GUI. This
        // is the piece most likely to fail silently: a device that is created
        // but never delivers an event looks identical to one that works.
        if (args.Length < 2)
        {
            Console.Error.WriteLine("usage: nexus-manager key <combo>   e.g. ctrl+alt+t");
            Console.Error.WriteLine("known keys: " + string.Join(" ",
                UinputKeyboard.KeyCodes.Keys.OrderBy(k => k)));
            return 2;
        }
        using var kb = new UinputKeyboard();
        string? kerr = kb.Press(args[1]);
        if (kerr is not null) { Console.Error.WriteLine(kerr); return 1; }
        Console.WriteLine($"pressed {args[1]}");

        // --repeat keeps the device alive and fires again, so a test can attach
        // a reader AFTER the device appears and still catch a press. Without it
        // the only press happens before anything can be listening, and "no event
        // captured" is indistinguishable from a keyboard that does not work.
        int repeat = 1;
        int rAt = Array.IndexOf(args, "--repeat");
        if (rAt >= 0 && rAt + 1 < args.Length) int.TryParse(args[rAt + 1], out repeat);
        for (int i = 1; i < Math.Clamp(repeat, 1, 100); i++)
        {
            await Task.Delay(300);
            kb.Press(args[1]);
            Console.WriteLine($"pressed {args[1]} ({i + 1})");
        }
        // Held open briefly: destroying the device immediately can race the
        // events out of the queue before anything reads them.
        await Task.Delay(300);
        break;
    }

    case "preview":
    {
        // Renders a configured screen to a PNG WITHOUT touching the device, so a
        // config can be checked while something else owns the panel - and so
        // button layout and drawing can be verified at all, which otherwise
        // needs hardware plus a finger.
        using var reg = SensorRegistry.CreateDefault();
        reg.Scale = fahrenheit ? TemperatureScale.Fahrenheit : TemperatureScale.Celsius;
        var pset = await Config.LoadAsync(null) ?? Config.Discover(reg, fahrenheit);
        if (pset.IsEmpty) { Console.Error.WriteLine("No screens configured."); return 1; }

        int which = 0;
        int sAt = Array.IndexOf(args, "--screen");
        if (sAt >= 0 && sAt + 1 < args.Length) int.TryParse(args[sAt + 1], out which);
        which = Math.Clamp(which, 0, pset.Screens.Count - 1);
        var pscreen = pset.Screens[which];

        string outPath = "screen.png";
        int oAt = Array.IndexOf(args, "--out");
        if (oAt >= 0 && oAt + 1 < args.Length) outPath = args[oAt + 1];

        reg.SetActive(pscreen.Modules.Select(m => m.Source));
        reg.Sample();
        await Task.Delay(250);
        reg.Sample();

        var (pmods, pbtns) = ScreenLayout.ComputeAll(pscreen, out var pnarrow);
        foreach (string wmsg in pnarrow) Console.Error.WriteLine($"  warning: {wmsg}");

        using var pcanvas = new NexusCanvas();
        using var prend = new ScreenRenderer(pscreen.Theme);
        var phist = pmods.Select(l => new History(Math.Max(2, (int)l.Rect.Width))).ToList();
        // A few samples so charts are not empty, which would make a screen look
        // broken rather than new.
        // Enough samples to fill the widest chart. With 8 the sparkline came out
        // EIGHT PIXELS wide at the right edge of its cell, so the preview could
        // not show what a chart looks like at all - which is most of what the
        // preview exists for. Only the first few are real reads; the rest repeat
        // the last value, because Read without a fresh Sample returns it.
        int seedCount = pmods.Count == 0 ? 8 : Math.Max(8, (int)pmods.Max(l => l.Rect.Width));
        for (int s = 0; s < seedCount; s++)
        {
            if (s < 8) reg.Sample();
            for (int i = 0; i < pmods.Count; i++)
            {
                double v = reg.Read(pmods[i].Spec.Source);
                if (!double.IsNaN(v)) phist[i].Add(v);
            }
        }

        int pressed = -1;
        int prAt = Array.IndexOf(args, "--press");
        if (prAt >= 0 && prAt + 1 < args.Length) int.TryParse(args[prAt + 1], out pressed);

        prend.Draw(pcanvas, pmods, phist,
            k => { double v = reg.Read(k); return double.IsNaN(v) ? 0 : v; },
            pscreen.Background, TimeSpan.Zero, pbtns, pressed, TimeSpan.FromDays(1));
        if (pset.ShowPageIndicator)
            PageIndicator.Draw(pcanvas.Canvas, which, pset.Screens.Count, pscreen.Theme.CaptionColor);

        var pframe = new byte[NexusDevice.FrameBytes];
        pcanvas.CopyTo(pframe);
        using (var pbmp = new SKBitmap(new SKImageInfo(
                   NexusCanvas.Width, NexusCanvas.Height, SKColorType.Bgra8888, SKAlphaType.Opaque)))
        {
            System.Runtime.InteropServices.Marshal.Copy(pframe, 0, pbmp.GetPixels(), pframe.Length);
            using var pdata = pbmp.Encode(SKEncodedImageFormat.Png, 100);
            using var pfs = File.Create(outPath);
            pdata.SaveTo(pfs);
        }
        Console.WriteLine($"screen {which} '{pscreen.Name}': " +
                          $"{pmods.Count} module(s), {pbtns.Count} button(s) -> {outPath}");
        foreach (var b in pbtns)
            Console.WriteLine($"  button [{b.Rect.Left:0}-{b.Rect.Right:0}] " +
                              $"'{b.Spec.Label}' {b.Spec.Action}");
        break;
    }

    case "blank":
    {
        // A way to clear the panel WITHOUT starting a render loop. Without this
        // the only way to blank the strip was to start a daemon and stop it
        // again, so a killed process left its last frame on the glass with no
        // way to clear it - which is exactly the stale-readings display the
        // blank-on-exit rule exists to prevent.
        using var d = NexusDevice.Open();
        d.Blank();
        d.SetBrightness(0);
        Console.WriteLine("panel handed back to its firmware animation");
        break;
    }

    case "run":
    case "daemon":
    {
        string? path = args.Length > 1 && !args[1].StartsWith('-') ? args[1] : null;
        // One writer only. Two processes pushing frames to the same panel do
        // NOT error - the HID output reports simply interleave - so the sole
        // symptom is the strip flickering between two screens, which is what
        // the user hit by launching the app a second time.
        SingleInstance? instance;
        try
        {
            instance = SingleInstance.TryAcquire("daemon");
        }
        catch (IOException ex)
        {
            // A configuration fault, not contention. Reported as a sentence
            // rather than as a stack trace: the message names the directory
            // and what to do about it, and a core dump names neither.
            Console.Error.WriteLine($"Cannot take the panel lock: {ex.Message}");
            return 1;
        }
        using var held = instance;
        if (instance is null)
        {
            Console.Error.WriteLine(
                $"The panel is already being driven by {SingleInstance.DescribeHolder()}.");
            Console.Error.WriteLine("Stop that first, or use its window instead.");
            return 1;
        }

        using var dev = NexusDevice.Open();
        using var reg = SensorRegistry.CreateDefault();
        reg.Scale = fahrenheit ? TemperatureScale.Fahrenheit : TemperatureScale.Celsius;

        var set = await Config.LoadAsync(path);
        if (set is null)
        {
            set = Config.Discover(reg, fahrenheit);
            Console.WriteLine($"No config at {path ?? Config.Path}; using auto-discovered screens.");
            Console.WriteLine("Write one with:  nexus-manager init");
        }
        if (set.IsEmpty) { Console.Error.WriteLine("No screens configured."); return 1; }

        using var daemon = new Daemon(dev, reg, set);
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        // Touch runs on its own HID stream so gesture reads never contend with
        // the 121 writes each frame costs.
        HidSharp.HidStream? touchStream = null;
        try
        {
            touchStream = NexusDevice.OpenTouchStream();
            var touch = new NexusTouch(touchStream);
            touch.Gesture += daemon.OnGesture;
            _ = touch.RunAsync(cts.Token);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"  touch unavailable ({ex.Message}); screens will not swipe.");
        }

        Console.WriteLine($"{set.Screens.Count} screen(s) at {set.TargetFps} fps. Swipe to change. Ctrl-C to stop.");
        await daemon.RunAsync(cts.Token);
        touchStream?.Dispose();

        // Hand the panel back to its firmware instead of killing it: the device
        // plays a Corsair animation on its own when nothing is driving it.
        dev.HandBack(set.IdleAnimation, set.Brightness);
        Console.WriteLine($"stopped ({daemon.AchievedFps} fps achieved); panel handed back to its firmware animation");
        break;
    }

    case "bench":
    {
        using var dev = NexusDevice.Open();
        double secs = args.Length > 1 ? double.Parse(args[1], CultureInfo.InvariantCulture) : 6.0;
        var frame = new byte[NexusDevice.FrameBytes];
        var sw = System.Diagnostics.Stopwatch.StartNew();
        long frames = 0;
        double worst = 0, best = double.MaxValue;
        while (sw.Elapsed.TotalSeconds < secs)
        {
            for (int i = 0; i < frame.Length; i += 4)
            {
                frame[i] = (byte)(frames * 3); frame[i + 1] = (byte)(i >> 8);
                frame[i + 2] = (byte)(frames * 7); frame[i + 3] = 255;
            }
            double t = sw.Elapsed.TotalMilliseconds;
            dev.PushFrame(frame);
            double dt = sw.Elapsed.TotalMilliseconds - t;
            worst = Math.Max(worst, dt); best = Math.Min(best, dt);
            frames++;
        }
        double el = sw.Elapsed.TotalSeconds;
        Console.WriteLine($"  {frames} frames in {el:0.00}s = {frames / el:0.00} fps");
        Console.WriteLine($"  per-frame best {best:0.00} ms / worst {worst:0.00} ms");
        Console.WriteLine($"  {frames * (double)NexusDevice.FrameBytes / el / 1048576.0:0.00} MB/s");
        break;
    }

    case "touch":
    {
        using var dev = NexusDevice.Open();
        using var stream = NexusDevice.OpenTouchStream();
        var touch = new NexusTouch(stream);
        touch.Gesture += g => Console.WriteLine(
            $"  {g.Kind,-11} start={g.StartX,-4} end={g.EndX,-4} travel={g.Travel,5} " +
            $"{g.DurationMs,6:0} ms  {g.Velocity,8:0} px/s");
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        Console.WriteLine("Gesture stream (Ctrl-C to stop). Fast swipes are stitched per D13.");
        await touch.RunAsync(cts.Token);
        break;
    }

    case "anim":
    {
        // The three animations baked into the firmware (`03 0D <1-3> <loop>`).
        // ⛔ The command has been in NexusDevice since the protocol was written
        // and NOTHING has ever called it - no verb reached it - so until this
        // runs it is documented rather than proved.
        //
        // Deliberately does NOT blank on exit, unlike every other verb here:
        // the animation runs in firmware and keeps going after this process is
        // gone, which is the whole point of looking at it.
        using var animInstance = SingleInstance.TryAcquire("anim");
        if (animInstance is null)
        {
            Console.Error.WriteLine(
                $"The panel is already being driven by {SingleInstance.DescribeHolder()}.");
            Console.Error.WriteLine("Stop it first - a running screen loop overwrites the animation.");
            return 1;
        }

        using var animDev = NexusDevice.Open();
        string which = args.Length > 1 ? args[1] : "";

        if (string.Equals(which, "stop", StringComparison.OrdinalIgnoreCase))
        {
            animDev.StopAnimation();
            Console.WriteLine("animation stopped (the panel keeps whatever was last on it)");
            break;
        }

        if (!int.TryParse(which, out int animId) || animId is < 1 or > 3)
        {
            Console.Error.WriteLine("usage: nexus-manager anim <1|2|3> [--loop]");
            Console.Error.WriteLine("       nexus-manager anim stop");
            return 1;
        }

        // ⛔ Brightness FIRST. A previous run of this app ends by blanking and
        // setting the backlight to 0, so an animation started after that plays
        // perfectly and is invisible - which would read as "the command does
        // nothing".
        animDev.SetBrightness(100);
        animDev.PlayAnimation(animId, args.Contains("--loop"));
        Console.WriteLine($"playing firmware animation {animId}"
                          + (args.Contains("--loop") ? " (looping)" : " (once)"));
        Console.WriteLine("It runs in firmware and keeps going after this exits.");
        Console.WriteLine("`nexus-manager anim stop` ends it; starting the app takes the panel back.");
        break;
    }

    case "fonts":
    {
        // Shows exactly what the font picker will offer, and what it dropped.
        // A filter nobody can see the output of is a filter nobody can check.
        var everything = FontCatalog.All();
        var offered = FontCatalog.Usable();
        bool showAll = args.Contains("--all");
        Console.WriteLine($"{everything.Count} families installed, {offered.Count} offered "
                          + $"({everything.Count - offered.Count} filtered out)");
        Console.WriteLine();
        foreach (string f in offered)
        {
            using var probe = SkiaSharp.SKTypeface.FromFamilyName(f);
            string got = probe?.FamilyName ?? "(null)";
            bool exact = string.Equals(got, f, StringComparison.OrdinalIgnoreCase);
            Console.WriteLine($"  {f,-28} -> {got}{(exact ? "" : "   *** FALLBACK, not the requested family ***")}");
        }
        if (showAll)
        {
            Console.WriteLine();
            Console.WriteLine("dropped:");
            var keep = new HashSet<string>(offered, StringComparer.OrdinalIgnoreCase);
            foreach (string f in everything.Where(f => !keep.Contains(f)))
                Console.WriteLine($"  {f}");
        }
        break;
    }

    case "calibrate":
    {
        // Does a tap land where it looks like it lands? Measured rather than
        // assumed: the 0..639 range in _docs/PROTOCOL.md was inferred from the
        // panel width, and no capture in that document ever exceeded 533.
        int cvAt = Array.IndexOf(args, "--preview");
        if (cvAt >= 0)
        {
            string cvOut = cvAt + 1 < args.Length ? args[cvAt + 1] : "calibration-card.png";
            int cvTarget = 0;
            int ctAt = Array.IndexOf(args, "--target");
            if (ctAt >= 0 && ctAt + 1 < args.Length) int.TryParse(args[ctAt + 1], out cvTarget);
            if (args.Contains("--sweep")) return Calibrate.PreviewSweep(cvOut);
            return Calibrate.PreviewCard(cvOut, cvTarget);
        }
        if (args.Contains("--sweep")) return await Calibrate.RunSweepAsync();
        if (args.Contains("--live")) return await Calibrate.RunLiveAsync();
        return await Calibrate.RunAsync();
    }

    case "image":
    {
        // Instrument for backgrounds. Reports what a file ACTUALLY decoded to -
        // frame count, per-frame delays, presentation size, memory - and can dump
        // composited frames so the result can be looked at rather than trusted.
        if (args.Length < 2)
        {
            Console.Error.WriteLine("usage: nexus-manager image <file> [--fit cover|stretch|contain|center|tile|scrollleft|scrollright] [--dump <dir>] [--frames N]");
            return 2;
        }
        string path = args[1];
        var fit = BackgroundFit.Cover;
        int fitAt = Array.IndexOf(args, "--fit");
        if (fitAt >= 0 && fitAt + 1 < args.Length
            && Enum.TryParse<BackgroundFit>(args[fitAt + 1], ignoreCase: true, out var parsed))
            fit = parsed;

        double zoom = 1, fx = 0.5, fy = 0.5;
        int zAt = Array.IndexOf(args, "--zoom");
        if (zAt >= 0 && zAt + 1 < args.Length) double.TryParse(args[zAt + 1], out zoom);
        int fAt = Array.IndexOf(args, "--focus");
        if (fAt >= 0 && fAt + 2 < args.Length)
        { double.TryParse(args[fAt + 1], out fx); double.TryParse(args[fAt + 2], out fy); }

        var img = AnimatedImage.Load(path, NexusCanvas.Width, NexusCanvas.Height, fit,
                                     zoom, fx, fy, out string? err);
        if (err is not null) Console.Error.WriteLine($"note: {err}");
        if (img is null) return 1;

        using (img)
        {
            Console.WriteLine($"file          {path}");
            Console.WriteLine($"fit           {fit}");
            Console.WriteLine($"frames        {img.FrameCount}{(img.IsAnimated ? " (animated)" : " (still)")}");
            Console.WriteLine($"stored size   {img.Size.Width}x{img.Size.Height}");
            Console.WriteLine($"duration      {img.Duration.TotalMilliseconds:F0} ms"
                              + (img.Duration.TotalMilliseconds > 0
                                 ? $"  ({img.FrameCount / img.Duration.TotalSeconds:F1} fps average)" : ""));
            Console.WriteLine($"memory        {img.BytesUsed / 1024.0:F0} KB");

            int dumpAt = Array.IndexOf(args, "--dump");
            if (dumpAt >= 0 && dumpAt + 1 < args.Length)
            {
                string dir = args[dumpAt + 1];
                Directory.CreateDirectory(dir);
                // A SAMPLE count over the timeline, not a frame index: a still image
                // with a scrolling fit is animated by time alone, so clamping this to
                // the source frame count would sample such a screen exactly once.
                int want = Math.Max(1, img.FrameCount);
                int nAt = Array.IndexOf(args, "--frames");
                if (nAt >= 0 && nAt + 1 < args.Length && int.TryParse(args[nAt + 1], out int n))
                    want = Math.Max(1, n);

                // Sampled across the animation's own timeline, through the same
                // painter the panel uses - so what lands on disk is what the panel
                // would show, scroll offsets and all, not a raw frame dump.
                using var painter = new BackgroundPainter();
                var spec = new BackgroundSpec { Image = path, Fit = fit, Zoom = zoom, FocusX = fx, FocusY = fy };
                using var canvas = new NexusCanvas();
                double totalMs = Math.Max(img.Duration.TotalMilliseconds, 1000);
                for (int i = 0; i < want; i++)
                {
                    var t = TimeSpan.FromMilliseconds(totalMs * i / want);
                    painter.Draw(canvas.Canvas, spec, SKColors.Black, t);
                    var frame = new byte[NexusDevice.FrameBytes];
                    canvas.CopyTo(frame);
                    // Written through CopyTo so the dump carries the dithering and
                    // channel order the panel actually receives.
                    using var bmp = new SKBitmap(new SKImageInfo(
                        NexusCanvas.Width, NexusCanvas.Height, SKColorType.Bgra8888, SKAlphaType.Opaque));
                    System.Runtime.InteropServices.Marshal.Copy(
                        frame, 0, bmp.GetPixels(), frame.Length);
                    using var data = bmp.Encode(SKEncodedImageFormat.Png, 100);
                    string outPath = Path.Combine(dir, $"frame-{i:D4}.png");
                    using var fs = File.Create(outPath);
                    data.SaveTo(fs);
                }
                Console.WriteLine($"dumped        {want} frame(s) to {dir}");
            }
        }
        break;
    }

    case "visualizer":
    case "vis":
    {
        if (args.Contains("--selftest"))
        {
            // ⛔ return, NOT Environment.ExitCode. Top-level statements with an
            // explicit `return 0` at the end of the file compile to Task<int>
            // Main, and that final return OVERWRITES ExitCode - so a self-test
            // that printed FAILED still exited 0 and no script could tell.
            return Visualizer.SelfTest();
        }
        if (args.Contains("--sweep"))
        {
            using var csw = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; csw.Cancel(); };
            return await Visualizer.SweepAsync(
                Visualizer.ParseInt(args, "--seconds", 5),
                Visualizer.ParseInt(args, "--fps", 30),
                Visualizer.ParseInt(args, "--bands", 32),
                csw.Token);
        }
        if (args.Contains("--probe"))
        {
            return await Visualizer.ProbeAsync(Visualizer.ParseInt(args, "--seconds", 6));
        }

        using var cvis = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cvis.Cancel(); };
        return await Visualizer.RunAsync(
            Visualizer.ParseInt(args, "--seconds", 20),
            Visualizer.ParseInt(args, "--fps", 30),
            Visualizer.ParseInt(args, "--bands", 32),
            Visualizer.ParseMode(args),
            Visualizer.ParseInt(args, "--cycle", 8),
            cvis.Token);
    }

    default:
        Console.WriteLine("""
            nexus-manager <command>

              sensors [-f]        list every sensor found, grouped by device
              info                device firmware and geometry
              init [file]         write a starter screen config for this machine
              run [file] [-f]     render a screen config to the panel
              bench [seconds]     raw frame upload rate
              touch               live gesture stream
              calibrate           tap known targets to measure touch alignment
              fonts [--all]       font families the picker offers (--all lists the dropped)
              image <file>        inspect a background image or animation
              blank               clear the panel and turn its backlight off
              anim <1-3> [--loop] play a firmware animation (anim stop to end it)
              preview [--screen N] render a screen to a PNG, no device needed
              key <combo>         send a synthetic keypress (ctrl+alt+t)
              visualizer          music spectrum on the panel
                --selftest        validate the DSP on synthetic signal, no hardware
                --probe           what the audio capture is actually seeing
                --seconds N --fps N --bands N

              -f / --fahrenheit   show temperatures in Fahrenheit
            """);
        break;
}

return 0;


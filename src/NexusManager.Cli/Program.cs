using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using NexusManager.Cli;
using NexusManager.Device;
using NexusManager.Render;
using NexusManager.Sensors;
using SkiaSharp;

JsonSerializerOptions JsonOpts = Config.Json;

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

    case "run":
    case "daemon":
    {
        string? path = args.Length > 1 && !args[1].StartsWith('-') ? args[1] : null;
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
            touchStream = OpenTouchStream();
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

        // Blank on exit: a monitoring panel frozen on stale numbers looks exactly
        // like a working one, which is worse than showing nothing.
        dev.Blank();
        dev.SetBrightness(0);
        Console.WriteLine($"stopped ({daemon.AchievedFps} fps achieved); panel blanked");
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
        using var stream = OpenTouchStream();
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

    default:
        Console.WriteLine("""
            nexus-manager <command>

              sensors [-f]        list every sensor found, grouped by device
              info                device firmware and geometry
              init [file]         write a starter screen config for this machine
              run [file] [-f]     render a screen config to the panel
              bench [seconds]     raw frame upload rate
              touch               live gesture stream

              -f / --fahrenheit   show temperatures in Fahrenheit
            """);
        break;
}

return 0;

static HidSharp.HidStream OpenTouchStream()
{
    foreach (var d in HidSharp.DeviceList.Local.GetHidDevices(NexusDevice.VendorId, NexusDevice.ProductId))
        if (d.GetMaxOutputReportLength() >= 1024 && d.TryOpen(out var s))
            return s;
    throw new InvalidOperationException("Could not open the NEXUS control interface for touch.");
}


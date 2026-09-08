using System.Threading.Channels;
using NexusManager.Device;
using NexusManager.Render;
using SkiaSharp;

namespace NexusManager.Cli;

/// <summary>
/// Touch alignment calibration: does a tap land where it looks like it lands?
///
/// This exists because the assumption was never measured. `_docs/PROTOCOL.md`
/// records the touch X range as 0..639, but the largest X ever actually
/// captured anywhere in that document is 533 - the range was inferred from the
/// panel being 640px wide, not observed. A digitizer reporting a WIDER range
/// would leave swipes working perfectly, because a swipe is a test on travel
/// and travel merely scales, while putting every TAP in the wrong cell. From
/// the user's chair that is indistinguishable from a button whose action is
/// misconfigured, which is the other open suspect.
///
/// So draw targets at known X, have a finger tap them, and print what the
/// device reported. The panel also marks where the tap was received, so the
/// answer is legible on the glass without reading the terminal.
/// </summary>
public static class Calibrate
{
    private const int Count  = 9;
    private const int FirstX = 20;
    private const int StepX  = 75;     // 20..620: symmetric about the 320 centre

    public static int TargetX(int i) => FirstX + StepX * i;

    private readonly record struct Sample(Gesture G, int DownX, int MinX, int MaxX);

    /// <summary>
    /// Live tracking bar. The decisive test for SIGN, which neither the dot card
    /// nor the sweep could give: the dot card measures aim and device together,
    /// and the sweep measures range but not position.
    ///
    /// A 3px bar under a fingertip roughly 50px wide is a binary indicator -
    /// hidden means the reading is under the finger, visible means it is not,
    /// and the side it emerges from is the sign of the error. No aiming, no
    /// arithmetic, and no need to trust a remembered direction.
    ///
    /// Also dumps the raw bytes of any out-of-range report. Two sweeps peaked at
    /// exactly 2465 and the stream opens reporting 4094, so some reports carry
    /// something other than a position in bytes 6-7 - and the filter for that
    /// should be built from the bytes, not from a threshold I guessed.
    /// </summary>
    public static async Task<int> RunLiveAsync()
    {
        using var instance = SingleInstance.TryAcquire("calibrate");
        if (instance is null)
        {
            Console.Error.WriteLine(
                $"The panel is already being driven by {SingleInstance.DescribeHolder()}.");
            Console.Error.WriteLine("Close it first - calibration needs the panel to itself.");
            return 1;
        }

        using var dev = NexusDevice.Open();
        using var stream = NexusDevice.OpenTouchStream();
        dev.SetBrightness(100);

        using var canvas = new NexusCanvas();
        var frame = new byte[NexusDevice.FrameBytes];

        var touch = new NexusTouch(stream);
        int cur = -1;
        touch.Down += x => cur = x;
        touch.Move += x => cur = x;
        touch.Gesture += g =>
        {
            Console.WriteLine($"  {g.Kind,-11} down={g.StartX,4} lift={g.EndX,4} travel={g.Travel,5} {g.DurationMs,6:0} ms");
            cur = -1;
        };

        int odd = 0;
        touch.Report += r =>
        {
            if (r.Length < 10 || odd >= 20) return;
            int x = r[6] | (r[7] << 8);
            if (x <= 700) return;
            odd++;
            Console.WriteLine($"  ODD x={x,5}  bytes {Convert.ToHexString(r[..Math.Min(12, r.Length)])}"
                              + $"  (bytes8-9={r[8] | (r[9] << 8)})");
        };

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        var pump = touch.RunAsync(cts.Token);

        Console.WriteLine("""
            Live tracking bar.

            Put a finger on the strip and slide it slowly from end to end, a few
            times. Watch the WHITE BAR, and answer one question:

              can you see the bar at all while your finger is down?

              hidden under your fingertip  -> the reading is correct
              visible, poking out one side -> that side is the error, and by
                                              roughly how much

            The number at the far end is the raw value, live. Ctrl-C to stop.
            """);
        Console.WriteLine();

        try
        {
            while (!cts.IsCancellationRequested)
            {
                DrawLive(canvas, cur);
                canvas.CopyTo(frame);
                dev.PushFrame(frame);
                await Task.Delay(33, cts.Token);   // ~30 fps, well inside the 65 ceiling
            }
        }
        catch (OperationCanceledException) { }

        try { await pump; } catch (Exception) { }
        dev.HandBack(1, 100);   // leave the firmware animation, never a dead strip
        Console.WriteLine();
        Console.WriteLine("Panel handed back to its firmware animation.");
        return 0;
    }

    private static void DrawLive(NexusCanvas canvas, int cur)
    {
        var c = canvas.Canvas;
        c.Clear(new SKColor(0x08, 0x08, 0x0A));

        var face = SKTypeface.FromFamilyName("DejaVu Sans", SKFontStyleWeight.Bold,
                                             SKFontStyleWidth.Normal, SKFontStyleSlant.Upright)
                   ?? SKTypeface.Default;
        using var font = new SKFont(face, 22);
        using var fill = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };

        if (cur < 0)
        {
            fill.Color = new SKColor(0x55, 0x55, 0x62);
            using var hint = new SKFont(face, 14);
            c.DrawText("touch and slide", NexusCanvas.Width / 2f, 31, SKTextAlign.Center, hint, fill);
            return;
        }

        // The number goes at whichever end the finger is NOT, so it stays
        // readable while a finger is on the glass.
        fill.Color = new SKColor(0xFF, 0xAA, 0x28);
        bool right = cur < NexusCanvas.Width / 2;
        c.DrawText(cur.ToString(), right ? NexusCanvas.Width - 12 : 12, 33,
                   right ? SKTextAlign.Right : SKTextAlign.Left, font, fill);

        fill.Color = SKColors.White;
        c.DrawRect(Math.Clamp(cur - 1, 0, NexusCanvas.Width - 3), 0, 3, NexusCanvas.Height, fill);
    }

    /// <summary>Draws the sweep card with no device and probes it, for the same
    /// reason PreviewCard exists: an instrument that renders nothing reads, from
    /// the user's chair, as a dead panel.</summary>
    public static int PreviewSweep(string outPath)
    {
        using var canvas = new NexusCanvas();
        DrawSweepCard(canvas, 0);
        var frame = new byte[NexusDevice.FrameBytes];
        canvas.CopyTo(frame);
        using (var bmp = new SKBitmap(new SKImageInfo(
                   NexusCanvas.Width, NexusCanvas.Height, SKColorType.Bgra8888, SKAlphaType.Opaque)))
        {
            System.Runtime.InteropServices.Marshal.Copy(frame, 0, bmp.GetPixels(), frame.Length);
            using var data = bmp.Encode(SKEncodedImageFormat.Png, 100);
            using var fs = File.Create(outPath);
            data.SaveTo(fs);
        }
        (int R, int G, int B) At(int x, int y)
        {
            int p = (y * NexusCanvas.Width + x) * 4;
            return (frame[p + 2], frame[p + 1], frame[p]);
        }
        var left   = At(5, 24);
        var right  = At(NexusCanvas.Width - 6, 24);
        var middle = At(NexusCanvas.Width / 2, 2);
        bool amberL = left.R > 200 && left.G is > 120 and < 200 && left.B < 90;
        bool amberR = right.R > 200 && right.G is > 120 and < 200 && right.B < 90;
        bool clearM = middle.R < 60;
        Console.WriteLine($"sweep card -> {outPath}");
        Console.WriteLine($"  left block   x=5    rgb{left}   {(amberL ? "AMBER" : "MISSING")}");
        Console.WriteLine($"  right block  x={NexusCanvas.Width - 6,3}  rgb{right}   {(amberR ? "AMBER" : "MISSING")}");
        Console.WriteLine($"  control      centre rgb{middle}   {(clearM ? "clear" : "*** blocks are not blocks ***")}");
        bool ok = amberL && amberR && clearM;
        Console.WriteLine(ok ? "sweep card OK" : "sweep card BROKEN");
        return ok ? 0 : 1;
    }

    /// <summary>
    /// The aim-free measurement: what raw values does the digitizer produce at
    /// the two physical ends of the glass?
    ///
    /// The nine-dot card cannot answer that. It measures the whole chain -
    /// where a dot is drawn, where the eye judges it to be through the cover
    /// glass at a shallow viewing angle, where the finger actually lands, and
    /// what the device reports - and returns one number for all four. A finger
    /// dragged from edge to edge has no aiming component at all: the ends are
    /// felt, not judged. So the span it produces is the digitizer's own range,
    /// with the user's aim removed from the measurement.
    /// </summary>
    public static async Task<int> RunSweepAsync()
    {
        using var instance = SingleInstance.TryAcquire("calibrate");
        if (instance is null)
        {
            Console.Error.WriteLine(
                $"The panel is already being driven by {SingleInstance.DescribeHolder()}.");
            Console.Error.WriteLine("Close it first - calibration needs the panel to itself.");
            return 1;
        }

        using var dev = NexusDevice.Open();
        using var stream = NexusDevice.OpenTouchStream();
        dev.SetBrightness(100);

        using var canvas = new NexusCanvas();
        var frame = new byte[NexusDevice.FrameBytes];
        void Push(int pass)
        {
            DrawSweepCard(canvas, pass);
            canvas.CopyTo(frame);
            dev.PushFrame(frame);
        }

        var touch = new NexusTouch(stream);
        int lo = int.MaxValue, hi = int.MinValue, samples = 0;
        void Note(int x) { if (x < lo) lo = x; if (x > hi) hi = x; samples++; }
        touch.Down += Note;
        touch.Move += Note;

        var ch = Channel.CreateUnbounded<(int Lo, int Hi, int N, double Ms)>();
        touch.Gesture += g => ch.Writer.TryWrite((lo, hi, samples, g.DurationMs));

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        var pump = touch.RunAsync(cts.Token);

        Console.WriteLine("""
            Touch range sweep - nothing to aim at.

            Put a finger on the very edge of the glass at ONE end, slide slowly
            all the way to the other end, and lift. Press hard against each
            physical end before you start and after you finish - the ends are
            what is being measured. Direction does not matter.

            Three passes. Ctrl-C stops early and still reports.
            """);
        Console.WriteLine();

        const int Passes = 3;
        var runs = new List<(int Lo, int Hi, int N, double Ms)>();
        try
        {
            for (int p = 0; p < Passes; p++)
            {
                lo = int.MaxValue; hi = int.MinValue; samples = 0;
                Push(p);
                Console.Write($"  pass {p + 1}/{Passes}  sweep now ...  ");
                var r = await ch.Reader.ReadAsync(cts.Token);
                if (r.N < 8)
                {
                    Console.WriteLine($"only {r.N} samples - that was a tap, not a sweep. Repeating.");
                    p--;
                    continue;
                }
                runs.Add(r);
                Console.WriteLine($"raw {r.Lo}..{r.Hi}   span {r.Hi - r.Lo}   {r.N} samples   {r.Ms:0} ms");
                await Task.Delay(400, cts.Token);
            }
        }
        catch (OperationCanceledException) { Console.WriteLine(); }

        try { cts.Cancel(); } catch (Exception) { }
        try { await pump; } catch (Exception) { }

        dev.HandBack(1, 100);   // leave the firmware animation, never a dead strip
        Console.WriteLine();
        Console.WriteLine("Panel handed back to its firmware animation.");
        Console.WriteLine();

        if (runs.Count == 0)
        {
            Console.WriteLine("No complete sweep captured.");
            return 1;
        }

        // The widest pass is the one that actually reached both ends. A pass
        // that stopped short can only understate the range, never overstate it,
        // so max-of-spans is the right summary rather than a mean.
        int aggLo = runs.Min(r => r.Lo);
        int aggHi = runs.Max(r => r.Hi);
        Console.WriteLine($"  aggregate   raw {aggLo}..{aggHi}   span {aggHi - aggLo}");
        Console.WriteLine($"  the panel   is {NexusCanvas.Width} px wide, and the code assumes raw 0..{NexusCanvas.Width - 1}");
        Console.WriteLine();

        bool identity = Math.Abs(aggLo) <= 12 && Math.Abs(aggHi - (NexusCanvas.Width - 1)) <= 12;
        if (identity)
        {
            Console.WriteLine("  VERDICT: the digitizer reports 0..639, matching the panel 1:1.");
            Console.WriteLine("           So the offset the dot card measured is NOT in the device -");
            Console.WriteLine("           it is between the drawn dot and where the finger lands, and");
            Console.WriteLine("           the fix belongs in the hit test's tolerance, not in a scale.");
        }
        else
        {
            double a = (aggHi - aggLo) / (double)(NexusCanvas.Width - 1);
            Console.WriteLine($"  VERDICT: the digitizer's range is {aggLo}..{aggHi}, NOT 0..{NexusCanvas.Width - 1}.");
            Console.WriteLine($"           _docs/PROTOCOL.md is wrong, and every tap is misplaced.");
            Console.WriteLine($"           panel x = (raw - {aggLo}) / {a:0.0000}");
        }
        return 0;
    }

    /// <summary>Both physical ends lit, so the finger has something to run between.</summary>
    private static void DrawSweepCard(NexusCanvas canvas, int pass)
    {
        var c = canvas.Canvas;
        c.Clear(new SKColor(0x08, 0x08, 0x0A));

        var face = SKTypeface.FromFamilyName("DejaVu Sans", SKFontStyleWeight.Bold,
                                             SKFontStyleWidth.Normal, SKFontStyleSlant.Upright)
                   ?? SKTypeface.Default;
        using var font = new SKFont(face, 15);
        using var fill = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };
        using var line = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2 };

        const float MidY = NexusCanvas.Height / 2f;
        fill.Color = new SKColor(0xFF, 0xAA, 0x28);
        c.DrawRect(0, 0, 22, NexusCanvas.Height, fill);
        c.DrawRect(NexusCanvas.Width - 22, 0, 22, NexusCanvas.Height, fill);

        line.Color = new SKColor(0x55, 0x55, 0x62);
        c.DrawLine(30, MidY, NexusCanvas.Width - 30, MidY, line);

        fill.Color = new SKColor(0xEB, 0xEB, 0xF0);
        c.DrawText($"edge  to  edge      {pass + 1} / 3", NexusCanvas.Width / 2f, MidY - 6,
                   SKTextAlign.Center, font, fill);
    }

    public static async Task<int> RunAsync()
    {
        // The panel takes one writer. Two processes pushing frames do not error,
        // they interleave - so without this the card would flicker against
        // whatever the tray app is drawing and every tap would be attributed to
        // a target the user cannot see.
        using var instance = SingleInstance.TryAcquire("calibrate");
        if (instance is null)
        {
            Console.Error.WriteLine(
                $"The panel is already being driven by {SingleInstance.DescribeHolder()}.");
            Console.Error.WriteLine("Close it first - calibration needs the panel to itself.");
            return 1;
        }

        using var dev = NexusDevice.Open();
        using var stream = NexusDevice.OpenTouchStream();
        dev.SetBrightness(100);

        using var canvas = new NexusCanvas();
        var frame = new byte[NexusDevice.FrameBytes];

        void Push(int active, int mark)
        {
            DrawCard(canvas, active, mark);
            canvas.CopyTo(frame);
            dev.PushFrame(frame);
        }

        var ch = Channel.CreateUnbounded<Sample>();
        var touch = new NexusTouch(stream);

        int downX = -1, minX = 0, maxX = 0;
        touch.Down += x => { downX = x; minX = maxX = x; };
        touch.Move += x => { if (x < minX) minX = x; if (x > maxX) maxX = x; };
        touch.Gesture += g => ch.Writer.TryWrite(new Sample(g, downX, minX, maxX));

        // Positive control on the DECODE, not just its conclusion. An off-by-one
        // in the report offsets produces a plausible number rather than an
        // error, and that has already cost this project a day once.
        int shown = 0;
        touch.Report += r =>
        {
            if (shown >= 3 || r.Length < 8) return;
            shown++;
            Console.WriteLine(
                $"    raw report, {r.Length} bytes: {Convert.ToHexString(r[..Math.Min(12, r.Length)])}"
                + $"  -> byte5={r[5]} (pressed), bytes6-7={r[6] | (r[7] << 8)} (X)");
        };

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        var pump = touch.RunAsync(cts.Token);

        Console.WriteLine($"""
            Touch alignment calibration - {Count} targets across the 640px strip.

            Tap the CENTRE of the lit dot, once, with the finger you would normally
            use. After each tap the panel draws a white bar where the tap was
            actually received: if that bar runs through the dot, the panel and the
            app agree about where your finger is.

            Ctrl-C stops early and still reports whatever was collected.
            """);
        Console.WriteLine();

        var got = new List<(int Drawn, Sample S)>();
        try
        {
            for (int i = 0; i < Count; i++)
            {
                Push(i, -1);
                Console.Write($"  target {i + 1}/{Count}  drawn x={TargetX(i),3}  ...  ");
                while (true)
                {
                    var s = await ch.Reader.ReadAsync(cts.Token);
                    if (s.G.Kind != GestureKind.Tap)
                    {
                        Console.WriteLine();
                        Console.WriteLine($"    ignored a {s.G.Kind} ({Signed(s.G.Travel)} px of travel) - tap, do not drag.");
                        Console.Write($"  target {i + 1}/{Count}  drawn x={TargetX(i),3}  ...  ");
                        continue;
                    }
                    got.Add((TargetX(i), s));
                    Console.WriteLine(
                        $"tapped x={s.DownX,3}   delta {Signed(s.DownX - TargetX(i)),4} px"
                        + $"   (lift {s.G.EndX}, held {s.MinX}..{s.MaxX})");
                    Push(i, s.DownX);
                    await Task.Delay(700, cts.Token);
                    break;
                }
            }
        }
        catch (OperationCanceledException) { Console.WriteLine(); }

        try { cts.Cancel(); } catch (Exception) { }
        try { await pump; } catch (Exception) { }

        dev.HandBack(1, 100);   // leave the firmware animation, never a dead strip
        Console.WriteLine();
        Console.WriteLine("Panel handed back to its firmware animation. Start the app when done.");
        Console.WriteLine();

        Report(got);
        return 0;
    }

    /// <summary>
    /// The card. Nine dots at known X, one lit at a time so a tap can never be
    /// attributed to the wrong target, plus a bar at the received position.
    /// </summary>
    private static void DrawCard(NexusCanvas canvas, int active, int mark)
    {
        var c = canvas.Canvas;
        c.Clear(new SKColor(0x08, 0x08, 0x0A));

        var face = SKTypeface.FromFamilyName("DejaVu Sans", SKFontStyleWeight.Bold,
                                             SKFontStyleWidth.Normal, SKFontStyleSlant.Upright)
                   ?? SKTypeface.Default;
        using var big   = new SKFont(face, 17);
        using var small = new SKFont(face, 11);
        using var fill  = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };
        using var line  = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1 };

        const float MidY = NexusCanvas.Height / 2f;

        for (int i = 0; i < Count; i++)
        {
            float cx = TargetX(i);
            if (i == active)
            {
                // Aim lines above and below: the dot is the target, these give
                // the eye its exact centre without being covered by the dot.
                line.Color = new SKColor(0xFF, 0xAA, 0x28);
                c.DrawLine(cx + 0.5f, 0, cx + 0.5f, MidY - 15, line);
                c.DrawLine(cx + 0.5f, MidY + 15, cx + 0.5f, NexusCanvas.Height, line);

                fill.Color = new SKColor(0xFF, 0xAA, 0x28);
                c.DrawCircle(cx, MidY, 15, fill);
                fill.Color = new SKColor(0x08, 0x08, 0x0A);
                c.DrawText((i + 1).ToString(), cx, MidY + 6, SKTextAlign.Center, big, fill);
            }
            else
            {
                line.Color = new SKColor(0x3A, 0x3A, 0x44);
                c.DrawCircle(cx, MidY, 9, line);
                fill.Color = new SKColor(0x55, 0x55, 0x62);
                c.DrawText((i + 1).ToString(), cx, MidY + 4, SKTextAlign.Center, small, fill);
            }
        }

        // Drawn only AFTER the finger has lifted: a marker under a fingertip is
        // a marker nobody can see, on a strip 9.4mm tall.
        if (mark >= 0)
        {
            fill.Color = SKColors.White;
            c.DrawRect(Math.Clamp(mark - 1, 0, NexusCanvas.Width - 3), 0, 3, NexusCanvas.Height, fill);
        }
    }

    private static void Report(List<(int Drawn, Sample S)> got)
    {
        if (got.Count < 2)
        {
            Console.WriteLine("Not enough taps to conclude anything. Run it again and tap at least two.");
            return;
        }

        Console.WriteLine("  drawn   tapped     delta");
        foreach (var (drawn, s) in got)
            Console.WriteLine($"  {drawn,5}   {s.DownX,6}   {Signed(s.DownX - drawn),7}");

        // Least squares, reported = a*drawn + b. Two unknowns, so a scale error
        // and a constant offset are told apart rather than averaged together.
        int n = got.Count;
        double sx  = got.Sum(t => (double)t.Drawn);
        double sy  = got.Sum(t => (double)t.S.DownX);
        double sxx = got.Sum(t => (double)t.Drawn * t.Drawn);
        double sxy = got.Sum(t => (double)t.Drawn * t.S.DownX);
        double denom = n * sxx - sx * sx;
        double a = denom == 0 ? 1 : (n * sxy - sx * sy) / denom;
        double b = (sy - a * sx) / n;
        double residual = got.Max(t => Math.Abs(t.S.DownX - (a * t.Drawn + b)));
        double worstOff = got.Max(t => (double)Math.Abs(t.S.DownX - t.Drawn));

        Console.WriteLine();
        Console.WriteLine($"  fit         reported = {a:0.0000} x drawn {Signed(b)}");
        Console.WriteLine($"  residual    worst {residual:0.0} px about that line (this is aim, not the device)");
        Console.WriteLine($"  error       worst {worstOff:0} px between finger and reading");
        Console.WriteLine($"  full scale  a tap on the right edge (x=639) would report {a * 639 + b:0}");
        Console.WriteLine();

        // The tolerance has to admit human aim. A fingertip is wider than the
        // error we are hunting, so the SLOPE is the discriminator: aim scatter
        // does not tilt a nine-point line.
        bool unity = Math.Abs(a - 1.0) <= 0.02 && Math.Abs(b) <= 12;
        if (unity)
        {
            Console.WriteLine("  VERDICT: 1:1. The digitizer reports panel pixels, so a tap lands in the");
            Console.WriteLine("           cell it looks like it lands in. Alignment is NOT the fault, and");
            Console.WriteLine("           the button bug is in the action, not the hit test.");
        }
        else
        {
            Console.WriteLine("  VERDICT: MISALIGNED. Where the finger is is not where the app reads it.");
            Console.WriteLine($"           A reported X converts to a panel pixel as: (x {Signed(-b)}) / {a:0.0000}");
            if (Math.Abs(a - 1.0) > 0.02)
                Console.WriteLine($"           The scale is off by {(a - 1) * 100:+0.0;-0.0}%: the digitizer's range is");
            Console.WriteLine($"           about 0..{a * 639 + b:0}, not 0..639. _docs/PROTOCOL.md is wrong.");
        }
    }


    /// <summary>
    /// Renders the card to a PNG with no device, and PROBES it: reads back the
    /// pixel at every target centre and at the mark column. An instrument that
    /// silently draws nothing would read, from the user's chair, as a dead
    /// panel - so the card is proved to exist before anyone is asked to tap it.
    /// </summary>
    public static int PreviewCard(string outPath, int active)
    {
        active = Math.Clamp(active, 0, Count - 1);
        int mark = TargetX(active) + 40;      // deliberately OFF the dot, so the
                                              // mark is distinguishable from it
        using var canvas = new NexusCanvas();
        DrawCard(canvas, active, mark);

        var frame = new byte[NexusDevice.FrameBytes];
        canvas.CopyTo(frame);

        using (var bmp = new SKBitmap(new SKImageInfo(
                   NexusCanvas.Width, NexusCanvas.Height, SKColorType.Bgra8888, SKAlphaType.Opaque)))
        {
            System.Runtime.InteropServices.Marshal.Copy(frame, 0, bmp.GetPixels(), frame.Length);
            using var data = bmp.Encode(SKEncodedImageFormat.Png, 100);
            using var fs = File.Create(outPath);
            data.SaveTo(fs);
        }

        (int R, int G, int B) At(int x, int y)
        {
            int p = (y * NexusCanvas.Width + x) * 4;
            return (frame[p + 2], frame[p + 1], frame[p]);
        }

        Console.WriteLine($"card -> {outPath}   (target {active + 1} lit, mark at x={mark})");
        bool ok = true;
        for (int i = 0; i < Count; i++)
        {
            var c = At(TargetX(i), NexusCanvas.Height / 2);
            bool lit = i == active;
            // The lit dot is amber and filled, so its centre is bright; an unlit
            // one is an outline, so its centre is background. The number glyph
            // sits on top of both, which is why this samples brightness bands
            // rather than an exact colour.
            bool bright = c.R > 120;
            if (lit != bright) { ok = false; Console.WriteLine($"  FAIL target {i + 1} at x={TargetX(i)}: rgb{c}, expected {(lit ? "lit" : "unlit")}"); }
            else Console.WriteLine($"  target {i + 1,2}  x={TargetX(i),3}  rgb{c}  {(lit ? "LIT" : "dim")}");
        }
        var m = At(mark, 4);
        bool white = m.R > 200 && m.G > 200 && m.B > 200;
        Console.WriteLine($"  mark      x={mark,3}  rgb{m}  {(white ? "WHITE" : "MISSING")}");
        if (!white) ok = false;
        // Positive control: a column that should NOT carry the mark.
        var off = At(Math.Min(NexusCanvas.Width - 1, mark + 20), 4);
        if (off.R > 200 && off.G > 200 && off.B > 200)
        {
            ok = false;
            Console.WriteLine($"  FAIL control at x={mark + 20} is also white - the mark is not a 3px bar");
        }
        else Console.WriteLine($"  control   x={mark + 20,3}  rgb{off}  clear");

        Console.WriteLine(ok ? "card OK" : "card BROKEN");
        return ok ? 0 : 1;
    }

    private static string Signed(int v)    => v.ToString("+0;-0;0");
    private static string Signed(double v) => v.ToString("+0.#;-0.#;0");
}

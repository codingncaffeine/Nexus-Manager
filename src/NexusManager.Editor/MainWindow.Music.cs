using Avalonia;
using Avalonia.Controls.Primitives;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using NexusManager.Audio;
using NexusManager.Render;

namespace NexusManager.Editor;

public sealed partial class MainWindow
{
    private Control? _musicView;
    private PanelPreview? _musicPreview;
    private AudioCapture? _capture;
    private VisualizerRenderer? _visRenderer;
    private readonly VisualizerSpec _visSpec = new();
    private TextBlock? _musicSource;
    private TextBlock? _musicLevel;

    /// <summary>
    /// The visualizer's own frame rate while it owns the panel.
    ///
    /// ⛔ The editor ticks at 250 ms - four frames a second - which is right for
    /// sensor readouts and useless for audio. The interval is raised on entering
    /// this view and PUT BACK on leaving; leaving it fast would run sensor
    /// sampling at 30 Hz, and sampling every sensor once cost 41 ms of a 41.7 ms
    /// budget when that was last measured.
    /// </summary>
    private static readonly TimeSpan MusicInterval = TimeSpan.FromMilliseconds(1000.0 / 30);
    private static readonly TimeSpan EditorInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>Seconds per visualizer frame, matching MusicInterval.</summary>
    private static readonly float MusicDt = (float)MusicInterval.TotalSeconds;

    private static string ModeName(VisualizerKind k) => k switch
    {
        VisualizerKind.Bars => "Spectrum bars",
        VisualizerKind.MirroredBars => "Mirrored bars",
        VisualizerKind.SegmentedVu => "Segmented bars",
        VisualizerKind.VuMeters => "VU meters",
        VisualizerKind.Spectrogram => "Spectrogram",
        VisualizerKind.ClassicSpectrum => "Classic analyser",
        VisualizerKind.ClassicScope => "Classic scope",
        VisualizerKind.Feedback => "Feedback tunnel",
        VisualizerKind.Fire => "Fire",
        VisualizerKind.GradientBars => "Gradient bars",
        VisualizerKind.SpectrumCurve => "Spectrum curve",
        VisualizerKind.DualChannelSpectrum => "Dual channel",
        VisualizerKind.DotMatrix => "Dot matrix",
        VisualizerKind.Oscilloscope => "Oscilloscope",
        VisualizerKind.FilledScope => "Filled scope",
        VisualizerKind.EnvelopeMirror => "Envelope mirror",
        VisualizerKind.LevelBar => "Level bar",
        VisualizerKind.ReactiveBackground => "Reactive background",
        VisualizerKind.BeatPulse => "Beat pulse",
        VisualizerKind.Superscope => "Superscope",
        VisualizerKind.Starfield => "Starfield",
        VisualizerKind.Plasma => "Plasma",
        VisualizerKind.EmeraldBars => "Emerald bars",
        VisualizerKind.DotScope => "Dot scope",
        VisualizerKind.Particles => "Particles",
        VisualizerKind.Ambience => "Ambience",
        VisualizerKind.Kaleidoscope => "Kaleidoscope",
        VisualizerKind.Vectorscope => "Vectorscope",
        VisualizerKind.ReflectedBars => "Reflected bars",
        VisualizerKind.GlowPills => "Glow pills",
        VisualizerKind.Blobs => "Blobs",
        VisualizerKind.Terrain => "Terrain",
        _ => k.ToString(),
    };

    private Control EnsureMusic()
    {
        if (_musicView is not null) return _musicView;

        var m = _settings.Music;
        _visSpec.Kind = m.Kind;
        _visSpec.BandCount = m.BandCount;
        _visSpec.Gap = m.Gap;
        _visSpec.ShowPeaks = m.ShowPeaks;
        _visSpec.Palette = m.Palette;
        _visSpec.Color = m.Color;
        _visSpec.PeakColor = m.PeakColor;

        _musicPreview = new PanelPreview(scale: 3);
        _visRenderer = new VisualizerRenderer();

        var modes = new ListBox
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            ItemsSource = VisualizerRenderer.Implemented.Select(ModeName).ToList(),
            SelectedIndex = Math.Max(0, Array.IndexOf(VisualizerRenderer.Implemented, m.Kind)),
        };
        modes.SelectionChanged += (_, _) =>
        {
            if (_building || modes.SelectedIndex < 0) return;
            _visSpec.Kind = VisualizerRenderer.Implemented[modes.SelectedIndex];
            _settings.Music.Kind = _visSpec.Kind;
            _settings.Save();
        };

        _musicSource = new TextBlock { Foreground = Style.TextDimBrush, FontSize = 11 };
        _musicLevel = new TextBlock
        {
            Foreground = Style.TextMidBrush, FontSize = 11,
            FontFamily = new FontFamily("monospace"),
        };

        var peaks = new CheckBox { Content = "Peak caps", IsChecked = m.ShowPeaks, FontSize = 12 };
        peaks.IsCheckedChanged += (_, _) =>
        {
            if (_building) return;
            _visSpec.ShowPeaks = peaks.IsChecked == true;
            _settings.Music.ShowPeaks = _visSpec.ShowPeaks;
            _settings.Save();
        };

        var bands = new Slider { Minimum = 8, Maximum = 64, Value = m.BandCount, Width = 180 };
        var bandsLabel = new TextBlock
        {
            Text = $"{m.BandCount} bands", Foreground = Style.TextDimBrush, FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
        };
        bands.PropertyChanged += (_, e) =>
        {
            if (_building || e.Property != RangeBase.ValueProperty) return;
            int v = (int)bands.Value;
            _visSpec.BandCount = v;
            bandsLabel.Text = $"{v} bands";
            _settings.Music.BandCount = v;
        };

        // Bar and cap colours are SEPARATE fields on purpose. The meters this
        // imitates treated the cap as its own object - green bars under a red or
        // white cap was the common look - so tying the cap to the bar colour
        // would remove the combination people actually remember.
        //
        // ⛔ The wheel matters more here than the hex box: this panel renders
        // green yellow-shifted (D12), so a colour reasoned about on a monitor
        // does not predict what lands on the glass. Pick it by looking.
        var barColour = new ColourField(m.Color, v =>
        {
            if (_building) return;
            _visSpec.Color = v ?? "#3B9AE1";
            _settings.Music.Color = _visSpec.Color;
            _settings.Save();
        });
        var capColour = new ColourField(m.PeakColor, v =>
        {
            if (_building) return;
            _visSpec.PeakColor = v ?? "#E8E8F0";
            _settings.Music.PeakColor = _visSpec.PeakColor;
            _settings.Save();
        });

        var palette = new ComboBox
        {
            ItemsSource = new[]
            {
                "Frequency ramp", "Single colour", "Theme colour",
                "Ocean", "Ember", "Meter", "Emerald",
            },
            SelectedIndex = (int)m.Palette,
            FontSize = 12,
        };
        palette.SelectionChanged += (_, _) =>
        {
            if (_building || palette.SelectedIndex < 0) return;
            _visSpec.Palette = (VisualizerPalette)palette.SelectedIndex;
            _settings.Music.Palette = _visSpec.Palette;
            _settings.Save();
        };

        var controls = new StackPanel
        {
            Spacing = 10,
            Children =
            {
                peaks,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 8,
                    Children = { bands, bandsLabel },
                },
                Row("Bar colour", barColour),
                Row("Cap colour", capColour),
                Row("Palette", palette),
            },
        };

        var right = new StackPanel
        {
            Spacing = 12,
            Children =
            {
                _musicPreview,
                _musicSource,
                _musicLevel,
                Style.CardPanel("Options", "Bands apply to the spectrum modes", controls),
            },
        };

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("220,*"), Margin = new Thickness(16) };
        var left = Style.CardPanel("Visualization", $"{VisualizerRenderer.Implemented.Length} available", modes);
        Grid.SetColumn(left, 0);
        Grid.SetColumn(right, 1);
        right.Margin = new Thickness(16, 0, 0, 0);
        grid.Children.Add(left);
        grid.Children.Add(right);

        _musicView = grid;
        return _musicView;
    }

    /// <summary>
    /// Starts capture and speeds the tick up. Capture is started HERE rather
    /// than at launch so the application does not hold a `parec` subprocess open
    /// for the whole session just in case the user visits this tab.
    /// </summary>
    private void StartMusic()
    {
        _capture ??= new AudioCapture(new AnalyserOptions { BandCount = 64 });
        _capture.Start();
        // The interval is owned by MaintainTickRate, which decides it from the
        // current view and screen every tick.
    }

    private void StopMusic()
    {
        // Interval restored by MaintainTickRate.
        _capture?.Dispose();
        _capture = null;
    }

    /// <summary>
    /// One visualizer frame. Deliberately does NOT sample sensors: this runs at
    /// 30 Hz and a full sensor sweep costs most of a frame budget on its own.
    /// </summary>
    private void RenderMusic()
    {
        if (_musicPreview is null || _visRenderer is null || _capture is null) return;

        var frame = _capture.Current;
        _canvas.Clear(Screen.Theme.BackgroundColor);
        _visRenderer.Draw(_canvas.Canvas, _visSpec,
            new SkiaSharp.SKRect(0, 0, NexusCanvas.Width, NexusCanvas.Height),
            frame, Screen.Theme, MusicDt);
        _musicPreview.Update(_canvas);

        // Status is refreshed a few times a second, not thirty: it is text, and
        // rewriting it every frame is work the user cannot even see.
        if (frame.Sequence % 10 != 0) return;
        var s = _capture.Status;
        _musicSource!.Text = s.Error is not null
            ? $"audio: {s.Error}"
            : $"source: {s.Sink}  ·  {(s.Running ? "capturing" : "waiting")}"
              + (s.Restarts > 0 ? $"  ·  {s.Restarts} reconnect(s)" : "");

        // ⛔ Names WHICH silence this is. A flat display has at least three
        // causes that look identical on the panel - wrong sink, suspended sink,
        // and actual silence - and without this line every one of them reads as
        // "the visualizer is broken".
        _musicLevel!.Text = s.Hops == 0
            ? "no audio delivered yet"
            : frame.Silent
                ? $"silent ({frame.PeakDbfs,6:F1} dBFS, below gate)"
                : $"peak   {frame.PeakDbfs,6:F1} dBFS";
    }
}

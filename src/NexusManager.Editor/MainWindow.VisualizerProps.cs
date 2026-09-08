using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using NexusManager.Render;

namespace NexusManager.Editor;

public sealed partial class MainWindow
{
    private readonly ListBox _visList = new();

    /// <summary>Its own panel in the CELLS column, not a section appended to
    /// Appearance. ⛔ It was added last in a narrow scrolling column, seventh of
    /// eight sections behind a font picker and three colour pickers - present,
    /// correct, and undiscoverable. A control nobody can find has not shipped.
    /// </summary>
    private readonly StackPanel _visProps = new() { Spacing = 6 };
    private int _visIndex;

    private VisualizerSpec? SelectedVisualizer =>
        Screen.Visualizers.Count == 0
            ? null
            : Screen.Visualizers[Math.Clamp(_visIndex, 0, Screen.Visualizers.Count - 1)];

    /// <summary>
    /// The visualizer cells on this screen, edited exactly like the buttons
    /// above them.
    ///
    /// ⛔ This exists because the cell type shipped without it. ScreenSpec grew
    /// Visualizers, the daemon and the editor both rendered them, the self-tests
    /// passed - and there was no way to add one without hand-editing JSON, so
    /// from inside the application the feature did not exist at all. A capability
    /// with no route to it is not a finished capability.
    /// </summary>
    private void RefreshVisualizerSection()
    {
        _visProps.Children.Clear();

        _visList.Background = Brushes.Transparent;
        _visList.BorderThickness = new Thickness(0);
        _visList.MaxHeight = 96;

        Building(() =>
        {
            _visList.ItemsSource = Screen.Visualizers
                .Select((v, i) => $"{i + 1}. {v.Kind}  ·  {v.EffectiveBands} bands")
                .ToList();
            _visList.SelectedIndex = Screen.Visualizers.Count == 0
                ? -1 : Math.Clamp(_visIndex, 0, Screen.Visualizers.Count - 1);
        });
        _visProps.Children.Add(_visList);

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 6,
            Margin = new Thickness(0, 6, 0, 0),
        };
        row.Children.Add(Flat("+", "Add a visualizer", () =>
        {
            Screen.Visualizers.Add(new VisualizerSpec());
            _visIndex = Screen.Visualizers.Count - 1;
            Changed(); RefreshThemePanel();
        }));
        row.Children.Add(Flat("−", "Remove this visualizer", () =>
        {
            if (Screen.Visualizers.Count == 0) return;
            Screen.Visualizers.RemoveAt(Math.Clamp(_visIndex, 0, Screen.Visualizers.Count - 1));
            _visIndex = Math.Max(0, _visIndex - 1);
            Changed(); RefreshThemePanel();
        }));
        _visProps.Children.Add(row);

        var v = SelectedVisualizer;
        if (v is null)
        {
            _visProps.Children.Add(Style.Note(
                "No visualizer on this screen. Adding one needs an audio server - the "
              + "capture uses parec, from pulseaudio-utils or libpulse."));
            return;
        }

        _visList.SelectionChanged -= VisualizerSelected;
        _visList.SelectionChanged += VisualizerSelected;

        // Only the modes that actually have a draw routine. Offering the enum
        // would list modes that silently render as bars.
        string[] modes = VisualizerRenderer.Implemented.Select(k => k.ToString()).ToArray();
        _visProps.Children.Add(Row("Mode", Choice(modes, v.Kind.ToString(), s =>
        {
            if (Enum.TryParse<VisualizerKind>(s, out var k)) { v.Kind = k; Changed(); RefreshVisualizerLabels(); }
        })));

        string[] palettes = Enum.GetNames<VisualizerPalette>();
        _visProps.Children.Add(Row("Palette", Choice(palettes, v.Palette.ToString(), s =>
        {
            if (Enum.TryParse<VisualizerPalette>(s, out var p)) { v.Palette = p; Changed(); }
        })));

        _visProps.Children.Add(Row("Bands", Num(v.BandCount, n =>
        {
            v.BandCount = (int)Math.Clamp(n, 4, 128);
            Changed(); RefreshVisualizerLabels();
        })));

        _visProps.Children.Add(Row("Width", Num(v.Weight, n => { v.Weight = Math.Max(0.05, n); Changed(); })));
        _visProps.Children.Add(Style.Note(
            "Share of the strip, the same units a readout uses. Three readouts and one "
          + "visualizer at width 1 each gives the visualizer a quarter."));

        _visProps.Children.Add(Row("Gap", Num(v.Gap, n => { v.Gap = (int)Math.Clamp(n, 0, 8); Changed(); })));

        var peaks = new CheckBox { Content = "Peak caps", IsChecked = v.ShowPeaks };
        peaks.IsCheckedChanged += (_, _) =>
        {
            if (_building) return;
            v.ShowPeaks = peaks.IsChecked == true; Changed();
        };
        _visProps.Children.Add(peaks);

        _visProps.Children.Add(Row("Bar colour", Colour(v.Color, s => { v.Color = s ?? "#3B9AE1"; Changed(); })));
        _visProps.Children.Add(Row("Cap colour", Colour(v.PeakColor, s => { v.PeakColor = s ?? "#E8E8F0"; Changed(); })));
        _visProps.Children.Add(Style.Note(
            "Cap colour is independent of the bars - green bars under a red or white "
          + "cap is the combination these meters are remembered for."));
    }

    private void VisualizerSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (_building || _visList.SelectedIndex < 0) return;
        _visIndex = _visList.SelectedIndex;
        RefreshThemePanel();
    }

    /// <summary>Relabels the list without rebuilding the panel that owns it.
    /// ⛔ Assigning ItemsSource raises SelectionChanged, which would re-enter
    /// RefreshThemePanel from inside itself - the same loop the buttons hit.
    /// </summary>
    private void RefreshVisualizerLabels() => Building(() =>
    {
        int keep = _visList.SelectedIndex;
        _visList.ItemsSource = Screen.Visualizers
            .Select((v, i) => $"{i + 1}. {v.Kind}  ·  {v.EffectiveBands} bands")
            .ToList();
        _visList.SelectedIndex = Math.Clamp(keep, -1, Screen.Visualizers.Count - 1);
    });
}

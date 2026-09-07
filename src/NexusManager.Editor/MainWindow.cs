using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using NexusManager.Device;
using NexusManager.Render;
using NexusManager.Sensors;

namespace NexusManager.Editor;

public sealed partial class MainWindow : Window
{
    // Everything expensive is created on a background thread in InitAsync.
    // Sensor discovery shells out to nvidia-smi with a five second timeout, and
    // a blocked UI thread does not paint and does not answer the close button —
    // which is exactly how this failed the first time.
    private SensorRegistry? _reg;
    private NexusDevice? _device;
    private ScreenRenderer? _renderer;
    private string _rendererSig = "";

    private readonly NexusCanvas _canvas = new();
    private readonly PanelPreview _preview = new(scale: 2);
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(250) };

    private ScreenSet _set = new();
    private List<History> _histories = [];
    private int _screenIndex;
    private int _moduleIndex;
    private bool _ready;
    // Populating a property panel assigns Text/Value on fresh controls, which
    // raises the same change events a user edit does. Without this guard the
    // handler rebuilds the panel, which raises the events again — an infinite
    // loop that pins the UI thread at 100% CPU.
    private bool _building;
    private volatile bool _pushBusy;
    // Reused rather than cloned each tick: the frame is 122880 bytes, and
    // cloning it four times a second is ~0.5 MB/s of garbage for no reason.
    // Safe to reuse because _pushBusy prevents a second push overlapping.
    private readonly byte[] _pushBuffer = new byte[NexusDevice.FrameBytes];

    private readonly ListBox _screenList = new();
    private readonly ListBox _moduleList = new();
    private readonly StackPanel _moduleProps = new() { Spacing = 4 };
    private readonly StackPanel _themeProps = new() { Spacing = 4 };
    private readonly TextBlock _status = new() { Foreground = Brushes.Gray };
    private readonly CheckBox _liveToDevice = new() { Content = "Live to panel", IsChecked = true };

    public MainWindow()
    {
        Title = "Nexus Manager";
        // Window icon feeds X11's _NET_WM_ICON, which the taskbar and window
        // list read. On Wayland the compositor matches the app id to a .desktop
        // file instead, so both this and the desktop entry are needed.
        try
        {
            using var iconStream = AssetLoader.Open(
                new Uri("avares://nexus-manager-editor/Assets/icon.png"));
            Icon = new WindowIcon(iconStream);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[boot] icon unavailable: {ex.Message}");
        }
        Background = new SolidColorBrush(Color.FromRgb(32, 32, 38));
        Width = 1020; Height = 740;
        MinWidth = 880; MinHeight = 600;

        // Placeholder screen so the layout has something valid to bind against
        // before discovery finishes.
        _set.Screens.Add(new ScreenSpec { Name = "Loading…" });

        Content = BuildLayout();
        _status.Text = "discovering sensors…";

        Console.Error.WriteLine("[boot] MainWindow constructed");
        Opened += async (_, _) => { Console.Error.WriteLine("[boot] Opened fired"); await InitAsync(); };
        Closed += (_, _) => Shutdown();
    }

    private async Task InitAsync()
    {
        // Every stage reports. An async void event handler swallows exceptions,
        // so a failure here otherwise presents as a window stuck on its
        // placeholder text with nothing to explain why.
        void Stage(string s) => Console.Error.WriteLine($"[init] {s}");
        try { await InitCoreAsync(Stage); }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[init] FAILED: {ex}");
            _status.Text = $"startup failed: {ex.Message}";
        }
    }

    private async Task InitCoreAsync(Action<string> Stage)
    {
        Stage("discovering sensors");
        SensorRegistry reg;
        ScreenSet set;
        try
        {
            (reg, set) = await Task.Run(() =>
            {
                var r = SensorRegistry.CreateDefault();
                ScreenSet? loaded = null;
                try
                {
                    if (File.Exists(Config.Path))
                        loaded = System.Text.Json.JsonSerializer.Deserialize<ScreenSet>(
                            File.ReadAllText(Config.Path), Config.Json);
                }
                catch (Exception) { /* fall through to discovery */ }
                return (r, loaded is { Screens.Count: > 0 } ? loaded : Config.Discover(r, false));
            });
        }
        catch (Exception ex)
        {
            _status.Text = $"sensor discovery failed: {ex.Message}";
            return;
        }

        Stage($"discovered {reg.All.Count} sensors, {set.Screens.Count} screen(s)");
        _reg = reg;
        _set = set;
        _screenIndex = 0; _moduleIndex = 0;
        _ready = true;

        Stage("building render state");         RebuildScreenState();
        Stage("screen list");         RefreshScreenList();
        Stage("module list");         RefreshModuleList();
        Stage("theme panel");         RefreshThemePanel();

        Stage("opening panel");         await Task.Run(TryOpenDevice);

        _timer.Tick += (_, _) => Tick();
        _timer.Start();
        Stage("ready");
    }

    private void Shutdown()
    {
        _timer.Stop();
        try
        {
            // Never leave stale readings on the panel.
            _device?.Blank();
            _device?.SetBrightness(0);
        }
        catch (Exception) { }
        _device?.Dispose();
        // Font matching is expensive (SKFontManager.MatchFamily). Only rebuild
        // the renderer when something it actually bakes in has changed — a
        // colour edit does not need new typefaces.
        string sig = $"{Screen.Theme.FontFamily}|{Screen.Theme.CaptionSize}|" +
                     $"{Screen.Theme.ValueSize}|{Screen.Theme.CaptionBold}|{Screen.Theme.ValueBold}";
        if (_renderer is null || sig != _rendererSig)
        {
            _renderer?.Dispose();
            _renderer = new ScreenRenderer(Screen.Theme);
            _rendererSig = sig;
        }
        _canvas.Dispose();
        _reg?.Dispose();
    }

    private ScreenSpec Screen => _set.Screens[Math.Clamp(_screenIndex, 0, _set.Screens.Count - 1)];

    private ModuleSpec? Module =>
        Screen.Modules.Count == 0 ? null
        : Screen.Modules[Math.Clamp(_moduleIndex, 0, Screen.Modules.Count - 1)];

    private void RebuildScreenState()
    {
        if (_set.Screens.Count == 0) _set.Screens.Add(new ScreenSpec { Name = "Screen 1" });
        _screenIndex = Math.Clamp(_screenIndex, 0, _set.Screens.Count - 1);

        // Font matching is expensive (SKFontManager.MatchFamily). Only rebuild
        // the renderer when something it actually bakes in has changed — a
        // colour edit does not need new typefaces.
        string sig = $"{Screen.Theme.FontFamily}|{Screen.Theme.CaptionSize}|" +
                     $"{Screen.Theme.ValueSize}|{Screen.Theme.CaptionBold}|{Screen.Theme.ValueBold}";
        if (_renderer is null || sig != _rendererSig)
        {
            _renderer?.Dispose();
            _renderer = new ScreenRenderer(Screen.Theme);
            _rendererSig = sig;
        }

        var layout = ScreenLayout.Compute(Screen, out var narrow);
        _histories = layout.Select(l => new History(Math.Max(2, (int)l.Rect.Width))).ToList();

        _reg?.SetActive(_set.Screens.SelectMany(s => s.Modules).Select(m => m.Source).Distinct());
        _status.Text = narrow.Count > 0
            ? "⚠ " + string.Join("; ", narrow)
            : $"{_set.Screens.Count} screen(s) · {Screen.Modules.Count} module(s) · " +
              $"{_reg?.All.Count ?? 0} sensors · panel {(_device is null ? "not connected" : "connected")}";
    }

    private void Tick()
    {
        if (!_ready || _renderer is null || _reg is null) return;

        _reg.Sample();

        var layout = ScreenLayout.Compute(Screen, out _);
        while (_histories.Count < layout.Count) _histories.Add(new History(160));

        for (int i = 0; i < layout.Count && i < _histories.Count; i++)
        {
            double v = _reg.Read(layout[i].Spec.Source);
            if (!double.IsNaN(v)) _histories[i].Add(v);
        }

        _renderer!.Draw(_canvas, layout, _histories,
            k => { double v = _reg.Read(k); return double.IsNaN(v) ? 0 : v; });
        if (_set.ShowPageIndicator)
            PageIndicator.Draw(_canvas.Canvas, _screenIndex, _set.Screens.Count, Screen.Theme.CaptionColor);

        _preview.Update(_canvas);

        // Diagnostic only: RSS climbing does not distinguish a leak from a GC
        // that simply has no reason to run yet. With NEXUSMANAGER_GCPROBE=1 the
        // heap is collected every tick, so any remaining growth is real.
        if (Environment.GetEnvironmentVariable("NEXUSMANAGER_GCPROBE") == "1")
        {
            GC.Collect(2, GCCollectionMode.Forced, blocking: true);
            GC.WaitForPendingFinalizers();
        }

        // 121 HID writes cost ~15 ms. Off the UI thread, and never queued twice.
        if (_liveToDevice.IsChecked == true && _device is not null && !_pushBusy)
        {
            _pushBusy = true;
            Array.Copy(_preview.Frame, _pushBuffer, NexusDevice.FrameBytes);
            _ = Task.Run(() =>
            {
                try { _device.PushFrame(_pushBuffer); }
                catch (Exception) { }
                finally { _pushBusy = false; }
            });
        }
    }

    private void TryOpenDevice()
    {
        try
        {
            var d = NexusDevice.Open();
            d.SetBrightness(_set.Brightness);
            _device = d;
        }
        catch (Exception)
        {
            _device = null;
            Dispatcher.UIThread.Post(() =>
            {
                _liveToDevice.IsChecked = false;
                _liveToDevice.IsEnabled = false;
            });
        }
    }

    /// <summary>A value changed: refresh what is RENDERED. Deliberately does not
    /// touch the property panels — they are what raised this, and rebuilding them
    /// here is what caused the feedback loop.</summary>
    /// <summary>
    /// Runs an action with the re-entrancy guard raised.
    ///
    /// Any code that populates controls — assigning Text, Value, ItemsSource,
    /// SelectedIndex — raises exactly the same events a user edit does. Without
    /// a guard, a handler that refreshes the UI re-enters itself forever. This
    /// happened twice here through two different paths (TextChanged and
    /// SelectionChanged), so the guard is centralised rather than sprinkled.
    /// </summary>
    private void Building(Action action)
    {
        bool prev = _building;
        _building = true;
        try { action(); }
        finally { _building = prev; }
    }

    private void Changed()
    {
        if (!_ready || _building) return;
        RebuildScreenState();
    }

    /// <summary>Structural change (module added, removed, reordered, screen
    /// switched): the lists must be rebuilt as well.</summary>
    private void StructureChanged()
    {
        if (!_ready || _building) return;
        RebuildScreenState();
        Building(RefreshModuleList);
    }
}

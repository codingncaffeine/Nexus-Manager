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
    /// <summary>The preview IS the layout editor: cells are resized by dragging
    /// on it directly, rather than on a second bar drawing the same thing.
    private readonly PanelPreview _preview = new(scale: 3, interactive: true);
    private readonly DashboardLayout _dashLayout = DashboardLayout.Load();
    private readonly AppSettings _settings = AppSettings.Load();
    /// <summary>Created once the registry exists, so it can resolve keys.</summary>
    private SensorLogger? _logger;
    private TrayIcon? _tray;
    /// <summary>Set when the tray asks to quit for real, so Closing stops
    /// hiding the window and lets the process go.</summary>
    private bool _quitting;

    /// <summary>Settings ask for a hidden start. Read here rather than in App so
    /// the command line and the saved preference land in the same place.</summary>
    public bool PrefersHidden => _settings.StartMinimised;

    /// <summary>Celsius / Fahrenheit switch. iCUE exposes the same choice, and
    /// the conversion is display-only — readings are stored in their native unit
    /// so switching is never lossy.</summary>
    private readonly ComboBox _unitToggle = new()
    {
        ItemsSource = new[] { "°C", "°F", "K" },
        SelectedIndex = 0, Width = 68, FontSize = 11,
        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
    };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    /// <summary>Wall clock the background animation plays against, so the
    /// preview runs a GIF at its own rate rather than at the UI tick rate.</summary>
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    /// <summary>Throttles reconnection attempts while the panel is absent.</summary>
    private readonly System.Diagnostics.Stopwatch _reopenClock = System.Diagnostics.Stopwatch.StartNew();
    private volatile bool _reopening;
    /// <summary>Consecutive failed frame pushes. One is noise; a run of them
    /// means the panel is gone.</summary>
    private int _pushFailures;

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
        Background = Style.PageBrush;
        Width = 1020; Height = 740;
        MinWidth = 880; MinHeight = 600;
        // Come back the size and place it was left. Read before the window is
        // shown, so there is no visible jump from the default to the saved size.
        RestoreGeometry();

        // Placeholder screen so the layout has something valid to bind against
        // before discovery finishes.
        _set.Screens.Add(new ScreenSpec { Name = "Loading…" });

        Content = BuildShell();
        ShowView("dashboard");
        _status.Text = "discovering sensors…";

        Console.Error.WriteLine("[boot] MainWindow constructed");
        // Init is posted to the dispatcher rather than hung off Opened. A
        // window that starts hidden in the tray never RAISES Opened, so an
        // Opened-triggered startup silently does nothing in exactly the mode
        // that has to keep the panel alive.
        Dispatcher.UIThread.Post(async () =>
        {
            Console.Error.WriteLine("[boot] init posted");
            await InitAsync();
        }, DispatcherPriority.Background);
        Closing += (_, e) =>
        {
            // Dismissing the window must not blank the panel: the whole point
            // of the tray is that the display keeps running.
            // Geometry is recorded on the way out whichever way that is: hiding
            // to the tray never raises Closed, so saving only there loses it.
            SaveGeometry();
            if (_quitting || !_settings.CloseToTray) return;
            e.Cancel = true;
            Hide();
        };
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
        _logger = new SensorLogger(reg);
        reg.Scale = _settings.Scale;
        _unitToggle.SelectedIndex = (int)_settings.Scale;
        HomeDefaults.Seed(_dashLayout, reg);
        _set = set;
        _screenIndex = 0; _moduleIndex = 0;
        _ready = true;

        Stage("building render state");         RebuildScreenState();
        Stage("screen list");         RefreshScreenList();
        Stage("module list");         RefreshModuleList();
        Stage("theme panel");         RefreshThemePanel();

        // The self test never touches the device: it checks that views BUILD,
        // and it has to stay safe to run while a real instance owns the panel.
        if (App.SelfTest || App.NoDevice) Stage("panel skipped");
        else { Stage("opening panel"); await Task.Run(TryOpenDevice); }

        _unitToggle.SelectionChanged += (_, _) =>
        {
            if (_reg is null) return;
            _reg.Scale = _unitToggle.SelectedIndex switch
            {
                1 => TemperatureScale.Fahrenheit,
                2 => TemperatureScale.Kelvin,
                _ => TemperatureScale.Celsius,
            };
            _dashboard?.Rebuild();
        };

        WireActions();
        // Diagnostics must never appear in the user's tray. --selftest,
        // --probe-drag and --no-device all ran BuildTray, so every diagnostic
        // run put a second icon beside the real one until it exited.
        if (App.SelfTest || App.ProbeDrag || App.NoDevice) Stage("tray skipped");
        else { Stage("tray"); BuildTray(); }

        // Launching the application again is how people ask for the window
        // back - the second process signals us and exits rather than starting
        // a rival render loop.
        if (App.Instance is { } instance)
        {
            instance.MessageReceived += message =>
            {
                if (message != "show") return;
                Dispatcher.UIThread.Post(ShowWindow);
            };
            instance.StartListening();
        }

        _timer.Tick += (_, _) => Tick();
        _timer.Start();
        ShowView(_view);
        if (App.ProbeDrag) { Stage("probe"); RunDragProbe(); return; }
        if (App.ProbeAction) { Stage("probe-action"); _ = RunActionProbe(); return; }
        if (App.SelfTest) { Stage("selftest"); RunSelfTest(); return; }
        Stage("ready");
    }

    private void Shutdown()
    {
        _timer.Stop();
        // Flush a pending autosave rather than dropping the last edit.
        if (_autosave.IsEnabled)
        {
            _autosave.Stop();
            try { Config.SaveAsync(_set, null).GetAwaiter().GetResult(); }
            catch (Exception ex) { Console.Error.WriteLine($"[save] {ex.Message}"); }
        }
        try
        {
            // Hand the panel back to the firmware animation rather than leaving a
            // dead black strip on the keyboard. See NexusDevice.HandBack.
            _device?.HandBack(_set.IdleAnimation, _set.Brightness);
        }
        catch (Exception) { }
        StopTouch();
        _device?.Dispose();
        // Remove the tray icon explicitly: leaving it to finalisation can leave
        // a dead entry in the tray after the process is gone.
        try { _tray?.Dispose(); } catch (Exception) { }
        _tray = null;
        _logger?.Dispose();
        // This block used to hold a COPY of RebuildScreenState reader code, so
        // shutdown constructed a renderer instead of releasing one - the exit
        // path allocated a fresh ScreenRenderer and then dropped it undisposed.
        _renderer?.Dispose();
        _renderer = null;
        _canvas.Dispose();
        _reg?.Dispose();
        // Released last, and only on a real exit: close-to-tray must keep it.
        App.Instance?.Dispose();
        App.Instance = null;
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

        var (layout, buttonLayout) = ScreenLayout.ComputeAll(Screen, out var narrow);
        _buttonLayout = buttonLayout;
        _histories = layout.Select(l => new History(Math.Max(2, (int)l.Rect.Width))).ToList();

        _preview.SetScreen(Screen);
        _preview.SelectedIndex = _moduleIndex;
        _reg?.SetActive(_set.Screens.SelectMany(s => s.Modules).Select(m => m.Source).Distinct());
        _status.Text = narrow.Count > 0
            ? "⚠ " + string.Join("; ", narrow)
            : $"{_set.Screens.Count} screen(s) · {Screen.Modules.Count} module(s) · " +
              $"{_reg?.All.Count ?? 0} sensors · panel {(_device is null ? "not connected" : "connected")}";
    }

    private void Tick()
    {
        if (!_ready || _renderer is null || _reg is null) return;

        MaintainDevice();

        // The visualizer owns the frame when its tab is up. It renders its
        // own canvas and shares the push path below, but must NOT fall
        // through the sensor sampling: this branch runs at 30 Hz and one
        // full sensor sweep costs most of a frame budget by itself.
        if (_view == "music")
        {
            RenderMusic();
            PushToPanel(_musicPreview?.Frame);
            return;
        }

        _reg.Sample();

        var (layout, buttonLayout) = ScreenLayout.ComputeAll(Screen, out _);
        _buttonLayout = buttonLayout;
        while (_histories.Count < layout.Count) _histories.Add(new History(160));

        for (int i = 0; i < layout.Count && i < _histories.Count; i++)
        {
            double v = _reg.Read(layout[i].Spec.Source);
            if (!double.IsNaN(v)) _histories[i].Add(v);
        }

        _renderer!.Draw(_canvas, layout, _histories,
            k => { double v = _reg.Read(k); return double.IsNaN(v) ? 0 : v; },
            Screen.Background, _clock.Elapsed,
            buttonLayout, _pressedButton, _flashUntil);
        if (_set.ShowPageIndicator)
            PageIndicator.Draw(_canvas.Canvas, _screenIndex, _set.Screens.Count, Screen.Theme.CaptionColor);

        _preview.Update(_canvas);
        if (_view == "dashboard") _dashboard?.Refresh();
        else if (_view == "home") _home?.Refresh();
        // The Home device card carries the same frame as the panel editor
        // preview, so whichever view is up shows live output.
        _home?.Preview.Update(_canvas);

        // Diagnostic only: RSS climbing does not distinguish a leak from a GC
        // that simply has no reason to run yet. With NEXUSMANAGER_GCPROBE=1 the
        // heap is collected every tick, so any remaining growth is real.
        if (Environment.GetEnvironmentVariable("NEXUSMANAGER_GCPROBE") == "1")
        {
            GC.Collect(2, GCCollectionMode.Forced, blocking: true);
            GC.WaitForPendingFinalizers();
        }

        PushToPanel(_preview.Frame);
    }

    /// <summary>
    /// Hands one composed frame to the panel, off the UI thread and never
    /// queued twice. Shared by the screen editor and the visualizer so the
    /// reconnect handling cannot exist in only one of them - which is how
    /// swipe was lost once already.
    /// </summary>
    private void PushToPanel(byte[]? source)
    {
        if (source is null) return;

        // 121 HID writes cost ~15 ms. Off the UI thread, and never queued twice.
        if (_liveToDevice.IsChecked == true && _device is not null && !_pushBusy)
        {
            // Captured once: the reconnect path may null _device between the
            // guard above and the push below, on a different thread.
            var dev = _device;
            _pushBusy = true;
            Array.Copy(source, _pushBuffer, NexusDevice.FrameBytes);
            _ = Task.Run(() =>
            {
                try { dev.PushFrame(_pushBuffer); _pushFailures = 0; }
                catch (Exception)
                {
                    // An unplugged panel keeps a non-null handle whose writes
                    // fail forever. Swallowing them silently means the reconnect
                    // path never runs and the strip stays dark until a restart.
                    if (++_pushFailures >= 8)
                    {
                        Console.Error.WriteLine("[device] panel stopped responding; will reconnect");
                        var dead = dev;
                        // The touch stream is on the same physical device, so it
                        // is dead too - leaving it open means the reconnect never
                        // reopens it and swipe stays broken until a restart.
                        Avalonia.Threading.Dispatcher.UIThread.Post(StopTouch);
                        _device = null;
                        _pushFailures = 0;
                        try { dead?.Dispose(); } catch (Exception) { }
                    }
                }
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
            StartTouch();
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

    /// <summary>
    /// Retries the connection while the panel is dark.
    ///
    /// Without this the device is opened exactly once at startup, so a panel
    /// that was unplugged at login - or replugged at any point after - stays
    /// blank until the application is restarted. A blank strip on the keyboard
    /// reads as broken hardware, so the reconnect is part of the feature, not
    /// a nicety.
    ///
    /// Throttled: opening a HID device that is not there costs an enumeration
    /// of every HID node, which is not something to do four times a second.
    /// </summary>
    private void MaintainDevice()
    {
        if (_device is not null || _reopening) return;
        if (_reopenClock.Elapsed < TimeSpan.FromSeconds(3)) return;
        _reopenClock.Restart();
        _reopening = true;
        _ = Task.Run(() =>
        {
            try
            {
                var d = NexusDevice.Open();
                d.SetBrightness(_settings.Brightness);
                _device = d;
                // Gestures need their own stream, and it has to be opened
                // whenever the device is - including after a reconnect.
                Dispatcher.UIThread.Post(StartTouch);
                Dispatcher.UIThread.Post(() =>
                {
                    _liveToDevice.IsEnabled = true;
                    _liveToDevice.IsChecked = true;
                    Console.Error.WriteLine("[device] panel connected");
                });
            }
            catch (Exception) { /* still absent; try again on the next window */ }
            finally { _reopening = false; }
        });
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
        QueueAutosave();
    }

    /// <summary>Structural change (module added, removed, reordered, screen
    /// switched): the lists must be rebuilt as well.</summary>
    private void StructureChanged()
    {
        if (!_ready || _building) return;
        RebuildScreenState();
        Building(RefreshModuleList);
        QueueAutosave();
    }
}

using System.Globalization;
using System.Text;

namespace NexusManager.Sensors;

/// <summary>Everything the logging UI collects, in one serialisable place.</summary>
public sealed class LogSettings
{
    public string Directory { get; set; } =
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

    /// <summary>Seconds between rows. iCUE defaults to 5.</summary>
    public int IntervalSeconds { get; set; } = 5;

    /// <summary>Stop after this many minutes. Corsair warn about the alternative
    /// in their own guide: logging with no limit writes to the drive until it is
    /// stopped by hand.</summary>
    public bool LimitDuration { get; set; } = true;
    public int DurationMinutes { get; set; } = 5;

    /// <summary>Sensor keys to record. Empty means nothing is logged - there is
    /// no implicit "everything", because 70 columns is not a useful default.</summary>
    public List<string> Sensors { get; set; } = [];
}

/// <summary>
/// Writes selected readings to a CSV on a timer, as iCUE's Sensor Logging does.
///
/// One row per interval, one column per sensor, plus a timestamp. Values are
/// written in the sensor's NATIVE unit with the unit named in the header, so a
/// log is not silently re-scaled by whatever the display happened to be set to
/// when it was recorded.
/// </summary>
public sealed class SensorLogger : IDisposable
{
    private readonly SensorRegistry _reg;
    private CancellationTokenSource? _cts;
    private Task? _task;

    public SensorLogger(SensorRegistry reg) => _reg = reg;

    public bool IsRunning => _task is { IsCompleted: false };

    /// <summary>Path of the file currently being written, or the last one.</summary>
    public string? CurrentFile { get; private set; }

    /// <summary>Rows written to <see cref="CurrentFile"/> so far.</summary>
    public int RowsWritten { get; private set; }

    /// <summary>Raised on the logger's own thread whenever a row lands or the run
    /// ends, so the UI can show progress without polling.</summary>
    public event Action? Progressed;

    public string Start(LogSettings settings)
    {
        Stop();
        if (settings.Sensors.Count == 0)
            throw new InvalidOperationException("Choose at least one sensor to log.");

        Directory.CreateDirectory(settings.Directory);
        string path = Path.Combine(
            settings.Directory,
            $"nexus-manager-sensors-{DateTime.Now:yyyyMMdd-HHmmss}.csv");

        CurrentFile = path;
        RowsWritten = 0;
        _cts = new CancellationTokenSource();
        _task = Task.Run(() => RunAsync(settings, path, _cts.Token));
        return path;
    }

    private async Task RunAsync(LogSettings settings, string path, CancellationToken ct)
    {
        try
        {
            var keys = settings.Sensors
                .Where(k => _reg.Describe(k) is not null)
                .ToList();

            // Written with FileShare.Read and flushed every row: a log being
            // watched in a spreadsheet must not be locked, and a crash must not
            // cost the rows already taken.
            await using var stream = new FileStream(
                path, FileMode.Create, FileAccess.Write, FileShare.Read);
            await using var writer = new StreamWriter(stream, new UTF8Encoding(false));

            var header = new StringBuilder("Timestamp");
            foreach (string k in keys)
            {
                var d = _reg.Describe(k)!;
                string unit = Units.Symbol(d.Unit, TemperatureScale.Celsius);
                header.Append(',').Append(Escape(
                    string.IsNullOrEmpty(unit) ? $"{d.Device} {d.Label}"
                                               : $"{d.Device} {d.Label} ({unit})"));
            }
            await writer.WriteLineAsync(header.ToString()).ConfigureAwait(false);
            await writer.FlushAsync(ct).ConfigureAwait(false);

            var interval = TimeSpan.FromSeconds(Math.Max(1, settings.IntervalSeconds));
            DateTime deadline = settings.LimitDuration
                ? DateTime.UtcNow.AddMinutes(Math.Max(1, settings.DurationMinutes))
                : DateTime.MaxValue;

            while (!ct.IsCancellationRequested && DateTime.UtcNow < deadline)
            {
                _reg.Sample();
                var row = new StringBuilder(
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
                foreach (string k in keys)
                {
                    double v = _reg.Read(k);
                    row.Append(',');
                    if (!double.IsNaN(v))
                        row.Append(v.ToString("F2", CultureInfo.InvariantCulture));
                }
                await writer.WriteLineAsync(row.ToString()).ConfigureAwait(false);
                await writer.FlushAsync(ct).ConfigureAwait(false);
                RowsWritten++;
                Progressed?.Invoke();

                try { await Task.Delay(interval, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[log] failed: {ex.Message}");
        }
        finally
        {
            Progressed?.Invoke();
        }
    }

    public void Stop()
    {
        try
        {
            _cts?.Cancel();
            _task?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (Exception) { }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            _task = null;
        }
    }

    /// <summary>RFC 4180 quoting. Device names carry commas often enough that
    /// skipping this produces a file that opens misaligned.</summary>
    private static string Escape(string s) =>
        s.Contains(',') || s.Contains('"') || s.Contains('\n')
            ? "\"" + s.Replace("\"", "\"\"") + "\""
            : s;

    public void Dispose() => Stop();
}

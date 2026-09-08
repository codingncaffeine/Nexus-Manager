using System.Net.Sockets;
using System.Text;

namespace NexusManager.Device;

/// <summary>
/// Makes sure only one process drives the panel, and lets a second launch talk to
/// the first instead of fighting it.
///
/// Without this, starting the application again while a copy is already running
/// gives TWO render loops pushing frames to the same 640x48 strip, and the panel
/// visibly flickers between two different screens. The device does not refuse the
/// second writer - HID output reports from two handles simply interleave - so
/// nothing errors and nothing is logged. It just looks broken.
///
/// Three files under XDG_RUNTIME_DIR, each doing one job:
///
///   nexus-manager.lock    the arbiter. An exclusive FileShare.None handle, which
///                         .NET implements with flock() on Linux. This is what
///                         makes "am I first?" atomic - checking for a socket or
///                         a pid file instead races two simultaneous launches.
///   nexus-manager.owner   who won, in text, so the loser can say something
///                         useful rather than "already running".
///   nexus-manager.sock    a Unix socket the winner listens on, so a second
///                         launch can ask it to show its window - which is what
///                         the user wanted by launching again in the first place.
///
/// The lock is released by the kernel when the process dies, however it dies, so
/// a crash cannot leave the application permanently unstartable. The socket file
/// CAN be left behind, so the new owner unlinks it before binding - safe, because
/// by then it holds the lock.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private readonly FileStream _lock;
    private Socket? _listener;
    private CancellationTokenSource? _cts;

    /// <summary>Raised on a background thread when another launch sends a message.
    /// A UI caller must marshal to its own thread.</summary>
    public event Action<string>? MessageReceived;

    private SingleInstance(FileStream held) => _lock = held;

    /// <summary>
    /// Where the lock, owner and socket files live.
    ///
    /// XDG_RUNTIME_DIR is the right answer and is present in every normal desktop
    /// session: it is already mode 0700, owned by the user, and cleaned up at
    /// logout.
    ///
    /// ⛔ The fallback under /tmp needs care, because /tmp is world-writable and
    /// this path is PREDICTABLE. Another local user can create
    /// `nexus-manager-&lt;name&gt;` first and then own the directory the lock, the
    /// owner file and the socket are about to be created in. That is not
    /// theoretical: `File.WriteAllText` on the owner file follows symlinks, so a
    /// symlink planted at that name is an arbitrary-file-write into anything the
    /// victim can write, and a squatted socket path makes a second launch talk to
    /// the attacker instead of to the running instance.
    ///
    /// So the directory is created 0700, and its mode is CHECKED afterwards.
    /// CreateDirectory does not alter an existing directory, so creating with the
    /// right mode proves nothing on its own - the check is the part that matters.
    /// A directory that is not exactly 0700 is refused rather than used.
    /// </summary>
    private static string Dir
    {
        get
        {
            string? runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
            if (!string.IsNullOrWhiteSpace(runtime) && Directory.Exists(runtime))
                return runtime;

            // Per-user rather than one shared path, so two users on one machine do
            // not lock each other out of their own panels.
            string fallback = Path.Combine(Path.GetTempPath(), $"nexus-manager-{Environment.UserName}");
            const UnixFileMode Private = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

            // Guarded for the analyser: these two calls are Unix-only, and this
            // whole fallback is a Unix path. On anything else the directory is
            // used as-is, which is what the previous behaviour was everywhere.
            if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            {
                Directory.CreateDirectory(fallback);
                return fallback;
            }

            if (!Directory.Exists(fallback))
                Directory.CreateDirectory(fallback, Private);

            UnixFileMode mode = File.GetUnixFileMode(fallback);
            if (mode != Private)
                throw new IOException(
                    $"{fallback} has mode {mode} rather than 0700 and may belong to another user. " +
                    "Refusing to put the panel lock there. Set XDG_RUNTIME_DIR, or remove that directory.");

            return fallback;
        }
    }
    private static string LockPath  => Path.Combine(Dir, "nexus-manager.lock");
    private static string OwnerPath => Path.Combine(Dir, "nexus-manager.owner");
    private static string SockPath  => Path.Combine(Dir, "nexus-manager.sock");

    public static SingleInstance? TryAcquire(string role)
    {
        string lockPath = LockPath;      // may throw; a configuration fault, not contention
        string ownerPath = OwnerPath;

        try
        {
            var held = new FileStream(lockPath, FileMode.OpenOrCreate,
                                      FileAccess.ReadWrite, FileShare.None);
            try
            {
                File.WriteAllText(ownerPath, $"{Environment.ProcessId}\n{role}\n");
            }
            catch (Exception) { /* the description is a courtesy, not the lock */ }
            return new SingleInstance(held);
        }
        catch (IOException)
        {
            return null;   // someone else holds it
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
    /// <summary>Whoever currently owns the panel, for a message to the user.</summary>
    public static string DescribeHolder()
    {
        try
        {
            var lines = File.ReadAllLines(OwnerPath);
            if (lines.Length >= 2) return $"{lines[1].Trim()} (pid {lines[0].Trim()})";
            if (lines.Length == 1) return $"pid {lines[0].Trim()}";
        }
        catch (Exception) { }
        return "another instance";
    }

    /// <summary>
    /// Asks the running instance to do something - in practice, to show its
    /// window. Returns false if nothing was listening, which is the normal case
    /// when the holder is the headless daemon.
    /// </summary>
    public static bool Signal(string message)
    {
        try
        {
            using var s = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            // A bound socket whose owner has stopped accepting still lets
            // Connect and Send succeed, so "sent" is not evidence of anything.
            // Wait for the holder to acknowledge that it acted.
            s.ReceiveTimeout = 2000;
            s.SendTimeout = 2000;
            s.Connect(new UnixDomainSocketEndPoint(SockPath));
            s.Send(Encoding.UTF8.GetBytes(message));
            var ack = new byte[8];
            int n = s.Receive(ack);
            return n > 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Starts accepting messages from later launches. Optional: the
    /// headless daemon has no window to show and does not call this.</summary>
    public void StartListening()
    {
        try
        {
            // Safe to remove: this process holds the lock, so any socket file here
            // is a leftover from an instance that is already gone.
            try { File.Delete(SockPath); } catch (Exception) { }

            _listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            _listener.Bind(new UnixDomainSocketEndPoint(SockPath));
            _listener.Listen(4);
            _cts = new CancellationTokenSource();
            _ = Task.Run(() => AcceptLoopAsync(_cts.Token));
        }
        catch (Exception ex)
        {
            // Losing the "raise the window" channel is a degradation, not a
            // failure: the lock still prevents the two-writer flicker.
            Console.Error.WriteLine($"[instance] not listening: {ex.Message}");
        }
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[256];
        while (!ct.IsCancellationRequested && _listener is not null)
        {
            try
            {
                using var client = await _listener.AcceptAsync(ct).ConfigureAwait(false);
                int n = await client.ReceiveAsync(buffer, SocketFlags.None, ct).ConfigureAwait(false);
                if (n > 0)
                {
                    MessageReceived?.Invoke(Encoding.UTF8.GetString(buffer, 0, n).Trim());
                    // Acknowledge, so the other process can tell the difference
                    // between "handled" and "nobody was listening".
                    try { client.Send("ok"u8.ToArray()); } catch (Exception) { }
                }
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[instance] accept failed: {ex.Message}");
                break;
            }
        }
    }

    public void Dispose()
    {
        try { _cts?.Cancel(); } catch (Exception) { }
        try { _listener?.Dispose(); } catch (Exception) { }
        try { File.Delete(SockPath); } catch (Exception) { }
        try { File.Delete(OwnerPath); } catch (Exception) { }
        // Last: releasing the lock is what lets the next launch start, so nothing
        // that could throw should come after it.
        try { _lock.Dispose(); } catch (Exception) { }
        _cts?.Dispose();
    }
}

using System.Runtime.InteropServices;

namespace NexusManager.Actions;

/// <summary>
/// A virtual keyboard on /dev/uinput, so a touch button can press a real key.
///
/// This is iCUE's macro button, and on Wayland there is no other way: Wayland
/// has no XTEST, so nothing can inject into another application's input queue
/// from user space. ckb-next solves the same problem the same way.
///
/// Access is by ACL, not by group: systemd's uaccess tags /dev/uinput for the
/// active local seat. ⛔ If a distribution needs a rule of its own it must sort
/// BELOW 73 - a rule numbered 99 applies the tag after the consumer has already
/// run and grants nothing while looking correct.
///
/// The device is created once, lazily, and kept: creating one per keypress would
/// cost a udev round trip each time, and the first events after creation are
/// dropped while udev is still settling.
/// </summary>
public sealed class UinputKeyboard : IDisposable
{
    private const int O_WRONLY = 0x001;
    private const int O_NONBLOCK = 0x800;

    // linux/uinput.h. _IO('U', 1) and _IO('U', 2); _IOW('U', 100|101, int).
    private const ulong UI_DEV_CREATE  = 0x5501;
    private const ulong UI_DEV_DESTROY = 0x5502;
    private const ulong UI_SET_EVBIT   = 0x40045564;
    private const ulong UI_SET_KEYBIT  = 0x40045565;

    private const ushort EV_SYN = 0x00;
    private const ushort EV_KEY = 0x01;
    private const ushort SYN_REPORT = 0x00;

    [DllImport("libc", SetLastError = true)]
    private static extern int open(string path, int flags);

    [DllImport("libc", SetLastError = true)]
    private static extern int close(int fd);

    [DllImport("libc", SetLastError = true, EntryPoint = "ioctl")]
    private static extern int ioctl(int fd, ulong request, ulong value);

    [DllImport("libc", SetLastError = true, EntryPoint = "write")]
    private static extern nint write(int fd, byte[] buffer, nint count);

    private int _fd = -1;
    private readonly Lock _gate = new();

    /// <summary>Resolves a key name, or null if it is not one we know.</summary>
    public static ushort? Resolve(string key) =>
        KeyCodes.TryGetValue(key.Trim(), out ushort c) ? c : null;

    /// <summary>
    /// Sends one key event without its partner, so a macro can hold a modifier
    /// across other keys.
    ///
    /// ⛔ Whoever presses a key with this OWNS releasing it. A held key is real
    /// at the kernel level: if the caller stops early without releasing, the
    /// key stays down for every application on the machine until something
    /// else sends the release. See ReleaseAll.
    /// </summary>
    public string? Send(ushort code, bool down)
    {
        lock (_gate)
        {
            string? err = EnsureDevice();
            if (err is not null) return err;
            err = Emit(EV_KEY, code, down ? 1 : 0);
            return err ?? Emit(EV_SYN, SYN_REPORT, 0);
        }
    }

    /// <summary>
    /// Releases every key given, ignoring failures.
    ///
    /// This is the cleanup that stops a cancelled macro leaving Ctrl or Shift
    /// stuck down across the whole desktop - which is not a cosmetic fault, it
    /// makes the machine unusable until it is noticed and undone.
    /// </summary>
    public void ReleaseAll(IEnumerable<ushort> codes)
    {
        lock (_gate)
        {
            if (_fd < 0) return;
            foreach (ushort c in codes) Emit(EV_KEY, c, 0);
            Emit(EV_SYN, SYN_REPORT, 0);
        }
    }

    /// <summary>Presses and releases. Returns null on success, or why not.</summary>
    public string? Press(string combination)
    {
        var codes = new List<ushort>();
        foreach (string part in combination.Split('+', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!KeyCodes.TryGetValue(part.Trim(), out ushort code))
                return $"unknown key '{part.Trim()}'";
            codes.Add(code);
        }
        if (codes.Count == 0) return "no keys given";

        lock (_gate)
        {
            string? err = EnsureDevice();
            if (err is not null) return err;

            // Modifiers down in order, the final key, then everything up in
            // reverse - the same order a real keyboard produces for a chord.
            string? fail = null;
            foreach (ushort c in codes) fail ??= Emit(EV_KEY, c, 1);
            fail ??= Emit(EV_SYN, SYN_REPORT, 0);
            for (int i = codes.Count - 1; i >= 0; i--) fail ??= Emit(EV_KEY, codes[i], 0);
            fail ??= Emit(EV_SYN, SYN_REPORT, 0);
            if (fail is not null) return fail;
        }
        return null;
    }

    private string? EnsureDevice()
    {
        if (_fd >= 0) return null;

        int fd = open("/dev/uinput", O_WRONLY | O_NONBLOCK);
        if (fd < 0)
        {
            int err = Marshal.GetLastWin32Error();
            return err == 13
                ? "no permission for /dev/uinput (needs a uaccess udev rule numbered below 73)"
                : $"cannot open /dev/uinput (errno {err})";
        }

        if (ioctl(fd, UI_SET_EVBIT, EV_KEY) < 0)
        {
            close(fd);
            return "UI_SET_EVBIT failed";
        }
        // Enable every key we know how to name. Enabling only the ones a config
        // currently uses would mean re-creating the device whenever it changes.
        foreach (ushort code in KeyCodes.Values.Distinct())
            ioctl(fd, UI_SET_KEYBIT, code);

        // Legacy creation path: write a uinput_user_dev, then UI_DEV_CREATE.
        // Preferred over UI_DEV_SETUP here because it needs no ioctl request
        // number computed from a struct size, which is the part that breaks
        // silently across architectures.
        var dev = new byte[1116];
        byte[] name = System.Text.Encoding.ASCII.GetBytes("Nexus Manager Virtual Keyboard");
        Array.Copy(name, dev, Math.Min(name.Length, 79));
        // struct input_id { bustype, vendor, product, version } at offset 80.
        BitConverter.GetBytes((ushort)0x03).CopyTo(dev, 80);   // BUS_USB
        BitConverter.GetBytes((ushort)0x1b1c).CopyTo(dev, 82); // Corsair, for recognisability
        BitConverter.GetBytes((ushort)0x1b8e).CopyTo(dev, 84);
        BitConverter.GetBytes((ushort)0x0001).CopyTo(dev, 86);

        if (write(fd, dev, dev.Length) != dev.Length)
        {
            close(fd);
            return "writing the uinput device description failed";
        }
        if (ioctl(fd, UI_DEV_CREATE, 0) < 0)
        {
            close(fd);
            return "UI_DEV_CREATE failed";
        }

        // udev has to see the new device and apply its rules before events from
        // it reach anyone. Without this pause the first keypress is silently lost.
        Thread.Sleep(200);
        _fd = fd;
        return null;
    }

    /// <summary>Returns null on success, or why the write failed. The first
    /// version ignored the result, so a keyboard that emitted NOTHING reported
    /// success and looked identical to one that worked.</summary>
    private string? Emit(ushort type, ushort code, int value)
    {
        // struct input_event on 64-bit: struct timeval (two 64-bit words),
        // __u16 type, __u16 code, __s32 value = 24 bytes. The kernel fills the
        // timestamp when it is left zero.
        var ev = new byte[24];
        BitConverter.GetBytes(type).CopyTo(ev, 16);
        BitConverter.GetBytes(code).CopyTo(ev, 18);
        BitConverter.GetBytes(value).CopyTo(ev, 20);
        nint written = write(_fd, ev, ev.Length);
        if (written == ev.Length) return null;
        int errno = Marshal.GetLastWin32Error();
        return $"write returned {written} of {ev.Length} (errno {errno})";
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_fd < 0) return;
            ioctl(_fd, UI_DEV_DESTROY, 0);
            close(_fd);
            _fd = -1;
        }
    }

    /// <summary>
    /// Names to Linux key codes (linux/input-event-codes.h). Names are the ones a
    /// person would type, not the kernel's KEY_ spelling.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, ushort> KeyCodes =
        BuildKeyCodes();

    private static Dictionary<string, ushort> BuildKeyCodes()
    {
        var map = new Dictionary<string, ushort>(StringComparer.OrdinalIgnoreCase)
        {
            ["esc"] = 1, ["escape"] = 1,
            ["minus"] = 12, ["equal"] = 13, ["backspace"] = 14, ["tab"] = 15,
            ["["] = 26, ["]"] = 27, ["enter"] = 28, ["return"] = 28,
            ["ctrl"] = 29, ["leftctrl"] = 29, ["rightctrl"] = 97,
            [";"] = 39, ["'"] = 40, ["`"] = 41,
            ["shift"] = 42, ["leftshift"] = 42, ["rightshift"] = 54,
            ["\\"] = 43, [","] = 51, ["."] = 52, ["/"] = 53,
            ["alt"] = 56, ["leftalt"] = 56, ["rightalt"] = 100, ["altgr"] = 100,
            ["space"] = 57, ["capslock"] = 58,
            ["numlock"] = 69, ["scrolllock"] = 70,
            ["home"] = 102, ["up"] = 103, ["pageup"] = 104,
            ["left"] = 105, ["right"] = 106, ["end"] = 107,
            ["down"] = 108, ["pagedown"] = 109, ["insert"] = 110, ["delete"] = 111,
            ["mute"] = 113, ["volumedown"] = 114, ["volumeup"] = 115,
            ["meta"] = 125, ["super"] = 125, ["win"] = 125, ["rightmeta"] = 126,
            ["printscreen"] = 99, ["sysrq"] = 99, ["menu"] = 127,
            ["nextsong"] = 163, ["playpause"] = 164, ["previoussong"] = 165, ["stop"] = 166,
        };

        // a-z run from KEY_A onwards but NOT alphabetically - the codes follow the
        // physical qwerty rows, so they have to be listed rather than computed.
        const string qwerty = "qwertyuiop";
        for (int i = 0; i < qwerty.Length; i++) map[qwerty[i].ToString()] = (ushort)(16 + i);
        const string asdf = "asdfghjkl";
        for (int i = 0; i < asdf.Length; i++) map[asdf[i].ToString()] = (ushort)(30 + i);
        const string zxcv = "zxcvbnm";
        for (int i = 0; i < zxcv.Length; i++) map[zxcv[i].ToString()] = (ushort)(44 + i);

        // Digit row: 1..9 are 2..10 and 0 is 11.
        for (int d = 1; d <= 9; d++) map[d.ToString()] = (ushort)(1 + d);
        map["0"] = 11;

        for (int f = 1; f <= 10; f++) map[$"f{f}"] = (ushort)(58 + f);
        map["f11"] = 87; map["f12"] = 88;

        return map;
    }
}

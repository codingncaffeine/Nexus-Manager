using HidSharp;
using HidSharp.Reports;

namespace NexusManager.Device;

/// <summary>
/// Corsair iCUE NEXUS (1b1c:1b8e). Protocol characterised against real hardware
/// 2026-09-07; see _docs/PROTOCOL.md. Measured ceiling is 65 fps.
/// </summary>
public sealed class NexusDevice : IDisposable
{
    public const int VendorId  = 0x1b1c;
    public const int ProductId = 0x1b8e;

    public const int Width  = 640;
    public const int Height = 48;
    public const int BytesPerPixel = 4;                       // BGRA; byte 3 ignored by the panel
    public const int FrameBytes = Width * Height * BytesPerPixel;   // 122880

    private const int PacketBytes = 1024;
    private const int HeaderBytes = 8;
    private const int ChunkBytes  = PacketBytes - HeaderBytes;      // 1016
    private const int PacketCount = 121;                            // 120*1016 + 960

    /// <summary>Usage 0x000C0001 — the vendor control interface (iface 0).</summary>
    private const uint ControlUsage = 0x000C0001;

    private readonly HidStream _stream;
    private readonly byte[] _packet = new byte[PacketBytes];

    private NexusDevice(HidStream stream) => _stream = stream;

    /// <summary>
    /// Opens the control interface. Selects by USAGE, never by enumeration order:
    /// the device also exposes a boot-keyboard interface, and writing frame data
    /// at that descriptor is the failure mode this guards against (D7).
    /// </summary>
    /// <summary>
    /// A SECOND stream on the same device, for touch.
    ///
    /// Gestures are read on their own stream so a 104 Hz input poll never waits
    /// behind the 121 output writes each frame costs. Measured to coexist fine.
    /// </summary>
    public static HidStream OpenTouchStream()
    {
        foreach (var d in DeviceList.Local.GetHidDevices(VendorId, ProductId))
            if (d.GetMaxOutputReportLength() >= 1024 && d.TryOpen(out var s))
                return s;
        throw new InvalidOperationException(
            "Could not open the NEXUS control interface for touch.");
    }

    public static NexusDevice Open()
    {
        var candidates = DeviceList.Local.GetHidDevices(VendorId, ProductId).ToList();
        if (candidates.Count == 0)
            throw new InvalidOperationException(
                $"No NEXUS found ({VendorId:x4}:{ProductId:x4}). Device unplugged?");

        HidDevice? control = null;
        foreach (var dev in candidates)
        {
            if (!DeclaresControlUsage(dev)) continue;
            if (dev.GetMaxOutputReportLength() < PacketBytes) continue;
            control = dev;
            break;
        }

        if (control is null)
            throw new InvalidOperationException(
                "NEXUS present but its control interface (usage 0x000C0001, 1024-byte " +
                "output reports) was not found. The other interface is a boot keyboard " +
                "and must not be written to.");

        if (!control.TryOpen(out var stream))
            throw new UnauthorizedAccessException(
                $"Cannot open {control.DevicePath}. Install a udev rule tagging uaccess, " +
                "numbered BELOW 73 — 73-seat-late.rules is what turns the tag into an ACL, " +
                "so a 99- rule applies the tag and grants nothing.");

        return new NexusDevice(stream);
    }

    private static bool DeclaresControlUsage(HidDevice dev)
    {
        try
        {
            ReportDescriptor rd = dev.GetReportDescriptor();
            foreach (var item in rd.DeviceItems)
                foreach (uint usage in item.Usages.GetAllValues())
                    if (usage == ControlUsage) return true;
        }
        catch (Exception)
        {
            // A descriptor we cannot parse is not the control interface we want.
        }
        return false;
    }

    // ---- commands: every write goes through feature report 3 -------------------

    /// <summary>Backlight, 0-100. 0 is fully off.</summary>
    public void SetBrightness(int percent)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(percent);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(percent, 100);
        SendCommand(0x01, (byte)percent);
    }

    public void Blank() => SendCommand(0x04);

    /// <summary>Plays one of the three animations baked into the firmware.</summary>
    public void PlayAnimation(int animation, bool loop)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(animation, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(animation, 3);
        SendCommand(0x0D, (byte)animation, (byte)(loop ? 1 : 0));
    }

    public void StopAnimation() => SendCommand(0x0F);

    private void SendCommand(params byte[] args)
    {
        var report = new byte[32];
        report[0] = 3;
        args.CopyTo(report, 1);
        _stream.SetFeature(report);
    }

    /// <summary>
    /// HidSharp hands back [requestedReportId][the device report, which itself
    /// starts with its own report id]. That is ONE BYTE MORE than hidapi returns,
    /// so every documented offset in _docs/PROTOCOL.md (which was captured with
    /// hidapi) must be read relative to this.
    /// </summary>
    private const int ReportStart = 1;

    /// <summary>Fails loudly if the buffer layout is not what we expect, rather
    /// than silently decoding garbage from the wrong offset.</summary>
    private static void VerifyEcho(byte[] buf, byte expectedId)
    {
        if (buf[ReportStart] != expectedId)
            throw new InvalidOperationException(
                $"Feature report layout unexpected: wanted id 0x{expectedId:x2} at " +
                $"offset {ReportStart}, found 0x{buf[ReportStart]:x2}. The HID backend " +
                "may have changed how it returns feature reports.");
    }

    /// <summary>Raw feature report, for protocol work. Returns the buffer as the
    /// HID stack hands it back, including whatever leading bytes it uses.</summary>
    public byte[] ReadFeatureRaw(int reportId)
    {
        var buf = new byte[32];
        buf[0] = (byte)reportId;
        _stream.GetFeature(buf);
        return buf;
    }

    /// <summary>Firmware version string, from feature report 5 (ASCII at offset 6).</summary>
    public string ReadFirmware()
    {
        var buf = new byte[32];
        buf[0] = 5;
        _stream.GetFeature(buf);
        VerifyEcho(buf, 5);
        int end = ReportStart + 6;
        while (end < buf.Length && buf[end] != 0) end++;
        return System.Text.Encoding.ASCII.GetString(
            buf, ReportStart + 6, end - (ReportStart + 6)).Trim();
    }

    /// <summary>
    /// Geometry as the device reports it (feature report 9), rather than hardcoded.
    /// </summary>
    public (int Width, int Height, int BytesPerPixel) ReadGeometry()
    {
        var buf = new byte[32];
        buf[0] = 9;
        _stream.GetFeature(buf);
        VerifyEcho(buf, 9);
        int w = buf[ReportStart + 7] | (buf[ReportStart + 8] << 8);
        int h = buf[ReportStart + 9] | (buf[ReportStart + 10] << 8);
        return (w, h, buf[ReportStart + 11]);
    }

    // ---- frame upload ----------------------------------------------------------

    /// <summary>
    /// Uploads one full 640x48 BGRA frame as 121 output reports. The panel latches
    /// and displays it once the final block arrives.
    /// </summary>
    public void PushFrame(ReadOnlySpan<byte> bgra)
    {
        if (bgra.Length != FrameBytes)
            throw new ArgumentException(
                $"Frame must be exactly {FrameBytes} bytes ({Width}x{Height} BGRA), got {bgra.Length}.",
                nameof(bgra));

        int offset = 0;
        for (int block = 0; block < PacketCount; block++)
        {
            int len = Math.Min(ChunkBytes, FrameBytes - offset);

            _packet[0] = 0x02;                      // report id / endpoint
            _packet[1] = 0x05;                      // command
            _packet[2] = 0x40;                      // don't-care; a third impl sends 0x1F
            _packet[3] = (byte)(offset + len >= FrameBytes ? 1 : 0);
            _packet[4] = (byte)(block & 0xff);
            _packet[5] = (byte)((block >> 8) & 0xff);
            _packet[6] = (byte)(len & 0xff);
            _packet[7] = (byte)((len >> 8) & 0xff);

            bgra.Slice(offset, len).CopyTo(_packet.AsSpan(HeaderBytes));
            if (len < ChunkBytes)
                _packet.AsSpan(HeaderBytes + len, ChunkBytes - len).Clear();

            _stream.Write(_packet);
            offset += len;
        }
    }

    public void Dispose() => _stream.Dispose();
}

# iCUE NEXUS (1b1c:1b8e) — protocol

Verified against the device itself 2026-09-07, firmware 2.2.6.0. Everything
below was obtained WITHOUT a VM or USB capture.

## Interfaces

    iface 0   usage page 0x0C, usage 0x01   /dev/hidraw2   <- the only one that matters
    iface 1   usage page 0x01, usage 0x06   /dev/hidraw3   boot keyboard, ignore

Select by usage page, never by hid_open(vid,pid) enumeration order (D7).

Permissions: a udev rule tagging uaccess MUST sort before 73-seat-late.rules.
`70-icue-nexus.rules` works; a 99- rule applies the tag and grants nothing.

## Reports declared by the descriptor

    ID 2    Output   1023 B    image upload
    ID 1    Input     511 B    touch
    ID 3..0x0E Feature  31 B   commands (write) and device info (read)

## Commands — written to feature report 3

    03 01 <0-100>          brightness, 0 = off
    03 04                  blank
    03 0D <1-3> <loop>     play built-in firmware animation
    03 0F                  stop animation
    03 10 01               seen at iCUE startup, purpose unknown

### The three firmware animations — IDENTIFIED ON HARDWARE 2026-09-08

Played with `nexus-manager anim <n> --loop` and looked at:

    1   the Corsair idle animation — what the device shows on its own when no
        host software is driving it, and what Windows users see when iCUE is
        not running. THIS is the one to hand the panel back to on exit.
    2   a second, equally presentable animation. A fine alternative.
    3   a download arrow over the same background. Almost certainly the
        firmware-update indicator, so NOT an idle choice.

They live in firmware and keep playing after the host process exits, which is
why an unplug/replug with nothing installed shows animation 1.

⛔ **Blanking on exit actively suppressed this.** The device had a resting state
of its own and nothing in our code knew it, so `blank` + brightness 0 left a
dead black strip that persisted until a power cycle. `NexusDevice.HandBack`
restores it instead. ⛔ Raise brightness BEFORE starting an animation: our own
shutdown used to end at 0, and an animation played against that is invisible,
which reads as a dead command rather than a dark backlight.

⛔ No evidence of WRITABLE onboard storage. These three are baked in; the image
path (output report 2) is a live push, not a store, and the 256-screen figure
on Corsair's product page is iCUE-side storage. Reports 0x0C (=1) and 0x0D (=4)
are still unexplained and are the only remaining candidates.

## Device info — READ from feature reports 4..0x0E

All return 32 bytes. Byte 0 echoes the report ID. Byte 1 is a length for the
string-bearing reports. Confirmed read-only info, NOT hidden command channels —
the write side all goes through report 3.

    ID   byte1  payload                          meaning
    ----------------------------------------------------------------------
    0x03  --    zeros                            command channel; reads back empty
    0x04  0x0c  1f dd 83 1c "2.2.6.0 "           firmware version + 4-byte tag
    0x05  0x0c  1f dd 83 1c "2.2.6.0 "           IDENTICAL to 0x04
    0x06  0x0c  45 dc 91 09 "1.00.002"           second component version
                                                 (bootloader?), different tag
    0x07  0x14  "<serial>"                  serial; matches USB iSerial
    0x08  0x0c  all zeros                        empty 12-byte field
    0x09  --    see below                        DISPLAY GEOMETRY
    0x0a  --    all zeros                        unused
    0x0b  --    all zeros                        unused
    0x0c  0x01  --                               flag, value 1
    0x0d  0x04  --                               value 4
    0x0e  --    zeros, then 42 a4 at offset 5-6  unknown

The 4-byte tag before each version string differs per component (1f dd 83 1c vs
45 dc 91 09), so it is probably a CRC or build id, not a length.

### Report 0x09 — display geometry

    09 01 06 48 00 30 00 80 02 30 00 04 05 00 ...
       |  |  |____| |____| |____| |____| |  |
       |  |    72     48     640    48   4  5

`80 02` = 640 and `30 00` = 48 are unmistakably the panel dimensions, and `04`
is bytes per pixel — the device self-describes its own geometry, so a driver
need not hardcode 640x48x4. `05` matches the image-upload command byte.

UNRESOLVED: the leading `01 06` and the first pair (72, 48). 06 may be the
button count (the .cuescreens format has exactly six slots) but that is a guess.
72 has no obvious meaning: it is not the DPI (640px over 126.2mm is ~129 dpi)
and 640/6 is 106.7, not 72. Do not build on this pair until it is explained.

## Image upload — output report 2

121 writes of exactly 1024 bytes. 8-byte header then 1016 bytes of pixel data.

    byte 0   0x02   report ID
    byte 1   0x05   command
    byte 2   0x40   don't-care (a third impl sends 0x1F and it works)
    byte 3          1 on the final block, else 0
    byte 4-5        block number, little-endian, 0..120
    byte 6-7        payload length, little-endian (0x03F8 = 1016, last 0x03C0 = 960)
    byte 8+         pixel data

    120 blocks x 1016 + 1 block x 960 = 122880 = 640 x 48 x 4

## Touch — input report 1

    01 02 21 .. byte5 = pressed/released, bytes 6-7 = X little-endian, 0..639

No Y axis. Corsair's own documentation confirms the full gesture vocabulary is
"one finger pressing or swiping left-and-right", so this is complete.

## MEASURED throughput — 2026-09-07, this machine

    achieved      65.00 fps sustained over 6.0 s (390 frames)
    per frame     best 15.10 ms, worst 18.37 ms  (54.4 fps floor)
    throughput    7.62 MB/s, 7864 HID writes/s

Against the endpoint-derived ceiling of 8192 writes/s and ~67.7 fps, that is
96% of theoretical — the device is bus-limited, not software-limited, and the
derived budget was accurate to 4%.

At the 24 fps design target (D8) this is 37% of capacity, 2.7x headroom.

CONDITIONS: quiet bus. All nine ALSA streams "Status: Stop", no Bluetooth audio
connected. This is arm A of R8 only. The loaded arm — USB audio streaming plus a
BT audio device — is NOT yet measured, and bus 008 carries 66 isochronous
endpoints of USB audio and 14 of Bluetooth.

## Pixel format — CHARACTERISED ON HARDWARE 2026-09-07

Determined by isolating one byte at a time on the real panel, not inferred.

    byte 0   BLUE
    byte 1   GREEN
    byte 2   RED
    byte 3   IGNORED — setting it alone produces black; alpha does nothing

So the wire format is BGRA / BGRX, confirming ICueNexusPlusPlus. NexusTool's
notes calling it "RGBA32" are loose wording, not a different format.

### Bit depth is really 6, and it is visible

Sweeping one bit at a time through byte 1: bits 0-5 produce nothing the eye can
see; only bits 6 and 7 register, bit 7 brighter than bit 6. That is 8->6 bit
truncation, exactly what a 262K-colour (18-bit) panel does. Confirms D9 —
dithering is required, not optional. A naive 8-bit gradient throws away its
bottom six bits and bands hard.

### The green primary is yellow-shifted — plan the palette around it

A/B on adjacent bands: byte1 alone reads LIME, byte1+byte2 reads YELLOW, byte2
alone reads RED. The three are clearly distinct, which is what proves byte1 is
pure green. But full green renders as LIME, not green.

Consequences, all observed:

    pure green  (0,255,0)  -> lime
    blue+green  (255,255,0)-> reads WHITE, not cyan
    red+green   (0,255,255)-> orange-yellow

This is panel colour rendition, not a protocol problem. It matters anyway: a
status dashboard leans on green-means-good, and sRGB green will not look green
here. Choose widget palettes against MEASURED output on the panel, never against
what the colour looks like on a monitor. See D12.

## Touch — MEASURED 2026-09-07

Captured with `touchmon.c`: four touches (2 taps, 2 slow swipes), timestamped.

### Report rate

    104.5 Hz, dead stable across all four touches
    (104.4 / 104.5 / 104.6 / 104.8 measured separately)
    inter-report gap 8.7-10.3 ms

At 512 bytes per report that is ~53 KB/s — negligible beside the render loop's
7.6 MB/s, so touch and video do not meaningfully compete for bus budget. This
retires the bandwidth half of R2.

### X TRACKS A DRAG — full gesture support is available

    swipe L->R   X 29 -> 611   travel +582   1637 ms   171 reports
    swipe R->L   X 584 -> 25   travel -559   1511 ms   158 reports

Per-report deltas run 1-6 px through the body of a swipe, rising to +20/-12 at
the very end as the finger lifts. So swipes are real gestures with position and
velocity, not just a start and end point. Direction, speed, and mid-drag
feedback (a page sliding under the finger) are all implementable.

Taps show travel of exactly 0 over the whole hold, including a 1770 ms one — so
a stationary finger produces a genuinely stable coordinate, no jitter filtering
needed.


### Release semantics — CORRECTED 2026-09-07 after a fast-flick capture

An earlier note here said "on release, X is ALWAYS 0", based on 4 of 4 touches.
That was wrong: those four were all slow and deliberate, so every release in
them was a genuine lift. A capture containing fast flicks gives 6 of 14
releases with a NON-ZERO X, and the distinction turns out to be meaningful.

    state=0 with X != 0   NOT a release. A transient tracking dropout during
                          fast motion. The finger is still down and the
                          reported X is a VALID position sample.
                          6 of 6 were followed by a fresh DOWN within ~10 ms.
    state=0 with X == 0   A genuine lift-off.
                          8 of 8 had no quick re-DOWN.

Perfect correlation, 14/14, no exceptions.

### A FAST SWIPE ARRIVES AS TWO TOUCHES — this WILL break naive handling

Every one of six fast flicks split into two DOWN/UP pairs:

    DOWN X=530 ... UP X=491   (28 ms)      <- dropout
      gap 10.0 ms
    DOWN X=464 ... UP X=0     (8 ms)       <- genuine lift

    DOWN X=533 ... UP X=483   (29 ms)
      gap 10.0 ms
    DOWN X=453 ... UP X=0     (56 ms, travel -159)

The gap is always ~9-10 ms — exactly one report interval, i.e. a single dropped
report — and X continues monotonically across it. Slow swipes never split.

Handled naively, every fast swipe registers as two short taps or two mini-swipes.
Fast swiping is the natural gesture, so navigation would fail precisely when used
normally, and would test fine when a developer swipes slowly and carefully. See
D13.

### Movement starts immediately — earlier "settle period" theory was WRONG

An earlier note wondered whether X holds still for 150-180 ms after touch-down
because of hardware settling. It does not. Fast flicks report travel in their
first few reports (28 ms / 3 reports / -19 px). The earlier delay was human
reaction time between pressing and starting to move. No device constraint here.

## R8 — bus contention under isochronous load: MEASURED, NOT A PROBLEM

Both arms measured on this machine 2026-09-07, same tool, same frame pattern.

    ARM A  quiet bus    65.00 fps   7.62 MB/s   7864 writes/s   worst 18.37 ms
    ARM B  loaded bus   64.87 fps   7.60 MB/s   7849 writes/s   worst 18.50 ms
    delta              -0.13 fps (-0.2%)                        +0.13 ms

Arm B ran with the ASUS Generic USB Audio (card 4, bus 008 port 11) actively
streaming 48 kHz stereo to its SPDIF sink, verified by `Status: Running` on
/proc/asound/card4/stream3 both BEFORE and AFTER the run, not merely assumed.

The 24 fps design target retains 2.7x headroom even under load.

### Why there was no contention, in numbers

    microframe capacity at 480 Mbps    ~7500 bytes
    periodic transfers may claim       80% -> ~6000 bytes
    our render loop reserves           1024 bytes per microframe
    48 kHz stereo s16 audio needs      ~24 bytes per microframe

Combined periodic demand is roughly 1050 of ~6000 available bytes. There was
never a shortage to fight over, and the measurement agrees with the arithmetic.

### What was NOT tested

Bluetooth SCO. The phone connected over BT (adapter is on bus 008) but only
idled — no voice call was placed, so the radio's 14 isochronous endpoints stayed
inactive. Note that A2DP music would not have tested this either: A2DP rides ACL
which maps to USB BULK endpoints, and bulk claims no periodic reservation at all.
Only SCO/eSCO voice uses the isochronous endpoints.

Given a 0.2% impact from real isochronous audio and ~5000 bytes per microframe
still unclaimed, SCO is very unlikely to matter. Left untested deliberately
rather than silently.

## Implementation note: HID backends disagree about feature-report layout

Caught 2026-09-07 while porting to C#/HidSharp. Every offset documented above
was captured with hidapi (the C tool), and HidSharp returns ONE BYTE MORE:

    hidapi   :    09 01 06 48 00 30 00 80 02 30 00 04 05
    HidSharp : 09 09 01 06 48 00 30 00 80 02 30 00 04 05
               ^^ requested report id, prepended

So HidSharp hands back [requestedReportId][the device report, which itself
begins with its own id]. Read every offset in this document relative to a
+1 shift on that backend.

⛔ This produced a bug that LOOKED like it passed. The firmware read at the
hidapi offset picked up a leading 0x1C separator byte and still printed
"2.2.6.0", because .NET's string.Trim() treats 0x1C-0x1F as whitespace. Only
the geometry read — which decodes numbers, not text — exposed the shift, coming
back as 32768x12290x0. A text field can hide an offset error indefinitely;
a numeric one cannot.

Guard against it rather than trusting the layout: assert that the byte at the
data offset echoes the report id you asked for, and throw if it does not.

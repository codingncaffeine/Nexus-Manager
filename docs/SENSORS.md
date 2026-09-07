# Sensor layer

Built 2026-09-07. Discovery is the point: rather than a hand-written list, every
source enumerates what the machine actually reports. On this box that is 70
sensors across 7 devices, including several no curated list would have included.

## Presentation follows iCUE

From Corsair's "Add Home Sensors" dialog: sensors are grouped BY DEVICE, each
group showing a friendly device name and a category — "AMD RYZEN 9 9950X3D /
Processor", "NVIDIA GEFORCE RTX 5080 / GPU", "VENGEANCE DDR5 / DRAM". A flat list
of driver names (k10temp, spd5118, amdgpu) is useless for picking from.

Colour encodes the sensor TYPE, not an alarm state — temperature orange, load
green, voltage violet — so the palette says what you are looking at before you
read the number. Alarm thresholds are opt-in per module and null by default.

## Sources

    CpuLoadSource   /proc/stat        total + per-core utilisation
    MemorySource    /proc/meminfo     RAM used %, used GB, free GB, swap
    HwmonSource     /sys/class/hwmon  temp, fan, voltage, power, current, freq
    NetworkSource   /sys/class/net    per-interface throughput from byte counters
    NvidiaSource    nvidia-smi        temp, load, VRAM, power, clocks, fan

## Naming rules that matter

⛔ hwmonN indices are NOT stable across boots or module load order. Everything
resolves by the driver's own `name` file, never by index.

⛔ The DRIVER is not the DEVICE. Four NVMe drives all report driver "nvme"; using
the driver as the device identity collapses them into one useless entry. Each
hwmon instance resolves a specific identity:

    nvme, drivetemp   model string from device/model, suffixed with the kernel
                      node when several identical drives are fitted
                      -> "SAMSUNG SSD 990 PRO 4TB (nvme0)"
    spd5118           i2c address, so two identical DIMMs stay distinct
                      -> "Memory Module 8-0051" / "Memory Module 8-0053"
    everything else   friendly name from DeviceCatalog
                      -> k10temp becomes the CPU model from /proc/cpuinfo,
                         amdgpu becomes "AMD Radeon Graphics"

Each source declares its own DeviceCategory. Inferring it from a driver string
left the NVIDIA GPU categorised "Unknown", because its device string is already
a product name and never passes through the driver table.

Duplicate labels within one hwmon device get a "#2" suffix, since a device can
legitimately expose the same label twice.

## Units

Sensors store their NATIVE unit and are converted only for display, so switching
scale is never lossy. TemperatureScale covers Celsius, Fahrenheit and Kelvin.
Chart bounds convert with the reading — a 20-95 range in Celsius becomes 68-203
in Fahrenheit rather than silently staying wrong.

Verified: a static 30.00 °C GPU reads exactly 86.00 °F.

## Known gap

No fan RPM on this machine. hwmon7 (asus) exposes no fan*_input and nct6775 is
not loaded, so the Fan sensor kind and its icon are implemented but nothing
currently feeds them here. Unresolved — see DESIGN.md R5.

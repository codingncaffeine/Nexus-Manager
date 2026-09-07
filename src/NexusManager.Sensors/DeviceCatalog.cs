namespace NexusManager.Sensors;

/// <summary>Broad grouping shown beside a device name, as iCUE does
/// ("Processor", "GPU", "Motherboard", "DRAM").</summary>
public enum DeviceCategory
{
    Unknown, Processor, Gpu, Motherboard, Dram, Storage, Network, Controller, Power, System,
}

public sealed record DeviceInfo(string Id, string Name, DeviceCategory Category);

/// <summary>
/// Turns kernel driver names into something a person recognises. A raw hwmon
/// sweep yields "k10temp" and "spd5118"; nobody browsing a sensor list wants to
/// pick from those. Where the real model name is discoverable we use it.
/// </summary>
public static class DeviceCatalog
{
    public static DeviceInfo Resolve(string driver)
    {
        string d = driver.ToLowerInvariant();

        if (d is "k10temp" or "zenpower")
            return new DeviceInfo(driver, CpuModel() ?? "AMD Processor", DeviceCategory.Processor);
        if (d is "coretemp")
            return new DeviceInfo(driver, CpuModel() ?? "Intel Processor", DeviceCategory.Processor);
        if (d.StartsWith("amdgpu", StringComparison.Ordinal))
            return new DeviceInfo(driver, "AMD Radeon Graphics", DeviceCategory.Gpu);
        if (d.StartsWith("nouveau", StringComparison.Ordinal))
            return new DeviceInfo(driver, "NVIDIA Graphics", DeviceCategory.Gpu);
        if (d is "nvme")
            return new DeviceInfo(driver, "NVMe Solid State Drive", DeviceCategory.Storage);
        if (d.StartsWith("drivetemp", StringComparison.Ordinal))
            return new DeviceInfo(driver, "Drive", DeviceCategory.Storage);
        if (d.StartsWith("spd", StringComparison.Ordinal))
            return new DeviceInfo(driver, "System Memory Module", DeviceCategory.Dram);
        if (d.StartsWith("mt79", StringComparison.Ordinal) || d.Contains("wifi", StringComparison.Ordinal) ||
            d.StartsWith("iwlwifi", StringComparison.Ordinal))
            return new DeviceInfo(driver, "Wi-Fi Adapter", DeviceCategory.Network);
        if (d.StartsWith("r8169", StringComparison.Ordinal) || d.StartsWith("igb", StringComparison.Ordinal) ||
            d.StartsWith("e1000", StringComparison.Ordinal))
            return new DeviceInfo(driver, "Ethernet Adapter", DeviceCategory.Network);
        if (d is "asus" or "asusec" or "nct6775" or "nct6683" or "it87" or "gigabyte_wmi")
            return new DeviceInfo(driver, MotherboardModel() ?? "Motherboard", DeviceCategory.Motherboard);
        if (d.Contains("xhci", StringComparison.Ordinal))
            return new DeviceInfo(driver, "USB Controller", DeviceCategory.Controller);
        if (d.Contains("bat", StringComparison.Ordinal) || d.Contains("acpi", StringComparison.Ordinal))
            return new DeviceInfo(driver, "Power", DeviceCategory.Power);

        return new DeviceInfo(driver, driver, DeviceCategory.Unknown);
    }

    /// <summary>Board vendor and model from DMI, e.g. "ASUSTeK ROG STRIX X670E-E".</summary>
    public static string? MotherboardModel()
    {
        string? vendor = Read("/sys/class/dmi/id/board_vendor");
        string? name   = Read("/sys/class/dmi/id/board_name");
        if (name is null) return vendor;
        return vendor is null ? name : $"{vendor} {name}";
    }

    public static string? CpuModel()
    {
        try
        {
            foreach (string line in File.ReadLines("/proc/cpuinfo"))
                if (line.StartsWith("model name", StringComparison.Ordinal))
                {
                    int i = line.IndexOf(':');
                    if (i >= 0) return line[(i + 1)..].Trim();
                }
        }
        catch (IOException) { }
        return null;
    }

    private static string? Read(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            string t = File.ReadAllText(path).Trim();
            return t.Length == 0 ? null : t;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }
}

public static class CategoryNames
{
    /// <summary>Display form, matching how iCUE labels its groups.</summary>
    public static string Display(DeviceCategory c) => c switch
    {
        DeviceCategory.Processor   => "Processor",
        DeviceCategory.Gpu         => "GPU",
        DeviceCategory.Motherboard => "Motherboard",
        DeviceCategory.Dram        => "DRAM",
        DeviceCategory.Storage     => "Storage",
        DeviceCategory.Network     => "Network",
        DeviceCategory.Controller  => "Controller",
        DeviceCategory.Power       => "Power",
        DeviceCategory.System      => "System Info",
        _                          => "Other",
    };
}

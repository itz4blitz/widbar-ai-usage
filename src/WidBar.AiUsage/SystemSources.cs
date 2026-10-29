using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WidBar.AiUsage;

public sealed record Sensor(string Id, string Name, string Hardware, double Value, string Unit);

public sealed record GpuInfo(string Name, string ShortName, double? Load, double? Temp, double? HotSpot,
    double? VramUsedMb, double? VramTotalMb, List<FanInfo> Fans, double? Power = null, double? CoreClock = null);

public sealed record FanInfo(string Name, double? Rpm, double? Percent);

public sealed record DiskInfo(string Name, string? Letter, double? Activity, double? UsedPercent, double? FreeGb, double? TotalGb,
    double? ReadRate, double? WriteRate, double? Temp = null, double? Life = null);

public sealed class SystemSnapshot
{
    public double? CpuLoad { get; init; }
    public double? CpuTemp { get; init; }
    public double? CpuCoreMax { get; init; }
    public double? CpuPower { get; init; }
    public double? CpuClock { get; init; }
    public string CpuName { get; init; } = "CPU";
    public List<GpuInfo> Gpus { get; init; } = [];
    public double? RamLoad { get; init; }
    public double? RamUsedGb { get; init; }
    public double? RamAvailableGb { get; init; }
    public List<DiskInfo> Disks { get; init; } = [];
    public List<FanInfo> Fans { get; init; } = [];
    public FanInfo? CpuFan { get; init; }
    public string? Error { get; init; }

    public DiskInfo? SystemDisk => Disks.FirstOrDefault(d => d.Letter == "C");
}

// Everything the system bar shows comes from LibreHardwareMonitor's local web
// server, which reads the hardware through its PawnIO driver.
public static partial class SystemSource
{
    private const string Url = "http://127.0.0.1:8085/data.json";

    public static async Task<SystemSnapshot> FetchAsync(CancellationToken ct)
    {
        string json;
        try
        {
            json = await Http.Client.GetStringAsync(Url, ct);
        }
        catch (HttpRequestException)
        {
            return new SystemSnapshot { Error = "LibreHardwareMonitor is not running" };
        }

        var sensors = new List<Sensor>();
        var hardwareNames = new Dictionary<string, string>();
        using (var doc = JsonDocument.Parse(json))
        {
            Walk(doc.RootElement, [], sensors, hardwareNames);
        }
        return Build(sensors, hardwareNames);
    }

    private static void Walk(JsonElement node, List<string> path, List<Sensor> sensors, Dictionary<string, string> hardwareNames)
    {
        var text = node.TryGetProperty("Text", out var t) ? t.GetString() ?? "" : "";
        if (node.TryGetProperty("SensorId", out var sid) && sid.GetString() is { } id &&
            node.TryGetProperty("Value", out var v) && TryParse(v.GetString(), out var value, out var unit))
        {
            // Path: Sensor / <computer> / <hardware> / ... / <group> / <sensor>
            var hardware = path.Count > 2 ? path[2] : "";
            sensors.Add(new Sensor(id, text, hardware, value, unit));
            var root = HardwareRoot(id);
            if (root is not null && !hardwareNames.ContainsKey(root)) hardwareNames[root] = hardware;
            return;
        }

        if (!node.TryGetProperty("Children", out var children)) return;
        path.Add(text);
        foreach (var child in children.EnumerateArray()) Walk(child, path, sensors, hardwareNames);
        path.RemoveAt(path.Count - 1);
    }

    // "/gpu-nvidia/1/load/0" -> "/gpu-nvidia/1"
    private static string? HardwareRoot(string id)
    {
        var m = HardwareRootRegex().Match(id);
        return m.Success ? m.Value : null;
    }

    private static bool TryParse(string? text, out double value, out string unit)
    {
        value = 0;
        unit = "";
        if (string.IsNullOrWhiteSpace(text)) return false;
        var m = ValueRegex().Match(text);
        if (!m.Success) return false;
        unit = m.Groups[2].Value.Trim();
        return double.TryParse(m.Groups[1].Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private static SystemSnapshot Build(List<Sensor> sensors, Dictionary<string, string> hardwareNames)
    {
        Sensor? Find(Func<Sensor, bool> pred) => sensors.FirstOrDefault(pred);
        double? Val(Func<Sensor, bool> pred) => Find(pred)?.Value;

        static bool IsCpu(Sensor s) => s.Id.StartsWith("/amdcpu/", StringComparison.Ordinal) || s.Id.StartsWith("/intelcpu/", StringComparison.Ordinal);

        var cpuTempNames = new[] { "Core (Tctl/Tdie)", "CPU Package", "Core (Tctl)", "Core Max" };
        var cpuTemp = cpuTempNames
            .Select(n => Find(s => IsCpu(s) && s.Id.Contains("/temperature/") && s.Name == n))
            .FirstOrDefault(s => s is not null);

        // GPUs, in LibreHardwareMonitor's order.
        var gpus = new List<GpuInfo>();
        foreach (var root in hardwareNames.Keys.Where(k => k.StartsWith("/gpu-", StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            bool Of(Sensor s) => s.Id.StartsWith(root + "/", StringComparison.Ordinal);
            var name = hardwareNames[root];
            var fans = sensors.Where(s => Of(s) && s.Id.Contains("/fan/"))
                .Select(f => new FanInfo(f.Name, f.Value,
                    Val(c => Of(c) && c.Id.Contains("/control/") && c.Name == f.Name)))
                .ToList();
            gpus.Add(new GpuInfo(
                name,
                ShortGpuName(name),
                Val(s => Of(s) && s.Id.Contains("/load/") && s.Name == "GPU Core"),
                Val(s => Of(s) && s.Id.Contains("/temperature/") && s.Name == "GPU Core"),
                Val(s => Of(s) && s.Id.Contains("/temperature/") && s.Name == "GPU Hot Spot"),
                Val(s => Of(s) && s.Name == "GPU Memory Used"),
                Val(s => Of(s) && s.Name == "GPU Memory Total"),
                fans,
                Val(s => Of(s) && s.Id.Contains("/power/") && s.Name == "GPU Package"),
                Val(s => Of(s) && s.Id.Contains("/clock/") && s.Name == "GPU Core")));
        }

        // Disks, matched to drive letters by free space (LHM doesn't know letters).
        // Recomputed every poll. A letter is used once, by the closest disk, so an
        // unmounted Linux volume can't keep a Windows letter from an earlier reading.
        var volumes = DriveInfo.GetDrives()
            .Where(d => d.DriveType == DriveType.Fixed && d.IsReady)
            .Select(d => (Letter: d.Name[..1], FreeGb: d.AvailableFreeSpace / 1e9))
            .ToList();
        var roots = hardwareNames.Keys.Where(k => k.StartsWith("/nvme/", StringComparison.Ordinal) ||
                                                   k.StartsWith("/ssd/", StringComparison.Ordinal) ||
                                                   k.StartsWith("/hdd/", StringComparison.Ordinal)).Order(StringComparer.Ordinal).ToList();
        var letters = DiskLetters.Assign(
            roots.Select(root =>
            {
                bool Of(Sensor s) => s.Id.StartsWith(root + "/", StringComparison.Ordinal);
                return (root, Val(s => Of(s) && s.Name == "Free Space") ?? double.NaN, Val(s => Of(s) && s.Name == "Total Space") ?? 0);
            }).ToList(),
            volumes);
        var disks = new List<DiskInfo>();
        foreach (var root in roots)
        {
            bool Of(Sensor s) => s.Id.StartsWith(root + "/", StringComparison.Ordinal);
            var free = Val(s => Of(s) && s.Name == "Free Space");
            letters.TryGetValue(root, out var letter);
            disks.Add(new DiskInfo(
                hardwareNames[root],
                letter,
                Val(s => Of(s) && s.Name == "Total Activity"),
                Val(s => Of(s) && s.Name == "Used Space"),
                free,
                Val(s => Of(s) && s.Name == "Total Space"),
                RateMb(Find(s => Of(s) && s.Name == "Read Rate")),
                RateMb(Find(s => Of(s) && s.Name == "Write Rate")),
                Val(s => Of(s) && s.Id.Contains("/temperature/") && s.Name == "Composite Temperature"),
                Val(s => Of(s) && s.Id.Contains("/level/") && s.Name == "Life")));
        }

        // Motherboard fan headers: RPM plus the duty cycle the fan curve set.
        var boardFans = sensors
            .Where(s => s.Id.StartsWith("/lpc/", StringComparison.Ordinal) && s.Id.Contains("/fan/"))
            .Select(f =>
            {
                var chip = f.Id[..f.Id.IndexOf("/fan/", StringComparison.Ordinal)];
                return new FanInfo(f.Name, f.Value,
                    Val(c => c.Id.StartsWith(chip + "/control/", StringComparison.Ordinal) && c.Name == f.Name));
            })
            .ToList();

        return new SystemSnapshot
        {
            CpuName = hardwareNames.FirstOrDefault(kv => kv.Key.StartsWith("/amdcpu/") || kv.Key.StartsWith("/intelcpu/")).Value ?? "CPU",
            CpuLoad = Val(s => IsCpu(s) && s.Id.Contains("/load/") && s.Name == "CPU Total"),
            CpuCoreMax = Val(s => IsCpu(s) && s.Id.Contains("/load/") && s.Name == "CPU Core Max"),
            CpuTemp = cpuTemp?.Value,
            CpuPower = Val(s => IsCpu(s) && s.Id.Contains("/power/") && s.Name is "Package" or "CPU Package"),
            CpuClock = Val(s => IsCpu(s) && s.Id.Contains("/clock/") && s.Name == "Cores (Average)"),
            Gpus = gpus,
            RamLoad = Val(s => s.Id.StartsWith("/ram/load/", StringComparison.Ordinal) && s.Name == "Memory"),
            RamUsedGb = Val(s => s.Id.StartsWith("/ram/data/", StringComparison.Ordinal) && s.Name == "Memory Used"),
            RamAvailableGb = Val(s => s.Id.StartsWith("/ram/data/", StringComparison.Ordinal) && s.Name == "Memory Available"),
            Disks = disks,
            Fans = boardFans,
            CpuFan = boardFans.FirstOrDefault(f => f.Name == "CPU Fan") ?? boardFans.FirstOrDefault(f => f.Rpm > 0),
        };
    }

    private static double? RateMb(Sensor? s) => s?.Unit switch
    {
        null => null,
        "KB/s" => s.Value / 1024,
        "GB/s" => s.Value * 1024,
        _ => s.Value,
    };

    // "NVIDIA GeForce RTX 3060 Ti" -> "3060 Ti", "AMD Radeon RX 7900 XT" -> "7900 XT"
    private static string ShortGpuName(string name)
    {
        var m = GpuModelRegex().Match(name);
        return m.Success ? m.Value.Trim() : name;
    }

    [GeneratedRegex(@"^/[a-z0-9\-]+/\d+")]
    private static partial Regex HardwareRootRegex();

    [GeneratedRegex(@"^\s*(-?[\d.,]+)\s*(.*)$")]
    private static partial Regex ValueRegex();

    [GeneratedRegex(@"\d{3,4}\w*(\s(Ti|SUPER|Super|XTX|XT|GRE))*")]
    private static partial Regex GpuModelRegex();
}

// Pairs LibreHardwareMonitor disks with Windows volumes. Each letter is given
// to the disk whose free space is closest, and only when it is actually close.
internal static class DiskLetters
{
    public static Dictionary<string, string> Assign(
        IReadOnlyList<(string Id, double FreeGb, double TotalGb)> disks,
        IReadOnlyList<(string Letter, double FreeGb)> volumes)
    {
        var pairs = new List<(double Distance, string Id, string Letter)>();
        foreach (var disk in disks)
        {
            if (double.IsNaN(disk.FreeGb)) continue;
            var threshold = Math.Max(5, disk.TotalGb * 0.02);
            foreach (var volume in volumes)
            {
                var distance = Math.Abs(volume.FreeGb - disk.FreeGb);
                if (distance < threshold) pairs.Add((distance, disk.Id, volume.Letter));
            }
        }

        var assigned = new Dictionary<string, string>();
        var used = new HashSet<string>();
        foreach (var pair in pairs.OrderBy(p => p.Distance).ThenBy(p => p.Id, StringComparer.Ordinal))
        {
            if (assigned.ContainsKey(pair.Id) || !used.Add(pair.Letter)) continue;
            assigned[pair.Id] = pair.Letter;
        }
        return assigned;
    }
}

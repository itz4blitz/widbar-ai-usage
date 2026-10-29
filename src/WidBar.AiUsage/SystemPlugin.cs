using System.Text.Json;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;
using WidBar.SDK;

namespace WidBar.AiUsage;

// CPU, both GPUs, RAM, the system disk and the fans, all from
// LibreHardwareMonitor. Replaces the System Metrics widget.
public sealed class SystemPlugin : WidgetPluginBase, IConfigurableWidgetPlugin, IWidgetFlyoutLifecycle
{
    public const string PluginId = "com.blitz.system";

    private const int TileWidth = 84;
    private const int SeparatorWidth = 9;

    private static readonly Color CpuColor = Color.FromArgb(0xFF, 0x4C, 0xC2, 0xFF);
    private static readonly Color[] GpuColors = [Color.FromArgb(0xFF, 0x6B, 0xCB, 0x6B), Color.FromArgb(0xFF, 0xA8, 0xE0, 0x63)];
    private static readonly Color RamColor = Color.FromArgb(0xFF, 0xD7, 0xA0, 0xE7);
    private static readonly Color DiskColor = Color.FromArgb(0xFF, 0xFF, 0xC8, 0x4C);
    private static readonly Color FanColor = Color.FromArgb(0xFF, 0x6F, 0xE0, 0xE0);

    private Settings _settings = new();
    private SystemSnapshot? _snapshot;
    private DispatcherQueue? _dispatcher;
    private CancellationTokenSource? _cts;
    private bool _flyoutOpen;

    private readonly Dictionary<string, GraphTile> _tiles = [];
    private string _layoutSignature = "";
    private StackPanel? _previewRow;
    private StackPanel? _flyoutPanel;

    public override string Id => PluginId;
    public override string Name => "System";
    public override int FlyoutWidth => 400;
    public override int FlyoutHeight => 660;

    public override int PreviewLogicalWidth
    {
        get
        {
            var n = Math.Max(1, VisibleKeys().Count);
            return n * TileWidth + (n - 1) * SeparatorWidth + 8;
        }
    }

    private sealed class Settings
    {
        public List<string> Hidden { get; set; } = [];
        public string? FanName { get; set; }

        public static Settings FromJson(string? json)
        {
            try
            {
                return string.IsNullOrWhiteSpace(json) ? new Settings() : JsonSerializer.Deserialize<Settings>(json) ?? new Settings();
            }
            catch
            {
                return new Settings();
            }
        }

        public string ToJson() => JsonSerializer.Serialize(this);
    }

    // Tile keys in display order. GPUs come from the hardware actually present.
    private List<string> AllKeys()
    {
        var keys = new List<string> { "cpu" };
        var gpuCount = _snapshot?.Gpus.Count ?? 2;
        for (var i = 0; i < gpuCount; i++) keys.Add($"gpu{i}");
        keys.AddRange(["ram", "disk", "fan"]);
        return keys;
    }

    private List<string> VisibleKeys() => AllKeys().Where(k => !_settings.Hidden.Contains(k)).ToList();

    public override async Task InitializeAsync(IWidgetContext context)
    {
        _settings = Settings.FromJson(context.SettingsJson);
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        await base.InitializeAsync(context);
        _cts = new CancellationTokenSource();
        _ = PollLoopAsync(_cts.Token);
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1.5));
        do
        {
            if (!_flyoutOpen && !(Context?.IsPreviewVisible ?? true)) continue;
            SystemSnapshot snap;
            try
            {
                snap = await SystemSource.FetchAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                snap = new SystemSnapshot { Error = ex.Message };
            }
            _dispatcher?.TryEnqueue(() => Apply(snap));
        }
        while (await WaitAsync(timer, ct));
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try
        {
            return await timer.WaitForNextTickAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private void Apply(SystemSnapshot snap)
    {
        _snapshot = snap;
        RenderPreview();
        if (_flyoutOpen) RenderFlyout();
    }

    // ---- Taskbar preview -------------------------------------------------

    public override UIElement? CreatePreviewContent()
    {
        _dispatcher ??= DispatcherQueue.GetForCurrentThread();
        _tiles.Clear();
        _previewRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _layoutSignature = "";
        RenderPreview();
        return new Grid { Background = null, Children = { _previewRow } };
    }

    private GraphTile TileFor(string key)
    {
        if (_tiles.TryGetValue(key, out var tile)) return tile;
        var (label, color) = key switch
        {
            "cpu" => ("CPU", CpuColor),
            "ram" => ("RAM", RamColor),
            "disk" => ("DISK", DiskColor),
            "fan" => ("FAN", FanColor),
            _ => GpuLabel(key),
        };
        return _tiles[key] = new GraphTile(label, color, TileWidth, key.StartsWith("gpu") ? "gpu" : key);
    }

    private (string, Color) GpuLabel(string key)
    {
        var i = int.Parse(key[3..]);
        var name = _snapshot?.Gpus.ElementAtOrDefault(i)?.ShortName;
        // The NVIDIA icon already says GPU, which leaves room for the full model.
        return (name is null ? $"GPU {i + 1}" : name.ToUpperInvariant(), GpuColors[i % GpuColors.Length]);
    }

    private void RenderPreview()
    {
        if (_previewRow is null) return;

        var visible = VisibleKeys();
        var signature = string.Join(",", visible) + "|" + string.Join(",", _snapshot?.Gpus.Select(g => g.ShortName) ?? []);
        if (signature != _layoutSignature)
        {
            var widthChanged = _layoutSignature.Split('|')[0].Split(',', StringSplitOptions.RemoveEmptyEntries).Length != visible.Count;
            _layoutSignature = signature;
            foreach (var k in _tiles.Keys.Where(k => k.StartsWith("gpu")).ToList()) _tiles.Remove(k); // relabel
            _previewRow.Children.Clear();
            for (var i = 0; i < visible.Count; i++)
            {
                if (i > 0) _previewRow.Children.Add(Separator());
                _previewRow.Children.Add(TileFor(visible[i]).Root);
            }
            if (widthChanged) Context?.RequestPreviewRefresh();
        }

        var s = _snapshot;
        if (s?.Error is not null)
        {
            foreach (var k in visible) TileFor(k).ShowError(s.Error);
            return;
        }
        if (s is null) return;

        foreach (var key in visible)
        {
            var tile = TileFor(key);
            switch (key)
            {
                case "cpu":
                    tile.Show(s.CpuLoad, s.CpuTemp is { } ct ? $"{ct:0}°C" : "",
                        Ui.SeverityColor(s.CpuTemp ?? 0, 80, 90),
                        $"{s.CpuName}\nLoad {s.CpuLoad:0}% · busiest core {s.CpuCoreMax:0}%\nTemperature {s.CpuTemp:0.0} °C");
                    break;
                case "ram":
                    tile.Show(s.RamLoad, s.RamUsedGb is { } used ? $"{used:0}G" : "", null,
                        $"Memory {s.RamLoad:0}%\n{s.RamUsedGb:0.0} GB used · {s.RamAvailableGb:0.0} GB free");
                    break;
                case "disk":
                    var d = s.SystemDisk;
                    tile.Show(d?.Activity, d?.UsedPercent is { } up ? $"{d.Letter}: {up:0}%" : "",
                        Ui.SeverityColor(d?.UsedPercent ?? 0, 85, 95),
                        d is null ? "No Windows disk" : $"{d.Letter}: {d.Name}\nActive {d.Activity:0}% · read {d.ReadRate:0.0} MB/s · write {d.WriteRate:0.0} MB/s\n{d.UsedPercent:0}% full · {d.FreeGb:0} GB free");
                    break;
                case "fan":
                    var f = TileFan(s);
                    tile.Show(f?.Percent, f?.Rpm is { } rpm ? $"{rpm:0} rpm" : "", null,
                        f is null ? "No fan sensors" : $"{f.Name}: {f.Percent:0}% duty · {f.Rpm:0} RPM");
                    break;
                default:
                    var g = s.Gpus.ElementAtOrDefault(int.Parse(key[3..]));
                    tile.Show(g?.Load, g?.Temp is { } gt ? $"{gt:0}°C" : "",
                        Ui.SeverityColor(g?.Temp ?? 0, 78, 85),
                        g is null ? "GPU not found" : $"{g.Name}\nLoad {g.Load:0}% · {g.Temp:0} °C\nVRAM {g.VramUsedMb / 1024:0.0} / {g.VramTotalMb / 1024:0.0} GB");
                    break;
            }
        }
    }

    private static Border Separator() => new()
    {
        Width = 1,
        Height = 30,
        Margin = new Thickness((SeparatorWidth - 1) / 2.0, 0, (SeparatorWidth - 1) / 2.0, 0),
        Background = new SolidColorBrush(Colors.Gray) { Opacity = 0.35 },
        VerticalAlignment = VerticalAlignment.Center,
    };

    // ---- Flyout ----------------------------------------------------------

    public override UIElement? CreateFlyoutContent()
    {
        _flyoutPanel = new StackPanel { Spacing = 12, Padding = new Thickness(20, 16, 20, 20) };
        RenderFlyout();
        return new ScrollViewer { Content = _flyoutPanel };
    }

    public void OnFlyoutShown()
    {
        _flyoutOpen = true;
        RenderFlyout();
    }

    public void OnFlyoutHidden() => _flyoutOpen = false;

    private void RenderFlyout()
    {
        if (_flyoutPanel is null) return;
        _flyoutPanel.Children.Clear();
        _flyoutPanel.Children.Add(new TextBlock { Text = "System", FontSize = 20, FontWeight = FontWeights.SemiBold });

        var s = _snapshot;
        if (s is null || s.Error is not null)
        {
            _flyoutPanel.Children.Add(Ui.Dim(s?.Error ?? "Loading…"));
            return;
        }

        var cpuInfo = Join(s.CpuClock is { } clk ? $"{clk / 1000:0.00} GHz" : null, s.CpuPower is { } cw ? $"{cw:0} W" : null);
        var cpu = Ui.Section("CPU", CpuColor, Join(s.CpuName, cpuInfo), "cpu");
        cpu.Children.Add(Ui.Meter("Load", s.CpuLoad ?? 0, s.CpuCoreMax is { } cm ? $"busiest core {cm:0}%" : "", CpuColor));
        if (s.CpuTemp is { } t) cpu.Children.Add(Ui.KeyValue("Temperature", $"{t:0} °C", Ui.SeverityColor(t, 80, 90)));
        Add(cpu);

        for (var i = 0; i < s.Gpus.Count; i++)
        {
            var g = s.Gpus[i];
            var color = GpuColors[i % GpuColors.Length];
            var sec = Ui.Section(g.Name.Replace("NVIDIA ", "", StringComparison.Ordinal), color,
                Join(g.CoreClock is { } gc ? $"{gc:0} MHz" : null, g.Power is { } gw ? $"{gw:0} W" : null), "gpu");
            sec.Children.Add(Ui.Meter("Load", g.Load ?? 0,
                g.VramUsedMb is { } vu && g.VramTotalMb is { } vt ? $"VRAM {vu / 1024:0.0} / {vt / 1024:0} GB" : "", color));
            if (g.Temp is { } gt)
            {
                sec.Children.Add(Ui.KeyValue("Temperature", $"{gt:0} °C" + (g.HotSpot is { } hs ? $"  ·  hot spot {hs:0} °C" : ""),
                    Ui.SeverityColor(gt, 78, 85)));
            }
            if (g.Fans.Count > 0)
            {
                var duty = g.Fans.Select(f => f.Percent).FirstOrDefault(p => p is not null);
                var rpms = string.Join(" / ", g.Fans.Select(f => $"{f.Rpm ?? 0:0}"));
                sec.Children.Add(Ui.KeyValue(g.Fans.Count > 1 ? "Fans" : "Fan", $"{(duty is { } p ? $"{p:0}%  ·  " : "")}{rpms} RPM"));
            }
            Add(sec);
        }

        var ram = Ui.Section("Memory", RamColor, $"{(s.RamUsedGb ?? 0) + (s.RamAvailableGb ?? 0):0} GB installed", "ram");
        ram.Children.Add(Ui.Meter("In use", s.RamLoad ?? 0, $"{s.RamUsedGb:0.0} GB used · {s.RamAvailableGb:0.0} GB free", RamColor));
        Add(ram);

        var disks = Ui.Section("Disks", DiskColor, null, "disk");
        foreach (var d in s.Disks.OrderBy(d => d.Letter is null).ThenBy(d => d.Letter))
        {
            var name = d.Name.Trim();
            if (d.Letter is null)
            {
                // No volume Windows can see (another OS's disk, say): LHM's "used" figure means nothing here.
                disks.Children.Add(Ui.KeyValue(name, Join("not mounted", DiskHealth(d))!, dim: true));
                continue;
            }
            disks.Children.Add(Ui.Meter($"{d.Letter}:  {name}", d.UsedPercent ?? 0,
                $"{Size(d.FreeGb)} free", DiskColor));
            disks.Children.Add(Ui.KeyValue("", Join(Io(d), DiskHealth(d))!, dim: true));
        }
        Add(disks);

        var fans = Ui.Section("Fans", FanColor, "Duty set by FanControl · measured speed", "fan");
        var spinning = s.Fans.Where(f => f.Rpm > 0).ToList();
        foreach (var f in spinning) fans.Children.Add(Ui.KeyValue(f.Name, FanText(f)));
        var idle = s.Fans.Count - spinning.Count;
        if (idle > 0) fans.Children.Add(Ui.Dim($"{idle} more header{(idle == 1 ? "" : "s")} report 0 RPM (nothing connected)", 11));
        Add(fans, divider: false);
    }

    private void Add(UIElement section, bool divider = true)
    {
        _flyoutPanel!.Children.Add(section);
        if (divider) _flyoutPanel.Children.Add(Ui.Divider());
    }

    private static string? Join(params string?[] parts)
    {
        var s = string.Join("  ·  ", parts.Where(p => !string.IsNullOrEmpty(p)));
        return s.Length == 0 ? null : s;
    }

    private static string Size(double? gb) => gb is not { } g ? "–" : g >= 1000 ? $"{g / 1000:0.0} TB" : $"{g:0} GB";

    private static string Io(DiskInfo d) =>
        (d.Activity ?? 0) < 1 && (d.ReadRate ?? 0) < 0.1 && (d.WriteRate ?? 0) < 0.1
            ? $"idle  ·  {Size(d.TotalGb)}"
            : $"{d.Activity:0}% busy  ·  R {d.ReadRate:0.0}  W {d.WriteRate:0.0} MB/s";

    private static string? DiskHealth(DiskInfo d) =>
        Join(d.Temp is { } t ? $"{t:0} °C" : null, d.Life is { } l ? $"health {l:0}%" : null);

    private static string FanText(FanInfo f) =>
        $"{(f.Percent is { } p ? $"{p:0}%  ·  " : "")}{(f.Rpm is { } r && r > 0 ? $"{r:0} RPM" : "stopped")}";

    // The fan the FAN tile follows: the one picked in settings, else the CPU fan.
    private FanInfo? TileFan(SystemSnapshot s) =>
        (_settings.FanName is { } n ? s.Fans.FirstOrDefault(f => f.Name == n) : null) ?? s.CpuFan;

    // ---- Settings --------------------------------------------------------

    public UIElement? CreateSettingsContent(IWidgetSettingsContext context)
    {
        var draft = Settings.FromJson(context.SettingsJson);
        var panel = new StackPanel();

        void Save()
        {
            context.SaveSettings(draft.ToJson());
            context.RequestPreviewRefresh();
        }

        panel.Children.Add(Ui.SettingsHeader("Taskbar tiles"));
        foreach (var key in AllKeys())
        {
            var gpu = key.StartsWith("gpu", StringComparison.Ordinal) ? _snapshot?.Gpus.ElementAtOrDefault(int.Parse(key[3..])) : null;
            var (icon, title, description) = key switch
            {
                "cpu" => ("cpu", "CPU", $"{_snapshot?.CpuName ?? "Processor"} · load, with temperature"),
                "ram" => ("ram", "Memory", "Percent in use, with GB used"),
                "disk" => ("disk", "System disk", $"Activity on {_snapshot?.SystemDisk?.Letter ?? "C"}:, with how full it is"),
                "fan" => ("fan", "Fan", "Fan curve duty, with measured RPM"),
                _ => ("gpu", gpu?.Name.Replace("NVIDIA ", "", StringComparison.Ordinal) ?? $"GPU {key[3..]}", "Core load, with temperature"),
            };
            var toggle = Ui.Switch(!draft.Hidden.Contains(key), $"Show {title}");
            toggle.Toggled += (_, _) =>
            {
                draft.Hidden.Remove(key);
                if (!toggle.IsOn) draft.Hidden.Add(key);
                Save();
            };
            panel.Children.Add(Ui.SettingsRow(icon, title, description, toggle));
        }

        panel.Children.Add(Ui.SettingsHeader("Options"));
        var fanNames = _snapshot?.Fans.Where(f => f.Rpm > 0).Select(f => f.Name).ToList() ?? [];
        if (draft.FanName is { } chosen && !fanNames.Contains(chosen)) fanNames.Add(chosen);
        var fanBox = new ComboBox { MinWidth = 200 };
        fanBox.Items.Add("CPU fan (automatic)");
        foreach (var n in fanNames) fanBox.Items.Add(n);
        fanBox.SelectedIndex = draft.FanName is { } fn ? fanNames.IndexOf(fn) + 1 : 0;
        fanBox.SelectionChanged += (_, _) =>
        {
            draft.FanName = fanBox.SelectedIndex <= 0 ? null : fanNames[fanBox.SelectedIndex - 1];
            Save();
        };
        panel.Children.Add(Ui.SettingsRow("fan", "Fan on the FAN tile", "Only fan headers that report a speed are listed", fanBox));

        panel.Children.Add(Ui.SettingsHeader("Data source"));
        var ok = _snapshot is { Error: null };
        var status = new TextBlock
        {
            Text = _snapshot is null ? "Checking…" : ok ? "Connected" : "Not reachable",
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(_snapshot is null ? Colors.Gray : ok ? Color.FromArgb(0xFF, 0x6C, 0xCB, 0x5F) : AiUsagePlugin.CritColor),
        };
        panel.Children.Add(Ui.SettingsRow(null, "LibreHardwareMonitor",
            "Its web server on 127.0.0.1:8085 provides every reading; it starts at sign-in", status));

        return Ui.SettingsPage(panel);
    }
    public override void OnSettingsDraftChanged(string settingsJson)
    {
        _settings = Settings.FromJson(settingsJson);
        _layoutSignature = "";
        RenderPreview();
        Context?.RequestPreviewRefresh();
    }

    public override ValueTask DisposeAsync()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        _previewRow = null;
        _flyoutPanel = null;
        return ValueTask.CompletedTask;
    }
}

// A system tile: label, value with a small second reading, and a 90-second graph.
internal sealed class GraphTile
{
    private const double GraphHeight = 12;
    private const int HistoryLength = 60;

    private readonly Color _color;
    private readonly double _graphWidth;
    private readonly TextBlock _value;
    private readonly TextBlock _secondary;
    private readonly Polyline _line;
    private readonly Polygon _area;
    private readonly Queue<double> _history = new();

    public StackPanel Root { get; }

    public GraphTile(string label, Color color, double width, string? icon = null)
    {
        _color = color;
        _graphWidth = width - 10;
        Root = new StackPanel
        {
            Width = width,
            Spacing = 1,
            VerticalAlignment = VerticalAlignment.Center,
            Padding = new Thickness(5, 0, 5, 0),
        };
        Root.Children.Add(Ui.TileLabel(icon, label, color));

        var values = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        _value = new TextBlock { Text = "…", FontSize = 15, LineHeight = 18, FontWeight = FontWeights.SemiBold };
        _secondary = new TextBlock
        {
            FontSize = 10,
            Opacity = 0.7,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 2),
        };
        values.Children.Add(_value);
        values.Children.Add(_secondary);
        Root.Children.Add(values);

        _area = new Polygon { Fill = new SolidColorBrush(color) { Opacity = 0.22 } };
        _line = new Polyline { Stroke = new SolidColorBrush(color), StrokeThickness = 1.2 };
        Root.Children.Add(new Canvas
        {
            Width = _graphWidth,
            Height = GraphHeight,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 1, 0, 0),
            Clip = new RectangleGeometry { Rect = new Rect(0, 0, _graphWidth, GraphHeight) },
            Children = { _area, _line },
        });
    }

    public void ShowError(string error)
    {
        _value.Text = "!";
        _secondary.Text = "";
        ToolTipService.SetToolTip(Root, error);
    }

    public void Show(double? percent, string secondary, Color? secondaryColor, string tooltip)
    {
        _value.Text = percent is { } p ? $"{p:0}%" : "–";
        if (percent is { } v && v >= 90) _value.Foreground = new SolidColorBrush(AiUsagePlugin.WarnColor);
        else _value.ClearValue(TextBlock.ForegroundProperty);

        _secondary.Text = secondary;
        if (secondaryColor is { } sc)
        {
            _secondary.Foreground = new SolidColorBrush(sc);
            _secondary.Opacity = 1;
        }
        else
        {
            _secondary.ClearValue(TextBlock.ForegroundProperty);
            _secondary.Opacity = 0.7;
        }
        ToolTipService.SetToolTip(Root, tooltip);

        if (percent is not { } value) return;
        _history.Enqueue(Math.Clamp(value, 0, 100));
        while (_history.Count > HistoryLength) _history.Dequeue();

        var samples = _history.ToArray();
        var step = _graphWidth / (HistoryLength - 1);
        var x0 = _graphWidth - (samples.Length - 1) * step;
        var line = new PointCollection();
        var area = new PointCollection { new Point(x0, GraphHeight) };
        for (var i = 0; i < samples.Length; i++)
        {
            var pt = new Point(x0 + i * step, GraphHeight - samples[i] / 100 * (GraphHeight - 1));
            line.Add(pt);
            area.Add(pt);
        }
        area.Add(new Point(_graphWidth, GraphHeight));
        _line.Points = line;
        _area.Points = area;
    }
}

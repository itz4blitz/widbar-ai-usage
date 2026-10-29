using System.Text.Json;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using WidBar.SDK;

namespace WidBar.AiUsage;

public sealed class AiUsagePlugin :
    WidgetPluginBase,
    IConfigurableWidgetPlugin,
    IWidgetFlyoutLifecycle
{
    public const string PluginId = "com.blitz.aiusage";

    // Six tiles have to sit between the system widget and the tray. 132px
    // tiles ran under the tray, and a 240px slot (the old manifest width)
    // showed only the middle two. 98px keeps the reset time on the tile.
    internal const int TileWidth = 98;
    private const int SeparatorWidth = 9;

    internal static readonly Color WarnColor = Color.FromArgb(0xFF, 0xFF, 0xB5, 0x47);
    internal static readonly Color CritColor = Color.FromArgb(0xFF, 0xFF, 0x5F, 0x5F);

    private sealed record ProviderDef(
        string Key,
        string Title,
        string TileLabel,
        Color Color,
        string Source,
        TimeSpan Interval,
        Func<CancellationToken, Task<ProviderSnapshot>> Fetch);

    private static readonly ProviderDef[] Providers =
    [
        new("claude", "Claude", "CLAUDE", Color.FromArgb(0xFF, 0xE0, 0x8A, 0x6A),
            "Plan limits · Claude Code sign-in", TimeSpan.FromMinutes(5), ClaudeSource.FetchAsync),
        new("codex", "Codex", "CODEX", Color.FromArgb(0xFF, 0x4C, 0xC2, 0xA0),
            "Plan limits · ChatGPT sign-in, Codex logs as backup", TimeSpan.FromSeconds(15), CodexSource.FetchAsync),
        new("grok", "Grok", "GROK", Color.FromArgb(0xFF, 0xB8, 0xC4, 0xD6),
            "Plan limits · Grok sign-in, spend from Grok Build logs", TimeSpan.FromSeconds(30), GrokSource.FetchAsync),
        new("zai", "z.ai", "Z.AI", Color.FromArgb(0xFF, 0x6B, 0x9B, 0xFF),
            "GLM Coding Plan quota · z.ai API key", TimeSpan.FromMinutes(2), ZaiSource.FetchAsync),
        new("cursor", "Cursor", "CURSOR", Color.FromArgb(0xFF, 0xB2, 0x8D, 0xFF),
            "Billing-month usage · Cursor sign-in or a session token", TimeSpan.FromMinutes(5), CursorSource.FetchAsync),
        new("opencode", "OpenCode", "OPENCODE", Color.FromArgb(0xFF, 0xF2, 0xC1, 0x4E),
            "OpenCode Go limits, or tokens from local sessions", TimeSpan.FromSeconds(30), OpenCodeSource.FetchAsync),
    ];

    private Settings _settings = new();
    private DispatcherQueue? _dispatcher;
    private CancellationTokenSource? _cts;
    private bool _flyoutOpen;

    private readonly Dictionary<string, ProviderSnapshot> _snapshots = [];
    private readonly Dictionary<string, DateTimeOffset> _fetchedAt = [];
    private readonly Dictionary<string, Tile> _tiles = [];
    private string _layoutSignature = "";

    private StackPanel? _previewRow;
    private StackPanel? _flyoutPanel;
    private bool _widthRefreshPending;

    public override string Id => PluginId;
    public override string Name => "AI Usage";

    public override int PreviewLogicalWidth
    {
        get
        {
            var tiles = Math.Max(1, VisibleProviders().Count());
            return tiles * TileWidth + (tiles - 1) * SeparatorWidth + 8;
        }
    }

    public override int FlyoutWidth => 380;
    public override int FlyoutHeight => 640;

    private sealed class Settings
    {
        public List<string> Hidden { get; set; } = [];
        public bool HideUnconfigured { get; set; } = true;

        public static Settings FromJson(string? json)
        {
            try
            {
                return string.IsNullOrWhiteSpace(json)
                    ? new Settings()
                    : JsonSerializer.Deserialize<Settings>(json) ?? new Settings();
            }
            catch
            {
                return new Settings();
            }
        }

        public string ToJson() => JsonSerializer.Serialize(this);
    }

    private bool IsEnabled(ProviderDef p) => !_settings.Hidden.Contains(p.Key);

    private IEnumerable<ProviderDef> VisibleProviders() =>
        Providers.Where(p => IsEnabled(p) &&
            !(_settings.HideUnconfigured && _snapshots.TryGetValue(p.Key, out var s) && s.NotConfigured));

    public override async Task InitializeAsync(IWidgetContext context)
    {
        _settings = Settings.FromJson(context.SettingsJson);
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        await base.InitializeAsync(context);

        LoadCache();
        _cts = new CancellationTokenSource();
        _ = PollLoopAsync(_cts.Token);

        // The first layout often runs before Context exists, so its refresh
        // is dropped and WidBar keeps the manifest width. Ask again now.
        RequestWidth();
    }

    private void RequestWidth()
    {
        if (Context is null)
        {
            _widthRefreshPending = true;
            return;
        }

        _widthRefreshPending = false;
        Context.RequestPreviewRefresh();
    }

    // ---- Cache -----------------------------------------------------------
    // Network sources (Claude especially) throttle hard. Remember the last
    // answer and when we asked, so a WidBar restart neither shows "!" nor
    // asks again before the interval is up.

    private static readonly string[] CachedKeys = ["claude", "zai", "cursor"];

    private sealed record CacheEntry(ProviderSnapshot Snapshot, DateTimeOffset FetchedAt);

    private string? CachePath => Context is null ? null : Path.Combine(Context.DataDirectory, "network-cache.json");

    private void LoadCache()
    {
        try
        {
            if (CachePath is not { } path || !File.Exists(path)) return;
            var entries = JsonSerializer.Deserialize<Dictionary<string, CacheEntry>>(File.ReadAllText(path));
            if (entries is null) return;
            foreach (var (key, entry) in entries)
            {
                if (DateTimeOffset.Now - entry.Snapshot.UpdatedAt > TimeSpan.FromHours(12)) continue;
                _snapshots[key] = entry.Snapshot;
                _fetchedAt[key] = entry.FetchedAt;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[AiUsage] cache load failed: {ex.Message}");
        }
    }

    private void SaveCache()
    {
        try
        {
            if (CachePath is not { } path) return;
            var entries = CachedKeys
                .Where(k => _snapshots.TryGetValue(k, out var s) && s.HasData)
                .ToDictionary(k => k, k => new CacheEntry(_snapshots[k], _fetchedAt.GetValueOrDefault(k)));
            File.WriteAllText(path, JsonSerializer.Serialize(entries));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[AiUsage] cache save failed: {ex.Message}");
        }
    }

    // ---- Polling ---------------------------------------------------------

    private async Task PollLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        do
        {
            try
            {
                await PollOnceAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"[AiUsage] poll failed: {ex}");
            }
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

    private async Task PollOnceAsync(CancellationToken ct)
    {
        if (!_flyoutOpen && !(Context?.IsPreviewVisible ?? true)) return;

        var now = DateTimeOffset.Now;
        var due = Providers
            .Where(p => (IsEnabled(p) || _flyoutOpen) &&
                        now - _fetchedAt.GetValueOrDefault(p.Key, DateTimeOffset.MinValue) >= p.Interval)
            .ToList();
        if (due.Count == 0) return;

        foreach (var p in due) _fetchedAt[p.Key] = now;
        var results = await Task.WhenAll(due.Select(async p => (p, snap: await Safe(p.Fetch, ct))));

        foreach (var (p, snap) in results)
        {
            // Usage endpoints throttle hard; after a 429 wait a while before asking again.
            if (snap.Error?.StartsWith("Rate limited", StringComparison.Ordinal) == true)
            {
                var wait = snap.RetryAfter is { } r && r > TimeSpan.FromMinutes(10) ? r : TimeSpan.FromMinutes(10);
                _fetchedAt[p.Key] = now + wait - p.Interval;
            }

            // Keep showing the last good numbers through transient failures.
            _snapshots[p.Key] = snap.Error is not null && !snap.NotConfigured && !snap.HasData &&
                                _snapshots.TryGetValue(p.Key, out var prev) && prev.HasData
                ? new ProviderSnapshot
                {
                    Session = prev.Session,
                    Weekly = prev.Weekly,
                    SpendToday = prev.SpendToday,
                    SpendWeek = prev.SpendWeek,
                    TokensToday = prev.TokensToday,
                    TokensWeek = prev.TokensWeek,
                    Details = prev.Details,
                    UpdatedAt = prev.UpdatedAt,
                    Error = snap.Error,
                }
                : snap;
        }

        if (due.Any(p => CachedKeys.Contains(p.Key))) SaveCache();
        _dispatcher?.TryEnqueue(RenderAll);
    }

    private static async Task<ProviderSnapshot> Safe(Func<CancellationToken, Task<ProviderSnapshot>> fetch, CancellationToken ct)
    {
        try
        {
            return await fetch(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[AiUsage] {ex}");
            return ProviderSnapshot.Failed(ex.Message);
        }
    }

    // ---- Taskbar preview -------------------------------------------------

    public override UIElement? CreatePreviewContent()
    {
        _dispatcher ??= DispatcherQueue.GetForCurrentThread();
        _tiles.Clear();
        foreach (var p in Providers) _tiles[p.Key] = new Tile(p.TileLabel, p.Color, bars: 2, icon: p.Key);

        _previewRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _layoutSignature = "";
        RenderPreview();

        return new Grid { Background = null, Children = { _previewRow } };
    }

    private void RenderAll()
    {
        RenderPreview();
        if (_flyoutOpen) RenderFlyout();
    }

    private void RenderPreview()
    {
        if (_previewRow is null) return;

        var visible = VisibleProviders().ToList();
        var signature = string.Join(",", visible.Select(p => p.Key));
        if (signature != _layoutSignature)
        {
            var widthChanged = _layoutSignature.Split(',', StringSplitOptions.RemoveEmptyEntries).Length != visible.Count;
            _layoutSignature = signature;
            _previewRow.Children.Clear();
            for (var i = 0; i < visible.Count; i++)
            {
                if (i > 0) _previewRow.Children.Add(Separator());
                _previewRow.Children.Add(_tiles[visible[i].Key].Root);
            }
            if (widthChanged || _widthRefreshPending) RequestWidth();
        }

        foreach (var p in visible)
        {
            _tiles[p.Key].ShowProvider(_snapshots.GetValueOrDefault(p.Key));
        }
    }

    private static Border Separator() => new()
    {
        Width = 1,
        Height = 26,
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
        // Fresh numbers when the user looks, but not more than every 30 seconds.
        foreach (var key in _fetchedAt.Keys.ToList())
        {
            if (DateTimeOffset.Now - _fetchedAt[key] > TimeSpan.FromSeconds(30)) _fetchedAt[key] = DateTimeOffset.MinValue;
        }
        RenderFlyout();
    }

    public void OnFlyoutHidden() => _flyoutOpen = false;

    private void RenderFlyout()
    {
        if (_flyoutPanel is null) return;
        _flyoutPanel.Children.Clear();
        _flyoutPanel.Children.Add(new TextBlock { Text = "AI Usage", FontSize = 20, FontWeight = FontWeights.SemiBold });

        var enabled = Providers.Where(IsEnabled).ToList();
        var missing = enabled.Where(p => _snapshots.GetValueOrDefault(p.Key) is { NotConfigured: true }).ToList();
        var shown = enabled.Except(missing).ToList();

        _flyoutPanel.Children.Add(Summary(shown));
        _flyoutPanel.Children.Add(Ui.Divider());

        foreach (var p in shown)
        {
            AddProviderSection(p, _snapshots.GetValueOrDefault(p.Key));
            _flyoutPanel.Children.Add(Ui.Divider());
        }

        if (missing.Count > 0)
        {
            var line = Ui.Dim($"Not set up on this PC: {string.Join(", ", missing.Select(p => p.Title))}", 12);
            ToolTipService.SetToolTip(line, string.Join("\n", missing.Select(p => $"{p.Title}: {_snapshots[p.Key].Error}")));
            _flyoutPanel.Children.Add(line);
        }
        else if (_flyoutPanel.Children[^1] is Border)
        {
            _flyoutPanel.Children.RemoveAt(_flyoutPanel.Children.Count - 1);
        }
    }

    // One glance at the top: each provider's icon and its fullest limit.
    private UIElement Summary(List<ProviderDef> providers)
    {
        var grid = new Grid { ColumnSpacing = 6 };
        for (var i = 0; i < providers.Count; i++)
        {
            var p = providers[i];
            var snap = _snapshots.GetValueOrDefault(p.Key);
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var cell = new StackPanel { Spacing = 3, HorizontalAlignment = HorizontalAlignment.Center };
            cell.Children.Add(Ui.Icon(p.Key, 18));
            var top = new[] { snap?.Session, snap?.Weekly }.Where(w => w is not null && !IsStale(w)).MaxBy(w => w!.Percent);
            var value = new TextBlock
            {
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                Text = top is not null ? $"{top.Percent:0}%"
                    : snap is { TokensWeek: > 0 } tok && (tok.SpendToday ?? 0) <= 0 && (tok.SpendWeek ?? 0) <= 0
                        ? Format.Tokens(tok.TokensToday > 0 ? tok.TokensToday : tok.TokensWeek)
                    : snap?.SpendToday is { } spend ? Format.UsdShort(spend)
                    : snap is null ? "…" : "–",
            };
            if (top is not null && Ui.SeverityColor(top.Percent, 75, 90) is { } c) value.Foreground = new SolidColorBrush(c);
            cell.Children.Add(value);
            ToolTipService.SetToolTip(cell, top is null ? p.Title : $"{p.Title} · {top.Name}{Format.ResetSuffix(top.ResetsAt)}");

            Grid.SetColumn(cell, i);
            grid.Children.Add(cell);
        }
        return grid;
    }

    private static bool IsStale(UsageWindow w) => w.ResetsAt is { } r && r < DateTimeOffset.Now;

    private void AddProviderSection(ProviderDef p, ProviderSnapshot? snap)
    {
        var section = Ui.Section(p.Title, p.Color, null, p.Key, tooltip: p.Source);

        if (snap is null)
        {
            section.Children.Add(Ui.Dim("Loading…", 12));
            _flyoutPanel!.Children.Add(section);
            return;
        }

        // Problems go right under the name, next to the numbers they affect.
        if (snap.Error is not null)
        {
            section.Children.Add(new TextBlock
            {
                Text = snap.HasData ? $"{snap.Error} · showing last known numbers" : snap.Error,
                Foreground = new SolidColorBrush(WarnColor),
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12,
            });
        }

        foreach (var window in new[] { snap.Session, snap.Weekly })
        {
            if (window is null) continue;
            var stale = IsStale(window);
            var note = stale ? "reset due · refreshing" : Format.ResetSuffix(window.ResetsAt).TrimStart(' ', '·').Trim();
            section.Children.Add(Ui.Meter(window.Name, window.Percent, note, p.Color, stale));
        }

        foreach (var d in snap.Details)
        {
            // Bookkeeping lines (when it last ran, where numbers came from) stay quiet.
            var quiet = d.Label is "Last activity" or "Cached" or "Updated" or "Pricing";
            section.Children.Add(Ui.KeyValue(d.Label, d.Value, dim: quiet));
        }

        _flyoutPanel!.Children.Add(section);
    }

    // ---- Settings --------------------------------------------------------

    public UIElement? CreateSettingsContent(IWidgetSettingsContext context)
    {
        var draft = Settings.FromJson(context.SettingsJson);
        var panel = new StackPanel();

        void Changed()
        {
            context.SaveSettings(draft.ToJson());
            context.RequestPreviewRefresh();
        }

        panel.Children.Add(Ui.SettingsHeader("Providers"));
        foreach (var p in Providers)
        {
            var toggle = Ui.Switch(!draft.Hidden.Contains(p.Key), $"Show {p.Title}");
            toggle.Toggled += (_, _) =>
            {
                draft.Hidden.Remove(p.Key);
                if (!toggle.IsOn) draft.Hidden.Add(p.Key);
                Changed();
            };
            panel.Children.Add(Ui.SettingsRow(p.Key, p.Title, $"{p.Source}  ·  {StatusText(p.Key)}", toggle));

            if (p.Key == "zai") panel.Children.Add(ZaiKeyRow());
            if (p.Key == "cursor")
            {
                panel.Children.Add(SecretRow("Cursor session token",
                    "WorkosCursorSessionToken from cursor.com while signed in. Stored encrypted for your Windows account",
                    CursorSource.HasKey, CursorSource.SaveKey, "cursor"));
            }
            if (p.Key == "opencode")
            {
                panel.Children.Add(SecretRow("OpenCode API key",
                    "From OpenCode Console, or run opencode auth login. Stored encrypted for your Windows account",
                    OpenCodeSource.HasKey, OpenCodeSource.SaveKey, "opencode"));
            }
        }

        panel.Children.Add(Ui.SettingsHeader("Display"));
        var hide = Ui.Switch(draft.HideUnconfigured, "Hide providers that aren't set up");
        hide.Toggled += (_, _) =>
        {
            draft.HideUnconfigured = hide.IsOn;
            Changed();
        };
        panel.Children.Add(Ui.SettingsRow(null, "Hide providers that aren't set up",
            "Tools that aren't installed or signed in on this PC stay off the taskbar until they are", hide));

        return Ui.SettingsPage(panel);
    }

    private string StatusText(string key) => _snapshots.GetValueOrDefault(key) switch
    {
        null => "Checking…",
        { NotConfigured: true } s => s.Error ?? "Not set up",
        { Error: { } e } when e.StartsWith("Rate limited", StringComparison.Ordinal) => "Rate limited, showing last numbers",
        { HasData: true } => "Connected",
        var s => s.Error ?? "No data yet",
    };

    private Border SecretRow(string title, string description, bool hasKey, Action<string> save, string providerKey)
    {
        var box = new PasswordBox { Width = 240, PlaceholderText = hasKey ? "Saved · paste to replace" : "Paste to save" };
        var button = new Button { Content = "Save", IsEnabled = false };
        box.PasswordChanged += (_, _) => button.IsEnabled = box.Password.Trim().Length > 0;
        button.Click += (_, _) =>
        {
            try
            {
                save(box.Password);
                box.Password = "";
                box.PlaceholderText = "Saved · paste to replace";
                _fetchedAt[providerKey] = DateTimeOffset.MinValue;
            }
            catch (Exception ex)
            {
                box.PlaceholderText = $"Couldn't save: {ex.Message}";
            }
        };
        var controls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { box, button } };
        return Ui.SettingsRow(null, title, description, controls);
    }

    // The z.ai key is saved straight away (encrypted for this Windows account),
    // independent of the dialog's Save button.
    private Border ZaiKeyRow()
    {
        var box = new PasswordBox { Width = 240, PlaceholderText = ZaiSource.HasKey ? "Saved · paste to replace" : "Paste API key" };
        var save = new Button { Content = "Save key", IsEnabled = false };
        box.PasswordChanged += (_, _) => save.IsEnabled = box.Password.Trim().Length > 0;
        save.Click += (_, _) =>
        {
            try
            {
                ZaiSource.SaveKey(box.Password);
                box.Password = "";
                box.PlaceholderText = "Saved · paste to replace";
                _fetchedAt["zai"] = DateTimeOffset.MinValue;
            }
            catch (Exception ex)
            {
                box.PlaceholderText = $"Couldn't save: {ex.Message}";
            }
        };
        var controls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { box, save } };
        return Ui.SettingsRow(null, "z.ai API key", "From z.ai → API keys. Stored encrypted for your Windows account only", controls);
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

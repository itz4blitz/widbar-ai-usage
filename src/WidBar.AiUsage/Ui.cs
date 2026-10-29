using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.UI;
using static WidBar.AiUsage.AiUsagePlugin;

namespace WidBar.AiUsage;

internal static class Ui
{
    public static StackPanel Section(string title, Color color, string? subtitle, string? icon = null, string? tooltip = null)
    {
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        header.Children.Add(icon is not null
            ? Icon(icon, 18)
            : new Border
            {
                Width = 10,
                Height = 10,
                CornerRadius = new CornerRadius(5),
                Background = new SolidColorBrush(color),
                VerticalAlignment = VerticalAlignment.Center,
            });
        header.Children.Add(new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeights.SemiBold });
        if (tooltip is not null) ToolTipService.SetToolTip(header, tooltip);

        var section = new StackPanel { Spacing = 6 };
        section.Children.Add(header);
        if (!string.IsNullOrEmpty(subtitle)) section.Children.Add(Dim(subtitle, 11));
        return section;
    }

    // Name and a dim note (reset time, caption) on one line, the percentage on
    // the right, the bar underneath. A stale meter is drawn faded.
    public static UIElement Meter(string name, double percent, string note, Color color, bool stale = false)
    {
        var grid = new Grid { RowSpacing = 4, Opacity = stale ? 0.45 : 1 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var label = new TextBlock { FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis };
        label.Inlines.Add(new Run { Text = name });
        if (!string.IsNullOrEmpty(note))
        {
            label.Inlines.Add(new Run { Text = "  " + note, FontSize = 11, Foreground = DimBrush });
        }
        grid.Children.Add(label);

        var value = new TextBlock { Text = $"{percent:0}%", FontSize = 13, FontWeight = FontWeights.SemiBold, Margin = new Thickness(8, 0, 0, 0) };
        if (SeverityColor(percent, 75, 90) is { } severity) value.Foreground = new SolidColorBrush(severity);
        Grid.SetColumn(value, 1);
        grid.Children.Add(value);

        var bar = new ProgressBar
        {
            Value = Math.Clamp(percent, 0, 100),
            Maximum = 100,
            Foreground = new SolidColorBrush(SeverityColor(percent, 75, 90) ?? color),
        };
        Grid.SetRow(bar, 1);
        Grid.SetColumnSpan(bar, 2);
        grid.Children.Add(bar);
        return grid;
    }

    // Label left, value right. Long values wrap instead of squeezing the label.
    public static UIElement KeyValue(string key, string value, Color? valueColor = null, bool dim = false)
    {
        var grid = new Grid { ColumnSpacing = 12, Opacity = dim ? 0.6 : 1 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(new TextBlock { Text = key, FontSize = 13, Opacity = 0.8, MaxWidth = 150, TextWrapping = TextWrapping.Wrap });
        var v = new TextBlock
        {
            Text = value,
            FontSize = 13,
            FontWeight = dim ? FontWeights.Normal : FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Right,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        if (valueColor is { } c) v.Foreground = new SolidColorBrush(c);
        Grid.SetColumn(v, 1);
        grid.Children.Add(v);
        return grid;
    }

    public static Border Divider() => new()
    {
        Height = 1,
        Background = new SolidColorBrush(Colors.Gray) { Opacity = 0.25 },
    };

    // Brand icons from dashboard-icons, shipped next to the exe (see package/Icons/SOURCES.md).
    public static Image Icon(string name, double size) => new()
    {
        Source = new BitmapImage(new Uri(Path.Combine(AppContext.BaseDirectory, "Icons", name + ".png"))),
        Height = size,
        MaxWidth = size * 1.6,
        Stretch = Stretch.Uniform,
        VerticalAlignment = VerticalAlignment.Center,
    };

    // A tile's top line: small brand icon, then the coloured label.
    public static StackPanel TileLabel(string? icon, string text, Color color)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Height = 12 };
        if (icon is not null) row.Children.Add(Icon(icon, 11));
        row.Children.Add(new TextBlock
        {
            Text = text,
            FontSize = 10,
            LineHeight = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(color),
            CharacterSpacing = 40,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        });
        return row;
    }

    // ---- Settings dialogs, in the style of the Windows 11 Settings app ----

    public static TextBlock SettingsHeader(string text) => new()
    {
        Text = text,
        FontSize = 14,
        FontWeight = FontWeights.SemiBold,
        Margin = new Thickness(1, 20, 0, 6),
    };

    // A settings card: icon, title and description on the left, control on the right.
    public static Border SettingsRow(string? icon, string title, string? description, UIElement? control)
    {
        var grid = new Grid { ColumnSpacing = 16 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        if (icon is not null)
        {
            var img = Icon(icon, 22);
            img.Width = 28;
            grid.Children.Add(img);
        }

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 2 };
        text.Children.Add(new TextBlock { Text = title, FontSize = 14 });
        if (!string.IsNullOrEmpty(description)) text.Children.Add(Dim(description, 12));
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        if (control is FrameworkElement fe)
        {
            fe.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(fe, 2);
            grid.Children.Add(fe);
        }

        return new Border
        {
            Child = grid,
            MinHeight = 64,
            Padding = new Thickness(16, 12, 16, 12),
            CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(1),
            Background = ThemeBrush("CardBackgroundFillColorDefaultBrush", Color.FromArgb(0x0D, 0xFF, 0xFF, 0xFF)),
            BorderBrush = ThemeBrush("CardStrokeColorDefaultBrush", Color.FromArgb(0x19, 0x00, 0x00, 0x00)),
        };
    }

    // A ToggleSwitch without the "On"/"Off" text, as Settings shows them.
    public static ToggleSwitch Switch(bool isOn, string name)
    {
        var toggle = new ToggleSwitch { IsOn = isOn, OnContent = "", OffContent = "", MinWidth = 0 };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(toggle, name);
        return toggle;
    }

    // Settings content: a centred column that scrolls, so a wide dialog doesn't look empty.
    public static ScrollViewer SettingsPage(StackPanel content)
    {
        content.Width = 860;
        content.Spacing = 4;
        content.Padding = new Thickness(8, 0, 8, 24);
        content.HorizontalAlignment = HorizontalAlignment.Center;
        var page = new ScrollViewer
        {
            Content = content,
            HorizontalScrollMode = ScrollMode.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        // WidBar hosts settings in a sideways-scrolling area that lays content out
        // at its natural width, which pushed the cards past the dialog's edge.
        // Turn that off and give the page and the card column fixed widths.
        page.Loaded += (_, _) =>
        {
            for (var p = VisualTreeHelper.GetParent(page); p is not null; p = VisualTreeHelper.GetParent(p))
            {
                if (p is not ScrollViewer host) continue;
                host.HorizontalScrollMode = ScrollMode.Disabled;
                host.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
                host.ChangeView(0, null, null, disableAnimation: true);

                // Size the page to what the dialog shows, so the cards centre in it.
                void Fit()
                {
                    page.Width = Math.Max(0, host.ViewportWidth - 16);
                    content.Width = Math.Min(860, page.Width);
                }
                Fit();
                host.SizeChanged += (_, _) => Fit();
                break;
            }
        };
        return page;
    }

    private static Brush ThemeBrush(string key, Color fallback) =>
        Application.Current.Resources.TryGetValue(key, out var b) && b is Brush brush ? brush : new SolidColorBrush(fallback);

    private static SolidColorBrush DimBrush => new(Colors.Gray);

    public static TextBlock Dim(string text, double size = 13) =>
        new() { Text = text, FontSize = size, Opacity = 0.6, TextWrapping = TextWrapping.Wrap };

    public static Color? SeverityColor(double value, double warn, double crit) =>
        value >= crit ? CritColor : value >= warn ? WarnColor : null;
}
// One provider on the taskbar: label, headline value and up to two thin bars.
internal sealed class Tile
{
    private const double TrackWidth = TileWidth - 10;

    private readonly Color _color;
    private readonly TextBlock _value;
    private readonly TextBlock _secondary;
    private readonly List<(Border Track, Border Fill)> _bars = [];

    public StackPanel Root { get; }

    public Tile(string label, Color color, int bars, string? icon = null)
    {
        _color = color;
        Root = new StackPanel
        {
            Width = TileWidth,
            Spacing = 1,
            VerticalAlignment = VerticalAlignment.Center,
            Padding = new Thickness(5, 0, 5, 0),
        };

        Root.Children.Add(Ui.TileLabel(icon, label, color));


        var values = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        _value = new TextBlock { Text = "–", FontSize = 15, LineHeight = 18, FontWeight = FontWeights.SemiBold };
        _secondary = new TextBlock
        {
            Text = "",
            FontSize = 10,
            Opacity = 0.65,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 2),
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = TileWidth - 36,
        };
        values.Children.Add(_value);
        values.Children.Add(_secondary);
        Root.Children.Add(values);

        for (var i = 0; i < bars; i++) AddBar(i == 0 ? 3 : 2, i == 0 ? 1.0 : 0.55);
    }

    private void AddBar(double height, double opacity)
    {
        var fill = new Border
        {
            Height = height,
            Width = 0,
            HorizontalAlignment = HorizontalAlignment.Left,
            CornerRadius = new CornerRadius(height / 2),
            Background = new SolidColorBrush(_color) { Opacity = opacity },
        };
        var track = new Border
        {
            Width = TrackWidth,
            Height = height,
            HorizontalAlignment = HorizontalAlignment.Left,
            CornerRadius = new CornerRadius(height / 2),
            Background = new SolidColorBrush(Colors.Gray) { Opacity = 0.25 },
            Margin = new Thickness(0, 2, 0, 0),
            Child = fill,
        };
        Root.Children.Add(track);
        _bars.Add((track, fill));
    }

    private void SetBar(int index, double? fraction)
    {
        if (index >= _bars.Count) return;
        var (track, fill) = _bars[index];
        // Spend tiles have no limit to fill towards: keep the row's height, hide the bar.
        track.Opacity = fraction is null ? 0 : 1;
        var f = Math.Clamp(fraction ?? 0, 0, 1);
        fill.Width = TrackWidth * f;
        if (fill.Background is SolidColorBrush b) b.Color = Ui.SeverityColor(f * 100, 75, 90) ?? _color;
    }

    private void SetValueColor(Color? color)
    {
        if (color is { } c) _value.Foreground = new SolidColorBrush(c);
        else _value.ClearValue(TextBlock.ForegroundProperty);
    }

    public void ShowProvider(ProviderSnapshot? snap)
    {
        if (snap is null || !snap.HasData)
        {
            _value.Text = snap is null ? "…" : snap.NotConfigured ? "–" : "!";
            _secondary.Text = "";
            SetValueColor(null);
            ToolTipService.SetToolTip(Root, snap?.Error);
            SetBar(0, 0);
            SetBar(1, 0);
            return;
        }

        string tip;
        if (snap.Session is not null || snap.Weekly is not null)
        {
            // With a single window (Cursor's billing cycle) it gets the headline.
            var head = snap.Session ?? snap.Weekly!;
            var second = snap.Session is null ? null : snap.Weekly;
            _value.Text = $"{head.Percent:0}%";
            SetValueColor(Ui.SeverityColor(head.Percent, 75, 90));
            _secondary.Text = SecondLine(head, second);
            SetBar(0, head.Percent / 100);
            SetBar(1, second is null ? null : second.Percent / 100);

            tip = $"{head.Name}: {head.Percent:0}%{Format.ResetSuffix(head.ResetsAt)}";
            if (second is not null) tip += $"\n{second.Name}: {second.Percent:0}%{Format.ResetSuffix(second.ResetsAt)}";
        }
        else if (snap.TokensWeek > 0 && (snap.SpendToday ?? 0) <= 0 && (snap.SpendWeek ?? 0) <= 0)
        {
            SetValueColor(null);
            SetBar(0, null);
            SetBar(1, null);
            if (snap.TokensToday > 0)
            {
                _value.Text = Format.Tokens(snap.TokensToday);
                _secondary.Text = $"wk {Format.Tokens(snap.TokensWeek)}";
            }
            else
            {
                _value.Text = Format.Tokens(snap.TokensWeek);
                _secondary.Text = "7d";
            }
            tip = $"Today: {Format.Tokens(snap.TokensToday)} tokens\nLast 7 days: {Format.Tokens(snap.TokensWeek)} tokens";
        }
        else
        {
            _value.Text = Format.UsdShort(snap.SpendToday ?? 0);
            SetValueColor(null);
            _secondary.Text = $"wk {Format.UsdShort(snap.SpendWeek ?? 0)}";
            SetBar(0, null);
            SetBar(1, null);
            tip = $"Today: {Format.Usd(snap.SpendToday ?? 0)}\nLast 7 days: {Format.Usd(snap.SpendWeek ?? 0)}";
        }

        if (snap.Error is not null) tip += $"\n{snap.Error}";
        ToolTipService.SetToolTip(Root, tip);
    }

    // "3h · wk 98%": the headline window's reset, then the other window.
    // When the headline has no reset time, use the other window's.
    private static string SecondLine(UsageWindow head, UsageWindow? second)
    {
        var reset = Format.CompactReset(head.ResetsAt);
        if (second is null) return reset;
        if (reset.Length == 0) reset = Format.CompactReset(second.ResetsAt);
        var tag = second.Name.Contains("month", StringComparison.OrdinalIgnoreCase) ? "mo" : "wk";
        var other = $"{tag} {second.Percent:0}%";
        return reset.Length == 0 ? other : $"{reset} · {other}";
    }
}

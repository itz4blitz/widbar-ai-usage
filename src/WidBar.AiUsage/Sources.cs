using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace WidBar.AiUsage;

public sealed record UsageWindow(string Name, double Percent, DateTimeOffset? ResetsAt);

public sealed record DetailLine(string Label, string Value);

public sealed class ProviderSnapshot
{
    public UsageWindow? Session { get; init; }
    public UsageWindow? Weekly { get; init; }

    // Pay-per-token tools have no plan limit to show, only what they cost.
    public double? SpendToday { get; init; }
    public double? SpendWeek { get; init; }
    public long TokensToday { get; init; }
    public long TokensWeek { get; init; }

    public List<DetailLine> Details { get; init; } = [];
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.Now;
    public string? Error { get; init; }

    // Set when the service asked us to slow down.
    public TimeSpan? RetryAfter { get; init; }

    // The tool isn't installed or signed in on this PC; its tile hides itself.
    public bool NotConfigured { get; init; }

    public bool HasData => Session is not null || Weekly is not null || SpendToday is not null;

    public static ProviderSnapshot Failed(string error) => new() { Error = error };

    public static ProviderSnapshot Missing(string why) => new() { Error = why, NotConfigured = true };
}

internal static class Http
{
    public static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(15) };
}


public static class Format
{
    public static string Until(DateTimeOffset at)
    {
        var span = at - DateTimeOffset.Now;
        if (span <= TimeSpan.Zero) return "now";
        if (span.TotalDays >= 1) return $"{(int)span.TotalDays}d {span.Hours}h";
        if (span.TotalHours >= 1) return $"{(int)span.TotalHours}h {span.Minutes}m";
        return $"{Math.Max(1, span.Minutes)}m";
    }

    public static string ResetSuffix(DateTimeOffset? at) => at is { } a ? $" · resets in {Until(a)}" : "";

    // Short enough for a taskbar tile: "3h", "2d", "18m".
    public static string CompactReset(DateTimeOffset? at)
    {
        if (at is not { } when) return "";
        var span = when - DateTimeOffset.Now;
        if (span <= TimeSpan.Zero) return "due";
        if (span.TotalDays >= 1) return $"{(int)span.TotalDays}d";
        if (span.TotalHours >= 1) return $"{(int)span.TotalHours}h";
        return $"{Math.Max(1, (int)span.TotalMinutes)}m";
    }

    public static string Usd(double amount) =>
        amount >= 100 ? $"${amount:0}" : amount >= 10 ? $"${amount:0.0}" : $"${amount:0.00}";

    // Compact form for the taskbar tile.
    public static string UsdShort(double amount) =>
        amount <= 0 ? "$0" : amount >= 10 ? $"${amount:0}" : $"${amount:0.00}";

    public static string Tokens(long tokens) =>
        tokens >= 1_000_000_000 ? $"{tokens / 1e9:0.0}B"
        : tokens >= 1_000_000 ? $"{tokens / 1e6:0.0}M"
        : tokens >= 1_000 ? $"{tokens / 1e3:0}K"
        : tokens.ToString(CultureInfo.CurrentCulture);

    public static string Ago(DateTimeOffset at)
    {
        var span = DateTimeOffset.Now - at;
        if (span.TotalMinutes < 1) return "just now";
        if (span.TotalHours < 1) return $"{(int)span.TotalMinutes}m ago";
        if (span.TotalDays < 1) return $"{(int)span.TotalHours}h ago";
        return $"{(int)span.TotalDays}d ago";
    }
}

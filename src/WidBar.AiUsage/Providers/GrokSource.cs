using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace WidBar.AiUsage;

// Grok Build: the SuperGrok usage allowance (the same endpoint the CLI's /usage
// modal reads), plus the spend tally from the usage.json Grok keeps per session.
// The allowance call uses Grok Build's own OIDC login from ~/.grok/auth.json.
// We never refresh that token; Grok Build renews it whenever it runs.
public static class GrokSource
{
    private static readonly string GrokHome =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".grok");

    private static readonly string SessionsDir = Path.Combine(GrokHome, "sessions");
    private static readonly string AuthPath = Path.Combine(GrokHome, "auth.json");
    private static readonly string SettingsCachePath = Path.Combine(GrokHome, "settings_cache.json");

    private const string BillingUrl = "https://cli-chat-proxy.grok.com/v1/billing?format=credits";

    // xAI bills in "USD ticks": 1 USD = 10^10 ticks.
    private const double TicksPerUsd = 1e10;

    public static async Task<ProviderSnapshot> FetchAsync(CancellationToken ct)
    {
        if (!Directory.Exists(GrokHome)) return ProviderSnapshot.Missing("Grok Build is not set up");

        var local = await Task.Run(() => ReadSpend(ct), ct);
        var details = new List<DetailLine>();
        if (ReadTier() is { } tier) details.Add(new DetailLine("Plan", tier));

        UsageWindow? session = null, weekly = null;
        TimeSpan? retryAfter = null;
        try
        {
            var allowance = await FetchAllowanceAsync(ct);
            if (allowance.Note is { } note) details.Add(new DetailLine("Usage limit", note));
            session = allowance.Session;
            weekly = allowance.Weekly;
            retryAfter = allowance.RetryAfter;
            details.AddRange(allowance.Details);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            details.Add(new DetailLine("Usage limit", "Couldn't reach grok.com"));
        }

        if (local is not null)
        {
            details.Add(new DetailLine("Today", $"{Format.Usd(local.Today)} · {Format.Tokens(local.TokensToday)} tokens"));
            details.Add(new DetailLine("Last 7 days", $"{Format.Usd(local.Week)} · {Format.Tokens(local.TokensWeek)} tokens"));
            details.Add(new DetailLine("Pricing", "xAI list price"));
            if (local.LastActivity is { } last) details.Add(new DetailLine("Last activity", Format.Ago(last)));
        }

        return new ProviderSnapshot
        {
            Session = session,
            Weekly = weekly,
            SpendToday = local?.Today,
            SpendWeek = local?.Week,
            Details = details,
            RetryAfter = retryAfter,
        };
    }

    private sealed record Allowance(
        UsageWindow? Session, UsageWindow? Weekly, List<DetailLine> Details, string? Note = null, TimeSpan? RetryAfter = null);

    private static async Task<Allowance> FetchAllowanceAsync(CancellationToken ct)
    {
        var (token, expires) = ReadToken();
        if (token is null) return new Allowance(null, null, [], "Not signed in to Grok Build");
        if (expires is { } exp && exp <= DateTimeOffset.UtcNow.AddSeconds(30))
        {
            return new Allowance(null, null, [], "Login expired. Open Grok Build to refresh it");
        }

        using var req = new HttpRequestMessage(HttpMethod.Get, BillingUrl);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.Accept.ParseAdd("application/json");
        req.Headers.UserAgent.ParseAdd("widbar-aiusage/1.0");
        using var resp = await Http.Client.SendAsync(req, ct);
        if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            return new Allowance(null, null, [], "Login expired. Open Grok Build to refresh it");
        }
        if (resp.StatusCode == HttpStatusCode.TooManyRequests)
        {
            return new Allowance(null, null, [], "Rate limited", resp.Headers.RetryAfter?.Delta ?? TimeSpan.FromMinutes(5));
        }
        resp.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        if (!doc.RootElement.TryGetProperty("config", out var cfg) || cfg.ValueKind != JsonValueKind.Object)
        {
            return new Allowance(null, null, [], "No usage data");
        }

        var details = new List<DetailLine>();
        UsageWindow? session = null, weekly = null;

        // e.g. {"type":"USAGE_PERIOD_TYPE_WEEKLY","start":"…","end":"…"}
        var periodType = "";
        DateTimeOffset? periodEnd = null;
        if (cfg.TryGetProperty("currentPeriod", out var period) && period.ValueKind == JsonValueKind.Object)
        {
            periodType = period.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
            periodEnd = Date(period, "end");
        }
        periodEnd ??= Date(cfg, "billingPeriodEnd");

        if (Number(cfg, "creditUsagePercent") is { } pct)
        {
            if (periodType.EndsWith("WEEKLY", StringComparison.Ordinal))
            {
                weekly = new UsageWindow("Weekly", pct, periodEnd);
            }
            else
            {
                // Daily / hourly / monthly allowances: show as the primary window.
                var name = periodType.Replace("USAGE_PERIOD_TYPE_", "", StringComparison.Ordinal) switch
                {
                    "DAILY" => "Daily",
                    "MONTHLY" => "Monthly",
                    { Length: > 0 } other => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(other.ToLowerInvariant()),
                    _ => "Usage limit",
                };
                session = new UsageWindow(name, pct, periodEnd);
            }
        }

        // Share of the allowance per product (Grok Build, Imagine, Chat).
        if (cfg.TryGetProperty("productUsage", out var products) && products.ValueKind == JsonValueKind.Array)
        {
            foreach (var p in products.EnumerateArray())
            {
                if (Number(p, "usagePercent") is not { } up || up <= 0) continue;
                var product = p.TryGetProperty("product", out var pn) ? pn.GetString() ?? "" : "";
                details.Add(new DetailLine(ProductName(product), $"{up:0}%"));
            }
        }

        // Money amounts come as {"val": n}. Assumed cents, like grok.com's billing page.
        if (Money(cfg, "prepaidBalance") is { } prepaid && prepaid > 0)
        {
            details.Add(new DetailLine("Prepaid credits", Format.Usd(prepaid / 100)));
        }
        var odCap = Money(cfg, "onDemandCap") ?? 0;
        var odUsed = Money(cfg, "onDemandUsed") ?? 0;
        if (odCap > 0 || odUsed > 0)
        {
            details.Add(new DetailLine("On-demand", odCap > 0
                ? $"{Format.Usd(odUsed / 100)} / {Format.Usd(odCap / 100)}"
                : Format.Usd(odUsed / 100)));
        }

        return session is null && weekly is null && details.Count == 0
            ? new Allowance(null, null, [], "No usage data")
            : new Allowance(session, weekly, details);
    }

    private static string ProductName(string product) => product switch
    {
        "GrokBuild" => "Grok Build",
        "GrokImagine" => "Imagine",
        "GrokChat" => "Chat",
        _ => product,
    };

    private static double? Number(JsonElement e, string key)
    {
        if (!e.TryGetProperty(key, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number) return v.GetDouble();
        return v.ValueKind == JsonValueKind.String &&
               double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
    }

    private static double? Money(JsonElement e, string key) =>
        e.TryGetProperty(key, out var m) && m.ValueKind == JsonValueKind.Object ? Number(m, "val") : null;

    private static DateTimeOffset? Date(JsonElement e, string key) =>
        e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String &&
        DateTimeOffset.TryParse(v.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d
            : null;

    // auth.json maps "<issuer>::<client id>" to the login; we take the first OIDC one.
    private static (string? Token, DateTimeOffset? Expires) ReadToken()
    {
        if (!File.Exists(AuthPath)) return (null, null);
        try
        {
            using var fs = new FileStream(AuthPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var doc = JsonDocument.Parse(fs);
            foreach (var scope in doc.RootElement.EnumerateObject())
            {
                var e = scope.Value;
                if (e.ValueKind != JsonValueKind.Object) continue;
                if (!e.TryGetProperty("key", out var k) || k.GetString() is not { Length: > 0 } key) continue;
                return (key, Date(e, "expires_at"));
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
        }
        return (null, null);
    }

    // settings_cache.json holds the server settings as a JSON string in "payload";
    // it includes a display name for the subscription ("SuperGrok Heavy").
    private static string? ReadTier()
    {
        if (!File.Exists(SettingsCachePath)) return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(SettingsCachePath));
            if (!doc.RootElement.TryGetProperty("payload", out var p) || p.GetString() is not { } payload) return null;
            using var inner = JsonDocument.Parse(payload);
            return inner.RootElement.TryGetProperty("settings", out var s) &&
                   s.TryGetProperty("subscription_tier_display", out var t) &&
                   t.GetString() is { Length: > 0 } tier
                ? tier
                : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    private static SpendTally? ReadSpend(CancellationToken ct)
    {
        if (!Directory.Exists(SessionsDir)) return null;

        var tally = new SpendTally();
        foreach (var file in new DirectoryInfo(SessionsDir).EnumerateFiles("usage.json", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            if (file.LastWriteTime < tally.Since) continue;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllBytes(file.FullName));
                if (!doc.RootElement.TryGetProperty("turns", out var turns)) continue;
                foreach (var turn in turns.EnumerateArray())
                {
                    if (!turn.TryGetProperty("endedAt", out var ended) ||
                        !DateTimeOffset.TryParse(ended.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var at))
                    {
                        continue;
                    }
                    var ticks = turn.TryGetProperty("costUsdTicks", out var c) ? c.GetDouble() : 0;
                    var tokens = turn.TryGetProperty("totalTokens", out var t) ? t.GetInt64() : 0;
                    tally.Add(at, ticks / TicksPerUsd, tokens);
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException)
            {
                // Grok is writing the file right now; it gets read next time.
            }
        }
        return tally;
    }
}

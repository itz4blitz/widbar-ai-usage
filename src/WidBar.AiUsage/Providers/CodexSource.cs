using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace WidBar.AiUsage;

// Codex plan limits. First choice is the live endpoint Codex's own /status
// reads (GET chatgpt.com/backend-api/wham/usage), authorised with the token
// Codex keeps in ~/.codex/auth.json. We never refresh that token: rotating it
// here would sign Codex out. If the token is missing, expired or rejected, we
// fall back to the newest rate_limits event in the session logs.
public static class CodexSource
{
    private static readonly string CodexDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");

    private static readonly string SessionsDir = Path.Combine(CodexDir, "sessions");
    private static readonly string AuthPath = Path.Combine(CodexDir, "auth.json");

    private const string UsageUrl = "https://chatgpt.com/backend-api/wham/usage";
    private const int TailBytes = 1024 * 1024;

    public static async Task<ProviderSnapshot> FetchAsync(CancellationToken ct)
    {
        var (live, liveError) = await TryLiveAsync(ct);
        if (live is not null) return live;

        var fromLogs = await Task.Run(() => ReadLogs(ct), ct);
        if (liveError is not null && fromLogs.HasData)
        {
            fromLogs.Details.Add(new DetailLine("Live", liveError));
        }
        if (!fromLogs.HasData && liveError is not null && !fromLogs.NotConfigured)
        {
            return ProviderSnapshot.Failed(liveError);
        }
        return fromLogs;
    }

    // ---------- live endpoint ----------

    // Returns (snapshot, null) on success; (null, reason) when we should fall back.
    // A 429 is returned as a snapshot so the caller backs off.
    private static async Task<(ProviderSnapshot?, string?)> TryLiveAsync(CancellationToken ct)
    {
        string token, accountId;
        try
        {
            using var auth = JsonDocument.Parse(await File.ReadAllTextAsync(AuthPath, ct));
            if (!auth.RootElement.TryGetProperty("tokens", out var tokens) || tokens.ValueKind != JsonValueKind.Object)
                return (null, null); // API-key mode: no plan limits to read live
            token = tokens.TryGetProperty("access_token", out var a) ? a.GetString() ?? "" : "";
            accountId = tokens.TryGetProperty("account_id", out var id) ? id.GetString() ?? "" : "";
            if (token.Length == 0) return (null, null);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException or JsonException)
        {
            return (null, null);
        }

        if (JwtExpiry(token) is { } exp && exp < DateTimeOffset.UtcNow)
            return (null, "Login expired. Run Codex to refresh it");

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, UsageUrl);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (accountId.Length > 0) req.Headers.Add("ChatGPT-Account-Id", accountId);
            req.Headers.UserAgent.ParseAdd("codex-cli");
            req.Headers.Accept.ParseAdd("application/json");
            using var resp = await Http.Client.SendAsync(req, ct);

            if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return (null, "Login expired. Run Codex to refresh it");
            if (resp.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var wait = resp.Headers.RetryAfter?.Delta
                           ?? (resp.Headers.RetryAfter?.Date is { } until ? until - DateTimeOffset.UtcNow : null);
                return (new ProviderSnapshot
                {
                    Error = wait is { } w && w > TimeSpan.FromSeconds(30) ? $"Rate limited, retrying in {Format.Until(DateTimeOffset.Now + w)}" : "Rate limited, retrying later",
                    RetryAfter = wait,
                }, null);
            }
            if (!resp.IsSuccessStatusCode) return (null, $"Usage service returned {(int)resp.StatusCode}");

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            return (ParseLive(doc.RootElement), null);
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return (null, "Usage service unreachable");
        }
    }

    private static ProviderSnapshot ParseLive(JsonElement root)
    {
        var details = new List<DetailLine>();
        UsageWindow? session = null, weekly = null;

        if (Obj(root, "rate_limit") is { } rl)
        {
            session = LiveWindow(rl, "primary_window");
            weekly = LiveWindow(rl, "secondary_window");
        }

        if (Str(root, "plan_type") is { } plan)
            details.Add(new DetailLine("Plan", Title(plan)));

        if (Obj(root, "credits") is { } credits)
        {
            if (credits.TryGetProperty("unlimited", out var u) && u.ValueKind == JsonValueKind.True)
                details.Add(new DetailLine("Credits", "Unlimited"));
            else if (Str(credits, "balance") is { } b &&
                     double.TryParse(b, NumberStyles.Float, CultureInfo.InvariantCulture, out var balance))
                details.Add(new DetailLine("Credits", balance.ToString("N0", CultureInfo.CurrentCulture)));
        }

        // Code review has its own limit (same shape as rate_limit), null on most plans.
        if (Obj(root, "code_review_rate_limit") is { } cr)
            AddLimitDetails(details, "Code review", cr);

        // Per-feature/model limits: [{ limit_name, metered_feature, rate_limit: {...} }]
        if (root.TryGetProperty("additional_rate_limits", out var extra) && extra.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in extra.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var name = Str(item, "limit_name") ?? Str(item, "metered_feature") ?? "Additional";
                if (Obj(item, "rate_limit") is { } lim) AddLimitDetails(details, name, lim);
            }
        }

        if (Str(root, "rate_limit_reached_type") is { } reached)
            details.Add(new DetailLine("Limit reached", reached.Replace('_', ' ')));

        return new ProviderSnapshot { Session = session, Weekly = weekly, Details = details };
    }

    private static void AddLimitDetails(List<DetailLine> details, string label, JsonElement limit)
    {
        foreach (var key in new[] { "primary_window", "secondary_window" })
        {
            if (LiveWindow(limit, key) is { } w)
                details.Add(new DetailLine($"{label} · {w.Name}", $"{w.Percent:0}%{Format.ResetSuffix(w.ResetsAt)}"));
        }
    }

    private static UsageWindow? LiveWindow(JsonElement rl, string key)
    {
        if (Obj(rl, key) is not { } w) return null;
        if (!w.TryGetProperty("used_percent", out var p) || p.ValueKind != JsonValueKind.Number) return null;
        var seconds = w.TryGetProperty("limit_window_seconds", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetInt64() : 0;
        DateTimeOffset? resets = w.TryGetProperty("reset_at", out var r) && r.ValueKind == JsonValueKind.Number
            ? DateTimeOffset.FromUnixTimeSeconds(r.GetInt64())
            : w.TryGetProperty("reset_after_seconds", out var ra) && ra.ValueKind == JsonValueKind.Number
                ? DateTimeOffset.UtcNow.AddSeconds(ra.GetInt64())
                : null;
        return new UsageWindow(WindowName((int)(seconds / 60), key), p.GetDouble(), resets);
    }

    private static DateTimeOffset? JwtExpiry(string jwt)
    {
        try
        {
            var parts = jwt.Split('.');
            if (parts.Length < 2) return null;
            var b64 = parts[1].Replace('-', '+').Replace('_', '/');
            b64 = b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '=');
            using var doc = JsonDocument.Parse(Convert.FromBase64String(b64));
            return doc.RootElement.TryGetProperty("exp", out var exp) && exp.ValueKind == JsonValueKind.Number
                ? DateTimeOffset.FromUnixTimeSeconds(exp.GetInt64())
                : null;
        }
        catch (Exception e) when (e is FormatException or JsonException)
        {
            return null;
        }
    }

    // ---------- session-log fallback ----------

    private static ProviderSnapshot ReadLogs(CancellationToken ct)
    {
        if (!Directory.Exists(SessionsDir)) return ProviderSnapshot.Missing("No Codex sessions found");

        var files = new DirectoryInfo(SessionsDir)
            .EnumerateFiles("*.jsonl", SearchOption.AllDirectories)
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .Take(5);

        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            var line = LastRateLimitLine(file.FullName);
            if (line is null) continue;

            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            var rl = root.GetProperty("payload").GetProperty("rate_limits");
            var loggedAt = root.TryGetProperty("timestamp", out var ts) &&
                           DateTimeOffset.TryParse(ts.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)
                ? t
                : new DateTimeOffset(file.LastWriteTimeUtc);

            var details = new List<DetailLine>();
            if (Str(rl, "plan_type") is { } plan) details.Add(new DetailLine("Plan", Title(plan)));
            if (Obj(rl, "credits") is { } credits && Str(credits, "balance") is { } b &&
                double.TryParse(b, NumberStyles.Float, CultureInfo.InvariantCulture, out var balance))
            {
                details.Add(new DetailLine("Credits", balance.ToString("N0", CultureInfo.CurrentCulture)));
            }
            details.Add(new DetailLine("Last activity", Format.Ago(loggedAt)));

            return new ProviderSnapshot
            {
                Session = LogWindow(rl, "primary"),
                Weekly = LogWindow(rl, "secondary"),
                Details = details,
                UpdatedAt = loggedAt,
            };
        }

        return ProviderSnapshot.Failed("No rate limit data in recent Codex sessions");
    }

    private static UsageWindow? LogWindow(JsonElement rl, string key)
    {
        if (Obj(rl, key) is not { } w) return null;
        if (!w.TryGetProperty("used_percent", out var p) || p.ValueKind != JsonValueKind.Number) return null;
        var minutes = w.TryGetProperty("window_minutes", out var wm) && wm.ValueKind == JsonValueKind.Number ? wm.GetInt32() : 0;
        DateTimeOffset? resets = w.TryGetProperty("resets_at", out var r) && r.ValueKind == JsonValueKind.Number
            ? DateTimeOffset.FromUnixTimeSeconds(r.GetInt64())
            : null;
        var percent = p.GetDouble();

        // The log is only written while Codex runs. Once a window has rolled
        // over since the last entry, its usage is back to zero.
        if (resets is { } at && at < DateTimeOffset.Now)
        {
            percent = 0;
            resets = null;
        }
        return new UsageWindow(WindowName(minutes, key), percent, resets);
    }

    private static string? LastRateLimitLine(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var start = Math.Max(0, fs.Length - TailBytes);
        fs.Seek(start, SeekOrigin.Begin);
        var buffer = new byte[fs.Length - start];
        fs.ReadExactly(buffer);
        var lines = Encoding.UTF8.GetString(buffer).Split('\n');
        // The first line of a mid-file read is usually partial, so skip it.
        for (var i = lines.Length - 1; i >= (start > 0 ? 1 : 0); i--)
        {
            var line = lines[i];
            if (line.Contains("\"rate_limits\":{", StringComparison.Ordinal) &&
                line.Contains("\"primary\":{", StringComparison.Ordinal))
            {
                return line;
            }
        }
        return null;
    }

    // ---------- helpers ----------

    private static string WindowName(int minutes, string key) => minutes switch
    {
        300 => "5-hour window",
        10080 => "Weekly",
        >= 1440 => $"{minutes / 1440}-day window",
        > 0 => $"{minutes / 60}-hour window",
        _ => key,
    };

    private static JsonElement? Obj(JsonElement e, string key) =>
        e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Object ? v : null;

    private static string? Str(JsonElement e, string key) =>
        e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string Title(string s) => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(s);
}

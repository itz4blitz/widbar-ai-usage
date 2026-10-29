using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace WidBar.AiUsage;

// Claude plan limits: the same endpoint Claude Code's /usage reads, authorised
// with Claude Code's own OAuth token. We never refresh the token ourselves:
// rotating it here would sign Claude Code out. Claude Code refreshes it on use.
//
// Claude Code also caches the full /api/oauth/usage response in ~/.claude.json
// ("cachedUsageUtilization", with fetchedAtMs + accountUuid). We read that first:
// when it is fresh we skip the network entirely, and when the endpoint 429s or
// fails we show it instead of nothing.
public static class ClaudeSource
{
    private static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private static readonly string CredentialsPath = Path.Combine(Home, ".claude", ".credentials.json");
    private static readonly string ClaudeJsonPath = Path.Combine(Home, ".claude.json");

    // Claude Code's own cache younger than this is used instead of calling the endpoint.
    private static readonly TimeSpan FreshCache = TimeSpan.FromMinutes(4);

    // Older than this, Claude Code's cache is too stale to show even as a fallback.
    private static readonly TimeSpan MaxFallbackAge = TimeSpan.FromHours(6);

    // Windows already shown as Session/Weekly or handled explicitly below.
    private static readonly HashSet<string> HandledKeys =
    [
        "five_hour", "seven_day", "seven_day_opus", "seven_day_sonnet", "seven_day_oauth_apps", "seven_day_cowork",
        "extra_usage", "limits", "spend", "seven_day_breakdown", "weekly_scoped_shares", "pace",
        "member_dashboard_available",
    ];

    public static async Task<ProviderSnapshot> FetchAsync(CancellationToken ct)
    {
        string token;
        string? plan;
        try
        {
            using var creds = JsonDocument.Parse(await File.ReadAllTextAsync(CredentialsPath, ct));
            var oauth = creds.RootElement.GetProperty("claudeAiOauth");
            token = oauth.GetProperty("accessToken").GetString() ?? "";
            plan = PlanName(Str(oauth, "subscriptionType"), Str(oauth, "rateLimitTier"));
            if (oauth.TryGetProperty("expiresAt", out var exp) && exp.ValueKind == JsonValueKind.Number &&
                DateTimeOffset.FromUnixTimeMilliseconds(exp.GetInt64()) < DateTimeOffset.UtcNow)
            {
                return FromCache(plan, "Login expired. Run Claude Code to refresh it")
                       ?? ProviderSnapshot.Failed("Login expired. Run Claude Code to refresh it");
            }
        }
        catch (FileNotFoundException)
        {
            return ProviderSnapshot.Missing("Not signed in to Claude Code");
        }
        catch (DirectoryNotFoundException)
        {
            return ProviderSnapshot.Missing("Not signed in to Claude Code");
        }

        // Claude Code fetched this itself moments ago: no need to spend a request.
        var cached = ReadClaudeCodeCache();
        if (cached is { } c && DateTimeOffset.UtcNow - c.FetchedAt < FreshCache)
        {
            using (c.Doc) return Build(c.Doc.RootElement, plan, null);
        }
        cached?.Doc.Dispose();

        HttpResponseMessage resp;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.anthropic.com/api/oauth/usage");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            req.Headers.Add("anthropic-beta", "oauth-2025-04-20");
            req.Headers.UserAgent.ParseAdd("widbar-aiusage/1.0");
            resp = await Http.Client.SendAsync(req, ct);
        }
        catch (HttpRequestException ex)
        {
            return FromCache(plan, $"Offline: {ex.Message}") ?? ProviderSnapshot.Failed($"Offline: {ex.Message}");
        }

        using (resp)
        {
            if (resp.StatusCode == HttpStatusCode.Unauthorized)
            {
                return FromCache(plan, "Login expired. Run Claude Code to refresh it")
                       ?? ProviderSnapshot.Failed("Login expired. Run Claude Code to refresh it");
            }
            if (resp.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var wait = resp.Headers.RetryAfter?.Delta
                           ?? (resp.Headers.RetryAfter?.Date is { } until ? until - DateTimeOffset.UtcNow : null);
                // Keep the "Rate limited" prefix: the plugin keys its backoff on it.
                var error = wait is { } w && w > TimeSpan.FromSeconds(30)
                    ? $"Rate limited, retrying in {Format.Until(DateTimeOffset.Now + w)}"
                    : "Rate limited, retrying later";
                var fallback = FromCache(plan, error);
                if (fallback is not null)
                {
                    return new ProviderSnapshot
                    {
                        Session = fallback.Session,
                        Weekly = fallback.Weekly,
                        Details = fallback.Details,
                        UpdatedAt = fallback.UpdatedAt,
                        Error = error,
                        RetryAfter = wait,
                    };
                }
                return new ProviderSnapshot { Error = error, RetryAfter = wait };
            }
            if (!resp.IsSuccessStatusCode)
            {
                var error = $"Usage endpoint returned {(int)resp.StatusCode}";
                return FromCache(plan, error) ?? ProviderSnapshot.Failed(error);
            }

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            return Build(doc.RootElement, plan, null);
        }
    }

    // ---- Claude Code's local cache --------------------------------------

    private sealed record CachedUsage(JsonDocument Doc, DateTimeOffset FetchedAt);

    private static ProviderSnapshot? FromCache(string? plan, string error)
    {
        var cached = ReadClaudeCodeCache();
        if (cached is null) return null;
        using (cached.Doc)
        {
            if (DateTimeOffset.UtcNow - cached.FetchedAt > MaxFallbackAge) return null;
            return Build(cached.Doc.RootElement, plan, cached.FetchedAt) is { } s
                ? new ProviderSnapshot
                {
                    Session = s.Session, Weekly = s.Weekly, Details = s.Details,
                    UpdatedAt = cached.FetchedAt.ToLocalTime(), Error = error,
                }
                : null;
        }
    }

    // ~/.claude.json is large and rewritten often by Claude Code; read it shared and
    // treat any failure (mid-write, schema change) as "no cache".
    private static CachedUsage? ReadClaudeCodeCache()
    {
        try
        {
            if (!File.Exists(ClaudeJsonPath)) return null;
            using var fs = new FileStream(ClaudeJsonPath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var doc = JsonDocument.Parse(fs);
            var root = doc.RootElement;
            if (!root.TryGetProperty("cachedUsageUtilization", out var cu) || cu.ValueKind != JsonValueKind.Object) return null;
            if (!cu.TryGetProperty("utilization", out var util) || util.ValueKind != JsonValueKind.Object) return null;
            if (!cu.TryGetProperty("fetchedAtMs", out var at) || at.ValueKind != JsonValueKind.Number) return null;

            // Ignore a cache written for a different account than the one signed in now.
            var cacheAccount = Str(cu, "accountUuid");
            var currentAccount = root.TryGetProperty("oauthAccount", out var acct) && acct.ValueKind == JsonValueKind.Object
                ? Str(acct, "accountUuid")
                : null;
            if (cacheAccount is not null && currentAccount is not null && cacheAccount != currentAccount) return null;

            return new CachedUsage(JsonDocument.Parse(util.GetRawText()),
                DateTimeOffset.FromUnixTimeMilliseconds(at.GetInt64()));
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or InvalidOperationException)
        {
            return null;
        }
    }

    // ---- Response mapping -----------------------------------------------

    internal static ProviderSnapshot Build(JsonElement root, string? plan, DateTimeOffset? cachedAt)
    {
        var details = new List<DetailLine>();
        if (plan is not null) details.Add(new DetailLine("Plan", plan));

        var session = ParseWindow(root, "five_hour", "5-hour window");
        var weekly = ParseWindow(root, "seven_day", "Weekly · all models");

        // Newer unified view: limits[] with kind session / weekly_all / weekly_scoped (per model or surface).
        var sawScoped = false;
        if (root.TryGetProperty("limits", out var limits) && limits.ValueKind == JsonValueKind.Array)
        {
            foreach (var limit in limits.EnumerateArray())
            {
                if (limit.ValueKind != JsonValueKind.Object) continue;
                var kind = Str(limit, "kind");
                var pct = Num(limit, "percent");
                var resets = ParseIso(limit, "resets_at");
                if (pct is null) continue;
                switch (kind)
                {
                    case "session":
                        session ??= new UsageWindow("5-hour window", pct.Value, resets);
                        break;
                    case "weekly_all":
                        weekly ??= new UsageWindow("Weekly · all models", pct.Value, resets);
                        break;
                    default:
                        if (kind == "weekly_scoped") sawScoped = true;
                        details.Add(new DetailLine(
                            $"{GroupLabel(limit, kind)} · {ScopeLabel(limit) ?? "scoped"}",
                            $"{pct.Value:0}%{Format.ResetSuffix(resets)}"));
                        break;
                }
            }
        }

        // Legacy per-model weekly windows (pre-limits[] responses). Skip if limits[] already covered them.
        if (!sawScoped)
        {
            AddWindow(details, root, "seven_day_opus", "Weekly · Opus");
            AddWindow(details, root, "seven_day_sonnet", "Weekly · Sonnet");
        }
        AddWindow(details, root, "seven_day_oauth_apps", "Weekly · other apps");
        AddWindow(details, root, "seven_day_cowork", "Weekly · Cowork");

        // Any other window-shaped object (codenamed promos/credit grants like "iguana_necktie"):
        // show dollar grants as spend vs limit, plain windows as a percentage.
        foreach (var prop in root.EnumerateObject())
        {
            if (HandledKeys.Contains(prop.Name) || prop.Value.ValueKind != JsonValueKind.Object) continue;
            var w = prop.Value;
            var util = Num(w, "utilization");
            var limitUsd = Num(w, "limit_dollars");
            if (util is null && limitUsd is null) continue;
            var resets = ParseIso(w, "resets_at");
            var label = limitUsd is not null ? "Bonus credit" : Humanize(prop.Name);
            var value = limitUsd is { } lim
                ? $"{Format.Usd(Num(w, "used_dollars") ?? 0)} / {Format.Usd(lim)}{Format.ResetSuffix(resets)}"
                : $"{util:0}%{Format.ResetSuffix(resets)}";
            details.Add(new DetailLine(label, value));
            if (Str(w, "locked_reason") is { } locked) details.Add(new DetailLine($"{label} locked", locked));
        }

        // A window can be locked (e.g. weekly cap hit) even below 100%.
        foreach (var (key, label) in new[] { ("five_hour", "Session"), ("seven_day", "Weekly") })
        {
            if (root.TryGetProperty(key, out var w) && w.ValueKind == JsonValueKind.Object &&
                Str(w, "locked_reason") is { } locked)
            {
                details.Add(new DetailLine($"{label} locked", locked));
            }
        }

        // Extra usage (pay-as-you-go overage). Amounts are minor units; decimal_places says how many.
        if (root.TryGetProperty("extra_usage", out var extra) && extra.ValueKind == JsonValueKind.Object)
        {
            var enabled = extra.TryGetProperty("is_enabled", out var en) && en.ValueKind == JsonValueKind.True;
            if (enabled)
            {
                var scale = Math.Pow(10, Num(extra, "decimal_places") ?? 2);
                var used = Num(extra, "used_credits") is { } u ? u / scale : (double?)null;
                var cap = Num(extra, "monthly_limit") is { } m ? m / scale : (double?)null;
                var pct = Num(extra, "utilization");
                var value = (used, cap) switch
                {
                    ({ } a, { } b) => $"{Format.Usd(a)} / {Format.Usd(b)}" + (pct is { } p ? $" ({p:0}%)" : ""),
                    ({ } a, null) => $"{Format.Usd(a)} (no cap)",
                    _ => pct is { } p2 ? $"{p2:0}%" : "on",
                };
                if (extra.TryGetProperty("spend_limit_reached", out var hit) && hit.ValueKind == JsonValueKind.True)
                    value += " · limit reached";
                details.Add(new DetailLine("Extra usage", value));
            }
        }

        // Usage credits balance ("spend"): prepaid credits that cover you past plan limits.
        if (root.TryGetProperty("spend", out var spend) && spend.ValueKind == JsonValueKind.Object &&
            spend.TryGetProperty("enabled", out var spendOn) && spendOn.ValueKind == JsonValueKind.True)
        {
            var used = Money(spend, "used");
            var lim = Money(spend, "limit");
            var bal = Money(spend, "balance");
            var parts = new List<string>();
            if (used is not null) parts.Add(lim is not null ? $"{Format.Usd(used.Value)} / {Format.Usd(lim.Value)}" : $"{Format.Usd(used.Value)} used");
            if (bal is not null) parts.Add($"{Format.Usd(bal.Value)} left");
            if (parts.Count > 0) details.Add(new DetailLine("Usage credits", string.Join(" · ", parts)));
        }

        // Where this week's usage went (Claude Code vs chat vs Cowork).
        if (root.TryGetProperty("seven_day_breakdown", out var bd) && bd.ValueKind == JsonValueKind.Object &&
            bd.TryGetProperty("rows", out var rows) && rows.ValueKind == JsonValueKind.Array)
        {
            var shares = rows.EnumerateArray()
                .Select(r => (Name: Str(r, "display_name") ?? Str(r, "key") ?? "?", Pct: Num(r, "percent") ?? 0))
                .Where(r => r.Pct >= 1)
                .OrderByDescending(r => r.Pct)
                .Select(r => $"{r.Name} {r.Pct:0}%")
                .ToList();
            if (shares.Count > 0) details.Add(new DetailLine("By app (7 days)", string.Join(", ", shares)));
        }

        if (cachedAt is { } at) details.Add(new DetailLine("Cached", $"from Claude Code, {Format.Ago(at.ToLocalTime())}"));

        return new ProviderSnapshot { Session = session, Weekly = weekly, Details = details };
    }

    private static void AddWindow(List<DetailLine> details, JsonElement root, string key, string label)
    {
        if (ParseWindow(root, key, label) is { } w)
            details.Add(new DetailLine(label, $"{w.Percent:0}%{Format.ResetSuffix(w.ResetsAt)}"));
    }

    private static UsageWindow? ParseWindow(JsonElement root, string key, string name)
    {
        if (!root.TryGetProperty(key, out var w) || w.ValueKind != JsonValueKind.Object) return null;
        return Num(w, "utilization") is { } u ? new UsageWindow(name, u, ParseIso(w, "resets_at")) : null;
    }

    private static string GroupLabel(JsonElement limit, string? kind) =>
        Str(limit, "group") switch
        {
            "weekly" => "Weekly",
            "session" => "Session",
            { } g => Humanize(g),
            null => Humanize(kind ?? "limit"),
        };

    private static string? ScopeLabel(JsonElement limit)
    {
        if (!limit.TryGetProperty("scope", out var scope) || scope.ValueKind != JsonValueKind.Object) return null;
        if (scope.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.Object &&
            (Str(m, "display_name") ?? Str(m, "id")) is { } model)
        {
            return model;
        }
        if (scope.TryGetProperty("surface", out var s))
        {
            if (s.ValueKind == JsonValueKind.String) return s.GetString();
            if (s.ValueKind == JsonValueKind.Object) return Str(s, "display_name") ?? Str(s, "id");
        }
        return null;
    }

    // "max" + "default_claude_max_20x" -> "Max 20x"; "pro" -> "Pro".
    private static string? PlanName(string? subscription, string? tier)
    {
        if (string.IsNullOrEmpty(subscription)) return null;
        var name = char.ToUpperInvariant(subscription[0]) + subscription[1..];
        var mult = tier is null ? null : System.Text.RegularExpressions.Regex.Match(tier, @"(\d+x)$").Groups[1].Value;
        return string.IsNullOrEmpty(mult) ? name : $"{name} {mult}";
    }

    // { amount_minor, currency, exponent } -> major units.
    private static double? Money(JsonElement parent, string key)
    {
        if (!parent.TryGetProperty(key, out var m) || m.ValueKind != JsonValueKind.Object) return null;
        if (Num(m, "amount_minor") is not { } minor) return null;
        return minor / Math.Pow(10, Num(m, "exponent") ?? 2);
    }

    private static string Humanize(string key)
    {
        var s = key.Replace('_', ' ');
        return s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
    }

    private static string? Str(JsonElement e, string key) =>
        e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static double? Num(JsonElement e, string key) =>
        e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    private static DateTimeOffset? ParseIso(JsonElement e, string key) =>
        e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String &&
        DateTimeOffset.TryParse(v.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d
            : null;
}

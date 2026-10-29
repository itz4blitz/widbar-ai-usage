using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace WidBar.AiUsage;

// Cursor plan usage from cursor.com's dashboard API (the same calls the
// cursor.com/dashboard usage page makes, also used by CodexBar).
//
// Auth: the Cursor IDE's access token (a WorkOS JWT) from state.vscdb, or the
// one `cursor-agent login` keeps in auth.json. Sent as the dashboard session
// cookie "WorkosCursorSessionToken=<userId>%3A%3A<jwt>". Read-only: we never
// refresh, rotate or write the token; Cursor refreshes it itself on use.
//
// GET /api/usage-summary returns (amounts in cents, percents already 0-100):
//   billingCycleStart/End (ISO), membershipType ("pro", "pro_plus", "ultra",
//   "free", "unified_*"...), limitType, isUnlimited,
//   individualUsage.plan { used, limit, remaining, totalPercentUsed,
//     autoPercentUsed, apiPercentUsed, breakdown { included, bonus, total } },
//   individualUsage.onDemand { enabled, used, limit, remaining },
//   individualUsage.overall { used, limit }  (enterprise/team members),
//   teamUsage.pooled / teamUsage.onDemand.
public static class CursorSource
{
    private const string Base = "https://cursor.com";

    private static readonly string KeyPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "widbar-aiusage", "cursor.key");

    private static readonly byte[] KeyEntropy = "widbar-aiusage/cursor"u8.ToArray();

    private static readonly string Roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    private static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private static readonly string[] AppDirs =
    [
        Path.Combine(Roaming, "Cursor"),
        Path.Combine(Roaming, "Cursor Nightly"),
    ];

    private static IEnumerable<string> AgentAuthFiles()
    {
        if (Environment.GetEnvironmentVariable("CURSOR_CONFIG_DIR") is { Length: > 0 } dir) yield return Path.Combine(dir, "auth.json");
        yield return Path.Combine(Home, ".cursor", "auth.json");
        yield return Path.Combine(Roaming, "Cursor", "auth.json");
        yield return Path.Combine(Home, ".config", "cursor", "auth.json");
    }

    public static async Task<ProviderSnapshot> FetchAsync(CancellationToken ct)
    {
        var (token, found) = await Task.Run(FindToken, ct);
        if (token is null)
        {
            return ProviderSnapshot.Missing(found
                ? "Not signed in to Cursor"
                : "No Cursor login on this PC. Paste a session token in settings");
        }

        string cookie = "";
        string? sessionUser = null;
        if (SplitSession(token, out sessionUser, out var sessionJwt))
        {
            token = sessionJwt;
            cookie = $"WorkosCursorSessionToken={sessionUser}%3A%3A{sessionJwt}";
        }

        var claims = JwtClaims(token);
        var userId = sessionUser ?? (claims is { } c && c.TryGetProperty("sub", out var sub) ? sub.GetString()?.Split('|').Last() : null);
        if (string.IsNullOrEmpty(userId) || !userId.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '.' or '_' or '-'))
        {
            return ProviderSnapshot.Failed("Unrecognised Cursor login");
        }
        if (claims is { } cl && cl.TryGetProperty("exp", out var exp) && exp.ValueKind == JsonValueKind.Number &&
            DateTimeOffset.FromUnixTimeSeconds(exp.GetInt64()) < DateTimeOffset.UtcNow)
        {
            return ProviderSnapshot.Failed("Cursor login expired. Sign in again, or paste a fresh session token in settings");
        }

        if (cookie.Length == 0) cookie = $"WorkosCursorSessionToken={userId}%3A%3A{token}";

        using var resp = await Send(HttpMethod.Get, "/api/usage-summary", cookie, ct);
        if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            return ProviderSnapshot.Failed("Cursor login expired. Sign in again, or paste a fresh session token in settings");
        }
        if (resp.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var wait = resp.Headers.RetryAfter?.Delta
                       ?? (resp.Headers.RetryAfter?.Date is { } until ? until - DateTimeOffset.UtcNow : null);
            return new ProviderSnapshot
            {
                Error = wait is { } w && w > TimeSpan.FromSeconds(30) ? $"Rate limited, retrying in {Format.Until(DateTimeOffset.Now + w)}" : "Rate limited, retrying later",
                RetryAfter = wait ?? TimeSpan.FromMinutes(5),
            };
        }
        if (!resp.IsSuccessStatusCode) return ProviderSnapshot.Failed($"Cursor API error: HTTP {(int)resp.StatusCode}");

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;
        var cycleEnd = Iso(root, "billingCycleEnd");
        var details = new List<DetailLine>();

        var membership = Str(root, "membershipType");
        if (membership is { Length: > 0 })
        {
            details.Add(new DetailLine("Plan", CultureInfo.InvariantCulture.TextInfo.ToTitleCase(membership.Replace('_', ' '))));
        }

        double? percent = null;
        var individual = Obj(root, "individualUsage");
        var plan = individual is { } iu ? Obj(iu, "plan") : null;
        if (plan is { } p)
        {
            var used = Num(p, "used");
            var limit = Num(p, "limit");
            var auto = Num(p, "autoPercentUsed");
            var api = Num(p, "apiPercentUsed");
            percent = Num(p, "totalPercentUsed")
                      ?? (auto is { } a && api is { } b ? (a + b) / 2 : api ?? auto)
                      ?? (used is { } u && limit is > 0 ? u / limit.Value * 100 : null);
            if (used is { } u2 && limit is > 0)
            {
                details.Add(new DetailLine("Included usage", $"{Format.Usd(u2 / 100)} / {Format.Usd(limit.Value / 100)}"));
            }
            if (auto is { } a2) details.Add(new DetailLine("Auto + Composer", $"{a2:0.#}%"));
            if (api is { } b2) details.Add(new DetailLine("API models", $"{b2:0.#}%"));
        }

        // Enterprise/team members get a personal cap instead of a plan block; fall back to the team pool.
        if (percent is null && individual is { } iu2 && Obj(iu2, "overall") is { } overall && Num(overall, "limit") is > 0 and var ol)
        {
            var used = Num(overall, "used") ?? 0;
            percent = used / ol * 100;
            details.Add(new DetailLine("Usage", $"{Format.Usd(used / 100)} / {Format.Usd(ol / 100)}"));
        }
        if (percent is null && Obj(root, "teamUsage") is { } team && Obj(team, "pooled") is { } pooled && Num(pooled, "limit") is > 0 and var pl)
        {
            var used = Num(pooled, "used") ?? 0;
            percent = used / pl * 100;
            details.Add(new DetailLine("Team pool", $"{Format.Usd(used / 100)} / {Format.Usd(pl / 100)}"));
        }

        if (individual is { } iu3 && Obj(iu3, "onDemand") is { } od && Num(od, "used") is { } odUsed && odUsed > 0)
        {
            var odLimit = Num(od, "limit");
            details.Add(new DetailLine("On-demand",
                odLimit is > 0 ? $"{Format.Usd(odUsed / 100)} / {Format.Usd(odLimit.Value / 100)}" : Format.Usd(odUsed / 100)));
        }

        // Unified Cursor/Grok billing can report a false zero above; its v2 status is authoritative.
        if (membership?.StartsWith("unified_", StringComparison.OrdinalIgnoreCase) == true &&
            string.Equals(Str(root, "limitType"), "user", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                using var u = await Send(HttpMethod.Get, "/api/dashboard/v2/usage-status?scope=user", cookie, ct);
                if (u.IsSuccessStatusCode)
                {
                    using var ud = JsonDocument.Parse(await u.Content.ReadAsStringAsync(ct));
                    if (Str(ud.RootElement, "availability") == "available" && Obj(ud.RootElement, "monthlyUsage") is { } m &&
                        Num(m, "usedCents") is { } uc && Num(m, "limitCents") is > 0 and var lc)
                    {
                        percent = uc / lc * 100;
                        cycleEnd = Iso(m, "resetsAt") ?? cycleEnd;
                        details.Add(new DetailLine("Monthly usage", $"{Format.Usd(uc / 100)} / {Format.Usd(lc / 100)}"));
                    }
                }
            }
            catch (Exception e) when (e is HttpRequestException or JsonException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                // Optional; keep the summary figures.
            }
        }

        // Legacy request-based plans (e.g. 500 fast requests/month) only show up in /api/usage.
        if (percent is null)
        {
            try
            {
                using var r = await Send(HttpMethod.Get, $"/api/usage?user={Uri.EscapeDataString(userId)}", cookie, ct);
                if (r.IsSuccessStatusCode)
                {
                    using var rd = JsonDocument.Parse(await r.Content.ReadAsStringAsync(ct));
                    if (Obj(rd.RootElement, "gpt-4") is { } g && Num(g, "maxRequestUsage") is > 0 and var max)
                    {
                        var n = Num(g, "numRequestsTotal") ?? Num(g, "numRequests") ?? 0;
                        percent = n / max * 100;
                        details.Add(new DetailLine("Requests", $"{n:0} / {max:0}"));
                    }
                }
            }
            catch (Exception e) when (e is HttpRequestException or JsonException or TaskCanceledException && !ct.IsCancellationRequested)
            {
            }
        }

        if (root.TryGetProperty("isUnlimited", out var unl) && unl.ValueKind == JsonValueKind.True)
        {
            details.Add(new DetailLine("Limit", "Unlimited"));
        }
        if (cycleEnd is { } end) details.Add(new DetailLine("Cycle resets", end.ToLocalTime().ToString("MMM d", CultureInfo.CurrentCulture)));

        if (percent is null && details.Count == 0) return ProviderSnapshot.Failed("Cursor returned no usage data");

        return new ProviderSnapshot
        {
            Weekly = percent is { } pct ? new UsageWindow("Billing month", Math.Clamp(pct, 0, 100), cycleEnd) : null,
            Details = details,
        };
    }

    private static async Task<HttpResponseMessage> Send(HttpMethod method, string path, string cookie, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(method, Base + path);
        req.Headers.Add("Cookie", cookie);
        req.Headers.Accept.ParseAdd("application/json");
        req.Headers.Add("Origin", Base);
        req.Headers.Referrer = new Uri(Base + "/dashboard");
        req.Headers.UserAgent.ParseAdd("widbar-aiusage/1.0");
        return await Http.Client.SendAsync(req, ct);
    }

    public static bool HasKey => ReadSavedKey() is not null;

    public static void SaveKey(string key)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(KeyPath)!);
        var blob = ProtectedData.Protect(Encoding.UTF8.GetBytes(key.Trim()), KeyEntropy, DataProtectionScope.CurrentUser);
        File.WriteAllText(KeyPath, Convert.ToBase64String(blob));
    }

    private static string? ReadSavedKey()
    {
        if (!File.Exists(KeyPath)) return null;
        try
        {
            var blob = Convert.FromBase64String(File.ReadAllText(KeyPath).Trim());
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(blob, KeyEntropy, DataProtectionScope.CurrentUser));
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or IOException)
        {
            return null;
        }
    }

    // A pasted dashboard cookie is "userId::jwt" (or the same value percent-encoded).
    private static bool SplitSession(string stored, out string? userId, out string jwt)
    {
        userId = null;
        jwt = stored;
        var text = Uri.UnescapeDataString(stored.Trim());
        const string prefix = "WorkosCursorSessionToken=";
        if (text.StartsWith(prefix, StringComparison.Ordinal)) text = text[prefix.Length..];
        var split = text.Split("::", 2, StringSplitOptions.None);
        if (split.Length != 2 || split[0].Length == 0 || split[1].Length == 0) return false;
        userId = split[0];
        jwt = split[1];
        return true;
    }

    // Returns the token (or null) and whether any Cursor install/login store exists at all.
    private static (string? Token, bool Found) FindToken()
    {
        if (ReadSavedKey() is { Length: > 0 } saved) return (saved, true);
        var found = false;
        foreach (var file in AgentAuthFiles())
        {
            if (!File.Exists(file)) continue;
            found = true;
            try
            {
                using var j = JsonDocument.Parse(File.ReadAllText(file));
                if (Str(j.RootElement, "accessToken") is { Length: > 0 } t) return (t.Trim(), true);
            }
            catch (Exception e) when (e is IOException or JsonException)
            {
            }
        }

        foreach (var dir in AppDirs)
        {
            var db = Path.Combine(dir, "User", "globalStorage", "state.vscdb");
            if (!File.Exists(db)) continue;
            found = true;
            string? value;
            try
            {
                using var conn = new SqliteConnection($"Data Source={db};Mode=ReadOnly");
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT value FROM ItemTable WHERE key = 'cursorAuth/accessToken' LIMIT 1";
                value = cmd.ExecuteScalar() switch
                {
                    string s => s,
                    byte[] b => Encoding.UTF8.GetString(b),
                    _ => null,
                };
            }
            catch (SqliteException)
            {
                continue;
            }
            value = value?.Trim().Trim('"');
            if (string.IsNullOrEmpty(value)) continue;

            // Newer Cursor builds store it with Electron safeStorage: "cursor-safe-storage:v1:" + base64("v10" + AES-GCM).
            const string prefix = "cursor-safe-storage:v1:";
            if (value.StartsWith(prefix, StringComparison.Ordinal))
            {
                value = DecryptSafeStorage(dir, value[prefix.Length..]);
                if (value is null) continue;
            }
            return (value, true);
        }
        return (null, found);
    }

    // Electron safeStorage on Windows: AES-256-GCM, key = DPAPI(Local State os_crypt.encrypted_key minus "DPAPI").
    private static string? DecryptSafeStorage(string appDir, string b64)
    {
        try
        {
            var blob = Convert.FromBase64String(b64);
            if (blob.Length < 3 + 12 + 16 || Encoding.ASCII.GetString(blob, 0, 3) != "v10") return null;

            using var state = JsonDocument.Parse(File.ReadAllText(Path.Combine(appDir, "Local State")));
            var wrapped = Convert.FromBase64String(state.RootElement.GetProperty("os_crypt").GetProperty("encrypted_key").GetString()!);
            if (Encoding.ASCII.GetString(wrapped, 0, 5) != "DPAPI") return null;
            var key = ProtectedData.Unprotect(wrapped[5..], null, DataProtectionScope.CurrentUser);

            var nonce = blob.AsSpan(3, 12);
            var tag = blob.AsSpan(blob.Length - 16);
            var cipher = blob.AsSpan(15, blob.Length - 15 - 16);
            var plain = new byte[cipher.Length];
            using var gcm = new AesGcm(key, 16);
            gcm.Decrypt(nonce, cipher, tag, plain);
            return Encoding.UTF8.GetString(plain).Trim().Trim('"');
        }
        catch (Exception e) when (e is IOException or JsonException or FormatException or CryptographicException
                                      or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }

    private static JsonElement? JwtClaims(string jwt)
    {
        var parts = jwt.Split('.');
        if (parts.Length < 2) return null;
        try
        {
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            using var doc = JsonDocument.Parse(Convert.FromBase64String(payload));
            return doc.RootElement.Clone();
        }
        catch (Exception e) when (e is FormatException or JsonException)
        {
            return null;
        }
    }

    private static JsonElement? Obj(JsonElement e, string key) =>
        e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Object ? v : null;

    private static string? Str(JsonElement e, string key) =>
        e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static double? Num(JsonElement e, string key) =>
        e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    private static DateTimeOffset? Iso(JsonElement e, string key) =>
        Str(e, key) is { } s && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
}

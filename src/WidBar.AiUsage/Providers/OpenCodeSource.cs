using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace WidBar.AiUsage;

// OpenCode (anomalyco/opencode). Two sources, best first:
//
// 1. OpenCode Go plan limits: GET https://opencode.ai/zen/go/v1/usage with the
//    OpenCode Console credential as a Bearer token (the same call OpenCode's own
//    packages/core/src/usage-limits.ts makes). Response:
//      { "usage": { "rolling": {status, percent, resetsAt},
//                   "weekly":  {status, percent, resetsAt},
//                   "monthly": {status, percent, resetsAt} } }
//    status is "ok" | "rate-limited"; 403 means the workspace has no Go plan.
//    Go limits are dollar caps: $12 / 5h, $30 / week, $60 / month.
//
// 2. Spend from local message history (every assistant message records its
//    cost), for Zen pay-as-you-go and BYO-key providers.
//
// Credentials: OpenCode v2 keeps them in the "credential" table of
// ~/.local/share/opencode/opencode*.db (connector_id = 'opencode', value is JSON
// {type:"key",key,metadata:{orgID,server}} or {type:"oauth",access,expires,...}).
// Older versions use ~/.local/share/opencode/auth.json ({"opencode":{"type":"api","key":...}}).
// We only read them; an expired OAuth token is reported, never refreshed.
public static class OpenCodeSource
{
    private const string GoUsageUrl = "https://opencode.ai/zen/go/v1/usage";

    private static readonly string DataDir = Path.Combine(
        Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } xdg
            ? xdg
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share"),
        "opencode");

    private sealed record Credential(string Token, string? OrgId, DateTimeOffset? Expires);

    public static async Task<ProviderSnapshot> FetchAsync(CancellationToken ct)
    {
        var envKey = Environment.GetEnvironmentVariable("OPENCODE_API_KEY");
        if (!Directory.Exists(DataDir) && string.IsNullOrEmpty(envKey))
        {
            return ProviderSnapshot.Missing("OpenCode is not installed");
        }

        var databases = Directory.Exists(DataDir)
            ? Directory.GetFiles(DataDir, "opencode*.db").OrderByDescending(File.GetLastWriteTimeUtc).ToArray()
            : [];

        // Local spend runs off the UI thread; the limit call runs alongside it.
        var spendTask = Task.Run(() => ReadSpend(databases, ct), ct);

        UsageWindow? session = null, weekly = null;
        var details = new List<DetailLine>();
        string? limitError = null;

        var cred = ReadCredential(databases)
                   ?? (envKey is { Length: > 0 } ? new Credential(envKey, null, null) : null)
                   ?? (ReadSavedKey() is { } saved ? new Credential(saved, null, null) : null);
        if (cred is not null)
        {
            if (cred.Expires is { } exp && exp < DateTimeOffset.UtcNow)
            {
                limitError = "OpenCode login expired. Open OpenCode to refresh it";
            }
            else
            {
                try
                {
                    (session, weekly, limitError) = await FetchGoAsync(cred, details, ct);
                }
                catch (HttpRequestException ex)
                {
                    limitError = $"OpenCode Go usage unavailable: {ex.Message}";
                }
                catch (JsonException)
                {
                    limitError = "OpenCode Go returned an unrecognised response";
                }
            }
        }

        SpendTally? tally;
        try
        {
            tally = await spendTask;
        }
        catch (SqliteException)
        {
            tally = null;
        }

        if (session is not null || weekly is not null)
        {
            details.Insert(0, new DetailLine("Plan", "OpenCode Go"));
            if (tally?.LastActivity is not null)
            {
                details.Add(new DetailLine("Spend today", Format.Usd(tally.Today)));
                details.Add(new DetailLine("Last 7 days", Format.Usd(tally.Week)));
                details.Add(new DetailLine("Last activity", Format.Ago(tally.LastActivity.Value)));
            }
            return new ProviderSnapshot { Session = session, Weekly = weekly, Details = details };
        }

        if (tally?.LastActivity is not null)
        {
            var snap = tally.ToSnapshot("As reported by OpenCode's providers");
            if (limitError is not null) snap.Details.Add(new DetailLine("Go limits", limitError));
            return snap;
        }

        if (limitError is not null) return ProviderSnapshot.Failed(limitError);
        return cred is null && databases.Length == 0
            ? ProviderSnapshot.Missing("OpenCode is not set up")
            : ProviderSnapshot.Missing("No OpenCode usage recorded yet");
    }

    private static async Task<(UsageWindow?, UsageWindow?, string?)> FetchGoAsync(
        Credential cred, List<DetailLine> details, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, GoUsageUrl);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", cred.Token);
        req.Headers.Accept.ParseAdd("application/json");
        if (cred.OrgId is { Length: > 0 } org) req.Headers.Add("x-opencode-org-id", org);
        req.Headers.UserAgent.ParseAdd("widbar-aiusage/1.0");

        using var resp = await Http.Client.SendAsync(req, ct);
        // 403: signed in, but this workspace has no Go subscription (Zen / BYO only).
        if (resp.StatusCode == HttpStatusCode.Forbidden) return (null, null, null);
        if (resp.StatusCode == HttpStatusCode.Unauthorized)
        {
            return (null, null, "OpenCode key rejected. Run `opencode auth login` again");
        }
        if (resp.StatusCode == HttpStatusCode.TooManyRequests) return (null, null, "Rate limited, retrying later");
        resp.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        if (!doc.RootElement.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
        {
            return (null, null, "OpenCode Go returned no usage data");
        }

        var rolling = Meter(usage, "rolling", "5-hour", details);
        var week = Meter(usage, "weekly", "Weekly", details);
        var month = Meter(usage, "monthly", "Monthly", details);

        // Session = 5h window. The second tile shows whichever of week/month is
        // closer to its cap, since either one blocks requests.
        var second = week is null ? month
            : month is null ? week
            : month.Percent > week.Percent ? month : week;
        return (rolling, second, null);
    }

    private static UsageWindow? Meter(JsonElement usage, string key, string name, List<DetailLine> details)
    {
        if (!usage.TryGetProperty(key, out var m) || m.ValueKind != JsonValueKind.Object) return null;
        if (!m.TryGetProperty("percent", out var p) || p.ValueKind != JsonValueKind.Number) return null;

        var percent = Math.Clamp(p.GetDouble(), 0, 100);
        DateTimeOffset? resets = m.TryGetProperty("resetsAt", out var r) && r.ValueKind == JsonValueKind.String &&
                                 DateTimeOffset.TryParse(r.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)
            ? at
            : null;
        var limited = m.TryGetProperty("status", out var s) && s.GetString() == "rate-limited";

        details.Add(new DetailLine($"{name} limit",
            $"{percent:0}%{(limited ? " (limit reached)" : "")}{Format.ResetSuffix(resets)}"));
        return new UsageWindow(name, percent, resets);
    }

    // ---- credentials (read-only) ----

    private static readonly string KeyPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "widbar-aiusage", "opencode.key");

    private static readonly byte[] KeyEntropy = "widbar-aiusage/opencode"u8.ToArray();

    public static bool HasKey => ReadSavedKey() is not null;

    public static void SaveKey(string key)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(KeyPath)!);
        var blob = System.Security.Cryptography.ProtectedData.Protect(
            System.Text.Encoding.UTF8.GetBytes(key.Trim()), KeyEntropy, System.Security.Cryptography.DataProtectionScope.CurrentUser);
        File.WriteAllText(KeyPath, Convert.ToBase64String(blob));
    }

    private static string? ReadSavedKey()
    {
        if (!File.Exists(KeyPath)) return null;
        try
        {
            var blob = Convert.FromBase64String(File.ReadAllText(KeyPath).Trim());
            return System.Text.Encoding.UTF8.GetString(
                System.Security.Cryptography.ProtectedData.Unprotect(blob, KeyEntropy, System.Security.Cryptography.DataProtectionScope.CurrentUser));
        }
        catch (Exception ex) when (ex is FormatException or System.Security.Cryptography.CryptographicException or IOException)
        {
            return null;
        }
    }

    // OpenCode's current login lives in the account table. Older builds used credential.
    private static Credential? ReadAccount(SqliteConnection conn)
    {
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT access_token, token_expiry FROM account ORDER BY time_updated DESC LIMIT 1";
            using var reader = cmd.ExecuteReader();
            if (!reader.Read() || reader.IsDBNull(0)) return null;
            var token = reader.GetString(0);
            if (string.IsNullOrEmpty(token)) return null;
            DateTimeOffset? exp = null;
            if (!reader.IsDBNull(1) && reader.GetInt64(1) is > 0 and var raw)
            {
                exp = raw > 10_000_000_000
                    ? DateTimeOffset.FromUnixTimeMilliseconds(raw)
                    : DateTimeOffset.FromUnixTimeSeconds(raw);
            }
            return new Credential(token, null, exp);
        }
        catch (SqliteException)
        {
            return null;
        }
    }

    private static Credential? ReadCredential(string[] databases)
    {
        foreach (var db in databases)
        {
            try
            {
                using var conn = new SqliteConnection($"Data Source={db};Mode=ReadOnly");
                conn.Open();
                if (ReadAccount(conn) is { } account) return account;
                using var cmd = conn.CreateCommand();
                cmd.CommandText =
                    "SELECT value FROM credential WHERE connector_id IN ('opencode','opencode-go') " +
                    "ORDER BY active DESC, time_updated DESC";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    if (ParseCredential(reader.GetString(0)) is { } c) return c;
                }
            }
            catch (SqliteException)
            {
                // Pre-v2 database without a credential table.
            }
        }

        var authJson = Path.Combine(DataDir, "auth.json");
        if (!File.Exists(authJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(authJson));
            foreach (var id in new[] { "opencode-go", "opencode" })
            {
                if (doc.RootElement.TryGetProperty(id, out var entry) && ParseCredential(entry.GetRawText()) is { } c)
                {
                    return c;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
        }
        return null;
    }

    private static Credential? ParseCredential(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var v = doc.RootElement;
            var type = v.TryGetProperty("type", out var t) ? t.GetString() : null;
            string? orgId = v.TryGetProperty("metadata", out var md) && md.ValueKind == JsonValueKind.Object &&
                            md.TryGetProperty("orgID", out var o) && o.ValueKind == JsonValueKind.String
                ? o.GetString()
                : null;

            switch (type)
            {
                case "key" or "api" when v.TryGetProperty("key", out var k) && k.GetString() is { Length: > 0 } key:
                    return new Credential(key, orgId, null);
                case "oauth" when v.TryGetProperty("access", out var a) && a.GetString() is { Length: > 0 } access:
                    DateTimeOffset? exp = v.TryGetProperty("expires", out var e) && e.ValueKind == JsonValueKind.Number &&
                                          e.GetInt64() > 0
                        ? DateTimeOffset.FromUnixTimeMilliseconds(e.GetInt64())
                        : null;
                    return new Credential(access, orgId, exp);
                default:
                    return null;
            }
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // ---- local spend history ----

    private static SpendTally ReadSpend(string[] databases, CancellationToken ct)
    {
        var tally = new SpendTally();
        foreach (var db in databases)
        {
            try
            {
                using var conn = new SqliteConnection($"Data Source={db};Mode=ReadOnly");
                conn.Open();
                using var cmd = conn.CreateCommand();
                // Current OpenCode stores history in session_message. Older builds used message.
                cmd.CommandText = "SELECT type, data FROM session_message";
                try
                {
                    using var reader = cmd.ExecuteReader();
                    while (reader.Read())
                    {
                        ct.ThrowIfCancellationRequested();
                        if (!reader.IsDBNull(1)) AddMessage(tally, reader.GetString(1), reader.IsDBNull(0) ? null : reader.GetString(0));
                    }
                }
                catch (SqliteException)
                {
                    cmd.CommandText = "SELECT data FROM message";
                    using var reader = cmd.ExecuteReader();
                    while (reader.Read())
                    {
                        ct.ThrowIfCancellationRequested();
                        if (!reader.IsDBNull(0)) AddMessage(tally, reader.GetString(0));
                    }
                }
            }
            catch (SqliteException)
            {
                // Database without either history table.
            }
        }

        var messages = Path.Combine(DataDir, "storage", "message");
        if (Directory.Exists(messages))
        {
            foreach (var file in new DirectoryInfo(messages).EnumerateFiles("*.json", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();
                if (file.LastWriteTime < tally.Since) continue;
                try
                {
                    AddMessage(tally, File.ReadAllText(file.FullName));
                }
                catch (IOException)
                {
                }
            }
        }
        return tally;
    }

    private static void AddMessage(SpendTally tally, string json, string? type = null)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var m = doc.RootElement;
            var role = type;
            if (role is null && m.TryGetProperty("role", out var roleEl)) role = roleEl.GetString();
            if (role is not null && role != "assistant") return;
            if (role is null && !m.TryGetProperty("cost", out _) && !m.TryGetProperty("tokens", out _)) return;
            if (m.TryGetProperty("model", out var model))
            {
                if (model.ValueKind == JsonValueKind.Object)
                {
                    var id = model.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
                    var provider = model.TryGetProperty("providerID", out var providerEl) ? providerEl.GetString() : null;
                    tally.NoteModel(provider is { Length: > 0 } && id is { Length: > 0 } ? $"{provider}/{id}" : id);
                }
                else if (model.ValueKind == JsonValueKind.String)
                {
                    tally.NoteModel(model.GetString());
                }
            }
            if (!m.TryGetProperty("time", out var time) || !time.TryGetProperty("created", out var created) ||
                created.ValueKind != JsonValueKind.Number) return;

            var cost = m.TryGetProperty("cost", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetDouble() : 0;
            long tokens = 0;
            if (m.TryGetProperty("tokens", out var t) && t.ValueKind == JsonValueKind.Object)
            {
                foreach (var name in new[] { "input", "output", "reasoning" })
                {
                    if (t.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number) tokens += v.GetInt64();
                }
                if (t.TryGetProperty("cache", out var cache) && cache.ValueKind == JsonValueKind.Object)
                {
                    foreach (var name in new[] { "read", "write" })
                    {
                        if (cache.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number) tokens += v.GetInt64();
                    }
                }
            }
            tally.Add(DateTimeOffset.FromUnixTimeMilliseconds(created.GetInt64()), cost, tokens);
        }
        catch (JsonException)
        {
        }
    }
}

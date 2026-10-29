using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WidBar.AiUsage;

// z.ai GLM Coding Plan quota. The API key is stored DPAPI-encrypted in
// ~/.config/widbar-aiusage/zai.key (or comes from the ZAI_API_KEY variable).
//
// Endpoints (same ones the z.ai dashboard and zai-coding-plugins' glm-plan-usage use):
//   GET /api/monitor/usage/quota/limit           -> plan level + limits[]
//   GET /api/monitor/usage/model-usage?startTime=&endTime=   (local "yyyy-MM-dd HH:mm:ss")
//       -> hourly calls/tokens + totalUsage{totalModelCallCount,totalTokensUsage} + modelSummaryList
// Only quota/limit is required; model-usage is best-effort extra detail.
public static class ZaiSource
{
    private const string BaseUrl = "https://api.z.ai/api/monitor/usage/";

    private static readonly string KeyPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config", "widbar-aiusage", "zai.key");

    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("widbar-aiusage/zai");

    public static async Task<ProviderSnapshot> FetchAsync(CancellationToken ct)
    {
        var key = ReadKey();
        if (key is null) return ProviderSnapshot.Missing("No z.ai API key saved");

        using var resp = await Http.Client.SendAsync(Request(key, "quota/limit"), ct);
        if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            return ProviderSnapshot.Failed("z.ai rejected the API key");
        }
        resp.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var data = doc.RootElement.GetProperty("data");

        UsageWindow? session = null, weekly = null;
        var details = new List<DetailLine>();
        if (data.TryGetProperty("level", out var level) && level.GetString() is { Length: > 0 } lv)
        {
            details.Add(new DetailLine("Plan", "GLM Coding " + CultureInfo.InvariantCulture.TextInfo.ToTitleCase(lv)));
        }

        foreach (var limit in data.GetProperty("limits").EnumerateArray())
        {
            var type = limit.TryGetProperty("type", out var ty) ? ty.GetString() : null;
            var unit = Int(limit, "unit");
            var number = Int(limit, "number");
            var percent = Num(limit, "percentage") ?? 0;
            DateTimeOffset? resets = limit.TryGetProperty("nextResetTime", out var r) && r.ValueKind == JsonValueKind.Number
                ? DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64())
                : null;

            if (type == "TOKENS_LIMIT")
            {
                // unit 3 = hours, 6 = weeks, 5 = months. Token limits only expose a percentage.
                var window = new UsageWindow(UnitName(unit, number), percent, resets);
                if (unit == 3 && session is null) session = window;
                else if (unit != 3 && weekly is null) weekly = window;
                else details.Add(new DetailLine(window.Name, $"{percent:0}%{Format.ResetSuffix(resets)}"));
            }
            else if (type == "TIME_LIMIT")
            {
                // Web search / web reader / zread MCP calls on a monthly allowance.
                var used = Num(limit, "currentValue") ?? 0;
                var total = Num(limit, "usage") ?? 0;
                var name = "MCP calls";
                details.Add(new DetailLine(name,
                    $"{used:N0} / {total:N0}{Format.ResetSuffix(resets)}"));

                if (limit.TryGetProperty("usageDetails", out var ud) && ud.ValueKind == JsonValueKind.Array)
                {
                    var parts = ud.EnumerateArray()
                        .Select(d => (code: d.TryGetProperty("modelCode", out var mc) ? mc.GetString() : null, n: Num(d, "usage") ?? 0))
                        .Where(x => x.code is { Length: > 0 } && x.n > 0)
                        .Select(x => $"{ToolName(x.code!)} {x.n:N0}")
                        .ToList();
                    if (parts.Count > 0) details.Add(new DetailLine("By tool", string.Join(" · ", parts)));
                }
            }
            else if (type is { Length: > 0 })
            {
                // Unknown future limit type: still show it.
                details.Add(new DetailLine($"{type} ({UnitName(unit, number).ToLowerInvariant()})",
                    $"{percent:0}%{Format.ResetSuffix(resets)}"));
            }
        }

        details.AddRange(await TodayModelUsageAsync(key, ct));
        return new ProviderSnapshot { Session = session, Weekly = weekly, Details = details };
    }

    // Calls and tokens since local midnight, per model. Failure here never breaks the quota view.
    private static async Task<List<DetailLine>> TodayModelUsageAsync(string key, CancellationToken ct)
    {
        var lines = new List<DetailLine>();
        try
        {
            var now = DateTime.Now;
            var q = $"model-usage?startTime={Uri.EscapeDataString(now.Date.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture))}" +
                    $"&endTime={Uri.EscapeDataString(now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture))}";
            using var resp = await Http.Client.SendAsync(Request(key, q), ct);
            if (!resp.IsSuccessStatusCode) return lines;

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            if (!doc.RootElement.TryGetProperty("data", out var data) ||
                !data.TryGetProperty("totalUsage", out var total))
            {
                return lines;
            }

            var calls = Num(total, "totalModelCallCount") ?? 0;
            var tokens = (long)(Num(total, "totalTokensUsage") ?? 0);
            lines.Add(new DetailLine("Today", $"{calls:N0} requests · {Format.Tokens(tokens)} tokens"));

            // Per-model totals come from summing the hourly series (the summary list can
            // cover a wider window than requested).
            if (data.TryGetProperty("modelDataList", out var models) && models.ValueKind == JsonValueKind.Array)
            {
                foreach (var m in models.EnumerateArray())
                {
                    var name = m.TryGetProperty("modelName", out var mn) ? mn.GetString() : null;
                    if (name is not { Length: > 0 }) continue;
                    long sum = 0;
                    if (m.TryGetProperty("tokensUsage", out var series) && series.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var v in series.EnumerateArray())
                            if (v.ValueKind == JsonValueKind.Number) sum += v.GetInt64();
                    }
                    if (sum > 0) lines.Add(new DetailLine($"  {name}", Format.Tokens(sum)));
                }
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException
                                       or KeyNotFoundException or FormatException
                                       || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            // Optional detail; leave it out.
        }
        return lines;
    }

    private static HttpRequestMessage Request(string key, string path)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, BaseUrl + path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        req.Headers.AcceptLanguage.ParseAdd("en-US");
        return req;
    }

    private static double? Num(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    private static int Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;

    private static string ToolName(string code) => code switch
    {
        "search-prime" => "Search",
        "web-reader" => "Web reader",
        "zread" => "Zread",
        _ => code,
    };

    private static string UnitName(int unit, int number) => (unit, number) switch
    {
        (3, _) => $"{number}-hour window",
        (6, 1) => "Weekly",
        (6, _) => $"{number}-week window",
        (5, 1) => "Monthly",
        (5, _) => $"{number}-month window",
        _ => "Quota",
    };

    // Saves a key entered in settings, encrypted for this Windows account.
    public static void SaveKey(string key)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(KeyPath)!);
        var blob = ProtectedData.Protect(Encoding.UTF8.GetBytes(key.Trim()), Entropy, DataProtectionScope.CurrentUser);
        File.WriteAllText(KeyPath, Convert.ToBase64String(blob));
    }

    public static bool HasKey => ReadKey() is not null;

    private static string? ReadKey()
    {
        if (Environment.GetEnvironmentVariable("ZAI_API_KEY") is { Length: > 0 } env) return env.Trim();
        if (!File.Exists(KeyPath)) return null;
        try
        {
            var blob = Convert.FromBase64String(File.ReadAllText(KeyPath).Trim());
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(blob, Entropy, DataProtectionScope.CurrentUser));
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            return null;
        }
    }
}

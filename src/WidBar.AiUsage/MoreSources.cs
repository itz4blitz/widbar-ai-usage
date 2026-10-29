using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace WidBar.AiUsage;

// Adds up spend and tokens per local day, for the pay-per-token tools.
internal sealed class SpendTally
{
    private readonly DateTime _today = DateTime.Today;
    private readonly DateTime _weekStart = DateTime.Today.AddDays(-6);

    public double Today { get; private set; }
    public double Week { get; private set; }
    public long TokensToday { get; private set; }
    public long TokensWeek { get; private set; }
    public DateTimeOffset? LastActivity { get; private set; }
    private readonly HashSet<string> _models = [];

    public DateTime Since => _weekStart;

    public void Add(DateTimeOffset at, double usd, long tokens)
    {
        var day = at.ToLocalTime().Date;
        if (LastActivity is null || at > LastActivity) LastActivity = at;
        if (day < _weekStart) return;
        Week += usd;
        TokensWeek += tokens;
        if (day == _today)
        {
            Today += usd;
            TokensToday += tokens;
        }
    }

    public void NoteModel(string? name)
    {
        if (name is { Length: > 0 }) _models.Add(name);
    }

    public ProviderSnapshot ToSnapshot(string spendNote)
    {
        var details = new List<DetailLine>
        {
            new("Today", $"{Format.Usd(Today)} · {Format.Tokens(TokensToday)} tokens"),
            new("Last 7 days", $"{Format.Usd(Week)} · {Format.Tokens(TokensWeek)} tokens"),
            new("Cost basis", spendNote),
        };
        if (_models.Count > 0) details.Add(new DetailLine("Models", string.Join(", ", _models.Order())));
        if (LastActivity is { } last) details.Add(new DetailLine("Last activity", Format.Ago(last)));
        return new ProviderSnapshot
        {
            SpendToday = Today,
            SpendWeek = Week,
            TokensToday = TokensToday,
            TokensWeek = TokensWeek,
            Details = details,
        };
    }
}

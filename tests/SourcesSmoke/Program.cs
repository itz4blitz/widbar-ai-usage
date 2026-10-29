using WidBar.AiUsage;

// The Linux NVMe reports ~100% used and almost no free space. It must not take C:.
// When two disks both look like C:, the closer free-space match wins, and the letter is used once.
var linux = DiskLetters.Assign(
[
    ("/nvme/1", 0.6, 1000.2),
    ("/nvme/2", 520.5, 1000.2),
    ("/ssd/0", 457.3, 500.1),
],
[
    ("C", 520.19),
    ("R", 457.28),
]);
if (linux.GetValueOrDefault("/nvme/2") != "C" || linux.ContainsKey("/nvme/1") || linux.GetValueOrDefault("/ssd/0") != "R")
    throw new Exception("Linux disk was treated as a Windows volume: " + string.Join(", ", linux));

var closer = DiskLetters.Assign(
[
    ("/nvme/1", 519.0, 1000),
    ("/nvme/2", 510.0, 1000),
],
[
    ("C", 520.2),
]);
if (closer.GetValueOrDefault("/nvme/1") != "C" || closer.ContainsKey("/nvme/2"))
    throw new Exception("Drive letter was given to the worse free-space match: " + string.Join(", ", closer));

var sources = new (string, Func<CancellationToken, Task<ProviderSnapshot>>)[]
{
    ("claude", ClaudeSource.FetchAsync), ("codex", CodexSource.FetchAsync), ("grok", GrokSource.FetchAsync),
    ("zai", ZaiSource.FetchAsync), ("cursor", CursorSource.FetchAsync), ("opencode", OpenCodeSource.FetchAsync),
};
foreach (var (name, fetch) in sources)
{
    var s = await fetch(CancellationToken.None);
    Console.WriteLine($"== {name}: err={s.Error} notcfg={s.NotConfigured} session={s.Session} weekly={s.Weekly} today={s.SpendToday} week={s.SpendWeek} tokens={s.TokensWeek}");
    foreach (var d in s.Details) Console.WriteLine($"   {d.Label}: {d.Value}");
    if (name == "opencode" && (s.NotConfigured || s.TokensWeek <= 0))
        throw new Exception($"OpenCode history was not read: {s.Error}");
}

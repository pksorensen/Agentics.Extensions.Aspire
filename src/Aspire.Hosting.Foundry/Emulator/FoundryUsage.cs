using System.Text.Json;

namespace Aspire.Hosting.Foundry.Emulator;

/// <summary>One model call, as the emulator metered it.</summary>
public sealed record FoundryUsageRecord(
    string Id,
    DateTimeOffset Timestamp,
    string Account,
    string Deployment,
    string Model,
    string Operation,
    string Caller,
    string CallerType,
    string? Customer,
    string? Project,
    string? EndUser,
    string? Agent,
    long InputTokens,
    long CachedInputTokens,
    long OutputTokens,
    long ReasoningTokens,
    int StatusCode,
    bool Stream,
    string? Scenario,
    int? ScenarioStep,
    bool Seeded = false,
    string? SubscriptionId = null,
    string? UserPrincipal = null,
    string? Initiative = null,
    string? Line = null,
    int Requests = 1)
{
    // A seeded record stands for a whole day's traffic; a live one is one call.
    public long TotalTokens => InputTokens + OutputTokens;
}

/// <summary>
/// Append-only usage log. In memory, mirrored to <c>usage.jsonl</c> in the data
/// directory when one is configured, so a demo's history survives a restart.
/// Seeded history is regenerated deterministically on every start and never
/// written to the file.
/// </summary>
public sealed class FoundryUsageStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly List<FoundryUsageRecord> _records = [];
    private readonly object _gate = new();
    private readonly string? _path;

    public FoundryUsageStore(string? dataDirectory)
    {
        if (string.IsNullOrWhiteSpace(dataDirectory)) return;
        Directory.CreateDirectory(dataDirectory);
        _path = Path.Combine(dataDirectory, "usage.jsonl");
        if (!File.Exists(_path)) return;
        foreach (var line in File.ReadLines(_path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                if (JsonSerializer.Deserialize<FoundryUsageRecord>(line, Json) is { } record) _records.Add(record);
            }
            catch (JsonException) { /* a torn last line after a crash; skip it */ }
        }
    }

    public void Add(FoundryUsageRecord record)
    {
        lock (_gate)
        {
            _records.Add(record);
            if (_path is not null && !record.Seeded)
                File.AppendAllText(_path, JsonSerializer.Serialize(record, Json) + "\n");
        }
    }

    public void AddSeeded(IEnumerable<FoundryUsageRecord> records)
    {
        lock (_gate) _records.AddRange(records);
    }

    public IReadOnlyList<FoundryUsageRecord> Snapshot()
    {
        lock (_gate) return _records.OrderBy(r => r.Timestamp).ToArray();
    }

    public int Clear(bool includeSeeded)
    {
        lock (_gate)
        {
            var removed = _records.RemoveAll(r => includeSeeded || !r.Seeded);
            if (_path is not null && File.Exists(_path)) File.WriteAllText(_path, "");
            return removed;
        }
    }

    /// <summary>Deterministic history: same seed, same numbers, every run.</summary>
    internal static IEnumerable<FoundryUsageRecord> Generate(FoundryUsageSeed seed, string model, DateTimeOffset now)
    {
        var random = new Random(seed.RandomSeed);
        var end = (seed.EndDate ?? now).UtcDateTime.Date;
        for (var day = seed.Days; day >= 1; day--)
        {
            var date = new DateTimeOffset(end.AddDays(-day), TimeSpan.Zero);
            // Quieter weekends, like real engineering traffic.
            var weekday = date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday ? 0.25 : 1.0;
            var factor = weekday * (0.6 + random.NextDouble() * 0.8);
            var requests = Math.Max(1, (int)Math.Round(seed.DailyRequests * factor));
            var input = (long)Math.Round(seed.DailyInputTokens * factor);
            var output = (long)Math.Round(seed.DailyOutputTokens * factor);
            if (input == 0 && output == 0) continue;
            yield return new FoundryUsageRecord(
                Id: $"seed-{seed.RandomSeed}-{seed.Account}-{seed.Deployment}-{seed.Customer}-{day}",
                Timestamp: date.AddHours(9 + random.Next(0, 8)).AddMinutes(random.Next(0, 60)),
                Account: seed.Account,
                Deployment: seed.Deployment,
                Model: model,
                Operation: "responses",
                Caller: seed.Caller ?? "seed",
                CallerType: "seed",
                Customer: seed.Customer,
                Project: seed.Project,
                EndUser: null,
                Agent: null,
                InputTokens: input,
                CachedInputTokens: (long)Math.Round(input * seed.CachedRatio),
                OutputTokens: output,
                ReasoningTokens: 0,
                StatusCode: 200,
                Stream: true,
                Scenario: null,
                ScenarioStep: null,
                Seeded: true,
                SubscriptionId: seed.SubscriptionId,
                UserPrincipal: seed.UserPrincipal,
                Initiative: seed.Initiative,
                Requests: requests);
        }
    }
}

/// <summary>Filters and groupings for <c>GET /_emulator/usage</c>.</summary>
public sealed record FoundryUsageQuery(
    DateTimeOffset? From,
    DateTimeOffset? To,
    string? Account,
    string? Deployment,
    string? Model,
    string? Customer,
    string? Project,
    string? Caller,
    string? SubscriptionId = null,
    string? UserPrincipal = null)
{
    public IEnumerable<FoundryUsageRecord> Apply(IEnumerable<FoundryUsageRecord> records) => records.Where(r =>
        (From is null || r.Timestamp >= From) &&
        (To is null || r.Timestamp < To) &&
        Eq(Account, r.Account) && Eq(Deployment, r.Deployment) && Eq(Model, r.Model) &&
        Eq(Customer, r.Customer) && Eq(Project, r.Project) && Eq(Caller, r.Caller) &&
        Eq(SubscriptionId, r.SubscriptionId) && Eq(UserPrincipal, r.UserPrincipal));

    private static bool Eq(string? filter, string? value) =>
        filter is null || string.Equals(filter, value, StringComparison.OrdinalIgnoreCase);
}

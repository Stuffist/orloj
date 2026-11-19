using System.Globalization;

namespace Olomoc.Tournaments.Stats;

/// <summary>
/// Period of the statistics (/stats/?from=&amp;to=): from, to, both included and optional. A
/// tournament counts on its date (or the day it finished), a zone game on the day it was played.
/// </summary>
public sealed record StatsPeriod(DateOnly? From = null, DateOnly? To = null)
{
    public static readonly StatsPeriod All = new();

    public bool IsAll => From is null && To is null;

    public bool Contains(DateOnly day) => (From is not { } f || day >= f) && (To is not { } t || day <= t);

    public bool Contains(ArchivedTournament t) => Contains(DayOf(t));

    /// <summary>Day of a tournament for the statistics: its date, otherwise the day it finished.</summary>
    public static DateOnly DayOf(ArchivedTournament t) => t.Date ?? DateOnly.FromDateTime(t.FinishedAt.UtcDateTime);

    /// <summary>Query of the API (and of the page): "from=…&amp;to=…", empty for everything.</summary>
    public string Query => string.Join("&", new[] { ("from", From), ("to", To) }
        .Where(x => x.Item2 is not null)
        .Select(x => $"{x.Item1}={x.Item2!.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}"));

    /// <summary>A date of the query (yyyy-MM-dd); anything else: none.</summary>
    public static DateOnly? Parse(string? value) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
}

/// <summary>
/// Reset of the statistics by an admin: from that moment, only what is recorded afterwards counts
/// (tournaments finished, zone games entered; dominances included). Nothing is deleted, and an admin
/// can cancel it.
/// </summary>
public static class StatsReset
{
    public static bool Counts(DateTimeOffset recordedAt, DateTimeOffset? since) => since is not { } s || recordedAt >= s;
}

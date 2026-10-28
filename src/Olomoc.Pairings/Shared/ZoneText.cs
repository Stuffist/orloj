using System.Globalization;
using Olomoc.Models;
using Olomoc.Services;
using Olomoc.Zones;

namespace Olomoc.Pairings.Shared;

/// <summary>Texts of the zones: period, state, color and order on the map.</summary>
public static class ZoneText
{
    public static string Date(DateOnly date) => date.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);

    /// <summary>"From 01/01/2026 to 31/03/2026", "Since…", "Until…", or empty (no dates).</summary>
    public static string Period(this LocalizationService l, DateOnly? start, DateOnly? end) => (start, end) switch
    {
        ({ } s, { } e) => l.Ui("zones.period.between", Date(s), Date(e)),
        ({ } s, null) => l.Ui("zones.period.since", Date(s)),
        (null, { } e) => l.Ui("zones.period.until", Date(e)),
        _ => "",
    };

    /// <summary>Description on one paragraph: its lines joined by " · " (the list shows two lines at most).</summary>
    public static string Condensed(string? text) => string.Join(" · ",
        (text ?? "").Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));

    public static string StatusClass(ZoneStatus status) => status switch
    {
        ZoneStatus.Open => "registration",
        ZoneStatus.Upcoming => "started",
        _ => "",
    };

    /// <summary>
    /// Color of a zone on the map: open, upcoming, closed under the Light or the Darkness, or closed
    /// without dominance (tie, no games).
    /// </summary>
    public static string MapClass(ZoneSummary z) => z.Status switch
    {
        ZoneStatus.Open => "open",
        ZoneStatus.Upcoming => "upcoming",
        _ => z.Dominance.Alignment switch
        {
            Alignment.LIGHT => "light",
            Alignment.DARK => "dark",
            _ => "none",
        },
    };

    /// <summary>Open zones (the closest end first), upcoming (the closest start first), then closed (the most recent first).</summary>
    public static IEnumerable<ZoneSummary> Ordered(IEnumerable<ZoneSummary> zones)
    {
        var list = zones.ToList();
        return list.Where(z => z.Status == ZoneStatus.Open).OrderBy(z => z.EndDate ?? DateOnly.MaxValue).ThenBy(z => z.Name)
            .Concat(list.Where(z => z.Status == ZoneStatus.Upcoming).OrderBy(z => z.StartDate).ThenBy(z => z.Name))
            .Concat(list.Where(z => z.Status == ZoneStatus.Closed).OrderByDescending(z => z.EndDate ?? z.StartDate).ThenBy(z => z.Name));
    }
}

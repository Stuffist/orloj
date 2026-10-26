using System.Globalization;
using Olomoc.Services;
using Olomoc.Zones;

namespace Olomoc.Pairings.Shared;

/// <summary>Texts of the zones: period, state.</summary>
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

    public static string StatusClass(ZoneStatus status) => status switch
    {
        ZoneStatus.Open => "registration",
        ZoneStatus.Upcoming => "started",
        _ => "",
    };
}

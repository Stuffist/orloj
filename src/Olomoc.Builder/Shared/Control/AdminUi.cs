using System.Globalization;
using Olomoc.Services;

namespace Olomoc.Builder.Shared.Control;

/// <summary>Mise en forme du panneau d'administration : dates à l'heure de l'appareil, tailles.</summary>
public static class AdminUi
{
    public static string Date(DateTimeOffset? at) =>
        at?.ToLocalTime().ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture) ?? "—";

    public static string Day(DateOnly? date) => date?.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture) ?? "—";

    public static string Bytes(this LocalizationService l, long bytes) => bytes switch
    {
        < 1024 => l.Ui("control.size.b", bytes),
        < 1024 * 1024 => l.Ui("control.size.kb", (bytes / 1024.0).ToString("0.#", CultureInfo.InvariantCulture)),
        _ => l.Ui("control.size.mb", (bytes / (1024.0 * 1024)).ToString("0.#", CultureInfo.InvariantCulture)),
    };
}

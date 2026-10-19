using Olomoc.Services;
using Olomoc.Tournaments;

namespace Olomoc.Builder.Shared;

/// <summary>Compte rendu d'un formulaire du compte : texte, réussite ou erreur.</summary>
public sealed record AccountStatus(string Text, bool Ok);

/// <summary>Outils des pages du compte.</summary>
public static class AccountUi
{
    /// <summary>Problèmes renvoyés par l'API des comptes, dans la langue courante.</summary>
    public static string Describe(this LocalizationService l, IReadOnlyList<Problem> problems) =>
        string.Join(" ", problems.Select(p => l.Ui("account.problem." + p.Key, (p.Args ?? []).Cast<object>().ToArray())));

    /// <summary>Paramètres de l'ancre d'une adresse (#user=…&amp;token=…) : liens reçus par e-mail.</summary>
    public static IReadOnlyDictionary<string, string> Fragment(string uri)
    {
        var hash = uri.IndexOf('#');
        if (hash < 0) return new Dictionary<string, string>();
        return uri[(hash + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2))
            .Where(kv => kv.Length == 2)
            .GroupBy(kv => kv[0])
            .ToDictionary(g => g.Key, g => Uri.UnescapeDataString(g.First()[1]));
    }
}

using Olomoc.Services;
using Olomoc.Tournaments;

namespace Olomoc.Pairings.Services;

/// <summary>Texte d'un problème renvoyé par l'API, dans la langue courante.</summary>
public static class ProblemText
{
    public static string Describe(this LocalizationService l, Problem p)
    {
        var args = (p.Args ?? Array.Empty<string>())
            .Select(a => p.Key == "cabalFaction" ? l.FactionName(a).Text : a)
            .Cast<object>().ToArray();
        var text = p.Legality is { } issue ? l.Describe(issue) : l.Ui("pairings.problem." + p.Key, args);
        return p.Cabal is { } i ? $"{l.Ui("pairings.cabalN", i + 1)} : {text}" : text;
    }
}

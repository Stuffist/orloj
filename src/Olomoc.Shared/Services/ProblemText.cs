using Olomoc.Services;
using Olomoc.Tournaments;

namespace Olomoc.Services;

/// <summary>Text of a problem returned by the API, in the current language.</summary>
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

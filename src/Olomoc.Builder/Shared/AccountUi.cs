using Olomoc.Services;
using Olomoc.Tournaments;

namespace Olomoc.Builder.Shared;

/// <summary>Report of an account form: text, success or error.</summary>
public sealed record AccountStatus(string Text, bool Ok);

/// <summary>Helpers of the account pages.</summary>
public static class AccountUi
{
    /// <summary>Problems returned by the accounts API, in the current language.</summary>
    public static string Describe(this LocalizationService l, IReadOnlyList<Problem> problems) =>
        string.Join(" ", problems.Select(p => l.Ui("account.problem." + p.Key, (p.Args ?? []).Cast<object>().ToArray())));

    /// <summary>Parameters of an address fragment (#user=…&amp;token=…): links received by email.</summary>
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

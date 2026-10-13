using Olomoc.Models;
using Olomoc.Services;

namespace Olomoc.Tournaments;

/// <summary>
/// Problème signalé à l'utilisateur : clé de message (ui.json, préfixe « pairings.problem. ») et
/// arguments ; pour une cabale, son numéro (à partir de 0) et, le cas échéant, le problème de
/// légalité (messages « legality. » du constructeur).
/// </summary>
public sealed record Problem(string Key, IReadOnlyList<string>? Args = null, int? Cabal = null, LegalityIssue? Legality = null)
{
    public static Problem Of(string key, params string[] args) => new(key, args);
}

/// <summary>
/// Règles du tournoi : chaque opération vérifie qu'elle est permise (phase, résultats saisis…)
/// et ne modifie le tournoi que si c'est le cas. Renvoie null, ou le problème qui l'empêche.
/// </summary>
public static class TournamentRules
{
    public const int MaxPlayers = 128, MaxNameLength = 80, MaxRounds = 10, MaxVictoryPoints = 999;
    public const int MinPasswordLength = 8, MaxPasswordLength = 128;

    /// <summary>Mot de passe organisateur : absent, ou de longueur raisonnable.</summary>
    public static Problem? CheckPassword(string? password) =>
        password is null || password.Length is >= MinPasswordLength and <= MaxPasswordLength
            ? null : Problem.Of("password", MinPasswordLength.ToString());

    public static IReadOnlyList<Problem> CheckSettings(string? name, int roundCount, int cabalsPerPlayer)
    {
        var problems = new List<Problem>();
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > MaxNameLength) problems.Add(Problem.Of("tournamentName", MaxNameLength.ToString()));
        if (roundCount is < 1 or > MaxRounds) problems.Add(Problem.Of("roundCount", MaxRounds.ToString()));
        if (cabalsPerPlayer is < 1 or > 2) problems.Add(Problem.Of("cabalsPerPlayer"));
        return problems;
    }

    /// <summary>
    /// Inscription (ou modification, <paramref name="playerId"/> donné) : nom libre et unique,
    /// faction connue, nombre de cabales du tournoi, toutes de cette faction et légales
    /// (mêmes règles que le constructeur).
    /// </summary>
    public static IReadOnlyList<Problem> CheckRegistration(Tournament t, CatalogService catalog,
        string? name, string? faction, IReadOnlyList<CabalExport>? cabals, string? playerId = null)
    {
        if (t.Phase != TournamentPhase.Registration) return new[] { Problem.Of("registrationClosed") };
        if (playerId is null && t.Players.Count >= MaxPlayers) return new[] { Problem.Of("tooManyPlayers", MaxPlayers.ToString()) };

        var problems = new List<Problem>();
        var trimmed = name?.Trim() ?? "";
        if (trimmed.Length is 0 || trimmed.Length > MaxNameLength) problems.Add(Problem.Of("playerName", MaxNameLength.ToString()));
        else if (t.Players.Any(p => p.Id != playerId && string.Equals(p.Name, trimmed, StringComparison.CurrentCultureIgnoreCase)))
            problems.Add(Problem.Of("nameTaken", trimmed));

        if (faction is null || catalog.AllFactions.All(f => f.Code != faction))
        {
            problems.Add(Problem.Of("faction"));
            return problems;
        }
        cabals ??= Array.Empty<CabalExport>();
        if (cabals.Count != t.CabalsPerPlayer) problems.Add(Problem.Of("cabalCount", t.CabalsPerPlayer.ToString()));

        for (var i = 0; i < cabals.Count; i++)
        {
            var cabal = cabals[i];
            if (cabal.Faction?.Code != faction)
            {
                problems.Add(new Problem("cabalFaction", new[] { faction }, i));
                continue;
            }
            var resolved = catalog.Resolve(cabal);
            if (resolved.Missing.Count > 0)
            {
                problems.Add(new Problem("cabalMissing", new[] { string.Join(", ", resolved.Missing) }, i));
                continue;
            }
            foreach (var issue in catalog.CheckLegality(faction, resolved.Cogs, resolved.Equipment).Issues)
                problems.Add(new Problem("cabalIllegal", null, i, issue));
        }
        return problems;
    }

    public static Problem? CanEditRegistrations(Tournament t) =>
        t.Phase == TournamentPhase.Registration ? null : Problem.Of("registrationClosed");

    /// <summary>Clôt les inscriptions et révèle les listes.</summary>
    public static Problem? Start(Tournament t)
    {
        if (t.Phase != TournamentPhase.Registration) return Problem.Of("alreadyStarted");
        if (t.Players.Count(p => !p.Dropped) < 2) return Problem.Of("notEnoughPlayers");
        t.Phase = TournamentPhase.Started;
        return null;
    }

    public static Problem? CanPair(Tournament t)
    {
        if (t.Phase == TournamentPhase.Registration) return Problem.Of("notStarted");
        if (t.Phase == TournamentPhase.Finished) return Problem.Of("finished");
        if (t.LastRound is { IsComplete: false } last) return Problem.Of("roundIncomplete", last.Number.ToString());
        if (t.Rounds.Count >= t.RoundCount) return Problem.Of("noMoreRounds", t.RoundCount.ToString());
        if (t.ActivePlayers.Count() < 2) return Problem.Of("notEnoughPlayers");
        return null;
    }

    /// <summary>Apparie et ajoute la ronde suivante ; <see cref="CanPair"/> doit l'avoir permis.</summary>
    public static PairingOutcome PairNext(Tournament t, Func<string, Alignment?> alignmentOf, int seed)
    {
        if (CanPair(t) is { } problem) throw new InvalidOperationException(problem.Key);
        var outcome = Pairer.Pair(t, alignmentOf, seed);
        t.Rounds.Add(outcome.Round);
        return outcome;
    }

    /// <summary>Retire la dernière ronde, tant qu'aucun résultat n'y est saisi.</summary>
    public static Problem? UndoLastRound(Tournament t)
    {
        if (t.Phase == TournamentPhase.Finished) return Problem.Of("finished");
        if (t.LastRound is not { } last) return Problem.Of("noRound");
        if (last.HasAnyResult) return Problem.Of("roundHasResults", last.Number.ToString());
        t.Rounds.RemoveAt(t.Rounds.Count - 1);
        return null;
    }

    /// <summary>PV d'une table (deux valeurs, ou aucune pour effacer le résultat).</summary>
    public static Problem? SetResult(Tournament t, int roundNumber, int tableNumber, int? vpA, int? vpB)
    {
        if (t.Phase == TournamentPhase.Finished) return Problem.Of("finished");
        var table = t.Rounds.FirstOrDefault(r => r.Number == roundNumber)?.Tables.FirstOrDefault(x => x.Number == tableNumber);
        if (table is null || table.IsBye) return Problem.Of("unknownTable");
        if (vpA.HasValue != vpB.HasValue || vpA is < 0 or > MaxVictoryPoints || vpB is < 0 or > MaxVictoryPoints)
            return Problem.Of("victoryPoints", MaxVictoryPoints.ToString());
        table.VpA = vpA;
        table.VpB = vpB;
        return null;
    }

    public static Problem? SetDropped(Tournament t, string playerId, bool dropped)
    {
        if (t.Phase == TournamentPhase.Finished) return Problem.Of("finished");
        if (t.PlayerById(playerId) is not { } player) return Problem.Of("unknownPlayer");
        player.Dropped = dropped;
        return null;
    }

    /// <summary>Termine le tournoi : la dernière ronde doit être complète.</summary>
    public static Problem? Finish(Tournament t)
    {
        if (t.Phase != TournamentPhase.Started) return Problem.Of(t.Phase == TournamentPhase.Finished ? "finished" : "notStarted");
        if (t.LastRound is not { } last) return Problem.Of("noRound");
        if (!last.IsComplete) return Problem.Of("roundIncomplete", last.Number.ToString());
        t.Phase = TournamentPhase.Finished;
        return null;
    }
}

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

    /// <summary>
    /// Correction à la main d'un appariement : échange deux joueurs de la ronde en cours, assis à
    /// des tables différentes sans résultat saisi (l'exempt compris : l'exemption change de
    /// joueur). Une revanche n'est pas interdite, l'organisateur décide (voir <see cref="MetBefore"/>).
    /// </summary>
    public static Problem? SwapPlayers(Tournament t, int roundNumber, string playerX, string playerY)
    {
        if (t.Phase != TournamentPhase.Started) return Problem.Of(t.Phase == TournamentPhase.Finished ? "finished" : "notStarted");
        if (t.LastRound is not { } last || last.Number != roundNumber) return Problem.Of("notCurrentRound");
        var tableX = last.Tables.FirstOrDefault(x => x.Involves(playerX));
        var tableY = last.Tables.FirstOrDefault(x => x.Involves(playerY));
        if (tableX is null || tableY is null) return Problem.Of("unknownPlayer");
        if (tableX == tableY) return Problem.Of("sameTable");
        if (HasScore(tableX) || HasScore(tableY)) return Problem.Of("tableHasResult");
        Replace(tableX, playerX, playerY);
        Replace(tableY, playerY, playerX);
        return null;

        static bool HasScore(Table x) => !x.IsBye && (x.VpA.HasValue || x.VpB.HasValue);
        static void Replace(Table x, string from, string to)
        {
            if (x.PlayerA == from) x.PlayerA = to;
            else x.PlayerB = to;
        }
    }

    /// <summary>Ronde (la plus récente) où deux joueurs se sont déjà affrontés avant la ronde donnée ; null sinon.</summary>
    public static int? MetBefore(IEnumerable<Round> rounds, int beforeRound, string a, string b) =>
        rounds.Where(r => r.Number < beforeRound && r.Tables.Any(x => !x.IsBye && x.Involves(a) && x.Involves(b)))
              .Select(r => (int?)r.Number).LastOrDefault();

    /// <summary>
    /// Scénarios des rondes, prévus à l'avance (modifiables jusqu'à la fin du tournoi) : un par
    /// ronde au plus, agenda officiel ou scénario maison nommé (<see cref="Agendas.Other"/>), ou
    /// null (pas encore choisi).
    /// </summary>
    public static Problem? SetScenarios(Tournament t, IReadOnlyList<Scenario?>? scenarios)
    {
        if (t.Phase == TournamentPhase.Finished) return Problem.Of("finished");
        scenarios ??= Array.Empty<Scenario?>();
        if (scenarios.Count > t.RoundCount) return Problem.Of("scenarioCount", t.RoundCount.ToString());
        var cleaned = new List<Scenario?>();
        foreach (var s in scenarios)
        {
            if (s is null) { cleaned.Add(null); continue; }
            if (!Agendas.IsKnown(s.Id)) return Problem.Of("scenario");
            if (s.Artifact is not null && !Decks.IsArtifact(s.Artifact)) return Problem.Of("deck");
            if (s.Ministry is not null && !Decks.IsMinistry(s.Ministry)) return Problem.Of("deck");
            if (s.Id != Agendas.Other) { cleaned.Add(new Scenario(s.Id, null, s.Artifact, s.Ministry)); continue; }
            var name = s.Name?.Trim() ?? "";
            if (name.Length is 0 || name.Length > MaxNameLength) return Problem.Of("scenarioName", MaxNameLength.ToString());
            cleaned.Add(new Scenario(Agendas.Other, name, s.Artifact, s.Ministry));
        }
        while (cleaned.Count > 0 && cleaned[^1] is null) cleaned.RemoveAt(cleaned.Count - 1);
        t.Scenarios = cleaned;
        return null;
    }

    /// <summary>
    /// PV d'une table (deux valeurs, ou aucune pour effacer le résultat). Facultatifs, pour les
    /// statistiques : la cabale jouée par chacun (avec une seule cabale par joueur, c'est la
    /// première ; avec deux, celle indiquée, ou inconnue), qui était J1, et la cause de la fin de partie.
    /// </summary>
    public static Problem? SetResult(Tournament t, int roundNumber, int tableNumber, int? vpA, int? vpB,
        int? cabalA = null, int? cabalB = null, TableSide? first = null, GameEnd? end = null)
    {
        if (t.Phase == TournamentPhase.Finished) return Problem.Of("finished");
        var table = t.Rounds.FirstOrDefault(r => r.Number == roundNumber)?.Tables.FirstOrDefault(x => x.Number == tableNumber);
        if (table is null || table.IsBye) return Problem.Of("unknownTable");
        if (vpA.HasValue != vpB.HasValue || vpA is < 0 or > MaxVictoryPoints || vpB is < 0 or > MaxVictoryPoints)
            return Problem.Of("victoryPoints", MaxVictoryPoints.ToString());
        if (vpA.HasValue && t.CabalsPerPlayer > 1 && (cabalA is < 0 || cabalA >= t.CabalsPerPlayer || cabalB is < 0 || cabalB >= t.CabalsPerPlayer))
            return Problem.Of("cabalPlayed");
        table.VpA = vpA;
        table.VpB = vpB;
        table.CabalA = vpA.HasValue ? (t.CabalsPerPlayer > 1 ? cabalA : 0) : null;
        table.CabalB = vpA.HasValue ? (t.CabalsPerPlayer > 1 ? cabalB : 0) : null;
        table.First = vpA.HasValue ? first : null;
        table.End = vpA.HasValue ? end : null;
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

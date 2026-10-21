namespace Olomoc.Tournaments.Stats;

// Archive anonyme d'un tournoi terminé, pour les statistiques : ni noms, ni jetons ; les joueurs
// sont numérotés. Conservée sans limite de durée (les tournois, eux, sont purgés).

/// <summary>Une carte d'une cabale archivée : rang, rouage, équipement/don (ids du référentiel).</summary>
public sealed record ArchivedSlot(int Rank, string Cog, string? Equipment);

/// <summary>Un joueur : numéro (ordre d'inscription), faction, cabales inscrites.</summary>
public sealed record ArchivedPlayer(int Number, string Faction, IReadOnlyList<IReadOnlyList<ArchivedSlot>> Cabals);

/// <summary>
/// Une partie jouée (exempts exclus) : joueurs A et B, cabale jouée par chacun (si connue), PV ;
/// si notés (archives version 2) : J1, cause de la fin, agenda de la ronde (<see cref="Agendas.Other"/>
/// pour un scénario maison, sans son nom) ; si précisés (archives version 3) : decks Artefact et Ministère.
/// </summary>
public sealed record ArchivedGame(int A, int B, int? CabalA, int? CabalB, int VpA, int VpB,
    TableSide? First = null, GameEnd? End = null, string? Scenario = null,
    string? Artifact = null, string? Ministry = null);

public sealed record ArchivedTournament(
    int Version, string Code, DateOnly? Date, DateTimeOffset FinishedAt, int CabalsPerPlayer,
    IReadOnlyList<ArchivedPlayer> Players, IReadOnlyList<ArchivedGame> Games)
{
    public const int CurrentVersion = 3;

    public static ArchivedTournament From(Tournament t, DateTimeOffset finishedAt)
    {
        var number = t.Players.Select((p, i) => (p.Id, i)).ToDictionary(x => x.Id, x => x.i);
        var players = t.Players.Select((p, i) => new ArchivedPlayer(i, p.Faction,
            p.Cabals.Select(c => (IReadOnlyList<ArchivedSlot>)c.Cogs
                .Select(s => new ArchivedSlot(s.Rank, s.Cog.Id, s.Equipment?.Id)).ToList()).ToList())).ToList();
        var games = t.Rounds.SelectMany(r => r.Tables.Select(x => (Round: r.Number, Table: x)))
            .Where(g => !g.Table.IsBye && g.Table.HasResult && number.ContainsKey(g.Table.PlayerA) && number.ContainsKey(g.Table.PlayerB!))
            .Select(g => (g.Table, Scenario: t.ScenarioOf(g.Round)))
            .Select(g => new ArchivedGame(number[g.Table.PlayerA], number[g.Table.PlayerB!], g.Table.CabalA, g.Table.CabalB,
                g.Table.VpA!.Value, g.Table.VpB!.Value, g.Table.First, g.Table.End,
                g.Scenario?.Id, g.Scenario?.Artifact, g.Scenario?.Ministry))
            .ToList();
        return new(CurrentVersion, t.Code, t.Date, finishedAt, t.CabalsPerPlayer, players, games);
    }
}

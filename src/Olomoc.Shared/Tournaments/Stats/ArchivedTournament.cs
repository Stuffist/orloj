namespace Olomoc.Tournaments.Stats;

// Anonymous archive of a finished tournament, for the statistics: no names, no tokens; players
// are numbered. Kept with no time limit (tournaments themselves are purged).

/// <summary>A card of an archived cabal: rank, cog, equipment/boon (card catalog ids).</summary>
public sealed record ArchivedSlot(int Rank, string Cog, string? Equipment);

/// <summary>A player: number (registration order), faction, registered cabals.</summary>
public sealed record ArchivedPlayer(int Number, string Faction, IReadOnlyList<IReadOnlyList<ArchivedSlot>> Cabals);

/// <summary>
/// A game played (byes excluded): players A and B, cabal played by each one (if known), VP; if
/// recorded (archive version 2): P1, cause of the end, agenda of the round (<see cref="Agendas.Other"/>
/// for a custom scenario, without its name); if specified (archive version 3): Artifact and Ministry decks.
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

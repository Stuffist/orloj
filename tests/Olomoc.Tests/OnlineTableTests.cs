using System.Text.Json;
using Olomoc.Models;
using Olomoc.Online;
using Olomoc.Services;
using Olomoc.Tournaments;
using Olomoc.Zones;
using Xunit;

namespace Olomoc.Tests;

/// <summary>
/// Table of an online game (free play, who moves what), result of the game, and the table's assets
/// (data/olomoc-table.json, imported from the Vassal module).
/// </summary>
public class OnlineTableTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 19, 20, 0, 0, TimeSpan.Zero);

    private static readonly Lazy<TableManifest> Manifest = new(() =>
        JsonSerializer.Deserialize<TableManifest>(File.ReadAllText(Path.Combine(AppData.WwwRoot, "data", "olomoc-table.json")), TableAssets.Json)!);

    private static async Task<(AppData D, OnlineGame G)> Started()
    {
        var d = await AppData.GetAsync();
        var x = new CabalExporter(d.L);
        CabalExport Cabal(string name, params (string Cog, string Equipment)[] slots) =>
            x.ToModel(name, "MAISEL", slots.Select(s => (Cog?)d.Cog(s.Cog)).ToArray(), slots.Select(s => (Equipment?)d.Equipment(s.Equipment)).ToArray());
        var g = new OnlineGame { Code = "TABLE1", Seats = { new Seat(), new Seat() } };
        Ok(Do(g, 0, new(OnlineCommandType.SetName, "Ana"), d));
        Ok(Do(g, 1, new(OnlineCommandType.SetName, "Bo"), d));
        Ok(Do(g, 0, new(OnlineCommandType.SetCabal, Cabal: Cabal("A", ("3R06", "E03"), ("2R08", "D01"), ("1R01", "E11"))), d));
        Ok(Do(g, 1, new(OnlineCommandType.SetCabal, Cabal: Cabal("B", ("3R06", "E03"), ("2R11", "E07"), ("1R01", "E11"))), d));
        Ok(Do(g, 0, new(OnlineCommandType.SetReady, Flag: true), d));
        Ok(Do(g, 1, new(OnlineCommandType.SetReady, Flag: true), d));
        return (d, g);
    }

    private static async Task<(AppData D, OnlineGame G)> InCycle()
    {
        var (d, g) = await Started();
        g.Step = OnlineStep.Agenda;
        g.First = 1;
        Ok(Do(g, 1, new(OnlineCommandType.ChooseAgenda, "VISITS"), d));
        return (d, g);
    }

    private static OnlineResult Do(OnlineGame g, int seat, OnlineCommand c, AppData d) =>
        OnlineRules.Apply(g, seat, c, d.Catalog, new Random(1), Now, Manifest.Value);

    private static void Ok(OnlineResult r) => Assert.True(r.Ok, string.Join(", ", r.Problems.Select(p => p.Key)));

    private static string Key(OnlineResult r) => Assert.Single(r.Problems).Key;

    // A token of a cog of the side's cabal, put down by a player (either one).
    private static TablePiece Cog(OnlineGame g, AppData d, int side, string id, int by = 0, double x = 50, double y = 50)
    {
        Ok(Do(g, by, new(OnlineCommandType.AddPiece, id, Piece: PieceKind.Cog, Side: side, X: x, Y: y), d));
        return g.Table!.Pieces.Last();
    }

    // ---- Table ----

    [Fact]
    public async Task Table_created_at_the_start_empty_on_the_default_mat()
    {
        var (_, g) = await Started();
        var table = Assert.IsType<OnlineTable>(g.Table);
        Assert.Equal(TableRules.DefaultBoard, table.Board);
        Assert.Empty(table.Pieces);                 // every cog off the Block: no token yet

        // Nothing secret on a table: the same for a spectator.
        Assert.Equal(TableRules.DefaultBoard, OnlineRules.View(g, null, new HashSet<int>()).Table!.Board);
    }

    [Fact]
    public async Task Cogs_are_tokens_as_many_as_wanted_moved_and_removed_by_either_player()
    {
        var (d, g) = await InCycle();
        var mine = Cog(g, d, 0, "2R08");
        Assert.Equal((PieceKind.Cog, 0, "2R08"), (mine.Kind, mine.Seat, mine.Ref));
        var rev = g.Revision;
        Ok(Do(g, 0, new(OnlineCommandType.MovePiece, PieceId: mine.Id, X: 50, Y: 120.5), d));
        Assert.Equal((50.0, 120.5), (mine.X, mine.Y));
        Assert.True(g.Revision > rev);

        // Rotation alone keeps the place; the opponent moves it too, as on Vassal.
        Ok(Do(g, 0, new(OnlineCommandType.MovePiece, PieceId: mine.Id, Rotation: -30), d));
        Assert.Equal((50.0, 120.5, 330), (mine.X, mine.Y, mine.Rotation));
        Ok(Do(g, 1, new(OnlineCommandType.MovePiece, PieceId: mine.Id, X: 10, Y: 10), d));
        Assert.Equal((10.0, 10.0), (mine.X, mine.Y));

        // A second token of the same cog (a copy: handy to mark a place); each one removed alone.
        var second = Cog(g, d, 0, "2R08", by: 1);
        Assert.Equal(2, g.Table!.Pieces.Count(p => p.Ref == "2R08" && p.Seat == 0));
        Ok(Do(g, 1, new(OnlineCommandType.RemovePiece, PieceId: mine.Id), d));
        Assert.Equal(second.Id, Assert.Single(g.Table.Pieces).Id);

        // A cog of a cabal only, on the side asked for (3R06: in both cabals); none asked: the player's.
        Assert.Equal("onlineTablePiece", Key(Do(g, 0, new(OnlineCommandType.AddPiece, "1R03", Piece: PieceKind.Cog, X: 10, Y: 10), d)));
        Assert.Equal("onlineTablePiece", Key(Do(g, 0, new(OnlineCommandType.AddPiece, "2R11", Piece: PieceKind.Cog, Side: 0, X: 10, Y: 10), d)));
        Assert.Equal(1, Cog(g, d, 1, "3R06").Seat);
        Ok(Do(g, 0, new(OnlineCommandType.AddPiece, "3R06", Piece: PieceKind.Cog, X: 10, Y: 10), d));
        Assert.Equal(0, g.Table.Pieces.Last().Seat);
        Assert.Equal(1, Cog(g, d, 1, "2R11", by: 0).Seat);           // Bo's cog, found without asking

        // Around the board only, both coordinates.
        Assert.Equal("onlineTablePosition", Key(Do(g, 0, new(OnlineCommandType.MovePiece, PieceId: second.Id), d)));
        Assert.Equal("onlineTablePosition", Key(Do(g, 0, new(OnlineCommandType.MovePiece, PieceId: second.Id, X: 500, Y: 10), d)));
        Assert.Equal("onlineTablePosition", Key(Do(g, 0, new(OnlineCommandType.MovePiece, PieceId: second.Id, X: 10), d)));
        Assert.Equal("onlineTablePosition", Key(Do(g, 0, new(OnlineCommandType.MovePiece, PieceId: second.Id, X: double.NaN, Y: 10), d)));
        Assert.Equal("onlineTablePosition", Key(Do(g, 0, new(OnlineCommandType.AddPiece, "2R08", Piece: PieceKind.Cog), d)));
        Assert.Equal("onlineTablePiece", Key(Do(g, 0, new(OnlineCommandType.MovePiece, PieceId: 999, X: 10, Y: 10), d)));
    }

    [Fact]
    public async Task Terrain_markers_and_equipment_tokens_by_either_player()
    {
        var (d, g) = await InCycle();
        var cuve = Manifest.Value.Terrain.First(t => t.Type == "cuve").Id;
        Ok(Do(g, 0, new(OnlineCommandType.AddPiece, cuve, Piece: PieceKind.Terrain, X: 200, Y: 200, Rotation: 90), d));
        var terrain = g.Table!.Pieces.Single(p => p.Kind == PieceKind.Terrain);
        Assert.Null(terrain.Seat);
        Ok(Do(g, 1, new(OnlineCommandType.MovePiece, PieceId: terrain.Id, X: 180, Y: 220), d));
        Assert.Equal((180.0, 220.0, 90), (terrain.X, terrain.Y, terrain.Rotation));
        Assert.Equal("tableAdd", g.Log[^1].Key);
        Ok(Do(g, 1, new(OnlineCommandType.RemovePiece, PieceId: terrain.Id), d));
        Assert.Equal("tableRemove", g.Log[^1].Key);

        Ok(Do(g, 1, new(OnlineCommandType.AddPiece, "objective", Piece: PieceKind.Marker, X: 10, Y: 10), d));
        Assert.Equal("onlineTablePiece", Key(Do(g, 0, new(OnlineCommandType.AddPiece, "nothing", Piece: PieceKind.Terrain, X: 10, Y: 10), d)));

        // Equipment: a card of either cabal (Carapace: Ana's, Ration: Bo's), put down by either player, of its side.
        Assert.Equal("onlineTablePiece", Key(Do(g, 0, new(OnlineCommandType.AddPiece, "E01", Piece: PieceKind.Equipment, X: 10, Y: 10), d)));
        Ok(Do(g, 0, new(OnlineCommandType.AddPiece, "E07", Piece: PieceKind.Equipment, X: 30, Y: 30), d));
        var token = g.Table.Pieces.Single(p => p.Kind == PieceKind.Equipment);
        Assert.Equal(1, token.Seat);
        Ok(Do(g, 0, new(OnlineCommandType.RemovePiece, PieceId: token.Id), d));
        Ok(Do(g, 0, new(OnlineCommandType.AddPiece, "E03", Piece: PieceKind.Equipment, Side: 1, X: 30, Y: 30), d));     // in both cabals: Bo's
        Assert.Equal(1, g.Table.Pieces.Last().Seat);

        // Board: one of the manifest's.
        Ok(Do(g, 1, new(OnlineCommandType.SetBoard, "karst"), d));
        Assert.Equal("karst", g.Table.Board);
        Assert.Equal("onlineTablePiece", Key(Do(g, 1, new(OnlineCommandType.SetBoard, "moon"), d)));
    }

    [Fact]
    public async Task Terrain_locked_in_place_and_objectives_taken_by_a_side()
    {
        var (d, g) = await InCycle();
        Ok(Do(g, 0, new(OnlineCommandType.AddPiece, Manifest.Value.Terrain[0].Id, Piece: PieceKind.Terrain, X: 100, Y: 100), d));
        var terrain = g.Table!.Pieces.Last();
        Ok(Do(g, 1, new(OnlineCommandType.SetLock, PieceId: terrain.Id, Flag: true), d));
        Assert.True(terrain.Locked);
        Assert.Equal("onlineTableLocked", Key(Do(g, 0, new(OnlineCommandType.MovePiece, PieceId: terrain.Id, X: 10, Y: 10), d)));
        Assert.Equal("onlineTableLocked", Key(Do(g, 0, new(OnlineCommandType.MovePiece, PieceId: terrain.Id, Rotation: 15), d)));
        Assert.Equal("onlineTableLocked", Key(Do(g, 0, new(OnlineCommandType.RemovePiece, PieceId: terrain.Id), d)));
        Ok(Do(g, 0, new(OnlineCommandType.SetLock, PieceId: terrain.Id, Flag: false), d));
        Ok(Do(g, 0, new(OnlineCommandType.MovePiece, PieceId: terrain.Id, X: 10, Y: 10), d));

        // An objective: taken by one side, then both (either player sets it), then given back.
        Ok(Do(g, 0, new(OnlineCommandType.AddPiece, "objective", Piece: PieceKind.Marker, X: 200, Y: 200), d));
        var objective = g.Table.Pieces.Last();
        Ok(Do(g, 0, new(OnlineCommandType.SetClaim, PieceId: objective.Id, Side: 1, Flag: true), d));
        Ok(Do(g, 1, new(OnlineCommandType.SetClaim, PieceId: objective.Id, Side: 0, Flag: true), d));
        Ok(Do(g, 1, new(OnlineCommandType.SetClaim, PieceId: objective.Id, Side: 0, Flag: true), d));
        Assert.Equal([0, 1], objective.Claims);
        Ok(Do(g, 0, new(OnlineCommandType.SetClaim, PieceId: objective.Id, Side: 1, Flag: false), d));
        Assert.Equal([0], OnlineRules.View(g, null, new HashSet<int>()).Table!.Pieces.Last().Claims);

        Assert.Equal("onlineTablePiece", Key(Do(g, 0, new(OnlineCommandType.SetClaim, PieceId: objective.Id, Side: 2, Flag: true), d)));
        Assert.Equal("onlineTablePiece", Key(Do(g, 0, new(OnlineCommandType.SetClaim, PieceId: terrain.Id, Side: 0, Flag: true), d)));
        Assert.Equal("onlineTablePiece", Key(Do(g, 0, new(OnlineCommandType.SetLock, PieceId: objective.Id, Flag: true), d)));
    }

    [Fact]
    public async Task Ranges_around_a_piece_set_by_either_player_and_seen_by_everyone()
    {
        var (d, g) = await InCycle();
        var mine = Cog(g, d, 0, "2R08");
        Ok(Do(g, 1, new(OnlineCommandType.SetAuras, PieceId: mine.Id, Auras: [3, 1, 3]), d));       // as on Vassal: either player
        Assert.Equal([1, 3], mine.Auras);
        Assert.Equal([1, 3], OnlineRules.View(g, null, new HashSet<int>()).Table!.Pieces.Single(p => p.Id == mine.Id).Auras);
        Ok(Do(g, 0, new(OnlineCommandType.SetAuras, PieceId: mine.Id, Auras: []), d));
        Assert.Empty(mine.Auras);

        Assert.Equal("onlineTableAura", Key(Do(g, 0, new(OnlineCommandType.SetAuras, PieceId: mine.Id, Auras: [10]), d)));
        Assert.Equal("onlineTableAura", Key(Do(g, 0, new(OnlineCommandType.SetAuras, PieceId: mine.Id, Auras: [0]), d)));
        Assert.Equal("onlineTableAura", Key(Do(g, 0, new(OnlineCommandType.SetAuras, PieceId: mine.Id), d)));
        var cuve = Manifest.Value.Terrain[0].Id;
        Ok(Do(g, 0, new(OnlineCommandType.AddPiece, cuve, Piece: PieceKind.Terrain, X: 100, Y: 100), d));
        var terrain = g.Table!.Pieces.Single(p => p.Kind == PieceKind.Terrain);
        Assert.Equal("onlineTablePiece", Key(Do(g, 0, new(OnlineCommandType.SetAuras, PieceId: terrain.Id, Auras: [2]), d)));
    }

    [Fact]
    public async Task Movement_templates_put_down_moved_and_magnetic_by_either_player()
    {
        var (d, g) = await InCycle();
        Ok(Do(g, 0, new(OnlineCommandType.AddPiece, "move", Piece: PieceKind.Template, X: 100, Y: 100), d));
        var template = g.Table!.Pieces.Single(p => p.Kind == PieceKind.Template);
        Assert.Equal((null, true), (template.Seat, template.Magnet));           // magnetic by default
        Ok(Do(g, 1, new(OnlineCommandType.MovePiece, PieceId: template.Id, X: 120, Y: 90, Rotation: 45), d));
        Assert.Equal((120.0, 90.0, 45), (template.X, template.Y, template.Rotation));
        Ok(Do(g, 1, new(OnlineCommandType.SetMagnet, PieceId: template.Id, Flag: false), d));
        Assert.False(OnlineRules.View(g, 0, new HashSet<int>()).Table!.Pieces.Single(p => p.Id == template.Id).Magnet);
        Ok(Do(g, 0, new(OnlineCommandType.SetMagnet, PieceId: template.Id, Flag: true), d));
        Assert.True(template.Magnet);

        Assert.Equal("onlineTablePiece", Key(Do(g, 0, new(OnlineCommandType.SetMagnet, PieceId: Cog(g, d, 0, "2R08").Id, Flag: true), d)));
        Assert.Equal("onlineTablePiece", Key(Do(g, 0, new(OnlineCommandType.SetAuras, PieceId: template.Id, Auras: [1]), d)));
        Assert.Equal("onlineTablePiece", Key(Do(g, 0, new(OnlineCommandType.AddPiece, "ladder", Piece: PieceKind.Template, X: 10, Y: 10), d)));
        Ok(Do(g, 1, new(OnlineCommandType.RemovePiece, PieceId: template.Id), d));
    }

    [Fact]
    public async Task Table_only_between_the_start_and_the_end_and_limited()
    {
        var (d, g) = await InCycle();
        var marker = new OnlineCommand(OnlineCommandType.AddPiece, "polisec", Piece: PieceKind.Marker, X: 5, Y: 5);
        while (g.Table!.Pieces.Count < TableRules.MaxPieces) Ok(Do(g, 0, marker, d));
        Assert.Equal("onlineTableFull", Key(Do(g, 0, marker, d)));

        g.Step = OnlineStep.Finished;
        Assert.Equal("onlineStep", Key(Do(g, 0, new(OnlineCommandType.MovePiece, PieceId: 1, X: 5, Y: 5), d)));
    }

    [Fact]
    public async Task A_game_stored_before_the_table_gets_one_at_its_first_command()
    {
        var (d, g) = await InCycle();
        var json = JsonSerializer.Serialize(g, TournamentJson.Options);
        var old = JsonSerializer.Deserialize<OnlineGame>(json.Replace("\"table\":", "\"oldTable\":"), TournamentJson.Options)!;
        Assert.Null(old.Table);
        var token = Cog(old, d, 0, old.Seats[0].State!.Cogs[0].CogId, x: 100, y: 100);
        Assert.Equal((TableRules.DefaultBoard, 1), (old.Table!.Board, old.Table.Pieces.Count));
        Assert.True(token.OnBoard);
    }

    // ---- Result ----

    [Fact]
    public async Task Result_confirmed_once_the_game_is_over_by_either_player()
    {
        var (d, g) = await InCycle();
        Assert.Equal("onlineNotOver", Key(Do(g, 0, new(OnlineCommandType.ConfirmResult, Vp: [5, 3]), d)));

        // Bile at 3 for Bo: over, the end is the Bile.
        var s = g.Seats[1];
        var next = GameSession.FromJson(GameSession.ToJson(s.State!))!;
        next.Bile = GameSession.MaxBile;
        Ok(Do(g, 1, new(OnlineCommandType.UpdateState, State: next, BasedOn: s.StateRevision), d));
        Assert.Equal("victoryPoints", Key(Do(g, 0, new(OnlineCommandType.ConfirmResult, Vp: [5]), d)));
        Assert.Equal("victoryPoints", Key(Do(g, 0, new(OnlineCommandType.ConfirmResult, Vp: [5, -1]), d)));
        Ok(Do(g, 0, new(OnlineCommandType.ConfirmResult, Vp: [5, 3]), d));
        Assert.Equal(OnlineStep.Finished, g.Step);
        Assert.Equal([5, 3], g.Result!.Vp);
        Assert.Equal((GameEnd.Bile, (int?)0, false), (g.Result.End, g.Result.ConfirmedBy, g.Result.Recorded));
        Assert.Equal("result", g.Log[^1].Key);

        // Corrected by the other player: replaced.
        Ok(Do(g, 1, new(OnlineCommandType.ConfirmResult, Vp: [6, 3]), d));
        Assert.Equal((6, (int?)1), (g.Result!.Vp[0], g.Result.ConfirmedBy));
        Assert.Equal(g.Result, OnlineRules.View(g, null, new HashSet<int>()).Result);
    }

    [Fact]
    public async Task End_of_the_game_and_its_duel_for_the_Block()
    {
        var (_, g) = await InCycle();
        Assert.Equal(GameEnd.FourthCycle, OnlineRules.EndOf(g));
        g.Seats[0].State!.Hunt = 2;
        g.Seats[1].State!.Hunt = 1;
        Assert.Equal(GameEnd.PoliSec, OnlineRules.EndOf(g));
        Assert.True(OnlineRules.Ended(g));

        g.Seats[0].State!.VictoryPoints = 4;
        var result = OnlineRules.ResultFromSheets(g, Now);
        Assert.Equal([4, 0], result.Vp);
        Assert.Null(result.ConfirmedBy);
        var duel = OnlineRules.ZoneGameOf(g, result)!;
        Assert.Equal((GameFormat.Duel, DateOnly.FromDateTime(Now.UtcDateTime), TableSide.B, GameEnd.PoliSec, "VISITS"),
            (duel.Format, duel.PlayedOn, duel.First, duel.End, duel.Scenario?.Id));
        Assert.Equal(("MAISEL", 4, 0), (duel.A.Players[0].Faction, duel.A.Vp, duel.B.Vp));
        Assert.All(duel.Players, p => Assert.Null(p.Name));
        Assert.All(duel.Players, p => Assert.Null(p.Cabal!.Name));             // the cabal's name may be a person's
        Assert.Equal(3, duel.B.Players[0].Cabal!.Cogs.Count);
    }

    // ---- Assets ----

    [Fact]
    public void Every_image_of_the_table_exists()
    {
        var m = Manifest.Value;
        Assert.NotEmpty(m.Boards);
        Assert.Contains(m.Boards, b => b.Id == TableRules.DefaultBoard);
        var images = m.Boards.Select(b => b.Image).Concat(m.Terrain.Select(t => t.Image)).Concat(m.Markers.Select(x => x.Image)).Concat(m.Tokens.Select(t => t.Image)).Concat((m.Templates ?? []).Select(t => t.Image));
        Assert.All(images, image =>
        {
            Assert.StartsWith(TableManifest.Images, image);
            Assert.True(File.Exists(Path.Combine(AppData.WwwRoot, image)), image);
        });
        Assert.All(m.Boards, b => Assert.Equal(TableManifest.BoardSize, b.Size));
        Assert.All(m.Terrain, t => Assert.True(t.Width > 0 && t.Height > 0, t.Id));
        Assert.Equal(["move", "push"], (m.Templates ?? []).Select(t => t.Id));
        Assert.All(m.Templates!, t => Assert.True(t.Length >= t.Width && t.Width > 0, t.Id));
    }

    [Fact]
    public async Task Tokens_are_catalog_cards_with_a_base()
    {
        var d = await AppData.GetAsync();
        Assert.All(Manifest.Value.Tokens, t => Assert.True(d.Catalog.CogById(t.Id) is not null || d.Catalog.EquipmentById(t.Id) is not null, t.Id));
        Assert.All(Manifest.Value.Tokens, t => Assert.Contains(t.Base, new[] { 30, 40, 50 }));
        // Nearly every cog has its token (a newer one may not be in the module yet: drawn with its initials).
        var cogs = d.Catalog.Catalog.Cogs.Where(c => c.Published).ToList();
        Assert.True(cogs.Count(c => Manifest.Value.Token(c.Id) is null) <= 2, string.Join(", ", cogs.Where(c => Manifest.Value.Token(c.Id) is null).Select(c => c.Id)));
    }

    [Fact]
    public void Every_board_terrain_and_marker_is_named_in_each_language()
    {
        var m = Manifest.Value;
        var keys = m.Boards.Select(b => $"online.table.board.{b.Id}")
            .Concat(m.Terrain.Select(t => t.Type).Distinct().Select(t => $"online.table.terrain.{t}"))
            .Concat(m.Markers.Select(x => $"online.table.marker.{x.Id}"))
            .Concat((m.Templates ?? []).Select(x => $"online.table.template.{x.Id}"));
        foreach (var lang in new[] { "fr", "en" })
        {
            var ui = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppData.WwwRoot, "i18n", lang, "ui.json"))).RootElement;
            Assert.All(keys, k => Assert.True(ui.TryGetProperty(k, out _), $"{lang}: {k}"));
        }
    }
}

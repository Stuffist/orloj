using Olomoc.Tournaments;

namespace Olomoc.Online;

/// <summary>
/// The table of an online game, played freely as on Vassal: the server keeps and relays the
/// positions, it does not check movements, nor who moves what: either player moves any piece (a
/// spectator none). It only checks that the piece exists and that it stays around the board. Seat:
/// the side a cog or an equipment token belongs to (its color). From the initiative (cabals known) to the end of the game.
/// </summary>
public static class TableRules
{
    public const string DefaultBoard = "mez";
    public const int MaxPieces = 80;
    /// <summary>A piece may stick out of the board by this much (mm), not more.</summary>
    public const double Margin = 30;
    /// <summary>Ranges shown around a piece: 1 to this many inches.</summary>
    public const int MaxAura = 9;

    /// <summary>
    /// The table at the start (or of a game stored before the table): the default mat, nothing on it. A
    /// cog is put down as tokens, as many as wanted (handy to mark a place); off the Block: none.
    /// </summary>
    public static OnlineTable Create(OnlineGame g) => new() { Board = DefaultBoard };

    public static Problem? Apply(OnlineGame g, int seat, OnlineCommand c, TableManifest? manifest, DateTimeOffset now)
    {
        if (g.Step is OnlineStep.Lobby or OnlineStep.Finished || g.Seats.Any(s => s.State is null)) return Problem.Of("onlineStep");
        var table = g.Table ??= Create(g);
        return c.Type switch
        {
            OnlineCommandType.SetBoard => SetBoard(g, table, seat, c.Text, manifest, now),
            OnlineCommandType.AddPiece => Add(g, table, seat, c, manifest, now),
            OnlineCommandType.MovePiece => Move(table, seat, c),
            OnlineCommandType.RemovePiece => Remove(g, table, seat, c.PieceId, now),
            OnlineCommandType.SetAuras => SetAuras(table, c.PieceId, c.Auras),
            OnlineCommandType.SetMagnet => SetMagnet(table, c.PieceId, c.Flag == true),
            OnlineCommandType.SetLock => SetLock(table, c.PieceId, c.Flag == true),
            OnlineCommandType.SetClaim => SetClaim(g, table, c.PieceId, c.Side, c.Flag == true),
            _ => Problem.Of("onlineCommand"),
        };
    }

    private static Problem? SetBoard(OnlineGame g, OnlineTable table, int seat, string? id, TableManifest? manifest, DateTimeOffset now)
    {
        if (manifest?.Board(id) is null) return Problem.Of("onlineTablePiece");
        if (table.Board == id) return null;
        table.Board = id!;
        OnlineRules.Log(g, seat, now, "tableBoard", id!);
        return null;
    }

    // The side a cog's or a card's token belongs to: the one asked for (Side), otherwise the player's own
    // first (both cabals may carry it); null if no cabal carries it.
    private static int? Owner(OnlineGame g, int seat, OnlineCommand c)
    {
        bool Carries(int s) => (g.Seats[s].State?.Cogs ?? []).Any(x => c.Piece == PieceKind.Cog
            ? x.CogId == c.Text : x.EquipmentId == c.Text || x.ExtraEquipmentId == c.Text);
        var sides = c.Side is { } side ? [side] : new[] { seat, 1 - seat };
        return c.Text is null ? null : sides.Where(s => s >= 0 && s < g.Seats.Count).Cast<int?>().FirstOrDefault(s => Carries(s!.Value));
    }

    private static Problem? Add(OnlineGame g, OnlineTable table, int seat, OnlineCommand c, TableManifest? manifest, DateTimeOffset now)
    {
        if (table.Pieces.Count >= MaxPieces) return Problem.Of("onlineTableFull", MaxPieces.ToString());
        var known = c.Piece switch
        {
            PieceKind.Terrain => manifest?.TerrainOf(c.Text) is not null,
            PieceKind.Marker => manifest?.Marker(c.Text) is not null,
            PieceKind.Template => manifest?.Template(c.Text) is not null,
            // A token of a cog or of an equipment/boon of a cabal (it belongs to that side).
            PieceKind.Cog or PieceKind.Equipment => Owner(g, seat, c) is not null,
            _ => false,
        };
        if (!known) return Problem.Of("onlineTablePiece");
        if (c.X is null || c.Y is null || Position(c) is not null) return Problem.Of("onlineTablePosition");      // put down somewhere
        var piece = new TablePiece
        {
            Id = table.NextId++, Kind = c.Piece!.Value, Ref = c.Text!, Seat = c.Piece is PieceKind.Cog or PieceKind.Equipment ? Owner(g, seat, c) : null,
            X = c.X, Y = c.Y, Rotation = Normal(c.Rotation ?? 0),
            Magnet = c.Piece == PieceKind.Template,         // movement templates: magnetic by default
        };
        table.Pieces.Add(piece);
        if (piece.Kind == PieceKind.Terrain) OnlineRules.Log(g, seat, now, "tableAdd", piece.Ref);
        return null;
    }

    // A move: position and/or rotation (a rotation alone keeps the place). The last one wins.
    private static Problem? Move(OnlineTable table, int seat, OnlineCommand c)
    {
        if (Find(table, c.PieceId) is not { } piece) return Problem.Of("onlineTablePiece");
        if (piece.Locked) return Problem.Of("onlineTableLocked");
        if (Position(c) is { } bad) return bad;
        if (c.X is { } x && c.Y is { } y) (piece.X, piece.Y) = (x, y);
        else if (c.Rotation is null) return Problem.Of("onlineTablePosition");
        if (c.Rotation is { } r) piece.Rotation = Normal(r);
        return null;
    }

    // Ranges around a piece (any player, any piece with a base, as on Vassal): the list replaces the
    // previous one (empty: none).
    private static Problem? SetAuras(OnlineTable table, int? id, IReadOnlyList<int>? auras)
    {
        if (Find(table, id) is not { Kind: not (PieceKind.Terrain or PieceKind.Template) } piece) return Problem.Of("onlineTablePiece");
        if (auras is null || auras.Any(a => a is < 1 or > MaxAura)) return Problem.Of("onlineTableAura", MaxAura.ToString());
        piece.Auras = auras.Distinct().Order().ToList();
        return null;
    }

    // A movement template magnetic or not (either player).
    private static Problem? SetMagnet(OnlineTable table, int? id, bool on)
    {
        if (Find(table, id) is not { Kind: PieceKind.Template } piece) return Problem.Of("onlineTablePiece");
        piece.Magnet = on;
        return null;
    }

    // Terrain or a marker (an objective) locked in place or not (either player).
    private static Problem? SetLock(OnlineTable table, int? id, bool on)
    {
        if (Find(table, id) is not { Kind: PieceKind.Terrain or PieceKind.Marker } piece) return Problem.Of("onlineTablePiece");
        piece.Locked = on;
        return null;
    }

    // A marker taken by a side or not (either player sets it, for either side).
    private static Problem? SetClaim(OnlineGame g, OnlineTable table, int? id, int? side, bool on)
    {
        if (Find(table, id) is not { Kind: PieceKind.Marker } piece || side is not { } s || s < 0 || s >= g.Seats.Count)
            return Problem.Of("onlineTablePiece");
        piece.Claims.Remove(s);
        if (on) piece.Claims.Add(s);
        piece.Claims.Sort();
        return null;
    }

    private static Problem? Remove(OnlineGame g, OnlineTable table, int seat, int? id, DateTimeOffset now)
    {
        if (Find(table, id) is not { } piece) return Problem.Of("onlineTablePiece");
        if (piece.Locked) return Problem.Of("onlineTableLocked");
        table.Pieces.Remove(piece);
        if (piece.Kind == PieceKind.Terrain) OnlineRules.Log(g, seat, now, "tableRemove", piece.Ref);
        return null;
    }

    private static TablePiece? Find(OnlineTable table, int? id) => table.Pieces.FirstOrDefault(p => p.Id == id);

    // Both coordinates or none, around the board.
    private static Problem? Position(OnlineCommand c)
    {
        if (c.X is null != c.Y is null) return Problem.Of("onlineTablePosition");
        if (c.X is { } x && c.Y is { } y && (!Inside(x) || !Inside(y))) return Problem.Of("onlineTablePosition");
        return null;
    }

    private static bool Inside(double v) => double.IsFinite(v) && v >= -Margin && v <= TableManifest.BoardSize + Margin;

    private static int Normal(int degrees) => (degrees % 360 + 360) % 360;
}

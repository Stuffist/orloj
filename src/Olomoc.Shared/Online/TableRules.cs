using Olomoc.Tournaments;

namespace Olomoc.Online;

/// <summary>
/// The table of an online game, played freely as on Vassal: the server keeps and relays the
/// positions, it does not check movements. It only checks who moves what (a player's cogs and
/// equipment tokens: that player; terrain, markers and movement templates: either player), that the piece exists, and
/// that it stays around the board. From the initiative (cabals known) to the end of the game.
/// </summary>
public static class TableRules
{
    public const string DefaultBoard = "mez";
    public const int MaxPieces = 80;
    /// <summary>A piece may stick out of the board by this much (mm), not more.</summary>
    public const double Margin = 30;
    /// <summary>Ranges shown around a piece: 1 to this many inches.</summary>
    public const int MaxAura = 9;

    /// <summary>The table at the start (or of a game stored before the table): each side's cogs, off the board.</summary>
    public static OnlineTable Create(OnlineGame g)
    {
        var table = new OnlineTable { Board = DefaultBoard };
        for (var seat = 0; seat < g.Seats.Count; seat++)
            foreach (var cog in (g.Seats[seat].State?.Cogs ?? []).OrderBy(c => c.Rank))
                table.Pieces.Add(new TablePiece { Id = table.NextId++, Kind = PieceKind.Cog, Ref = cog.CogId, Seat = seat });
        return table;
    }

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

    private static Problem? Add(OnlineGame g, OnlineTable table, int seat, OnlineCommand c, TableManifest? manifest, DateTimeOffset now)
    {
        if (table.Pieces.Count >= MaxPieces) return Problem.Of("onlineTableFull", MaxPieces.ToString());
        var known = c.Piece switch
        {
            PieceKind.Terrain => manifest?.TerrainOf(c.Text) is not null,
            PieceKind.Marker => manifest?.Marker(c.Text) is not null,
            PieceKind.Template => manifest?.Template(c.Text) is not null,
            // An equipment/boon token: one of the player's own cards.
            PieceKind.Equipment => c.Text is { } id && (g.Seats[seat].State?.Cogs ?? []).Any(x => x.EquipmentId == id || x.ExtraEquipmentId == id),
            _ => false,                     // cogs: created with the table
        };
        if (!known) return Problem.Of("onlineTablePiece");
        if (Position(c) is { } bad) return bad;
        var piece = new TablePiece
        {
            Id = table.NextId++, Kind = c.Piece!.Value, Ref = c.Text!, Seat = c.Piece == PieceKind.Equipment ? seat : null,
            X = c.X, Y = c.Y, Rotation = Normal(c.Rotation ?? 0),
        };
        table.Pieces.Add(piece);
        if (piece.Kind == PieceKind.Terrain) OnlineRules.Log(g, seat, now, "tableAdd", piece.Ref);
        return null;
    }

    // A move: position and/or rotation (a rotation alone keeps the place); neither: a cog back off the
    // board. The last one wins.
    private static Problem? Move(OnlineTable table, int seat, OnlineCommand c)
    {
        if (Find(table, c.PieceId) is not { } piece) return Problem.Of("onlineTablePiece");
        if (piece.Seat is { } owner && owner != seat) return Problem.Of("onlineTableNotYours");
        if (Position(c) is { } bad) return bad;
        if (c.X is { } x && c.Y is { } y) (piece.X, piece.Y) = (x, y);
        else if (c.Rotation is null)            // nothing but "off the board": a cog back to its tray
        {
            if (piece.Kind != PieceKind.Cog) return Problem.Of("onlineTablePosition");
            (piece.X, piece.Y) = (null, null);
        }
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

    private static Problem? Remove(OnlineGame g, OnlineTable table, int seat, int? id, DateTimeOffset now)
    {
        if (Find(table, id) is not { } piece) return Problem.Of("onlineTablePiece");
        if (piece.Kind == PieceKind.Cog) return Problem.Of("onlineTableCog");
        if (piece.Seat is { } owner && owner != seat) return Problem.Of("onlineTableNotYours");
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

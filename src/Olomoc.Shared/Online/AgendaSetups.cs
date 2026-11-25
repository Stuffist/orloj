using Olomoc.Tournaments;

namespace Olomoc.Online;

// Setup of each agenda on the mat (Recueil des agendas, Occicat, January 2026): what "Placer" puts
// down in an online game. Positions in inches from the centre of the mat, x to the right, y down
// (the deployment lines are the top and bottom edges). Where an agenda leaves a choice ("on one of
// the median lines", "on one of the diagonals"), each choice is a variant.

/// <summary>
/// An element of an agenda: a marker of the table (objective marker by default, base 30 mm) or a
/// terrain (zone de Fiel…); Label: shown on it ("2", or the kind's name: texts
/// "online.agenda.element.{kind}"); Owner: 0 for player 1, 1 for player 2 (its color), null: none.
/// </summary>
public sealed record AgendaElement(string Kind, double X, double Y, string? Number = null, PieceKind Piece = PieceKind.Marker,
    string Ref = "objective", int? Owner = null);

/// <summary>A choice of placement (texts "online.agenda.variant.{id}").</summary>
public sealed record AgendaVariant(string Id, IReadOnlyList<AgendaElement> Elements);

/// <summary>
/// The setup of an agenda: its variants (none: nothing to place, the note says what to do), the
/// note of the rest of the setup (texts "online.agenda.note.{id}").
/// </summary>
public sealed record AgendaSetup(string Id, IReadOnlyList<AgendaVariant> Variants);

public static class AgendaSetups
{
    // 6″ along a diagonal: 6 / √2 on each axis.
    private const double D = 4.243;

    public static readonly IReadOnlyList<AgendaSetup> All =
    [
        // 3 secrets on a median line (the centre and 6″ on each side), 2 on a diagonal at 6″ from the centre.
        new("PROSPECTION",
        [
            Variant("h-rise", Secrets((-6, 0), (0, 0), (6, 0), (-D, D), (D, -D))),
            Variant("h-fall", Secrets((-6, 0), (0, 0), (6, 0), (-D, -D), (D, D))),
            Variant("v-rise", Secrets((0, -6), (0, 0), (0, 6), (-D, D), (D, -D))),
            Variant("v-fall", Secrets((0, -6), (0, 0), (0, 6), (-D, -D), (D, D))),
        ]),
        // The anomaly at the centre.
        new("QUANTUM_ANOMALY", [Variant("centre", [new("anomaly", 0, 0)])]),
        // Nothing on the mat: the 3 Rogue cards on top of the Artefact.
        new("VISITS", []),
        // 3 doors on a diagonal: 2 at the centre, 3 and 4 at 6″ on each side.
        new("LOST_HYPOGEUM",
        [
            Variant("rise", [new("door", 0, 0, "2"), new("door", D, -D, "3"), new("door", -D, D, "4")]),
            Variant("fall", [new("door", 0, 0, "2"), new("door", -D, -D, "3"), new("door", D, D, "4")]),
        ]),
        // Each player puts a battery more than 4″ from the edges, after the first Mécanisme: put down
        // in the middle, to move.
        new("QUANTAR_BATTERIES",
        [
            Variant("players", [new("battery", 0, 4, Owner: 0), new("battery", 0, -4, Owner: 1)]),
        ]),
        // The 3 objective terrain pieces are chosen and placed with the terrain.
        new("SABOTAGE", []),
        // The Gargoyle (base 50 mm) at the centre.
        new("GARGOYLE", [Variant("centre", [new("gargoyle", 0, 0, Ref: "gargoyle")])]),
        // Accumulator 2 at the centre, 1 on the median line at 6″ in one half, 3 and 4 on the diagonals
        // of the other half, at 6″.
        new("PNEUMATIC_NETWORK",
        [
            Variant("right", Accumulators((6, 0), (-D, -D), (-D, D))),
            Variant("left", Accumulators((-6, 0), (D, -D), (D, D))),
            Variant("top", Accumulators((0, -6), (-D, D), (D, D))),
            Variant("bottom", Accumulators((0, 6), (-D, -D), (D, -D))),
        ]),
        // Robert Kadlec joins the escort's cabal: nothing on the mat.
        new("CARTOGRAPHER", []),
        // 4 secrets as in the drawing, each one on a zone de Fiel.
        new("ALONE_IN_THE_DARK",
        [
            Variant("drawing", Dark((-5, -5), (0, 0), (6, 0), (-D, D))),
            Variant("mirror", Dark((5, -5), (0, 0), (-6, 0), (D, D))),
        ]),
    ];

    public static AgendaSetup? Of(string? agenda) => All.FirstOrDefault(a => a.Id == agenda);

    private static AgendaVariant Variant(string id, IReadOnlyList<AgendaElement> elements) => new(id, elements);

    private static IReadOnlyList<AgendaElement> Secrets(params (double X, double Y)[] at) =>
        at.Select(p => new AgendaElement("secret", p.X, p.Y)).ToList();

    private static IReadOnlyList<AgendaElement> Accumulators((double X, double Y) one, (double X, double Y) three, (double X, double Y) four) =>
        [new("accumulator", 0, 0, "2"), new("accumulator", one.X, one.Y, "1"), new("accumulator", three.X, three.Y, "3"), new("accumulator", four.X, four.Y, "4")];

    // The zones de Fiel first (under the secrets).
    private static IReadOnlyList<AgendaElement> Dark(params (double X, double Y)[] at) =>
    [
        .. at.Select((p, i) => new AgendaElement("fiel", p.X, p.Y, Piece: PieceKind.Terrain, Ref: $"zone-de-fiel-{i + 1}")),
        .. at.Select(p => new AgendaElement("secret", p.X, p.Y)),
    ];

    /// <summary>Position on the mat (mm from its top-left corner) of an element (inches from the centre).</summary>
    public static (double X, double Y) OnMat(AgendaElement e) =>
        (TableManifest.BoardSize / 2 + e.X * TableManifest.MmPerInch, TableManifest.BoardSize / 2 + e.Y * TableManifest.MmPerInch);

    /// <summary>
    /// Puts the agenda's elements on the table (variant: one of the agenda's; ordinary pieces, movable and
    /// lockable). Placing again adds them again.
    /// </summary>
    public static Problem? Place(OnlineGame g, int seat, string? variantId, TableManifest? manifest, DateTimeOffset now)
    {
        if (g.Step is OnlineStep.Lobby or OnlineStep.Finished || g.Seats.Any(s => s.State is null)) return Problem.Of("onlineStep");
        if (Of(g.Agenda?.Id) is not { } setup) return Problem.Of("onlineAgendaNone");
        if (setup.Variants.FirstOrDefault(v => v.Id == variantId) is not { } variant) return Problem.Of("onlineAgendaVariant");
        var table = g.Table ??= TableRules.Create(g);
        if (table.Pieces.Count + variant.Elements.Count > TableRules.MaxPieces) return Problem.Of("onlineTableFull", TableRules.MaxPieces.ToString());
        if (variant.Elements.Any(e => e.Piece == PieceKind.Terrain ? manifest?.TerrainOf(e.Ref) is null : manifest?.Marker(e.Ref) is null))
            return Problem.Of("onlineTablePiece");
        // Player 1 and 2: the seats, by the initiative (P1: g.First).
        int? SeatOf(int? owner) => owner is { } o && g.First is { } first ? (o == 0 ? first : 1 - first) : null;
        foreach (var e in variant.Elements)
        {
            var (x, y) = OnMat(e);
            table.Pieces.Add(new TablePiece
            {
                Id = table.NextId++, Kind = e.Piece, Ref = e.Ref, X = Math.Round(x, 1), Y = Math.Round(y, 1),
                Label = e.Piece == PieceKind.Marker ? e.Kind : null, Number = e.Number, Claims = SeatOf(e.Owner) is { } s ? [s] : [],
            });
        }
        OnlineRules.Log(g, seat, now, "agendaPlaced", setup.Id, variant.Id);
        return null;
    }
}

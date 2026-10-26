using Microsoft.EntityFrameworkCore;
using Olomoc.Server.Data;
using Olomoc.Server.Hosting;
using Olomoc.Tournaments;

namespace Olomoc.Server.Tournaments;

/// <summary>Tournaments stored in the database, one JSON document per tournament (see <see cref="DocumentStore{T}"/>).</summary>
public sealed class TournamentStore(IDbContextFactory<OlomocDb> db, int maxTournaments = 1000)
    : DocumentStore<Tournament>(db, Kind, maxTournaments, TournamentJson.Options)
{
    public const string Kind = "tournament";

    /// <summary>
    /// Deletes the tournaments unchanged since <paramref name="maxAge"/>, except those dated today
    /// or later (never deleted); returns their number.
    /// </summary>
    public Task<int> PurgeAsync(TimeSpan maxAge, DateOnly today) =>
        PurgeAsync(maxAge, t => t.Date is not { } date || date < today);
}

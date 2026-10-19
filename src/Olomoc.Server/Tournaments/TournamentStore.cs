using Microsoft.EntityFrameworkCore;
using Olomoc.Server.Data;
using Olomoc.Server.Hosting;
using Olomoc.Tournaments;

namespace Olomoc.Server.Tournaments;

/// <summary>Tournois enregistrés dans la base, un document JSON par tournoi (voir <see cref="DocumentStore{T}"/>).</summary>
public sealed class TournamentStore(IDbContextFactory<OlomocDb> db, int maxTournaments = 1000)
    : DocumentStore<Tournament>(db, Kind, maxTournaments, TournamentJson.Options)
{
    public const string Kind = "tournament";

    /// <summary>
    /// Supprime les tournois sans modification depuis <paramref name="maxAge"/>, sauf ceux dont la
    /// date est aujourd'hui ou à venir (jamais supprimés) ; renvoie leur nombre.
    /// </summary>
    public Task<int> PurgeAsync(TimeSpan maxAge, DateOnly today) =>
        PurgeAsync(maxAge, t => t.Date is not { } date || date < today);
}

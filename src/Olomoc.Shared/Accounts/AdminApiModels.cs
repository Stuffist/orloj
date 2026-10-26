using Olomoc.Online;
using Olomoc.Tournaments;

namespace Olomoc.Accounts;

// Panneau d'administration (/api/admin, admins seulement) : vue d'ensemble de la base, comptes,
// tournois et parties en ligne de tout le monde, maintenance, journal des actions. Jamais d'e-mail,
// de jeton ni de hash dans ces vues.

/// <summary>Compte vu dans le panneau : rôle, dates, état ; jamais l'e-mail.</summary>
public sealed record AdminAccount(string UserName, AccountRole Role, DateTimeOffset CreatedAt, DateTimeOffset? LastLoginAt,
    bool EmailConfirmed, bool LockedOut, int Cabals);

public sealed record AccountMetrics(int Total, int Max, int Confirmed, int Editors, int Admins, int CreatedLast30Days,
    int ActiveLast30Days, int LockedOut, int SavedCabals);

public sealed record TournamentMetrics(int Total, int Max, int Registration, int Started, int Finished);

public sealed record OnlineMetrics(int Total, int Max, int ActiveLastDay);

/// <summary>Chiffres de la base et état du serveur.</summary>
public sealed record AdminOverview(AccountMetrics Accounts, TournamentMetrics Tournaments, OnlineMetrics Online,
    int StatsArchives, int CatalogChanges, long DatabaseBytes, DateTimeOffset? LastBackup, int Backups,
    bool MailBySmtp, DateTimeOffset StartedAt);

/// <summary>Tournoi vu dans le panneau (sans secrets).</summary>
public sealed record AdminTournament(string Code, string Name, DateOnly? Date, TournamentPhase Phase, int Players,
    int Rounds, int RoundCount, bool HasPassword, bool ExcludeFromStats, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

/// <summary>Partie en ligne vue dans le panneau : noms des sièges (vides tant que personne ne s'est assis).</summary>
public sealed record AdminOnlineGame(string Code, IReadOnlyList<string> Seats, OnlineStep Step, int Cycle,
    OnlineTournamentRef? Tournament, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

/// <summary>
/// Action d'un admin, gardée un an : <see cref="Action"/> est une clé (« role », « deleteTournament »…,
/// textes « control.audit.action.* » de ui.json), <see cref="Target"/> un pseudo ou un code.
/// </summary>
public sealed record AdminAction(DateTimeOffset At, string Admin, string Action, string Target, string? Detail);

public enum PurgeKind { Tournaments, Online }

public sealed record PurgeRequest(PurgeKind Kind);

public sealed record PurgeResult(int Removed);

/// <summary>Sauvegarde faite à la main : nom du fichier (dans DATA_DIR/backups) et taille.</summary>
public sealed record BackupResult(string File, long Bytes);

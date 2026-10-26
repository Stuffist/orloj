using Olomoc.Online;
using Olomoc.Tournaments;

namespace Olomoc.Accounts;

// Admin panel (/api/admin, admins only): database overview, accounts, everyone's tournaments and
// online games, maintenance, action log. Never an email, a token or a hash in these views.

/// <summary>Account as seen in the panel: role, dates, state; never the email.</summary>
public sealed record AdminAccount(string UserName, AccountRole Role, DateTimeOffset CreatedAt, DateTimeOffset? LastLoginAt,
    bool EmailConfirmed, bool LockedOut, int Cabals);

public sealed record AccountMetrics(int Total, int Max, int Confirmed, int Editors, int Admins, int CreatedLast30Days,
    int ActiveLast30Days, int LockedOut, int SavedCabals);

public sealed record TournamentMetrics(int Total, int Max, int Registration, int Started, int Finished);

public sealed record OnlineMetrics(int Total, int Max, int ActiveLastDay);

/// <summary>Database figures and server state.</summary>
public sealed record AdminOverview(AccountMetrics Accounts, TournamentMetrics Tournaments, OnlineMetrics Online,
    int StatsArchives, int CatalogChanges, long DatabaseBytes, DateTimeOffset? LastBackup, int Backups,
    bool MailBySmtp, DateTimeOffset StartedAt);

/// <summary>Tournament as seen in the panel (without secrets).</summary>
public sealed record AdminTournament(string Code, string Name, DateOnly? Date, TournamentPhase Phase, int Players,
    int Rounds, int RoundCount, bool HasPassword, bool ExcludeFromStats, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

/// <summary>Online game as seen in the panel: seat names (empty until someone sits down).</summary>
public sealed record AdminOnlineGame(string Code, IReadOnlyList<string> Seats, OnlineStep Step, int Cycle,
    OnlineTournamentRef? Tournament, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

/// <summary>
/// An admin's action, kept for a year: <see cref="Action"/> is a key ("role", "deleteTournament"…,
/// texts "control.audit.action.*" of ui.json), <see cref="Target"/> a username or a code.
/// </summary>
public sealed record AdminAction(DateTimeOffset At, string Admin, string Action, string Target, string? Detail);

public enum PurgeKind { Tournaments, Online }

public sealed record PurgeRequest(PurgeKind Kind);

public sealed record PurgeResult(int Removed);

/// <summary>Backup made by hand: file name (in DATA_DIR/backups) and size.</summary>
public sealed record BackupResult(string File, long Bytes);

using Microsoft.AspNetCore.Identity;

namespace Olomoc.Server.Data;

/// <summary>
/// Account (ASP.NET Core Identity): username (UserName), email (only to recover the account),
/// password hash, security stamp, lockout. In addition: dates, and the count of emails sent (at
/// most <see cref="Accounts.AccountEndpoints.MailsPerHour"/> per hour).
/// </summary>
public sealed class OlomocUser : IdentityUser
{
    public DateTime CreatedAt { get; set; }
    public DateTime? LastLoginAt { get; set; }
    public DateTime? MailWindowStart { get; set; }
    public int MailsInWindow { get; set; }
}

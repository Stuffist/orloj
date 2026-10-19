using Microsoft.AspNetCore.Identity;

namespace Olomoc.Server.Data;

/// <summary>
/// Compte (ASP.NET Core Identity) : pseudo (UserName), e-mail (seulement pour récupérer le
/// compte), hash du mot de passe, tampon de sécurité, verrouillage. En plus : dates, et le
/// décompte des e-mails envoyés (au plus <see cref="Accounts.AccountEndpoints.MailsPerHour"/>
/// par heure).
/// </summary>
public sealed class OlomocUser : IdentityUser
{
    public DateTime CreatedAt { get; set; }
    public DateTime? LastLoginAt { get; set; }
    public DateTime? MailWindowStart { get; set; }
    public int MailsInWindow { get; set; }
}

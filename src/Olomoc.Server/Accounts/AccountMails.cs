namespace Olomoc.Server.Accounts;

/// <summary>Textes des e-mails, en français ou en anglais (langue du site lors de la demande).</summary>
public static class AccountMails
{
    public static string Language(string? language) => language == "en" ? "en" : "fr";

    public static OutgoingMail Confirm(string to, string userName, string link, string? language) => Language(language) == "en"
        ? new(to, "Orloj — confirm your email address",
            $"""
            Hello {userName},

            To confirm the email address of your Orloj account, open this link (valid for 2 days):
            {link}

            This address is only used to recover your account if you forget your password.
            If you did not create an Orloj account, you can ignore this message.
            """)
        : new(to, "Orloj — confirmez votre adresse e-mail",
            $"""
            Bonjour {userName},

            Pour confirmer l'adresse e-mail de votre compte Orloj, ouvrez ce lien (valable 2 jours) :
            {link}

            Cette adresse ne sert qu'à récupérer votre compte si vous oubliez votre mot de passe.
            Si vous n'avez pas créé de compte Orloj, ignorez ce message.
            """);

    /// <summary>Contrôle mensuel, pour l'admin du site (en français).</summary>
    public static OutgoingMail Check(string to, DateTime now) =>
        new(to, "Orloj — l'envoi des e-mails fonctionne",
            $"""
            Message automatique du site Orloj ({now:yyyy-MM-dd HH:mm} UTC), envoyé tous les {MailCheck.Interval.Days} jours.

            Il garde la clé SMTP active (désactivée après 90 jours sans envoi) et montre que les
            e-mails du site (confirmation d'adresse, mot de passe oublié) partent bien.

            S'il ne vous parvient plus : vérifier SMTP_HOST, SMTP_USER et SMTP_PASSWORD dans le
            .env du VPS, et le journal du conteneur (docker logs olomoc).
            """);

    public static OutgoingMail Reset(string to, string userName, string link, string? language) => Language(language) == "en"
        ? new(to, "Orloj — new password",
            $"""
            Hello {userName},

            To choose a new password for your Orloj account, open this link (valid for 1 hour, once):
            {link}

            If you did not ask for it, ignore this message: your password does not change.
            """)
        : new(to, "Orloj — nouveau mot de passe",
            $"""
            Bonjour {userName},

            Pour choisir un nouveau mot de passe pour votre compte Orloj, ouvrez ce lien (valable 1 heure, une seule fois) :
            {link}

            Si vous n'avez rien demandé, ignorez ce message : votre mot de passe ne change pas.
            """);
}

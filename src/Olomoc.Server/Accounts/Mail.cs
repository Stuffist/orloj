using System.Threading.Channels;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace Olomoc.Server.Accounts;

/// <summary>Email to send (plain text).</summary>
public sealed record OutgoingMail(string To, string Subject, string Body);

public interface IMailSender
{
    Task SendAsync(OutgoingMail mail, CancellationToken stop);
}

/// <summary>
/// Sending settings (environment variables): SMTP relay (SMTP_HOST, SMTP_PORT, SMTP_USER,
/// SMTP_PASSWORD) and sender (MAIL_FROM; default "Orloj &lt;noreply@orloj.top&gt;", the orloj.top
/// instance's, to override elsewhere). Without SMTP_HOST, emails are written to DATA_DIR/mail
/// (development, tests).
/// </summary>
public sealed record MailSettings(string? Host, int Port, string? User, string? Password, string From)
{
    public static MailSettings Read(IConfiguration config) => new(
        NullIfEmpty(config["SMTP_HOST"]), config.GetValue("SMTP_PORT", 587),
        NullIfEmpty(config["SMTP_USER"]), NullIfEmpty(config["SMTP_PASSWORD"]),
        NullIfEmpty(config["MAIL_FROM"]) ?? "Orloj <noreply@orloj.top>");   // orloj.top default

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}

/// <summary>Sending through an SMTP relay (Brevo…), with STARTTLS or TLS depending on the port.</summary>
public sealed class SmtpMailSender(MailSettings settings) : IMailSender
{
    public async Task SendAsync(OutgoingMail mail, CancellationToken stop)
    {
        var message = new MimeMessage();
        message.From.Add(MailboxAddress.Parse(settings.From));
        message.To.Add(MailboxAddress.Parse(mail.To));
        message.Subject = mail.Subject;
        message.Body = new TextPart("plain") { Text = mail.Body };

        using var client = new SmtpClient();
        await client.ConnectAsync(settings.Host ?? throw new InvalidOperationException("SMTP_HOST missing."), settings.Port, SecureSocketOptions.Auto, stop);
        if (settings.User is not null) await client.AuthenticateAsync(settings.User, settings.Password ?? "", stop);
        await client.SendAsync(message, stop);
        await client.DisconnectAsync(true, stop);
    }
}

/// <summary>Without an SMTP relay: one text file per email in DATA_DIR/mail.</summary>
public sealed class FileMailSender(string directory) : IMailSender
{
    public string Directory { get; } = directory;

    public async Task SendAsync(OutgoingMail mail, CancellationToken stop)
    {
        System.IO.Directory.CreateDirectory(Directory);
        var path = Path.Combine(Directory, $"{DateTime.UtcNow:yyyyMMdd-HHmmss-fffffff}-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(path, $"To: {mail.To}\nSubject: {mail.Subject}\n\n{mail.Body}\n", stop);
    }
}

/// <summary>
/// Sending queue: requests answer at once (same delay whether an account exists or not), sending
/// happens in the background; a failure is logged (the user can ask again).
/// </summary>
public sealed class MailQueue(IMailSender sender, ILogger<MailQueue> log) : BackgroundService
{
    private readonly Channel<OutgoingMail> _queue = Channel.CreateBounded<OutgoingMail>(new BoundedChannelOptions(500)
    {
        FullMode = BoundedChannelFullMode.DropWrite,
    });

    public void Enqueue(OutgoingMail mail)
    {
        if (!_queue.Writer.TryWrite(mail)) log.LogWarning("Mail queue full: email dropped.");
    }

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        await foreach (var mail in _queue.Reader.ReadAllAsync(stop))
        {
            try { await sender.SendAsync(mail, stop); }
            catch (Exception e) when (e is not OperationCanceledException) { log.LogWarning(e, "Failed to send an email."); }
        }
    }
}

using System.Buffers;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Connections;

namespace Olomoc.Server.Hosting;

/// <summary>
/// HTTP en clair reçu sur le port HTTPS (http://hôte:8443/…) : réponse 301 vers la même
/// adresse en https://. Toute connexion TLS commence par un enregistrement de négociation
/// (premier octet 0x16) ; le reste est lu comme une requête HTTP/1.x.
/// </summary>
public static partial class PlainHttpRedirect
{
    private const byte TlsHandshake = 0x16;
    private const int MaxHeaderBytes = 8 * 1024;

    /// <summary>Middleware de connexion, placé avant UseHttps.</summary>
    public static ConnectionDelegate Middleware(ConnectionDelegate next) => async connection =>
    {
        var input = connection.Transport.Input;
        var result = await input.ReadAsync();
        var buffer = result.Buffer;
        if (buffer.IsEmpty || buffer.FirstSpan[0] == TlsHandshake)
        {
            input.AdvanceTo(buffer.Start);          // rien de consommé : la négociation TLS suit
            await next(connection);
            return;
        }

        // En-têtes de la requête en clair, jusqu'à la ligne vide (ou la limite).
        while (!result.IsCompleted && buffer.Length < MaxHeaderBytes && !HeadersComplete(buffer))
        {
            input.AdvanceTo(buffer.Start, buffer.End);
            result = await input.ReadAsync();
            buffer = result.Buffer;
        }
        var head = Encoding.ASCII.GetString(buffer.Slice(0, Math.Min(buffer.Length, MaxHeaderBytes)));
        input.AdvanceTo(buffer.End);

        var response = Location(head) is { } location
            ? $"HTTP/1.1 301 Moved Permanently\r\nLocation: {location}\r\nConnection: close\r\nContent-Length: 0\r\n\r\n"
            : "HTTP/1.1 400 Bad Request\r\nConnection: close\r\nContent-Length: 0\r\n\r\n";
        await connection.Transport.Output.WriteAsync(Encoding.ASCII.GetBytes(response));
        await connection.Transport.Output.CompleteAsync();
    };

    /// <summary>
    /// Adresse https:// de la requête (ligne de requête + en-tête Host, port public compris) ;
    /// null si la requête est illisible ou si l'hôte ou le chemin contiennent autre chose
    /// que des caractères attendus (pas d'injection dans l'en-tête Location).
    /// </summary>
    public static string? Location(string head)
    {
        var request = RequestLine().Match(head);
        var host = HostHeader().Match(head);
        return request.Success && host.Success ? $"https://{host.Groups[1].Value}{request.Groups[1].Value}" : null;
    }

    private static bool HeadersComplete(ReadOnlySequence<byte> buffer) =>
        Encoding.ASCII.GetString(buffer).Contains("\r\n\r\n");

    [GeneratedRegex(@"\A[A-Z]+ (/[\x21-\x7E]*) HTTP/1\.[01]\r\n")]
    private static partial Regex RequestLine();

    [GeneratedRegex(@"\r\nHost: *([A-Za-z0-9.\-]+(?::[0-9]{1,5})?|\[[0-9A-Fa-f:.]+\](?::[0-9]{1,5})?) *\r\n", RegexOptions.IgnoreCase)]
    private static partial Regex HostHeader();
}

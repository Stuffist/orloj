using System.Security.Cryptography;
using System.Text;

namespace Olomoc.Server.Tournaments;

/// <summary>
/// Jetons d'accès (organisateur, joueur) : 128 bits aléatoires, transmis une seule fois ;
/// seul leur hash SHA-256 est enregistré, comparé en temps constant.
/// </summary>
public static class Tokens
{
    public static string New() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    public static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    public static bool Matches(string? token, string hash) =>
        !string.IsNullOrEmpty(token) && !string.IsNullOrEmpty(hash)
        && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Hash(token)), Encoding.ASCII.GetBytes(hash));

    // Mot de passe organisateur : PBKDF2-SHA256, sel aléatoire, enregistré « pbkdf2$itérations$sel$hash ».
    private const int Iterations = 100_000;

    public static string HashPassword(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
        return $"pbkdf2${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool VerifyPassword(string? password, string stored)
    {
        var parts = stored.Split('$');
        if (string.IsNullOrEmpty(password) || parts is not ["pbkdf2", var iterations, var salt, var hash]) return false;
        var expected = Convert.FromBase64String(hash);
        var actual = Rfc2898DeriveBytes.Pbkdf2(password, Convert.FromBase64String(salt), int.Parse(iterations), HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    /// <summary>Identifiant public court (joueur).</summary>
    public static string NewId() => Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();
}

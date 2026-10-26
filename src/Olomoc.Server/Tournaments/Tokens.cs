using System.Security.Cryptography;
using System.Text;

namespace Olomoc.Server.Tournaments;

/// <summary>
/// Access tokens (organizer, player): 128 random bits, sent only once; only their SHA-256 hash is
/// stored, compared in constant time.
/// </summary>
public static class Tokens
{
    public static string New() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    public static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    public static bool Matches(string? token, string hash) =>
        !string.IsNullOrEmpty(token) && !string.IsNullOrEmpty(hash)
        && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Hash(token)), Encoding.ASCII.GetBytes(hash));

    // Organizer password: PBKDF2-SHA256, random salt, stored as "pbkdf2$iterations$salt$hash".
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

    /// <summary>Short public identifier (player).</summary>
    public static string NewId() => Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();
}

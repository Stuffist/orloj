namespace Olomoc;

/// <summary>Document enregistré par le serveur sous un code public (tournoi, partie en ligne).</summary>
public interface IStoredDocument
{
    string Code { get; set; }
    DateTimeOffset CreatedAt { get; set; }
    DateTimeOffset UpdatedAt { get; set; }
}

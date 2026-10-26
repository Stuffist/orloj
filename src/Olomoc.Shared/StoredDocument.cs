namespace Olomoc;

/// <summary>Document stored by the server under a public code (tournament, online game).</summary>
public interface IStoredDocument
{
    string Code { get; set; }
    DateTimeOffset CreatedAt { get; set; }
    DateTimeOffset UpdatedAt { get; set; }
}

namespace Olomoc;

/// <summary>
/// Shared static files (card catalog, languages, pictograms, styles): published by the
/// Olomoc.Shared library at the site root, shared by all apps (/builder/…). In a Razor
/// component: Olomoc.Assets (ComponentBase.Assets, since .NET 9, hides this name).
/// </summary>
public static class Assets
{
    public const string Base = "/_content/Olomoc.Shared/";
}

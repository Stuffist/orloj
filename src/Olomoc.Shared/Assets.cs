namespace Olomoc;

/// <summary>
/// Fichiers statiques communs (référentiel, langues, pictogrammes, styles) : publiés par la
/// bibliothèque Olomoc.Shared à la racine du site, partagés par toutes les apps (/builder/…).
/// Dans un composant Razor : Olomoc.Assets (ComponentBase.Assets, depuis .NET 9, masque ce nom).
/// </summary>
public static class Assets
{
    public const string Base = "/_content/Olomoc.Shared/";
}

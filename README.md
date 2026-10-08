# Olomòc — Constructeur de cabale (Blazor WebAssembly)

Application cliente qui charge le référentiel depuis deux fichiers JSON statiques
(`wwwroot/data/`) — pas de base de données — et compose une cabale légale.

## Lancer

Nécessite le SDK .NET 8.

```bash
cd OlomocListBuilder
dotnet run
```

Puis ouvrir l'URL affichée (typiquement `https://localhost:5001`).
Pour un build statique déployable sur n'importe quel hébergeur de fichiers :

```bash
dotnet publish -c Release
# le site se trouve dans bin/Release/net8.0/publish/wwwroot
```

## Données

Deux ressources, servies telles quelles :

- `wwwroot/data/olomoc-catalog.json` — rouages, équipements/dons, ministères
  (schéma `olomoc.schema.json`).
- `wwwroot/data/olomoc-factions.json` — appartenances aux factions
  (schéma `olomoc-factions.schema.json`).

Remplacer ces deux fichiers par les vraies données suffit à mettre l'app à jour ;
aucun code à toucher. Les cartes fournies ici sont des exemples (dont plusieurs
inventés) pour rendre le constructeur démontrable.

## Règles appliquées

- Une cabale = un rouage de rang 1, un de rang 2, un de rang 3, **même faction**.
- Chaque rouage porte **un** équipement/don.
- Un équipement est proposé pour un rouage s'il est affilié à la faction de la
  cabale, à un ministère présent parmi les trois rouages, ou nommément à ce rouage.
- **Chaque rouage ne débloque qu'un seul équipement de son ministère** : les
  équipements « ministère » sont affectés à des rouages distincts (couplage
  biparti). Le panneau de légalité signale la sur-souscription.

## Architecture

- `Models/CatalogModels.cs` — records du référentiel, désérialisés en
  insensible à la casse ; enums `HealthPip` (N/A/T), `Temporality`, `GearKind`.
- `Services/CatalogService.cs` — chargement + `AvailableCogs`, `AvailableGears`,
  `CheckLegality` (dont le matching biparti).
- `Pages/Home.razor` — état de la cabale et UI réactive.

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
  (schéma `schemas/olomoc.schema.json`).
- `wwwroot/data/olomoc-factions.json` — appartenances aux factions
  (schéma `schemas/olomoc-factions.schema.json`).

Chaque fichier référence son schéma via `$schema` (validation et autocomplétion
dans l'éditeur). Le dossier `schemas/` contient aussi un exemple minimal par
schéma (`referentiel.example.json`, `factions.example.json`) ; il n'est ni copié
dans le build ni publié. Validation en ligne de commande, par exemple :

```bash
npx ajv-cli@5 validate --spec=draft2020 --strict=false \
  -s schemas/olomoc.schema.json -d wwwroot/data/olomoc-catalog.json
```

Remplacer ces deux fichiers suffit à mettre l'app à jour ; aucun code à toucher.

Contenu actuel (relevé du 2026-10-08) : 28 rouages, 53 équipements/dons, 7 factions.
Les rattachements (factions, ministères, rangs) viennent des filtres du builder
[bwaaaanegamesdb.fr](https://bwaaaanegamesdb.fr/builder_olomoc/index_builder_olomoc.php) ;
les stats, lignes de vie et capacités ont été transcrites depuis les images des
cartes. Conventions de transcription :

- Le rang est celui du site, **pas** le préfixe du code (`1R01` est un rang 3).
- PV gris (uni « renforcé » ou hachuré) → `A`.
- Couronne N → `frequencies: [{count: N, per: PLANNING_PHASE}]` ;
  cercle plein N → `{count: N, per: GAME}`.
- Cube blanc plein dans la colonne de coût → +1 `actionCost`.
- Pictogrammes dans le texte : `[Corps-à-corps]`, `[Tir]`, `[Technique]`,
  `[pion d'action]`, `[Noirceur]`, `[1/partie]`.
- `baseSize` n'est pas imprimée sur les cartes et reste vide.

## Règles appliquées

- Une cabale = un rouage de rang 1, un de rang 2, un de rang 3, **même faction**.
- Chaque rouage porte **un** équipement/don.
- Un équipement est proposé pour un rouage s'il est affilié à la faction de la
  cabale, à un ministère présent parmi les trois rouages, ou nommément à ce rouage.
- **Chaque rouage ne débloque qu'un seul équipement de son ministère** : les
  équipements « ministère » sont affectés à des rouages distincts (couplage
  biparti). Le panneau de légalité signale la sur-souscription.
- **Règles de constitution portées par les cartes** (`buildRules` dans le
  référentiel, interprétées par `CheckLegality`) :
  - `RECRUIT_MINISTRY` — *Harmonium* : la cabale peut recruter **un** rouage hors
    faction de l'Axe, de rang 1 ou 2.
  - `RECRUIT_WEARER` — *Asservisseur mental* : le porteur, de rang 1 ou 2, peut
    être recruté hors faction (chez les Autres).
  - `ALL_MINISTRIES` — *Passe-droit du Census* : le porteur compte comme affilié
    à tous les ministères (disponibilité des équipements, déblocage, recrutement).
    Il ne débloque pas son propre Passe-droit : un rouage de la Coordination reste
    nécessaire.

  Les rouages hors faction ainsi recrutables apparaissent dans la liste avec la
  mention « via … ». Non géré : l'équipement supplémentaire de Medic Ruzicka.

## Architecture

- `Models/CatalogModels.cs` — records du référentiel, désérialisés en
  insensible à la casse ; enums `HealthPip` (N/A/T), `Temporality`, `GearKind`.
- `Services/CatalogService.cs` — chargement + `AvailableCogs`, `AvailableGears`,
  `CheckLegality` (dont le matching biparti).
- `Pages/Home.razor` — état de la cabale et UI réactive.

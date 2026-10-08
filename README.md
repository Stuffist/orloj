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

## Vocabulaire

- **Tout ce qui est affiché** (interface, messages, noms et effets de jeu) suit le
  [livre de règles V1.1](https://www.occicat.com/wp-content/uploads/2026/01/LDR_V1.1.pdf).
- **Tout ce qui est caché** (code, clés et codes des JSON, classes CSS, fichiers)
  suit le [livre de règles anglais](https://www.occicat.com/wp-content/uploads/2026/08/LDR_EN_olomoc.pdf).

| Livre V1.1 (affichage)            | Livre EN (identifiants)                 |
|-----------------------------------|-----------------------------------------|
| rouage, cabale                    | `Cog`, cabal                            |
| équipement / don                  | `Equipment`, `EquipmentKind.EQUIPMENT` / `BOON` |
| Technique, Combat, Tir, Esquive   | `tech`, `melee`, `ranged`, `dodge`      |
| ligne de vie                      | `lifeLine`                              |
| PV standard / renforcé (Protection) / teinté | `S` / `R` / `T` (*standard / reinforced / tainted*) |
| temporalité : Active / Permanente | `timing` : `Active` / `Permanent`       |
| effet de jeu                      | `gameEffect`                            |
| pion d'action, pion de Noirceur   | `actionCost`, `darknessCost`            |
| fréquence d'utilisation           | `frequencies` (`PLANNING_PHASE`, `CYCLE`, `GAME`) |
| taille de socle                   | `baseSize`                              |
| recto / verso                     | `front` / `back`                        |
| Référentiel de faction            | `FactionReference`                      |
| Ministère de la Coordination, de l'Intégrité, de l'Effort, du Progrès, de l'Harmonie, de l'Édification, de la Sécurité ; la Frange, l'Axe (Ministères dévoyés) | `COORDINATION`, `INTEGRITY`, `EFFORT`, `PROGRESS`, `HARMONY`, `EDIFICATION`, `SECURITY`, `FRINGE`, `AXIS` |

Les factions ne sont nommées dans aucun des deux livres : leurs codes gardent les
noms propres (`BLAUKAUST`, `MAISEL`, `URKARAS`, `RUBIN`) et traduisent le reste
(`OTHERS` = Les Autres, `DYKOVIAN` = Cabale Dykovienne, `UNDERWORLD` = La Pègre),
en attendant un référentiel de faction anglais officiel.

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
les caractéristiques, lignes de vie et capacités ont été transcrites depuis les
images des cartes. Conventions de transcription :

- Le rang est celui du site, **pas** le préfixe du code (`1R01` est un rang 3).
- PV gris (uni ou hachuré selon l'édition) → `R` (renforcé).
- Couronne N → `frequencies: [{count: N, per: PLANNING_PHASE}]` ;
  cercle plein N → `{count: N, per: GAME}`.
- Cube blanc plein dans la colonne de coût → +1 `actionCost`.
- Pictogrammes dans l'effet de jeu : `[Technique]`, `[Combat]`, `[Tir]`,
  `[Active]`, `[pion d'action]`, `[pion de Noirceur]`, `[1 fois par partie]`.
- `baseSize` est imprimée au verso des cartes, absent du relevé : elle reste vide.

## Règles appliquées (LDR p.32)

- Une cabale doit être constituée de 3 rouages : un rang 1, un rang 2 et un
  rang 3, appartenant tous à la **même faction**.
- Chaque rouage doit être équipé d'**un** équipement/don, affilié à un Ministère
  d'un des rouages de la cabale, ou à la faction, ou au rouage équipé.
- **Chaque rouage ne rend disponible qu'un seul équipement/don de son Ministère
  d'affiliation** : les équipements de Ministère sont affectés à des rouages
  distincts (couplage biparti). Le panneau de légalité signale le dépassement.
- Pas de doublon : deux rouages ou deux équipements/dons de même nom.
- **Règles de composition portées par les cartes** (`buildRules` dans le
  référentiel, interprétées par `CheckLegality`) :
  - `RECRUIT_MINISTRY` — *Harmonium* : la cabale peut recruter **un** rouage hors
    faction affilié à l'Axe, de rang 1 ou 2.
  - `RECRUIT_WEARER` — *Asservisseur mental* : le rouage équipé, de rang 1 ou 2,
    peut être recruté hors faction (chez les Autres).
  - `ALL_MINISTRIES` — *Passe-droit du Census* : le rouage équipé compte comme
    affilié à tous les Ministères (équipements/dons disponibles, recrutement).
    Il ne rend pas disponible son propre Passe-droit : un rouage de la
    Coordination reste nécessaire.

  Les rouages hors faction ainsi recrutables apparaissent dans la liste avec la
  mention « via … ». Non géré : l'équipement supplémentaire de Medic Ruzicka.

## Recherche

Le champ de recherche combine des mots libres (nom ou titre, sans tenir compte
des accents ni de la casse) et des conditions cumulables sur les caractéristiques :
`tec`, `cbt`, `tir`, `esq` suivis de `>`, `<`, `>=`, `<=` ou `=` et d'un nombre,
par exemple `kantor tir>4 esq<=3`.

## Architecture

- `Models/CatalogModels.cs` — modèle du référentiel (`Cog`, `Equipment`,
  `Ministry`, `Faction`…), désérialisé en insensible à la casse ; enums
  `HealthPointType` (S/R/T), `Timing`, `EquipmentKind`, `BuildRuleType`.
- `Services/CatalogService.cs` — chargement + `AvailableCogs`,
  `AvailableEquipment`, `RecruitVia`, `CheckLegality` (dont le couplage biparti).
- `Services/CogQuery.cs` — analyse de la recherche (mots + conditions sur les stats).
- `Shared/StatLine.razor` — caractéristiques avec les icônes du livre (`wwwroot/img/stats/`).
- `Pages/Home.razor` — état de la cabale et UI réactive.

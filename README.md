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

## Déploiement (Docker, VPS)

L'app est un Blazor WebAssembly : l'image (`Dockerfile`, multi-étapes) publie avec
le SDK .NET puis sert les fichiers statiques avec nginx (≈ 65 Mo, pas de runtime
.NET). Le déploiement est **autonome** : Olomoc a son propre port HTTPS et ne
dépend d'aucun autre projet du VPS (B5T garde les ports 80/443). Il réutilise le
certificat Let's Encrypt de l'hôte (`/etc/letsencrypt`, monté en lecture seule) :
un certificat vaut pour le nom d'hôte, quel que soit le port.

→ `https://vps-24cf4978.vps.ovh.net:8443/`

```bash
docker compose up -d --build
```

Réglages facultatifs (fichier `.env` à côté du `docker-compose.yml`) :

| Variable            | Défaut                       | Rôle                                         |
|---------------------|------------------------------|----------------------------------------------|
| `DOMAIN`            | `vps-24cf4978.vps.ovh.net`   | nom d'hôte, et dossier du certificat (`live/<DOMAIN>/`) |
| `OLOMOC_HTTPS_PORT` | `8443`                       | port public HTTPS                            |
| `OLOMOC_HTTP_PORT`  | `8090`                       | port HTTP de diagnostic, sur 127.0.0.1 seulement |
| `CERT_ROOT`         | `/etc/letsencrypt`           | dossier Let's Encrypt de l'hôte              |

À prévoir sur le VPS :

- **Pare-feu** : ouvrir le port HTTPS (`8443/tcp`) s'il y en a un (`ufw allow 8443/tcp`,
  pare-feu réseau OVH).
- **Renouvellement du certificat** : nginx lit le certificat au démarrage. Après un
  renouvellement par certbot, recharger le conteneur, par exemple avec un hook
  `/etc/letsencrypt/renewal-hooks/deploy/olomoc.sh` (exécutable) :
  `docker exec olomoc nginx -s reload`.
- Un `http://…:8443` est renvoyé vers `https://…:8443`. Diagnostic local :
  `curl -I http://127.0.0.1:8090/` ; état : `docker ps` (healthcheck `/healthz`).

**Mises à jour** : `git pull && docker compose up -d --build`. Variante sans les
sources sur le VPS, comme pour B5T : `docker compose build && docker compose push`
sur le poste (image `stuffist/olomoc` sur Docker Hub), puis sur le VPS, avec le seul
`docker-compose.yml` : `docker compose pull && docker compose up -d`.

L'image accepte aussi un chemin de base (`build.args.BASE_PATH`, ex. `/olomoc/`) si
l'app doit un jour être servie sous un sous-chemin derrière un reverse proxy.

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

Le référentiel est **neutre en langue** ; les textes vivent dans des paquets par langue.

- `wwwroot/data/olomoc-catalog.json` — rouages, équipements/dons, ministères : ids,
  codes, caractéristiques, lignes de vie, coûts, fréquences, règles de composition
  (schéma `schemas/olomoc.schema.json`).
- `wwwroot/data/olomoc-factions.json` — appartenances aux factions
  (schéma `schemas/olomoc-factions.schema.json`).
- `wwwroot/i18n/<langue>/cards.json` — textes des cartes : noms, titres, noms et
  effets de jeu des capacités, noms des Ministères et des factions
  (schéma `schemas/olomoc-texts.schema.json`).
- `wwwroot/i18n/<langue>/ui.json` — libellés de l'interface
  (schéma `schemas/olomoc-ui.schema.json`).

Chaque fichier référence son schéma via `$schema` (validation et autocomplétion
dans l'éditeur). Le dossier `schemas/` contient aussi des exemples minimaux
(`referentiel.example.json`, `factions.example.json`, `textes.example.json`) ;
il n'est ni copié dans le build ni publié. Validation en ligne de commande :

```bash
npx ajv-cli@5 validate --spec=draft2020 --strict=false   -s schemas/olomoc.schema.json -d wwwroot/data/olomoc-catalog.json
npx ajv-cli@5 validate --spec=draft2020 --strict=false   -s schemas/olomoc-texts.schema.json -d "wwwroot/i18n/*/cards.json"
```

Remplacer ces fichiers suffit à mettre l'app à jour ; aucun code à toucher. Les
tests (`dotnet test`) vérifient aussi la cohérence entre référentiel et paquets.

Contenu actuel (relevé du 2026-10-08) : 28 rouages, 53 équipements/dons, 7 factions.
Les rattachements (factions, ministères, rangs) viennent des filtres du builder
[bwaaaanegamesdb.fr](https://bwaaaanegamesdb.fr/builder_olomoc/index_builder_olomoc.php) ;
les caractéristiques, lignes de vie et capacités ont été transcrites depuis les
images des cartes. Conventions de transcription (pictogrammes : LDR p.20) :

- Le rang est celui du site, **pas** le préfixe du code (`1R01` est un rang 3).
- PV gris (uni ou hachuré selon l'édition) → `R` (renforcé).
- Cube blanc ×N → `actionCost: N` ; cube noir ×N → `darknessCost: N`.
- Fréquences : couronne N → `{count: N, per: PLANNING_PHASE}`, pentagone N →
  `{count: N, per: CYCLE}`, cercle N → `{count: N, per: GAME}` (cumulables).
- `baseSize` est imprimée au verso des cartes, absent du relevé : elle reste vide.

## Multilingue

- `wwwroot/i18n/languages.json` liste les langues proposées (sélecteur dans
  l'en-tête). La langue est détectée depuis le navigateur, puis mémorisée.
- `fr` est la **langue de référence**, complète. Les autres paquets peuvent être
  partiels : tout texte manquant se replie sur `fr` (affiché en italique souligné).
  Les règles indépendantes de la langue (doublons de nom) comparent les noms `fr`.
- **Ajouter une langue** : créer `wwwroot/i18n/<code>/ui.json` et `cards.json`, puis
  l'ajouter à `languages.json`. Les clés de `ui.json` sont celles de `fr` ; les
  trigrammes et noms de stats (`stat.*.abbr`, `stat.*.name`) deviennent aussi des
  mots-clés de recherche, valables quelle que soit la langue affichée.
- **Pictogrammes** : identiques dans toutes les langues, jamais traduits. Dans les
  effets de jeu, ce sont des jetons neutres rendus par les icônes du livre de règles
  (`wwwroot/img/icons/`, `wwwroot/img/stats/`) :
  `{tech}` `{melee}` `{ranged}` `{dodge}` `{active}` `{action}` `{darkness}`
  `{stunned}` (pion sonné) et `{freq:planning|cycle|game:N}`.
- **Factions** : sans nom anglais officiel, `en/cards.json` reprend les noms
  français (noms propres assumés, donc pas signalés comme non traduits).
- Les tests vérifient que chaque carte a ses textes `fr`, qu'aucune langue ne
  contient d'id ou de clé inconnus, et que les pictogrammes d'un effet de jeu sont
  les mêmes dans toutes les langues.

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
trigramme ou nom d'une stat, dans n'importe quelle langue (`tec`, `cbt`, `tir`,
`esq`, `mel`, `rng`, `dod`, `combat`, `ranged`…), suivi de `>`, `<`, `>=`, `<=` ou
`=` et d'un nombre, par exemple `kantor tir>4 esq<=3` ou `rng>4 dod<=3`.

## Architecture

- `Models/CatalogModels.cs` — référentiel neutre (`Cog`, `Equipment`, `Ministry`,
  `Faction`…), désérialisé en insensible à la casse ; enums `HealthPointType`
  (S/R/T), `Timing`, `EquipmentKind`, `BuildRuleType`.
- `Models/TextModels.cs` — paquets de langue, `Localized` (texte + repli), et
  `LegalityIssue` (messages de légalité neutres : clé + ids).
- `Services/LocalizationService.cs` — langue courante, libellés (`Ui`), textes des
  cartes avec repli sur `fr`, mots-clés de stats de toutes les langues.
- `Services/CatalogService.cs` — chargement + `AvailableCogs`,
  `AvailableEquipment`, `RecruitVia`, `CheckLegality` (dont le couplage biparti).
- `Services/CogQuery.cs` — analyse de la recherche (mots + conditions sur les stats).
- `Shared/StatLine.razor`, `AbilityList.razor`, `GameEffect.razor`,
  `Pictogram.razor` — caractéristiques, capacités et pictogrammes du livre.
- `Pages/Home.razor` — état de la cabale et UI réactive.
- `tests/OlomocListBuilder.Tests` — xUnit : légalité, recherche, cohérence des langues
  (compile les modèles et services de l'app, lit les données de `wwwroot`).

# Orloj — Constructeur de cabale et tournois pour Olomòc (Blazor WebAssembly)

Application cliente qui charge le référentiel depuis deux fichiers JSON statiques
(`src/Olomoc.Shared/wwwroot/data/`) — pas de base de données — et compose une cabale légale.
Le site est servi par un petit hôte ASP.NET Core (`src/Olomoc.Server`) : constructeur
sous `/builder/` (la racine `/` y renvoie), tournois sous `/pairings/` (inscriptions,
appariements, résultats, classement), avec leur API (`/api/tournaments`).

Olomòc est un jeu d'Occicat Games : le contenu du jeu repris ici leur appartient et n'est pas
sous la licence du code (voir [Licence](#licence)).

## Lancer

Nécessite le SDK .NET 10.

```bash
dotnet run --project src/Olomoc.Server
```

Puis ouvrir `http://localhost:5180/` (renvoie vers `/builder/`).

**Devenir admin en local** : créer son compte (« Mon compte »), arrêter le serveur, puis le
relancer une fois avec son pseudo dans `OLOMOC_ADMINS` ; le rôle est alors dans la base
(`data/olomoc.db`), la variable n'est plus utile :

```powershell
$env:OLOMOC_ADMINS = "MonPseudo"; dotnet run --project src/Olomoc.Server
```

Tests : `dotnet test` à la racine (solution `Olomoc.slnx`).

## Déploiement (Docker, VPS)

Une seule image (`Dockerfile`, multi-étapes) : publication avec le SDK .NET, puis
l'hôte `Olomoc.Server` sur le runtime ASP.NET (Alpine, ≈ 140 Mo), en HTTP sur le port
8080, sous un utilisateur sans privilèges (`app`, uid 1654). Il sert le constructeur
(`/builder/`) et les tournois (`/pairings/`), fichiers `_framework` précompressés en
Brotli/gzip, les fichiers communs (`/_content/Olomoc.Shared/`), l'API des tournois,
redirige `/` vers `/builder/`, et répond à `/healthz`. Les données (tournois, statistiques)
sont dans une base **SQLite**, `/data/olomoc.db` du conteneur, monté sur le dossier `./data`
à côté du `docker-compose.yml` : conservées par `down`/`up`, `--force-recreate` et
`--build`. Le serveur en fait chaque jour une copie cohérente dans `data/backups/` (les 7
dernières sont gardées) ; c'est le dossier à sauvegarder (`tar czf olomoc-data.tgz data`).
Copier `olomoc.db` lui-même pendant que le serveur tourne ne donne pas une copie sûre.

Passage aux versions avec base : au premier démarrage, les anciens fichiers
`data/tournaments/*.json` et `data/stats/*.json` sont importés dans la base, puis leurs
dossiers renommés `tournaments.imported` et `stats.imported` (gardés tels quels, jamais
réimportés ; un fichier illisible y reste, signalé dans le journal du conteneur).

→ `https://orloj.top/`

### Reverse proxy (`deploy/proxy`)

Sur le VPS, un conteneur **Caddy** (`deploy/proxy/docker-compose.yml` et `Caddyfile`, copiés
dans `/var/www/proxy/`) est seul à tenir les ports 80/443. Il obtient et renouvelle lui-même
les certificats Let's Encrypt (validation sur le port 80 : ni certbot, ni arrêt d'un
autre conteneur), redirige HTTP vers HTTPS et répartit par nom d'hôte, sur le réseau
Docker `web` commun aux projets :

| Nom                                   | Destination                                   |
|---------------------------------------|-----------------------------------------------|
| `orloj.top`                           | Orloj (`olomoc:8080`)                         |
| `dauphine.orloj.top`                  | B5T (`b5tweb:8080`)                           |
| `www.orloj.top`, `…:8443` (anciennes adresses d'Orloj) | redirection vers `https://orloj.top` |
| `vps-24cf4978.vps.ovh.net` (ancienne adresse de B5T) | redirection vers `https://dauphine.orloj.top` |

Orloj n'ouvre aucun port public. Il lit l'adresse IP et le schéma du visiteur transmis par
Caddy (`X-Forwarded-For` / `-Proto`, `ASPNETCORE_FORWARDEDHEADERS_ENABLED`) : limites de
requêtes par visiteur, HSTS. Noms acceptés : `AllowedHosts` du `docker-compose.yml`.

### Installation et mises à jour

Une fois par machine : `docker network create web`. Dossier des tournois : il doit
appartenir à l'utilisateur du conteneur (`sudo chown -R 1654:1654 data`).

```bash
docker compose up -d --build                     # depuis les sources
```

Sans les sources sur le VPS, comme pour B5T : `docker compose build && docker compose push`
sur le poste (image `stuffist/olomoc` sur Docker Hub), puis sur le VPS, avec le seul
`docker-compose.yml` : `docker compose pull && docker compose up -d`. Proxy :
`docker compose up -d` dans `/var/www/proxy/` ; après une modification du `Caddyfile`,
`docker compose exec caddy caddy reload --config /etc/caddy/Caddyfile`.

Réglages (fichier `.env` à côté du `docker-compose.yml`, **non versionné** : il contient les
identifiants SMTP) :

| Variable           | Défaut                          | Rôle                                         |
|--------------------|---------------------------------|----------------------------------------------|
| `OLOMOC_HOSTS`     | `orloj.top;localhost;127.0.0.1` | noms d'hôte acceptés (les autres : 400)      |
| `OLOMOC_HTTP_PORT` | `8090`                          | port HTTP de diagnostic, sur 127.0.0.1 seulement |
| `OLOMOC_ADMINS`    | (aucun)                         | pseudos à faire admins au démarrage, séparés par `;` (comptes existants ; premier admin ou secours, jamais retiré) |
| `SMTP_HOST`        | (aucun)                         | relais SMTP des e-mails (Brevo…) ; sans lui, e-mails écrits dans `data/mail` |
| `SMTP_PORT`        | `587`                           | port du relais (STARTTLS ; 465 : TLS)        |
| `SMTP_USER`, `SMTP_PASSWORD` | (aucun)               | identifiants du relais (secrets : dans `.env` seulement) |
| `MAIL_FROM`        | `Orloj <noreply@orloj.top>`     | expéditeur des e-mails                       |
| `MAIL_CHECK_TO`    | (aucun)                         | e-mail de contrôle tous les 60 jours (garde la clé SMTP active) |

Diagnostic : `curl -I http://127.0.0.1:8090/healthz` ; état : `docker ps` (healthcheck
`/healthz`) ; journaux : `docker logs olomoc`, `docker logs caddy`.

**En local (Docker Desktop)** : `docker network create web`, puis le même
`docker compose up -d --build` → `http://localhost:8090/`. Arrêt : `docker compose down`.
Pour le développement courant, `dotnet run --project src/Olomoc.Server` reste plus rapide
(pas de reconstruction d'image).
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
| Sain / Teinté / Corrompu, Fiel, hors de combat, sonné | `Sane` / `Tainted` / `Corrupted`, `Bile`, `OutOfAction`, `Stunned` |
| niveau de traque (PoliSec), points de victoire | `Hunt`, `VictoryPoints` |
| fréquence d'utilisation           | `frequencies` (`PLANNING_PHASE`, `CYCLE`, `GAME`) |
| taille de socle                   | `baseSize`                              |
| recto / verso                     | `front` / `back`                        |
| Référentiel de faction            | `FactionReference`                      |
| Ministère de la Coordination, de l'Intégrité, de l'Effort, du Progrès, de l'Harmonie, de l'Édification, de la Sécurité ; la Frange, l'Axe (Ministères dévoyés) | `COORDINATION`, `INTEGRITY`, `EFFORT`, `PROGRESS`, `HARMONY`, `EDIFICATION`, `SECURITY`, `FRINGE`, `AXIS` |

Les noms des factions suivent le [référentiel de faction](https://www.occicat.com/wp-content/uploads/2026/09/FACTIONS_FR.pdf)
(septembre 2026) : Les Autres, Blockhaus Devet, Cabale Dykovienne, Ligue de Maisel,
Famille Urkaras, Loups de Libus, Réseau Rubin ; repris tels quels en anglais, faute de
référentiel anglais. Codes : `OTHERS`, `BLOCKHAUS`, `DYKOVIAN`, `MAISEL`, `URKARAS`,
`LIBUS`, `RUBIN`.

## Données

Le référentiel est **neutre en langue** ; les textes vivent dans des paquets par langue.

- `src/Olomoc.Shared/wwwroot/data/olomoc-catalog.json` — rouages, équipements/dons, ministères : ids,
  codes, caractéristiques, lignes de vie, coûts, fréquences, règles de composition
  (schéma `schemas/olomoc.schema.json`).
- `src/Olomoc.Shared/wwwroot/data/olomoc-factions.json` — appartenances aux factions
  (schéma `schemas/olomoc-factions.schema.json`).
- `src/Olomoc.Shared/wwwroot/i18n/<langue>/cards.json` — textes des cartes : noms, titres, noms et
  effets de jeu des capacités, noms des Ministères et des factions
  (schéma `schemas/olomoc-texts.schema.json`).
- `src/Olomoc.Shared/wwwroot/i18n/<langue>/ui.json` — libellés de l'interface
  (schéma `schemas/olomoc-ui.schema.json`).
- `src/Olomoc.Shared/wwwroot/img/cards/<id>.jpg` — images des cartes (recto), une par
  rouage et équipement/don, nommées par leur id : 1051 × 744 px, soit 300 ppp à la taille
  réelle (89 × 63 mm). Relevées sur bwaaaanegamesdb.fr et recompressées ; ce sont les
  illustrations d'Occicat Games, utilisées avec leur permission (voir [Licence](#licence)).
- `src/Olomoc.Shared/wwwroot/img/emblems/factions|ministries/<code>.png` — logos des
  factions (Référentiel de faction, septembre 2026) et emblèmes des Ministères (LDR p.25,
  disque central seulement : l'anneau texturé ne se lit pas en petit), un par code.
  Masques 96 × 96 px (blanc, la forme dans l'alpha) teintés en CSS, comme les
  pictogrammes : logos à la couleur du texte, emblèmes en rouille. Extraits des PDF
  d'Occicat (images intégrées ; rendu à 600 ppp pour les logos vectoriels de Dykovie
  et de Maisel).

Chaque fichier référence son schéma via `$schema` (validation et autocomplétion
dans l'éditeur). Le dossier `schemas/` contient aussi des exemples minimaux
(`referentiel.example.json`, `factions.example.json`, `textes.example.json`) ;
il n'est ni copié dans le build ni publié. Validation en ligne de commande :

```bash
npx ajv-cli@5 validate --spec=draft2020 --strict=false   -s schemas/olomoc.schema.json -d src/Olomoc.Shared/wwwroot/data/olomoc-catalog.json
npx ajv-cli@5 validate --spec=draft2020 --strict=false   -s schemas/olomoc-texts.schema.json -d "src/Olomoc.Shared/wwwroot/i18n/*/cards.json"
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

### Éditeur du référentiel (éditeurs et admins)

`/builder/admin` (lien dans « Mon compte », section « Référentiel des cartes ») : rouages, équipements et dons,
factions, avec leurs textes dans chaque langue de `languages.json` (nom obligatoire en français ;
les autres langues se replient dessus).

- **Rouages** : rang, Ministères, **factions** (référentiel de faction), caractéristiques, ligne
  de vie (lettres S, R, T), socle, carte supplémentaire, image, capacités (moment, coûts,
  fréquences ; nom et effet par langue), nom, titre, instinct.
- **Équipements et dons** : type, affiliation exclusive (Ministère, faction ou rouage), points de
  vie ajoutés, règles de composition, image, capacités, nom.
- **Factions** : alignement, nom et description par langue.
- **Publication** : seule une entrée `"published": true` est publiée. Sans ce champ (ou à
  `false`), c'est un brouillon, servi aux seuls éditeurs et admins (marqué « brouillon » dans le
  constructeur), retiré pour les autres avec ses textes et sa place dans les factions, et refusé
  aux inscriptions de tournoi. Une entrée nouvelle est donc un brouillon tant qu'elle n'est pas
  publiée ; une PR qui ajoute une carte visible de tous met `"published": true`.
- **Stockage** : les fichiers du dépôt restent la base (PR bienvenues) ; les modifications faites
  dans l'éditeur sont des JSON au même format, avec les seules entrées modifiées ou créées, dans
  `data/catalog/` (`data/olomoc-catalog.json`, `data/olomoc-factions.json`,
  `i18n/<langue>/cards.json`), fusionnés par id ou code avec ceux du dépôt. Les fichiers du
  référentiel sont servis aux mêmes adresses (`/_content/Olomoc.Shared/…`), fusionnés.
- **Export** : onglet « Exporter », zip des fichiers complets aux chemins du dépôt, à dézipper à sa
  racine puis commiter. Au démarrage suivant, les modifications devenues identiques au dépôt sont
  retirées de `data/catalog/` ; « Revenir à la version du dépôt » annule celles d'une entrée
  (une entrée nouvelle est alors supprimée).
- Les identifiants ne changent jamais (les cabales sauvegardées y font référence) ; les images
  (`img/cards/`) restent à ajouter au dépôt.

## Multilingue

- `src/Olomoc.Shared/wwwroot/i18n/languages.json` liste les langues proposées (sélecteur dans
  l'en-tête). La langue est détectée depuis le navigateur, puis mémorisée.
- `fr` est la **langue de référence**, complète. Les autres paquets peuvent être
  partiels : tout texte manquant se replie sur `fr` (affiché en italique souligné).
  Les règles indépendantes de la langue (doublons de nom) comparent les noms `fr`.
- **Ajouter une langue** : créer `src/Olomoc.Shared/wwwroot/i18n/<code>/ui.json` et `cards.json`, puis
  l'ajouter à `languages.json`. Les clés de `ui.json` sont celles de `fr` ; les
  trigrammes et noms de stats (`stat.*.abbr`, `stat.*.name`) deviennent aussi des
  mots-clés de recherche, valables quelle que soit la langue affichée.
- **Pictogrammes** : identiques dans toutes les langues, jamais traduits. Dans les
  effets de jeu, ce sont des jetons neutres rendus par les icônes du livre de règles
  (`src/Olomoc.Shared/wwwroot/img/icons/`, `src/Olomoc.Shared/wwwroot/img/stats/`) :
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
  mention « via … ». La fiche d'un rouage choisi donne ses factions (logo et nom),
  puis celles où une carte permet de le recruter (« Via une carte ») ; dans la liste
  de choix, les logos des factions dont il est membre.
- **Choix déjà pris** :
  - Rouage hors faction dont la carte de recrutement couvre déjà un autre rouage
    (`CanJoin`, `CogConflict`) : marqué 🔃 « remplace … ». Le choisir retire de la cabale
    l'autre rouage (échange) ; grisé seulement si aucun retrait ne suffit. La liste des
    factions grise celles où les rouages choisis ne tiennent pas **ensemble** (deux rouages
    de l'Axe pour un seul Harmonium).
  - Rouage hors faction choisi sans carte portée qui le couvre : sa carte de recrutement lui
    est équipée d'office (`RecruitCard` : Harmonium, Asservisseur mental), et retirée au
    rouage qui la portait sans en profiter ; une note le rappelle sous sa liste d'équipement.
  - Équipement/don déjà porté, ou d'un Ministère dont les rouages débloquent déjà chacun
    un équipement (`EquipmentConflict`) : marqué 🔃 dans la liste. Le choisir fait
    l'**échange** : la carte en conflit est retirée à l'autre rouage (infobulle, puis rappel
    sous la liste). Les flèches ‹ › sautent ces options.
  - Chaque catégorie de Ministère de la liste indique combien la cabale peut encore en
    prendre (`MinistryRemaining`), la carte du rouage mise à part.

  Rien n'est imputé à un choix si la cabale est déjà illégale sans lui.
- **Medic Ruzicka** (« au début du premier cycle », `extraEquipment` dans le
  référentiel) : l'équipement supplémentaire de l'Intégrité se choisit en mode
  partie, pas dans la composition.

## Recherche

Le champ de recherche combine des mots libres (nom ou titre, sans tenir compte
des accents ni de la casse) et des conditions cumulables sur les caractéristiques :
trigramme ou nom d'une stat, dans n'importe quelle langue (`tec`, `cbt`, `tir`,
`esq`, `mel`, `rng`, `dod`, `combat`, `ranged`…), suivi de `>`, `<`, `>=`, `<=` ou
`=` et d'un nombre, par exemple `kantor tir>4 esq<=3` ou `rng>4 dod<=3`.

## Catalogue des équipements et dons

Sous le constructeur, ou dans une fenêtre détachée (`/builder/equipment?window=true`) à
garder à côté (`Shared/EquipmentPanel.razor`) : filtre par Ministère ou faction, recherche
dans les noms et les effets, tri par Ministère ou par nom. **« Équipables seulement »** ne
garde que ce qu'au moins un rouage de la cabale en cours peut porter (`EquipableByCabal` :
faction, Ministères présents, cartes des rouages choisis). La fenêtre détachée est une autre
instance de l'app : le constructeur y publie la cabale dans le localStorage
(`CabalDraft.ShareKey`), et elle suit ses changements par l'événement `storage`.

## Export

Sous le panneau de légalité, avec le nom de la cabale (facultatif pour l'export) ; copie dans le
presse-papiers ou téléchargement (`<nom-de-la-cabale>.txt` / `.json`). L'export
reflète la composition en cours, même incomplète (un avertissement le signale).

- **Texte** — format du builder [bwaaaanegamesdb.fr](https://bwaaaanegamesdb.fr/builder_olomoc/index_builder_olomoc.php),
  vérifié à l'identique : nom de la cabale (ligne omise s'il est vide), puis une
  ligne par rouage du rang 1 au rang 3, `NOM ROUAGE - NOM ÉQUIPEMENT/DON` (nom seul
  s'il n'est pas équipé), en majuscules sans accents, lignes séparées par CRLF.

  ```text
  Ma Cabale Test
  DOMINIK KRALOV - HARMONIUM
  HYNEK NOVOTNY - CHRONOGRAPHE
  PETRA KANTOR - ASSERVISSEUR MENTAL
  ```

- **JSON** — schéma `schemas/olomoc-cabal.schema.json` : ids et codes du
  référentiel (réimportables), noms dans la langue courante (lisibles), champs
  facultatifs omis.

  ```json
  { "format": "olomoc-cabal", "version": 1, "language": "fr",
    "name": "Ma Cabale Test",
    "faction": { "code": "MAISEL", "name": "Ligue de Maisel" },
    "cogs": [ { "rank": 1, "cog": { "id": "3R05", "name": "Dominik Kralov" },
                "equipment": { "id": "E11", "name": "Harmonium" } }, … ] }
  ```

- **Cartes (PDF / PNG)** — planche des cartes de la cabale, ouverte dans un nouvel onglet
  (`/builder/cards?c=3R05.E11,2R04,1R01.E38&name=…` : la cabale passe dans l'adresse, le
  lien se partage ; `c` donne les rangs 1, 2, 3). Une page A4 paysage, cartes à taille réelle
  (89 × 63 mm) centrées : rang 3 à 1 de gauche à droite comme dans le constructeur, chaque
  rouage au-dessus de son équipement/don, 4 mm entre les
  rouages, 1,5 mm entre un rouage et son équipement (`Services/CardSheet.cs`).
  « Imprimer / PDF » passe par l'impression du navigateur (« Enregistrer au format PDF »,
  échelle 100 %) ; « Télécharger PNG » produit la même page en 2339 × 1654 px (200 ppp).

## Cabales sauvegardées

« Sauvegarder », à côté du nom de la cabale (section « Sauvegarde et export »),
garde la composition sur l'appareil (`localStorage`, clé `olomoc.cabals`), au
format de l'export JSON. Le nom est requis et identifie la cabale : sauvegarder sous
un nom déjà pris la remplace, après confirmation. Le bouton indique « Sauvegardée ✓ »
tant que la composition affichée est celle sauvegardée sous ce nom.

« Mes cabales », en haut du constructeur, liste les cabales sauvegardées (la plus
récente d'abord) : charger, supprimer, ou **importer** un export JSON (d'un autre
appareil, par exemple ; la cabale importée est chargée, à sauvegarder ensuite).
Charger par-dessus une composition non sauvegardée demande confirmation. Les cartes
sont retrouvées par leurs ids, chaque rouage à l'emplacement de son rang ; une carte
absente du référentiel actuel est écartée et signalée sous le nom enregistré.

**Avec un compte**, les mêmes cabales sur tous les appareils (`CabalLibrary`,
`POST /api/account/cabals/sync`) :

- chaque appareil garde sa copie (`olomoc.cabals`) : tout marche hors connexion ;
- connecté, chaque sauvegarde ou suppression part aussitôt ; sans réseau, elle attend le
  prochain chargement (« N changements pas encore envoyés » dans « Mes cabales ») ;
- à nom égal (sans tenir compte de la casse), **le plus récent l'emporte**, suppression
  comprise : le compte garde la trace des suppressions, une vieille copie ne les annule pas ;
- **première connexion** d'un appareil : ses cabales partent dans le compte (« N cabales de
  cet appareil ajoutées à votre compte ») ; un **autre compte** ensuite sur le même appareil :
  seules les cabales faites ici hors compte le suivent, les autres laissent la place aux siennes
  (`olomoc.cabals.account` : compte de la copie) ;
- déconnecté, la copie reste sur l'appareil ;
- 200 cabales au plus par compte (au-delà, la cabale reste sur l'appareil, signalée) ; envoi
  par paquets de 10 ; supprimer le compte efface ses cabales.

## Mode partie

Depuis le constructeur, « Lancer une partie » ouvre `/play` avec la cabale en cours
(trois rouages requis ; une cabale illégale est acceptée, avec un avertissement).
Pensé pour un téléphone ou une tablette, utilisable sur PC. Une seule partie par
appareil, sauvegardée dans le `localStorage` (clé `olomoc.game`) après chaque
modification ; « Annuler » remonte l'historique de la session (100 étapes, non sauvegardé).

Règles appliquées (`Services/GameSession.cs`) :

- **Ligne de vie** (LDR p.16) : blessures cochées de gauche à droite. Suite à une
  attaque, une blessure de moins s'il reste au moins un PV renforcé à cocher (sauf
  blessures qui ignorent la protection), et arrêt au premier PV teinté coché. Tout
  PV teinté coché donne un pion de Noirceur (p.17), attaque ou non. Soin = décocher
  les derniers PV cochés ; toucher un PV corrige la ligne à la main.
- **Carte supplémentaire** (Medic Ruzicka, `extraEquipment`) : choisie sur la carte
  du rouage pendant le premier cycle, parmi les équipements de l'Intégrité que la
  cabale ne porte pas déjà ; ses capacités sont suivies comme les autres.
- **Équipements qui modifient la ligne de vie** : `lifeLinePrefix` dans le
  référentiel (Carapace : deux PV renforcés en tête). Interrupteur « PV renforcés »
  (PV standards renforcés jusqu'à la fin du cycle, p. ex. Anabolisant).
- **Noirceur et Fiel** (p.17) : statut Sain (0), Teinté (1-2), Corrompu (3) ; un
  pion au-delà de 3 va sur le Fiel ; Fiel à 3 = partie perdue.
- **Pions d'action** (p.33) : 4 par rouage et par cycle ; **sonné** (p.18) :
  interdit les capacités actives.
- **Capacités suivies** : celles qui ont un coût ou une limite par partie (cercle).
  « Utiliser » paie les pions d'action, donne les pions de Noirceur et décompte
  l'usage par partie. Les limites par planification ou par cycle ne sont pas suivies.
- **Traque** (p.16, p.26) : +1 quand un de vos rouages est mis hors de combat (une
  correction qui le remet en jeu retire ce pion) ; les rouages adverses s'ajoutent
  à la main. À 3, la partie prend fin. **Points de victoire** : compteur manuel.
- **Fin du cycle** (p.35) : +1 Fiel par rouage corrompu encore en jeu, fin des PV
  renforcés temporaires, 4 pions d'action ; partie terminée après le cycle 4.

Le reste (effets des cartes, adversaire) se fait à la main avec les actions génériques. Blessures et soins s'ouvrent dans une fenêtre
superposée et leur compte rendu s'affiche en notification, avec un bouton
« Annuler » : les boutons des cartes ne bougent jamais. Cycle, Fiel, traque et points
de victoire sont dans la barre du haut (fixée à l'écran, sauf sur téléphone).

## Comptes

Facultatifs : sans compte, tout marche comme avant, sur l'appareil. `/builder/account` (lien
« Se connecter » de l'en-tête, le pseudo une fois connecté, dans toutes les apps) :

- **Création** : pseudo (3 à 24 caractères : lettres sans accent, chiffres, `-`, `_` ; unique
  sans tenir compte de la casse), e-mail, mot de passe (8 caractères au moins, sans règle de
  composition). Connexion par pseudo ou par e-mail ; session d'au moins 3 mois par appareil
  (cookie `olomoc_session` : HttpOnly, SameSite=Strict, Secure en HTTPS), renouvelée pour 3 mois à une visite passé la moitié.
- **E-mail** : ne sert qu'à récupérer le compte. Lien de confirmation (2 jours) à l'inscription
  et à chaque changement d'adresse ; **mot de passe oublié** seulement pour une adresse confirmée
  (lien d'une heure, à usage unique ; même réponse que le compte existe ou non). Au plus 3
  e-mails par heure et par compte. Liens vers `/builder/account/confirm` et `/reset`, compte et
  jeton dans l'ancre (retirés de l'adresse dès leur lecture).
- **Sécurité** : ASP.NET Core Identity (PBKDF2, verrouillage 15 minutes après 5 échecs).
  Changer de mot de passe, le réinitialiser ou « Déconnecter les autres appareils » ferme les
  autres sessions (tampon de sécurité relu chaque minute). Clés de chiffrement des cookies dans
  la base : un redéploiement ne déconnecte personne.
- **Rôles**, enregistrés dans la base (rôles d'Identity) :
  - **joueur** (par défaut) : tout le site, sauf l'éditeur ;
  - **éditeur** : en plus, l'éditeur du référentiel (et ses brouillons) ;
  - **admin** : tout, dont les rôles des autres comptes (« Mon compte », « Admin : rôles des
    comptes » : recherche par pseudo, jamais l'e-mail) et un mot de passe temporaire pour un
    joueur sans e-mail confirmé. Un admin ne change jamais son propre rôle : il en reste toujours
    un. Un changement de rôle vaut tout de suite pour l'API, dans la minute pour les brouillons
    servis.
  - **Premier admin** : créer son compte, puis redémarrer avec son pseudo dans `OLOMOC_ADMINS`
    (seuls des comptes existants le reçoivent). La variable donne le rôle à chaque démarrage
    sans jamais le retirer : à vider ensuite, ou à garder comme secours.
- **Cabales** : les mêmes sur tous les appareils (voir « Cabales sauvegardées »).
- **Suppression** du compte (mot de passe redemandé) et page **Données personnelles**
  (`/builder/privacy`).

Mise en service des e-mails, une fois : compte chez un relais SMTP (Brevo : gratuit jusqu'à 300
e-mails par jour), enregistrements DNS SPF, DKIM et DMARC de `orloj.top` (donnés par le relais),
puis `SMTP_HOST`, `SMTP_USER`, `SMTP_PASSWORD` dans le `.env` du VPS. Brevo désactive une clé
SMTP restée 90 jours sans servir : `MAIL_CHECK_TO` (ton adresse) envoie un court e-mail de contrôle
tous les 60 jours, qui la garde active et montre que l'envoi marche.

## Tournois

`/pairings/` : l'organisateur crée un tournoi (nom, date, nombre de rondes, 1 ou 2 cabales
par joueur, mode d'appariement) et reçoit un **code** à 6 caractères à donner aux joueurs,
ainsi qu'un **lien d'administration**. Pas de compte : chaque tournoi est un document JSON
dans la base du serveur ; jetons et mot de passe ne sont conservés que hachés.
L'organisateur peut **supprimer** son tournoi (en bas de l'administration, en retapant le
code). Sinon, un tournoi est supprimé automatiquement après 90 jours sans
modification, si sa date est passée ou s'il n'en a pas ; un tournoi daté d'aujourd'hui ou
plus tard ne l'est **jamais** (contrôle quotidien).

- **Administrer depuis plusieurs appareils** (créer le tournoi sur PC, le gérer sur
  téléphone le jour J), deux clés au choix :
  - le **lien d'administration** (jeton secret dans l'adresse), mis en avant juste après la
    création avec son **QR code** : on le scanne avec le téléphone, ou on le copie. Il
    reste dans « Partage » (QR sur demande, pour ne pas l'afficher par mégarde) ;
  - un **mot de passe** facultatif (8 caractères au moins), choisi à la création ou plus
    tard dans « Accès organisateur » : sur un appareil sans le lien, la page
    d'administration le demande et ouvre une session propre à cet appareil. Le changer ou
    le retirer ferme les autres sessions (pas celle de l'appareil qui le change ; le lien
    reste valable). Haché côté serveur (PBKDF2), 10 essais par quart d'heure et par adresse.
  Le lien public a aussi son QR code, à afficher sur place pour les inscriptions.
- **Inscriptions** : nom, faction, cabales choisies dans « Mes cabales » (sauvegardes du
  constructeur sur l'appareil, même site), composées sur le moment (« Composer… » ouvre le
  constructeur dans un nouvel onglet, sur la faction choisie ; la cabale sauvegardée apparaît
  dans la liste au retour sur l'onglet, sans perdre la saisie) ou **collées** (« Coller… ») :
  liste au format texte de l'export (une ligne par rouage, « ROUAGE - ÉQUIPEMENT/DON », nom
  en première ligne facultatif ; format du builder bwaaaanegamesdb aussi), tapée à la main ou
  copiée, sans souci de casse, d'accents ni de tiret, ou export JSON (`Services/CabalImport.cs`).
  Aperçu immédiat (cartes reconnues, lignes inconnues, légalité) ; un texte valable dans
  plusieurs factions fait choisir la sienne. Faction
  commune et légalité vérifiées dans le navigateur, puis par le serveur avec les mêmes
  règles que le constructeur. Chacun peut modifier ou retirer son inscription (jeton
  joueur, gardé sur son appareil) tant qu'elles sont ouvertes. Les listes des autres
  restent masquées jusqu'à la clôture, qui les révèle à tous. L'organisateur peut aussi
  **inscrire lui-même** un joueur (joueur sans téléphone, listes reçues à l'avance), avec le
  même formulaire depuis l'administration, et modifier ou retirer toute inscription ; le
  jeton d'un joueur qu'il inscrit n'est pas gardé, son appareil reste celui de l'organisateur.
- **Appariements** (`Tournaments/Pairer.cs`), entre les joueurs actifs (hors abandons) :
  - *Ronde suisse* : ronde 1 au hasard, puis par classement, chacun contre le plus
    proche en points (1 c. 2, 3 c. 4…) ; option : à points égaux, préférer l'alignement
    opposé ;
  - *Lumières contre Ténèbres* : au hasard, alignements opposés, le surplus d'un camp
    s'affrontant entre lui.
  Jamais de revanche si c'est possible (recherche avec retour arrière) ; sinon elle est
  admise et signalée. Nombre impair : exempt au moins bien classé qui n'en a pas eu (camp
  le plus nombreux en mode Alignement). Alignements (`olomoc-factions.json`) : Lumières
  — Cabale Dykovienne, Famille Urkaras, Loups de Libus, Réseau Rubin ; Ténèbres — Les
  Autres, Blockhaus Devet, Ligue de Maisel.
- **Résultats et classement** (feuille de ronde officielle) : l'organisateur saisit les
  PV de chaque table ; points de tournoi 3/1/0 (exempt : victoire au Fiel 7-0, soit 3 points, +7), classement par
  points, puis différence de PV cumulée, puis PV marqués. Les joueurs qui ont abandonné
  passent en fin de classement, derrière tous ceux encore en lice, et sont classés entre eux
  selon les mêmes critères. La ronde suivante s'apparie
  quand la précédente est complète ; la dernière ronde s'annule tant qu'aucun résultat
  n'y est saisi. Les pages se mettent à jour toutes les 30 secondes.
- **Cabale jouée** (facultative, pour les statistiques) : avec deux cabales par joueur, l'orga
  peut indiquer à la saisie des PV laquelle chacun a jouée (« C1 » / « C2 » sous chaque nom,
  un second clic retire le choix ; le nom de la cabale en info-bulle). Inconnue, la partie
  compte pour les victoires par faction, pas pour celles par rouage de rang 3.
- **J1 et fin de partie** (facultatifs, pour les statistiques) : à la saisie des PV, « J1 » sous
  le joueur qui a commencé, et la cause de la fin (Fiel, 4ᵉ cycle, PoliSec) ; affichés dans
  les tables.
- **Scénarios** (`Tournaments/Agendas.cs`) : l'orga prévoit à l'avance celui de chaque ronde,
  parmi les 10 agendas du recueil officiel (Occicat, janvier 2026 ; version anglaise de
  septembre 2026), ou « Autre » pour un scénario maison à nommer. Tirage au sort possible :
  agendas officiels, sans répétition, pour les rondes pas encore appariées (scénarios maison
  et rondes appariées gardés). Programme visible sur la page publique, scénario à côté du
  titre de chaque ronde.
- **Correction d'un appariement** : ⇄ sur deux joueurs de la ronde en cours (tables sans
  résultat, exempt compris) les échange, après confirmation ; une revanche créée est signalée.
- **Garde-fous** : 128 joueurs par tournoi, requêtes de 64 Ko au plus, 10 créations de
  tournoi par heure, 10 essais de mot de passe par quart d'heure et 120 écritures par
  minute par adresse IP (`RATE_CREATE_PER_HOUR`, `RATE_LOGINS_PER_15_MINUTES`,
  `RATE_WRITES_PER_MINUTE`).

## Statistiques

`/pairings/stats` (lien depuis l'accueil des tournois), publiques, sur les tournois **terminés**
— sauf ceux que leur orga exclut (« Ne pas compter dans les statistiques », à la création ou
à tout moment dans l'administration) :

- **factions jouées** : part des joueurs ;
- **choix des rouages**, par faction (sélecteur) et par rang : part des cabales inscrites de la
  faction qui contiennent le rouage (les deux cabales d'un joueur comptent) ;
- **victoires par faction** et **par rouage de rang 3** de la cabale jouée : victoires / parties,
  chaque camp d'une partie comptant (affrontement de même faction : des deux côtés ; exempts
  exclus), avec V/N/D et le nombre de parties ; estompé en dessous de 10 parties ;
- sur les parties où l'orga les a notés : **victoires de J1**, **fins de partie** (Fiel, 4ᵉ cycle,
  PoliSec), **scénarios joués** (avec les victoires de J1 par scénario ; scénarios maison
  regroupés, sans leur nom).

À la fin d'un tournoi, une **archive anonyme** (factions, cabales en ids, parties ; ni noms
ni jetons) est enregistrée dans la base (document « stats » du code du tournoi), que la purge
des tournois ne touche pas : les statistiques couvrent tout l'historique. Elles sont calculées en mémoire
(`Tournaments/Stats/TournamentStats.cs`) et recalculées à chaque archive ajoutée ou retirée
(`GET /api/stats`). Supprimer un tournoi, ou l'exclure, retire son archive ; au démarrage, un
tournoi terminé sans archive en reçoit une.

## Architecture

```
Olomoc.slnx
src/Olomoc.Shared/    bibliothèque Razor commune : modèles, règles, composants des cartes,
                      fichiers statiques servis sous /_content/Olomoc.Shared/ (Assets.Base)
src/Olomoc.Builder/   app Blazor WebAssembly du constructeur et du mode partie (/builder/)
src/Olomoc.Pairings/  app Blazor WebAssembly des tournois (/pairings/)
src/Olomoc.Server/    hôte ASP.NET Core : apps, fichiers communs, API des tournois, HTTPS
tests/Olomoc.Tests/   xUnit
```

**`src/Olomoc.Shared`**
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
- `Services/CabalExporter.cs` — exports texte (format bwaaaanegamesdb.fr) et JSON, relecture du JSON.
- `Services/CabalLibrary.cs` — cabales sauvegardées dans le navigateur, synchronisées avec le compte
  (`Accounts/CabalSyncModels.cs`) ; `CatalogService.Resolve` les relit.
- `Models/GameModels.cs`, `Services/GameSession.cs`, `Services/GameStore.cs` — mode
  partie : état sauvegardable, règles et annulation, sauvegarde dans le navigateur.
- `Services/CabalDraft.cs` — cabale en cours de composition, conservée en naviguant.
- `Components/StatLine.razor`, `AbilityList.razor`, `GameEffect.razor`,
  `Pictogram.razor` — caractéristiques, capacités et pictogrammes du livre ;
  `Emblem.razor` (logo de faction, emblème de Ministère) ;
  `TokenTrack.razor` (réserve de pions), `Sheet.razor` (fenêtre superposée).
- `Components/HeaderTools.razor` — fin de l'en-tête des apps : liens vers les autres apps, compte, langue.
- `Accounts/AccountApiModels.cs` (règles et échanges de l'API des comptes), `Services/AccountService.cs`
  (session commune aux apps).
- `Tournaments/` — tournoi (`TournamentModels.cs`), classement (`Standings.cs`),
  appariements (`Pairer.cs`), règles des phases et des inscriptions (`TournamentRules.cs`),
  échanges avec l'API (`TournamentApiModels.cs`).
- `wwwroot/` — référentiel (`data/`), langues (`i18n/`), pictogrammes (`img/`), styles
  (`css/app.css`), téléchargement de fichier et enregistrement du service worker
  (`js/olomoc.js`), application installable (`manifest.webmanifest`, icônes dans `img/app/`).

**`src/Olomoc.Builder`**
- `Pages/Home.razor` — constructeur : composition, légalité, sauvegarde et export, lancement d'une partie.
- `Pages/Play.razor`, `Shared/PlayCogCard.razor` — mode partie.
- `Pages/AdminCatalog.razor`, `Shared/CogEditor.razor`, `EquipmentEditor.razor`, `AbilitiesEditor.razor`,
  `CatalogDrafts.cs`, `AdminCatalogApi.cs` — éditeur du référentiel (éditeurs et admins) ;
  `Shared/RolesPanel.razor` — rôles des comptes (admins).
- `Pages/Account.razor`, `AccountConfirm.razor`, `AccountReset.razor`, `Privacy.razor` — compte,
  liens reçus par e-mail, données personnelles.

**`src/Olomoc.Pairings`**
- `Pages/Index.razor` — créer un tournoi, en rejoindre un, « Mes tournois » (localStorage).
- `Pages/TournamentPage.razor` — page publique : inscription, ronde, classement, joueurs.
- `Pages/Admin.razor`, `Shared/ResultEditor.razor` — administration : clôture, rondes,
  saisie des PV, abandons, export, liens de partage.
- `Services/TournamentApi.cs` (appels à l'API), `Services/MyTournaments.cs` (jetons gardés
  sur l'appareil).

**`src/Olomoc.Server`**
- `Program.cs` — en-têtes (`Cache-Control: no-cache`, `nosniff`), `/` → `/builder/`,
  `/healthz`, limites de débit et de taille des requêtes.
- `Tournaments/TournamentEndpoints.cs` — API `/api/tournaments` (droits par jeton) ;
  `TournamentStore.cs` (un document par tournoi, verrou par tournoi), `StatsArchive.cs`,
  `ServerCatalog.cs` (référentiel côté serveur), `TournamentPurge.cs` (90 jours sans modification, date passée ou absente).
- `Accounts/AccountEndpoints.cs` — API `/api/account` (Identity, cookie de session) et
  `/api/admin/accounts` (rôles, mot de passe temporaire) ; `Roles.cs` (joueur, éditeur, admin ;
  filtre des API, `OLOMOC_ADMINS`) ; `CabalSyncEndpoint.cs` (cabales du compte) ; `Mail.cs` (relais SMTP par MailKit, ou fichiers ; file d'envoi),
  `AccountMails.cs` (textes des e-mails).
- `CardCatalog/CatalogStore.cs` — référentiel servi : fichiers du dépôt + modifications de l'éditeur
  (`DATA_DIR/catalog`), brouillons cachés aux visiteurs, export ; `CatalogEndpoints.cs` (fichiers
  servis, API `/api/admin/catalog`).
- `Data/OlomocDb.cs` — base SQLite (EF Core) : table `Documents` (sorte, code, dates,
  JSON), comptes (tables d'Identity), cabales des comptes (`SavedCabals`), clés des cookies ; schéma par migrations (`Data/Migrations`, appliquées au démarrage ; nouvelle
  migration : `dotnet dotnet-ef migrations add <Nom> --project src/Olomoc.Server
  --output-dir Data/Migrations`). `JsonImport.cs` (reprise des anciens fichiers),
  `DbBackup.cs` (copie quotidienne, `VACUUM INTO`).
- `Hosting/DocumentStore.cs` — documents JSON sous un code public de 6 caractères (création,
  modification et suppression sous verrou, purge des inactifs).
- `Hosting/ClientApps.cs` — une app WebAssembly par chemin (`StaticWebAssetBasePath`),
  routes de l'app renvoyées vers son `index.html`.
- `Hosting/SecurityHeaders.cs` — CSP, cadres interdits, Referrer-Policy, HSTS en HTTPS.
- `wwwroot/service-worker.js` — hors connexion : réseau d'abord, copie en cache si le réseau
  manque (jamais l'API) ; à la racine pour couvrir les deux apps et `/_content/` (voir `docs/pwa.md`).

**`deploy/proxy`** — reverse proxy du VPS (Caddy) : HTTPS et certificats, répartition par
nom d'hôte entre Orloj et B5T.

**`tests/Olomoc.Tests`** — légalité, recherche, export, sauvegarde, mode partie, cohérence
des langues, tournois (classement, appariements, règles) ; référence `Olomoc.Shared` et lit
ses fichiers statiques sur le disque ; hôte démarré en mémoire (`WebApplicationFactory`)
pour les routes du site, l'API des tournois et celle des comptes (dossier de données
temporaire, e-mails écrits sur le disque).

## Licence

**Code** : GNU GPL v2 (fichier `LICENSE`).

**Contenu du jeu : © Occicat Games, utilisé avec permission, hors licence GPL.** Olomòc, ses
noms, textes, illustrations, logos et pictogrammes appartiennent à Occicat Games ; la licence
du code ne s'applique pas à eux et ne donne aucun droit de les réutiliser. Sont concernés :

- `src/Olomoc.Shared/wwwroot/img/` — images des cartes, logos des factions, emblèmes des
  Ministères, pictogrammes et icônes des caractéristiques ;
- `src/Olomoc.Shared/wwwroot/i18n/*/cards.json` — noms et effets de jeu des cartes, noms des
  Ministères et des factions ;
- `src/Olomoc.Shared/wwwroot/data/` — référentiel des cartes et référentiel de faction ;
- les noms des agendas et le vocabulaire du jeu repris dans `i18n/*/ui.json`.

Pour réutiliser ce contenu ailleurs, demandez l'accord d'Occicat Games
([occicat.com](https://www.occicat.com/)).

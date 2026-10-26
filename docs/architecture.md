# Architecture

Vue d'ensemble technique d'Orloj. Le détail des règles et des fonctions est dans le
[README](../README.md) ; le hors connexion dans [pwa.md](pwa.md).

## Découpage

```
Navigateur ── une app Blazor WebAssembly (Olomoc.Web), téléchargée une fois
   │            /builder/…  pages de Olomoc.Builder   (constructeur, partie, compte, éditeur, admin)
   │            /pairings/… pages de Olomoc.Pairings  (tournois, statistiques)
   │            /online/…   pages de Olomoc.Online    (parties en ligne à deux)
   │            localStorage : cabales, partie, langue, tournois et parties en ligne suivis
   │
   │  HTTPS (Caddy, deploy/proxy) ; WebSocket pour les parties en ligne
   ▼
Olomoc.Server (ASP.NET Core, un conteneur)
   ├── fichiers : l'app (/_framework/, index.html), /_content/Olomoc.Shared/ (CSS, images, langues)
   ├── référentiel : fichiers du dépôt + modifications de l'éditeur (DATA_DIR/catalog)
   ├── API : /api/tournaments, /api/stats, /api/account, /api/online, /api/admin/… (éditeur, panneau d'administration)
   ├── hub SignalR : /hubs/online (parties en ligne)
   └── SQLite : DATA_DIR/olomoc.db (+ backups/, mail/ sans SMTP)
```

| Projet | Rôle |
|---|---|
| `Olomoc.Shared` | Bibliothèque commune au navigateur et au serveur : modèles, règles (légalité, partie, appariements, classement, statistiques), services du navigateur, composants d'affichage, fichiers statiques. |
| `Olomoc.Web` | L'app du navigateur : démarrage, routeur, layout, menu. Réunit les trois bibliothèques de pages ; on passe de l'une à l'autre sans rechargement. |
| `Olomoc.Builder` | Pages `/builder/…`. |
| `Olomoc.Pairings` | Pages `/pairings/…`. |
| `Olomoc.Online` | Pages `/online/…` ; client SignalR. |
| `Olomoc.Server` | Hôte : sert l'app et les fichiers, API, hub des parties en ligne, base, e-mails, tâches de fond (purges, sauvegarde, e-mail de contrôle). |
| `tests/Olomoc.Tests` | xUnit : règles (Shared) et API (serveur en mémoire, base temporaire). |

Les règles sont dans `Olomoc.Shared` et tournent des deux côtés : le navigateur les applique
pour l'affichage, le serveur les revérifie pour tout ce qu'il enregistre (inscriptions,
résultats, référentiel, commandes des parties en ligne).

## Technos

- **.NET 10** : Blazor WebAssembly (navigateur), ASP.NET Core (serveur), xUnit.
- **SQLite + EF Core** : une base dans un fichier, schéma par migrations appliquées au
  démarrage. Tournois, archives de statistiques et parties en ligne : documents JSON (table `Documents`).
- **SignalR** (WebSocket) : synchronisation des parties en ligne.
- **ASP.NET Core Identity** : comptes, mots de passe, rôles, cookie de session (90 jours,
  prolongé à l'usage), clés de chiffrement dans la base.
- **MailKit** : e-mails par un relais SMTP (Brevo) ; sans `SMTP_HOST`, écrits dans `DATA_DIR/mail`.
- **QRCoder** : QR codes des liens de tournoi.
- **Docker** (image Alpine, lecture seule hormis `/data`) derrière **Caddy** (HTTPS, Let's Encrypt).
- **Service worker** maison : réseau d'abord, copie en cache sans réseau (jamais l'API).
- Aucune dépendance JavaScript : `olomoc.js` (téléchargements, fenêtre, impression, stockage)
  et le service worker.

## Onglets et données

| Onglet | Ce qu'il fait | Données lues | Données écrites |
|---|---|---|---|
| **Constructeur** `/builder/` | Compose une cabale (faction, 3 rouages, équipements), vérifie la légalité, exporte (texte, JSON, planche de cartes), sauvegarde. Catalogue des équipements dans une fenêtre à part. | Référentiel (`olomoc-catalog.json`, `olomoc-factions.json`, `i18n/*/cards.json`) | `olomoc.cabals` (localStorage) ; avec un compte : table `SavedCabals` (`/api/account/cabals/sync`). Cabale en cours : mémoire, partagée avec la fenêtre des équipements (`olomoc.draft`). |
| **Partie** `/builder/play` | La partie courante de l'appareil, locale ou en ligne. Locale : PV, Noirceur, Fiel, pions, capacités limitées, 4 cycles ; annulation ; « Ouvrir en ligne » la passe en ligne avec sa cabale. | La cabale lancée depuis le constructeur | `olomoc.game` (localStorage) ou `olomoc.game.online` (code de la partie en ligne courante) : une partie courante par appareil. Historique d'annulation : mémoire. |
| ↳ **En ligne** (parties `/online/g/…` ; sans partie en cours, cadre « Jouer en ligne ») | Partie à deux synchronisée : salon (cabales secrètes jusqu'à « Prêt »), numéro des joueurs, agenda, cycles ; chacun suit son camp, l'autre en lecture ; traque commune. Partie libre (lien d'invitation) ou table de tournoi. Nom : compte, sinon appareil, sinon tiré au sort. | Hub `/hubs/online` (vue propre à chaque siège) | Partie : table `Documents` (sorte `online`). Jetons de siège : `olomoc.online`, nom : `olomoc.online.name` (localStorage) ; présence : mémoire du serveur. |
| **Tournois** `/pairings/` | Crée ou rejoint un tournoi ; page publique (inscription, rondes, classement) ; administration (rondes, résultats, abandons, export). Rafraîchie toutes les 30 s. | `/api/tournaments/{code}` | Tournoi : table `Documents` (sorte `tournament`). Jetons organisateur et joueur : `olomoc.tournaments` (localStorage) et lien d'administration (jeton dans l'ancre). |
| **Stats** `/pairings/stats` | Statistiques publiques des tournois terminés : factions, rouages, victoires, scénarios, decks. | `/api/stats`, calculées en mémoire | — (l'archive anonyme est écrite à la fin d'un tournoi : `Documents`, sorte `stats`) |
| **Éditeur** `/builder/admin` (éditeurs, admins) | Crée et modifie rouages, équipements, dons, factions, en français et en anglais ; publication ; export zip. | Référentiel fusionné, brouillons compris | `DATA_DIR/catalog/` (JSON au format du dépôt, entrées modifiées seulement). Le dépôt n'est jamais modifié : export puis commit. |
| **Compte** `/builder/account` | Connexion, création, e-mail (confirmation, mot de passe oublié), mot de passe, autres appareils, suppression ; liens vers l'éditeur et le panneau d'administration. | `/api/account/me` | Tables d'Identity (comptes, rôles), `SavedCabals`. Session : cookie `olomoc_session`. |
| **Admin** `/builder/control` (admins) | Chiffres de la base ; comptes (rôle, mot de passe temporaire, déverrouillage, déconnexion, suppression) ; tournois de tout le monde (nouveau lien d'administration, stats, suppression) ; parties en ligne (suppression) ; sauvegarde et purges à la main ; journal. | `/api/admin/…` (overview, accounts, tournaments, online, audit) | Tables d'Identity, `Documents`, `DATA_DIR/backups/` ; chaque action dans la table `AdminActions` (gardée un an). |

Langue : `olomoc.lang` (localStorage) ; textes de l'interface `i18n/*/ui.json`, des cartes
`i18n/*/cards.json` (repli sur le français).

### Droits

- **Visiteur** : tout sauf l'éditeur ; cabales et partie sur l'appareil seulement.
- **Compte** : en plus, ses cabales sur tous ses appareils.
- **Éditeur** : en plus, l'éditeur et les brouillons. **Admin** : en plus, le panneau
  d'administration : les autres comptes (rôle, jamais le sien ; suppression, jamais d'un admin),
  les tournois et parties en ligne de tout le monde (actions limitées, journalisées).
  Premier admin : `OLOMOC_ADMINS`.
- **Tournois** : pas de compte. Administration par le jeton organisateur (dans le lien
  d'administration) ou un mot de passe facultatif ; modification d'une inscription par le
  jeton joueur. Un admin ne pilote pas un tournoi : il peut en générer un nouveau lien
  d'administration (l'ancien cesse de marcher), l'exclure des stats ou le supprimer.

## Limites connues

- **Un seul serveur** : SQLite et les verrous en mémoire excluent plusieurs instances.
  Sauvegarde : copie quotidienne (`data/backups/`, 7 gardées), à sauvegarder hors du VPS.
- **Partie** : une par appareil, ni partagée ni liée au compte.
- **Cabales sans compte** : propres à l'appareil et au navigateur (vider les données du site
  les efface). Avec un compte : 200 au plus ; en cas de conflit, la plus récente l'emporte, par nom.
- **Tournois** :
  - perdre le lien d'administration sans mot de passe, c'est perdre l'administration ;
  - « Mes tournois » est propre à l'appareil ;
  - 128 joueurs et 10 rondes au plus, 1 000 tournois en base ;
  - purgés après 90 jours sans modification (les statistiques restent) ;
  - mise à jour par rafraîchissement toutes les 30 s, pas en direct.
- **Hors connexion** : l'interface marche ; les données de tournoi et de compte non (l'API
  n'est jamais mise en cache).
- **Éditeur** :
  - pas d'envoi d'image (fichier à ajouter dans `img/cards/` du dépôt) ;
  - pas de création de faction ;
  - une entrée modifiée sur le serveur remplace celle du dépôt en entier : une PR sur la même
    entrée ne se voit qu'après export ou retour à la version du dépôt ;
  - après un enregistrement, recharger le site pour voir le référentiel à jour ailleurs.
- **Comptes** :
  - l'e-mail ne sert qu'à récupérer le compte ; sans e-mail confirmé, seul un admin peut
    réinitialiser le mot de passe ;
  - 3 e-mails par heure et par compte ; offre gratuite de Brevo : 300 par jour.
- **Taille** : l'app entière (constructeur et tournois) est téléchargée à la première visite,
  puis mise en cache.
- **Langues** : français et anglais ; une carte sans texte anglais s'affiche en français.
- **Parties en ligne** :
  - sans compte : perdre le lien (jeton du siège) sur un autre appareil, c'est jouer en spectateur ;
  - 2 000 parties en base, purgées après 7 jours sans activité ;
  - le Bloc (plateau, figurines) n'est pas encore géré : seuls les camps sont suivis.

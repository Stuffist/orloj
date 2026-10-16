# Installer Olomòc comme une application (PWA)

Étude préalable, faite sur l'état de `main` au 16 octobre 2026 (commit `26343f7`). Rien n'est
encore implémenté.

Une **PWA** (Progressive Web App) est un site qu'on peut installer sur l'écran d'accueil d'un
téléphone ou sur un ordinateur. Il s'ouvre alors en plein écran, sans barre d'adresse, comme une
vraie application. Ça reste le même site, servi par le même serveur, donc ça marche sur Android
comme sur iOS (et sur ordinateur), sans passer par un store.

Il y a deux niveaux :

1. **Installable** : quelques fichiers et balises. Peu de travail.
2. **Hors connexion** : un service worker qui garde le site en cache. C'est là qu'est le vrai travail.

## Ce qui est déjà en place

- **HTTPS** : obligatoire pour une PWA. C'est déjà fait par Caddy et Let's Encrypt sur orloj.top
  (`deploy/proxy/Caddyfile`).
- **CSP** (`src/Olomoc.Server/Hosting/SecurityHeaders.cs`) : `default-src 'self'` couvre le
  manifeste (`manifest-src`) et `script-src 'self'` couvre un service worker (`worker-src`), s'ils
  sont servis par le site lui-même. Rien à changer.
- **Sauvegardes sur l'appareil** (cabales, partie en cours, langue : `CabalLibrary`, `GameStore`,
  `LocalizationService`) : elles sont dans le `localStorage` et continuent de marcher dans l'app
  installée (voir la réserve iOS plus bas).

## Niveau 1 : installable

Estimation : environ une heure, surtout pour l'icône.

### 1. Le manifeste

Un fichier `manifest.webmanifest`, à mettre par exemple dans `src/Olomoc.Shared/wwwroot/` pour
que les deux apps le partagent (il est alors servi sous `/_content/Olomoc.Shared/`). Exemple :

```json
{
  "id": "/",
  "name": "Olomòc",
  "short_name": "Olomòc",
  "start_url": "/builder/",
  "scope": "/",
  "display": "standalone",
  "background_color": "#ffffff",
  "theme_color": "#ffffff",
  "icons": [
    { "src": "img/app/icon-192.png", "sizes": "192x192", "type": "image/png" },
    { "src": "img/app/icon-512.png", "sizes": "512x512", "type": "image/png" },
    { "src": "img/app/icon-maskable-512.png", "sizes": "512x512", "type": "image/png", "purpose": "maskable" }
  ]
}
```

Les couleurs sont à reprendre de `app.css`. Les chemins des icônes se lisent par rapport au
manifeste.

### 2. Les icônes

- 192 × 192 et 512 × 512 px ;
- une version « maskable » pour Android (le dessin tient dans le cercle central, à environ 80 %,
  pour qu'Android puisse la découper en rond ou en carré arrondi) ;
- une `apple-touch-icon` de 180 × 180 px pour iOS (fond plein, pas de transparence).

**C'est ce qui manque vraiment** : le site n'a aujourd'hui ni favicon ni logo Olomòc. Il faut en
dessiner une, ou reprendre un emblème (`img/emblems/`). Dans ce cas, il faut vérifier que la
permission d'Occicat Games couvre cet usage, puisque le contenu du jeu est utilisé avec leur
permission et ne relève pas de la licence GPL (voir le README).

### 3. Les balises dans les deux `index.html`

Dans `src/Olomoc.Builder/wwwroot/index.html` et `src/Olomoc.Pairings/wwwroot/index.html` :

```html
<link rel="manifest" href="/_content/Olomoc.Shared/manifest.webmanifest" />
<meta name="theme-color" content="#ffffff" />
<link rel="icon" href="/_content/Olomoc.Shared/img/app/icon-192.png" />
<link rel="apple-touch-icon" href="/_content/Olomoc.Shared/img/app/apple-touch-icon.png" />
```

Et éventuellement `viewport-fit=cover` dans la balise `viewport` pour occuper l'écran autour de
l'encoche des téléphones, avec `env(safe-area-inset-*)` dans le CSS pour ne rien masquer.

### Résultat

- **Android (Chrome, Edge, Samsung Internet)** : le menu propose « Installer l'application ». Un
  service worker n'est plus exigé pour ça.
- **iOS (Safari, et les autres navigateurs, qui utilisent le même moteur)** : il n'y a pas de
  proposition automatique. On passe par **Partager → Sur l'écran d'accueil**, et l'app s'ouvre en
  plein écran. Un petit texte d'aide dans l'interface serait utile.
- **Ordinateur (Chrome, Edge)** : une icône d'installation apparaît dans la barre d'adresse.

### Choix à faire : une app ou deux ?

Le constructeur et les tournois se renvoient l'un à l'autre : menu commun (`HeaderTools.razor`),
retour au tournoi depuis le constructeur (`Home.razor`), composition d'une cabale depuis
l'inscription (`CabalPicker.razor`).

Je recommande **une seule app** avec `scope: "/"`, qui ouvre le constructeur. Avec deux apps
limitées à `/builder/` et `/pairings/`, chaque passage de l'une à l'autre sortirait de l'app et
ouvrirait un navigateur intégré.

## Niveau 2 : hors connexion

Estimation : une demi-journée à une journée, tests compris.

Il faut un **service worker** : un script que le navigateur installe et qui répond aux requêtes du
site, depuis un cache quand il n'y a pas de réseau. Blazor WebAssembly en fournit un modèle
(propriété `ServiceWorkerAssetsManifest` dans le csproj, plus `service-worker.js` et
`service-worker.published.js`), mais plusieurs choses compliquent le cas d'Olomòc.

### Emplacement du service worker

Un service worker ne contrôle que les adresses situées sous son propre dossier. Or les données de
jeu, les traductions et les images sont servies sous `/_content/Olomoc.Shared/`
(`src/Olomoc.Shared/Assets.cs`), en dehors de `/builder/`. Un service worker placé sous
`/builder/` ne peut pas les intercepter.

Deux solutions :

- le servir à la racine du site (`/service-worker.js`) ;
- ou le laisser sous `/builder/` et ajouter l'en-tête `Service-Worker-Allowed: /` sur ce fichier
  côté serveur, pour l'enregistrer avec `scope: "/"`.

Le modèle de Blazor ne liste que les fichiers d'une app : il faut vérifier qu'il inclut bien ceux
d'`Olomoc.Shared` à leur vraie adresse, ou les ajouter.

### Poids

Les images de cartes (`img/cards/`) font environ 11 Mo (81 JPG). Le runtime .NET et les DLL de
`_framework` s'y ajoutent. Deux stratégies :

- **tout télécharger à l'installation** : tout marche hors ligne, mais le premier chargement est
  lourd sur réseau mobile ;
- **mettre en cache au fur et à mesure** : plus léger, mais seules les cartes déjà affichées sont
  disponibles hors ligne.

Un compromis : télécharger d'avance l'app et les JSON (petits), et les images au fur et à mesure.

### Mises à jour

Aujourd'hui, toutes les réponses sont en `Cache-Control: no-cache` (`src/Olomoc.Server/Program.cs`)
pour qu'un redéploiement (données, traductions) soit vu immédiatement. Avec un cache hors ligne, la
nouvelle version est téléchargée en arrière-plan et n'apparaît qu'au lancement suivant.

Pour garder le comportement actuel autant que possible :

- un bandeau « Nouvelle version disponible : recharger » quand le service worker a fini de
  télécharger une mise à jour ;
- et/ou demander toujours les JSON de données au réseau d'abord, avec le cache en secours
  seulement s'il n'y a pas de réseau.

### Tournois

Cette partie dépend du serveur : API `/api/tournaments` et `/api/stats`, rafraîchissement toutes
les 30 secondes. Hors ligne, seule l'interface s'afficherait. Les réponses de `/api/*` ne doivent
**jamais** être mises en cache par le service worker (données qui changent, jetons d'organisateur).

À l'inverse, le constructeur et le mode Partie se prêtent bien au hors ligne, par exemple autour
d'une table sans réseau.

## Points à tester sur iPhone, quel que soit le niveau

- **Stockage séparé** : sur iOS, l'app installée a son propre `localStorage`, distinct de celui de
  Safari. Les cabales déjà sauvegardées dans Safari n'apparaîtront pas dans l'app. L'export/import
  JSON existant (`CabalExporter`, `CabalImport`) permet de les transférer, et il faudrait le
  signaler aux joueurs. En contrepartie, l'app installée n'est pas soumise à l'effacement
  automatique des données que Safari applique aux sites peu visités.
- **Pas de bouton retour** en plein écran sur iOS (Android garde le geste ou le bouton retour du
  système). Chaque page doit avoir un moyen de revenir en arrière dans l'interface elle-même.
- **Fichiers et fenêtres** (`src/Olomoc.Shared/wwwroot/js/olomoc.js`) : les téléchargements
  (`download` pour l'export JSON, `renderCards` pour la planche PNG), `openWindow` et `print` se
  comportent différemment en mode app. Sur iOS, un téléchargement ouvre une feuille d'aperçu et
  `window.open` ouvre une vue Safari par-dessus l'app. Ça marche, mais à vérifier un par un.

## Et les stores ?

- **Play Store** : une PWA peut y être publiée sans réécriture, sous forme de TWA (Trusted Web
  Activity, générée par exemple avec Bubblewrap). Il faut un compte développeur Google et un
  fichier `/.well-known/assetlinks.json` sur le site.
- **App Store** : Apple refuse souvent une app qui n'est qu'un site enveloppé. Ça ne vaut pas le
  coup : l'ajout à l'écran d'accueil suffit.

## Fichiers concernés

| Fichier | Niveau | Changement |
| --- | --- | --- |
| `src/Olomoc.Shared/wwwroot/manifest.webmanifest` | 1 | nouveau |
| `src/Olomoc.Shared/wwwroot/img/app/*.png` | 1 | nouvelles icônes |
| `src/Olomoc.Builder/wwwroot/index.html` | 1 (et 2) | balises ; enregistrement du service worker |
| `src/Olomoc.Pairings/wwwroot/index.html` | 1 (et 2) | idem |
| `src/Olomoc.Shared/wwwroot/css/app.css` | 1 (option) | marges `safe-area` si `viewport-fit=cover` |
| service worker (racine ou `/builder/`) | 2 | nouveau |
| `src/Olomoc.Builder/Olomoc.Builder.csproj` | 2 | `ServiceWorkerAssetsManifest` si le modèle Blazor est repris |
| `src/Olomoc.Server/Program.cs` ou `Hosting/` | 2 | en-tête `Service-Worker-Allowed`, cache du service worker |
| interface (bandeau de mise à jour, aide iOS) | 2 | à ajouter, avec les textes dans `i18n/` |

## Prochaine étape proposée

Faire le niveau 1 sur une branche, avec des icônes provisoires à remplacer par une vraie icône,
puis tester l'installation sur un Android et un iPhone. Le niveau 2 peut venir ensuite,
séparément.

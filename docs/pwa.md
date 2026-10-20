# Installer Orloj comme une application (PWA)

Une **PWA** (Progressive Web App) est un site qu'on peut installer sur l'écran d'accueil d'un
téléphone ou sur un ordinateur. Il s'ouvre alors en plein écran, sans barre d'adresse, comme une
vraie application. Ça reste le même site, servi par le même serveur, donc ça marche sur Android
comme sur iOS (et sur ordinateur), sans passer par un store.

Deux niveaux, tous deux faits sur la branche `pwa` (un commit chacun) :

1. **Installable** : manifeste, icônes, balises.
2. **Hors connexion** : un service worker qui garde une copie du site en cache.

## Ce qui était déjà en place

- **HTTPS**, obligatoire pour une PWA : assuré par Caddy et Let's Encrypt sur orloj.top
  (`deploy/proxy/Caddyfile`). En local, `http://localhost` compte comme sûr.
- **CSP** (`src/Olomoc.Server/Hosting/SecurityHeaders.cs`) : `default-src 'self'` couvre le
  manifeste et `script-src 'self'` le service worker. Rien à changer.
- **Sauvegardes sur l'appareil** (cabales, partie en cours, langue) : dans le `localStorage`,
  elles marchent aussi dans l'app installée (voir la réserve iOS plus bas).

## Niveau 1 : installable

- `src/Olomoc.Shared/wwwroot/manifest.webmanifest` : **une seule app** pour tout le site
  (`scope: "/"`), qui s'ouvre sur le constructeur (`start_url: "/builder/"`), en plein écran
  (`display: "standalone"`), aux couleurs du fond du site (`#14181c`). Le constructeur et les
  tournois sont d'ailleurs une seule app Blazor (`Olomoc.Web`) : on passe de l'un à l'autre par
  le menu (`AppNav.razor`), sans rechargement.
- `src/Olomoc.Shared/wwwroot/img/app/` : icônes **provisoires**, dessinées d'après la marque de
  l'en-tête (`.mark` de `app.css` : anneaux rouille sur fond sombre). 192 et 512 px, une version
  « maskable » (dessin réduit pour qu'Android puisse la découper en rond), `apple-touch-icon`
  180 px pour iOS, favicon 32 px. À remplacer par une vraie icône le jour venu, aux mêmes noms et
  tailles. Si elle reprend un visuel du jeu, vérifier que la permission d'Occicat Games couvre
  cet usage.
- Les deux `index.html` : lien vers le manifeste, `theme-color`, favicon, `apple-touch-icon`.

Résultat :

- **Android (Chrome, Edge, Samsung Internet)** : le menu propose « Installer l'application » (ou
  « Ajouter à l'écran d'accueil »).
- **iOS** : pas de proposition automatique. Dans Safari : **Partager → Sur l'écran d'accueil**.
  L'app s'ouvre ensuite en plein écran.
- **Ordinateur (Chrome, Edge)** : une icône d'installation apparaît dans la barre d'adresse.

## Niveau 2 : hors connexion

`src/Olomoc.Server/wwwroot/service-worker.js`, servi à `/service-worker.js` et enregistré par
`src/Olomoc.Shared/wwwroot/js/olomoc.js`.

### Pourquoi à la racine

Un service worker ne contrôle que les adresses situées sous son propre dossier. Les données, les
traductions et les images sont servies sous `/_content/Olomoc.Shared/`, en dehors de `/builder/`.
Placé à la racine, il couvre l'app (`/_framework/`, `/builder/…`, `/pairings/…`) et ces fichiers, sans en-tête particulier.

### Réseau d'abord

- **En ligne**, chaque fichier est demandé au serveur, comme sans service worker. Un
  redéploiement est donc vu aussitôt, comme aujourd'hui (`Cache-Control: no-cache`), sans
  bandeau « nouvelle version ». Chaque réponse est copiée en cache au passage.
- **Hors ligne**, ou si le serveur ne répond pas en 4 secondes (réseau faible dans une salle),
  c'est la copie en cache qui sert.
- Toutes les routes des sections (`/builder/play`, `/pairings/stats`…) sont servies par la même
  copie de `index.html`. Une route jamais visitée marche donc aussi hors ligne.
- **L'API (`/api/…`) n'est jamais mise en cache** : données de tournoi vivantes, jetons
  d'organisateur. Hors ligne, la partie Tournois affiche son interface mais pas ses données.

### Première visite

La page et les fichiers chargés avant que le service worker ne prenne la main ne passent pas par
lui. `olomoc.js` lui envoie donc la liste des fichiers déjà chargés (Resource Timing), quand il
prend la main et chaque fois que la page passe en arrière-plan. Il copie ceux qui manquent.

Seules les images de cartes **déjà affichées** sont disponibles hors ligne. Tout précharger
coûterait environ 11 Mo dès la première visite.

### Vérifié en local (Edge sans interface, profil vierge)

Fait deux fois : avec `dotnet run` (Debug) et avec la version publiée (`dotnet publish -c
Release`, celle de l'image Docker, fichiers compressés compris).

1. Site lancé sur `http://localhost:5199`. Ouverture de `/builder/` : le service worker prend la
   main, Edge juge le site **installable** et lit le manifeste sans erreur. Fichiers en cache :
   249 en Debug, 82 en Release (assemblies élaguées). Aucune réponse de `/api/`.
2. **Serveur arrêté**, puis `/builder/`, `/builder/play` (jamais visitée) et
   `/builder/equipment` : toutes s'affichent complètes depuis le cache (emblèmes, pictogrammes),
   sans aucune erreur dans la console.

Reste à tester sur de vrais téléphones, une fois déployé.

## Tester

- **Sur ordinateur** : `dotnet run --project src/Olomoc.Server` (profil de lancement, en
  Development), ouvrir le site dans Chrome ou Edge. Outils de développement → **Application** :
  manifeste, service worker, contenu du cache. Onglet **Réseau** → « Hors connexion » pour
  simuler la perte de réseau. L'icône d'installation apparaît dans la barre d'adresse.
- **Sur téléphone** : il faut du HTTPS, donc déployer sur orloj.top (un téléphone qui ouvre
  `http://<IP du PC>:…` n'aura ni installation ni service worker). Pour Android sans déployer :
  débogage USB et `chrome://inspect` → redirection de port, le téléphone voit alors
  `localhost`.

## À tester sur iPhone

- **Stockage séparé** : sur iOS, l'app installée a son propre `localStorage`, distinct de celui
  de Safari. Les cabales déjà sauvegardées dans Safari n'apparaîtront pas dans l'app. Il faut les
  transférer par l'export/import JSON, et le signaler aux joueurs. En contrepartie, l'app
  installée n'est pas soumise à l'effacement automatique que Safari applique aux sites peu visités.
- **Pas de bouton retour** en plein écran sur iOS (Android garde le retour du système). Chaque
  page doit avoir un moyen de revenir en arrière dans l'interface.
- **Fichiers et fenêtres** (`olomoc.js`) : téléchargements (export JSON, planche PNG), fenêtre à
  part (`openWindow`) et impression se comportent différemment en mode app. Sur iOS, un
  téléchargement ouvre une feuille d'aperçu et `window.open` une vue Safari par-dessus l'app. À
  vérifier un par un.

## Limites et suites possibles

- **Le cache ne se vide pas** : chaque redéploiement y ajoute les fichiers de `_framework` qui ont
  changé (quelques centaines de Ko). On pourra supprimer plus tard les copies que la version en
  cours n'utilise plus.
- **Bouton « Installer l'application »** dans l'en-tête : sur Android et ordinateur via
  l'événement `beforeinstallprompt`, et sur iOS une aide « Partager → Sur l'écran d'accueil ».
  Textes à ajouter dans `i18n/`.
- **Raccourcis** du manifeste (appui long sur l'icône, par exemple « Tournois ») : le manifeste
  n'existe qu'en une langue.
- **Play Store** : une PWA peut y être publiée sans réécriture, sous forme de TWA (Trusted Web
  Activity, avec Bubblewrap). Il faut un compte développeur Google et
  `/.well-known/assetlinks.json` sur le site. **App Store** : Apple refuse souvent une app qui
  n'est qu'un site enveloppé, ça ne vaut pas le coup.

## Revenir en arrière

Retirer le manifeste suffit à défaire le niveau 1 (les icônes déjà posées sur les écrans
d'accueil restent, et ouvrent le site). Mais **un service worker reste installé dans
les navigateurs qui l'ont reçu**, même si le fichier disparaît du serveur. Si le niveau 2 a été
déployé puis abandonné, il faut publier à la place un `service-worker.js` qui se désinstalle :

```js
self.addEventListener("install", () => self.skipWaiting());
self.addEventListener("activate", event => event.waitUntil((async () => {
    await caches.delete("olomoc");
    await self.registration.unregister();
})()));
```

Retirer aussi l'enregistrement dans `olomoc.js`. Comme le service worker passe d'abord par le
réseau, il ne fige pas un vieux site en attendant : le risque se limite à une copie inutile.

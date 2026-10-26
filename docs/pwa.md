# Installing Orloj as an app (PWA)

A **PWA** (Progressive Web App) is a site that can be installed on a phone's home screen or on a
computer. It then opens full screen, without an address bar, like a real app. It is still the
same site, served by the same server, so it works on Android as on iOS (and on desktop), without
going through a store.

Two levels, both done on the `pwa` branch (one commit each):

1. **Installable**: manifest, icons, tags.
2. **Offline**: a service worker that keeps a copy of the site in its cache.

## What was already in place

- **HTTPS**, required for a PWA: handled by the reverse proxy in front of the server (on
  orloj.top: Caddy and Let's Encrypt, `deploy/proxy/Caddyfile`, specific to that server).
  Locally, `http://localhost` counts as secure.
- **CSP** (`src/Olomoc.Server/Hosting/SecurityHeaders.cs`): `default-src 'self'` covers the
  manifest and `script-src 'self'` the service worker. Nothing to change.
- **Saves on the device** (cabals, game in progress, language): in `localStorage`, they also
  work in the installed app (see the iOS caveat below).

## Level 1: installable

- `src/Olomoc.Shared/wwwroot/manifest.webmanifest`: **a single app** for the whole site
  (`scope: "/"`), which opens on the builder (`start_url: "/builder/"`), full screen
  (`display: "standalone"`), in the site's background color (`#14181c`). The builder and the
  tournaments are a single Blazor app anyway (`Olomoc.Web`): you move from one to the other
  through the menu (`AppNav.razor`), without reloading.
- `src/Olomoc.Shared/wwwroot/img/app/`: **temporary** icons, drawn after the header's mark
  (`.mark` in `app.css`: rust rings on a dark background). 192 and 512 px, a "maskable" version
  (smaller drawing so that Android can crop it into a circle), a 180 px `apple-touch-icon` for
  iOS, a 32 px favicon. To be replaced by a real icon when the time comes, with the same names
  and sizes. If it uses artwork from the game, check that Occicat Games' permission covers this
  use.
- Both `index.html` files: link to the manifest, `theme-color`, favicon, `apple-touch-icon`.

Result:

- **Android (Chrome, Edge, Samsung Internet)**: the menu offers "Install app" (or "Add to home
  screen").
- **iOS**: no automatic offer. In Safari: **Share → Add to Home Screen**. The app then opens full
  screen.
- **Desktop (Chrome, Edge)**: an install icon appears in the address bar.

## Level 2: offline

`src/Olomoc.Server/wwwroot/service-worker.js`, served at `/service-worker.js` and registered by
`src/Olomoc.Shared/wwwroot/js/olomoc.js`.

### Why at the root

A service worker only controls the addresses under its own folder. Data, translations and images
are served under `/_content/Olomoc.Shared/`, outside `/builder/`. Placed at the root, it covers
the app (`/_framework/`, `/builder/…`, `/pairings/…`) and these files, without any special header.

### Network first

- **Online**, each file is requested from the server, as without a service worker. A
  redeployment is therefore seen at once, as before (`Cache-Control: no-cache`), without a "new
  version" banner. Each response is copied into the cache on the way.
- **Offline**, or if the server does not answer within 4 seconds (weak network in a venue), the
  cached copy is used.
- All the section routes (`/builder/play`, `/pairings/stats`…) are served by the same copy of
  `index.html`. A route never visited therefore also works offline.
- **The API (`/api/…`) is never cached**: live tournament data, organizer tokens. Offline, the
  Tournaments part shows its interface but not its data.

### First visit

The page and the files loaded before the service worker takes over do not go through it.
`olomoc.js` therefore sends it the list of files already loaded (Resource Timing), when it takes
over and every time the page goes to the background. It copies the missing ones.

Only the card images **already displayed** are available offline. Preloading everything would
cost about 11 MB from the first visit.

### Checked locally (headless Edge, blank profile)

Done twice: with `dotnet run` (Debug) and with the published version (`dotnet publish -c
Release`, the one in the Docker image, compressed files included).

1. Site started on `http://localhost:5199`. Opening `/builder/`: the service worker takes over,
   Edge considers the site **installable** and reads the manifest without errors. Cached files:
   249 in Debug, 82 in Release (trimmed assemblies). No response from `/api/`.
2. **Server stopped**, then `/builder/`, `/builder/play` (never visited) and
   `/builder/equipment`: all of them display completely from the cache (emblems, pictograms),
   without any error in the console.

Still to be tested on real phones, once deployed.

## Testing

- **On desktop**: `dotnet run --project src/Olomoc.Server` (launch profile, in Development),
  open the site in Chrome or Edge. Developer tools → **Application**: manifest, service worker,
  cache content. **Network** tab → "Offline" to simulate losing the network. The install icon
  appears in the address bar.
- **On a phone**: HTTPS is needed, so the site must be deployed behind HTTPS (for the public
  instance: orloj.top). A phone opening `http://<PC IP>:…` gets neither installation nor service
  worker. For Android without deploying: USB debugging and `chrome://inspect` → port
  forwarding, the phone then sees `localhost`.

## To test on iPhone

- **Separate storage**: on iOS, the installed app has its own `localStorage`, distinct from
  Safari's. Cabals already saved in Safari will not appear in the app. They have to be moved
  with the JSON export/import, and players should be told. On the other hand, the installed app
  is not subject to the automatic erasing Safari applies to rarely visited sites.
- **No back button** full screen on iOS (Android keeps the system back). Each page must have a
  way to go back in the interface.
- **Files and windows** (`olomoc.js`): downloads (JSON export, PNG sheet), separate window
  (`openWindow`) and printing behave differently in app mode. On iOS, a download opens a preview
  sheet and `window.open` a Safari view over the app. To check one by one.

## Limits and possible next steps

- **The cache is never emptied**: each redeployment adds the `_framework` files that changed (a
  few hundred KB). The copies the current version no longer uses could be deleted later.
- **"Install app" button** in the header: on Android and desktop through the
  `beforeinstallprompt` event, and on iOS a "Share → Add to Home Screen" hint. Texts to add in
  `i18n/`.
- Manifest **shortcuts** (long press on the icon, e.g. "Tournaments"): the manifest only exists
  in one language.
- **Play Store**: a PWA can be published there without rewriting, as a TWA (Trusted Web
  Activity, with Bubblewrap). It needs a Google developer account and
  `/.well-known/assetlinks.json` on the site. **App Store**: Apple often refuses an app that is
  only a wrapped site, not worth it.

## Rolling back

Removing the manifest is enough to undo level 1 (icons already placed on home screens stay, and
open the site). But **a service worker stays installed in the browsers that received it**, even
if the file disappears from the server. If level 2 was deployed then abandoned, a
`service-worker.js` that uninstalls itself must be published instead:

```js
self.addEventListener("install", () => self.skipWaiting());
self.addEventListener("activate", event => event.waitUntil((async () => {
    await caches.delete("olomoc");
    await self.registration.unregister();
})()));
```

Also remove the registration in `olomoc.js`. Since the service worker goes to the network first,
it does not freeze an old site in the meantime: the risk is limited to a useless copy.

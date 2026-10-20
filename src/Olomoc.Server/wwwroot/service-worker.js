// Service worker du site (portée « / » : l'app, ses sections /builder/ et /pairings/, et /_content/) :
// l'app reste utilisable hors connexion. Réseau d'abord : en ligne, tout vient du serveur comme
// sans service worker (un redéploiement est vu aussitôt) ; la copie gardée en cache ne sert que
// si le réseau manque, ou tarde à répondre. Enregistré par olomoc.js.

const CACHE = "olomoc";
const NETWORK_TIMEOUT_MS = 4000;            // réseau trop lent (salle de jeu mal couverte) : copie en cache
const SECTIONS = ["builder", "pairings"];

self.addEventListener("install", () => self.skipWaiting());
self.addEventListener("activate", event => event.waitUntil(self.clients.claim()));

self.addEventListener("fetch", event => {
    const request = event.request;
    const url = new URL(request.url);
    if (!cacheable(url) || request.method !== "GET") return;
    event.respondWith(networkFirst(event, cacheKey(url, request.mode === "navigate")));
});

// Fichiers du site (pas les blob: des téléchargements), hors API (tournois, statistiques :
// données vivantes, jetons d'organisateur).
const cacheable = url => url.protocol === location.protocol && url.origin === location.origin && !url.pathname.startsWith("/api/");

// Toutes les routes des sections renvoient le même index.html : une seule copie.
function cacheKey(url, page) {
    const section = url.pathname.split("/")[1];
    return page && SECTIONS.includes(section) ? `${url.origin}/index.html` : url.href;
}

async function networkFirst(event, key) {
    const cache = await caches.open(CACHE);
    const network = fetch(event.request).then(response => {
        if (complete(response)) event.waitUntil(cache.put(key, response.clone()));
        return response;
    });
    network.catch(() => { });                  // échec traité ci-dessous, ou copie en cache déjà rendue
    try {
        return await Promise.race([network, timeout(NETWORK_TIMEOUT_MS)]);
    } catch {
        // Pas de copie : on attend le réseau (ou son erreur), comme sans service worker.
        return (await cache.match(key)) ?? network;
    }
}

// Réponses complètes du site seulement (pas les redirections ni les erreurs).
const complete = response => response.ok && response.type === "basic";

const timeout = ms => new Promise((_, reject) => setTimeout(() => reject(new Error("timeout")), ms));

// Première visite : la page et les fichiers chargés avant que le service worker ne prenne la main
// ne sont pas passés par lui. La page en envoie la liste (olomoc.js) : mis en cache à leur tour.
self.addEventListener("message", event => {
    if (event.data?.type !== "loaded") return;
    event.waitUntil(cacheMissing([
        [new URL(event.data.page), true],
        ...event.data.files.map(href => [new URL(href), false]),
    ]));
});

async function cacheMissing(entries) {
    const cache = await caches.open(CACHE);
    for (const [url, page] of entries) {
        if (!cacheable(url)) continue;
        const key = cacheKey(url, page);
        if (await cache.match(key)) continue;
        try {
            const response = await fetch(url);
            if (complete(response)) await cache.put(key, response);
        } catch { }                             // réseau perdu entre-temps : ce sera pour une autre visite
    }
}

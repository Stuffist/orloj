// The site's service worker (scope "/": the app, its /builder/, /pairings/ and /online/ sections, and /_content/):
// the app stays usable offline. Network first: online, everything comes from the server as without a
// service worker (a redeployment is seen at once); the cached copy is only used when the network is
// missing, or slow to answer. Registered by olomoc.js.

const CACHE = "olomoc";
const NETWORK_TIMEOUT_MS = 4000;            // network too slow (game venue with poor coverage): cached copy
const SECTIONS = ["builder", "pairings", "online"];

self.addEventListener("install", () => self.skipWaiting());
self.addEventListener("activate", event => event.waitUntil(self.clients.claim()));

self.addEventListener("fetch", event => {
    const request = event.request;
    const url = new URL(request.url);
    if (!cacheable(url) || request.method !== "GET") return;
    event.respondWith(networkFirst(event, cacheKey(url, request.mode === "navigate")));
});

// The site's files (not the blob: of downloads), except the API (tournaments, statistics: live
// data, organizer tokens) and the online games hub (/hubs/).
const cacheable = url => url.protocol === location.protocol && url.origin === location.origin && !url.pathname.startsWith("/api/") && !url.pathname.startsWith("/hubs/");

// All section routes return the same index.html: a single copy.
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
    network.catch(() => { });                  // failure handled below, or cached copy already returned
    try {
        return await Promise.race([network, timeout(NETWORK_TIMEOUT_MS)]);
    } catch {
        // No copy: wait for the network (or its error), as without a service worker.
        return (await cache.match(key)) ?? network;
    }
}

// Complete responses from the site only (no redirects, no errors).
const complete = response => response.ok && response.type === "basic";

const timeout = ms => new Promise((_, reject) => setTimeout(() => reject(new Error("timeout")), ms));

// First visit: the page and the files loaded before the service worker takes over did not go
// through it. The page sends their list (olomoc.js): they are cached in turn.
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
        } catch { }                             // network lost in the meantime: it will be for another visit
    }
}

// Petits services du navigateur pour les apps Blazor (appelés par JS interop).
window.olomoc = {
    // Enregistre un texte sous forme de fichier (téléchargement).
    download(fileName, text, type) {
        const url = URL.createObjectURL(new Blob([text], { type: type || "application/octet-stream" }));
        const link = Object.assign(document.createElement("a"), { href: url, download: fileName });
        document.body.appendChild(link);
        link.click();
        link.remove();
        setTimeout(() => URL.revokeObjectURL(url), 1000);
    },

    // Planche de cartes en image PNG : page blanche de widthMm × heightMm à dpi points par
    // pouce, chaque carte dessinée à sa position (mm). Images du même site (sans quoi le
    // navigateur interdit l'export du canevas).
    async renderCards(fileName, widthMm, heightMm, dpi, cards) {
        const px = mm => Math.round(mm / 25.4 * dpi);
        const canvas = Object.assign(document.createElement("canvas"), { width: px(widthMm), height: px(heightMm) });
        const ctx = canvas.getContext("2d");
        ctx.fillStyle = "#fff";
        ctx.fillRect(0, 0, canvas.width, canvas.height);
        ctx.imageSmoothingQuality = "high";
        const images = await Promise.all(cards.map(c => new Promise((resolve, reject) => {
            const img = new Image();
            img.onload = () => resolve(img);
            img.onerror = () => reject(new Error(c.src));
            img.src = c.src;
        })));
        cards.forEach((c, i) => ctx.drawImage(images[i], px(c.x), px(c.y), px(c.w), px(c.h)));
        const blob = await new Promise(resolve => canvas.toBlob(resolve, "image/png"));
        const url = URL.createObjectURL(blob);
        const link = Object.assign(document.createElement("a"), { href: url, download: fileName });
        document.body.appendChild(link);
        link.click();
        link.remove();
        setTimeout(() => URL.revokeObjectURL(url), 1000);
    },

    // Petite fenêtre à part (réutilisée si elle est déjà ouverte sous ce nom), à poser à côté
    // de la page. Renvoie false si le navigateur l'a bloquée.
    openWindow(url, name) {
        const w = window.open(url, name, "popup,width=480,height=900");
        if (!w) return false;
        w.focus();
        return true;
    },

    // Fenêtre d'impression du navigateur (impression ou « Enregistrer au format PDF »).
    print() { window.print(); },

    // Retour sur l'onglet (page de nouveau visible) : appelle la méthode .NET indiquée.
    // Renvoie un identifiant pour offVisible.
    onVisible(dotnetRef, method) {
        const handler = () => { if (document.visibilityState === "visible") dotnetRef.invokeMethodAsync(method); };
        document.addEventListener("visibilitychange", handler);
        window.olomoc._handlers.push(handler);
        return window.olomoc._handlers.length - 1;
    },
    offVisible(id) {
        const handler = window.olomoc._handlers[id];
        if (handler) document.removeEventListener("visibilitychange", handler);
        window.olomoc._handlers[id] = null;
    },

    // Clé du localStorage modifiée par une autre fenêtre du site (cabale publiée par le
    // constructeur) : appelle la méthode .NET indiquée avec la nouvelle valeur. Renvoie un
    // identifiant pour offStorage.
    onStorage(dotnetRef, key, method) {
        const handler = e => { if (e.key === key) dotnetRef.invokeMethodAsync(method, e.newValue); };
        window.addEventListener("storage", handler);
        window.olomoc._handlers.push(handler);
        return window.olomoc._handlers.length - 1;
    },
    offStorage(id) {
        const handler = window.olomoc._handlers[id];
        if (handler) window.removeEventListener("storage", handler);
        window.olomoc._handlers[id] = null;
    },
    _handlers: [],
};

// Application utilisable hors connexion : service worker du site (/service-worker.js, portée « / »).
if ("serviceWorker" in navigator) {
    performance.setResourceTimingBufferSize(1000);      // tous les fichiers de Blazor (des centaines en Debug)
    navigator.serviceWorker.register("/service-worker.js", { scope: "/" }).catch(() => { });

    // Page et fichiers chargés jusqu'ici, à garder en cache s'ils n'y sont pas déjà : ceux de la
    // première visite, chargés avant que le service worker ne prenne la main.
    const sendLoaded = () => navigator.serviceWorker.controller?.postMessage({
        type: "loaded",
        page: location.href,
        files: performance.getEntriesByType("resource").map(e => e.name),
    });
    navigator.serviceWorker.addEventListener("controllerchange", sendLoaded);
    document.addEventListener("visibilitychange", () => { if (document.visibilityState === "hidden") sendLoaded(); });
}

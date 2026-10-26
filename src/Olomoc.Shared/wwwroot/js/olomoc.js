// Small browser services for the Blazor apps (called through JS interop).
window.olomoc = {
    // Saves a text as a file (download).
    download(fileName, text, type) {
        const url = URL.createObjectURL(new Blob([text], { type: type || "application/octet-stream" }));
        const link = Object.assign(document.createElement("a"), { href: url, download: fileName });
        document.body.appendChild(link);
        link.click();
        link.remove();
        setTimeout(() => URL.revokeObjectURL(url), 1000);
    },

    // Card sheet as a PNG image: white page of widthMm × heightMm at dpi dots per inch, each card
    // drawn at its position (mm). Images from the same site (otherwise the browser forbids
    // exporting the canvas).
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

    // Small separate window (reused if it is already open under this name), to place next to the
    // page (optional width and height). Returns false if the browser blocked it.
    openWindow(url, name, width = 480, height = 900) {
        const w = window.open(url, name, `popup,width=${width},height=${height}`);
        if (!w) return false;
        w.focus();
        return true;
    },

    // Browser print window (print or "Save as PDF").
    print() { window.print(); },

    // Back on the tab (page visible again): calls the given .NET method.
    // Returns an identifier for offVisible.
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

    // localStorage key changed by another window of the site (cabal published by the builder):
    // calls the given .NET method with the new value. Returns an identifier for offStorage.
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

// App usable offline: the site's service worker (/service-worker.js, scope "/").
if ("serviceWorker" in navigator) {
    performance.setResourceTimingBufferSize(1000);      // all the Blazor files (hundreds in Debug)
    navigator.serviceWorker.register("/service-worker.js", { scope: "/" }).catch(() => { });

    // Page and files loaded so far, to keep in the cache if they are not there yet: those of the
    // first visit, loaded before the service worker takes over.
    const sendLoaded = () => navigator.serviceWorker.controller?.postMessage({
        type: "loaded",
        page: location.href,
        files: performance.getEntriesByType("resource").map(e => e.name),
    });
    navigator.serviceWorker.addEventListener("controllerchange", sendLoaded);
    document.addEventListener("visibilitychange", () => { if (document.visibilityState === "hidden") sendLoaded(); });
}

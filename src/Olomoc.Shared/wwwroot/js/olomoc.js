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
    _handlers: [],
};

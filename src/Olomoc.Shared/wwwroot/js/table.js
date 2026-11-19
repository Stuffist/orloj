// Table of an online game (TablePanel): an SVG in millimetres. The browser handles the gestures
// alone (smooth), .NET only hears of their result:
// - drag a movable piece ([data-piece][data-movable]): it follows the pointer, then Dropped(id, x, y);
//   a press without moving: Selected(id);
// - press on the board: Clicked(x, y) (placing a piece, deselecting); with the ruler, the distance
//   from the press to the pointer is drawn until the next press;
// - drag on the board (no ruler): pan; wheel or zoom(): zoom. The viewBox belongs to this script.
window.olomocTable = {
    _tables: new Map(),

    attach(svg, dotnet, size, margin) {
        const t = {
            svg, dotnet, ruler: false, drag: null, pan: null, measure: null,
            view: { x: -margin, y: -margin, w: size + 2 * margin, h: size + 2 * margin },
            limits: { min: 40, max: size + 2 * margin, from: -margin, to: size + margin },
        };
        const point = e => {
            const p = svg.createSVGPoint();
            p.x = e.clientX; p.y = e.clientY;
            return p.matrixTransform(svg.getScreenCTM().inverse());
        };
        const apply = () => svg.setAttribute("viewBox", `${t.view.x} ${t.view.y} ${t.view.w} ${t.view.h}`);
        t.apply = apply;
        t.point = point;

        t.down = e => {
            if (e.button !== undefined && e.button !== 0) return;
            const p = point(e);
            const piece = e.target.closest("[data-piece]");
            if (piece && !t.ruler) {
                const movable = piece.hasAttribute("data-movable");
                const [x, y] = (piece.getAttribute("data-at") || "0 0").split(" ").map(Number);
                t.drag = { piece, id: +piece.getAttribute("data-piece"), dx: x - p.x, dy: y - p.y, x, y, moved: false, movable,
                    rotate: piece.getAttribute("data-rotate") || "0", start: { x: e.clientX, y: e.clientY } };
                svg.setPointerCapture(e.pointerId);
                e.preventDefault();
                return;
            }
            if (t.ruler) {
                // From a miniature: from its centre, the distance counted from the edge of its base.
                const base = piece && piece.hasAttribute("data-radius") ? piece : null;
                const [cx, cy] = base ? base.getAttribute("data-at").split(" ").map(Number) : [p.x, p.y];
                t.measure = { x: cx, y: cy, from: piece ? +piece.getAttribute("data-piece") : null, r: base ? +base.getAttribute("data-radius") : 0 };
                window.olomocTable._ruler(t, p, e);
                svg.setPointerCapture(e.pointerId);
                e.preventDefault();
                return;
            }
            t.pan = { x: e.clientX, y: e.clientY, view: { ...t.view }, moved: false, p };
            svg.setPointerCapture(e.pointerId);
        };
        t.move = e => {
            if (t.drag) {
                if (Math.hypot(e.clientX - t.drag.start.x, e.clientY - t.drag.start.y) > 4) t.drag.moved = true;
                if (!t.drag.moved || !t.drag.movable) return;
                const p = point(e);
                const x = Math.min(t.limits.to, Math.max(t.limits.from, p.x + t.drag.dx));
                const y = Math.min(t.limits.to, Math.max(t.limits.from, p.y + t.drag.dy));
                t.drag.x = x; t.drag.y = y;
                t.drag.piece.setAttribute("transform", `translate(${x} ${y}) rotate(${t.drag.rotate})`);
            } else if (t.measure) {
                window.olomocTable._ruler(t, point(e), e);
            } else if (t.pan) {
                const rect = svg.getBoundingClientRect();
                const k = t.view.w / rect.width;
                const dx = (e.clientX - t.pan.x) * k, dy = (e.clientY - t.pan.y) * k;
                if (Math.hypot(e.clientX - t.pan.x, e.clientY - t.pan.y) > 4) t.pan.moved = true;
                t.view.x = t.pan.view.x - dx;
                t.view.y = t.pan.view.y - dy;
                apply();
            }
        };
        t.up = e => {
            if (t.drag) {
                const d = t.drag;
                t.drag = null;
                if (d.moved && d.movable) dotnet.invokeMethodAsync("Dropped", d.id, Math.round(d.x * 10) / 10, Math.round(d.y * 10) / 10);
                else dotnet.invokeMethodAsync("Selected", d.id);
            } else if (t.measure) {
                const m = t.measure;
                t.measure = null;
                const live = svg.querySelector(".tb-ruler-live");
                if (live) live.style.display = "none";
                const p = point(e);
                const target = document.elementsFromPoint(e.clientX, e.clientY).map(el => el.closest && el.closest("[data-piece]")).find(el => el);
                dotnet.invokeMethodAsync("Measured", m.from, target ? +target.getAttribute("data-piece") : null,
                    m.x, m.y, p.x, p.y);
            } else if (t.pan) {
                const pan = t.pan;
                t.pan = null;
                if (!pan.moved) dotnet.invokeMethodAsync("Clicked", Math.round(pan.p.x * 10) / 10, Math.round(pan.p.y * 10) / 10);
            }
        };
        t.wheel = e => {
            e.preventDefault();
            window.olomocTable._zoom(t, e.deltaY < 0 ? 0.85 : 1 / 0.85, point(e));
        };
        svg.addEventListener("pointerdown", t.down);
        svg.addEventListener("pointermove", t.move);
        svg.addEventListener("pointerup", t.up);
        svg.addEventListener("pointercancel", t.up);
        svg.addEventListener("wheel", t.wheel, { passive: false });
        apply();
        const id = Math.random().toString(36).slice(2);
        window.olomocTable._tables.set(id, t);
        return id;
    },

    detach(id) {
        const t = window.olomocTable._tables.get(id);
        if (!t) return;
        t.svg.removeEventListener("pointerdown", t.down);
        t.svg.removeEventListener("pointermove", t.move);
        t.svg.removeEventListener("pointerup", t.up);
        t.svg.removeEventListener("pointercancel", t.up);
        t.svg.removeEventListener("wheel", t.wheel);
        window.olomocTable._tables.delete(id);
    },

    setRuler(id, on) {
        const t = window.olomocTable._tables.get(id);
        if (t) t.ruler = on;
    },

    // factor < 1: closer; 0: back to the whole board.
    zoom(id, factor) {
        const t = window.olomocTable._tables.get(id);
        if (!t) return;
        if (factor === 0) {
            t.view = { x: t.limits.from, y: t.limits.from, w: t.limits.max, h: t.limits.max };
            t.apply();
            return;
        }
        window.olomocTable._zoom(t, factor, { x: t.view.x + t.view.w / 2, y: t.view.y + t.view.h / 2 });
    },

    _zoom(t, factor, at) {
        const w = Math.min(t.limits.max, Math.max(t.limits.min, t.view.w * factor));
        const k = w / t.view.w;
        t.view = { x: at.x - (at.x - t.view.x) * k, y: at.y - (at.y - t.view.y) * k, w, h: w };
        t.apply();
    },

    // Line from the press to the pointer, with its length in inches (the exact figures: Measured). As soon
    // as a miniature is involved (start, or under the pointer), from the edge of its base.
    _ruler(t, p, e) {
        const g = t.svg.querySelector(".tb-ruler-live");
        if (!g || !t.measure) return;
        const line = g.querySelector("line"), text = g.querySelector("text");
        line.setAttribute("x1", t.measure.x); line.setAttribute("y1", t.measure.y);
        let end = p, r = 0;
        const target = document.elementsFromPoint(e.clientX, e.clientY).map(el => el.closest && el.closest("[data-radius]")).find(el => el);
        if (target && +target.getAttribute("data-piece") !== t.measure.from) {
            const [x, y] = target.getAttribute("data-at").split(" ").map(Number);
            end = { x, y };
            r = +target.getAttribute("data-radius");
        }
        line.setAttribute("x2", end.x); line.setAttribute("y2", end.y);
        text.setAttribute("x", (t.measure.x + end.x) / 2); text.setAttribute("y", (t.measure.y + end.y) / 2 - 4);
        const d = Math.max(0, Math.hypot(end.x - t.measure.x, end.y - t.measure.y) - t.measure.r - r);
        text.textContent = `${(d / 25.4).toFixed(1)}″`;
        g.style.display = "";
    },
};

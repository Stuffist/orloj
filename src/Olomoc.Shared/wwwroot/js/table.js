// Table of an online game (TablePanel): an SVG in millimetres. The browser handles the gestures
// alone (smooth), .NET only hears of their result:
// - drag a movable piece ([data-piece][data-movable]): it follows the pointer, then
//   Dropped(id, x, y, rotation); a press without moving: Selected(id);
// - movement templates ([data-capsule]: a capsule of length × width): drag the handle of the selected
//   one ([data-handle], front or back, in a [data-handles-for] group above the bases) to rotate it. A magnetic template ([data-magnet]) dropped near a base
//   ([data-radius]) comes to touch it; a base dropped near a magnetic template comes to touch it;
//   shown while dragging;
// - press on the board: Clicked(x, y) (placing a piece, deselecting); with the ruler, the distance
//   from the press to the pointer is drawn until the next press;
// - drag on the board (no ruler): pan; wheel or zoom(): zoom. The viewBox belongs to this script;
// - keys 0 to 9 (top row): AuraKey(n), the ranges of the selected piece; Ctrl+C/X/V, Delete,
//   arrows (Ctrl: turn): Key(name, ctrl, shift).
window.olomocTable = {
    _tables: new Map(),

    /** Within this distance (mm, between the edges), a base and a magnetic template come to touch. */
    snap: 8,

    attach(svg, dotnet, size, margin) {
        const T = window.olomocTable;
        const t = {
            svg, dotnet, ruler: false, drag: null, turn: null, pan: null, measure: null,
            view: { x: -margin, y: -margin, w: size + 2 * margin, h: size + 2 * margin },
            limits: { min: 40, max: size + 2 * margin, from: -margin, to: size + margin },
        };
        const point = e => {
            const p = svg.createSVGPoint();
            p.x = e.clientX; p.y = e.clientY;
            return p.matrixTransform(svg.getScreenCTM().inverse());
        };
        const apply = () => svg.setAttribute("viewBox", `${t.view.x} ${t.view.y} ${t.view.w} ${t.view.h}`);
        const clamp = v => Math.min(t.limits.to, Math.max(t.limits.from, v));
        const round = v => Math.round(v * 10) / 10;
        t.apply = apply;
        t.point = point;

        t.down = e => {
            if (e.button !== undefined && e.button !== 0) return;
            const p = point(e);
            const handle = !t.ruler && e.target.closest("[data-handle]");
            const handles = handle && handle.closest("[data-handles-for]");
            const turned = handles && svg.querySelector(`[data-piece="${handles.getAttribute("data-handles-for")}"]`);
            const piece = e.target.closest("[data-piece]");
            if (turned) {
                // The handles are drawn above the bases (never hidden by them), turned with the template.
                const [x, y] = T._at(turned);
                t.turn = { piece: turned, handles, id: +turned.getAttribute("data-piece"), x, y, cx: x, cy: y,
                    back: handle.getAttribute("data-handle") === "back", rotate: +(turned.getAttribute("data-rotate") || 0) };
                svg.setPointerCapture(e.pointerId);
                e.preventDefault();
                return;
            }
            if (piece && !t.ruler) {
                const movable = piece.hasAttribute("data-movable");
                const [x, y] = T._at(piece);
                t.drag = { piece, id: +piece.getAttribute("data-piece"), dx: x - p.x, dy: y - p.y, x, y, moved: false, movable,
                    rotate: +(piece.getAttribute("data-rotate") || 0), start: { x: e.clientX, y: e.clientY } };
                svg.setPointerCapture(e.pointerId);
                e.preventDefault();
                return;
            }
            if (t.ruler) {
                // From a miniature: from its centre, the distance counted from the edge of its base.
                const base = piece && piece.hasAttribute("data-radius") ? piece : null;
                const [cx, cy] = base ? T._at(base) : [p.x, p.y];
                t.measure = { x: cx, y: cy, from: piece ? +piece.getAttribute("data-piece") : null, r: base ? +base.getAttribute("data-radius") : 0 };
                T._ruler(t, p, e);
                svg.setPointerCapture(e.pointerId);
                e.preventDefault();
                return;
            }
            t.pan = { x: e.clientX, y: e.clientY, view: { ...t.view }, moved: false, p };
            svg.setPointerCapture(e.pointerId);
        };
        t.move = e => {
            if (t.turn) {
                // Rotation around the centre, towards the pointer (the handle is at the front); a magnetic
                // template stays against the base it touches.
                const p = point(e), r = t.turn;
                r.rotate = Math.round(Math.atan2(p.x - r.cx, -(p.y - r.cy)) * 180 / Math.PI) + (r.back ? 180 : 0);
                [r.x, r.y] = T._snapTemplate(t, r.piece, r.cx, r.cy, r.rotate);
                const transform = `translate(${r.x} ${r.y}) rotate(${r.rotate})`;
                r.piece.setAttribute("transform", transform);
                r.handles.setAttribute("transform", transform);
            } else if (t.drag) {
                const d = t.drag;
                if (Math.hypot(e.clientX - d.start.x, e.clientY - d.start.y) > 4) d.moved = true;
                if (!d.moved || !d.movable) return;
                const p = point(e);
                let x = clamp(p.x + d.dx), y = clamp(p.y + d.dy);
                if (d.piece.hasAttribute("data-radius")) [x, y] = T._snapBase(t, d.piece, x, y, +d.piece.getAttribute("data-radius"));
                else if (d.piece.hasAttribute("data-magnet")) [x, y] = T._snapTemplate(t, d.piece, x, y, d.rotate);
                d.x = x; d.y = y;
                d.piece.setAttribute("transform", `translate(${x} ${y}) rotate(${d.rotate})`);
            } else if (t.measure) {
                T._ruler(t, point(e), e);
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
            if (t.turn) {
                const r = t.turn;
                t.turn = null;
                dotnet.invokeMethodAsync("Dropped", r.id, round(r.x), round(r.y), ((r.rotate % 360) + 360) % 360);
            } else if (t.drag) {
                const d = t.drag;
                t.drag = null;
                if (d.moved && d.movable) dotnet.invokeMethodAsync("Dropped", d.id, round(d.x), round(d.y), null);
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
                if (!pan.moved) dotnet.invokeMethodAsync("Clicked", round(pan.p.x), round(pan.p.y));
            }
        };
        t.wheel = e => {
            e.preventDefault();
            T._zoom(t, e.deltaY < 0 ? 0.85 : 1 / 0.85, point(e));
        };
        svg.addEventListener("pointerdown", t.down);
        svg.addEventListener("pointermove", t.move);
        svg.addEventListener("pointerup", t.up);
        svg.addEventListener("pointercancel", t.up);
        svg.addEventListener("wheel", t.wheel, { passive: false });
        // Keys 0 to 9 of the top row (whatever the layout: AZERTY gives &é"… without Shift), outside the
        // text fields: ranges of the selected piece.
        // Then Ctrl+C, X, V (copy, cut, paste), Delete and Backspace, arrows (Ctrl: turn): Key(name, ctrl,
        // shift). The browser's own action (scroll, copy) is kept when no piece is selected.
        t.key = e => {
            const el = document.activeElement;
            if (el && (el.isContentEditable || /^(INPUT|TEXTAREA|SELECT)$/.test(el.tagName))) return;
            const ctrl = e.ctrlKey || e.metaKey;
            const selected = !!svg.querySelector(".tb-piece.selected");
            const digit = /^Digit([0-9])$/.exec(e.code);
            if (digit && !ctrl && !e.altKey) {
                e.preventDefault();
                dotnet.invokeMethodAsync("AuraKey", +digit[1]);
                return;
            }
            if (e.altKey) return;
            const letter = ctrl && /^[cxv]$/i.exec(e.key);
            const name = letter ? letter[0].toLowerCase() : /^(Delete|Backspace|Arrow(Left|Right|Up|Down))$/.test(e.key) ? e.key : null;
            if (!name || (!selected && name !== "v") || (name === "v" && !ctrl)) return;
            e.preventDefault();
            dotnet.invokeMethodAsync("Key", name, ctrl, e.shiftKey);
        };
        document.addEventListener("keydown", t.key);
        apply();
        const id = Math.random().toString(36).slice(2);
        T._tables.set(id, t);
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
        document.removeEventListener("keydown", t.key);
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

    // ---- Geometry of the movement templates ----

    _at: el => (el.getAttribute("data-at") || "0 0").split(" ").map(Number),

    // A template as a capsule: the segment A–B and the radius R around it (rotation 0: lengthwise, up).
    _capsule(el, x, y, rotate) {
        const [length, width] = el.getAttribute("data-capsule").split(" ").map(Number);
        const half = Math.max(0, (length - width) / 2), a = rotate * Math.PI / 180;
        const dx = Math.sin(a) * half, dy = -Math.cos(a) * half;
        return { ax: x + dx, ay: y + dy, bx: x - dx, by: y - dy, R: width / 2 };
    },

    // Point of the segment A–B closest to (px, py).
    _closest(c, px, py) {
        const vx = c.bx - c.ax, vy = c.by - c.ay, l2 = vx * vx + vy * vy;
        const s = l2 > 0 ? Math.max(0, Math.min(1, ((px - c.ax) * vx + (py - c.ay) * vy) / l2)) : 0;
        return { x: c.ax + s * vx, y: c.ay + s * vy };
    },

    // Unit vector from q to p (p on q: across the segment).
    _unit(c, q, px, py) {
        const ux = px - q.x, uy = py - q.y, d = Math.hypot(ux, uy);
        if (d > 1e-6) return { x: ux / d, y: uy / d, d };
        const vx = c.bx - c.ax, vy = c.by - c.ay, l = Math.hypot(vx, vy);
        return l > 1e-6 ? { x: -vy / l, y: vx / l, d: 0 } : { x: 0, y: -1, d: 0 };
    },

    // A base (centre x, y, radius r) near a magnetic template: the position where it touches the
    // nearest one (gap between the edges under `snap`, overlaps included), or where it is.
    _snapBase(t, self, x, y, r) {
        const T = window.olomocTable;
        let best = null;
        for (const el of t.svg.querySelectorAll("[data-magnet]")) {
            if (el === self) continue;
            const [cx, cy] = T._at(el);
            const c = T._capsule(el, cx, cy, +(el.getAttribute("data-rotate") || 0));
            const q = T._closest(c, x, y), u = T._unit(c, q, x, y);
            const gap = u.d - c.R - r;
            if (gap < T.snap && (!best || Math.abs(gap) < best.gap))
                best = { gap: Math.abs(gap), x: q.x + u.x * (c.R + r), y: q.y + u.y * (c.R + r) };
        }
        return best ? [best.x, best.y] : [x, y];
    },

    // A magnetic template at (x, y, rotate): moved without turning so that its point closest to the
    // nearest base touches it (under `snap`), or where it is. Not magnetic: where it is.
    _snapTemplate(t, self, x, y, rotate) {
        const T = window.olomocTable;
        if (!self.hasAttribute("data-magnet")) return [x, y];
        const c = T._capsule(self, x, y, rotate);
        let best = null;
        for (const el of t.svg.querySelectorAll("[data-radius]")) {
            const [bx, by] = T._at(el), r = +el.getAttribute("data-radius");
            const q = T._closest(c, bx, by), u = T._unit(c, q, bx, by);
            const gap = u.d - c.R - r;
            if (gap < T.snap && (!best || Math.abs(gap) < best.gap)) best = { gap: Math.abs(gap), x: x + u.x * gap, y: y + u.y * gap };
        }
        return best ? [best.x, best.y] : [x, y];
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
            const [x, y] = window.olomocTable._at(target);
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

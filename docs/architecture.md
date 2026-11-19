# Architecture

Technical overview of Orloj. The details of the rules and features are in the
[README](../README.md); offline use in [pwa.md](pwa.md).

## Layout

```
Browser ── one Blazor WebAssembly app (Olomoc.Web), downloaded once
   │         /builder/…  Olomoc.Builder pages   (builder, play, account, editor, admin)
   │         /pairings/… Olomoc.Pairings pages  (events: tournaments, Dystopian Encounters)
   │         /bloc/…     Olomoc.Pairings pages  (the Block: zones)
   │         /stats/     Olomoc.Pairings page   (statistics: events and the Block)
   │         /online/…   Olomoc.Online pages    (two-player online games)
   │         localStorage: cabals, game, language, followed events and online games, zone entries
   │
   │  HTTPS (reverse proxy; orloj.top: Caddy, deploy/proxy); WebSocket for online games
   ▼
Olomoc.Server (ASP.NET Core, one container)
   ├── files: the app (/_framework/, index.html), /_content/Olomoc.Shared/ (CSS, images, languages)
   ├── card catalog: repository files + editor changes (DATA_DIR/catalog)
   ├── API: /api/tournaments, /api/stats, /api/zones, /api/account, /api/online, /api/admin/… (editor, admin panel)
   ├── SignalR hub: /hubs/online (online games)
   └── SQLite: DATA_DIR/olomoc.db (+ backups/, mail/ without SMTP)
```

| Project | Role |
|---|---|
| `Olomoc.Shared` | Library shared by the browser and the server: models, rules (legality, play, pairings, standings, statistics), browser services, display components, static files. |
| `Olomoc.Web` | The browser app: startup, router, layout, menu. Brings the three page libraries together; moving from one to another needs no reload. |
| `Olomoc.Builder` | `/builder/…` pages. |
| `Olomoc.Pairings` | `/pairings/…`, `/zones/…` and `/stats/` pages. |
| `Olomoc.Online` | `/online/…` pages; SignalR client. |
| `Olomoc.Server` | Host: serves the app and the files, API, online games hub, database, emails, background tasks (purges, backup, check email). |
| `tests/Olomoc.Tests` | xUnit: rules (Shared) and API (in-memory server, temporary database). |

The rules live in `Olomoc.Shared` and run on both sides: the browser applies them for display,
the server checks them again for everything it stores (registrations, results, card catalog,
online game commands).

## Technologies

- **.NET 10**: Blazor WebAssembly (browser), ASP.NET Core (server), xUnit.
- **SQLite + EF Core**: a database in one file, schema through migrations applied at startup.
  Events (`Tournament`), statistics archives, online games and zones: JSON documents (`Documents` table);
  games of the zones: `ZoneGames` table.
- **SignalR** (WebSocket): synchronization of online games.
- **ASP.NET Core Identity**: accounts, passwords, roles, session cookie (90 days, extended on
  use), encryption keys in the database.
- **MailKit**: emails through an SMTP relay (Brevo); without `SMTP_HOST`, written to `DATA_DIR/mail`.
- **QRCoder**: QR codes of the event links.
- **Docker** (Alpine image, read-only except `/data`) behind a reverse proxy for HTTPS (orloj.top: **Caddy** and Let's Encrypt, `deploy/proxy`, specific to that server).
- Home-made **service worker**: network first, cached copy without network (never the API).
- No JavaScript dependency: `olomoc.js` (downloads, window, printing, storage) and the service
  worker.

## Tabs and data

| Tab | What it does | Data read | Data written |
|---|---|---|---|
| **Builder** `/builder/` | Composes a cabal (faction, 3 cogs, equipment), checks legality, exports (text, JSON, card sheet), saves. Equipment catalog in a separate window. | Card catalog (`olomoc-catalog.json`, `olomoc-factions.json`, `i18n/*/cards.json`) | `olomoc.cabals` (localStorage); with an account: `SavedCabals` table (`/api/account/cabals/sync`). Cabal being composed: memory, shared with the equipment window (`olomoc.draft`). |
| **Play** `/builder/play` | The device's current game, local or online. Local: HP, Darkness, Bile, tokens, limited abilities, 4 cycles; undo; "Open online" takes it online with its cabal. | The cabal started from the builder | `olomoc.game` (localStorage) or `olomoc.game.online` (code of the current online game): one current game per device. Undo history: memory. |
| ↳ **Online** (games `/online/g/…`; without a game in progress, "Play online" box) | Synchronized two-player game: lobby (cabals secret until "Ready"), player numbers, agenda, cycles; each player tracks their side, the other read-only; shared hunt. Shared table (mat, terrain, miniatures, ruler; detachable window `/online/g/{code}/table`), played freely. Result confirmed at the end: into the Block zone receiving the online games, or on the event table. Free game (invitation link) or event table. Name: account, otherwise device, otherwise random. | `/hubs/online` hub (view specific to each seat) | Game: `Documents` table (kind `online`). Result of a free game: `ZoneGames` (column `OnlineCode`). Table assets: `data/olomoc-table.json`, `img/table/`. Seat tokens: `olomoc.online`, name: `olomoc.online.name` (localStorage); presence: server memory. |
| **Events** `/pairings/` | Creates (tournament or Dystopian Encounter) or joins an event; public page (registration, rounds, standings); administration (rounds, results, drops, export). Refreshed every 30 s. | `/api/events/{code}` | Tournament: `Documents` table (kind `event`). Organizer and player tokens: `olomoc.events` (localStorage) and admin link (token in the fragment). |
| **Stats** `/stats/` | Public statistics: everything (finished events and games of the Block together, a bound event's game counted once), or events only, or the Block only, over a period (from, to, shortcuts): factions, cogs, wins (faction, rank 3), scenarios, decks. Since the admins' reset, if any. | `/api/stats/all`, `/api/stats`, `/api/zones/stats` (`?from=&to=`), computed in memory | — (the anonymous archive is written at the end of an event: `Documents`, kind `stats`) |
| **Block** `/bloc/` | The Block: zones (club, convention, period, online), entry of stand-alone games without an account (duel or 2 v 2, cabals, P1, end, scenario and decks required, optional names), each zone's games, statistics and dominance (leading faction); statistics of all zones. | `/api/zones`, statistics computed in memory | Zone: `Documents` (kind `zone`); games: `ZoneGames` table. Entry tokens and zones seen: `olomoc.zones` (localStorage). |
| **Editor** `/builder/admin` (editors, admins) | Creates and edits cogs, equipment, boons, factions, in French and English; publication; zip export. | Merged card catalog, drafts included | `DATA_DIR/catalog/` (JSON in the repository format, changed entries only). The repository is never changed: export, then commit. |
| **Account** `/builder/account` | Sign-in, creation, email (confirmation, forgotten password), password, other devices, deletion; links to the editor and the admin panel. | `/api/account/me` | Identity tables (accounts, roles), `SavedCabals`. Session: `olomoc_session` cookie. |
| **Admin** `/builder/control` (admins) | Site settings (photos of the Block: `DATA_DIR/settings.json`); statistics (export, reset of all the statistics: `StatsSince` in the settings, nothing deleted, cancellable); database figures; accounts (role, temporary password, unlock, sign out, deletion); everyone's events (new admin link, stats, deletion); online games (deletion); backup and purges by hand; log. | `/api/admin/…` (overview, accounts, events, online, settings, stats/reset, export, audit) | Identity tables, `Documents`, `DATA_DIR/backups/`; each action in the `AdminActions` table (kept for a year). |

Language: `olomoc.lang` (localStorage); interface texts `i18n/*/ui.json`, card texts
`i18n/*/cards.json` (French fallback).

### Rights

- **Visitor**: everything except the editor; cabals and game on the device only.
- **Account**: in addition, their cabals on all their devices; its username is suggested when a
  game of the Block is entered, with its public cabals (per cabal; new ones public).
- **Editor**: in addition, the editor and the drafts, the zones and the data export
  (`/api/admin/export`). **Admin**: in addition, the admin panel: other accounts (role, never
  their own; deletion, never of an admin), everyone's events and online games
  (limited actions, logged). First admin: `OLOMOC_ADMINS`.
- **Events**: no account. Administration through the organizer token (in the admin link) or
  an optional password; a registration is changed with the player token. An admin does not run a
  event: they can generate a new admin link for it (the old one stops working), exclude it
  from the stats or delete it.
- **Zones** (the Block): created, deleted, hidden by the editors and admins, who also choose the
  dominance mode and the managers (optional accounts). Managers: settings, closing, moderation of
  their zone's games. Entering a game: anyone, no account, in an open zone; changing it: the
  entry token of the device, or the author's account, while the zone is open. An event can be
  bound to an open zone at its creation: its results are copied there, and only change through it.

## Known limits

- **A single server**: SQLite and in-memory locks rule out several instances. Backup: daily
  copy (`data/backups/`, 7 kept), to back up off the server.
- **Play**: one game per device, neither shared nor tied to the account.
- **Cabals without an account**: specific to the device and the browser (clearing the site's
  data erases them). With an account: 200 at most; on conflict, the most recent wins, by name.
- **Events**:
  - losing the admin link without a password means losing the administration (an admin can
    issue a new link);
  - "My events" is specific to the device;
  - 128 players and 10 rounds at most, 1,000 events in the database;
  - purged after 90 days without changes (statistics stay);
  - updated by refreshing every 30 s, not live.
- **Offline**: the interface works; event and account data do not (the API is never cached).
- **Editor**:
  - no image upload (file to add to `img/cards/` in the repository);
  - no faction creation;
  - an entry changed on the server replaces the repository's entirely: a PR on the same entry is
    only seen after an export or a return to the repository version;
  - after saving, reload the site to see the updated catalog elsewhere.
- **Accounts**:
  - the email is only used to recover the account; without a confirmed email, only an admin can
    reset the password;
  - 3 emails per hour and per account; Brevo free plan: 300 per day.
- **Size**: the whole app (builder and events) is downloaded on the first visit, then cached.
- **Languages**: French and English; a card without English text is shown in French.
- **Online games**:
  - without an account: losing the link (seat token) on another device means playing as a
    spectator;
  - 2,000 games in the database, purged after 7 days without activity;
  - the Block (board, miniatures) is not handled yet: only the sides are tracked.
- **Zones**:
  - all zones are public (a hidden zone is only left out of the list): no private zone yet;
  - a game entered without an account can only be changed from the same browser (entry token),
    or by a manager or an admin;
  - 500 zones and 10,000 games per zone; zones are never purged;
  - entries without an account are only limited per IP address (`RATE_ZONE_ENTRIES_PER_HOUR`):
    managers delete the unwanted ones;
  - photos of the games (when turned on): files in `DATA_DIR/photos/`, not in the daily database
    backup (to back up with the data folder);
  - a zone's logo is a link to an image on another site (the CSP accepts `https:` images for it):
    that site sees the visitors' IP address, and the image disappears if the link breaks.

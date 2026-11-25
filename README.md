# Orloj — Cabal builder and events for Olomòc (Blazor WebAssembly)

A Blazor WebAssembly app: cabal builder and play mode (`/builder/`, the root `/` redirects
there), events (tournaments and Dystopian Encounters) and statistics (`/pairings/`), two-player online games (`/online/`,
synchronized by SignalR), optional accounts and a card catalog editor. It is served by an
ASP.NET Core host (`src/Olomoc.Server`): API, online games hub, SQLite database, emails. The
card catalog (cogs, equipment, factions, texts) is JSON in the repository
(`src/Olomoc.Shared/wwwroot/`).

**Architecture, technologies, tabs and their data, known limits:
[docs/architecture.md](docs/architecture.md).**

Olomòc is a game by Occicat Games: the game content used here belongs to them and is not
covered by the code's license (see [License](#license)).

The site's interface is in French and English; French is the reference language of the game
texts.

## Running

Requires the .NET 10 SDK.

```bash
dotnet run --project src/Olomoc.Server
```

Then open `http://localhost:5180/` (redirects to `/builder/`).

**Becoming admin locally**: create your account ("Mon compte" / "My account"), stop the server,
then start it once with your username in `OLOMOC_ADMINS`; the role is then in the database
(`src/Olomoc.Server/App_Data/olomoc.db`, not versioned), and the variable is no longer needed:

```powershell
$env:OLOMOC_ADMINS = "MyUsername"; dotnet run --project src/Olomoc.Server
```

Tests: `dotnet test` at the root (`Olomoc.slnx` solution).

## Deployment (Docker)

This section has two parts:

- **[Generic](#generic-any-instance)**: what anyone hosting Orloj needs (image, data,
  settings, reverse proxy). Valid for any server and any domain.
- **[Specific to orloj.top](#specific-to-orlojtop-the-authors-server)**: how the public
  instance `https://orloj.top/` runs on its author's VPS. Given for reference; not needed to run
  Orloj elsewhere.

In the files, the same distinction is written in the comments: `deploy/proxy/` is entirely
specific to orloj.top; `Dockerfile` is generic; `docker-compose.yml` is generic except the
values marked "orloj.top" (image name, host names, email sender, public address), to override
in `.env`.

### Generic (any instance)

A single image (`Dockerfile`, multi-stage): publish with the .NET SDK, then the
`Olomoc.Server` host on the ASP.NET runtime (Alpine, ≈ 140 MB), HTTP on port 8080, under an
unprivileged user (`app`, uid 1654). It serves the app (builder `/builder/`, events
`/pairings/`; `_framework` files precompressed with Brotli/gzip), the shared files
(`/_content/Olomoc.Shared/`) and the API, redirects `/` to `/builder/`, and answers
`/healthz`. The data (events, statistics, accounts, account cabals) lives in a **SQLite**
database, `/data/olomoc.db` in the container, mounted on the `./data` folder next to
`docker-compose.yml`: kept across `down`/`up`, `--force-recreate` and `--build`. The server
makes a consistent copy of it every day in `data/backups/` (the last 7 are kept); that is the
folder to back up (`tar czf olomoc-data.tgz data`). Copying `olomoc.db` itself while the server
runs does not give a safe copy.

Moving to the versions with a database: at the first start, the old
`data/tournaments/*.json` and `data/stats/*.json` files are imported into the database, then
their folders are renamed `tournaments.imported` and `stats.imported` (kept as they are, never
imported again; an unreadable file stays there, reported in the container's log).

**HTTPS**: the container only speaks HTTP and opens no public port. Put a reverse proxy in
front of it (Caddy, nginx, Traefik…) on the same Docker network, `web` in
`docker-compose.yml`. The proxy must pass on the visitor's IP address and scheme
(`X-Forwarded-For` / `-Proto`); Orloj reads them (`ASPNETCORE_FORWARDEDHEADERS_ENABLED`) for the
per-visitor request limits and HSTS. With Caddy, two lines are enough:

```
your-domain.example {
	reverse_proxy olomoc:8080
}
```

**Installation**: once per machine, `docker network create web`. The data folder must belong to
the container's user (`sudo chown -R 1654:1654 data`). Then, from the sources:

```bash
docker compose up -d --build
```

**Settings**: `.env` file next to `docker-compose.yml`, **not versioned** (it contains the SMTP
credentials). The defaults marked *orloj.top* are those of the public instance: **another
instance must set them** to its own domain.

| Variable           | Default                         | Role                                         |
|--------------------|---------------------------------|----------------------------------------------|
| `OLOMOC_HOSTS`     | *orloj.top:* `orloj.top;localhost;127.0.0.1` | accepted host names (others: 400); keep `localhost;127.0.0.1` for the healthcheck |
| `PUBLIC_URL`       | *orloj.top:* `https://orloj.top` | public address of the site, base of the email links (without it: the request's host, only safe with a restricted `OLOMOC_HOSTS`) |
| `MAIL_FROM`        | *orloj.top:* `Orloj <noreply@orloj.top>` | sender of the emails (an address of a domain your SMTP relay is allowed to send for) |
| `OLOMOC_HTTP_PORT` | `8090`                          | diagnostic HTTP port, on 127.0.0.1 only      |
| `OLOMOC_ADMINS`    | (none)                          | usernames to make admins at startup, separated by `;` (existing accounts; first admin or fallback, never removed) |
| `SMTP_HOST`        | (none)                          | SMTP relay for emails (Brevo…); without it, emails are written to `data/mail` |
| `SMTP_PORT`        | `587`                           | relay port (STARTTLS; 465: TLS)              |
| `SMTP_USER`, `SMTP_PASSWORD` | (none)                | relay credentials (secrets: in `.env` only)  |
| `MAIL_CHECK_TO`    | (none)                          | check email every 60 days (keeps the SMTP key active) |

The `image:` line of `docker-compose.yml` (`stuffist/olomoc`) is the orloj.top image on Docker
Hub; with `--build`, the image is built from the sources under that local name, nothing is
downloaded. To publish your own image, change that name.

Diagnostics: `curl -I http://127.0.0.1:8090/healthz`; state: `docker ps` (`/healthz`
healthcheck); logs: `docker logs olomoc`.

**Locally (Docker Desktop)**: `docker network create web`, then the same
`docker compose up -d --build` → `http://localhost:8090/`. Stop: `docker compose down`. For
day-to-day development, `dotnet run --project src/Olomoc.Server` stays faster (no image
rebuild).

### Specific to orloj.top (the author's server)

How the public instance `https://orloj.top/` is hosted. Nothing here is needed to run Orloj
elsewhere.

- **Server**: a VPS shared with another project, B5T.
- **Reverse proxy**: a **Caddy** container (`deploy/proxy/docker-compose.yml` and `Caddyfile`,
  copied to `/var/www/proxy/` on the VPS) is the only one holding ports 80/443. It obtains and
  renews the Let's Encrypt certificates itself (validation on port 80), redirects HTTP to HTTPS
  and routes by host name on the `web` network:

  | Name                                  | Destination                                   |
  |---------------------------------------|-----------------------------------------------|
  | `orloj.top`                           | Orloj (`olomoc:8080`)                         |
  | `dauphine.orloj.top`                  | B5T (`b5tweb:8080`)                           |
  | `www.orloj.top`, `…:8443` (old Orloj addresses) | redirect to `https://orloj.top`     |
  | `vps-24cf4978.vps.ovh.net` (old B5T address) | redirect to `https://dauphine.orloj.top` |

- **Updates** (no sources on the VPS, as for B5T): `docker compose build && docker compose push`
  on the workstation (`stuffist/olomoc` image on Docker Hub), then on the VPS, with only
  `docker-compose.yml` and `.env`: `docker compose pull && docker compose up -d`.
- **Proxy**: `docker compose up -d` in `/var/www/proxy/`; after changing the `Caddyfile`,
  `docker compose exec caddy caddy reload --config /etc/caddy/Caddyfile`. Logs:
  `docker logs caddy`.
- **Emails**: Brevo relay, DNS records (SPF, DKIM, DMARC) of `orloj.top`, credentials in the
  VPS's `.env`.

## Vocabulary

- **Everything displayed** (interface, messages, game names and effects) follows the
  [French rulebook V1.1](https://www.occicat.com/wp-content/uploads/2026/01/LDR_V1.1.pdf).
- **Everything hidden** (code, JSON keys and codes, CSS classes, files) follows the
  [English rulebook](https://www.occicat.com/wp-content/uploads/2026/08/LDR_EN_olomoc.pdf).

| French rulebook V1.1 (display)    | English rulebook (identifiers)          |
|-----------------------------------|-----------------------------------------|
| rouage, cabale                    | `Cog`, cabal                            |
| équipement / don                  | `Equipment`, `EquipmentKind.EQUIPMENT` / `BOON` |
| Technique, Combat, Tir, Esquive   | `tech`, `melee`, `ranged`, `dodge`      |
| ligne de vie                      | `lifeLine`                              |
| PV standard / renforcé (Protection) / teinté | `S` / `R` / `T` (*standard / reinforced / tainted*) |
| temporalité : Active / Permanente | `timing`: `Active` / `Permanent`        |
| effet de jeu                      | `gameEffect`                            |
| pion d'action, pion de Noirceur   | `actionCost`, `darknessCost`            |
| Sain / Teinté / Corrompu, Fiel, hors de combat, sonné | `Sane` / `Tainted` / `Corrupted`, `Bile`, `OutOfAction`, `Stunned` |
| niveau de traque (PoliSec), points de victoire | `Hunt`, `VictoryPoints` |
| fréquence d'utilisation           | `frequencies` (`PLANNING_PHASE`, `CYCLE`, `GAME`) |
| taille de socle                   | `baseSize`                              |
| recto / verso                     | `front` / `back`                        |
| Référentiel de faction            | `FactionReference`                      |
| Ministère de la Coordination, de l'Intégrité, de l'Effort, du Progrès, de l'Harmonie, de l'Édification, de la Sécurité ; la Frange, l'Axe (Ministères dévoyés) | `COORDINATION`, `INTEGRITY`, `EFFORT`, `PROGRESS`, `HARMONY`, `EDIFICATION`, `SECURITY`, `FRINGE`, `AXIS` |
| Bloc, zone, affrontement (partie enregistrée seule dans une zone) | Block, `Zone`, `ZoneGame` |
| événement : tournoi (rondes suisses) ou Rencontre Dystopique (appariements aléatoires) | event: `Tournament` (code, API `/api/tournaments`, storage, `/pairings/`), `EventFormat.Tournament` / `EventFormat.Encounter` |

In the code and comments, the French game terms are written with the English rulebook's words:
cog, cabal, equipment/boon, Ministry, HP, Darkness, Bile, hunt, rank, card catalog (the French
"référentiel").

Faction names follow the [faction reference](https://www.occicat.com/wp-content/uploads/2026/09/FACTIONS_FR.pdf)
(September 2026): Les Autres, Blockhaus Devet, Cabale Dykovienne, Ligue de Maisel, Famille
Urkaras, Loups de Libus, Réseau Rubin; kept as they are in English, for lack of an English
reference. Codes: `OTHERS`, `BLOCKHAUS`, `DYKOVIAN`, `MAISEL`, `URKARAS`, `LIBUS`, `RUBIN`.

## Data

The card catalog is **language-neutral**; texts live in per-language packs.

- `src/Olomoc.Shared/wwwroot/data/olomoc-catalog.json` — cogs, equipment/boons, ministries: ids,
  codes, stats, life lines, costs, frequencies, composition rules
  (schema `schemas/olomoc.schema.json`).
- `src/Olomoc.Shared/wwwroot/data/olomoc-factions.json` — faction memberships
  (schema `schemas/olomoc-factions.schema.json`).
- `src/Olomoc.Shared/wwwroot/i18n/<language>/cards.json` — card texts: names, titles, ability
  names and game effects, Ministry and faction names
  (schema `schemas/olomoc-texts.schema.json`).
- `src/Olomoc.Shared/wwwroot/i18n/<language>/ui.json` — interface labels
  (schema `schemas/olomoc-ui.schema.json`).
- `src/Olomoc.Shared/wwwroot/img/cards/<id>.jpg` — card images (front), one per cog and
  equipment/boon, named by their id: 1051 × 744 px, i.e. 300 dpi at actual size (89 × 63 mm).
  Taken from bwaaaanegamesdb.fr and recompressed; these are Occicat Games' illustrations, used
  with their permission (see [License](#license)).
- `src/Olomoc.Shared/wwwroot/img/emblems/factions|ministries/<code>.png` — faction logos
  (faction reference, September 2026) and Ministry emblems (rulebook p.25, central disc only:
  the textured ring is unreadable when small), one per code. 96 × 96 px masks (white, the shape
  in the alpha channel) tinted in CSS, like the pictograms: logos in the text color, emblems in
  rust. Extracted from Occicat's PDFs (embedded images; rendered at 600 dpi for the vector logos
  of Dykovia and Maisel).
- `src/Olomoc.Shared/wwwroot/data/olomoc-table.json` and `img/table/boards|terrain|markers|tokens|templates/` —
  what can be put on the table of an online game: the 16″ game mats, the terrain (size,
  obstructing or not, outline), the markers, the token of each cog and equipment/boon (catalog id,
  base diameter), and the movement templates (length, width). Sizes in mm. Imported from Occicat's Vassal module, with their permission, by
  `tools/VassalImport` (images copied as they are; in the module, 30 px = 1″; the objectives' bases (30 mm) and the push template (25 mm) are set by the tool). With a new
  version of the module:
  `dotnet run --project tools/VassalImport -- path/to/OLOMOC_2P_V1.x.vmod` (empties `img/table/`,
  rewrites the manifest, lists the pieces left out: NPCs, proxies, cards without a French version). Names: matched on the French card names and titles ("« Sonar »"), with a few overrides
  in the tool.

Each file references its schema through `$schema` (validation and autocompletion in the
editor). The `schemas/` folder also contains minimal examples (`referentiel.example.json`,
`factions.example.json`, `textes.example.json`); it is neither copied into the build nor
published. Command-line validation:

```bash
npx ajv-cli@5 validate --spec=draft2020 --strict=false   -s schemas/olomoc.schema.json -d src/Olomoc.Shared/wwwroot/data/olomoc-catalog.json
npx ajv-cli@5 validate --spec=draft2020 --strict=false   -s schemas/olomoc-texts.schema.json -d "src/Olomoc.Shared/wwwroot/i18n/*/cards.json"
```

Replacing these files is enough to update the app; no code to touch. The tests
(`dotnet test`) also check the consistency between the catalog and the packs.

Current content (taken on 2026-10-08): 28 cogs, 53 equipment/boons, 7 factions. Affiliations
(factions, ministries, ranks) come from the filters of the
[bwaaaanegamesdb.fr](https://bwaaaanegamesdb.fr/builder_olomoc/index_builder_olomoc.php)
builder; stats, life lines and abilities were transcribed from the card images. Transcription
conventions (pictograms: rulebook p.20):

- The rank is the site's, **not** the code prefix (`1R01` is a rank 3).
- Grey HP (plain or hatched depending on the edition) → `R` (reinforced).
- White cube ×N → `actionCost: N`; black cube ×N → `darknessCost: N`.
- Frequencies: crown N → `{count: N, per: PLANNING_PHASE}`, pentagon N →
  `{count: N, per: CYCLE}`, circle N → `{count: N, per: GAME}` (cumulative).
- `baseSize` is printed on the back of the cards, missing from the source: it stays empty.

### Card catalog editor (editors and admins)

`/builder/admin` (link in "My account", "Card catalog" section): cogs, equipment and boons,
factions, with their texts in each language of `languages.json` (French name required; the
other languages fall back on it).

- **Cogs**: rank, Ministries, **factions** (faction reference), stats, life line (letters S, R,
  T), base, extra card, image, abilities (timing, costs, frequencies; name and effect per
  language), name, title, instinct.
- **Equipment and boons**: type, exclusive affiliation (Ministry, faction or cog), added health
  points, composition rules, image, abilities, name.
- **Factions**: alignment, name and description per language.
- **Publication**: only an entry with `"published": true` is published. Without this field (or
  with `false`), it is a draft, served to editors and admins only (marked "draft" in the
  builder), removed for everyone else with its texts and its place in the factions, and refused
  for event registrations. A new entry is therefore a draft until it is published; a PR
  that adds a card visible to all sets `"published": true`.
- **Storage**: the repository files stay the base (PRs welcome); the changes made in the editor
  are JSON files in the same format, with only the changed or created entries, in
  `data/catalog/` (`data/olomoc-catalog.json`, `data/olomoc-factions.json`,
  `i18n/<language>/cards.json`), merged by id or code with the repository's. The catalog files
  are served, merged, at the same addresses (`/_content/Olomoc.Shared/…`).
- **Export**: "Export" tab, a zip of the complete files at the repository paths, to unzip at
  its root then commit. At the next start, the changes that have become identical to the
  repository are removed from `data/catalog/`; "Back to the repository version" cancels those
  of an entry (a new entry is then deleted).
- Identifiers never change (saved cabals refer to them); images (`img/cards/`) have to be added
  to the repository.

## Languages

- `src/Olomoc.Shared/wwwroot/i18n/languages.json` lists the offered languages (selector in the
  header). The language is detected from the browser, then remembered.
- `fr` is the **reference language**, complete. The other packs can be partial: any missing
  text falls back to `fr` (shown in underlined italics). Language-independent rules (duplicate
  names) compare the `fr` names.
- **Adding a language**: create `src/Olomoc.Shared/wwwroot/i18n/<code>/ui.json` and
  `cards.json`, then add it to `languages.json`. The `ui.json` keys are those of `fr`; the stat
  codes and names (`stat.*.abbr`, `stat.*.name`) also become search keywords, valid whatever
  the displayed language.
- **Pictograms**: identical in every language, never translated. In game effects, they are
  neutral tokens rendered with the rulebook's icons (`src/Olomoc.Shared/wwwroot/img/icons/`,
  `src/Olomoc.Shared/wwwroot/img/stats/`): `{tech}` `{melee}` `{ranged}` `{dodge}` `{active}`
  `{permanent}` `{action}` `{darkness}` `{stunned}` (stunned token) and
  `{freq:planning|cycle|game:N}`. Central list: `Models/GameTokens.cs`, used by the card
  catalog editor's "ⓘ Keywords" glossary.
- **Factions**: without an official English name, `en/cards.json` keeps the French names
  (assumed proper nouns, so not flagged as untranslated).
- The tests check that each card has its `fr` texts, that no language contains an unknown id or
  key, and that the pictograms of a game effect are the same in every language.

## Rules applied (rulebook p.32)

- A cabal must consist of 3 cogs: one rank 1, one rank 2 and one rank 3, all belonging to the
  **same faction**.
- Each cog must be equipped with **one** equipment/boon, affiliated with a Ministry of one of
  the cabal's cogs, or with the faction, or with the equipped cog.
- **Each cog makes only one equipment/boon of its affiliated Ministry available**: Ministry
  equipment is assigned to distinct cogs (bipartite matching). The legality panel reports the
  overflow.
- No duplicates: two cogs or two equipment/boons with the same name.
- **Composition rules carried by the cards** (`buildRules` in the catalog, interpreted by
  `CheckLegality`):
  - `RECRUIT_MINISTRY` — *Harmonium*: the cabal can recruit **one** out-of-faction cog
    affiliated with the Axis, of rank 1 or 2.
  - `RECRUIT_WEARER` — *Asservisseur mental*: the equipped cog, of rank 1 or 2, can be recruited
    out of faction (in Les Autres).
  - `ALL_MINISTRIES` — no card at the moment (*Passe-droit du Census* carried it before its
    update): the equipped cog counts as affiliated with every Ministry (available
    equipment/boons, recruitment). It does not make its own card available: a cog of that
    card's Ministry is still needed.

  The out-of-faction cogs recruitable this way appear in the list with a "via …" mention. The
  details of a chosen cog give its factions (logo and name), then those where a card allows
  recruiting it ("Via a card"); in the choice list, the logos of the factions it is a member of.
- **Choices already taken**:
  - Out-of-faction cog whose recruitment card already covers another cog (`CanJoin`,
    `CogConflict`): marked 🔃 "replaces …". Choosing it removes the other cog from the cabal
    (swap); greyed out only if no removal is enough. The faction list greys out those where the
    chosen cogs do not fit **together** (two Axis cogs for a single Harmonium).
  - Out-of-faction cog chosen without a carried card covering it: its recruitment card is
    equipped on it automatically (`RecruitCard`: Harmonium, Asservisseur mental), and removed
    from the cog that carried it without benefiting from it; a note recalls it under its
    equipment list.
  - Equipment/boon already carried, or of a Ministry whose cogs each already unlock a piece of
    equipment (`EquipmentConflict`): marked 🔃 in the list. Choosing it makes the **swap**: the
    conflicting card is removed from the other cog (tooltip, then a reminder under the list).
    The ‹ › arrows skip these options.
  - Each Ministry category of the list shows how many more the cabal can take
    (`MinistryRemaining`), the cog's card set aside.

  Nothing is blamed on a choice if the cabal is already illegal without it.
- **Medic Ruzicka** ("at the start of the first cycle", `extraEquipment` in the catalog): the
  extra Integrity equipment is chosen in play mode, not in the composition.

## Search

The search field combines free words (name or title, ignoring accents and case) and cumulative
conditions on the stats: three-letter code or name of a stat, in any language (`tec`, `cbt`,
`tir`, `esq`, `mel`, `rng`, `dod`, `combat`, `ranged`…), followed by `>`, `<`, `>=`, `<=` or
`=` and a number, for example `kantor tir>4 esq<=3` or `rng>4 dod<=3`.

## Equipment and boon catalog

Under the builder, or in a detached window (`/builder/equipment?window=true`) to keep alongside
(`Shared/EquipmentPanel.razor`): filter by Ministry or faction, search in names and effects,
sort by Ministry or by name. **"Equippable only"** keeps only what at least one cog of the
current cabal can carry (`EquipableByCabal`: faction, Ministries present, cards of the chosen
cogs). The detached window is another instance of the app: the builder publishes the cabal in
localStorage (`CabalDraft.ShareKey`), and it follows its changes through the `storage` event.

## Export

Under the legality panel, with the cabal's name (optional for the export); copy to the
clipboard or download (`<cabal-name>.txt` / `.json`). The export reflects the current
composition, even incomplete (a warning says so).

- **Text** — format of the [bwaaaanegamesdb.fr](https://bwaaaanegamesdb.fr/builder_olomoc/index_builder_olomoc.php)
  builder, checked to be identical: cabal name (line omitted if it is empty), then one line per
  cog from rank 1 to rank 3, `COG NAME - EQUIPMENT/BOON NAME` (name alone if it is not
  equipped), in uppercase without accents, lines separated by CRLF.

  ```text
  Ma Cabale Test
  DOMINIK KRALOV - HARMONIUM
  HYNEK NOVOTNY - CHRONOGRAPHE
  PETRA KANTOR - ASSERVISSEUR MENTAL
  ```

- **JSON** — schema `schemas/olomoc-cabal.schema.json`: catalog ids and codes (can be
  imported again), names in the current language (readable), optional fields omitted.

  ```json
  { "format": "olomoc-cabal", "version": 1, "language": "fr",
    "name": "Ma Cabale Test",
    "faction": { "code": "MAISEL", "name": "Ligue de Maisel" },
    "cogs": [ { "rank": 1, "cog": { "id": "3R05", "name": "Dominik Kralov" },
                "equipment": { "id": "E11", "name": "Harmonium" } }, … ] }
  ```

- **Cards (PDF / PNG)** — card sheet of the cabal, opened in a new tab
  (`/builder/cards?c=3R05.E11,2R04,1R01.E38&name=…`: the cabal goes in the address, the link
  can be shared; `c` gives ranks 1, 2, 3). One landscape A4 page, cards at actual size
  (89 × 63 mm) centered: rank 3 to 1 from left to right as in the builder, each cog above its
  equipment/boon, 4 mm between cogs, 1.5 mm between a cog and its equipment
  (`Services/CardSheet.cs`). "Print / PDF" goes through the browser's printing ("Save as PDF",
  100% scale); "Download PNG" produces the same page at 2339 × 1654 px (200 dpi).

## Saved cabals

"Save", next to the cabal's name ("Save and export" section), keeps the composition on the
device (`localStorage`, `olomoc.cabals` key), in the JSON export format. The name is required
and identifies the cabal: saving under a name already taken replaces it, after confirmation.
The button shows "Saved ✓" as long as the displayed composition is the one saved under that
name.

"My cabals", at the top of the builder, lists the saved cabals (most recent first): load,
delete, or **import** a JSON export (from another device, for example; the imported cabal is
loaded, to be saved afterwards). Loading over an unsaved composition asks for confirmation.
Cards are found again by their ids, each cog in the slot of its rank; a card missing from the
current catalog is set aside and reported under the recorded name.

**With an account**, the same cabals on every device (`CabalLibrary`,
`POST /api/account/cabals/sync`):

- each device keeps its copy (`olomoc.cabals`): everything works offline;
- signed in, each save or deletion is sent at once; without network, it waits for the next load
  ("N changes not sent yet" in "My cabals");
- for the same name (case-insensitive), **the most recent wins**, deletion included: the
  account keeps track of deletions, an old copy does not cancel them;
- **first sign-in** of a device: its cabals go to the account ("N cabals of this device added
  to your account"); **another account** afterwards on the same device: only the cabals made
  here without an account follow it, the others make way for its own (`olomoc.cabals.account`:
  account of the copy);
- signed out, the copy stays on the device;
- 200 cabals at most per account (beyond that, the cabal stays on the device, reported); upload
  in batches of 10; deleting the account erases its cabals;
- **public or private**, per cabal ("Public" in "My cabals", `PUT /api/account/cabals/visibility`):
  a public cabal is offered to whoever types the account's username while entering a game of the
  Block (`GET /api/accounts/{name}/cabals`). Signed in, a "Public" box next to "Save" (checked by
  default) sets it when saving (sent with the cabal; on the composition already saved, changed at
  once). Without that choice, new cabals are public; those saved before this
  setting existed stay private.

## Play mode

From the builder, "Start a game" opens `/play` with the current cabal (three cogs required; an
illegal cabal is accepted, with a warning). Designed for a phone or a tablet, usable on
desktop. A single game per device, saved in `localStorage` (`olomoc.game` key) after each
change; "Undo" goes back through the session's history (100 steps, not saved).

Rules applied (`Services/GameSession.cs`):

- **Life line** (rulebook p.16): wounds marked from left to right. From an attack, one wound
  fewer if at least one reinforced HP is left to mark (except wounds that ignore protection),
  and stop at the first tainted HP marked. Every tainted HP marked gives a Darkness token
  (p.17), attack or not. Healing = unmarking the last marked HP (the rightmost ones); tapping an
  HP marks or unmarks it alone (correction, or an ability that targets a specific HP, like
  Jakub Myasnik marking a tainted HP of his target); a tainted HP marked this way also gives a
  Darkness token (unmarking it does not remove it); the following wounds skip the HP already
  marked.
- **Extra card** (Medic Ruzicka, `extraEquipment`): chosen on the cog's card during the first
  cycle, among the Integrity equipment the cabal does not carry yet; its abilities are tracked
  like the others.
- **Equipment that changes the life line**: `lifeLinePrefix` in the catalog (Carapace: two
  reinforced HP at the start). "Reinforced HP" switch (standard HP reinforced until the end of
  the cycle, e.g. Anabolisant).
- **Darkness and Bile** (p.17): Sane (0), Tainted (1-2), Corrupted (3) status; a token beyond
  3 goes to the Bile; Bile at 3 = game lost.
- **Action tokens** (p.33): 4 per cog and per cycle; **stunned** (p.18): forbids active
  abilities.
- **Tracked abilities**: those with a cost or a limit per game (circle). "Use" pays the action
  tokens, gives the Darkness tokens and counts the use per game. Limits per planning or per
  cycle are not tracked.
- **Hunt** (p.16, p.26): +1 when one of your cogs is taken out of action (a correction that
  puts it back in play removes that token); enemy cogs are added by hand. At 3, the game ends.
  **Victory points**: manual counter.
- **End of the cycle** (p.35): +1 Bile per corrupted cog still in play, end of the temporary
  reinforced HP, 4 action tokens; game over after cycle 4.

Everything else (card effects, opponent) is done by hand with the generic actions. Wounds and
healing open in an overlay window and their report is shown as a notification, with an "Undo"
button: the cards' buttons never move. Cycle, Bile, hunt and victory points are in the top bar
(fixed on screen, except on phones).

## Accounts

Optional: without an account, everything works as before, on the device. `/builder/account`
(the menu's "Sign in" link, the username once signed in, everywhere on the site):

- **Creation**: username (3 to 24 characters: letters without accents, digits, `-`, `_`;
  unique, case-insensitive), email, password (8 characters at least, no composition rule).
  Sign-in by username or by email; a session of at least 3 months per device (`olomoc_session`
  cookie: HttpOnly, SameSite=Strict, Secure over HTTPS), renewed for 3 months on a visit past
  halfway.
- **Email**: only used to recover the account. Confirmation link (2 days) at registration and on
  each address change; **forgotten password** only for a confirmed address (one-hour,
  single-use link; same answer whether the account exists or not). At most 3 emails per hour and
  per account. Links to `/builder/account/confirm` and `/reset`, account and token in the
  fragment (removed from the address as soon as they are read); base address: `PUBLIC_URL`
  (see [Deployment](#generic-any-instance)).
- **Security**: ASP.NET Core Identity (PBKDF2, 15-minute lockout after 5 failures). Changing
  the password, resetting it or "Sign out other devices" closes the other sessions (security
  stamp re-read every minute). Cookie encryption keys in the database: a redeployment signs
  nobody out.
- **Roles**, stored in the database (Identity roles):
  - **player** (default): the whole site, except the editor;
  - **editor**: in addition, the card catalog editor (and its drafts), the zones of the Block and
    the data export (see "Statistics");
  - **admin**: everything, including the **admin panel** (see below). An admin never changes
    their own role: there is always one left. A role change applies at once for the API, within
    a minute for the served drafts.
  - **First admin**: create your account, then restart with your username in `OLOMOC_ADMINS`
    (only existing accounts receive it). The variable grants the role at each start without
    ever removing it: empty it afterwards, or keep it as a fallback.
- **Cabals**: the same on every device (see "Saved cabals").
- Account **deletion** (password asked again) and **Personal data** page (`/builder/privacy`).

### Admin panel (admins)

`/builder/control` ("Admin" tab on desktop, link in "My account"):
- **overview**: accounts (confirmed, editors, admins, new and signed in over 30 days, locked),
  saved cabals, events by phase, online games, statistics archives, changed catalog
  entries, database size, last backup, email sending;
- **accounts** (search by username, editors and admins, or newest; never the email): role,
  temporary password (player without a confirmed email), unlock, sign out everywhere, deletion
  with its cabals (not one's own account, nor an admin);
- everyone's **events**: new admin link (the old one stops working; for an organizer who
  lost theirs), include in or exclude from the stats, deletion. An event's admin page stays
  with its organizer;
- **online games**: list and deletion;
- **statistics**: "Export the data" (the editors' export, see "Data export") next to
  "Reset ALL stats", which asks to confirm that the current data was exported (a box to tick),
  then resets every statistic (see "Reset of the statistics"); cancellable;
- **maintenance**: backup now (replaces today's), purges of events and games with the
  background tasks' rules;
- **log** of the admins' actions (`AdminActions` table, kept for a year), last 100 shown.

Enabling emails, once (generic): an account with an SMTP relay (Brevo, for example: free up to
300 emails per day), SPF, DKIM and DMARC DNS records for your domain (given by the relay), then
`SMTP_HOST`, `SMTP_USER`, `SMTP_PASSWORD` and `MAIL_FROM` in the `.env`. Brevo disables an SMTP
key unused for 90 days: `MAIL_CHECK_TO` (the admin's address) sends a short check email every 60
days, which keeps it active and shows that sending works. (orloj.top uses Brevo, with the DNS
records of `orloj.top`.)

## Events (tournaments and Dystopian Encounters)

The **Block** holds **zones**; games are recorded in a zone on their own (see "The Block") or
through an **event** bound to it. An event is a **tournament** (Swiss rounds) or a **Dystopian
Encounter** (random pairings every round), chosen at creation (`EventFormat`). In the code, the
API and the storage, an event is still a `Tournament` (`/api/tournaments`, `/pairings/`).

`/pairings/`: the organizer creates an event (name, date, format, number of rounds, 1 or 2 cabals
per player, standings hidden or not) and receives a 6-character **code** to give to the players, along
with an **admin link**. No account: each event is a JSON document in the server's
database; tokens and password are only kept hashed. The organizer can **delete** their
event (at the bottom of the administration, by typing the code again). Otherwise, an
event is deleted automatically after 90 days without changes, if its date has passed or
if it has none; an event dated today or later **never** is (daily check).

- **Administering from several devices** (create the event on a PC, run it on a phone on
  the day), two keys to choose from:
  - the **admin link** (secret token in the address), highlighted right after creation with its
    **QR code**: scan it with the phone, or copy it. It stays in "Share" (QR on demand, so as
    not to show it by mistake);
  - an optional **password** (8 characters at least), chosen at creation or later in "Organizer
    access": on a device without the link, the admin page asks for it and opens a session
    specific to that device. Changing or removing it closes the other sessions (not the one of
    the device changing it; the link stays valid). Hashed on the server (PBKDF2), 10 attempts
    per quarter of an hour and per address.
  The public link also has its QR code, to show on site for registrations.
- **Registrations**: name, faction, cabals chosen in "My cabals" (builder saves on the device,
  same site), composed on the spot ("Compose…" opens the builder in a new tab, on the chosen
  faction; the saved cabal appears in the list when coming back to the tab, without losing what
  was typed) or **pasted** ("Paste…"): a list in the export's text format (one line per cog,
  "COG - EQUIPMENT/BOON", optional name on the first line; bwaaaanegamesdb builder format too),
  typed by hand or copied, regardless of case, accents or dash, or a JSON export
  (`Services/CabalImport.cs`). Immediate preview (recognized cards, unknown lines, legality); a
  text valid in several factions makes the player choose theirs. Shared faction and legality
  checked in the browser, then by the server with the same rules as the builder. Everyone can
  change or withdraw their registration (player token, kept on their device) while
  registrations are open. The others' lists stay hidden until registrations close, which
  reveals them to all. The organizer can also **register a player themselves** (player without
  a phone, lists received in advance), with the same form from the administration, and change
  or withdraw any registration; the token of a player they register is not kept, their device
  stays the organizer's.
- **Pairings** (`Tournaments/Pairer.cs`), between the active players (drops excluded): in a
  *tournament*, true Swiss rounds: at random among the players with the same points (round 1:
  everyone), from the highest points group down, a player left over meeting the closest points;
  **no rematch**, mirror matches allowed. In a *Dystopian Encounter*, at random every round,
  whatever the points, with **no rematch** and **no mirror match** (two players of the same
  faction). If that is impossible (backtracking search), the mirror match is allowed first, then
  the rematch, and the organizer is told how many. Odd number: a bye to a player
  who has not had one, the lowest-ranked (tournament) or drawn at random (Encounter). The
  factions' alignments (Light / Darkness) are no longer a criterion, anywhere: kept in the card
  catalog only.
- **Number of rounds**: chosen, or **Auto** (default): the smallest n with 2ⁿ ≥ players (2: 1
  round, 3-4: 2, 5-8: 3, 9-16: 4…), following the registrations, then fixed at the start
  (`TournamentRules.AutoRoundCount`); scenarios planned beyond it are dropped.
- **Hidden standings** (by default for a Dystopian Encounter, not for a tournament; changed at
  any time in the administration): the players and the visitors do not get them from the API,
  the organizer still sees them.
- **Results and standings** (official round sheet): the organizer enters each table's VP;
  points 3/1/0 (bye: a 7-0 win, i.e. 3 points, +7), standings by points, then
  cumulative VP difference, then VP scored. Players who dropped go to the end of the standings,
  behind all those still in, and are ranked among themselves by the same criteria. The next
  round can be paired when the previous one is complete; the last round can be undone as long
  as no result is entered in it. Pages refresh every 30 seconds.
- **Cabal played** (optional, for the statistics): with two cabals per player, the organizer can
  say when entering the VP which one each player played ("C1" / "C2" under each name, a second
  click removes the choice; the cabal's name in the tooltip). If unknown, the game counts for
  the wins per faction, not for those per rank 3 cog.
- **P1 and end of the game** (optional, for the statistics): when entering the VP, "J1" (P1)
  under the player who started, and the cause of the end (Bile, 4th cycle, PoliSec); shown in
  the tables.
- **Scenarios** (`Tournaments/Agendas.cs`): the organizer plans each round's scenario in
  advance, among the 10 agendas of the official collection (Occicat, January 2026; English
  version of September 2026), or "Other" for a custom scenario to name. Random draw possible:
  official agendas, without repeats, for the rounds not paired yet (custom scenarios and paired
  rounds kept). For each round, optional Artifact (Rogue, Parasite) and Ministry
  (Disinformation, Indoctrination) decks, also drawn at random (custom scenarios included).
  Schedule visible on the public page, scenario and decks next to each round's title.
- **Correcting a pairing**: ⇄ on two players of the current round (tables without a result,
  bye included) swaps them, after confirmation; a rematch created is reported.
- **Safeguards**: 128 players per event, requests of 64 KB at most, 10 event creations
  per hour, 10 password attempts per quarter of an hour and 120 writes per minute per IP address
  (`RATE_CREATE_PER_HOUR`, `RATE_LOGINS_PER_15_MINUTES`, `RATE_WRITES_PER_MINUTE`).

## Online games

`/online/`: a two-player game, synchronized live (SignalR, WebSocket). The **server is the
authority**: each player sends commands, which `Online/OnlineRules.cs` checks according to the
step and the seat; each one only receives **their** view (`OnlineRules.View`). After each change,
the room receives "Changed" and each client reads its view again: nothing secret goes to another
connection.

- **One current game per device**, local or online (`GameStore`: `olomoc.game`, or the online
  game's code in `olomoc.game.online`). The **Play** tab shows the local game, or leads to the
  current online game; "Leave the game" removes it from there (it stays on the server and in
  "My online games"). Starting a game from the builder replaces the current game (after
  confirmation).
- **Access** without an account, from the **Play** tab (the old `/online/` address redirects
  there):
  - **opening a local game online**: "Open online" (game bar) creates a lobby with its cabal
    (rebuilt from the game: `CabalExporter.FromGame`), checked by the server and **ready**
    right away; the online game starts from the beginning (player numbers, agenda) and replaces
    the local game. An illegal cabal is refused (message);
  - **free game**: without a game in progress, the "Play online" box; "Create a lobby" gives the
    creator's link and an **invitation link** (token of the second seat, in the `#…` fragment,
    kept on the device then removed from the address), cabal chosen in the lobby. A guest who
    joins without a game in progress on their device makes it their current game;
  - **player name**: the account's if signed in, otherwise the last one used on the device
    (`olomoc.online.name`), otherwise random ("Joueur 4821"); a guest opening their link is
    named the same way, and can change name in the lobby;
  - **event table**: "Play online" on one's table of the current round (event
    started). The game is created on the first call then found again. Each player identifies
    with their event player token and chooses one of their registered cabals, which is
    recorded on the table at the start. The table shows an "online" link to follow it;
  - **spectators**: the link without a token follows the game without being able to play.
- **Guided steps** (`OnlineStep`):
  - **lobby**: name (sent when leaving the field or with "Ready", without validating it; can be
    changed throughout the lobby, even once ready), cabal ("My cabals", compose, paste), "Ready"
    as soon as there is a name and a cabal. The opponent's cabal stays secret until both are
    ready (rulebook p.32);
  - **player numbers**: 5 D6 rolled by the server, success on 5+, the most successes becomes P1,
    tie: roll again (rulebook p.31). After the second roll, both rolls and the verdict stay on
    screen for 2 s before the next step (`SetupPanel`, read back from the log);
  - **agenda**: chosen or drawn at random by P1; the draw is offered, to confirm or redraw
    (nothing is sent before "Confirm");
  - **cycles**: each player tracks their side with the play mode cards (`PlayCogCard`, read-only
    for the opponent's side, which can be hidden), in the **Cabales** panel under the table
    (`Shared/CampsPanel.razor`, with undo and end of the cycle). The panel can be **detached** into its
    own window (`Pages/OnlineCamps.razor`, `/online/g/{code}/camps?window=true`, another screen):
    the player's side stays editable there. Every window tracks the player's side the same way
    (`Services/MySide.cs`: changes sent from the last revision, the server's view taken back when it
    refuses one). The end of the cycle applies to both sides once both confirmed it; the game ends
    after the 4th cycle, or as soon as a Bile (specific to each player) or the hunt reaches 3.
  - **« Recommencer la partie »** (game's header, a player, after a confirmation; `Restart`): back
    to the lobby, names and cabals kept but nobody ready; the initiative, the scenario, the mat, the
    cards and the sheets are cleared. Not once a result is confirmed (it is in the statistics).
- **Shared hunt**: a single level for both players, shown in the game's header. Each side keeps
  its share (its cogs taken out of action, counted automatically, and the tokens added by hand);
  the hunt is their sum (`OnlineRules.Hunt`), 3 at most. This way a player's undo removes their
  own tokens, and two simultaneous uploads do not get in each other's way.
- **Tracking a side**: as on a real table, the player has control over their sheet. They send
  the state of their side with the revision it starts from (`UpdateState`): the server refuses
  an outdated state (the client reloads), or an inconsistent one (other cards, a life line that
  does not follow from the cards, counters out of bounds). It enforces the shared cycle. Undo:
  the last 30 states of one's side, until the end of the cycle.
- **Table** (`Shared/TablePanel.razor`, `Online/TableRules.cs`), from the initiative on, played
  freely as on Vassal: the server keeps and relays the positions, it does not check the movements.
  - Created when both players are ready, empty: every cog is off the Block. Mat chosen among those of
    the manifest (default "Bloc (Mez)").
  - **Either player moves any piece**, as on Vassal: both sides' cogs, the equipment/boon tokens (a
    card of either cabal, colored by its side), terrain, markers and templates; spectators watch. Only
    checks: the piece exists, the position stays around the mat (3 cm), 80 pieces at most. The last
    move wins, piece by piece; no log line per move (terrain put down or removed: logged).
  - Gestures in `js/table.js` (SVG in mm): drag a piece (the server hears of the drop only), zoom
    (wheel, buttons), pan (drag the background); from the list or the palettes, a click on the item then on the mat.
    A cog out of action on its sheet is faded. Bases have no orientation (no rotation); the selected terrain
    turns with its handles (one at each end, like the movement template), or by 15°.
  - **Scenario**, in the game's header next to its code (`Shared/ScenarioBox.razor`): the agenda
    chosen at the setup, the Artifact and Ministry decks of the game (chosen or « Tirer les decks » by
    P1; drawn with the scenario when it is drawn), a note on its setup, and then, by P1,
    **« Placer + distribuer »** (`SetUp`): the ministries dealt, the Artefact pile built (Indra's cards
    taken), and its
    elements put down on the mat (none for Visites, Sabotage, Cartographe) at the positions of the Recueil des agendas (`Online/AgendaSetups.cs`, inches
    from the centre; a choice where the agenda leaves one: which median, which diagonal…), as
    ordinary pieces (movable, lockable) with their name and number (doors, accumulators), the
    batteries in their player's color, the zones de Fiel under the secrets of « Seuls dans le noir ».
    **La Gargoule is not offered online** (`OnlineRules.OnlineAgendas`: its 7 activation cards
    replacing the ministries are not played).
  - **Decks** (`Online/DeckRules.cs`, `Online/CardDecks.cs`: the cards as printed, rank, planning
    constraint, artefact number, and the effects of the ministries and the artefact cards, read on
    the images of the module V1.9, in the hover texts): **ministries** dealt (1 Axe card each, 3
    random ministries each, the hands known to both; the last one face up: the fallen ministry); the **Artefact**, beside the Block (see the wide layout): a pile (secret order)
    built at its first « Mécanisme » (Visites: the 3 Rogue cards on top), « Mécanisme » draws its top
    card (upright as in the module: its logo shows a corner or an edge of the Block; with the
    priority its number gives; the previous one to the discard; an empty pile: the discard shuffled
    again), « Remélanger »; the **hostiles' profile cards** (front, back: instinct; bigger when
    hovered) of the deck (and the Gargoyle with its agenda); **Indra**: at the setup (« Placer +
    distribuer »), each one, in either cabal, receives the pile's 3 last cards (secret; P1's first),
    shown in her player's hand (« Planification » panel), who may swap the drawn card for one of
    them (« Remplacer »). **Once the ministries are dealt, the decks no longer change**; before,
    another Artefact deck starts its pile again.
  - **"Planification" panel** (`Shared/OffBlockPanel.razor`, under the table, detachable:
    `/online/g/{code}/offblock?window=true`; `Online/PlanningRules.cs`, semi-automatic, rulebook
    p.33): on the left the opponent's ministries, stacked by pairs (their text and planning constraint
    left visible; a ministry played or revealed greyed); then the player's hand (the activation cards
    of their cogs, their propaganda cards: 1 plus 1 per cog out of action at the end of an earlier
    cycle, its activation card replaced; a cog out of action during the cycle keeps it until then;
    their ministries; their Indra cards, with « Remplacer »); then a row of 5 slots per player. Each
    player drags their cards onto their slots, in secret, saved
    as they go (one ministry, on the slot of its planning constraint; checked by the server). Once
    both plannings are complete, « Révéler la phase » shows the next card of each (the opponent's
    still hidden ones as card backs), with their ranks and the active player (equal: « Mécanisme,
    puis la priorité »); the rest is up to the players. At the end of the cycle the planning starts
    again and the ministry played is discarded. Activation and propaganda cards from the Vassal module.
  - **Wide layout** (online games only: the page takes the whole width): above the mat, the two dice
    rollers and the **actions of the selected piece** (ranges, padlock, objective colors, magnet,
    rotation, removal); on the far left, **beside the Block**: the
    Artefact (its card drawn with its priority and its effect in the hover text, « Mécanisme »
    and « Remélanger », the discard, how many Indra cards the opponent holds, the hostiles' profiles) and the fallen ministry, once the scenario has its decks; then the **opponent on the left** of the mat, the **player on the right** with
    the Décors / Marqueurs / Réglettes palettes under their cabal (a spectator: seat 1 left, seat 2
    right). Above each cabal, the player's **state** (`Shared/SideStatus.razor`): presence, name,
    P1, Fiel and VP (a line each, both frames the same height), on the player's own side the Fiel
    tokens clickable and the VP with +/-.
  - **Both cabals**, always listed next to the mat: a card per cog with its stats, its life line (HP as pips: standard, reinforced, tainted; marked ones crossed), action tokens,
    Darkness and state (out of action, otherwise sane, tainted or corrupted; stunned; **off the Block**
    when none of its tokens is on the mat), read from its side's sheet, and its cards below (a click
    puts their token down). On the player's own cabal, a pip marks or unmarks that HP (damage by
    hand) and a token sets the action tokens or the Darkness, as on the sheet (sent the same way).
    A cog is put down as **tokens, as many as wanted** (a copy marks a place):
    the card is linked to all of them. A click on the card selects them in turn (the others outlined),
    or puts one down if there is none; "＋" puts one more. Selecting a token lights its card.
  - **Padlock** of the terrain and the objectives (shared): locked, the piece is neither moved, turned
    nor removed (server-side too) until unlocked; its padlock shows when it is selected.
  - **Objectives**: the palette offers only the objective marker (yellow ring); a toggle per player
    marks it as taken by that side (its color; both: split diagonally), to track who took what.
  - These toggles (padlock, colors) are **on the selected piece itself**, to its right, like the
    rotation handles, and also in the line under the mat.
  - **Movement templates** (from the module: the ruler of a normal move, a stadium, and the push one,
    a disc), put down, moved and turned by either player; the selected one turns with its handles (one
    at each end, drawn above the bases so that a cog never hides them).
    **Magnetic** (by default; option of the selected template, shared): dropped near a base (within 8 mm between
    the edges), the template moves without turning until its closest point touches it; a base dropped
    near a magnetic template comes to touch it at that place. Shown while dragging (`js/table.js`:
    a template is a capsule, its contact with a round base is exact). Placement stays free otherwise.
  - **Keyboard** (outside the text fields), on the selected piece: arrows move it by 0.1″ (Shift:
    1″; sent once the keys stop for a moment), Ctrl+arrows turn terrain and templates by 5° (Shift:
    15°), 1 to 9 toggle its ranges (0 removes them), Ctrl+C then Ctrl+V paste a copy half an inch
    away (each paste further; the copy gets selected; a cog's token too), Ctrl+X or Delete remove it. Without a selected piece, the browser keeps its keys.
  - **Ruler** (for the viewer only): distance in inches; as soon as a miniature is involved (start or
    end), from the edge of its base (between two: edge to edge), while dragging too. **Ranges**, as on Vassal:
    keys 1 to 9 of the top row (or the buttons) toggle a circle of that many inches around the selected
    piece, from the edge of its base, 0 removes them; kept by the server, seen by both players and the
    spectators (either player, any piece but terrain). 1″ grid on demand.
  - **Dice** (`Online/DiceRules.cs`, `Shared/DicePanel.razor`, above the mat): two rollers, the **action** and under it the **reaction** that answers it (dodge, riposte),
    open to both players. 1 to 12 D6 rolled by the server (the same for both, in the log); the
    successes lit at the chosen threshold and counted for each one beside the dice (the frame drawn
    before the first roll too, so nothing moves; 5+ by default, rulebook p.10;
    4+, 3+, 2+ with support or a charge), the threshold changed by either player for both;
    "Reroll the failures": the dice under the chosen threshold again, the successes kept (trying
    fate; the Darkness token is added on the sheet by the player); a whole new roll is "Roll" again.
  - **Full screen** ("⛶" in its header): the table panel alone on the screen (browser full screen,
    Esc leaves it), the mat as large as the height allows, the cabals' list scrolling beside it.
  - **Detachable** like the builder's equipment catalog: `/online/g/{code}/table?window=true`
    (`Pages/OnlineTable.razor`), for another screen. Unlike the opponent's side window, it can be
    played: it joins with the seat's token kept on the device, on its own connection.
- **Result** (`ConfirmResult`, `Shared/ResultBox.razor`): once the game is over (4th cycle ended,
  a Bile or the hunt at 3), either player confirms the VP of both sides (read from the sheets,
  corrected if needed); the end is derived (Bile, otherwise PoliSec, otherwise 4th cycle). Confirming
  again replaces it. Then (`Online/OnlineResults.cs`):
  - **free game**: a duel (cabals without their name, VP, P1, end, agenda as the scenario; no
    names) in the zone of the Block that **receives the online games** (kind Online, a single one,
    chosen by the editors), so in the Block's statistics, "All", the zone's dominance and the data
    export (`source = online`). One row per online game (`ZoneGames.OnlineCode`), updated by a new
    confirmation; not editable from the zone. No open receiving zone: nothing recorded, tried again
    at the next confirmation or at the purge;
  - **event game**: VP, P1 and end written on the event's table, only if it has no result yet (the
    organizer's stays); it then counts through the event's archive, never twice. Once written there,
    the game no longer takes a correction: the organizer corrects the table.
  - A finished game nobody confirmed is recorded from the sheets when the purge deletes it (also
    the admin panel's purge by hand). An abandoned game (not over) never is.
- **Log**: arrivals, dice, agenda, card reports, ends of cycle, terrain, result.
- **Storage**: one document per game in the database (`Documents` table, kind `online`),
  deleted after 7 days without activity. Presence (connected seat) is kept in memory. Automatic
  reconnection: the client joins the game again and reads its view again. Leaving the game page
  (to another tab of the site) closes the connection: the player is no longer counted as
  present.
- **Safeguards**: 2,000 games, messages of 32 KB at most, 20 calls per second and per
  connection, creations limited like events (`RATE_CREATE_PER_HOUR`).

Planned next steps: the cards (Ministries, Artifact, planning revealed simultaneously), a ruler
and a line of sight shared with the opponent (the terrain outlines are in the manifest), dice
linked to the cogs (characteristic, modifiers) and an opposed attack / reaction flow.

## Statistics

`/stats/` ("Statistics" tab, `Pages/StatsPage.razor`), public, with a filter:

- **All** (default, `/api/stats/all`): the finished events and the games of the Block
  (visible zones) together. A game of an event bound to a zone is copied into the
  zone: it counts through the zone while the event is in progress, then through the
  event's archive once it is finished (the archive and the copy share the event's
  code), never twice. Factions: registered players and participations; cog choices: registered
  cabals and cabals played.
- **Events** (`?source=tournaments`, `/api/stats`): the events only, as below.
- **Block** (`?source=bloc`, `/api/zones/stats`): all the visible zones (see
  below).

`/pairings/stats` and `/bloc/stats` redirect there with the matching filter.

A **period** narrows any of them (`?from=&to=`, `yyyy-MM-dd`, both optional and included; the same
query on the three APIs, `Tournaments/Stats/StatsPeriod.cs`): two date fields and shortcuts (all
dates, last 12 months, this year, last year), kept when the filter changes, in the address
(shareable). An event counts on its date (otherwise the day it finished), a zone game on the day it
was played. Computed on demand (the figures without a period stay in memory).

The events counted are the **finished** ones — except those their organizer excludes ("Do
not count in the statistics", at creation or at any time in the administration):

- **factions played**: share of the players;
- **cog choices**, per faction (selector) and per rank: share of the faction's registered
  cabals that contain the cog (both of a player's cabals count);
- **wins per faction** and **per rank 3 cog** of the cabal played: wins / games, each side of a
  game counting (mirror match: on both sides; byes excluded), with W/D/L and the number of
  games; faded below 10 games;
- on the games where the organizer recorded them: **P1 wins**, **game endings** (Bile, 4th
  cycle, PoliSec), **scenarios played** (with P1 wins per scenario; custom scenarios grouped,
  without their name), **Artifact and Ministry decks played** (likewise; v3 archives).

At the end of an event, an **anonymous archive** (factions, cabals as ids, games; neither
names nor tokens) is stored in the database ("stats" document with the event's code),
which the event purge does not touch: the statistics cover the whole history. They are
computed in memory (`Tournaments/Stats/TournamentStats.cs`) and computed again for each archive
added or removed (`GET /api/stats`). Deleting an event, or excluding it, removes its
archive; at startup, a finished event without an archive gets one.

The computation shared with the zones is in `Tournaments/Stats/GameStats.cs` (games in a neutral
form: a side is one player or a team of two).

### Reset of the statistics (admins)

"Reset ALL stats" in the admin panel (`POST /api/admin/stats/reset`, logged): from that moment,
only what is recorded afterwards counts, everywhere: events finished afterwards, zone games
entered afterwards (`/stats/`, each zone's statistics and **dominance**). **Nothing is deleted**:
the archives and the zones' games stay (the zones list them), the export keeps everything (its
`counted` column follows the reset), and "Cancel the reset" (`DELETE /api/admin/stats/reset`)
makes everything count again. The moment is kept in the site settings (`DATA_DIR/settings.json`,
`StatsSince`, outside the database backup); the statistics pages say "Statistics reset on …".

### Data export (editors and admins)

On `/stats/`, editors and admins get an "Export the data" button (`GET /api/admin/export`,
`Admin/DataExport.cs`, logged): a ZIP to compute the statistics again or make other figures.
It holds every game of the archives (finished and counted events) and of every zone, hidden ones
included, anonymous like the archives (no player names, no comments, no tokens):

- `data.json`: everything, structured (events, zones, games with their sides, players and cabals);
- CSV tables for a spreadsheet (UTF-8 with BOM, `;` separator, dates `yyyy-MM-dd`, booleans `1`/`0`):
  `tournaments.csv`, `zones.csv`, `games.csv` (one row per game), `players.csv` (one row per
  player of a game: faction, VP for and against, result, P1, opposing factions, rank 3 cog) and
  `cards.csv` (one row per card of a cabal played, with its result).

Ids: `T-{event}-{n}` for an event's game, `Z-{id}` for a zone's. `source`: `tournament`, `zone`, or `online`
(a zone game recorded from an online game, its code in `copied_from`). Card ids are the catalog's, with
their names in the reference language. An event's players are numbered (registration order);
a zone's are not followed from one game to the next. The `counted` column marks the games counted
in "All": a zone game copied from an archived event (`copied_from`) is counted in the event,
hidden zones never count, and after a reset of the statistics only what was recorded afterwards.

## The Block (zones)

`/bloc/` ("Block" tab; formerly `/zones/`, which redirects) replaces Occicat's "Rencontres dystopiques" form: games played
on their own or at an event, recorded in **zones** (a club, a convention, a period of the year, online),
with their statistics and their **dominance**. **Never a ranking of people**: player names are
optional, shown in the list of games, never counted.

- **Zones**: created by the **editors and admins** (`/bloc/new`): name, description (place, schedule…),
  kind, optional opening and closing dates, dominance mode, optional **managers** (accounts, by
  username), hidden (test zone: out of the list and of the all-zones statistics, reachable by its
  link). A zone is *upcoming* before its opening, *open*, then *closed* after its closing date or
  when a manager closes it by hand (reversible). Managers change the settings (not the managers,
  the mode or the visibility) and moderate the games; editors and admins can do everything on
  every zone (logged in the admin panel's log).
- **Online games**: a zone of kind Online can **receive the online games** (editors and admins;
  one at a time: ticking it on a zone unticks it on the others; changed to another kind, it stops).
  The results of the free online games are recorded there (see "Online games").
- **Entering a game** ("Record a game"): **anyone, without an account**, in an open zone.
  Duel (1 v 1) or **2 v 2** (any factions, VP per team). Every field is
  required (marked with an asterisk) except the names (optional, never counted): the faction and the cabal played of each
  player (from "My cabals", composed or pasted, legality checked; typing a name suggests account
  usernames, `GET /api/accounts/search`, and an account's username offers its public cabals), the VP of each side and
  who played first (each side starts with its VP and P1/P2 buttons: P1 on one side makes the other
  P2), the end of the game, the scenario and both its decks; optional too, a free
  comment (a notable moment, 500 characters at most, shown under the game), and a **photo** when the
  site allows them (admin panel, "Site settings", off by default: files stored on the server, in
  `DATA_DIR/photos/`, outside the database backup; JPEG/PNG/WebP checked from the first bytes,
  5 MB at most, reduced to 1600 px and re-encoded by the browser, which drops the location;
  chosen in the form of a new game, next to the comment, and sent once the game is recorded, or
  added, replaced or removed from the list by whoever can change the game; deleted with the game or the zone; turned
  off, the photos are hidden, not deleted); all the fields shown at
  once. Games copied from an event keep what it recorded. Checked by `Zones/ZoneRules.cs` in the browser, then again on
  the server: date within the zone's period and not in the future.
- **Changing or deleting a game**: the device that entered it (entry token in the localStorage,
  `olomoc.zones`), or the author's account on any device, while the zone is open; its managers
  and the admins, at any time. Entries limited per IP address (`RATE_ZONE_ENTRIES_PER_HOUR`,
  60 by default).
- **Statistics** of each zone, and of all the zones together: the same panels as the events
  (`Shared/StatsSections.razor`), each player counting (in 2 v 2, both players of the winning team
  win); factions counted on the participations, cog choices on the cabals played.
- **Dominance** (`Zones/ZoneDominance.cs`), mode chosen per zone: number of **wins**, **win rate**
  (at least 10 games for a faction) or total **VP**: the leading **faction**, among all of them;
  a tie at the top: no dominance. Computed live: an open zone is "Dominated by …", a closed one
  "Closed, dominated by …".
- **Map of the zones** (top of `/bloc/`, `Shared/ZoneMap.razor`): a city plan
  (`img/zones/map.jpg`) with one hexagon per zone (hidden ones left out), on the slots of a
  honeycomb laid over its districts (`Zones/ZoneMapLayout.cs`): the first 25 cover the city, the
  next ones widen the honeycomb ring by ring (one slot per possible zone). Each zone gets a slot
  once, the most central free one, and keeps it whatever changes: adding, editing, closing a zone
  never moves the others; a deleted zone frees its slot for the oldest zone without one. Too many
  zones for the plan: the whole honeycomb shrinks around the center so that all fit (hexagons
  smaller, never reordered); small ones show only the name and the emblem, tiny ones the emblem
  (the name stays in the tooltip). Slightly translucent.
  Narrow screens scroll the plan. Green: open (with the leading faction's emblem once there is one); hatched: upcoming;
  pale with a gold edge: closed and dominated, with the dominant faction's emblem; grey: closed without dominance (tie, no games). The detailed list
  stays below.
- **Logo**: managers and admins can give a zone a logo as a **link** to an image (https only,
  500 characters at most); the server stores the link, never the image. Shown on the map, the
  zone page and the list (`Shared/ZoneLogo.razor`, no Referer sent). The CSP accepts `https:`
  images for that.
- **My zones** (below the map): zones managed, and zones seen on this device.
- **Events bound to a zone**: when creating an event, an optional zone (not closed). Each
  result entered is copied into it as a duel (`Zones/TournamentZone.cs`, `TournamentZoneSync.cs`):
  factions, cabal played, P1, end, scenario, **without the names** (the zone keeps its games after
  the event is purged). A corrected or cleared result, an event excluded from the
  statistics or deleted: the zone follows. These games are changed through the event only; a
  closed zone is no longer changed.

Storage: zones are documents (kind `zone`, 500 at most, never purged); games are rows of the
`ZoneGames` table (JSON of the game, author's account forgotten when the account is deleted,
hash of the entry token; 10,000 per zone). Statistics computed in memory, again after each
change (`Zones/ZoneStats.cs`).

## Files

Overview: [docs/architecture.md](docs/architecture.md). Main files:

```
Olomoc.slnx
src/Olomoc.Shared/    shared Razor library: models, rules, card components,
                      static files served under /_content/Olomoc.Shared/ (Assets.Base)
src/Olomoc.Web/       the site's Blazor WebAssembly app (served at the root): router, startup,
                      layout; brings together the three page libraries below, without
                      reloading from one to another
src/Olomoc.Builder/   pages of the builder, play mode, account, editor and admin panel (/builder/…)
src/Olomoc.Pairings/  pages of the events and statistics (/pairings/…), and of the
                      Block (/bloc/…)
src/Olomoc.Online/    pages of the online games (/online/…)
src/Olomoc.Server/    ASP.NET Core host: app, shared files, API, online games hub
tests/Olomoc.Tests/   xUnit
```

**`src/Olomoc.Shared`**
- `Models/CatalogModels.cs` — neutral card catalog (`Cog`, `Equipment`, `Ministry`,
  `Faction`…), deserialized case-insensitively; enums `HealthPointType` (S/R/T), `Timing`,
  `EquipmentKind`, `BuildRuleType`.
- `Models/TextModels.cs` — language packs, `Localized` (text + fallback), and `LegalityIssue`
  (neutral legality messages: key + ids).
- `Services/LocalizationService.cs` — current language, labels (`Ui`), card texts with `fr`
  fallback, stat keywords of every language.
- `Services/CatalogService.cs` — loading + `AvailableCogs`, `AvailableEquipment`, `RecruitVia`,
  `CheckLegality` (including the bipartite matching).
- `Services/CogQuery.cs` — search parsing (words + conditions on the stats).
- `Services/CabalExporter.cs` — text (bwaaaanegamesdb.fr format) and JSON exports, reading the
  JSON back.
- `Services/CabalLibrary.cs` — cabals saved in the browser, synchronized with the account
  (`Accounts/CabalSyncModels.cs`); `CatalogService.Resolve` reads them back.
- `Models/GameModels.cs`, `Services/GameSession.cs`, `Services/GameStore.cs` — play mode:
  savable state, rules and undo, saving in the browser.
- `Online/` — online game (`OnlineModels.cs`), rules of the steps, commands and views
  (`OnlineRules.cs`), exchanges with the hub (`OnlineApiModels.cs`).
- `Services/CabalDraft.cs` — cabal being composed, kept while navigating.
- `Components/StatLine.razor`, `AbilityList.razor`, `GameEffect.razor`, `Pictogram.razor` —
  stats, abilities and rulebook pictograms; `Emblem.razor` (faction logo, Ministry emblem);
  `TokenTrack.razor` (token reserve), `Sheet.razor` (overlay window).
- `Components/PlayCogCard.razor` — a cog's card in a game (play mode, online games).
- `Components/CabalPicker.razor`, `CabalSummary.razor`, `LegalityStatus.razor`,
  `Problems.razor`, `FactionBadge.razor`, `ScenarioTag.razor` — choice and summary of a cabal,
  problems returned by the server (events, online games).
- `Components/AppNav.razor` — site menu: tabs under the header (desktop), bar fixed at the
  bottom (phone); builder, play (dot if a game is in progress; online games included),
  events, statistics, zones, editor (editors and admins), admin panel (admins), account.
  `Components/HeaderTools.razor` — language choice.
- `Accounts/AccountApiModels.cs` (rules and exchanges of the accounts API),
  `Accounts/AdminApiModels.cs` (admin panel), `Services/AccountService.cs` (the site's session).
- `Tournaments/` — event (`TournamentModels.cs`), standings (`Standings.cs`), pairings
  (`Pairer.cs`), rules of the phases and registrations (`TournamentRules.cs`), exchanges with
  the API (`TournamentApiModels.cs`); statistics (`Stats/`: `GameStats.cs`, `TournamentStats.cs`).
- `Zones/` — the Block: zone and game (`ZoneModels.cs`), rules (`ZoneRules.cs`),
  dominance (`ZoneDominance.cs`), exchanges with the API (`ZoneApiModels.cs`).
- `wwwroot/` — card catalog (`data/`), languages (`i18n/`), pictograms (`img/`), styles
  (`css/app.css`), file download and service worker registration (`js/olomoc.js`), installable
  app (`manifest.webmanifest`, icons in `img/app/`).

**`src/Olomoc.Web`**
- `App.razor` (router: Builder, Pairings and Online pages), `Program.cs` (services),
  `Layout/MainLayout.razor` (section header, language, menu), `wwwroot/index.html`
  (`<base href="/">`).

**`src/Olomoc.Builder`** (Razor library, `/builder/…` routes)
- `Pages/Home.razor` — builder: composition, legality, save and export, starting a game.
- `Pages/Play.razor` — play mode.
- `Pages/AdminCatalog.razor`, `Shared/CogEditor.razor`, `EquipmentEditor.razor`,
  `AbilitiesEditor.razor`, `CatalogDrafts.cs`, `AdminCatalogApi.cs` — card catalog editor
  (editors and admins).
- `Pages/Control.razor`, `Shared/Control/` (`AccountsPanel`, `TournamentsPanel`,
  `OnlineGamesPanel`), `Shared/AdminApi.cs` — admin panel (admins).
- `Pages/Account.razor`, `AccountConfirm.razor`, `AccountReset.razor`, `Privacy.razor` —
  account, links received by email, personal data.

**`src/Olomoc.Pairings`** (Razor library, `/pairings/…` routes)
- `Pages/Index.razor` — create an event, join one, "My events" (localStorage).
- `Pages/TournamentPage.razor` — public page: registration, round, standings, players.
- `Pages/Admin.razor`, `Shared/ResultEditor.razor` — administration: closing registrations,
  rounds, VP entry, drops, export, share links.
- `Services/TournamentApi.cs` (API calls), `Services/MyTournaments.cs` (tokens kept on the
  device).
- `Pages/Zones.razor` (map, My zones, list), `ZonePage.razor` (a zone: entry, games, statistics),
  `ZoneManage.razor` (settings; creation by an admin), `ZoneStatsAll.razor` (former address);
  `Shared/EncounterForm.razor` (entry of a game), `StatsSections.razor` (statistics panels,
  shared with `/stats/`), `DominanceLine.razor`, `ZoneMap.razor` (map of the zones),
  `ZoneLogo.razor`, `ZoneText.cs`; `Services/ZoneApi.cs`,
  `Services/MyZoneEntries.cs` (entry tokens kept on the device).

**`src/Olomoc.Online`** (Razor library, `/online/…` routes)
- `Shared/OnlineStart.razor` — "Play online" box of the Play page: create a lobby, games followed
  on the device and player name (`Services/MyGames.cs`); `Pages/Home.razor`: `/online/` → Play
  page.
- `Pages/Game.razor` — a game: lobby (`Shared/LobbyPanel.razor`), setup
  (`Shared/SetupPanel.razor`), sides (`Shared/CampPanel.razor`), log (`Shared/GameLog.razor`).
- `Services/OnlineConnection.cs` — hub connection, automatic reconnection.
- `Services/OnlineLauncher.cs` — opens a lobby (with or without a cabal) and makes it the
  current game.

**`src/Olomoc.Server`**
- `Program.cs` — headers (`Cache-Control: no-cache`, `nosniff`), `/` → `/builder/`, `/healthz`,
  rate and request size limits.
- `Tournaments/TournamentEndpoints.cs` — `/api/tournaments` API (rights by token);
  `TournamentStore.cs` (one document per event, one lock per event),
  `StatsArchive.cs`, `ServerCatalog.cs` (server-side card catalog), `TournamentPurge.cs` (90 days
  without changes, date passed or missing).
- `Accounts/AccountEndpoints.cs` — `/api/account` API (Identity, session cookie) and
  `/api/admin/accounts` (roles, temporary password, unlock, sign out, deletion); `Roles.cs`
  (player, editor, admin; API filter, `OLOMOC_ADMINS`); `CabalSyncEndpoint.cs` (account cabals);
  `Mail.cs` (SMTP relay through MailKit, or files; sending queue), `AccountMails.cs` (email
  texts).
- `Zones/ZoneEndpoints.cs` — `/api/zones` API (public reading, entries without an account, rights
  by entry token, account, manager or admin) and `/api/admin/zones`; `ZoneStore.cs` (zones as
  documents, games in the `ZoneGames` table), `ZoneStats.cs` (statistics kept in memory).
- `Admin/AdminEndpoints.cs` — `/api/admin` API of the admin panel (overview, events, online
  games, backup, purges, log); `AdminAudit.cs` (action log).
- `CardCatalog/CatalogStore.cs` — served card catalog: repository files + editor changes
  (`DATA_DIR/catalog`), drafts hidden from visitors, export; `CatalogEndpoints.cs` (served
  files, `/api/admin/catalog` API).
- `Data/OlomocDb.cs` — SQLite database (EF Core): `Documents` table (kind, code, dates, JSON),
  accounts (Identity tables), account cabals (`SavedCabals`), admin log (`AdminActions`), games
  of the zones (`ZoneGames`), cookie
  keys; schema through migrations (`Data/Migrations`, applied at startup; new migration:
  `dotnet dotnet-ef migrations add <Name> --project src/Olomoc.Server
  --output-dir Data/Migrations`). `JsonImport.cs` (import of the old files), `DbBackup.cs`
  (daily copy, `VACUUM INTO`).
- `Hosting/DocumentStore.cs` — JSON documents under a public 6-character code (creation, change
  and deletion under lock, purge of inactive ones).
- `Online/` — creation of games (`OnlineEndpoints.cs`: `/api/online`, event table), SignalR
  hub `/hubs/online` (`OnlineHub.cs`, call limit per connection), storage and purge
  (`OnlineStore.cs`: one document per game, kind `online`), players' presence
  (`OnlinePresence.cs`).
- `Hosting/ClientApps.cs` — the WebAssembly app at the root (`_framework/`); routes of the
  `/builder/…`, `/pairings/…`, `/online/…`, `/bloc/…` and `/stats/` sections sent to `index.html` (`/zones/…`: redirected to `/bloc/…`).
- `Hosting/SecurityHeaders.cs` — CSP, frames forbidden, Referrer-Policy, HSTS over HTTPS.
- `wwwroot/service-worker.js` — offline: network first, cached copy if the network is missing
  (never the API); at the root to cover the app and `/_content/` (see `docs/pwa.md`).

**`Dockerfile`, `docker-compose.yml`** — generic: image and container of any instance
(`docker-compose.yml`: defaults marked "orloj.top" to override in `.env`).

**`deploy/proxy`** — specific to orloj.top: the author's VPS reverse proxy (Caddy), HTTPS and
certificates, routing by host name between Orloj and B5T. Not needed elsewhere; a generic
two-line Caddy example is in [Deployment](#generic-any-instance).

**`tests/Olomoc.Tests`** — legality, search, export, save, play mode, language consistency,
events (standings, pairings, rules), online games (rules, hub), admin panel; references
`Olomoc.Shared` and reads its static files from disk; host started in memory
(`WebApplicationFactory`) for the site's routes and the APIs (events, accounts, card
catalog, admin; temporary data folder, emails written to disk).

## License

**Code**: GNU GPL v2 (`LICENSE` file).

**Game content: © Occicat Games, used with permission, outside the GPL license.** Olomòc, its
names, texts, illustrations, logos and pictograms belong to Occicat Games; the code's license
does not apply to them and grants no right to reuse them. This covers:

- `src/Olomoc.Shared/wwwroot/img/` — card images, faction logos, Ministry emblems, pictograms
  and stat icons, the table's mats, terrain, markers and tokens (from the Vassal module), the
  ministry and artefact cards and the hostiles' profiles (`img/offblock/`, from the Vassal module);
- `src/Olomoc.Shared/wwwroot/i18n/*/cards.json` — card names and game effects, Ministry and
  faction names;
- `src/Olomoc.Shared/wwwroot/data/` — card catalog and faction reference, table manifest;
- the agenda names, the agendas' setups (`src/Olomoc.Shared/Online/AgendaSetups.cs`, after
  the Recueil des agendas) and the game vocabulary used in `i18n/*/ui.json`.

To reuse this content elsewhere, ask Occicat Games for permission
([occicat.com](https://www.occicat.com/)).

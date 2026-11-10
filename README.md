# Minerva

A real-time advisor for Deadlock. It reads the match state the player can see and suggests what to do next.

**Status: step 1, validating that live game state can be read.**

## How it reads the game

| Option | Verdict |
|---|---|
| Valve/community web APIs, broadcast stream | 60–90 s delay. Too slow. |
| Game State Integration (as in CS2/Dota 2) | Not in Deadlock: no GSI code in `client.dll` or `engine2.dll`. |
| Client POV demo (`record` + `demo_flush`) | `record` is flagged `cheat`, so it doesn't work in matchmaking. Kept for offline replay analysis. |
| Reading game memory / injecting | Risks bans. Not used. |
| **Panorama HUD mod + `console.log`** | **Chosen.** A UI addon like other Deadlock HUD mods. It reads only the HUD's own panel tree (what the game already draws) and prints it to the console. With `-condebug` the game mirrors the console to `game/citadel/console.log`, which the companion follows. |

Ban risk comes from policy, not detection. Valve hasn't published a Deadlock mod policy, and a real-time advisor gives
an advantage by design. The mod is read-only: no input, no commands, no network, no memory access.

## Architecture: built to survive patches

Deadlock patches constantly, so nothing patch-specific is hand-maintained:

| Component | What it does | When Deadlock patches |
|---|---|---|
| `Minerva.Source2` | Source 2 formats: binary KV3 writer, resource container, Panorama compiling. No Valve tools needed. | Unaffected unless Valve changes file formats |
| `Minerva.Mod` | Builds the addon **from the installed game's own files**: reads the current `base_hud.vxml_c`, adds one `<scripts>` include, re-encodes it, and packs it with the script into `addons/pak90_dir.vpk`. A manifest records the game build and host-layout CRC. | `probe mod status` reports the addon as stale; `probe mod install` regenerates it |
| `Minerva.Mod/Panorama/minerva.js` | In-game script. **Generic**: walks the whole HUD panel tree and logs changes, with no Deadlock panel IDs hardcoded. | Unaffected; HUD changes only change what the companion sees |
| `Minerva.Mod/ClassProbes.cs` | Panorama can't list a panel's classes, only test one (`BHasClass`). For layouts whose panels carry meaning in classes (the minimap: hero/camp/boss, enemy, dead, camp up), the builder reads the layout's stylesheets from the game files and writes the class list into the script. | The class list is regenerated; only a renamed layout needs a code change (reported as a warning) |
| `Minerva.Hud` | Follows `console.log`, reassembles the mod's messages, mirrors the HUD tree. | Unaffected |
| `Minerva.GameState` | Static game data read from the game's packages: heroes and items (`GameData`), and the map's camps (position, type, first spawn, respawn, tether) and team objectives (`MapData`, from the map's entity lump plus `scripts/misc.vdata`). Also demo parsing for offline analysis. | Data is re-read from game files automatically |
| `Minerva.Match` | Interpretation: HUD tree + static data → match state. `Bindings/` matches every live panel to its node in its layout file (`LayoutCatalog` reads the game's layouts) by id, then type and position, so a label is known by its dialog variable (`{i:kills}` in `citadel_hud_top_bar_player`) instead of a panel path. `Hud/` reads named values that way (all names in `HudVocabulary`, checked against the installed layouts at start). `MatchReader` joins top bar rows with minimap icons by player slot order. `Minimap/` reads hero and objective icons by their classes (all Deadlock class names in `MinimapVocabulary`) and calibrates minimap ↔ world per match by fitting the walker/patron/barracks icons to the same objectives in `MapData`, which also tells which minimap team is which game team. Reports what it expected but didn't find. | Class renames: one line in `MinimapVocabulary`, flagged by a warning; transform and team mapping are refitted every match |
| `Minerva.Probe` | Development CLI. | — |

The interpretation layer (which panel holds souls, the clock, objectives, minimap icons) comes next, built from what
step 1 reveals. It should also be derived from game files where possible, e.g. by decompiling the scoreboard and
minimap layouts to find panel IDs.

## Trying it (step 1)

Requirements: .NET 10 SDK; Deadlock launch option `-condebug`; `citadel/addons` mounted in `gameinfo.gi`
(Deadlock Mod Manager does this).

```
dotnet build -c Release
alias probe="dotnet src/Minerva.Probe/bin/Release/net10.0/probe.dll"

probe mod install          # build from current game files, install to addons/pak90_dir.vpk
probe hud watch            # follow console.log
```

Start Deadlock and open a **bot match**. `hud watch` should print `[hello]` when the HUD loads, then `[full]`/`[delta]`
lines every second with the mod→companion latency. The latest tree is written to `out/hud_tree.json`. Remove the addon
with `probe mod uninstall`.

Then `probe map` prints the map's camps and objectives from the game files, `probe minimap` reads heroes and
objectives from `out/hud_tree.json` and places them in world coordinates (with each hero's nearest camp), and
`probe state` prints the whole match state: clock, souls, and each hero's row joined with their map position.

## Tests

`dotnet test --project tests/Minerva.Tests -c Release`:
- re-encoding the game's real `base_hud` layout is lossless, and the override keeps every original panel;
- compiled scripts read back as valid `PanoramaScript` resources;
- the real `minerva.js` runs under a JS interpreter against a fake Panorama API, and its console output rebuilds the
  same tree in the companion (full dumps, deltas, removals, chunking, non-ASCII text, script reloads).

Tests that need the game skip themselves if Deadlock isn't installed (or set `DEADLOCK_DIR`).

## Findings so far

- First launch (main menu, 2026-11-10): the game loads the generated addon. The script runs in `CitadelHudRoot`, which
  already exists in the menu with about 19,000 panels. The game clock is readable (`/Hud/Panel#0/TopBar/Panel#2/GameTime`).
- Walking the full tree in one go cost 80–150 ms per walk, a visible stutter. Protocol 2 split the walk into ~2 ms
  slices per frame, caps it at ~5% of main-thread time, and sends a full dump only every 5 minutes.
- First bot match: ~27,600 panels, of which only ~3,000 are on screen; the rest are closed menus (the shop alone has
  4,700). Protocol 2 still spent 27–30 ms in some frames, because finishing a walk diffed the whole tree at once and
  console output costs ~0.15 ms per line. Protocol 3 diffs per panel as it walks, puts output under the same per-frame
  budget, and walks hidden subtrees only every 10th walk.
- Second bot match (protocol 3): worst frame 2 ms on nearly every walk (one 15 ms outlier). Visible panels
  (~5,000) refresh every second at ~40 ms of script time spread over ~20 frames; a complete walk (~25,500 panels)
  costs ~270 ms spread over ~140 frames. The companion's mirrored tree stayed consistent. Load is capped as an average
  over 10 walks, so a complete walk no longer delays the next update by 5 s.
- What the HUD exposes in a match (all readable as text):
  - the clock and pregame countdown;
  - for all 12 heroes (top bar): hero, souls, K/D/A, objective damage, health and resists;
  - off-screen hero indicators with live distances (`Paige 171m`);
  - objective states (shrines, mid-boss: protected, out of range, destroyed);
  - the minimap location name (`central park`), and about 50 positioned minimap icon panels (not identified yet);
  - the player's own health and regen, resists, move speed, ammo and ability cooldowns;
  - souls pickup events.

- Minimap icons are snippets from `hud_minimap` (`MinimapPlayerSnippet`, `MinimapBossSnippet`, ...); what an icon is
  and its state are only style classes (`.map_button.neutral.neutral_large.active` = a large camp that is up,
  `.map_button.player.enemy.PlayerDead`, ...). Protocol 4 reads each panel's layout file and, for minimap icons, the
  probed classes; `hud watch` writes them to `out/minimap.json`. It also dumps the member names of Panorama's globals
  and of each panel type to `out/hud_api.json`, to see what this build exposes beyond the documented Dota API.

- Third bot match (protocol 4, 2026-11-10): updates every 1.2–1.5 s with no long gap after complete walks; worst frame
  2–3 ms on 55 of 57 walks (one 44 ms console write). The game reports `layoutfile` as `panorama\layout\hud_minimap`
  (backslashes, no extension), which the first version of the probe didn't match; fixed.
- Panorama API in this build (`out/hud_api.json`): no game-state globals (no Dota-style `Game`/`Players`/`Entities`),
  only `$`/`panorama` plus sound functions, so the HUD is the only source. Panels can test classes (`BHasClass`) but
  not list them, and images can be set but not read. Readable extras: `heroid` (hero badges/logos), `min`/`max` on
  progress bars. `$.AsyncWebRequest` exists (a possible localhost transport instead of console.log).
- Minimap: 69 icons (45 placed): 12 heroes (`MinimapPlayerSnippet`), ~31 bosses with health bars, the mid-boss with a
  `NextSpawnTimer` label, and the rest (camps, markers) to be identified by class.

- Fourth bot match (5 min, layout fix in): worst frame 2–3 ms on 193 of 195 walks; updates every ~1.5 s. All 69
  minimap icons report classes:
  - heroes: `player` + `friend`/`enemy`, `localplayer`, `team1`/`team2`; `active` on an enemy means visible now
    (without it, the icon is the last known position); `PlayerDead` while dead; `arrowUp`/`arrowDown` for height
    relative to you. Positions for all 12 heroes update every walk.
  - objectives: `boss` + `boss_icon_t1` (guardian) / `_t2` (walker) / `_t3` (patron) / `boss_barracks_icon` /
    `boss_buildingzip`, with team and lane (`yellowLane`, `purpleLane`, `blueLane`); `mid_boss midboss_spawned`.
  - markers: shops, tunnel exits, teleporters, gold spawn, stairs, capture points.
  - **no camps**: the minimap no longer shows neutral camps (the `neutral_*` styles are leftovers). Camp state has to
    be inferred.
- Camp data is in the game files: the map's entity lump (`maps/dl_midtown.vpk` → `entities/default_ents.vents_c`) has
  52 `info_neutral_trooper_camp` entities with world position, type (weak 4, medium 24, strong 12, vault 11, mid-boss
  1) and some per-camp timing overrides; `scripts/misc.vdata` has each type's first spawn and respawn interval
  (weak 120 s / 85 s, medium 300 s / 290 s, strong 480 s / 335 s, vault 480 s / 300 s).
- Minimap ↔ world: the same entity lump has the walkers, patrons and barracks (`npc_boss_tier2/3`,
  `npc_barrack_boss`, with `teamnumber` and lane in `BossName`). Fitting an affine transform from those 14 landmarks
  to their minimap icons gives `x ≈ 0.0239·wx + 252`, `y ≈ −0.0242·wy + 254` (mean error 8 px, ~42 world units per
  px; game team 2 = minimap `team1`). The minimap may be inverted per team (`invert_map`), so fit per match from the
  landmarks rather than hardcoding.

- HUD values by binding (from the recorded match): every hero's name (→ hero id via the game's localization), souls
  (abbreviated, `2.1k`), level, health bar fraction, max health and resists, respawn timer; the match clock, team net
  worth and kills, the local player's exact unspent souls. 17.5k of 25.7k panels match a layout node.
  - The top bar's team containers are named `TeamFriendly`/`TeamEnemy` but are fixed game teams (their layout
    attribute `team="2"`/`"3"`): the local player was under `TeamEnemy`. The score labels, by contrast, are relative to
    the local player. Both conventions confirmed against the recorded match (deaths, net worth sums).
  - Top bar rows and minimap hero icons are in the same player-slot order: all five deaths in the match appeared on
    row `TopBarPlayerN` and icon `Panel#(N+4)` at the same moment. That links names to map positions.
  - K/D/A stayed 0/0/0 all match although heroes died: the top bar doesn't update it while hidden (probably only with
    the scoreboard open). Deaths can be counted from the respawn timer instead.
  - Reading the whole tree takes ~190 ms; fine at one update per 1.5 s, but worth making incremental.

## Open questions

- What does the HUD panel tree expose in a match: scoreboard numbers, items, timers, minimap icon positions?
- Does `console.log` flush immediately? `hud watch` reports the latency.
- Is the sliced walk invisible in play? `hud watch` reports the worst frame cost per walk.
- Offline demo parsing: DemoFile.Net 0.44.1 can't read current demos (`CitadelAbilityUpgradeInfoPacked_t`) and
  needs a one-line fork fix. Low priority now that demos aren't the live source.

Credits: Panorama/KV3/resource writing follows [modlock](https://github.com/paralin/modlock) (MIT).

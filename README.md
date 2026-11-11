# Minerva

A real-time advisor for Deadlock. It reads the match state the player can see and suggests what to do next.

**Status: the game state is read live (HUD values, hero positions, kills, camps). Next: the advisor, starting with
item recommendations.**

## How it reads the game

| Option | Verdict |
|---|---|
| Valve/community web APIs, broadcast stream | 60–90 s delay. Too slow. |
| Game State Integration (as in CS2/Dota 2) | Not in Deadlock: no GSI code in `client.dll` or `engine2.dll`. |
| Client POV demo (`record` + `demo_flush`) | `record` is flagged `cheat`, so it doesn't work in matchmaking. Not used. |
| Reading game memory / injecting | Risks bans. Not used. |
| **Panorama HUD mod + `console.log`** | **Chosen.** A UI addon like other Deadlock HUD mods. It reads only the HUD's own panel tree (what the game already draws) and prints it to the console. With `-condebug` the game mirrors the console to `game/citadel/console.log`, which the companion follows. |

Ban risk comes from policy, not detection. Valve hasn't published a Deadlock mod policy, and a real-time advisor gives
an advantage by design. The mod is read-only: no input, no commands, no network, no memory access.

## Architecture

Three layers on a foundation of game data, in one library; an app on top.

```
src/
  Minerva/              the library, one folder per layer
    GameData/           what the game files say: install, heroes, items, map (camps, lanes, objectives), layouts
    Mod/                the in-game addon: script, builder, installer; Formats/ writes Source 2 files
    Reading/            what the HUD shows now
      Transport/        console.log → the mod's messages → the mirrored HUD panel tree
      Hud/              panels matched to the game's layout files; HUD values and the kill feed
      Minimap/          hero and objective icons, calibrated to world coordinates
      Camps/            camp state, inferred (camps have no icons)
      Timeline/         the match over time: events, K/D/A, last-seen positions
    Advisor/            what to do about it (next)
  Minerva.App/          the executable: development commands now, the companion window later
  Minerva.Tests/        tests in the same folders; Fixtures/ holds recordings from real matches (no player names)
```

Layers only depend downward (GameData ← Mod, Reading ← Advisor ← App), and the advisor only sees the reading layer's
results, never its panels. `ArchitectureTests` checks this, since one project doesn't make the compiler enforce it.

Deadlock patches constantly, so nothing patch-specific is hand-maintained:

| Part | What it does | When Deadlock patches |
|---|---|---|
| `GameData/` | Heroes and items (`GameCatalog`), the map's camps (position, timings, units and their soul rewards), lane paths and objectives (`MapData`, from the map's entity lump and `scripts/*.vdata`), and every HUD layout file (`LayoutCatalog`). | Re-read from the game files at start |
| `Mod/` | Builds the addon **from the installed game's own files**: reads the current `base_hud.vxml_c`, adds one `<scripts>` include, re-encodes it, and packs it with the script into `addons/pak90_dir.vpk`. A manifest records the game build, host-layout CRC and script hash. `Formats/` writes binary KV3, resource containers and compiled Panorama, so no Valve tools are needed. | `mod status` reports the addon as stale; `mod install` regenerates it |
| `Mod/minerva.js` | The in-game script. **Generic**: walks the whole HUD panel tree and logs changes, with no Deadlock panel ids hardcoded. Panorama can't list a panel's classes, only test one, so for layouts whose panels carry meaning in classes (the minimap) the builder reads the layout's stylesheets and writes the class list into the script (`ClassProbes`). | Unaffected; the class list is regenerated |
| `Reading/Transport/` | Follows `console.log`, reassembles the mod's messages, mirrors the HUD tree. | Unaffected |
| `Reading/Hud/` | Matches every live panel to its node in its layout file by id, then type and position, so a label is known by its dialog variable (`{i:kills}` in `citadel_hud_top_bar_player`) instead of a panel path. Reads named values and the kill feed that way; all names it relies on are in `HudVocabulary`, checked against the installed layouts at start. | A renamed variable or element is reported at start |
| `Reading/Minimap/` | Reads hero and objective icons by their classes (`MinimapVocabulary`) and calibrates minimap ↔ world per match by fitting the walker/patron/barracks icons to the same objectives in `MapData`, which also tells which minimap team is which game team. | Transform and team mapping are refitted every match |
| `Reading/MatchReader` | Joins top bar rows with minimap icons by player slot order, and ties kill feed names to heroes. | — |
| `Reading/Camps/` | Camp state over time: spawn and respawn timers from `MapData`; clears from the local player's exact soul gains matching a nearby camp's unit rewards, and from other heroes' net worth jumps at a camp away from lanes. | Timings and rewards are re-read from the game files |
| `Reading/Timeline/` | The match over time, fed each read: kill, death, respawn, objective and camp events, each hero's K/D/A (from the feed) and last-seen position. The advisor reads from it. | — |

## Trying it

Requirements: .NET 10 SDK; Deadlock launch option `-condebug`; `citadel/addons` mounted in `gameinfo.gi`
(Deadlock Mod Manager does this).

```
dotnet build -c Release
alias minerva="dotnet src/Minerva.App/bin/Release/net10.0/Minerva.App.dll"

minerva mod install        # build from current game files, install to addons/pak90_dir.vpk
minerva watch              # follow console.log
```

Start Deadlock and open a **bot match**. `watch` prints `[hello]` when the HUD loads, then a line per update with the
mod→companion latency, and match events as they're inferred (`[event] 2:15  kill  Haze killed Billy (514 souls)`,
`[event] 3:31  camp  park_club_camp: cleared by you`). The latest tree is written to `out/hud_tree.json`. Remove the
addon with `minerva mod uninstall`.

`minerva replay --log <console.log>` replays a saved session through the timeline: every event, then each hero's K/D/A
and last-seen position, and every camp's state. `console.log` is rewritten at each launch, so copy it to keep a match.
`minerva map`, `gamedata` and `vpk` inspect the game files; `minerva minimap` and `state` read a tree dump. `minerva`
alone lists every command.

## Tests

`dotnet test --project src/Minerva.Tests -c Release`. Besides unit tests for each part:
- re-encoding the game's real `base_hud` layout is lossless, and the override keeps every original panel;
- the real `minerva.js` runs under a JS interpreter against a fake Panorama API, and its console output rebuilds the
  same tree in the companion (full dumps, deltas, removals, chunking, non-ASCII text, script reloads);
- recorded matches (`Fixtures/`) read completely through the installed game's layouts, and replay to the camp clears
  that happened;
- the names the readers rely on still exist in the installed game (fails right after a patch that renamed one);
- each layer uses only the layers below it.

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
  probed classes; `watch` writes them to `out/minimap.json`. It also dumps the member names of Panorama's globals
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
  px; game team 2 = minimap `team1`). The minimap is inverted for one team, but only visually:
  `hud_minimap.vcss` flips the whole panel (`.invert_map.HudMinimap { transform: scaleY(-1) scaleX(-1) }`) and
  counter-flips the icons, and the mod reads layout positions, which are taken before transforms. So icon
  coordinates are the same on either team. The transform is still fitted per match from the landmarks, so a future
  change to that would be absorbed.

- HUD values by binding (from the recorded match): every hero's name (→ hero id via the game's localization), souls
  (abbreviated, `2.1k`), level, health bar fraction, max health and resists, respawn timer; the match clock, team net
  worth and kills, the local player's exact unspent souls. 17.5k of 25.7k panels match a layout node.
  - The top bar's team containers are named `TeamFriendly`/`TeamEnemy` but are fixed game teams (their layout
    attribute `team="2"`/`"3"`): the local player was under `TeamEnemy`. The score labels, by contrast, are relative to
    the local player. Both conventions confirmed against the recorded match (deaths, net worth sums). The game's
    stylesheets agree that the containers don't depend on the local team: `hud.vcss` places `#TeamFriendly` left
    and `#TeamEnemy` right unconditionally, and spectators are styled through the same ids
    (`.TeamSpectator #TeamFriendly …`). Ally/enemy is relative only through `.friend`/`.enemy` classes.
    `MatchReader` cross-checks each row's team against its minimap icon's team, which comes from map geometry
    independently. If they ever disagree, it warns and doesn't link names to positions.
  - Top bar rows and minimap hero icons are in the same player-slot order: all five deaths in the match appeared on
    row `TopBarPlayerN` and icon `Panel#(N+4)` at the same moment. That links names to map positions.
  - K/D/A stayed 0/0/0 all match although heroes died, in both recordings, even with the scoreboard opened three
    times for ~2 s in the second (the K/D/A labels were shown, at zero height, still reading 0 after the local
    player's death). The in-match scoreboard is the expanded top bar; there's no other K/D/A label. Don't use it.
    Deaths can be counted from the respawn timer, and kills read from the kill feed (`citadel_hud_data_feed_info`:
    `{s:killer_name}`, `{s:victim_name}` and the soul bag amount). The feed shows persona names (in bot matches the
    hero's name), so mapping to heroes goes through each top bar row's `player_name`.
  - Kill feed (10-minute match): the killer side's `{d:gold}` is the kill's bounty; solo kills by the local player
    matched their soul gains exactly (+514, +599, +610, +688). The victim side's copy is hidden and keeps another
    entry's value. Assists are hero images with ids `PlayerAssist<hero id>` (Haze, hero 13, as `PlayerAssist13`).
    Objective kills come under an element named `BossDied`, with the `bossLaneIcon` element shown; NPC killers
    (`GUARDIAN`) match no row. Entries move down as new ones arrive (their panels change), so kills are identified by
    killer, victim and bounty, and counted once seen on two consecutive reads. Deaths from respawn timers and from the
    feed agreed for every hero; the timeline gave the local player 13/1/3.
  - Objective icons: the `active` class drops when an objective dies and the icon stays. But the minimap reuses
    icon panels, so after a guardian dies a panel can show a different one: objectives are followed by kind, team and
    lane. Five guardians went down in the match, two of them in the feed (the others presumably to troopers).
  - Reading the whole tree takes ~25 ms once warm (~190 ms for the first read); fine at one update per 1.5 s.
  - Some layouts are loaded more than once. The souls display is also inside the shop, where (closed, so hidden) the
    mod only refreshes it on complete walks, ~15 s apart. Readers take the copy on screen (`HudBindings.Primary`).

- Camps (from the game files and a recorded bot match):
  - Spawn points (`info_neutral_trooper_spawn`) name their camp's `targetname` and the unit they spawn; units'
    rewards are `m_flGoldReward` in `scripts/npc_units.vdata`, growing linearly by
    `m_flGoldRewardBonusPercentPerMinute` (1.08% of the base per minute). A weak camp is three 45-soul units.
  - Respawn timers start when the camp's last unit dies; a camp with a survivor doesn't respawn (community guides
    agree, and their timers match `misc.vdata`: 1:25 small, 4:50 medium, 5:35 large, 5:00 Sinner's Sacrifice).
  - The local player's unspent souls show each kill: clearing `park_club_camp` at 3:24–3:31 was three gains of
    exactly +46 (45 + 3.4 min of growth) 200–300 units from the camp. Lane gains are different amounts (+56/+58 per
    trooper and +26/+28 orb halves at that time) and 1000+ units from camps.
  - Lane paths are `lane_marker_path` entities (`pathNodes`: node positions plus tangents). In the recording 95% of
    hero positions were within 430 units of one, so other heroes' net worth rises are only read as camp clears when
    they are 600+ units off every lane: lane troopers raise net worth just as fast.
  - Camps have no HUD or minimap icons; community guides say the game now shows a respawn timer in the world, which
    the HUD tree doesn't carry.
  - Camp souls are split evenly between the allied players who damaged the unit in the last 5 s, and camp units
    drop no orbs (Deadlock wiki). The tracker allows a half or a third of a reward only with allies at the camp.
  - Kills often land in the same update as other souls: a lane trooper (+107 = 47 + 60 in the 10-minute match) or a
    few souls of trickle (+5 to +13 appear throughout). The tracker allows up to 8 extra souls per match (a lane
    trooper pays 10–12 more than a small camp's unit), and counts a visit's other gains toward clearing a camp the
    player killed part of.
  - A Sinner's Sacrifice pays per melee hit (~1/20 light, ~1/10 heavy, about half on the final hit, shared between
    hitters; Deadlock wiki). Seen as +34, +34, +68 next to a 170-soul machine. Its class
    (`npc_neutral_sinners_sacrifice`) marks per-hit payout; it is cleared by a melee-range visit's souls.
  - Reward growth: units paid 46–47 at 3–5 min (45 base) and 80–82 at 8:39–9:06 (75 base, formula 82): rewards can
    come in up to ~3% under the linear formula.

## Open questions

- Camp clears by others are inferred, not seen: does net-worth inference produce false clears in longer matches?
- Is the mod acceptable in matchmaking? Only tested in bot matches so far.

Credits: Panorama/KV3/resource writing follows [modlock](https://github.com/paralin/modlock) (MIT).

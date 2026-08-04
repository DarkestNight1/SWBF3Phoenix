# BF3 Legacy — Battlefront III features for Phoenix

SWBF3 Phoenix extends the SWBF2 Phoenix Unity runtime with the feature set of
Free Radical's cancelled *Star Wars Battlefront III* (2008) and its salvaged
release *Star Wars Battlefront: Elite Squadron*, plus modern-era upgrades.
The player still supplies their own copy of Star Wars Battlefront II (2005);
no original game assets are distributed.

All new code lives in `UnityProject/Assets/Runtime/BF3Legacy/`.

## Feature overview

| Feature | Status | Code |
|---|---|---|
| Capital ship destruction | Playable systems + greybox ships | `CapitalShip/` |
| Capital ship interiors (hangar→reactor→bridge) | Implemented, ES/BF3-accurate layout | `CapitalShip/PhxShipInterior.cs` |
| Escape pods, hangar turret stations, autoguns | Implemented | `CapitalShip/` |
| Ion cannon (ground↔space link) | Implemented | `CapitalShip/PhxIonCannon.cs` |
| Vertical Battlefront on ALL maps (stock + mods) | Implemented, atmosphere/orbit per planet | `VerticalBattlefront/` |
| Melee weapons (`melee` ODF class, lightsabers) | Implemented | `GameClasses/weapons/PhxMeleeWeapon.cs` |
| Lightsaber dismemberment on melee kills | Implemented (mesh extraction sever) | `Dismemberment/` |
| Soldier damage & death | Implemented (was a TODO upstream) | `GameClasses/soldier/PhxSoldier.cs` |
| Modern AI (squads, flanking, difficulty tiers) | Implemented | `AI/` |
| Modern lighting (ACES, SSAO, SSR, volumetrics) | Implemented | `Graphics/PhxModernLighting.cs` |
| 4K graphics mode | Implemented | `Graphics/PhxResolutionManager.cs` |
| Mod support (load order, toggling, detection) | Implemented | `Mods/PhxModManager.cs` |
| BF3 map recreations | 6 greybox layouts, data-driven | `Maps/` |

Everything is toggleable via `bf3legacy.json` in Unity's persistent data path
(created with defaults on first run) — see `PhxBF3Config` in `PhxBF3.cs`.

## Capital ship destruction

Recreates the Elite Squadron assault flow (the design BF3 was built around):

1. **Shielded** — the ship ignores hull damage. Shields drain from space
   weapon fire, or drop instantly when a ground team fires the **ion cannon**
   (hold the linked command post to own it).
2. **ShieldsDown** — the hangar opens; attackers can board.
3. **Breached** — internal critical systems (shield generator, auto-turret
   mainframe, life support, engines) can be sabotaged.
4. **ReactorExposed** — once all criticals are down, the main reactor drops
   its invulnerability.
5. **Dying** — reactor destroyed: a staged breakup plays — chain-reaction
   explosions rippling outward, the ship lists and fractures into physics
   debris sections, then a final reactor flash and shockwave. In-atmosphere
   ships (Coruscant) fall toward the city; deep-space wrecks drift.

External fire alone can never finish a ship (hull clamps at 25%) — you must
board and destroy the reactor, exactly like the source games.

`PhxCapitalShip` keeps a per-team registry (`GetShipOfTeam`) so game modes,
HUD and AI can query ship state; `OnStateChanged` / `OnShipDestroyed` events
drive scoring and voice-over hooks.

### Interior playspace (`PhxShipInterior`)

Every capital ship has a fully walkable interior, laid out from what is
documented of Elite Squadron's boarding runs and the BF3 leaked builds:

```
 BOW                                                              STERN
 ┌─────────────┬───────┬—————————————————————────┬───────────┬────────┐
 │   HANGAR    │JUNCT. │ corridor (port)         │  REACTOR  │ BRIDGE │
 │ mouth+shield│ pods  ├─────────────────────────┤  CHAMBER  │        │
 │ turret alc. │ pods  │ shield gen│mainfr.│life │  core     │        │
 │ droids,spawn│       ├───────────┴───────┴─────┤  spawn    │        │
 │             │ pods  │ corridor (starboard)    │           │        │
 └─────────────┴───────┴─────────────────────────┴───────────┴────────┘
```

- **Hangar** (bow): shielded mouth (drops with the ship's shields), landing
  space, health/ammo droid stations, defender spawn pad, and two **manned
  anti-fighter turret stations** (`PhxShipTurretStation`) — a friendly soldier
  standing at the console mans the external hull gun, which then engages
  enemy flyers.
- **Junction**: four **escape pod bays** (`PhxEscapePod`). Linger a moment to
  launch; if the ship is already dying the pod fires immediately — BF3's
  "watch the destruction from an escape pod". Pods arc down and deliver the
  passenger to their team's nearest command post (or a drift point in space).
- **Twin narrow corridors** (ES's hallways) guarded by ceiling
  **autoguns** (`PhxShipAutogun`, BF3's interior defense weapon). All autoguns
  go offline when the auto-turret mainframe is sabotaged.
- **Systems rooms** between the corridors: shield generator, auto-turret
  mainframe, life support — the BF2-heritage criticals that gate the reactor.
- **Reactor chamber** (aft): the reactor core column, the kill objective.
- **Bridge** (sternmost, deepest compartment — BF3: "fight your way to the
  bridge"): non-critical subsystem, destroying it is optional score/flavor.

The hull around the interior is built from break-section plates, so the
destruction sequence tears the actual ship apart.

## Lightsaber dismemberment

`PhxDismemberment.TrySever(victimGameObject, hitPosition)` — call on lethal
saber hits. It finds the nearest severable bone by name matching (works with
the SWBF2 `human` skeleton: `bone_l_forearm`, `bone_r_calf`, …), extracts the
limb triangles from the skinned mesh (dominant-bone-weight split, baked in the
current pose), spawns the piece as a physics chunk, collapses the bone on the
body and adds a cauterized glow. Film-style: no blood.

Gore levels in config: `0` off, `1` sparks only, `2` full dismemberment.

## AI

- `PhxBF3AIController` — per-soldier brain: LOS target acquisition, reaction
  time, burst-fire discipline, reloading, strafing/cover, objective seeking
  and command-post capture. Drop-in replacement for the old stub controller
  (spawn with `MTC.SpawnAI<PhxBF3AIController>(...)`).
- `PhxAIDirector` — battlefield commander: builds squads, spreads them over
  command posts, designates flanking squads with lateral approach offsets.
- `PhxAISkillProfile` — four difficulty tiers: **Classic** (2005 feel),
  **Veteran**, **Elite**, **Legendary** (fast reactions, tight aim, heavy
  flanking). Per-unit jitter keeps squads from acting in lockstep.

## Graphics & lighting

`PhxModernLighting` adds a priority-100 global HDRP volume at runtime: ACES
tonemapping, automatic exposure, ambient occlusion, screen-space reflections,
contact + micro shadows, volumetric fog, restrained bloom, and pushes
directional shadow maps to 4096. `PhxResolutionManager` applies the configured
resolution (4K default), full-res textures, forced anisotropic filtering and a
raised LOD bias.

> Note: HDRP override APIs move between versions. These are written against
> HDRP 10.x (Unity 2020.3, this project's pinned version); re-verify in the
> editor after any HDRP upgrade.

## Vertical battlefront on every map

`PhxVerticalBattlefront` (on the persistent BF3Legacy host) watches for map
loads and builds the space layer over **any** map — stock SWBF2, BF3 Legacy
mod maps, or other addons:

- **Planet surface maps** (Coruscant, Hoth, Tatooine, …): capital ships fly
  **in atmosphere**, looming ~450 m over the battlefield exactly like Elite
  Squadron's ships loom over every planetary battle.
- **Space and vacuum maps** (`spa*`, Polis Massa): ships sit in **orbit**
  (~900 m) instead.
- **Interior maps** (Tantive IV, Death Star): no space layer — there's no sky.
- **Unknown addon maps** (including BF3 Legacy content) default to atmosphere
  unless their script name looks like a space map.

The transition is seamless — same scene, no loading (Free Radical's core
pitch). A `PhxSpaceTransitionBand` trigger at ~55 % of ship altitude marks the
seam and provides the hook for sky-fade/music/HUD callouts. The current map's
lua script name is exposed via `PhxGame.CurrentMapScript`.

## Melee weapons & dismemberment wiring

The SWBF2 `melee` ODF class (lightsabers) was unimplemented in Phoenix; the
new `PhxMeleeWeapon` (registered in `PhxClassRegister`) performs an arc sweep
per swing and damages everything in reach. Soldier damage flow is now real:

- `PhxSoldier` implements `IPhxDamageableInstance` — blaster bolts, autoguns
  and melee all actually kill now (`Die()` was previously a TODO).
- Lethal hits from a weapon whose class or name contains "saber" (or with
  `IsLightSaber = 1` in the ODF) call `PhxDismemberment.TrySever` with the
  hit position — severing the nearest limb, honoring the configured gore
  level. Corpses are frozen and cleaned up after 15 s.

## Mod support & BF3 Legacy mod compatibility

`PhxModManager` layers on top of the existing `GameData/addon` discovery:

- `modorder.txt` in the addon folder controls load order (one folder name per
  line, `#` comments, `!folder` disables a mod).
- Mods can be toggled from code/UI via reversible `addme.script` renames.
- Known conversions are detected; when the community
  [Star Wars Battlefront III Legacy mod](https://www.moddb.com/mods/star-wars-battlefront-iii-legacy)
  (recovered Free Radical BF3 assets packaged as SWBF2 addon content) is
  installed, `PhxModManager.IsBF3LegacyInstalled` is set and BF3 features can
  light up on its maps. This is the preferred way to get *real* BF3 assets —
  the runtime loads them through the standard lvl pipeline.

## Installing the BF3 Legacy mod (3.1) — yes, it's just an addon folder

The current all-in-one release,
[Battlefront 3 Legacy Mod (3.1)](https://www.moddb.com/addons/battlefront-3-legacy-mod-31),
ships as **standard SWBF2 addon content** — the original 2005 game is
required, which is exactly the setup Phoenix expects (the user supplies their
own BF2 install):

1. Extract the release into your BF2 install's `GameData/addon/` folder, so
   each mod folder contains its own `addme.script`
   (`GameData/addon/<FOLDER>/addme.script`). The 3.1 pack bundles the
   Pre-Demo 3.0 content, Era Mod 1.4, Death Star II, the GCW space demo, map
   fixes and the MoreMaps patch.
2. Launch Phoenix with `Game Path String` pointing at that BF2 install.
   `PhxGame` discovers every addon via its `addme.script`;
   `PhxModManager` lists them, applies `modorder.txt` ordering, and flags
   `IsBF3LegacyInstalled`.
3. The mod's maps then load through the normal lvl pipeline like any other
   SWBF2 map, and the vertical battlefront layer activates over them.

Caveats: the release notes assume the *retail* game patched with the
community 1.3 patch and recommend an HD HUD mod — those target the original
executable's quirks. Phoenix reimplements the engine, so HUD patching is
irrelevant here, but any 1.3-era Lua APIs the mod's mission scripts call must
exist in `PhxLuaAPI`; missing-function log warnings on map load are the mod
compatibility TODO list.

## Debugging mods and maps

- **In the Unity editor**: open `Runtime/Scenes/PhxMainScene`, set
  `Game Path String` on the *Game* object, leave `Mission List Path` empty,
  and press Play. Everything logs to the Console: addon registration
  (`RegisterAddonScript`), lvl load failures, missing ODF classes, Lua
  errors from `PhxLuaRuntime`, and all `[BF3Legacy]` systems.
- **Mod triage**: use `GameData/addon/modorder.txt` — `!foldername` disables
  a mod without deleting it; binary-search a broken load order this way.
- **Built player logs**: `%USERPROFILE%\AppData\LocalLow\<company>\<product>\Player.log`
  (Linux: `~/.config/unity3d/.../Player.log`) contains the same output.
- **BF3 Legacy config**: `bf3legacy.json` next to the player log — flip
  features (`EnhancedAI`, `VerticalBattlefront`, `Dismemberment`, …) to
  isolate a misbehaving system, then relaunch.
- **Boot straight into a map**: `PhxSettings.BootSWBF2Map` accepts a map
  script name (e.g. `geo1c_con`) to skip the menu while iterating.

## BF3 map recreations

Where recovered assets aren't available, `Maps/` provides data-driven greybox
recreations compiled from public documentation of the leaked BF3 builds and
Free Radical Archive material, with Elite Squadron layouts as fallback
inspiration. `PhxBF3MapBuilder.Build("bf3_coruscant")` produces a playable
blockout: ground, landmark volumes, lit team-colored command post markers, a
sun matched to the planet's palette, per-team destructible capital ships in
the space layer, and an ion cannon where documented.

| Map id | Layout basis |
|---|---|
| `bf3_coruscant` | Leaked build: rooftop plazas, comm towers, senate skyline, space battle directly overhead |
| `bf3_cato_neimoidia` | Free Radical map, as reconstructed by the BF3 Legacy team: bridge city between rock arches |
| `bf3_dantooine` | Leaked build: plains + the extended cave system with secret areas |
| `bf3_bespin` | BF3 map list; classic Platforms layout with lethal drops |
| `bf3_desolation_station` | BF3's original deep-space shipyard: space-only, capital ships are the objective |
| `bf3_tatooine` | BF3 map list + Elite Squadron ground-to-space Mos Eisley layout |

Greyboxes are intentionally placeholder-shaped: they make the layouts playable
and testable now, and are meant to be replaced landmark-by-landmark with real
art (or superseded entirely by the BF3 Legacy mod's own level files).

## Research sources

- [Free Radical Archive — Star Wars: Battlefront III](https://freeradical.fandom.com/wiki/Star_Wars:_Battlefront_III)
- [TechRaptor — Build of Free Radical's Battlefront III leaked](https://techraptor.net/gaming/news/build-of-free-radicals-star-wars-battlefront-iii-leaked)
- [Wookieepedia — Star Wars Battlefront: Elite Squadron](https://starwars.fandom.com/wiki/Star_Wars_Battlefront:_Elite_Squadron)
- [GameFAQs — How do you destroy capital ships? (Elite Squadron)](https://gamefaqs.gamespot.com/boards/960345-star-wars-battlefront-elite-squadron/55868344)
- [ModDB — Star Wars Battlefront III Legacy mod](https://www.moddb.com/mods/star-wars-battlefront-iii-legacy)
- [Retroware — Inside the mod bringing Battlefront III back to life](https://articles.retroware.com/2022/01/18/inside-the-mod-bringing-battlefront-iii-back-to-life/)

## Roadmap

- AI pathing on imported SWBF2 planning/path data (`PhxPath`/`SWBFPath`)
  instead of straight-line movement; AI boarding parties tasked by the
  director once shields drop.
- Full seat/camera possession for the hangar turret stations (`PhxSeat`
  integration) instead of stand-to-man auto control.
- Swing animations + blade trail visuals for `PhxMeleeWeapon`, and saber
  blocking/deflection.
- Optional AI-upscaled texture pack loading through the mod system for true
  4K-res assets.
- Remaining documented BF3 maps: Yavin 4, Hoth, Endor, Mustafar, Kashyyyk,
  Dathomir, Death Star II.
- Per-mod Lua API compatibility sweep for BF3 Legacy 3.1 mission scripts.

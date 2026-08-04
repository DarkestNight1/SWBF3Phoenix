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
| Ion cannon (ground↔space link) | Implemented | `CapitalShip/PhxIonCannon.cs` |
| Lightsaber dismemberment | Implemented (mesh extraction sever) | `Dismemberment/` |
| Modern AI (squads, flanking, difficulty tiers) | Implemented | `AI/` |
| Modern lighting (ACES, SSAO, SSR, volumetrics) | Implemented | `Graphics/PhxModernLighting.cs` |
| 4K graphics mode | Implemented | `Graphics/PhxResolutionManager.cs` |
| Mod support (load order, toggling, detection) | Implemented | `Mods/PhxModManager.cs` |
| BF3 map recreations | 6 greybox layouts, data-driven | `Maps/` |
| Vertical Battlefront (ground→space) | Space layer w/ capital ships per map | `Maps/PhxBF3MapBuilder.cs` |

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

- Wire `PhxDismemberment` into saber melee kill resolution once melee damage
  lands in `PhxSoldier`.
- AI pathing on imported SWBF2 planning/path data (`PhxPath`/`SWBFPath`)
  instead of straight-line movement.
- Capital ship interiors as boardable playspaces (BF3's ship-deck combat),
  with AI boarding parties tasked by the director.
- Seamless flight transition band between ground layer and space layer.
- Optional AI-upscaled texture pack loading through the mod system for true
  4K-res assets.
- Remaining documented BF3 maps: Yavin 4, Hoth, Endor, Mustafar, Kashyyyk,
  Dathomir, Death Star II.

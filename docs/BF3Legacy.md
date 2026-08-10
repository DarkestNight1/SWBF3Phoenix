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
| BF3 Legacy 3.1 pack support (components, modes, eras, maps) | Implemented | `Mods/PhxBF3LegacyContent.cs`, `Mods/PhxBF3LegacyCompat.cs` |
| BF2 install auto-detection + mod installer scripts | Implemented | `Mods/PhxGamePathDetector.cs`, `Tools/` |
| AI boarding parties, defense, vehicles, grenades, stuck recovery | Implemented | `AI/` |
| Vehicle AI: ground driving, flyer combat, capital ship attack runs | Implemented | `AI/PhxAIVehicleOperator.cs` |
| AI turret gunners (vehicle seats + ship stations) | Implemented | `AI/`, `CapitalShip/` |
| Runtime texture upscaling (bicubic + adaptive sharpen) | Implemented | `Graphics/PhxTextureUpscaler.cs` |
| Graphics fidelity (TAA, mip bias, PBR sky, probes, post) | Implemented | `Graphics/PhxGraphicsEnhancer.cs` |
| Player turret possession (camera + UI) | Implemented | `CapitalShip/PhxShipTurretStation.cs` |
| Per-map weather, storms, day/night | Implemented | `Weather/PhxWeatherSystem.cs` |
| Procedural animation modernization | Implemented | `Animation/PhxProceduralMotion.cs` |
| BF2 documentation compatibility audit | See [BF2Compatibility.md](BF2Compatibility.md) | — |
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
  (spawn with `MTC.SpawnAI<PhxBF3AIController>(...)`). Additional behaviors:
  - **Grenades/secondary**: skill-gated secondary-channel pulses against
    mid-range targets, on an 8–16 s cooldown.
  - **Vehicles**: for long approaches (>80 m) the AI mounts a nearby free
    friendly vehicle (via the soldier's normal `Enter` flow), steers it
    toward the objective with heading-error mouse input, and dismounts
    ~25 m out.
  - **Stuck recovery**: if the soldier wants to move but hasn't displaced in
    1.5 s, it jumps + sidesteps on a fresh vector and rotates its flank
    offset — no more grinding against geometry. Plus whisker raycasts that
    deflect around obstacles before contact.
  - **Boarding runs**: with a `BoardTarget` set, the unit musters, inserts
    into the enemy ship's hangar (simulated transport for now), then
    sabotages criticals room by room and finishes the reactor.
- `PhxAIDirector` — battlefield commander: builds squads, splits them into
  **attackers** (spread over capturable posts, some flanking), **defenders**
  (~1/3 hold owned posts, patrol and re-take them if lost) and — the moment
  an enemy capital ship's shields drop — **one boarding party**. Units
  mid-boarding-run are never reassigned by replans.
- `PhxAISkillProfile` — four difficulty tiers: **Classic** (2005 feel),
  **Veteran**, **Elite**, **Legendary** (fast reactions, tight aim, heavy
  flanking). Per-unit jitter keeps squads from acting in lockstep.

## Turret possession (player)

Walk up to a hangar turret console as a soldier of the ship's team and press
**E**: the camera possesses the external hull gun (`PhxCamera.Track`), mouse
aims, left mouse fires hitscan shots, **E** exits back to your soldier (whose
controller is restored). An on-screen prompt shows when in range, and a
reticle + control hint while possessed. AI soldiers still man consoles by
standing at them.

## Weather, time of day & atmosphere

`PhxWeatherSystem` applies a per-planet profile on every map load, all
generated at runtime (camera-following particle emitters, no assets):

| Planet prefix | Weather |
|---|---|
| `hot` (Hoth) | heavy driven snow, day |
| `kam` (Kamino) | storm: heavy rain + lightning flashes, dusk |
| `mus` (Mustafar) | falling embers/ash, night, thick haze |
| `dag` (Dagobah) | dense fog + ground mist, dusk |
| `fel` (Felucia) | drifting spores, dusk |
| `geo` (Geonosis) | dust storm, day |
| `tat` (Tatooine) | light blowing dust, day |
| `cor` (Coruscant) | night city with light rain |
| `myg` (Mygeeto) | light snow, dusk |
| `yav`/`end` | jungle/forest mist |
| space maps | clean vacuum, night lighting |

Unlisted maps get clear weather and a **deterministic day/dusk/night** roll
from the map name (same map always looks the same). Time of day rescales and
retints the map's directional lights; storms add double-strike lightning
flicker. Everything sits behind `Config.DynamicWeather`.

## Modernized animation feel

`PhxProceduralMotion` (auto-attached to every soldier when
`Config.ProceduralAnimation` is on) layers small additive motion on top of
the stock animation banks after the animator runs each frame: torso lean
into strafes, subtle breathing sway, and recoil kicks on shots with
exponential recovery — the standard modern-shooter procedural layer, kept to
a few degrees so the original SWBF2 animations stay recognizable.

## Vehicle AI (land, air, turrets)

`PhxAIVehicleOperator` drives vehicles through the **engine's own control
path** — it writes the same `PhxPawnController` fields a human produces
(`MoveDirection`, `mouseX/Y`, `Jump`, `ShootPrimary`), which `PhxSeat.Tick`
already consumes — so no vehicle internals are special-cased for AI.

- **Ground**: steers by signed heading error, slows for sharp turns, probes
  ahead and deflects around obstacles, and reverses out when wedged.
- **Air**: requests takeoff (`Jump`), then flies with yaw/pitch stick values
  derived from heading error, banks into turns via roll, climbs when terrain
  is dead ahead or altitude is low, and holds combat altitude when cruising.
- **Capital ship attack runs**: air AI prioritizes enemy capital ships —
  targeting the *externally* attackable criticals (engines) since internal
  systems need boarding. It dives in firing, breaks off at ~90 m, climbs out
  past the hull and comes around for another pass. With shields still up it
  attacks the hull, which drains them.
- **Gunners**: AI in a passenger seat never fights the driver for movement
  input — it only traverses and fires, driving the seat's pitch/yaw
  accumulators so the turret model actually moves, with skill-scaled aim
  error.
- **Ship hangar turrets**: defenders aboard a threatened friendly ship walk
  to unmanned `PhxShipTurretStation` consoles and hold them (state
  `ManTurret`), yielding immediately if boarders show up.

Soldier AI mounts up for long approaches, and will now specifically seek out
an aircraft when there's an enemy capital ship alive to kill.

## Texture upscaling

`PhxTextureUpscaler` + `PhxTextureUpscale.shader` enhance the original
game's textures at display time (nothing is modified on disk or
redistributed — these are the user's own files):

1. **Catmull-Rom bicubic** reconstruction to 2× or 4× (`UpscaleFactor`),
   which is what removes the blocky bilinear stretch of a 256² texture on a
   4K screen.
2. **Contrast-adaptive sharpening** (CAS-style) that restores micro-detail
   while clamping to the local min/max, so it doesn't ring on edges or
   amplify the source's compression artifacts. Normal maps skip this pass and
   are re-normalized instead.

Results stay as mipmapped `RenderTexture`s — **no GPU→CPU readback**, so a
texture costs two blits rather than a pipeline stall. Work is amortized
across frames, and a VRAM budget (`UpscaleBudgetMB`, default 1.5 GB) caps
total added memory; past the cap remaining textures stay native rather than
risking an allocation failure. Tiny textures, oversized ones and lightmaps
are skipped.

## Graphics fidelity

`PhxGraphicsEnhancer` covers what the lighting volume doesn't:

- **TAA** — the biggest single image-quality win here, because 2005-era
  alpha-tested geometry (fences, foliage, antennae) shimmers badly at 4K.
- **Negative mip bias (−0.5)** — with TAA resolving the aliasing, textures
  can be sampled sharper than 1:1. This is what makes the upscaled textures
  actually *read* as upscaled rather than just being bigger.
- **Physically Based Sky** — real Rayleigh/Mie scattering for correct
  horizons and dusk gradients instead of a flat skybox.
- **Realtime reflection probe** baked once per map, centered on the command
  posts, so specular surfaces reflect the actual map.
- **Cinematic post** — restrained motion blur, close-range depth of field,
  subtle vignette, chromatic aberration and fine film grain.
- **Shadow distance 500 m / 4 cascades / very high resolution** for the
  large SWBF2 maps, and **GPU instancing** enabled across loaded materials to
  buy back the frame time the rest of the stack spends.
- 5 km far clip so the vertical-battlefront capital ships stay visible.

Every step is independently try-guarded — HDRP's API surface moves between
versions, and a missing override must not take down the renderer.

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
  installed, `PhxModManager.IsBF3LegacyInstalled` is set and BF3 features
  light up on its maps. This is the preferred way to get *real* BF3 assets —
  the runtime loads them through the standard lvl pipeline. See
  [Running the BF3 Legacy 3.1 pack](#running-the-bf3-legacy-31-pack) for what
  that takes.

## Running the BF3 Legacy 3.1 pack

The current all-in-one release,
[Battlefront 3 Legacy Mod (3.1)](https://www.moddb.com/addons/battlefront-3-legacy-mod-31),
ships as **standard SWBF2 addon content** — the original 2005 game is
required, which is exactly the setup Phoenix expects (the user supplies their
own BF2 install).

### Installing

One command does the install *and* the editor setup:

```bash
.\Tools\install_mod.ps1 -ModPath "<extracted pack folder>"     # add -Link to avoid a 19 GB copy
./Tools/install_mod.sh  "<extracted pack folder>"              # or --link
```

It locates the BF2 install, copies (or links) all seven component folders into
`GameData/addon/`, names the components it recognized, and writes
`GamePathOverride` into `bf3legacy.json` — which is where the runtime looks
when the scene's `Game Path String` is empty. `.\Tools\install_mod.ps1 -Verify`
prints the whole picture: game path, required lvl files, installed components,
editor config.

From there `PhxGame` discovers every addon via its `addme.script`,
`PhxModManager` applies `modorder.txt` ordering, and the pack's maps load
through the normal lvl pipeline with the vertical battlefront layer over them.

You do **not** need the 1.3 patch or [GT]Anakin's UI Remaster that the pack's
readme asks for. Both exist to work around the retail executable — the 1.3
patch to make mod-defined modes selectable at all, the Remaster to fix the
doubled HUD and to supply the shell helpers described below. Phoenix
reimplements the engine and provides those helpers itself.

### What the pack is made of

It is not one mod but seven addon folders that cross-register content into
each other's maps — MoreMaps, for instance, adds Hero Deathmatch to the main
mod's Coruscant. `PhxBF3LegacyContent` knows all seven, so the log names them
instead of guessing from folder names:

| Folder | Component | Maps |
|---|---|---|
| `BF3` | Pre-Demo 3.0 (El_Fabricio) | Coruscant, Cato Neimoidia, Bespin |
| `BF3Era` | Era Mod 1.4 (iamashaymin) | BF3 eras/sides on the stock SWBF2 maps |
| `BF3MoreMaps` | MoreMaps Patch 1.9 | Dantooine, Death Star II, `MP*` ports, extra modes |
| `BF3GCWSpaceDemo` | GCW Space Demo | `SP3` space battle |
| `BF3Vjun` | Extended Engagements (bk2-modder) | Vjun, Sulon, Lucrehulk |
| `BF3Venator` | Venator (bk2-modder) | Venator conquest |
| `BF3Cato-Hunt` | Cato Neimoidia: Hunt (bk2-modder) | Cato Hunt mode |

Every add-on component depends on the main `BF3` folder for its sides and
scripts. If the others are present without it, `PhxModManager` logs a warning
(`BF3LegacyMissingBaseMod`) rather than letting maps load half-missing.

### The shell helpers, and why nothing loaded without them

Each `addme.script` in the pack calls two functions that stock Battlefront II
does not have:

- `MergeTables(dst, src)` — deep-merges mission list tables so one component
  can extend a map another component declared.
- `AddNewGameModes(...)` — declares the name, blurb and icon of modes and eras
  the shell doesn't ship with.

In the original engine both come from the UI Remaster's patched shell scripts.
Phoenix reimplements the shell in C#, so the Remaster's version is never loaded
even if it's installed — and an undefined-function error in `addme.script`
aborts it, which meant **no BF3 Legacy map reached the mission list at all**.

Verified against a retail GOG `shell.lvl`: it defines `missionlist_ExpandMaplist`
/ `ExpandModelist` / `ExpandEralist`, the entry fields `mapluafile`, `isModLevel`,
`showstr`, `subst`, `bIsWildcard`, the eras `era_c` / `era_g` / `era_v` and the
modes `con`, `tdm`, `ctf`, `1flag`, `hunt`, `eli`, `assault`, `xl` — and
contains no `AddNewGameModes`, no `MergeTables`, and no `mode_space`,
`mode_siege` or `mode_uber`. Stock space maps use subst `ass`
(`spa1g_ass`), so the pack's `mode_space` really is mod-only and does not
collide with anything.

`PhxBF3LegacyCompat` installs both as a small Lua prelude before any
`addme.script` runs (and again per map environment, for mission scripts that
use them). It defines them only if undefined, so a real implementation keeps
precedence. `AddNewGameModes`' argument shape is undocumented and differs
between components, so the shim walks whatever it is handed and picks up every
table keyed `mode_*` or `era_*`, forwarding the descriptors to
`PhxBF3LegacyContent` via `PhxBF3RegisterGameMode`.

### Modes and eras

A map entry declares its playable combinations as flags —
`mapluafile = "CO3%s_%s"` with `era_x = 1`, `mode_siege_x = 1` — which the menu
substitutes into the script name (`CO3x_siege`). Stock `missionlist_ExpandModelist`
only reports combinations the stock shell recognizes, so the pack's two eras
(`x` = BF3: Clone Wars, `y` = BF3: Galactic Civil War) and its `siege`
(Orbital Assault) and `uber` modes were dropped, leaving its maps selectable
but unplayable.

`PhxMainMenu` now reads the map entry's flags directly and appends anything the
stock expansion missed, using descriptors from `PhxBF3LegacyContent` — the
mod's own `AddNewGameModes` data where it provided some, built-in defaults
otherwise, and a generated name for a mode nobody has ever heard of. That last
fallback means this works for non-BF3 mods with custom modes too. Mod maps
without a localized name fall back to the content table's name instead of
showing a raw key.

### Vertical battlefront on pack maps

`PhxVerticalBattlefront` consults the content table before its stock prefix
list, because the pack's map ids are unknown to it and the "assume a planet
surface" fallback is wrong for a third of them:

| Maps | Layer |
|---|---|
| `CO3`, `CN3`, `BS3`, `DN3`, `VF3`, `SL3`, `MP*` | Ground — capital ships in atmosphere |
| `SP3` | Space — ships in orbit |
| `VEN`, `LUC`, `DS2` | Interior — no space layer (ship/station interiors) |

The Era Mod's entries reuse stock map ids, which the existing prefix table
already classifies correctly.

### Known limits

Phoenix still needs every 1.3-era Lua API the pack's *mission* scripts call to
exist in `PhxLuaAPI`; missing-function warnings on map load remain the
compatibility TODO list. The pack's own readme also notes that crashes are
frequent on the retail engine and that multiplayer hosting is unsupported —
neither is something this runtime inherits, but neither is it tested here.

## Debugging mods and maps

- **In the Unity editor**: open `Runtime/Scenes/PhxMainScene` and press Play.
  Both inspector fields on the *Game* object stay empty — `PhxFirstTimeEditorSetup`
  imports the HDRP particle sample on first load and logs the game path the
  runtime resolved, and the installer scripts wrote that path into
  `bf3legacy.json` already. Everything logs to the Console: addon registration
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

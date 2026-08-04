# BF2 (2005) compatibility review

A code audit of the Phoenix runtime against the known SWBF2 mod tools
documentation ([odf guide / parameters / weapon notes](https://sites.google.com/site/swbf2modtoolsdocumentation/)),
with the fixes this fork applied. Status legend: OK = matches documented
behavior, FIXED = corrected in this fork, TODO = known gap.

## ODF ClassLabel coverage (`PhxClassRegister`)

| ClassLabel | Status | Notes |
|---|---|---|
| `prop`, `animatedprop`, `leafpatch` | OK | upstream |
| `door` | OK | `AnimationName`/`Animation`/`AnimationTrigger` (node + radius) are read and drive a trigger-opened CraPlayer animation, matching the documented door odf contract. Trigger uses the SoldierAll layer, doors auto-close when empty. |
| `destructablebuilding`, `armedbuilding` | OK | upstream |
| `commandpost`, `hologram` | OK | capture/neutralize times, sounds per odf |
| `soldier` | FIXED | health/death: `AddHealth` previously stopped at `TODO: dead!`; soldiers now implement `IPhxDamageableInstance`, die, and clean up. WEAPONSECTION channels honored. |
| `hover`, `commandhover`, `flyer`, `commandflyer` | OK | upstream (walkers still TODO, below) |
| `vehiclespawn`, `powerupstation` | OK | upstream |
| `weapon`, `launcher`, `cannon` | OK | both documented spread systems (PitchSpread/YawSpread and SpreadPerShot/Recover/Threshold/Limit per weapon_notes) implemented upstream |
| `grenade` | OK | upstream |
| `melee` | FIXED | previously unregistered — lightsabers simply did not function. Now `PhxMeleeWeapon`: arc sweep, `MaxDamage`, `LightSaberLength/Width/Texture`, `ComboAnimationBank` accepted (combo state machine itself still TODO). |
| `bolt`, `bullet`, `beam`, `missile`, `shell`, `sticky` | OK/FIXED | bolt damage was hardcoded `100f`; now uses ordnance `MaxDamage` with `PersonScale`/`VehicleScale`/`BuildingScale` per the documented damage-scale properties. Impact effects per surface type (`ImpactEffectStatic/Rigid/Soft/Terrain/Water/Shield`) were already wired. |
| `explosion` | OK | upstream |
| `walker`, `commandwalker` | FIXED | were unregistered — no AT-ST/AT-AT/AT-TE/spider droid spawned on any map. `PhxWalker` parses `WALKERSECTION` seats/weapons and ground-follows terrain (leg animation still TODO). |
| `turret` | FIXED | was unregistered — no emplaced gun spawned on any map. `PhxTurret` handles `TURRETSECTION` and the `BUILDINGSECTION/TURRET1` variant. |
| `TURRETSECTION` in `PhxSeat.InitManual` | FIXED | the header check was **commented out**, so a turret seat's property scan never terminated and consumed the following sections' properties. |
| `PhxSeat.GetController()` | FIXED | only existed on the flyer/hover main sections, so no other seat type could read its occupant's input. Lifted to `PhxSeat`. |
| `remoteterminal`, `animatedbuilding`, `mine`, `beacon`, `repair` | TODO | see [BF2DataFeatures.md](BF2DataFeatures.md) |

## AI data (planning graph, barriers, hint nodes)

Previously unused entirely — see [BF2DataFeatures.md](BF2DataFeatures.md) for
the full write-up. Summary: `PlanSet` (hubs/arcs with unit-size filters),
`World.GetBarriers()` and `World.GetHintNodes()` are now imported, giving A*
pathfinding over the designers' authored routes and tactical cover/snipe
positioning. The `DisableBarriers` / `EnableBarriers` /
`BlockPlanningGraphArcs` / `UnblockPlanningGraphArcs` Lua stubs are now live.

## Vehicles

| Issue | Status | Detail |
|---|---|---|
| Vehicles were invulnerable | FIXED | `PhxVehicle` never implemented `IPhxDamageableInstance`, so every ordnance hit on a vehicle was silently discarded — nothing in the game could destroy one. It now takes damage against `CurHealth`/odf `MaxHealth`, ejects all occupants on death, fires `OnDeath` and despawns. |
| AI stole the player's camera | FIXED | `TryEnterVehicle`, `TrySwitchSeat` and `Eject` called `CAM.Track()`/`CAM.Follow()` unconditionally, so whenever *any* AI soldier mounted, switched seats or exited a vehicle, the local player's view snapped to that bot. Camera changes are now gated on the occupant actually being the player's pawn. |
| `Eject` index check | FIXED | The guard was `if (i < Seats.Count \|\| Seats[i] != null \|\| ...)` — an OR chain, so an out-of-range index fell through to `Seats[i]` and threw `IndexOutOfRangeException` instead of returning false. Now AND-ed. |
| Seat aiming was camera-only | FIXED | `PhxSeat.Tick` derived its weapon target from a raycast relative to `CAM.transform.position`, which is meaningless for an AI occupant. Seats now accept an `AimOverride` that AI sets; the player path is unchanged. |

## HDRP / build-safety audit

Issues found reviewing the BF3 Legacy code against how HDRP and player builds
actually behave (all fixed; helpers live in `PhxRuntimeAssets`):

| Issue | Why it mattered |
|---|---|
| Runtime lights had no `HDAdditionalLightData` | HDRP drives punctual lights through that component. Every light created from script — explosions, cauterize glow, ship room/subsystem lights, command post markers, the map sun, lightning — risked **not rendering at all**. All now go through `PhxRuntimeAssets.CreatePointLight/CreateDirectionalLight`, which adds the component and sets intensity in HDRP's physical units (Lumen / Lux) rather than builtin-pipeline values. |
| `Light.intensity` writes during fades | Under HDRP the effective intensity lives on `HDAdditionalLightData`, so explosion/glow fades and the day-night relight silently did nothing. Fades now go through `SetIntensity` / `ScaleIntensity`. |
| `material.color` on HDRP/Lit | HDRP/Lit exposes `_BaseColor`, not `_Color`; `material.color = x` was a no-op, so greybox tinting would have rendered default-grey. All assignments now route through `PhxRuntimeAssets.SetColor`, which writes whichever property exists. |
| `Shader.Find("Sprites/Default")` | Shaders referenced only from script can be stripped from player builds, yielding null materials (invisible/magenta line effects). Now a cached multi-candidate lookup with a primitive-material fallback. |
| Turret `E` key collided with vehicle enter | `E` is already the soldier's vehicle enter/exit key in `PhxPlayerController`. Manning a hangar turret beside a vehicle could trigger both; the possession path now consumes the input. |
| Per-frame `OverlapSphere` in turret stations / escape pods | Several instances per ship each ran a physics query every frame. Now throttled (0.5 s / 0.25 s), with the boarding timer accumulating the throttle interval rather than a frame delta. |
| AI boarding teleport wrote `transform.position` | Writing a transform directly on a physics body desyncs the rigidbody and can tunnel through the hull. Now zeroes velocity and sets `Rigidbody.position`. |
| Procedural animation could accumulate | The additive spine offset multiplied onto the bone each frame assuming the animator had reset it. If it hadn't (paused, culled, or a bank that doesn't drive the spine) the rotation would spin up without bound. The layer now explicitly removes its previous contribution first, so it can never accumulate. |
| Mod scan ran before the game path was known | `PhxModManager.Scan()` at bootstrap could find nothing and never retry, leaving the mod list permanently empty. Added `EnsureScanned()`, called on map load. |

## Damage model

- Ordnance→target damage now flows through odf values end to end
  (weapon `OrdnanceName` → ordnance `MaxDamage` × per-target scale).
- `AddDamage` reaches any collider hierarchy (`GetComponentInParent`), so
  compound objects (vehicles, buildings, capital ship subsystems) receive
  hits even when the struck collider is not on the rigidbody root — the
  previous code required a rigidbody-rooted `PhxInstance` and silently
  dropped damage otherwise.
- Health regen / bleed (`AddHealth` positive path) clamps at `MaxHealth`
  per odf.

## Mission Lua / addon pipeline

- `addme.script` discovery and `ScriptInit`/`ScriptPostLoad` execution match
  the stock game's addon contract; the BF3 Legacy 3.1 pack loads through it.
- Unknown-map handling (vertical battlefront, weather) keys off the
  `mapluafile` name only — no assumptions that break custom-named addon maps.
- Missing `PhxLuaAPI` functions log warnings instead of hard-failing, so
  partially supported mods still boot. Sweeping the BF3 Legacy mission
  scripts for unimplemented calls remains the active compatibility TODO.

## Known intentional deviations

- BF3 Legacy features (capital ships, weather, dismemberment, AI director)
  are additive layers gated behind `bf3legacy.json` toggles — with every
  toggle off, the runtime behaves like upstream SWBF2 Phoenix.
- The 1.3 community patch's HUD fixes target the original executable and are
  not applicable; Phoenix renders its own UI.

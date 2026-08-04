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
| `walker`, `commandwalker` | TODO | not registered; AT-ST/AT-AT etc. won't spawn |
| `turret`, `remoteterminal` | TODO | map-placed turret odfs not registered (BF3 Legacy ship turrets are separate) |
| `animatedbuilding`, `mine`, `beacon`, `repair` | TODO | |

## Vehicles

| Issue | Status | Detail |
|---|---|---|
| Vehicles were invulnerable | FIXED | `PhxVehicle` never implemented `IPhxDamageableInstance`, so every ordnance hit on a vehicle was silently discarded — nothing in the game could destroy one. It now takes damage against `CurHealth`/odf `MaxHealth`, ejects all occupants on death, fires `OnDeath` and despawns. |
| AI stole the player's camera | FIXED | `TryEnterVehicle`, `TrySwitchSeat` and `Eject` called `CAM.Track()`/`CAM.Follow()` unconditionally, so whenever *any* AI soldier mounted, switched seats or exited a vehicle, the local player's view snapped to that bot. Camera changes are now gated on the occupant actually being the player's pawn. |
| `Eject` index check | FIXED | The guard was `if (i < Seats.Count \|\| Seats[i] != null \|\| ...)` — an OR chain, so an out-of-range index fell through to `Seats[i]` and threw `IndexOutOfRangeException` instead of returning false. Now AND-ed. |
| Seat aiming was camera-only | FIXED | `PhxSeat.Tick` derived its weapon target from a raycast relative to `CAM.transform.position`, which is meaningless for an AI occupant. Seats now accept an `AimOverride` that AI sets; the player path is unchanged. |

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

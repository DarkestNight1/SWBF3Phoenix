# Advanced features from original BF2 data

A survey of what Star Wars Battlefront II (2005) actually ships in its data
files, what this fork now consumes, and what remains. Nothing here adds game
content — it uses data already present in the user's own installation that the
runtime previously ignored.

The starting point was a blunt measurement: **169 of 218 `PhxLuaAPI` functions
were empty stubs**, and only 2 of the available `EConfigType` categories
(Lighting, Skydome) were ever read. Maps were calling into a runtime that
silently did nothing.

## Implemented in this pass

### AI navigation — the planning graph (`PhxNavGraph`)

Every SWBF2 map ships a hand-authored navigation graph, built in ZeroEditor's
PLANNING mode and exposed by LibSWBF2 as `PlanSet`:

- **Hubs** — named waypoints with a position and radius.
- **Connections** — arcs between hubs carrying
  `EArcFilterFlags` (Soldier / Small / Medium / Hover / Large / Huge — which
  unit sizes may use the arc) and `EArcAttributeFlags` (OneWay, Jump,
  JetJump), plus per-hub quantized branch weights.

We now import it and run A* over it, filtered by unit size, so AI follows the
routes level designers actually authored instead of walking into walls. Arcs
are bidirectional unless flagged `OneWay`.

`PhxBF3AIController.MoveTowards` paths over the graph for goals beyond 15 m and
falls back to direct steering for short hops or when a map has no graph, so
behaviour degrades gracefully rather than stalling.

### Barriers

Maps also ship oriented "AI keep out" boxes. These are imported and respected
during pathfinding, and the previously-empty Lua hooks now work:

| Lua call | Now does |
|---|---|
| `DisableBarriers(name)` / `EnableBarriers(name)` | toggles barrier volumes |
| `BlockPlanningGraphArcs(name\|index)` | blocks arcs at a hub |
| `UnblockPlanningGraphArcs(name\|index)` | restores them |

Scripted map events (a door opening a route, a bridge collapsing) therefore
change AI routing the way the original scripts intended.

### Hint nodes — tactical AI (`PhxHintNodes`)

Designers annotated every map with tactical positions. Per the mod tools
documentation the types are **SNIPE, PATROL, COVER, ACCESS, JETJUMP, MINE,
LAND**, each with properties (attack/defend mode, stand/crouch/prone posture,
an owning command post).

AI in combat now looks for an authored position instead of strafing randomly:
marksmen at range seek `SNIPE` posts, everyone else seeks `COVER`, and nodes
are scored by whether their authored facing actually points at the threat — a
cover node facing away is rejected. Nodes are claimed so two bots don't fight
over one, and released when the fight ends. Posture follows the node.

> **Type numbering caveat.** LibSWBF2 exposes the raw `uint16` from the Hint
> chunk. The mod tools document the type *names* and their editor order but not
> the on-disk numeric values, so `PhxHintNodes.TypeFromRaw` follows that
> documented order. Unrecognised values are logged once and treated as
> `Unknown`. If nodes come through mis-typed on real maps, correct that one
> function — everything downstream keys off the enum, not the raw number.

### Stock space assault → real capital ships (`PhxSpaceAssault`)

Stock space maps configure themselves through Lua that was previously stubbed:

```lua
SpaceAssaultEnable(true)
SpaceAssaultAddCriticalSystem("ATT_shieldgenerator", points, hudX, hudY)
SpaceAssaultLinkCriticalSystems(...)
AddSpaceAssaultDestroyPoints(killer, instName)
```

Those critical-system names refer to **real authored instances** in the map —
actual Star Destroyer / Republic cruiser geometry with real hardpoints. We now
resolve each name to its scene instance, attach `PhxCapitalShipSubsystem`, and
group systems onto a per-team `PhxCapitalShip` (stock maps prefix instances
`ATT_`/`DEF_`). Subsystem role is inferred from the authored name
(shield/engine/bridge/reactor/comm/life support).

The result: stock space maps get the full BF3 Legacy capital ship behaviour —
shield gating, reactor progression, boarding, staged destruction — driven by
original game content rather than greybox stand-ins. `PhxVerticalBattlefront`
detects this and stands down so the two systems never both spawn ships.

### ODF class coverage

| Class | Was | Now |
|---|---|---|
| `turret` | unregistered — **no emplaced gun on any map spawned** | `PhxTurret`, handling `TURRETSECTION` (and the `BUILDINGSECTION/TURRET1` variant) |
| `walker` / `commandwalker` | unregistered — **no AT-ST/AT-AT/AT-TE/spider droid spawned** | `PhxWalker`: `WALKERSECTION` seats, weapons, terrain-following movement |
| `TURRETSECTION` parsing | **commented out** in `PhxSeat.InitManual`, so a turret seat's property scan never terminated and swallowed following sections | fixed |
| `PhxSeat.GetController()` | only existed on flyer/hover main sections | lifted to `PhxSeat` so every seat type can read its occupant's input |

> **Walker caveat.** SWBF2 walkers are animation-driven (leg cycles with foot
> planting). Reproducing that needs the walker animation banks driven by travel
> speed, which is its own piece of work. `PhxWalker` currently ground-follows
> with tank steering: walkers spawn, carry troops, aim and fire correctly, but
> legs slide rather than step.

## Still available — prioritized next steps

1. **Walker leg animation + foot IK** — the one visible gap in what now spawns.
2. **Game modes from stubbed API** — CTF, Hunt, Assault and campaign
   objectives are all driven by `AddMissionObjective` / `ActivateObjective` /
   `CompleteObjective` / `ScriptCB_GetCTF*` / `AddAIGoal`, still stubs.
   Highest remaining gameplay value per unit of work.
3. **`.combo` files** — BF2's hero combo system: the real path to lightsaber
   combos, deflection and force powers, replacing our single-swing melee.
   `ComboAnimationBank` is already parsed off the ODF.
4. **Destruction chunks** — `ChunkGeometryName` / `ChunkNodeName` /
   `ChunkPhysics` etc. are already parsed into `PhxVehicleProperties` and
   `PhxChunk` exists; wiring them would replace primitive debris with the
   authored chunk meshes.
5. **Native `.fx` effects** — replace our primitive explosions/ion beam with
   the game's own particle definitions (`PhxEffectsManager` already loads them).
6. **Remaining ODF classes** — `mine`, `beacon`, `remoteterminal`,
   `animatedbuilding` (droideka shields, orbital strike beacons, sabotage
   terminals).
7. **Sound/music configs** — dynamic combat music, per-map ambience,
   surface-dependent footsteps; sound loading is currently minimal.
8. **World config extras** — water, foliage and wind settings, currently
   unimported (Kashyyyk / Felucia / Naboo).
9. **Hint node types beyond cover/snipe** — `PATROL` idle behaviour, `JETJUMP`
   for jet troopers, `MINE` for engineers, `LAND` for AI transports.
10. **Branch weights** — `PlanSet.GetBranchWeights` is available and would let
    AI prefer the routes the designers weighted, not just the shortest path.

## Audit pass — issues found against the documentation and fixed

Re-checking the first navigation implementation against the mod tools docs and
the LibSWBF2 C++ chunk parsing turned up several real defects:

| Issue | Source of truth | Fix |
|---|---|---|
| **Barriers ignored their size filter.** Every barrier blocked every unit. | Docs: barrier flags are SOLDIER/HOVER/SMALL/MEDIUM/HUGE — "the same flags set in the editor when creating the barriers or the path planning graph", and "AI cannot chart a course to a command post within a barrier filtering out that AI type". | `Barrier.Flag` is now read and matched against the unit's size class, so a vehicle barrier no longer stops infantry. `IsBlockedByBarrier` takes the size. |
| **AI always pathed as SOLDIER.** Tanks used infantry routes. | Docs: `AISizeType` in the ODF "is the size category that the soldier/vehicle will use when referencing the connectivity graph and new barrier system", defaulting to SOLDIER. | Soldier AI reads `AISizeType` off its class (re-evaluated when the pawn changes); ground vehicle AI reads it off the vehicle class and now paths over the graph too. |
| **Space assault config was wiped before use.** | `PhxEnvironment.RunMain()` executes the map's `ScriptInit` at stage ExecuteMain; `CreateScene()`/`Import()` runs later. | `PhxSpaceAssault.Reset()` moved out of `Import()` into the `PhxScene` constructor, which runs before any Lua. Without this, `SpaceAssaultEnable`/`AddCriticalSystem` results were discarded and the feature could never work. |
| **Isolated hubs were selectable as path start/goal**, guaranteeing pathfinding failure. | — | `FindNearestHub` now skips any hub with no arc traversable by that unit size. |
| **Destroyed pawns were never pruned.** | Unity's overloaded `== null` does not apply to *interface* references, so `Pawn == null` stayed false for a destroyed object. | Staleness checks go through `GetInstance()` (a MonoBehaviour) in both `PhxAIDirector` and the controller's `Tick` guard — the latter would otherwise throw on every tick after a soldier died. `PhxAIDirector.ResetAll()` is called on map load. |
| **Capital ships leaked across maps.** | Ships and the transition band are root objects, not children of a world root, so map teardown didn't destroy them. | Both `PhxVerticalBattlefront` and `PhxSpaceAssault` now destroy what they created. |
| Premature binding logged a warning per critical system. | — | `ResolveAll` waits for `PhxEnvironment.IsLoaded`, and probes with the silent `GetInstanceIndex` instead of the warning-emitting `GetInstance<T>(name)`. |

### Remaining assumption to verify on real maps

Barrier flag *polarity*. The documentation says a barrier "filters out" listed
AI types, which we read as **set bit = that size is blocked**. A flag of `0`
carries no filter information and is treated as blocking everything
(conservative). Both readings live in one small method
(`PhxNavGraph.PhxBarrier.Blocks`) so this is a one-line flip if real maps show
the opposite. Symptom to watch for: AI either walking straight through
obvious keep-out zones (polarity inverted) or refusing to path at all near
barriers (flag 0 handling too strict).

## Verification status

All of the above is written against the LibSWBF2 wrapper surface as it exists
in the pinned submodule (`PlanSet`/`Hub`/`Connection`, `World.GetBarriers()`,
`World.GetHintNodes()`, `Level.Get<PlanSet>()`), and world-space data uses the
same `Vec3FromLibWorld`/`QuatFromLibWorld` conversions the region importer
uses. It has **not** been run through a Unity compile or against real map
files — the first editor session is where hint-node numbering and barrier
extents should be checked against actual maps.

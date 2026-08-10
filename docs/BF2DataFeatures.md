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

### The semantic source layer (`Assets/Runtime/SourceData`)

Until now the Unity scene was the *only* surviving representation of a map:
once an instance had become a GameObject, the odf it came from, the properties
it overrode and the fact that four hundred others shared its class were gone.
Anything wanting to re-render the world differently, validate that an import
was complete, diff a mod against stock data or report what a map contains had
to re-read the LVL.

`BFSourceDatabase` now captures the authored data into immutable typed records
before anything is converted — `BFWorldDefinition`, `BFInstanceDefinition`,
`BFEntityClassDefinition`, `BFRegionDefinition`, `BFBarrierDefinition`,
`BFHintNodeDefinition`, `BFTerrainDefinition`, `BFLightDefinition`,
`BFPathDefinition`, `BFPlanningHub/ArcDefinition` — each addressed by a
`BFSourceRef` (level : world : kind : name # ordinal; the ordinal is what makes
the many unnamed instances in a stock world individually addressable).

Every Unity object built from one carries a `BFSourceLink` back to its record,
so the scene is a *view* of the map rather than a copy of it. `PhxNavGraph` and
`PhxHintNodes` were converted to read the records rather than walking the
native wrappers a second time, which removes the second place for the
coordinate conventions and hub-index arithmetic to drift.

### Import validation harness

`BFImportDiagnostics` counts every name the loaders try to resolve and whether
they found it; `BFImportValidator` compares that and the source database
against what was actually built and writes
`bf2-import-validation-<world>.json` on every load, plus a console summary and
a **Phoenix → Import Monitor** editor window.

Per category (instances, regions, barriers, hint nodes, lights, terrain,
planning hubs/arcs, paths, models, materials, textures, sounds, effects,
configs) it reports imported vs authored, lists every unresolved reference with
what needed it, lists odf classes whose root base class has no runtime type,
and lists objects dropped by exceptions. "Did this map import completely?" is
now a question with an answer.

## Still available — prioritized next steps

1. **Game modes from stubbed API** — CTF, Hunt, Assault and campaign
   objectives are all driven by `AddMissionObjective` / `ActivateObjective` /
   `CompleteObjective` / `ScriptCB_GetCTF*`, still stubs. Highest remaining
   gameplay value per unit of work.
2. **Native `.fx` effects** — replace our primitive explosions/ion beam with
   the game's own particle definitions (`PhxEffectsManager` already loads them).
   The effects loader still has documented unfinished emitter-velocity,
   modifier and blend-mode behaviour.
3. **Jet troopers** — nothing implements a jetpack, so `PhxBF3AIController`
   declares `PhxNavCapabilities.Infantry` and the planning graph's JETJUMP arcs
   are excluded from AI routes (a soldier routed over one walks to the lip of
   the gap and stops). Implementing the jetpack turns those authored arcs on
   with a one-line change.
4. **Hero animation** — the combo state machine drives timing, damage windows
   and chaining from the `.combo` data, but `PhxComboMove.AnimationName` is not
   yet played; swings are mechanically authored and visually generic.
7. **Sound/music configs** — dynamic combat music and surface-dependent
   footsteps. Script-driven music and voice-over now play (`PhxMusicManager`:
   ambient/victory/defeat music, broadcast VO, bleeding and low-reinforcement
   lines); what remains is combat-state-driven music and footstep surfaces.
8. **World config extras** — water, foliage and wind settings, currently
   unimported (Kashyyyk / Felucia / Naboo). Fog, sun and dome ambient from the
   `.sky` config *are* now imported and applied per map (`PhxMapAtmosphere`).
8b. **Terrain baked vertex colour — done, luma-only, via a code path.** The
   original terrain lighting/AO is exported from the native library
   (`Terrain_GetColorBuffer`) and written onto the terrain mesh
   (`WorldLoader.BuildTerrainMesh` → `mesh.colors32`). Rather than a Shader
   Graph edit, `WorldLoader.BuildBlendLightingScale` maps each baked vertex
   colour's luma onto the matching blend-map texel and darkens all four
   generated blend textures by it in `ImportTerrainAsMeshHDRP` - since the
   shader computes `sum(layerColour_i * blendWeight_i)`, darkening every
   layer's weight by the same factor darkens the final colour by that factor
   too, with no shader-graph edit at all. This is necessarily luma-only: blend
   channels are layer weights, not colour, so any tint in the baked data is
   lost.
   Investigating this also turned up that the shader graph was **not just
   ignoring vertex colour but structurally broken**: `SWBFTerrainHDRP.shadergraph`'s
   `BlendTerrainLayers` custom-function node referenced its HLSL by a GUID
   that didn't match `BlendTerrainLayers.hlsl.meta` (the meta was untracked,
   caught by the repo's `*.meta` ignore rule, so a fresh clone minted a new
   GUID every time). ShaderGraph silently drops a node whose source doesn't
   resolve, so terrain rendering was undefined, not merely flat. Fixed by
   restoring the expected GUID and force-tracking the meta (same fix shape as
   the earlier "Repair the terrain material's dangling shader reference"
   commit, which caught one dangling GUID and missed this second one).
   **Follow-up, full-colour version:** insert a `VertexColor` node and a
   `Multiply` node into the shader graph between the custom-function output
   and `BaseColor` - this is a normal, safe Shader Graph edit once opened in
   the editor (add two nodes, drag two wires), not something to hand-edit in
   the serialized JSON. **If done, remove the code-path darkening above
   first** — applying both multiplies the baked lighting in twice and terrain
   goes too dark.
   Per-layer tiling (`TERR.TileRange`, currently the hardcoded
   `modulo = 24` in `BlendTerrainLayers.hlsl`) would need the same kind of
   graph edit: a new input wired to a per-layer tile-scale value.
9. **Hint node types** — done except JETJUMP (see 3 above). Defenders walk
   authored `PATROL` routes, engineers mine `MINE` positions, AI transports put
   down at `LAND` spots rather than on the objective.
10. **Branch weights** — done. Decoded per A* expansion from the hub's
    quantized table rather than through the native helper's per-call name
    search, and applied as a cost multiplier in `[1, 2]` so the straight-line
    heuristic stays admissible. Layered on top: dynamic tactical costs, fed by
    `PhxAIDanger` kill sites, which bias routes without ever blocking them —
    the authored graph stays the authority on what is reachable.

## The presentation layer (Phase 1)

`Assets/Runtime/Presentation` is a layer *downstream* of libswbf2 that never
reaches back up it. No stock mesh, texture, map, character or weapon is
replaced; nothing here alters a gameplay value, an odf property or a source
record to make something look better.

```
original files -> libswbf2 -> source data -> presentation -> HDRP
```

- **`BFEnvironmentLightingProfile`** — sun, sky, atmosphere, fog, volumetrics,
  ambient, exposure, shadows, reflections and GI per planet, resolved from the
  mission script's three-letter prefix. Hoth, Geonosis, Endor, Tatooine,
  Mustafar, Kamino, Coruscant, Naboo, Kashyyyk, Felucia, Mygeeto, Utapau,
  Yavin, Dagobah, plus interior and space archetypes. Unknown prefixes (mods)
  fall through to a neutral default, so nothing regresses.
- **`BFLightingDirector`** — applies the profile at volume priority 120, above
  the generic modernization (100) and the map's own authored atmosphere (110).
  Authored sun angle and fog range still win unless the profile says the map
  cannot have meant one (interiors, space). The map's sun is *retuned*, not
  replaced, so the level designer's placement survives.
- **`BFSurfaceType` / `BFSurfaceProfile` / `BFSurfaceQuery`** — thirteen
  semantic surfaces, and a resolver that derives them from an explicit tag, the
  terrain blend map, the artists' texture-naming vocabulary, or the collision
  layer. `BFTerrainSurfaceMap` collapses the blend map to one surface per texel
  at import, which is how the same Hoth mesh reads as snow in the valley and
  rock on the ridge.
- **`BFSurfaceInteraction` / `…System` / `BFInteractionReceiver` /
  `BFInteractionProfile`** — one channel for every footstep, impact, explosion,
  splash and weather event, with distance and cooldown budgets applied
  centrally rather than per caller.
- **`BFImpactResponse`** — weapon → hit → surface query → surface-specific
  response. Sparks fly along the reflected direction so grazing hits read as
  ricochets; flash colour comes from the surface, not the bolt.
- **`BFImpactLightPool` / `BFDecalSystem`** — fixed pools with hard budgets.
  Impact lights are shadowless, sub-tenth-of-a-second, and steal the
  nearest-to-expiring when the budget is spent. Decals are HDRP projectors, so
  a scorch mark never edits a stock texture.
- **`BFMaterialEnhancer`** — derives normal, occlusion, smoothness and gated
  metallic from the stock diffuse via luminance gradient, local darkness and
  local contrast. Base colour passes through untouched; only light behaviour
  changes.
- **`BFReflectionProbeManager` / `BFLightProbeManager`** — probes at command
  posts (where the fighting is, per the level designers), and probe/shadow
  configuration pushed onto imported renderers so characters are lit by their
  surroundings instead of a global average.
- **`BFTerrainInteractionSystem`** — footprints, tracks and craters written to
  a render-texture mask (depth / rim / compaction) that heals over time. The
  terrain mesh is never modified. Surfaces that do not deform never write to it.
- **`BFWaterSystem` / `BFWaterSurface` / `BFWaterRippleSimulation` /
  `BFUnderwaterVolume`** — takes over the map's own water planes; depth
  colouring, ripples from anything entering, underwater fog and grading, and
  planar reflections only for bodies large enough to earn a scene re-render.
- **`BFWetnessSystem` / `BFSnowAccumulation`** — global scalars published as
  shader uniforms (so no imported material is touched), gated per surface:
  snow does not get shiny in rain, lava never gets wet or covered, and snow
  only settles on slopes shallow enough to hold it.
- **`BFPresentationQuality`** — one tier driving every budget: decals, impact
  lights, probe count, deformation resolution, interaction distance, and which
  HDRP features are on at all.

### Phase 1 status

The architecture, budgets and every system above are implemented and wired.
Two things are deliberately left as shader-side work, because they are edits to
`SWBFTerrainHDRP.shadergraph` rather than code: the terrain material does not
yet *read* `_BFTerrainDeformation`, `_BFSnowCoverage` or `_BFWetness`. Until
those wires exist the masks are computed, budgeted and correct but not visible
on the terrain — everything that does not depend on the terrain graph
(lighting, impacts, decals, lights, probes, water, underwater, material
derivation) is visible now. `BFMaterialEnhancer` is also not yet called from
`MaterialLoader`; it is safe to enable per surface category once there is a
play session to judge it against.

## AI enhancement (Phase 2)

- **`BFAimProfile` / `BFAimState`** — aim error decomposed into causes that
  behave differently over time: a large settling error on acquisition that
  decays, tracking error proportional to the target's angular speed across the
  view, range error beyond the soldier's competent range, and burst walk that
  resets between bursts. Error is drawn biased toward the centre of the cone,
  so grouping looks like a person's. **No tier has perfect aim**, including
  Elite.
- **`BFAIDecision`** — replaces "enemy detected → shoot enemy" with a scored
  choice across attack, flank, seek cover, pursue, retreat, reload, defend,
  capture, advance and regroup, including an explicit "can I win this fight?"
  read. The chosen action is drawn from among the good ones, with the width of
  that band set by difficulty — which is where controlled imperfection lives:
  recruits take the wrong reasonable option often, elites rarely but not never.
- **`BFAIObjectiveEvaluator`** — scores every command post per team from who
  holds it, who is standing on it, how central it is to the map, and how badly
  the team is losing on reinforcements. Squads are handed objectives off the
  top of that ranking, so a losing team consolidates and a winning one pushes.
- Difficulty changes decision quality, not just accuracy, and **defaults to
  Hard** pending a selector on the map/mode screen.

## Second pass — what this fork added on top

| Area | Was | Now |
|---|---|---|
| Source data | Unity scene was the only surviving representation | typed immutable records + `BFSourceLink`; nav/hints read the records |
| Import completeness | hundreds of individually-survivable, individually-invisible failures | per-category counts, unresolved-reference list, JSON report, editor window |
| `building` / `animatedbuilding` | unregistered — geometry with no instance behind it | `PhxProp` / `PhxAnimatedProp` |
| `mine` / `beacon` / `remoteterminal` | unregistered — no minefields, no beacons, no usable terminals; objectives written around them could never complete | implemented |
| Destruction chunks | parsed, `PhxChunk` commented out in its entirety | `PhxChunkSpawner` + `PhxChunk`: authored geometry or the dying model's own nodes, launch, spin, trail/smoke, terrain-hit effects, settle |
| Destructible buildings | `AddDamage` ignored the damage and toggled health, so one rifle round levelled a bunker and the next hit rebuilt it | subtracts damage against the authored MaxHealth |
| Vehicle death | no blast at all | odf `ExplosionName` plus authored break-up |
| Damage model | three private `CurHealth` fields, three destruction paths, capital ships bolted alongside | `IPhxDestructible` + `PhxDestructionRegistry`, shared by vehicles, buildings, ship subsystems and mission objects |
| Walkers | terrain-following tank steering; legs slid | leg cycle driven by distance travelled, feet probed onto what is under them, hull carried on the feet |
| Heroes | `SetHeroClass` stored an odf nothing read; melee was one arc sweep | `.combo` state machine, saber deflection (returned to sender), five force powers on the stamina bar, and both hero rule sets wired to their Lua entry points |
| Mission AI directives | `SetAIDifficulty`, `AllowAISpawn`, `SetAttackingTeam`, flight ceilings, flyer paths, aggressiveness, dense environment, blind jet jumps — all empty | `PhxAIDirectives`, consumed by spawning, skill profiles and flight |
| AI boarding | teleported into the hangar after a muster timer | finds a transport, rides it, disembarks at the authored LAND spot; a team with no flyers simply cannot board |

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
uses.

Both the runtime and editor assemblies **compile clean** (Roslyn, against the
Unity 2020.3.22f1 reference assemblies and the rebuilt `LibSWBF2.NET`), and
`Tools/SyntaxCheck` reports no `PhxProp` inheritance collisions, so type errors
and duplicate-property registration are ruled out. It has still **not been run
against real map files**.

The first play session should check, in rough order of how much rests on them:

- **The import report itself.** It is the fastest way to see the state of
  everything else: load a map and read
  `bf2-import-validation-<world>.json`, or open **Phoenix → Import Monitor**.
- **Combo schema.** The `.combo` format is not published in the mod tools, so
  `PhxComboLoader` reads the field names the format appears to use and
  tolerates anything else. If heroes swing once and stop, the moves parsed but
  the transitions did not.
- **Walker stride length.** `PhxWalkerLocomotion.StrideToHeightRatio` converts
  travel into cycle rate and is inferred from ride height, not authored. Feet
  skating forwards means it is too low, backwards too high — one constant.
- **Branch-weight layer mapping.** A hub carries five planning layers and the
  arc filter has six size bits; `PhxNavGraph.LayerOf` folds Large onto Huge. A
  wrong mapping decodes to nothing and routing silently falls back to
  unweighted, which is the previous behaviour rather than a regression.
- **Barrier flag polarity**, as before.

Two changes alter how existing content plays and are worth watching for
specifically: destructible buildings are no longer destroyed by a single hit
(they now use their authored MaxHealth), and plain `building` instances now get
a runtime instance, which means their odf collision masks are applied where
previously they were not.

# Roadmap

The organising idea, after a pass over what LibSWBF2 actually exposes:

> The Unity project should not merely *use* LibSWBF2-imported assets. It should
> preserve their identity, relationships, materials, world context, animation
> data and source semantics, and layer modern systems over that foundation.

Everything below is ordered by that principle rather than by visible feature.
A system that lets the rest of the project talk to the real imported world
outranks a system that makes one more thing look good.

---

## The four foundation systems

These are what let every other phase communicate with the actual SWBF2 data
instead of with disconnected Unity objects.

| System | State | Notes |
|---|---|---|
| `BFObjectIdentity` | **partial** | `BFSourceRef` + `BFSourceLink` cover level / world / kind / name / ordinal; `BFSegmentIdentity` now covers model / node / tag / role per renderable part. Missing: material and animation-bank identity. |
| `BFWorldQuerySystem` | **done** | `BFWorldQuery`: surface, ground height, water level, regions, indoor/outdoor, source object, lighting profile. Cached where it is a raycast. |
| `BFMaterial / Surface system` | **done** | `BFSurfaceType/Profile/Query`, `BFTerrainSurfaceMap`, `BFMaterialDefinition`, `BFMaterialInterpreter`. |
| `BFSWBF2EventBus` | **done** | `BFEventBus`. Wired to command post capture/neutralise and vehicle destruction; the remaining producers still need connecting. |

`BFObjectIdentity` finishing the job is the next foundation task, and it is
blocked on nothing — see *Segment awareness* below, which needs the same data.

---

## Phase 1 — SWBF2 → HDRP rendering

### Done

- Map-specific lighting profiles (`BFEnvironmentLightingProfile`,
  `BFLightingDirector`), applied over the map's own authored `.sky` data.
- `BFMaterialInterpreter`: authored MATL flags, specular exponent and specular
  colour drive HDRP response; derived data fills only channels the source never
  had.
- Surface system, including per-texel terrain surface types from the map's own
  blend map.
- One interaction channel with central budgets; surface-driven impacts; pooled
  impact lights and HDRP decals.
- Terrain deformation mask, snow accumulation, wetness — computed, published as
  globals, and **read by the terrain shader**.
- Water, underwater volume, ripples, planar reflections for large bodies only.
- Reflection and light probe management, spread over frames.
- Quality tiers driving every budget.

### Not done, in priority order

**1. ~~Segment-aware rendering~~ — done, partially consumed.**
`BFSegmentIdentity` is attached in `ModelLoader` where the importer already
reconstructs segments, carrying model name, node, the artist's MSH tag, and a
`BFSegmentRole` inferred from that tag. Consumed so far by:

- selective decals — no scorch marks on canopies, lights, wheels or people
- `PhxVehicleDamageZones` — per-part damage that modulates rather than
  replaces vehicle health, so every existing consumer keeps working
- `BFWorldQuery.Describe` returns the segment at a position

Still to consume: per-segment emissive, snow and wetness; per-segment LOD and
culling; hero hit regions (Phase 3).

Role inference is keyword matching against the artists' own tags, so it needs
validating on real content — **Phoenix → Import Monitor → "Report segment
roles in scene"** prints the distribution with examples. A map that comes back
almost entirely `Unknown` means the vocabulary in `BFSegmentRoles` does not
match how that content was named.

**2. World animation.** `WorldAnimation`, `WorldAnimationKey`,
`WorldAnimationGroup` and `WorldAnimationHierarchy` are all parsed and
`PhxSceneAnimator.InitializeWorldAnimations` is called — but rotating
machinery, moving platforms and map mechanisms are not visibly running. Needs
an audit of what that path actually produces, then a `BFWorldAnimationSystem`
that drives them and reports what it could not resolve into the import report.

**3. Skydome reconstruction.** The importer builds the dome geometry and reads
`SkyInfo`/`SunInfo`/`DomeInfo`; the lighting director then puts a physically
based sky behind it. Those two need reconciling per environment — currently the
dome is scenery and the PBR sky is the lighting, which is right for Hoth and
wrong for Tatooine, where the dome *is* the sky.

**4. Terrain fidelity.** `BFTerrainDefinition` preserves the source. Still
missing: tessellation/displacement so deformation is depth rather than shading,
per-layer tiling from `TERR.TileRange` (currently a hardcoded modulo in the
HLSL), and vegetation.

**5. Shader-graph outputs.** Deformation displacement and per-pixel smoothness
need real graph outputs. Everything else in the terrain response is in HLSL and
needs no graph edit.

---

## Phase 2 — AI

### Done

- Planning graph, barriers, hint nodes read from the semantic source records.
- Branch weights decoded per A* expansion; dynamic tactical costs layered over
  authored topology; jump-arc capability gating.
- `BFAimProfile`/`BFAimState`: settling, tracking, range and burst-walk error.
  No tier has perfect aim.
- `BFAIDecision`: scored choice across ten actions, with difficulty setting how
  wide a band of good-enough options a soldier draws from.
- `BFAIObjectiveEvaluator`: posts ranked by ownership, presence, centrality and
  reinforcement pressure.
- Squad grouping by proximity, shared contacts, flank assignment.

### Not done

**1. `BFAIRegion`.** Regions are imported and drive triggers, but AI does not
read them. They are the level designers' own tactical annotation: indoor/outdoor,
objective areas, vehicle areas, defensive ground. `BFWorldQuery.RegionsAt` now
exists, so this is a consumer away.

**2. Environmental understanding.** `BFWorldQuery` can answer "what am I
standing on" and "am I indoors". AI does not ask yet. Should influence movement
speed, sound propagation, cover value and vehicle behaviour.

**3. Action-level animation.** AI picks behaviours; nothing maps a behaviour to
a stock animation. The AI should say `TakeCover` and an animation layer should
choose the clip — otherwise Phase 2 becomes coupled to clip names.

**4. `BFSquadSystem` proper.** Squads exist but are a grouping, not an entity
with a leader, a formation, a tactical state and its own memory.

**5. Tactical memory over regions.** "Enemy seen in region A → region A is
threatened → squad leader reconsiders" is the shape; currently memory is
per-soldier and positional.

---

## Phase 3 — Heroes

`.combo` state machine, saber deflection, force powers and hero rules are done.

Missing: hero animation banks driven by the state machine (the combo system
knows which move is playing and nothing plays it), `BFHitRegionSystem` for
head/torso/limb/saber damage regions, and `BFHeroPresentation` consuming Phase 1
(saber glow through the impact light pool, wetness, snow, damage state).

---

## Phase 4 — Gameplay and Lua

`BFObjectDefinition` is largely `BFEntityClassDefinition` already; what is
missing is the discipline that **Lua does not own gameplay state**. Today
several `PhxLuaAPI` functions mutate state directly. The target shape is
Lua → scripting API → gameplay state → Unity systems, with `BFEventBus`
carrying the results back. The bus exists; the migration does not.

Game modes beyond conquest (CTF, Hunt, Assault, campaign objectives) remain the
highest gameplay value per unit of work.

---

## Phase 5 — Vehicles

Segment awareness (Phase 1, item 1) is the prerequisite for almost all of it:
damage zones, per-part destruction and selective rendering all need the model's
semantic parts. After that: `BFVehiclePhysicsProfile` per stock vehicle,
seat architecture with per-seat damage exposure, vehicle animation banks, and a
target evaluator that distinguishes infantry from heroes from vehicles.

---

## Performance

Costs introduced by the presentation layer, and what was done about them:

| Cost | Status |
|---|---|
| Derived material maps over every texture at full resolution | Derived at ≤256 px (16× less work), compressed, and behind `PhxBF3Config.DerivedMaterialMaps` |
| Reflection probes rendered in the load frame — 60 scene renders | Spread one per frame |
| Renderer probe/shadow configuration over a whole map in one frame | Batched 400 per frame |
| Footstep ground probe per soldier per frame | Probes on travelled distance; every frame only while airborne |
| Volumetric fog forced on every map | Only where authored or where the environment is genuinely soupy |
| Command post lights unshadowed at 15 m | Shadowed at 256, short range, short fade |

Not yet addressed: terrain deformation stamps are immediate-mode `GL` draws
(bounded by the interaction budget, but a draw call each), and no LOD or
instancing work has been done on imported geometry at all.

`PhxBF3Config.PresentationQuality` (0–3) now drives every budget from the
config file, so a machine that struggles has a dial that does not need a
rebuild.

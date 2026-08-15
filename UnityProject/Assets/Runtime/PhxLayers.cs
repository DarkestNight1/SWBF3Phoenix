using UnityEngine;

/// <summary>
/// Shared physics layer masks, resolved once from the project's own collision
/// settings and reused.
/// </summary>
/// <remarks>
/// These deliberately do NOT use static field initializers. Unity refuses to
/// run LayerMask.NameToLayer (and the Physics layer queries below) from a
/// static constructor:
///
///     UnityException: NameToLayer is not allowed to be called from a
///     MonoBehaviour constructor (or instance field initializer), call it in
///     Awake or Start instead.
///
/// A "static readonly int Mask = ... NameToLayer(...)" field looks harmless and
/// compiles cleanly, but its initializer runs in the type's cctor - and when a
/// cctor throws, the CLR caches that failure permanently, so every later touch
/// of the type rethrows TypeInitializationException for the rest of the
/// session. Putting that on PhxSoldier took out character select, unit previews
/// and spawning wholesale.
///
/// Resolving lazily on first read keeps the "look this up once" intent while
/// moving the actual call to ordinary runtime code, where it is legal. Layers
/// and the collision matrix cannot change at runtime, so one resolve is all
/// that is ever needed.
/// </remarks>
public static class PhxLayers
{
    static int SoldierGroundMask;
    static bool SoldierGroundResolved;

    static int SoldierMask;
    static bool SoldierResolved;

    /// <summary>
    /// Surfaces a soldier can actually stand on.
    /// </summary>
    /// <remarks>
    /// This is derived from the project's physics collision matrix rather than
    /// hand-listed, because hand-listing got it wrong in a way that was very
    /// hard to see.
    ///
    /// The previous mask was "everything except SoldierAll/VehicleAll/
    /// OrdnanceAll", i.e. it assumed every remaining layer is solid ground. BF2
    /// models ship a SEPARATE collision mesh per target type, and the importer
    /// splits them onto separate layers accordingly (see
    /// SWBFModel.MapRoleAndMaskToLayer): a building's ordnance-only collision
    /// mesh lands on BuildingOrdnance, its vehicle-only mesh on
    /// BuildingVehicle, and so on. Soldiers pass straight through all of those.
    ///
    /// That mismatch caused spawn fall-through, and the reason it was so easy
    /// to miss is that a physics query taking an explicit layer mask IGNORES
    /// the collision matrix entirely - it tests every collider in the mask. So
    /// SettleOnGround would happily settle a spawn onto an ordnance-only
    /// collision mesh, CheckSphere would report "grounded" from that same
    /// surface, and then the capsule - which genuinely does not collide with it
    /// - would sink through under gravity until it fell far enough for the
    /// grounded check to flip, at which point the soldier entered the Jump
    /// state and fell forever. Only posts whose nearest surface happened to be
    /// a non-soldier collision mesh were affected, which is why it hit some
    /// spawns and not others.
    ///
    /// Deriving from GetIgnoreLayerCollision keeps this honest: whatever the
    /// collision matrix says a soldier collides with is exactly what counts as
    /// ground, and it stays correct if layers are ever re-assigned.
    ///
    /// The three dynamic-body layers are then subtracted. They ARE collidable,
    /// but settling a spawn onto another soldier's capsule or a vehicle hull
    /// parks the spawner in mid-air the moment that body moves.
    /// </remarks>
    public static int SoldierGround
    {
        get
        {
            if (!SoldierGroundResolved)
            {
                int soldier = LayerMask.NameToLayer("SoldierAll");

                int mask = 0;
                for (int layer = 0; layer < 32; ++layer)
                {
                    if (!Physics.GetIgnoreLayerCollision(soldier, layer))
                    {
                        mask |= 1 << layer;
                    }
                }

                mask &= ~(1 << soldier
                        | 1 << LayerMask.NameToLayer("VehicleAll")
                        | 1 << LayerMask.NameToLayer("OrdnanceAll"));

                SoldierGroundMask = mask;
                SoldierGroundResolved = true;
            }
            return SoldierGroundMask;
        }
    }

    /// <summary>Soldiers only - used to test whether a spawn point is crowded.</summary>
    public static int Soldier
    {
        get
        {
            if (!SoldierResolved)
            {
                SoldierMask = 1 << LayerMask.NameToLayer("SoldierAll");
                SoldierResolved = true;
            }
            return SoldierMask;
        }
    }

    static int OrdnanceHitsMask;
    static bool OrdnanceHitsResolved;

    /// <summary>
    /// Everything a projectile can actually hit.
    /// </summary>
    /// <remarks>
    /// This is the mask an aim ray must use, because the only useful answer to
    /// "what am I pointing at" is "where would my shot stop".
    ///
    /// Derived from the collision matrix rather than hand-listed, for the same
    /// reason <see cref="SoldierGround"/> is: hand-listing got it catastrophically
    /// wrong. PhxPlayerController's aim ray used a literal
    ///
    ///     int layerMask = 7;   // "ignore vehicle colliders"
    ///
    /// which is not a layer index but a BITMASK - bits 0, 1 and 2, meaning
    /// Default, TransparentFX and Ignore Raycast. Soldiers are on layer 10,
    /// terrain 11, buildings 12, so the ray could hit essentially nothing in
    /// the game and the aim point fell through to its "1000 metres along the
    /// camera ray" fallback on virtually every shot.
    ///
    /// That is what made the player unable to kill anything. Shots are fired
    /// from the barrel toward the aim point, so with the aim point a thousand
    /// metres away the barrel-to-camera offset closes by well under a percent
    /// over the first few metres - a target seven metres away is missed by
    /// most of the third-person camera's offset, and the bolt lands in the
    /// terrain. The damage trace showed exactly that: every single bolt
    /// hitting Terrain with enemies alive seven metres away.
    ///
    /// The convergence itself was always right; it was being handed a target
    /// that did not exist. Diagnosed with the help of the barrel-fire-origin
    /// research in BF2GameExt by S1thK3nny (MIT) - see the credits in
    /// docs/BF2Compatibility.md - which documents the same class of parallax
    /// fault in the original engine.
    /// </remarks>
    public static int OrdnanceHits
    {
        get
        {
            if (!OrdnanceHitsResolved)
            {
                int ordnance = LayerMask.NameToLayer("OrdnanceAll");

                int mask = 0;
                for (int layer = 0; layer < 32; ++layer)
                {
                    if (!Physics.GetIgnoreLayerCollision(ordnance, layer))
                    {
                        mask |= 1 << layer;
                    }
                }

                // The ordnance layer itself is in the matrix for bolt-vs-bolt
                // cases and is not something to aim at.
                mask &= ~(1 << ordnance);

                OrdnanceHitsMask = mask;
                OrdnanceHitsResolved = true;
            }
            return OrdnanceHitsMask;
        }
    }

    static int TerrainLayerIndex = -1;

    /// <summary>
    /// Layer index of the imported terrain. Resolved lazily for the same
    /// reason everything else here is - see the note on this class.
    /// </summary>
    public static int TerrainLayer
    {
        get
        {
            if (TerrainLayerIndex < 0)
            {
                TerrainLayerIndex = LayerMask.NameToLayer("TerrainAll");
            }
            return TerrainLayerIndex;
        }
    }
}

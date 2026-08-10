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

using System.Collections.Generic;
using LibSWBF2.Utils;

/// <summary>
/// Reverse lookup from the FNV hashes the munged data stores back to the
/// property names the mod tools document.
/// </summary>
/// <remarks>
/// Munging replaces every property name with its hash, so the data itself
/// cannot tell us what a property was called - the only way back is to hash
/// candidate names and compare. The table below is therefore a dictionary of
/// names we know to look for, not a complete list of what a map may contain:
/// an unlisted property is still captured with its hash and value intact
/// (see <see cref="BFProperty"/>), it just reads as a hash in reports.
///
/// Add names here as they become interesting rather than pre-emptively; each
/// one costs a hash at startup and nothing else.
/// </remarks>
public static class BFPropertyNames
{
    static readonly string[] Known =
    {
        // Region / instance addressing
        "Name", "Radius", "Team", "GeometryName", "ClassLabel", "ClassParent",
        "AttachOdf", "AttachToHardPoint", "OverrideTexture", "Layer",

        // Command post / objective wiring
        "CommandPost", "SpawnPath", "CaptureRegion", "ControlRegion", "ControlZone",
        "CaptureTime", "NeutralizeTime", "HoloIconOffset",

        // Hint node annotations
        "Mode", "PrimaryStance", "SecondaryStance", "AllyPathing",

        // AI / navigation
        "AISizeType", "AIPathfindingType", "NoPathfinding", "IsHardpoint",

        // Damage / destruction
        "MaxHealth", "MaxShield", "HealthType", "Explosion", "DeathExplosion",
        "ChunkGeometryName", "ChunkNodeName", "ChunkPhysics", "ChunkTerrainCollisions",
        "ChunkTerrainEffect", "ChunkTrailEffect", "ChunkSmokeEffect", "ChunkSpeed",
        "ChunkUpFactor", "ChunkOmega", "ChunkStickiness", "ChunkGravity",

        // Hero / combo
        "ComboAnimationBank", "AnimationName", "AnimationBank",

        // Ordnance / weapons
        "OrdnanceName", "WeaponName", "WeaponSection", "ShotDelay", "SalvoDelay",

        // Mission objects added by the class-coverage pass
        "TriggerRadius", "ExplosionName", "ArmedTime", "LifeTime",
        "BeaconEffect", "BeaconDuration", "UseTime", "UseRadius",
    };

    static readonly Dictionary<uint, string> ByHash = Build();

    static Dictionary<uint, string> Build()
    {
        var map = new Dictionary<uint, string>(Known.Length);
        for (int i = 0; i < Known.Length; ++i)
        {
            uint hash = HashUtils.GetFNV(Known[i]);
            if (!map.ContainsKey(hash))
            {
                map.Add(hash, Known[i]);
            }
        }
        return map;
    }

    /// <summary>Documented name for a hash, or empty when we don't know it.</summary>
    public static string Resolve(uint hash)
    {
        return ByHash.TryGetValue(hash, out string name) ? name : string.Empty;
    }

    /// <summary>Pair up the parallel hash/value arrays the native layer returns.</summary>
    public static List<BFProperty> Pair(uint[] hashes, string[] values)
    {
        var props = new List<BFProperty>();
        if (hashes == null || values == null) return props;

        int count = hashes.Length < values.Length ? hashes.Length : values.Length;
        for (int i = 0; i < count; ++i)
        {
            props.Add(new BFProperty(hashes[i], Resolve(hashes[i]), values[i]));
        }
        return props;
    }

    /// <summary>First value for a known property name, or the fallback.</summary>
    public static string Get(IReadOnlyList<BFProperty> props, string name, string fallback = "")
    {
        if (props == null) return fallback;

        uint hash = HashUtils.GetFNV(name);
        for (int i = 0; i < props.Count; ++i)
        {
            if (props[i].Hash == hash) return props[i].Value;
        }
        return fallback;
    }
}

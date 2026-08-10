using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// How one surface type responds to being hit, walked on, rained on or blown
/// up.
/// </summary>
/// <remarks>
/// One table rather than per-weapon or per-effect code. A blaster hitting snow
/// and a grenade landing in snow want the same puff colour, the same
/// darkening, the same softness underfoot - and if that lives in the weapon,
/// every new weapon has to re-know it and the twentieth one gets it wrong.
///
/// Values are presentation only. Nothing here changes damage, movement speed
/// or any other gameplay quantity.
/// </remarks>
public sealed class BFSurfaceProfile
{
    public BFSurfaceType Type = BFSurfaceType.Unknown;
    public string Name = "Unknown";

    // ---------------------------------------------------------- appearance

    /// <summary>Colour of dust/debris kicked up. Drives particle tint.</summary>
    public Color DebrisColor = new Color(0.5f, 0.48f, 0.45f);

    /// <summary>Colour a decal darkens the surface toward.</summary>
    public Color ScorchColor = new Color(0.07f, 0.06f, 0.05f);

    /// <summary>How much material is thrown up, 0..2. Sand a lot, metal none.</summary>
    public float DebrisAmount = 1f;

    /// <summary>Whether impacts throw sparks. Metal, rock and glass do.</summary>
    public bool Sparks;

    /// <summary>Whether impacts produce a lingering smoke plume.</summary>
    public bool Smoke = true;

    /// <summary>Whether the surface visibly deforms - footprints, craters.</summary>
    public bool Deformable;

    /// <summary>How deep a footstep sinks, in metres.</summary>
    public float FootprintDepth;

    /// <summary>How long a deformation lasts before healing, in seconds. 0 = forever.</summary>
    public float DeformationLifetime = 120f;

    // ---------------------------------------------------------- reflectance

    /// <summary>Baseline smoothness for wetness and snow blending, 0..1.</summary>
    public float BaseSmoothness = 0.2f;

    /// <summary>Smoothness this surface reaches when fully wet.</summary>
    public float WetSmoothness = 0.85f;

    /// <summary>How readily it takes water at all. Stone yes, snow no.</summary>
    public float WetnessReceptivity = 1f;

    /// <summary>How readily snow settles on it. Lava never.</summary>
    public float SnowReceptivity = 1f;

    /// <summary>Emissive surfaces light their own impacts.</summary>
    public bool Emissive;

    // ---------------------------------------------------------------- audio

    /// <summary>Sound name fragment the sound loader can try for footsteps.</summary>
    public string FootstepSoundKey = "";

    /// <summary>Sound name fragment for impacts.</summary>
    public string ImpactSoundKey = "";

    // ================================================================== table

    static readonly Dictionary<BFSurfaceType, BFSurfaceProfile> Profiles = Build();

    static Dictionary<BFSurfaceType, BFSurfaceProfile> Build()
    {
        var table = new Dictionary<BFSurfaceType, BFSurfaceProfile>();

        void Add(BFSurfaceProfile p) => table[p.Type] = p;

        Add(new BFSurfaceProfile
        {
            Type = BFSurfaceType.Snow, Name = "Snow",
            DebrisColor = new Color(0.95f, 0.97f, 1f),
            ScorchColor = new Color(0.25f, 0.28f, 0.34f),   // wet grey, not black
            DebrisAmount = 1.6f, Smoke = false,
            Deformable = true, FootprintDepth = 0.09f, DeformationLifetime = 90f,
            BaseSmoothness = 0.25f, WetSmoothness = 0.5f,
            WetnessReceptivity = 0.2f, SnowReceptivity = 1f,
            FootstepSoundKey = "snow", ImpactSoundKey = "snow",
        });

        Add(new BFSurfaceProfile
        {
            Type = BFSurfaceType.Ice, Name = "Ice",
            DebrisColor = new Color(0.85f, 0.93f, 1f),
            ScorchColor = new Color(0.30f, 0.38f, 0.45f),
            DebrisAmount = 0.7f, Sparks = false, Smoke = false,
            Deformable = false,
            BaseSmoothness = 0.9f, WetSmoothness = 0.95f,
            WetnessReceptivity = 0.4f, SnowReceptivity = 0.6f,
            FootstepSoundKey = "ice", ImpactSoundKey = "ice",
        });

        Add(new BFSurfaceProfile
        {
            Type = BFSurfaceType.Sand, Name = "Sand",
            DebrisColor = new Color(0.78f, 0.66f, 0.44f),
            ScorchColor = new Color(0.20f, 0.15f, 0.09f),
            DebrisAmount = 1.8f, Smoke = true,
            Deformable = true, FootprintDepth = 0.05f, DeformationLifetime = 60f,
            BaseSmoothness = 0.12f, WetSmoothness = 0.55f,
            WetnessReceptivity = 0.8f, SnowReceptivity = 0.9f,
            FootstepSoundKey = "sand", ImpactSoundKey = "sand",
        });

        Add(new BFSurfaceProfile
        {
            Type = BFSurfaceType.Mud, Name = "Mud",
            DebrisColor = new Color(0.32f, 0.25f, 0.17f),
            ScorchColor = new Color(0.10f, 0.08f, 0.05f),
            DebrisAmount = 1.3f,
            Deformable = true, FootprintDepth = 0.13f, DeformationLifetime = 180f,
            BaseSmoothness = 0.45f, WetSmoothness = 0.9f,
            WetnessReceptivity = 1f, SnowReceptivity = 0.7f,
            FootstepSoundKey = "mud", ImpactSoundKey = "mud",
        });

        Add(new BFSurfaceProfile
        {
            Type = BFSurfaceType.Grass, Name = "Grass",
            DebrisColor = new Color(0.30f, 0.38f, 0.18f),
            ScorchColor = new Color(0.08f, 0.07f, 0.04f),
            DebrisAmount = 1.1f,
            Deformable = true, FootprintDepth = 0.03f, DeformationLifetime = 25f,
            BaseSmoothness = 0.2f, WetSmoothness = 0.6f,
            WetnessReceptivity = 0.9f, SnowReceptivity = 1f,
            FootstepSoundKey = "grass", ImpactSoundKey = "grass",
        });

        Add(new BFSurfaceProfile
        {
            Type = BFSurfaceType.Rock, Name = "Rock",
            DebrisColor = new Color(0.45f, 0.42f, 0.38f),
            ScorchColor = new Color(0.06f, 0.055f, 0.05f),
            DebrisAmount = 1.2f, Sparks = true,
            BaseSmoothness = 0.18f, WetSmoothness = 0.75f,
            WetnessReceptivity = 1f, SnowReceptivity = 0.9f,
            FootstepSoundKey = "rock", ImpactSoundKey = "rock",
        });

        Add(new BFSurfaceProfile
        {
            Type = BFSurfaceType.Metal, Name = "Metal",
            DebrisColor = new Color(0.85f, 0.75f, 0.55f),
            ScorchColor = new Color(0.04f, 0.035f, 0.03f),
            DebrisAmount = 0.35f, Sparks = true, Smoke = true,
            BaseSmoothness = 0.62f, WetSmoothness = 0.92f,
            WetnessReceptivity = 1f, SnowReceptivity = 0.8f,
            FootstepSoundKey = "metal", ImpactSoundKey = "metal",
        });

        Add(new BFSurfaceProfile
        {
            Type = BFSurfaceType.Wood, Name = "Wood",
            DebrisColor = new Color(0.55f, 0.40f, 0.24f),
            ScorchColor = new Color(0.05f, 0.03f, 0.02f),
            DebrisAmount = 1.4f, Smoke = true,
            BaseSmoothness = 0.25f, WetSmoothness = 0.7f,
            WetnessReceptivity = 0.9f, SnowReceptivity = 1f,
            FootstepSoundKey = "wood", ImpactSoundKey = "wood",
        });

        Add(new BFSurfaceProfile
        {
            Type = BFSurfaceType.Concrete, Name = "Concrete",
            DebrisColor = new Color(0.62f, 0.60f, 0.57f),
            ScorchColor = new Color(0.05f, 0.05f, 0.05f),
            DebrisAmount = 1f, Sparks = true,
            BaseSmoothness = 0.22f, WetSmoothness = 0.8f,
            WetnessReceptivity = 1f, SnowReceptivity = 1f,
            FootstepSoundKey = "concrete", ImpactSoundKey = "concrete",
        });

        Add(new BFSurfaceProfile
        {
            Type = BFSurfaceType.Water, Name = "Water",
            DebrisColor = new Color(0.75f, 0.85f, 0.90f),
            ScorchColor = Color.clear,                      // water takes no marks
            DebrisAmount = 1.5f, Smoke = false,
            BaseSmoothness = 0.95f, WetSmoothness = 0.95f,
            WetnessReceptivity = 0f, SnowReceptivity = 0f,
            FootstepSoundKey = "water", ImpactSoundKey = "water",
        });

        Add(new BFSurfaceProfile
        {
            Type = BFSurfaceType.Glass, Name = "Glass",
            DebrisColor = new Color(0.85f, 0.92f, 0.95f),
            ScorchColor = new Color(0.10f, 0.12f, 0.14f),
            DebrisAmount = 1.2f, Sparks = true, Smoke = false,
            BaseSmoothness = 0.95f, WetSmoothness = 0.97f,
            WetnessReceptivity = 1f, SnowReceptivity = 0.7f,
            FootstepSoundKey = "glass", ImpactSoundKey = "glass",
        });

        Add(new BFSurfaceProfile
        {
            Type = BFSurfaceType.Lava, Name = "Lava",
            DebrisColor = new Color(1f, 0.45f, 0.10f),
            ScorchColor = Color.clear,
            DebrisAmount = 1.4f, Smoke = true, Emissive = true,
            BaseSmoothness = 0.35f, WetSmoothness = 0.35f,
            WetnessReceptivity = 0f, SnowReceptivity = 0f,
            FootstepSoundKey = "lava", ImpactSoundKey = "lava",
        });

        Add(new BFSurfaceProfile
        {
            Type = BFSurfaceType.Flesh, Name = "Flesh",
            DebrisColor = new Color(0.45f, 0.10f, 0.08f),
            ScorchColor = Color.clear,          // no decals on people
            DebrisAmount = 0.6f, Smoke = false,
            BaseSmoothness = 0.3f, WetSmoothness = 0.6f,
            WetnessReceptivity = 0.7f, SnowReceptivity = 0f,
            ImpactSoundKey = "flesh",
        });

        Add(new BFSurfaceProfile
        {
            Type = BFSurfaceType.Unknown, Name = "Unknown",
        });

        return table;
    }

    /// <summary>Profile for a type. Never null.</summary>
    public static BFSurfaceProfile Get(BFSurfaceType type)
    {
        return Profiles.TryGetValue(type, out BFSurfaceProfile profile)
            ? profile
            : Profiles[BFSurfaceType.Unknown];
    }
}

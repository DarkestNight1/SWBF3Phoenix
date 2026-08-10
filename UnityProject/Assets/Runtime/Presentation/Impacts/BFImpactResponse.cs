using UnityEngine;

/// <summary>
/// What a surface does when something hits it.
/// </summary>
/// <remarks>
/// The pipeline the whole impact system runs on:
///
/// <code>
/// weapon -> hit -> BFSurfaceQuery -> surface type -> surface-specific response
/// </code>
///
/// The important property is that the weapon does not decide. A blaster does
/// not know that metal throws sparks and snow does not; it reports that it hit
/// something, and the surface answers. That is what makes a new weapon
/// correct on every surface for free, and a new surface correct for every
/// weapon.
///
/// Everything here is presentation. No damage, no physics on gameplay bodies,
/// nothing that a headless server would need to agree about.
/// </remarks>
public static class BFImpactResponse
{
    /// <summary>
    /// Respond to a hit, resolving the surface from the collider.
    /// </summary>
    /// <param name="scale">
    /// Relative size of the hit: 1 for a rifle bolt, larger for cannon rounds.
    /// </param>
    public static void Play(RaycastHit hit, Vector3 direction, float scale = 1f,
                            GameObject instigator = null)
    {
        BFSurfaceType type = BFSurfaceQuery.Resolve(hit);
        Play(hit.point, hit.normal, direction, type, scale, instigator);
    }

    /// <param name="playSurfaceParticles">
    /// False when the caller already played the ordnance's own authored impact
    /// effect. The odf's effect is the stock artistic answer and wins; this
    /// system then contributes only what the stock format could not express -
    /// the flash, the mark, and the surface's physical reaction.
    /// </param>
    public static void Play(Vector3 position, Vector3 normal, Vector3 direction,
                            BFSurfaceType type, float scale = 1f, GameObject instigator = null,
                            bool playSurfaceParticles = true)
    {
        BFSurfaceProfile profile = BFSurfaceProfile.Get(type);

        var interaction = new BFSurfaceInteraction(
            position, normal, direction,
            BFInteractionProfile.For(BFInteractionSource.BlasterImpact).Strength * scale,
            BFInteractionProfile.For(BFInteractionSource.BlasterImpact).Radius * scale,
            BFInteractionSource.BlasterImpact, type, instigator);

        // Anything that reacts to being hit - snow deformation, water ripples,
        // grass - hears about it through the one channel.
        BFSurfaceInteractionSystem.Report(interaction);

        PlayFlash(interaction, profile, scale);
        PlayDecal(interaction, profile, scale);
        if (playSurfaceParticles)
        {
            PlayParticles(interaction, profile, scale);
        }
    }

    /// <summary>
    /// The light of the hit itself.
    /// </summary>
    /// <remarks>
    /// Colour comes from the surface, not the bolt: a red blaster hitting a
    /// bulkhead throws a hot white-orange flash off the metal, and the same
    /// bolt hitting snow lights the snow blue-white. Tinting by the bolt is
    /// the thing that makes every impact in a game look identical.
    /// </remarks>
    static void PlayFlash(in BFSurfaceInteraction interaction, BFSurfaceProfile profile, float scale)
    {
        // A surface that emits its own light gains nothing from a flash.
        if (profile.Emissive) return;

        Color flash = profile.Sparks
            ? new Color(1f, 0.85f, 0.55f)          // hot metal spray
            : Color.Lerp(profile.DebrisColor, Color.white, 0.4f);

        // Very short: this is the moment of the hit, not a fire left burning.
        BFImpactLightPool.Flash(
            interaction.Position + interaction.Normal * 0.15f,
            flash,
            intensity: 900f * scale,
            range: Mathf.Lerp(2.5f, 6f, Mathf.Clamp01(scale)),
            lifetime: 0.07f);
    }

    static void PlayDecal(in BFSurfaceInteraction interaction, BFSurfaceProfile profile, float scale)
    {
        if (profile.ScorchColor.a <= 0f) return;   // water, lava, people

        // Grazing hits leave longer, fainter marks; square-on hits leave a
        // small dark one. Approximated by scaling the mark with the angle
        // rather than stretching it, which a square projector cannot do.
        float size = Mathf.Lerp(0.22f, 0.5f, interaction.Grazing) * scale;
        float opacity = Mathf.Lerp(1f, 0.55f, interaction.Grazing);

        BFDecalSystem.Place(interaction.Position, interaction.Normal,
                            profile.ScorchColor, size, opacity);
    }

    /// <summary>
    /// Sparks, dust and smoke, through the map's own effects where it has
    /// them.
    /// </summary>
    /// <remarks>
    /// Spark direction is the physical one: the incoming direction reflected
    /// off the surface normal. A bolt arriving square-on sprays back along its
    /// own path; one arriving at a grazing angle throws its sparks down the
    /// surface, which is the single detail that makes ricochets read as
    /// ricochets.
    ///
    /// The effect names are the stock game's own. Where a map does not ship
    /// one, nothing plays - the flash and decal above still carry the hit, and
    /// inventing geometry here would violate the "no external assets" rule.
    /// </remarks>
    static void PlayParticles(in BFSurfaceInteraction interaction, BFSurfaceProfile profile, float scale)
    {
        PhxScene scene = PhxGame.GetScene();
        if (scene == null) return;

        Vector3 spray = Vector3.Slerp(interaction.Normal, interaction.Deflection,
                                      Mathf.Clamp01(interaction.Grazing));
        Quaternion rotation = Quaternion.LookRotation(spray, Vector3.up);

        string effect = ImpactEffectName(interaction.SurfaceType);
        if (!string.IsNullOrEmpty(effect))
        {
            scene.EffectsManager.PlayEffectOnce(effect, interaction.Position, rotation);
        }
    }

    /// <summary>
    /// Stock effect name for a surface, or empty.
    /// </summary>
    /// <remarks>
    /// These follow the naming the shipped effects use. A map without the
    /// named effect simply gets none, which the effects manager already
    /// handles and the import report already counts - so a missing one shows
    /// up as a measured unresolved reference rather than a silent hole.
    /// </remarks>
    static string ImpactEffectName(BFSurfaceType type)
    {
        switch (type)
        {
            case BFSurfaceType.Metal: return "com_sfx_spark";
            case BFSurfaceType.Glass: return "com_sfx_spark";
            case BFSurfaceType.Rock: return "com_sfx_dustcloud";
            case BFSurfaceType.Concrete: return "com_sfx_dustcloud";
            case BFSurfaceType.Sand: return "com_sfx_sand";
            case BFSurfaceType.Snow: return "com_sfx_snow";
            case BFSurfaceType.Water: return "com_sfx_watersplash";
            case BFSurfaceType.Wood: return "com_sfx_woodchips";
            default: return "";
        }
    }

    /// <summary>
    /// An explosion's interaction with the ground it went off on.
    /// </summary>
    /// <remarks>
    /// Same pipeline, bigger numbers, plus the crater. The surface decides
    /// what a crater looks like: snow gets a wide shallow displacement and a
    /// cloud, sand gets a rim of displaced material, metal gets scorch and
    /// sparks and no crater at all because a deck plate does not deform.
    /// </remarks>
    public static void PlayExplosion(Vector3 position, float radius, GameObject instigator = null)
    {
        BFSurfaceType type = BFSurfaceType.Unknown;
        Vector3 normal = Vector3.up;
        Vector3 groundPoint = position;

        // Find what is under the blast rather than assuming the ground: a
        // grenade going off against a wall craters the wall, not the floor.
        if (Physics.Raycast(position + Vector3.up * 2f, Vector3.down, out RaycastHit hit,
                            radius + 4f, ~0, QueryTriggerInteraction.Ignore))
        {
            type = BFSurfaceQuery.Resolve(hit);
            normal = hit.normal;
            groundPoint = hit.point;
        }

        BFSurfaceProfile profile = BFSurfaceProfile.Get(type);

        BFSurfaceInteractionSystem.Report(new BFSurfaceInteraction(
            groundPoint, normal, Vector3.down,
            BFInteractionProfile.For(BFInteractionSource.Explosion).Strength,
            Mathf.Max(radius, 1f),
            BFInteractionSource.Explosion, type, instigator));

        BFImpactLightPool.Flash(position + Vector3.up * 0.5f,
                                new Color(1f, 0.72f, 0.38f),
                                intensity: 12000f,
                                range: Mathf.Max(8f, radius * 3f),
                                lifetime: 0.35f);

        if (profile.ScorchColor.a > 0f)
        {
            BFDecalSystem.Place(groundPoint, normal, profile.ScorchColor,
                                Mathf.Max(1.5f, radius * 1.4f), 1f);
        }
    }

    /// <summary>A foot landing, which is the most common interaction in the game.</summary>
    public static void PlayFootstep(Vector3 position, Vector3 normal, BFSurfaceType type,
                                    float scale = 1f, GameObject instigator = null)
    {
        BFSurfaceInteractionSystem.Report(BFInteractionSource.Footstep, position, normal,
                                          -normal, type, scale, instigator);
    }
}

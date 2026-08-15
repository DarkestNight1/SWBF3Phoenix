using UnityEngine;

/// <summary>
/// A shaft of light through a canopy - Endor's forest, one on Yavin.
/// </summary>
/// <remarks>
/// The largest single gap the class census found: 135 instances, 134 of them
/// end_prop_godraycluster on Endor, where the shafts through the trees are most
/// of what the map looks like.
///
/// Unlike a flag or a trap, this one really was invisible rather than merely
/// inert. <c>LoadGeneralClass</c> builds an instance's model from its
/// GeometryName whatever its base class, so an unregistered base with geometry
/// still appears and only its behaviour is missing. A godray odf carries no
/// GeometryName at all - there is no mesh, the shaft is something the engine
/// draws from the numbers - so an unregistered godray imported as an empty
/// GameObject and the map lost the effect entirely.
///
/// Property names come from hashing candidates against the shipped odfs rather
/// than from guesswork about what a god ray ought to have. Confirmed: Radius,
/// Color, MinAlpha, MaxAlpha, and on the cluster variant NumRays and
/// SpreadRadius. Endor states Radius 3.0, Color 202 201 171, MinAlpha 0.15,
/// MaxAlpha 0.8, NumRays 8, SpreadRadius 5.0 - a warm off-white, eight shafts
/// to a cluster, scattered over five metres.
///
/// Two hashes did not resolve, 0xbd9397ec (3.5) and 0x9d399b92 (5.0). Both are
/// larger than Radius and both are lengths; the shaft has to have a height and
/// the pair is the obvious candidate for it, so <see cref="Height"/> defaults
/// to the larger of the two observed values. Stated rather than pretended, the
/// same as the unresolved pair on <see cref="PhxGrassPatchClass"/>.
/// </remarks>
public class PhxGodRayClass : PhxClass
{
    /// <summary>Width of one shaft.</summary>
    public PhxProp<float> Radius = new PhxProp<float>(3.0f);

    /// <summary>
    /// Length of the shaft, top to bottom.
    /// </summary>
    /// <remarks>
    /// Defaulted, not read - the property carrying it is one of the two
    /// unresolved hashes above. 5.0 is the larger of the values Endor states
    /// for that pair, which puts an unauthored shaft where an authored one is.
    /// </remarks>
    public PhxProp<float> Height = new PhxProp<float>(5.0f);

    public PhxProp<UnityEngine.Color> Color =
        new PhxProp<UnityEngine.Color>(UnityEngine.Color.white);

    public PhxProp<float> MinAlpha = new PhxProp<float>(0.15f);
    public PhxProp<float> MaxAlpha = new PhxProp<float>(0.8f);

    /// <summary>Shafts in a cluster. The single-ray variant states none.</summary>
    public PhxProp<int> NumRays = new PhxProp<int>(1);

    /// <summary>How far across the cluster's shafts are scattered.</summary>
    public PhxProp<float> SpreadRadius = new PhxProp<float>(0f);
}


public class PhxGodRay : PhxInstance<PhxGodRayClass>
{
    ParticleSystem Rays;

    public override void Init()
    {
        int count = Mathf.Max(1, C.NumRays);

        Rays = gameObject.AddComponent<ParticleSystem>();
        Rays.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

        ParticleSystemRenderer renderer = GetComponent<ParticleSystemRenderer>();

        var emission = Rays.emission;
        emission.enabled = false;

        var shape = Rays.shape;
        shape.enabled = false;

        var main = Rays.main;
        main.startSpeed = 0f;
        main.startLifetime = 1f;
        main.maxParticles = count;
        main.simulationSpace = ParticleSystemSimulationSpace.Local;

        // A shaft stands up and turns to face the viewer about its own axis -
        // the same reason grass is VerticalBillboard rather than Facing. A
        // full-facing quad tips flat when looked down on, and a god ray seen
        // from above would become a disc lying in the canopy.
        renderer.alignment = ParticleSystemRenderSpace.Local;
        renderer.renderMode = ParticleSystemRenderMode.VerticalBillboard;
        renderer.minParticleSize = .000001f;

        // maxParticleSize is a FRACTION OF THE VIEWPORT. Left at its default a
        // shaft walked into fills the screen with flat light.
        renderer.maxParticleSize = 0.75f;

        // Additive, not the normal particle material the foliage uses. A god
        // ray is light arriving, so it brightens what is behind it and never
        // occludes it; blended it would read as a grey post standing in the
        // trees.
        Material mat = new Material(Resources.Load<Material>("effects/HDRPParticleAdditive"));
        renderer.sharedMaterial = mat;

        // The quad is stretched into a shaft rather than kept square: Radius is
        // the width and Height the length, and a god ray is much taller than it
        // is wide. startSize3D is the only way to say that per particle.
        main.startSize3D = true;
        main.startSizeX = C.Radius;
        main.startSizeY = C.Height;
        main.startSizeZ = C.Radius;

        UnityEngine.Color tint = C.Color;

        for (int i = 0; i < count; ++i)
        {
            // Each shaft gets its own alpha from the authored range, so a
            // cluster reads as depth rather than as one object drawn eight
            // times.
            float alpha = Random.Range(C.MinAlpha, C.MaxAlpha);

            var emitParams = new ParticleSystem.EmitParams();
            emitParams.startColor = new UnityEngine.Color(tint.r, tint.g, tint.b, alpha);
            Rays.Emit(emitParams, 1);
        }

        ParticleSystem.Particle[] rays = new ParticleSystem.Particle[count];
        int emitted = Rays.GetParticles(rays);

        for (int i = 0; i < emitted; ++i)
        {
            // A single ray sits on the instance's own origin; a cluster spreads
            // over SpreadRadius. sqrt() on the radius again, or the shafts
            // bunch in the middle and leave the edge of the cluster empty.
            Vector3 offset = Vector3.zero;
            if (C.SpreadRadius > 0f && emitted > 1)
            {
                float angle = Random.Range(0f, Mathf.PI * 2f);
                float dist = C.SpreadRadius * Mathf.Sqrt(Random.value);
                offset = new Vector3(Mathf.Cos(angle) * dist, 0f, Mathf.Sin(angle) * dist);
            }

            // Centred on its own height: the instance is placed where the shaft
            // should be, not where its top should be, so lifting by half the
            // length keeps the placement meaning what the map intended.
            rays[i].position = offset + new Vector3(0f, C.Height * 0.5f, 0f);
        }

        Rays.SetParticles(rays);

        // No second Emit - the loop above already emitted every ray and then
        // placed it. Emitting again stacks an unplaced batch on the origin.

        Rays.Pause();
    }

    public override void Destroy() { }
}

using UnityEngine;

/// <summary>
/// A patch of ground cover - Naboo's flowers, Yavin's grass.
/// </summary>
/// <remarks>
/// <c>grasspatch</c> had no entry in <see cref="PhxClassRegister"/>, so every
/// instance of one was skipped at import with no error: from the importer's
/// side an unregistered base class is not a failure, it is just a class it does
/// not build. Probing the shipped maps (Tools/SkelProbe --missing) puts that at
/// 66 dropped instances - nab_prop_flowers x52 on Naboo, yav_prop_grass_tall
/// x10 and yav_prop_grass x4 on Yavin.
///
/// Worth saying plainly, because it is the question that led here: Kashyyyk is
/// NOT one of them. kas2 defines kas_prop_grass_tall and then places none, and
/// places no leafpatch either; its greenery is ordinary props
/// (kas2_prop_leaf x32, the tree pieces, kas_prop_treegroup x4) which import
/// normally. If Kashyyyk shows grass in the original that this does not, it is
/// coming from the terrain's own foliage data - the same part of the terrain
/// chunk LibSWBF2 does not parse that the water level lives in (see
/// <see cref="BFMapWater"/>), not from here.
///
/// The class schema is shared with <see cref="PhxLeafPatchClass"/> rather than
/// guessed at. Hashing the property names (--hash) against what the ODFs
/// actually carry confirms eight of grasspatch's ten: MinSize, MaxSize, Alpha,
/// NumParticles, MaxDistance, Texture, DarknessMin and DarknessMax all resolve
/// exactly. So this renders the same way a leaf patch does, because the data
/// says it is the same kind of thing.
///
/// The two that did not resolve are 0x80887a6f and 0x6e74d661. They are a pair:
/// across all three shipped grass classes the second is exactly one greater
/// than the first (kas 3.5/4.5, yav grass 3.0/4.0, yav grass_tall 4.0/5.0), and
/// both grow when the class is the "tall" variant. That is the shape of a
/// min/max extent, so <see cref="Radius"/> below stands in for it with a
/// default in the same range. Naming them properly needs a hash table that has
/// them; until then this is stated rather than pretended.
/// </remarks>
public class PhxGrassPatchClass : PhxClass
{
    public PhxProp<float> MinSize = new PhxProp<float>(1.0f);
    public PhxProp<float> MaxSize = new PhxProp<float>(1.4f);

    public PhxProp<float> Alpha = new PhxProp<float>(1.0f);

    public PhxProp<int> NumParticles = new PhxProp<int>(50);
    public PhxProp<float> MaxDistance = new PhxProp<float>(60f);

    public PhxProp<Texture2D> Texture = new PhxProp<Texture2D>(null);

    /// <summary>
    /// Horizontal spread of the patch.
    /// </summary>
    /// <remarks>
    /// Defaulted rather than read, because the property that carries it is one
    /// of the two unresolved hashes above. 3.5 is the midpoint of what the
    /// three shipped classes state, so an unauthored patch lands where an
    /// authored one would.
    /// </remarks>
    public PhxProp<float> Radius = new PhxProp<float>(3.5f);

    /// <summary>
    /// Vertical spread. Small on purpose: grass grows out of the ground.
    /// </summary>
    /// <remarks>
    /// The one place this must not follow the leaf patch. A leaf patch is a
    /// canopy and scatters through a volume Height tall; grass at that height
    /// is a cloud of billboards hanging in the air over the map. Ground cover
    /// wants its billboards sitting on the surface with only enough vertical
    /// jitter to stop the patch reading as a single flat line.
    /// </remarks>
    public PhxProp<float> Height = new PhxProp<float>(0.15f);

    public PhxProp<float> DarknessMin = new PhxProp<float>(0.0f);
    public PhxProp<float> DarknessMax = new PhxProp<float>(0.0f);
}


public class PhxGrassPatch : PhxInstance<PhxGrassPatchClass>
{
    ParticleSystem Blades;

    public override void Init()
    {
        if (C.Texture.Get() == null)
        {
            return;
        }

        Blades = gameObject.AddComponent<ParticleSystem>();
        Blades.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

        ParticleSystemRenderer renderer = GetComponent<ParticleSystemRenderer>();

        transform.localRotation = Quaternion.identity;

        var emission = Blades.emission;
        emission.enabled = false;

        var shape = Blades.shape;
        shape.enabled = false;

        var main = Blades.main;
        main.startSpeed = 0f;
        main.startLifetime = 1f;
        main.startSize = new ParticleSystem.MinMaxCurve(C.MinSize, C.MaxSize);
        main.maxParticles = C.NumParticles;
        main.simulationSpace = ParticleSystemSimulationSpace.Local;

        // Billboards that stand up rather than face the camera in every axis.
        //
        // A leaf patch uses Facing, which is right for something suspended in a
        // canopy and wrong for something rooted: a camera looking down at
        // Facing grass sees every blade tip toward it, so the patch turns into
        // a plate of texture lying on the ground. Vertical keeps the quad's up
        // axis up and only yaws it toward the viewer, which is what a blade of
        // grass does.
        renderer.alignment = ParticleSystemRenderSpace.Local;
        renderer.renderMode = ParticleSystemRenderMode.VerticalBillboard;
        renderer.minParticleSize = .000001f;

        // maxParticleSize is a FRACTION OF THE VIEWPORT, not a world size - the
        // same clamp EffectsLoader and the leaf patch already apply. Left
        // unclamped, walking into a patch fills the screen with one blade.
        renderer.maxParticleSize = 0.6f;

        Material mat = new Material(Resources.Load<Material>("effects/HDRPParticleNormal"));
        mat.SetTexture(Shader.PropertyToID("Texture2D_23DD87FD"), C.Texture.Get());
        renderer.sharedMaterial = mat;

        byte byteAlpha = (byte)(255f * C.Alpha);

        for (int j = 0; j < C.NumParticles; j++)
        {
            // Darkness is an amount to DARKEN BY, not a brightness - the same
            // inversion the leaf patch documents. Feeding the authored value in
            // directly makes an unauthored patch pure black.
            byte byteDarkness = (byte)(255f * Mathf.Clamp01(
                1f - UnityEngine.Random.Range(C.DarknessMin, C.DarknessMax)));

            var emitParams = new ParticleSystem.EmitParams();
            emitParams.startColor = new Color32(byteDarkness, byteDarkness, byteDarkness, byteAlpha);
            emitParams.startSize = UnityEngine.Random.Range(C.MinSize, C.MaxSize);
            Blades.Emit(emitParams, 1);
        }

        ParticleSystem.Particle[] blades = new ParticleSystem.Particle[C.NumParticles];
        int count = Blades.GetParticles(blades);

        for (int i = 0; i < count; i++)
        {
            // Even spread over the disc: sqrt() on the radius, or the blades
            // bunch toward the centre of the patch and leave its edge bare.
            float angle = UnityEngine.Random.Range(0f, Mathf.PI * 2f);
            float dist = C.Radius * Mathf.Sqrt(UnityEngine.Random.value);

            // Half the blade's own size above the origin, so a vertical
            // billboard is rooted in the ground rather than buried to its
            // middle in it, plus a little jitter so the bases do not all sit on
            // one plane.
            float rooted = blades[i].startSize * 0.5f
                         + UnityEngine.Random.Range(0f, C.Height);

            blades[i].position = new Vector3(
                Mathf.Cos(angle) * dist,
                rooted,
                Mathf.Sin(angle) * dist
            );
        }

        Blades.SetParticles(blades);

        // No second Emit. The loop above already emitted NumParticles and then
        // placed each one; emitting another batch adds the same number again at
        // the origin, where nothing repositions them - a clump at the centre.

        Blades.Pause();
    }

    public override void Destroy() { }
}

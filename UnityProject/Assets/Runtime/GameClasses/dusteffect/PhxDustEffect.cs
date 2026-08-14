using UnityEngine;

/// <summary>
/// A local volume of drifting dust or mist.
/// </summary>
/// <remarks>
/// Placed by the dozen on outdoor and cave maps to give the air some body -
/// blowing snow on Hoth, haze in the Dantooine caves. The base class had no
/// runtime type, so every placement imported as an empty GameObject and the
/// air was completely clear.
///
/// The odf describes a spawn box, a velocity range, size and lifetime ranges,
/// a particle count and a draw distance. That maps onto a box-shaped emitter
/// almost directly; the only judgement is the emission rate, which has to be
/// derived so the steady-state population matches the authored count.
/// </remarks>
public class PhxDustEffect : PhxInstance<PhxDustEffect.ClassProperties>, IPhxTickable
{
    public class ClassProperties : PhxClass
    {
        public PhxProp<Vector3> MinPos = new PhxProp<Vector3>(new Vector3(-10f, 0f, -10f));
        public PhxProp<Vector3> MaxPos = new PhxProp<Vector3>(new Vector3(10f, 5f, 10f));

        public PhxProp<Vector3> MinVel = new PhxProp<Vector3>(Vector3.zero);
        public PhxProp<Vector3> MaxVel = new PhxProp<Vector3>(Vector3.zero);

        public PhxProp<float> MinSize = new PhxProp<float>(1f);
        public PhxProp<float> MaxSize = new PhxProp<float>(2f);

        public PhxProp<float> MinLifetime = new PhxProp<float>(2f);
        public PhxProp<float> MaxLifetime = new PhxProp<float>(4f);

        public PhxProp<float> Alpha = new PhxProp<float>(1f);
        public PhxProp<int> NumParticles = new PhxProp<int>(20);
        public PhxProp<float> MaxDistance = new PhxProp<float>(100f);

        public PhxProp<Texture2D> Texture = new PhxProp<Texture2D>(null);

        // Mist-only. Height scales the volume vertically; the radius fade is
        // recorded for completeness but is not modelled - it needs a shader
        // that knows the emitter centre, which the shared particle material
        // does not.
        public PhxProp<float> HeightScale = new PhxProp<float>(1f);
        public PhxProp<float> RadiusFadeMin = new PhxProp<float>(0f);
        public PhxProp<float> RadiusFadeMax = new PhxProp<float>(0f);
    }

    ParticleSystem Dust;
    ParticleSystemRenderer DustRenderer;
    float MaxDistanceSqr;
    bool Emitting = true;

    public override void Init()
    {
        if (C.Texture.Get() == null) return;

        Vector3 minPos = C.MinPos.Get();
        Vector3 maxPos = C.MaxPos.Get();

        float heightScale = C.HeightScale.Get() > 0f ? C.HeightScale.Get() : 1f;
        minPos.y *= heightScale;
        maxPos.y *= heightScale;

        Vector3 centre = (minPos + maxPos) * 0.5f;
        Vector3 extent = maxPos - minPos;
        extent.x = Mathf.Abs(extent.x);
        extent.y = Mathf.Abs(extent.y);
        extent.z = Mathf.Abs(extent.z);

        Dust = gameObject.AddComponent<ParticleSystem>();
        Dust.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

        DustRenderer = GetComponent<ParticleSystemRenderer>();

        float minLife = Mathf.Max(0.1f, C.MinLifetime.Get());
        float maxLife = Mathf.Max(minLife, C.MaxLifetime.Get());
        int count = Mathf.Max(1, C.NumParticles.Get());

        ParticleSystem.MainModule main = Dust.main;
        main.startLifetime = new ParticleSystem.MinMaxCurve(minLife, maxLife);
        main.startSize = new ParticleSystem.MinMaxCurve(C.MinSize.Get(), C.MaxSize.Get());
        main.startSpeed = 0f;      // motion comes from velocityOverLifetime
        main.maxParticles = count;
        main.startColor = new Color(1f, 1f, 1f, Mathf.Clamp01(C.Alpha.Get()));

        // The volume is authored around the object, so the particles have to
        // stay put in the world while the player moves through them.
        main.simulationSpace = ParticleSystemSimulationSpace.World;

        ParticleSystem.ShapeModule shape = Dust.shape;
        shape.enabled = true;
        shape.shapeType = ParticleSystemShapeType.Box;
        shape.position = centre;
        shape.scale = extent;

        // A box emitter holds NumParticles only if it replaces them as fast as
        // they expire: rate = population / mean lifetime. Emitting NumParticles
        // per second instead would over- or under-fill the volume by whatever
        // factor the lifetime happens to be.
        ParticleSystem.EmissionModule emission = Dust.emission;
        emission.enabled = true;
        emission.rateOverTime = count / ((minLife + maxLife) * 0.5f);

        Vector3 minVel = C.MinVel.Get();
        Vector3 maxVel = C.MaxVel.Get();
        if (minVel != Vector3.zero || maxVel != Vector3.zero)
        {
            ParticleSystem.VelocityOverLifetimeModule vel = Dust.velocityOverLifetime;
            vel.enabled = true;
            vel.space = ParticleSystemSimulationSpace.World;
            vel.x = new ParticleSystem.MinMaxCurve(minVel.x, maxVel.x);
            vel.y = new ParticleSystem.MinMaxCurve(minVel.y, maxVel.y);
            vel.z = new ParticleSystem.MinMaxCurve(minVel.z, maxVel.z);
        }

        DustRenderer.alignment = ParticleSystemRenderSpace.Facing;
        DustRenderer.minParticleSize = 0.000001f;

        // maxParticleSize is a fraction of the viewport, not a world size - the
        // same trap the leaf patches fell into. Dust sprites are authored large
        // (15-20 units), so an unclamped billboard near the camera fills the
        // screen with flat texture.
        DustRenderer.maxParticleSize = 0.6f;

        Material mat = new Material(Resources.Load<Material>("effects/HDRPParticleNormal"));
        mat.SetTexture(Shader.PropertyToID("Texture2D_23DD87FD"), C.Texture.Get());
        DustRenderer.sharedMaterial = mat;

        float maxDist = C.MaxDistance.Get();
        MaxDistanceSqr = maxDist > 0f ? maxDist * maxDist : float.MaxValue;

        Dust.Play();
    }

    public override void Destroy()
    {
    }

    public void Tick(float deltaTime)
    {
        if (Dust == null) return;

        // MaxDistance is a draw distance in the source data. Unity will happily
        // simulate every volume on the map at once, so honour it - a map with
        // eighty dust boxes is eighty emitters otherwise.
        Camera cam = Camera.main;
        if (cam == null) return;

        bool inRange = (cam.transform.position - transform.position).sqrMagnitude <= MaxDistanceSqr;
        if (inRange == Emitting) return;

        Emitting = inRange;
        ParticleSystem.EmissionModule emission = Dust.emission;
        emission.enabled = inRange;

        if (!inRange)
        {
            Dust.Clear(true);
        }
    }
}

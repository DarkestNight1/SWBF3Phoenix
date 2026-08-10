using UnityEngine;

/// <summary>
/// A console a soldier stands at and holds Use on: sabotage terminals, the
/// panels campaign objectives make you hack, turret and droid control
/// stations.
/// </summary>
/// <remarks>
/// The <c>remoteterminal</c> base class was unregistered. On a map whose
/// objective is "use the terminal", the terminal imported as a mesh with no
/// behaviour, so the objective could not be completed at all.
///
/// The interaction is deliberately the same shape as capturing a command post:
/// stand inside a radius, hold, progress accrues and decays if you leave. That
/// is the interaction the original game uses here, and reusing it means HUD
/// and AI can treat the two the same way.
/// </remarks>
public class PhxRemoteTerminal : PhxInstance<PhxRemoteTerminal.ClassProperties>,
                                  IPhxTickable, IPhxDamageableInstance, IPhxDestructible
{
    static PhxScene SCENE => PhxGame.GetScene();

    public class ClassProperties : PhxClass
    {
        public PhxProp<string> GeometryName = new PhxProp<string>("");

        /// <summary>Seconds of continuous use to complete it.</summary>
        public PhxProp<float> UseTime = new PhxProp<float>(5f);

        /// <summary>How close a soldier must stand.</summary>
        public PhxProp<float> UseRadius = new PhxProp<float>(3f);

        public PhxProp<float> MaxHealth = new PhxProp<float>(200f);

        /// <summary>Effect played while someone is using it.</summary>
        public PhxProp<string> ActiveEffect = new PhxProp<string>(null);

        public PhxProp<PhxClass> ExplosionName = new PhxProp<PhxClass>(null);
    }

    public PhxProp<float> CurHealth = new PhxProp<float>(200f);

    /// <summary>0 at rest, 1 when complete.</summary>
    public float Progress { get; private set; }

    /// <summary>Whether the terminal has already been used to completion.</summary>
    public bool IsUsed { get; private set; }

    /// <summary>Team of the soldier who completed it, or 0.</summary>
    public int UsedByTeam { get; private set; }

    /// <summary>Raised once, when a soldier finishes using it.</summary>
    public System.Action<PhxRemoteTerminal, PhxSoldier> OnUsed;

    // How fast progress drains when nobody is using it, relative to how fast
    // it fills. Slower than it fills so stepping away briefly under fire does
    // not undo everything, which is how the original reads to play.
    const float DecayRate = 0.5f;

    static readonly Collider[] OverlapCache = new Collider[32];

    PhxEffect ActiveEffect;
    bool EffectPlaying;
    bool Destroyed;

    public override void Init()
    {
        Rigidbody body = gameObject.AddComponent<Rigidbody>();
        body.isKinematic = true;

        gameObject.layer = LayerMask.NameToLayer("BuildingAll");
        CurHealth.Set(C.MaxHealth.Get());

        string effect = C.ActiveEffect.Get();
        if (!string.IsNullOrEmpty(effect) && SCENE != null)
        {
            ActiveEffect = SCENE.EffectsManager.LendEffect(effect);
            if (ActiveEffect != null)
            {
                ActiveEffect.SetParent(transform);
                ActiveEffect.SetLooping(true);
                ActiveEffect.Stop();
            }
        }

        PhxDestructionRegistry.Register(this);
    }

    public override void Destroy()
    {
        SetEffectPlaying(false);
        PhxDestructionRegistry.Unregister(this);
    }

    // ------------------------------------------------------ IPhxDestructible
    public PhxDestructibleKind DestructibleKind => PhxDestructibleKind.MissionObject;
    public GameObject GetGameObject() => gameObject;
    public string GetDestructibleName() => name;
    public int GetTeam() => Team.Get();
    public float GetHealth() => CurHealth.Get();
    public float GetMaxHealth() => C == null ? 0f : C.MaxHealth.Get();
    public bool IsDestroyed => Destroyed;

    public void Tick(float deltaTime)
    {
        if (IsUsed || Destroyed) return;

        PhxSoldier user = FindUser();
        if (user == null)
        {
            SetEffectPlaying(false);
            Progress = Mathf.Max(0f, Progress - deltaTime * DecayRate / Mathf.Max(0.01f, C.UseTime.Get()));
            return;
        }

        SetEffectPlaying(true);
        Progress += deltaTime / Mathf.Max(0.01f, C.UseTime.Get());
        if (Progress < 1f) return;

        Progress = 1f;
        IsUsed = true;
        UsedByTeam = user.Team.Get();
        SetEffectPlaying(false);

        OnUsed?.Invoke(this, user);

        // Scripts wait on this by object name; it is what turns "the player
        // held Use on the panel" into a completed mission objective.
        int? objIdx = SCENE?.GetInstanceIndex(this);
        PhxLuaEvents.InvokeParameterized(PhxLuaEvents.Event.OnObjectKillName, gameObject.name.ToLower(), objIdx);
        PhxLuaEvents.InvokeParameterized(PhxLuaEvents.Event.OnObjectKillTeam, UsedByTeam, objIdx);

        Debug.Log($"[Phoenix] Remote terminal '{name}' used by team {UsedByTeam}.");
    }

    /// <summary>
    /// A living soldier in range with Use held. AI can drive this too, since
    /// it reads the same controller field player input writes.
    /// </summary>
    PhxSoldier FindUser()
    {
        int count = Physics.OverlapSphereNonAlloc(transform.position, C.UseRadius.Get(),
                                                  OverlapCache, ~0, QueryTriggerInteraction.Ignore);
        for (int i = 0; i < count; ++i)
        {
            Collider coll = OverlapCache[i];
            if (coll == null) continue;

            PhxSoldier soldier = coll.GetComponentInParent<PhxSoldier>();
            if (soldier == null || soldier.IsDead) continue;

            PhxPawnController controller = soldier.GetController();
            if (controller == null || !controller.Enter) continue;

            return soldier;
        }
        return null;
    }

    void SetEffectPlaying(bool playing)
    {
        if (ActiveEffect == null || playing == EffectPlaying) return;

        EffectPlaying = playing;
        if (playing) ActiveEffect.Play();
        else ActiveEffect.Stop();
    }

    public void AddDamage(float damage)
    {
        if (Destroyed) return;

        CurHealth.Set(CurHealth.Get() - damage);
        if (CurHealth.Get() > 0f) return;

        Destroyed = true;
        SetEffectPlaying(false);
        PhxExplosionManager.AddExplosion(null, C.ExplosionName.Get() as PhxExplosionClass,
                                         transform.position, transform.rotation);
        PhxDestructionRegistry.NotifyDestroyed(this);

        int? objIdx = SCENE?.GetInstanceIndex(this);
        PhxLuaEvents.InvokeParameterized(PhxLuaEvents.Event.OnObjectKillName, gameObject.name.ToLower(), objIdx);
        PhxLuaEvents.InvokeParameterized(PhxLuaEvents.Event.OnObjectKillTeam, Team.Get(), objIdx);
    }
}

using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering.HighDefinition;
using LibSWBF2.Wrappers;

public class PhxCommandpost : PhxInstance<PhxCommandpost.ClassProperties>, IPhxTickable
{
    PhxMatch Match => PhxGame.GetMatch();
    PhxScene Scene => PhxGame.GetScene();

    public class ClassProperties : PhxClass
    {
        public PhxProp<float> NeutralizeTime = new PhxProp<float>(1.0f);
        public PhxProp<float> CaptureTime    = new PhxProp<float>(1.0f);
        public PhxProp<float> HoloTurnOnTime = new PhxProp<float>(1.0f);
        public PhxMultiProp   ChargeSound    = new PhxMultiProp(typeof(AudioClip), typeof(string));
        public PhxMultiProp   CapturedSound  = new PhxMultiProp(typeof(AudioClip), typeof(string));
        public PhxMultiProp   DischargeSound = new PhxMultiProp(typeof(AudioClip), typeof(string));
        public PhxMultiProp   LostSound      = new PhxMultiProp(typeof(AudioClip), typeof(string));

        public PhxMultiProp HoloImageGeometry = new PhxMultiProp(typeof(string), typeof(string));
        public PhxProp<PhxClass> HoloOdf      = new PhxProp<PhxClass>(null);

        public PhxProp<Texture2D> MapTexture = new PhxProp<Texture2D>(null);
        public PhxProp<float>     MapScale   = new PhxProp<float>(1.0f);
    }

    // SWBF Instance Properties
    public PhxProp<PhxRegion> CaptureRegion = new PhxProp<PhxRegion>(null);
    public PhxProp<PhxRegion> ControlRegion = new PhxProp<PhxRegion>(null);
    public PhxProp<SWBFPath>  SpawnPath     = new PhxProp<SWBFPath>(null);

    [Header("References")]
    public LineRenderer HoloRay;
    public PhxHoloIcon HoloIcon;
    public HDAdditionalLightData Light;

    [Header("Settings")]
    public Vector2 CapturePitch = new Vector2(0.5f, 1.5f);
    public float   HoloPresenceSpeed = 1.0f;


    public int   CaptureTeam { get; private set; }
    public bool  CaptureDisputed { get; private set; }
    public bool  CaptureToNeutral { get; private set; }

    public float CaptureTimer;
    int   CaptureCount;
    AudioSource AudioAction;
    AudioSource AudioAmbient;
    AudioSource AudioCapture;
    HashSet<PhxPawnController> CaptureControllers = new HashSet<PhxPawnController>();

    // cache
    bool  bInitInstance => C != null;
    float HoloWidthStart;
    float HoloWidthEnd;
    float LightIntensity;
    Color HoloColor;
    float HoloAlpha;
    float HoloPresence = 1.0f;
    float HoloPresenceDest = 1.0f;
    float HoloPresenceVel;
    float LastHoloPresence;


    public override void Init()
    {
        Transform hpHolo = transform.Find(string.Format("{0}/hp_hologram", C.Name));
        if (hpHolo != null)
        {
            GameObject holoPrefab = Resources.Load<GameObject>("cp_holo");
            GameObject holo = Instantiate(holoPrefab, hpHolo);
            HoloRay = holo.GetComponent<LineRenderer>();
            Light = holo.GetComponentInChildren<HDAdditionalLightData>();

            HoloWidthStart = HoloRay.startWidth;
            HoloWidthEnd = HoloRay.endWidth;
            HoloAlpha = HoloRay.material.GetColor("_UnlitColor").a;

            // The prefab light is a 15 m point light at 800 lumens with
            // shadows off and a 10 km fade distance. Unshadowed, that lights
            // everything within 15 m through any wall between - a post behind
            // a bulkhead lit the corridor on the other side of it, and on an
            // interior map several posts together washed the whole space in
            // team colour. Constrain it to the projector's own pool and let it
            // cast, so geometry stops it.
            LightIntensity = BFLocalLightPolicy.Apply(Light);
        }

        AudioAmbient = gameObject.AddComponent<AudioSource>();
        AudioAmbient.spatialBlend = 1.0f;
        AudioAmbient.clip = SoundLoader.Instance.LoadSound("com_blg_commandpost2");
        AudioAmbient.pitch = 1.0f;
        AudioAmbient.volume = 0.5f;
        AudioAmbient.rolloffMode = AudioRolloffMode.Linear;
        AudioAmbient.minDistance = 2.0f;
        AudioAmbient.maxDistance = 30.0f;
        AudioAmbient.Play();

        AudioCapture = gameObject.AddComponent<AudioSource>();
        AudioCapture.spatialBlend = 1.0f;
        AudioCapture.loop = true;
        AudioCapture.pitch = 1.0f;
        AudioCapture.volume = 0.8f;
        AudioCapture.rolloffMode = AudioRolloffMode.Linear;
        AudioCapture.minDistance = 2.0f;
        AudioCapture.maxDistance = 30.0f;

        AudioAction = gameObject.AddComponent<AudioSource>();
        AudioAction.spatialBlend = 1.0f;
        AudioAction.loop = false;
        AudioAction.pitch = 1.1f;
        AudioAction.volume = 0.5f;
        AudioAction.rolloffMode = AudioRolloffMode.Linear;
        AudioAction.minDistance = 2.0f;
        AudioAction.maxDistance = 30.0f;


        Team.OnValueChanged += ApplyTeam;

        // Either property can name the capture zone, and either can arrive
        // after Init during InitInstance's property assignment - so both
        // re-run the same hookup rather than each owning half of it.
        CaptureRegion.OnValueChanged += (PhxRegion _) => UpdateCaptureRegion();
        ControlRegion.OnValueChanged += (PhxRegion _) => UpdateCaptureRegion();

        UpdateCaptureRegion();

        if (C.HoloOdf.Get() != null)
        {
            HoloIcon = (PhxHoloIcon)Scene.CreateInstance(C.HoloOdf, $"{name}_{C.HoloOdf.Get().Name}", new Vector3(0.0f, 4.7f, 0.0f), Quaternion.identity, false, hpHolo != null ? hpHolo : gameObject.transform);
        }
    }
    
    public override void Destroy()
    {
        
    }

    // The zone currently driving capture, and our own stand-in if the map
    // supplied neither region.
    PhxRegion ActiveCaptureRegion;
    SphereCollider FallbackCollider;
    PhxRegion FallbackRegion;

    // Radius of the stand-in zone. BF2's own control zones are roughly this
    // across; only used when a post names no region at all.
    const float FallbackCaptureRadius = 8f;

    static readonly HashSet<string> ReportedCaptureSource = new HashSet<string>();

    /// <summary>
    /// Forget which posts have been reported. Statics outlive a match, so
    /// without this a warning raised on one map stays suppressed for the rest
    /// of the session - including on maps where it would be a different
    /// problem. Same shape as PhxBF3AIController.ResetDiagnostics.
    /// </summary>
    public static void ResetDiagnostics()
    {
        ReportedCaptureSource.Clear();
    }

    /// <summary>
    /// Hook whichever region actually drives this post's capture.
    /// </summary>
    /// <remarks>
    /// Capture is entirely trigger-driven: no region means nothing is ever
    /// added to CaptureControllers, CaptureCount stays 0, Tick's capture branch
    /// never runs, the team never changes - and since the holo animation and
    /// colour only update from ApplyTeam, the post also never visibly reacts.
    /// A post with no usable region is therefore not "hard to capture", it is
    /// silently impossible.
    ///
    /// Only CaptureRegion was ever consulted; ControlRegion was declared and
    /// never read, so a post naming its zone there could never be taken. Both
    /// are tried here, and if neither resolves we fall back to a sphere around
    /// the post - the same approach PhxPowerupstation uses for droids without
    /// an authored region.
    /// </remarks>
    void UpdateCaptureRegion()
    {
        if (ActiveCaptureRegion != null)
        {
            ActiveCaptureRegion.OnEnter -= AddToCapture;
            ActiveCaptureRegion.OnLeave -= RemoveFromCapture;
        }

        PhxRegion region = CaptureRegion.Get() ?? ControlRegion.Get();
        string source = CaptureRegion.Get() != null ? "CaptureRegion"
                      : ControlRegion.Get() != null ? "ControlRegion"
                      : "fallback sphere";

        if (region == null)
        {
            if (FallbackRegion == null)
            {
                FallbackCollider = gameObject.AddComponent<SphereCollider>();
                FallbackCollider.radius = FallbackCaptureRadius;
                FallbackCollider.isTrigger = true;
                FallbackRegion = gameObject.AddComponent<PhxRegion>();
            }
            region = FallbackRegion;
        }
        else if (FallbackRegion != null)
        {
            // An authored region arrived - retire the stand-in so a soldier
            // isn't counted twice.
            Destroy(FallbackRegion);
            Destroy(FallbackCollider);
            FallbackRegion = null;
            FallbackCollider = null;
        }

        ActiveCaptureRegion = region;
        ActiveCaptureRegion.OnEnter += AddToCapture;
        ActiveCaptureRegion.OnLeave += RemoveFromCapture;

        // Say which path each post took, once. If posts are falling back, the
        // region names in the map data aren't resolving and that is worth
        // knowing directly rather than inferring from "capture feels broken".
        if (ReportedCaptureSource.Add(name))
        {
            Debug.Log($"[Commandpost] '{name}' capture zone from {source}.");
        }
    }

    public float GetCaptureProgress()
    {
        return CaptureTimer / C.CaptureTime;
    }

    public void RemoveFromCapture(IPhxControlableInstance other)
    {
        PhxPawnController controller = other.GetController();
        if (controller != null)
        {
            CaptureControllers.Remove(controller);
            controller.CapturePost = null;
            RefreshCapture();
        }
    }

    public void UpdateColor()
    {
        HoloColor = PhxGame.GetMatch().GetTeamColor(Team);
        HoloRay?.material.SetColor("_EmissiveColor", HoloColor);
        if (Light != null)
        {
            // Tinted rather than the raw team colour. A team colour is a fully
            // saturated primary, and a light emitting in one channel makes
            // every surface it touches a silhouette in that channel - which is
            // why a green post read as a green filter over the frame instead
            // of as a light in the room. The hue still reads clearly.
            Light.color = BFLocalLightPolicy.TintForLight(HoloColor);
        }
    }

    public void Tick(float deltaTime)
    {
        if (!bInitInstance) return;

        if (CaptureCount > 0)
        {
            if (CaptureDisputed)
            {
                // TODO: play dispute sound
            }
            else
            {
                float progress;
                float captureMultiplier = Mathf.Sqrt(CaptureCount);
                CaptureTimer += deltaTime * captureMultiplier;
                if (Team == 0)
                {
                    if (C.CaptureTime - CaptureTimer <= HoloPresenceSpeed * captureMultiplier * 2f)
                    {
                        HoloPresenceDest = 0.0f;
                    }

                    progress = CaptureTimer / C.CaptureTime;
                    if (CaptureTimer >= C.CaptureTime)
                    {
                        Team.Set(CaptureTeam);
                        progress = 0.0f;
                    }

                    CaptureToNeutral = false;
                    AudioCapture.pitch = Mathf.Lerp(CapturePitch.x, CapturePitch.y, progress);
                }
                else if (CaptureTeam != Team)
                {
                    if (C.NeutralizeTime - CaptureTimer <= HoloPresenceSpeed * captureMultiplier * 2f)
                    {
                        HoloPresenceDest = 0.0f;
                    }

                    progress = CaptureTimer / C.NeutralizeTime;
                    if (CaptureTimer >= C.NeutralizeTime)
                    {
                        Team.Set(0);
                        progress = 0.0f;
                    }

                    CaptureToNeutral = true;
                    AudioCapture.pitch = Mathf.Lerp(CapturePitch.y, CapturePitch.x, progress);
                }
            }
        }
        else
        {
            HoloPresenceDest = 1.0f;
            CaptureTimer = Mathf.Max(CaptureTimer - deltaTime * 0.1f, 0.0f);
            AudioCapture.pitch = 0.0f;
        }

        HoloPresence = Mathf.SmoothDamp(HoloPresence, HoloPresenceDest, ref HoloPresenceVel, HoloPresenceSpeed);
        //HoloPresence = Mathf.Lerp(HoloPresence, HoloPresenceDest, deltaTime * HoloPresenceSpeed);
        if (HoloPresence != LastHoloPresence)
        {
            HoloColor.a = Mathf.Lerp(0.0f, HoloAlpha, HoloPresence);
            if (Light != null)
            {
                Light.intensity = Mathf.Lerp(0.0f, LightIntensity, HoloPresence);
            }
            if (HoloRay != null)
            {
                HoloRay.startWidth = Mathf.Lerp(0.0f, HoloWidthStart, HoloPresence);
                HoloRay.endWidth   = Mathf.Lerp(0.0f, HoloWidthEnd, HoloPresence);
                HoloRay.material.SetColor("_UnlitColor", HoloColor);
            }
            LastHoloPresence = HoloPresence;
        }
    }

    void AddToCapture(IPhxControlableInstance other)
    {
        PhxPawnController controller = other.GetController();
        if (controller != null)
        {
            CaptureControllers.Add(controller);
            controller.CapturePost = this;
            RefreshCapture();
        }
    }

    void RefreshCapture()
    {
        CaptureTeam = 0;
        CaptureDisputed = false;
        ushort[] teamCounts = new ushort[255];

        foreach (PhxPawnController controller in CaptureControllers)
        {
            if (++teamCounts[controller.Team] > teamCounts[CaptureTeam])
            {
                CaptureTeam = controller.Team;
            }

            if (CaptureTeam > 0)
            {
                CaptureDisputed = CaptureDisputed ||
                    (controller.Team != CaptureTeam && !Match.IsFriend(controller.Team, CaptureTeam));
            }
        }

        CaptureCount = teamCounts[CaptureTeam];
    }

    public void ChangeIcon()
    {
        if (CaptureToNeutral) {
            if (HoloIcon != null)
                HoloIcon.Hide();
            return; 
        }
        // HoloIcon is only created when the class has a HoloOdf that resolves,
        // so it is legitimately null on posts whose hologram data is missing -
        // every other branch here already tests for that. This one did not, and
        // threw during ApplyTeam while the scene was still being imported,
        // which aborted the rest of the instance import.
        if (HoloIcon == null) return;

        EnsureTeamHologram();

        HoloIcon.LoadIcon(Match.GetTeamHologram(Team), Team);
        HoloIcon.Show();
    }

    // Team is a 1-based team number; indexing Match.Teams with it directly
    // read the wrong team's hologram (and overran the array for the last
    // team), so go through the converting accessors.
    void EnsureTeamHologram()
    {
        if (Match.GetTeamHologram(Team) == null)
        {
            GameObject icon = null;
            LoadIcon(ref icon);
            Match.SetTeamHologram(Team, icon);
        }
    }

    public void ChangeColorIcon()
    {
        if (CaptureToNeutral) {
            if(HoloIcon!=null)
                HoloIcon.Hide();
            return; 
        }
        // Same unguarded dereference as ChangeIcon had - see the note there.
        if (HoloIcon == null) return;

        EnsureTeamHologram();

        HoloIcon.ChangeColorIcon(Team);
        HoloIcon.Show();
    }

    private void LoadIcon(ref GameObject icon)
    {
        string name = Match.getTeamName(Team); //Odf use full name but teams only 3 first chars
        if (string.IsNullOrEmpty(name)) return;   // unnamed/out-of-range team - nothing to match against
        if (name.Equals("imp")) { name = "emp"; } //Do not know how to solve better atm

        for (int i = 0; i < C.HoloImageGeometry.GetCount(); i++)
        {
            // The odf's team field is matched on its first three characters.
            // Substring(0, 3) throws on any entry shorter than that, and a
            // throw here escapes all the way out of the scene import - so a
            // single malformed HoloImageGeometry row could cost the whole map.
            string odfTeam = C.HoloImageGeometry.Get<string>(1, i);
            if (odfTeam == null || odfTeam.Length < 3) continue;

            if (name.Equals(odfTeam.Substring(0, 3).ToLower()))
            {
                icon = ModelLoader.Instance.GetGameObjectFromModel(C.HoloImageGeometry.Get<string>(0, i), "");
            }
        }

        //To be destroy and be invisible
        //Vector3 scale = new Vector3(0, 0, 0);
        if (icon != null)
        {
            icon.transform.localScale = new Vector3(0, 0, 0);
            icon.transform.parent = gameObject.transform; //Maybe has to be changed now
        }
    }

    void ApplyTeam(int oldTeam)
    {
        if (Team == oldTeam)
        {
            // nothing to do
            return;
        }

        CaptureTimer = 0.0f;

        AudioCapture.clip = Team == 0 ? C.ChargeSound.Get<AudioClip>(0) : C.DischargeSound.Get<AudioClip>(0);
        AudioCapture.Play();

        AudioAmbient.loop = true;
        AudioAction.clip = Team == 0 ? C.LostSound.Get<AudioClip>(0) : C.CapturedSound.Get<AudioClip>(0);
        AudioAction.Play();

        HoloPresence = 0.0f;
        HoloPresenceDest = 1.0f;

        if (Team == 0)
        {
            PhxLuaEvents.Invoke(PhxLuaEvents.Event.OnFinishNeutralize, Scene.GetInstanceIndex(this));
            BFEventBus.Raise(BFEvent.CommandPostNeutralized, Team.Get(), gameObject, name: name);
        }
        else
        {
            PhxLuaEvents.Invoke(PhxLuaEvents.Event.OnFinishCapture, Scene.GetInstanceIndex(this));
            PhxLuaEvents.Invoke(PhxLuaEvents.Event.OnFinishCaptureName, name, Scene.GetInstanceIndex(this));
            PhxLuaEvents.Invoke(PhxLuaEvents.Event.OnFinishCaptureTeam, Team.Get(), Scene.GetInstanceIndex(this));

            // Announced to engine systems too, not only to Lua. The AI's
            // objective evaluation, the HUD and scoring all need to know a
            // post changed hands, and wiring each of them to this class
            // individually is how a system ends up depending on twenty others.
            BFEventBus.Raise(BFEvent.CommandPostCaptured, Team.Get(), gameObject, name: name);
        }

        RefreshCapture();
        UpdateColor();
        ChangeIcon();
    }

    void OnDrawGizmos()
    {
        SWBFPath path = SpawnPath.Get();
        if(path != null)
        {
            Gizmos.color = Color.green;
            for(int i = 1; i < path.Nodes.Length; i++)
            {
                SWBFPath.Node nodePrevious = path.Nodes[i - 1];
                SWBFPath.Node nodeCurrent = path.Nodes[i];
                Gizmos.DrawLine(nodePrevious.Position, nodeCurrent.Position);
            }
        }
    }

}

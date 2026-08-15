using System;
using System.Linq;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Animations;
using LibSWBF2.Utils;
using System.Runtime.ExceptionServices;


public enum PhxWeaponState : int
{
    Reloading,
    Overheated,
    ShotDelayed,
    SalvoDelayed,
    Charging,
    Free
}


public class PhxGenericWeapon : PhxInstance<PhxGenericWeapon.ClassProperties>, IPhxWeapon, IPhxTickable
{
	protected PhxGame Game => PhxGame.Instance;
    protected PhxScene Scene => PhxGame.GetScene();

    protected Action ShotCallback;
    protected Action ReloadCallback;
    protected AudioSource Audio;

    /// <summary>Authored pitch for the fire sound, 1 when the data says nothing.</summary>
    protected float BasePitch = 1f;

    /// <summary>How far either side of the authored pitch a shot may land.</summary>
    const float PitchJitter = 0.04f;

    protected int Ammunition;
    protected int MagazineAmmo;

    protected float FireDelay;
    protected float ReloadDelay;

    protected float SalvoDelayTimer;


    PhxWeaponState WeaponState = PhxWeaponState.Free;


    protected PhxPawnController OwnerController;

    // Colliders that spawned ordnance should not collide with, ie,
    // that of the OwnerController's soldier or vehicle
    protected List<Collider> IgnoredColliders;


    protected Transform FirePoint;




	public class ClassProperties : PhxClass
	{
        public PhxProp<string> AnimationBank = new PhxProp<string>("");
        public PhxProp<string> GeometryName = new PhxProp<string>("");

        // Various state time values
        public PhxProp<float> ShotDelay = new PhxProp<float>(0.5f);
        public PhxProp<float> InitialSalvoDelay = new PhxProp<float>(0f);
        public PhxProp<float> ReloadTime = new PhxProp<float>(1.0f);
	    public PhxProp<string> SalvoDelay = new PhxProp<string>("1.0");
	    public PhxProp<int>   SalvoCount = new PhxProp<int>(1);
	    public PhxProp<int>   ShotsPerSalvo = new PhxProp<int>(1);

        public PhxProp<bool> TriggerSingle = new PhxProp<bool>(false);

        public PhxProp<bool> OffhandWeapon = new PhxProp<bool>(false);

	    public PhxProp<int> RoundsPerClip = new PhxProp<int>(50);

	    public PhxProp<PhxClass> OrdnanceName = new PhxProp<PhxClass>(null);

	    // Sound
	    public PhxProp<string> FireSound = new PhxProp<string>(null);

	    public PhxProp<float> HeatRecoverRate = new PhxProp<float>(0.25f);
	    public PhxProp<float> HeatThreshold = new PhxProp<float>(0.2f); 
	    public PhxProp<float> HeatPerShot = new PhxProp<float>(0.12f);

        // Old spread system
        public PhxProp<float> PitchSpread = new PhxProp<float>(0f);
        public PhxProp<float> YawSpread   = new PhxProp<float>(0f);

        // New spread system see: https://sites.google.com/site/swbf2modtoolsdocumentation/weapon_notes
        public PhxProp<float> SpreadPerShot = new PhxProp<float>(0f);
        public PhxProp<float> SpreadRecoverRate = new PhxProp<float>(0f); 
        public PhxProp<float> SpreadThreshold = new PhxProp<float>(0f); 
        public PhxProp<float> SpreadLimit = new PhxProp<float>(0f);

        // Applied before or after spread?
        public PhxProp<float> ShotElevate = new PhxProp<float>(0f);        
	}


    // Need a better name for this, but will replace IPhxWeapon.IsFiring()
    public bool IsTriggerPressed;

    protected bool CanFire = true;

    float SalvoDelay;


    public override void Init()
    {   
        if (C.FireSound.Get() != null)
        {
            AudioClip FireSound = SoundLoader.Instance.LoadSound(C.FireSound.Get());

            if (FireSound != null)
            {
                Audio = gameObject.AddComponent<AudioSource>();
                Audio.playOnAwake = false;
                Audio.spatialBlend = 1.0f;

                // Logarithmic, not linear. Weapon fire is impulsive: loud up
                // close, falling away fast, then faintly present for a long
                // way. Linear falloff makes distant fire fade evenly to
                // nothing, which is what turns a battle into a series of
                // isolated local skirmishes.
                Audio.rolloffMode = AudioRolloffMode.Logarithmic;

                // Falloff comes from the sound's own authored data where it
                // exists. The parser has been reading MinDistance/MaxDistance
                // and Pitch out of the .snd configs all along; this hardcoded
                // 2 to 30 metres ignored them, and 30 metres is roughly one
                // building. Every firefight beyond that was silent, on maps
                // hundreds of metres across.
                if (SoundLoader.Instance != null &&
                    SoundLoader.Instance.TryGetProperties(C.FireSound.Get(),
                                                          out SoundLoader.SoundProperties props) &&
                    props.HasDistances)
                {
                    Audio.minDistance = props.MinDistance;
                    Audio.maxDistance = props.MaxDistance;
                    BasePitch = props.Pitch > 0f ? props.Pitch : 1f;
                }
                else
                {
                    Audio.minDistance = 8f;
                    Audio.maxDistance = 180f;
                }

                Audio.loop = false;
                Audio.clip = FireSound;
            }
        }

        if (C.OrdnanceName.Get() == null)
        {
            Debug.LogWarning($"Missing Ordnance class in weapon '{name}'!");
        }

        if (transform.childCount > 0)
        {
            FirePoint = transform.GetChild(0).Find("hp_fire");
        }

        if (FirePoint == null)
        {
            //Debug.LogWarning($"Cannot find 'hp_fire' in '{name}', class '{C.Name}'!");
            FirePoint = transform;
        }

        // Total amount of 4 magazines
        Ammunition = C.RoundsPerClip * 3;
        MagazineAmmo = C.RoundsPerClip;


        // Implied, but need to confirm 
        if (C.SpreadPerShot > 0.001f)
        {
            bUsesNewSpreadSystem = true;
        }

        SalvoDelay = float.Parse(C.SalvoDelay.Get().Split(new string[]{" "}, StringSplitOptions.RemoveEmptyEntries)[0],
                                System.Globalization.CultureInfo.InvariantCulture);
    }

    public override void Destroy()
    {
        
    }

    public void SetFirePoint(Transform FP)
    {
        FirePoint = FP;
    }



    public virtual void GetFirePoint(out Vector3 Pos, out Quaternion Rot)
    {
        if (FirePoint == null)
        {
            Pos = transform.position;
            Rot = transform.rotation;
        }
        else 
        {
            Pos = FirePoint.position;
            Rot = FirePoint.rotation;
        }
    }


    public virtual Transform GetFirePoint()
    {
        return FirePoint;
    }


    /// <summary>Whether the weapon is part-way through a shot.</summary>
    /// <remarks>
    /// Was a hardcoded `true`, which made it useless to ask. The meaning here
    /// matches PhxMeleeWeapon's "mid swing": the shot delay and the salvo
    /// delay are both parts of a shot already under way, and a held trigger on
    /// a free weapon is the moment before the next one.
    /// </remarks>
    public bool IsFiring()
    {
        return WeaponState == PhxWeaponState.ShotDelayed
            || WeaponState == PhxWeaponState.SalvoDelayed
            || (IsTriggerPressed && CanFire && WeaponState == PhxWeaponState.Free);
    }


    public void SetOwnerController(PhxPawnController Owner)
    {
        OwnerController = Owner;
    }


    public PhxPawnController GetOwnerController()
    {
        return OwnerController;
    }


    public void SetIgnoredColliders(List<Collider> Colliders)
    {
        IgnoredColliders = Colliders;
    }

    public List<Collider> GetIgnoredColliders()
    {
        if (IgnoredColliders == null)
        {
            return new List<Collider>();
        }
        return IgnoredColliders;
    }


    public PhxInstance GetInstance()
    {
        return this;
    }


    int SalvoIndex = 0;
    Vector3 SalvoTargetPosition;

    Vector3 SpreadAxis = Vector3.zero;
    float CurrSpread, EffectiveSpread;
    Quaternion SpreadQuat, ShotElevationQuat;

    protected bool bUsesNewSpreadSystem = false;

	public virtual bool Fire(PhxPawnController owner, Vector3 targetPos)
    {
        // Remember who pulled the trigger. This is the whole attribution
        // chain, and it was missing: Fire took an owner and dropped it, so
        // OwnerController stayed null, so every bolt reported a null
        // instigator. Which meant no kill was ever credited for gunfire, no
        // team kill was ever detected, no hit marker appeared, the death
        // camera never knew who to look at, and - because the perception call
        // below is gated on this field - the AI could not hear a shot fired.
        // Saber kills worked the whole time, because PhxMeleeWeapon captures
        // its owner. That is the contract; this is it applied here.
        //
        // The ?? matters. Tick re-enters Fire with a null owner on the
        // salvo-delay and trigger-held paths, so a plain assignment would
        // credit the first round of a burst and orphan the rest.
        OwnerController = owner ?? OwnerController;

        if (WeaponState == PhxWeaponState.Free)
        {
            SalvoTargetPosition = targetPos;

            WeaponState = PhxWeaponState.SalvoDelayed;
            SalvoDelayTimer = C.InitialSalvoDelay;   
        }


        if (WeaponState == PhxWeaponState.SalvoDelayed && SalvoDelayTimer < .0001)
        {
            if (Audio != null)
            {
                // PitchSpread was previously used here to vary the sound pitch, that was a misunderstanding
                // as PitchSpread is part of the weapon's aim spread, not sound.
                //
                // A small variation is still wanted, just not from that
                // property. A blaster fires the identical sample several times
                // a second, and identical repeats phase-align into something
                // that reads as a machine rather than a weapon. A few percent
                // either side of the authored pitch is enough to break that up
                // without anyone hearing it as detuned.
                Audio.pitch = BasePitch * UnityEngine.Random.Range(1f - PitchJitter, 1f + PitchJitter);
                Audio.PlayOneShot(Audio.clip, 1.0f);
            }

            if (SalvoIndex < C.SalvoCount)
            {
                PhxOrdnanceClass Ordnance = C.OrdnanceName.Get() as PhxOrdnanceClass;
                if (Ordnance != null) 
                {
                    /*
                    New SPREAD, per salvo

                    Most of this function could use some optimization, maybe keeping everything local until needed in global space
                    for Scene.FireProjectile?
                    */
                    if (bUsesNewSpreadSystem)
                    {
                        CurrSpread = Mathf.Min(CurrSpread + C.SpreadPerShot, C.SpreadLimit + C.SpreadThreshold);
                        EffectiveSpread = Mathf.Max(CurrSpread - C.SpreadThreshold, 0f);

                        if (EffectiveSpread < .0001f)
                        {
                            SpreadQuat = Quaternion.identity;
                        }
                        else 
                        {
                            // Maybe we replace this with a fixed sequence of axes
                            SpreadAxis.x = UnityEngine.Random.Range(-1f, 1f);
                            SpreadAxis.y = UnityEngine.Random.Range(-1f, 1f);
                            SpreadQuat = Quaternion.AngleAxis(EffectiveSpread, FirePoint.TransformDirection(SpreadAxis)); 
                        }
                    }

                    // Debug.LogFormat("SpreadAxis: {0}, CurrSpread: {1}, EffectiveSpread: {2} ", SpreadAxis.ToString("F4"), CurrSpread, EffectiveSpread);


                    /*
                    SHOT ELEVATION
                    */

                    ShotElevationQuat = Quaternion.AngleAxis(C.ShotElevate, -FirePoint.right);

                    for (int i = 0; i < C.ShotsPerSalvo; i++)
                    {
                        /*
                        Old SPREAD, per shot (just from PitchSpread and YawSpread)
                        */
                        if (!bUsesNewSpreadSystem)
                        {
                            SpreadQuat = Quaternion.AngleAxis(UnityEngine.Random.Range(-C.PitchSpread, C.PitchSpread), -FirePoint.right) * 
                                         Quaternion.AngleAxis(UnityEngine.Random.Range(-C.YawSpread, C.YawSpread), FirePoint.up);
                        }
                        
                        Scene.FireProjectile(this, Ordnance, FirePoint.position,
                            SpreadQuat * ShotElevationQuat * Quaternion.LookRotation(SalvoTargetPosition - FirePoint.position, Vector3.up)); 

                        ShotCallback?.Invoke();
                    }
                }
            }

            // Gunfire is the loudest thing on a battlefield, and until now the
            // AI was completely deaf to it - you could shoot at a squad from
            // behind cover and none of them would react until one happened to
            // get line of sight. Reported once per salvo rather than per
            // projectile so a shotgun isn't eight times louder than a rifle.
            if (OwnerController != null)
            {
                PhxAIPerception.Report(FirePoint != null ? FirePoint.position : transform.position,
                                       OwnerController.Team,
                                       PhxAIPerception.GunshotLoudness);
            }

            if (++SalvoIndex >= C.SalvoCount || C.TriggerSingle.Get())
            {
                SalvoIndex = 0;
                WeaponState = PhxWeaponState.ShotDelayed;
                FireDelay = C.ShotDelay;
            }
            else 
            {
                WeaponState = PhxWeaponState.SalvoDelayed;
                SalvoDelayTimer = SalvoDelay;
            }


            if (!Game.Settings.InfiniteAmmo)
            {
                MagazineAmmo -= C.ShotsPerSalvo;
            }
            if (MagazineAmmo < C.ShotsPerSalvo)
            {
                Reload();
            }

            return true;
        }
        else 
        {
            return false;
        }
    }


    public void Reload()
    {
        if (WeaponState == PhxWeaponState.Reloading)
        {
            // already busy reloading
            return;
        }

        if (Ammunition > 0)
        {
            WeaponState = PhxWeaponState.Reloading;
            
            ReloadDelay = C.ReloadTime;
            ReloadCallback?.Invoke();
        }
    }

    public void OnShot(Action callback)
    {
        ShotCallback += callback;
    }

    public void OnReload(Action callback)
    {
        ReloadCallback += callback;
    }

    public virtual string GetAnimBankName()
    {
        return C.AnimationBank.Get();
    }

    public int GetMagazineSize()
    {
        return C.RoundsPerClip;
    }

    public int GetTotalAmmo()
    {
        return Ammunition + MagazineAmmo;
    }

    public int GetMagazineAmmo()
    {
        return MagazineAmmo;
    }

    public int GetAvailableAmmo()
    {
        return Ammunition;
    }

    // Left over from a resupply too small to be a whole round. Without this an
    // ammo droid ticking a quarter of a clip at a time would floor to zero
    // every tick and hand out nothing at all, forever.
    float AmmoCredit;

    /// <summary>
    /// Top up the reserve. See <see cref="IPhxWeapon.AddAmmo"/>.
    /// </summary>
    /// <remarks>
    /// PhxSoldier.AddAmmo was an empty TODO while PhxPowerupstation and
    /// PhxDroidStation both called it every tick, so health droids healed and
    /// ammo droids were furniture.
    ///
    /// The ceiling is the same reserve Init hands out (RoundsPerClip * 3), so
    /// standing on a station returns you to what you spawned with and no more.
    /// The magazine itself is deliberately not filled - that is what reloading
    /// is for, and topping it up here would let a player skip the reload by
    /// standing on the pad.
    /// </remarks>
    public void AddAmmo(float magazines)
    {
        if (magazines <= 0f) return;

        // A clip size of -1 means "never runs out" in the odfs (the
        // fusioncutter, which uses heat instead). Nothing to resupply.
        int clipSize = C.RoundsPerClip;
        if (clipSize <= 0) return;

        int maxReserve = clipSize * 3;
        if (Ammunition >= maxReserve)
        {
            AmmoCredit = 0f;
            return;
        }

        AmmoCredit += magazines * clipSize;

        int rounds = Mathf.FloorToInt(AmmoCredit);
        if (rounds <= 0) return;

        AmmoCredit -= rounds;
        Ammunition = Mathf.Min(Ammunition + rounds, maxReserve);
    }

    public float GetReloadTime()
    {
        return C.ReloadTime;
    }

    public float GetReloadProgress()
    {
        return 1f - ReloadDelay / C.ReloadTime;
    }

    public void Tick(float deltaTime)
    {
        CurrSpread = Mathf.Max(CurrSpread - deltaTime * C.SpreadRecoverRate, 0f);

        if (WeaponState == PhxWeaponState.Reloading)
        {
            ReloadDelay -= deltaTime;
            if (ReloadDelay <= 0f)
            {
                int reloadAmount = C.RoundsPerClip - MagazineAmmo;
                if (Ammunition < reloadAmount)
                {
                    reloadAmount = Ammunition;
                }

                MagazineAmmo += reloadAmount;
                Ammunition -= reloadAmount;

                ReloadDelay = 0f;

                WeaponState = PhxWeaponState.Free;
            }
        }
        else if (WeaponState == PhxWeaponState.SalvoDelayed)
        {
            SalvoDelayTimer -= deltaTime;
            if (SalvoDelayTimer < 0f)
            {
                Fire(null, SalvoTargetPosition);
            }
        }
        else if (WeaponState == PhxWeaponState.ShotDelayed)
        {
            FireDelay-= deltaTime;
            if (FireDelay < 0f)
            {
                WeaponState = PhxWeaponState.Free;
            }
        }
        else if (WeaponState == PhxWeaponState.Free)
        {
            if (IsTriggerPressed)
            {
                if (CanFire)
                {
                    Fire(null, SalvoTargetPosition);
                    if (C.TriggerSingle == true)
                    {
                        CanFire = false;
                    }
                }
            }
            else 
            {
                if (C.TriggerSingle == true)
                {
                    CanFire = true;
                }
            }
        }
    }
}
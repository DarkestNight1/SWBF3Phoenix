using UnityEngine;

/// <summary>
/// Periodic ambient rumble - distant explosions, settling caves, a capital
/// ship taking fire overhead.
/// </summary>
/// <remarks>
/// Placed once or twice per map in the spots that should feel unstable. Every
/// so often it shakes the view and plays a low sound; on a pad it also drives
/// the two rumble motors, which is what most of the odf's properties describe.
///
/// The base class had no runtime type, so these were silent and still.
///
/// Interval, shake amount and shake length are ranges, re-rolled per event, so
/// two emitters on one map never settle into lockstep. An interval of zero -
/// which is what the space maps' com_rum_space uses - means continuous, and is
/// treated as a short fixed cadence rather than a divide by zero.
/// </remarks>
public class PhxRumbleEffect : PhxInstance<PhxRumbleEffect.ClassProperties>, IPhxTickable
{
    public class ClassProperties : PhxClass
    {
        public PhxProp<float> MinInterval = new PhxProp<float>(0f);
        public PhxProp<float> MaxInterval = new PhxProp<float>(0f);

        public PhxProp<float> MinShakeAmt = new PhxProp<float>(0.2f);
        public PhxProp<float> MaxShakeAmt = new PhxProp<float>(1f);

        public PhxProp<float> MinShakeLen = new PhxProp<float>(0.1f);
        public PhxProp<float> MaxShakeLen = new PhxProp<float>(0.75f);

        public PhxProp<string> SoundName = new PhxProp<string>("");
    }

    // Zero interval means "always going"; use a short cadence so the shake is
    // continuous without scheduling an event every frame.
    const float ContinuousInterval = 0.5f;

    // Past this the player cannot plausibly feel it, and a map's other rumble
    // emitters should not shake the view from across the level.
    const float AudibleRange = 150f;

    AudioSource Source;
    float Countdown;

    public override void Init()
    {
        Source = gameObject.AddComponent<AudioSource>();
        Source.playOnAwake = false;
        Source.loop = false;
        Source.spatialBlend = 1f;
        Source.rolloffMode = AudioRolloffMode.Linear;
        Source.minDistance = 10f;
        Source.maxDistance = AudibleRange;

        Countdown = NextInterval();
    }

    public override void Destroy()
    {
    }

    float NextInterval()
    {
        float min = C.MinInterval.Get();
        float max = C.MaxInterval.Get();

        if (max <= 0f && min <= 0f) return ContinuousInterval;
        if (max < min) max = min;

        return Random.Range(min, max);
    }

    public void Tick(float deltaTime)
    {
        Countdown -= deltaTime;
        if (Countdown > 0f) return;

        Countdown = NextInterval();

        Camera cam = Camera.main;
        if (cam == null) return;

        float distance = Vector3.Distance(cam.transform.position, transform.position);
        if (distance > AudibleRange) return;

        // Falls off with distance so a far emitter is a tremor, not a jolt.
        float falloff = 1f - Mathf.Clamp01(distance / AudibleRange);

        float amount = Random.Range(C.MinShakeAmt.Get(), Mathf.Max(C.MinShakeAmt.Get(), C.MaxShakeAmt.Get()));
        float length = Random.Range(C.MinShakeLen.Get(), Mathf.Max(C.MinShakeLen.Get(), C.MaxShakeLen.Get()));

        PhxCamera phxCam = PhxGame.GetCamera();
        if (phxCam != null && amount > 0f && length > 0f)
        {
            phxCam.Shake(amount * falloff, length);
        }

        string sound = C.SoundName.Get();
        if (!string.IsNullOrEmpty(sound) && Source != null && SoundLoader.Instance != null)
        {
            if (Source.clip == null)
            {
                Source.clip = SoundLoader.Instance.LoadSound(sound);
            }
            if (Source.clip != null && !Source.isPlaying)
            {
                Source.volume = PhxBF3.Config.AmbienceVolume;
                Source.Play();
            }
        }
    }
}

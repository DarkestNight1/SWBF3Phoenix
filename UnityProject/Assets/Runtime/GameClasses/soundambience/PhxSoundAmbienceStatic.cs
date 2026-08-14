using UnityEngine;

/// <summary>
/// A positional ambient sound emitter placed in the world.
/// </summary>
/// <remarks>
/// Maps keep their environmental audio in a dedicated soundemitters world:
/// control panels, tractor beams, shield hums, water, machinery. Each instance
/// names a clip and the radius over which it should be heard - Death Star has
/// 19 of them, Kashyyyk 80, Kamino 80, Mos Eisley 116.
///
/// The base class had no runtime type, so every one of those instances
/// imported as an empty GameObject and every map was environmentally silent.
/// The import report counted them as entity classes with no behaviour, which is
/// how a few hundred missing emitters stayed invisible.
///
/// Values come from the instance rather than the odf: the shared
/// com_snd_amb_static class only carries fallback distances, and each placement
/// overrides sound/mindistance/maxdistance for its own spot.
/// </remarks>
public class PhxSoundAmbienceStatic : PhxInstance<PhxSoundAmbienceStatic.ClassProperties>, IPhxTickable
{
    public class ClassProperties : PhxClass
    {
        public PhxProp<float> MinDistance = new PhxProp<float>(1f);
        public PhxProp<float> MaxDistance = new PhxProp<float>(10f);
    }

    // Instance overrides. Zero means "not set here, take the class value".
    public PhxProp<string> Sound = new PhxProp<string>("");
    public PhxProp<float> MinDistance = new PhxProp<float>(0f);
    public PhxProp<float> MaxDistance = new PhxProp<float>(0f);

    AudioSource Source;
    string AppliedSound;
    bool Dirty;

    public override void Init()
    {
        Source = gameObject.AddComponent<AudioSource>();
        Source.playOnAwake = false;
        Source.loop = true;
        Source.spatialBlend = 1f;

        // Linear rather than logarithmic: the data means "inaudible past
        // MaxDistance", and logarithmic rolloff never actually reaches zero,
        // so a hundred emitters would all still contribute at the far edge.
        Source.rolloffMode = AudioRolloffMode.Linear;

        // Init runs before instance properties are assigned, so the emitter is
        // configured on the next tick. Flagging rather than applying on each
        // change keeps it order independent - min/max may arrive after sound.
        Sound.OnValueChanged += _ => Dirty = true;
        MinDistance.OnValueChanged += _ => Dirty = true;
        MaxDistance.OnValueChanged += _ => Dirty = true;
        Dirty = true;
    }

    public override void Destroy()
    {
    }

    public void Tick(float deltaTime)
    {
        if (!Dirty) return;
        Dirty = false;
        Apply();
    }

    void Apply()
    {
        if (Source == null) return;

        if (!PhxBF3.Config.AmbienceEnabled)
        {
            Source.Stop();
            return;
        }

        string soundName = Sound.Get();
        if (string.IsNullOrEmpty(soundName))
        {
            Source.Stop();
            return;
        }

        float minD = MinDistance.Get() > 0f ? MinDistance.Get() : C.MinDistance.Get();
        float maxD = MaxDistance.Get() > 0f ? MaxDistance.Get() : C.MaxDistance.Get();

        // A max at or inside the min silences the source outright in Unity.
        if (maxD <= minD) maxD = minD + 1f;

        Source.minDistance = minD;
        Source.maxDistance = maxD;
        Source.volume = PhxBF3.Config.AmbienceVolume;

        if (SoundLoader.Instance != null &&
            SoundLoader.Instance.TryGetProperties(soundName, out SoundLoader.SoundProperties props))
        {
            Source.pitch = props.Pitch > 0f ? props.Pitch : 1f;
        }

        if (soundName != AppliedSound)
        {
            AppliedSound = soundName;
            Source.clip = SoundLoader.Instance != null ? SoundLoader.Instance.LoadSound(soundName) : null;
        }

        if (Source.clip == null)
        {
            Source.Stop();
            return;
        }

        if (!Source.isPlaying)
        {
            // Stagger the start so a room full of copies of one hum does not
            // play them phase locked, which sounds like a single loud source.
            Source.time = Random.Range(0f, Source.clip.length);
            Source.Play();
        }
    }
}

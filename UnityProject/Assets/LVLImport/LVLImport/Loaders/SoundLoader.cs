using System;
using System.Collections.Generic;
using UnityEngine;

using LibSWBF2.Wrappers;
using LibSWBF2.Enums;
using LibSWBF2.Utils;

public class SoundLoader : Loader
{
    public static SoundLoader Instance { get; private set; } = null;


    Dictionary<uint, AudioClip> SoundDB = new Dictionary<uint, AudioClip>();

    /// <summary>
    /// Every sample a sound property names, not just the first.
    /// </summary>
    /// <remarks>
    /// BF2 sound properties carry a whole SampleList and pick from it at play
    /// time - that variation is why the original's blaster fire and footsteps
    /// don't sound like a loop. This kept only samples[0], so every shot was
    /// byte-identical.
    /// </remarks>
    Dictionary<uint, uint[]> NameHashToClipMapping = new Dictionary<uint, uint[]>();

    /// <summary>Authored 3D falloff and pitch, keyed the same way.</summary>
    public struct SoundProperties
    {
        public float Pitch;
        public float MinDistance;
        public float MaxDistance;
        public bool HasDistances;
    }

    Dictionary<uint, SoundProperties> PropertiesByName = new Dictionary<uint, SoundProperties>();


    static SoundLoader()
    {
        Instance = new SoundLoader();
    }


    public void ResetDB()
    {
        // For now, we don't clear the nametohash mapping when Resetting
        SoundDB.Clear();
    }


    public void InitializeSoundProperties(Level level)
    {
        if (level == null || !level.IsValid())
        {
            return;
        }

        foreach (Config sndCfg in level.GetConfigs(EConfigType.Sound))
        {
            foreach (Field soundField in sndCfg.GetFields("SoundProperties"))
            {
                uint name = soundField.Scope.GetUInt("Name");

                // Authored playback properties. These were read and discarded,
                // so every sound played flat and at full volume regardless of
                // how far away it was.
                SoundProperties props = new SoundProperties { Pitch = 1f };
                props.Pitch = ReadFloatField(soundField, "Pitch", 1f);

                float minDist = ReadFloatField(soundField, "MinDistance", -1f);
                float maxDist = ReadFloatField(soundField, "MaxDistance", -1f);
                if (minDist > 0f && maxDist > minDist)
                {
                    props.MinDistance = minDist;
                    props.MaxDistance = maxDist;
                    props.HasDistances = true;
                }
                PropertiesByName[name] = props;

                Field sampleList;
                try {
                    sampleList = soundField.Scope.GetField("SampleList");
                }
                catch
                {
                    continue;
                }
                if (sampleList == null)
                {
                    continue;
                }

                Field[] samples = sampleList.Scope.GetFields("Sample");
                if (samples.Length > 0)
                {
                    // Keep the whole list; LoadSound picks one per call.
                    uint[] clips = new uint[samples.Length];
                    for (int i = 0; i < samples.Length; ++i)
                    {
                        clips[i] = samples[i].GetUInt(0);
                    }
                    NameHashToClipMapping[name] = clips;
                }
            }
        }
    }


    public AudioClip LoadSound(string soundName)
    {
        AudioClip clip = LoadSound(HashUtils.GetFNV(soundName), soundName);
        if (clip == null)
        {
            Debug.LogWarningFormat("Failed to find sound clip: {0}", soundName);
        }
        return clip;
    }

    /// <summary>
    /// Falloff/pitch the data authored for a sound, if any.
    /// </summary>
    public bool TryGetProperties(string soundName, out SoundProperties props)
    {
        return PropertiesByName.TryGetValue(HashUtils.GetFNV(soundName), out props);
    }

    /// <summary>
    /// Read a float that may be stored as a float or an int, tolerating a
    /// missing field. LibSWBF2 throws rather than returning null for absent
    /// fields, which is why this is wrapped.
    /// </summary>
    static float ReadFloatField(Field parent, string name, float fallback)
    {
        try
        {
            Field f = parent.Scope.GetField(name);
            return f != null ? f.GetFloat() : fallback;
        }
        catch
        {
            return fallback;
        }
    }

    /// <summary>Name a sound as helpfully as the caller allows.</summary>
    /// <remarks>
    /// The three warnings below used to read
    /// <c>soundNameString == null ? soundNameString : FNVToString(...)</c>,
    /// which is backwards in both directions: with no name it printed null, and
    /// with a name it threw that name away and printed a hash decode instead.
    /// So a message whose entire job was to say WHICH sound failed never did.
    /// </remarks>
    static string DescribeSound(string soundNameString, uint soundName)
    {
        return string.IsNullOrEmpty(soundNameString)
            ? HashUtils.FNVToString(soundName, false)
            : soundNameString;
    }

    public AudioClip LoadSound(uint soundName, string soundNameString = null)
    {
        uint clipNameHash;
        AudioClip foundClip;


        if (NameHashToClipMapping.TryGetValue(soundName, out uint[] clipHashes) && clipHashes.Length > 0)
        {
            // Pick a different sample each time so repeated fire doesn't loop
            // one recording. Cached per clip hash below, so the variants are
            // only decoded once each.
            clipNameHash = clipHashes.Length == 1
                ? clipHashes[0]
                : clipHashes[UnityEngine.Random.Range(0, clipHashes.Length)];

            if (SoundDB.TryGetValue(clipNameHash, out foundClip))
            {
                return foundClip;
            }
        }
        else if (SoundDB.TryGetValue(soundName, out foundClip))
        {
            return foundClip;
        }
        else
        {
            //Debug.LogWarningFormat("No sound mapping exists for {0}, attempting to get clip by name...", soundName);
            clipNameHash = soundName;
        }

        Sound sound = container.Get<Sound>(clipNameHash);
        if (sound == null)
        {
            Debug.LogWarningFormat("failed to find sound queried with: {0} (hash key: 0x{1:X})",
                                    DescribeSound(soundNameString, soundName),
                                    clipNameHash);
            BFImportDiagnostics.Missing(BFSourceKind.Sound,
                                        soundNameString ?? $"0x{clipNameHash:X}");
            return null;
        }

        if (!sound.GetData(out uint sampleRate, out uint sampleCount, out byte blockAlign, out byte[] data))
        {
            // Say WHY, when the reason is knowable.
            //
            // A clip carrying an alias has no samples of its own - the native
            // reader skips it deliberately, because its data belongs to the
            // clip it points at - so "couldn't retrieve data" is true but
            // useless. Alias resolution is not implemented, and measuring the
            // stock data says it need not be: one aliased clip out of 1567 in
            // common.bnk, none in any level bank, and no aliased stream segment
            // on any map checked. A mod leaning on aliases would look like
            // randomly silent sounds, so it names itself here rather than
            // costing someone an afternoon.
            if (sound.Alias != 0)
            {
                Debug.LogWarning($"Sound '{DescribeSound(soundNameString, soundName)}' is an alias " +
                                 $"of 0x{sound.Alias:X} and carries no samples of its own; alias " +
                                 "resolution is not implemented, so this sound is silent.");
                return null;
            }

            Debug.LogWarningFormat("Couldn't retrieve sound data of sound '{0}'! (hash key: 0x{1:X})",
                                    DescribeSound(soundNameString, soundName),
                                    clipNameHash);
            return null;
        }

        // Honour the source's channel count.
        //
        // This used to build EVERY clip as mono and assert that blockAlign was
        // 2 bytes. A stereo 16-bit sample has blockAlign 4, so the assert fired
        // and the interleaved data was then read as if it were a mono stream -
        // giving a clip of double the length at half the pitch. NumSamples
        // counts samples across all channels, so the frame count Unity wants is
        // NumSamples / channels.
        int channels = Mathf.Max(1, (int)sound.NumChannels);
        int totalSamples = (int)sampleCount;
        int frames = Mathf.Max(1, totalSamples / channels);

        // GetPCM16 is the wrapper's own decoder and replaces the hand-rolled
        // BitConverter loop that lived here; it also copes with formats whose
        // block alignment isn't a plain 16-bit sample.
        short[] pcm16 = sound.GetPCM16();
        float[] pcm = new float[totalSamples];

        if (pcm16 != null && pcm16.Length >= totalSamples)
        {
            for (int i = 0; i < totalSamples; ++i)
            {
                pcm[i] = pcm16[i] / 32768.0f;
            }
        }
        else
        {
            // Fall back to the raw bytes when the decoder gives us nothing,
            // rather than returning silence.
            int usable = Mathf.Min(totalSamples, data.Length / sizeof(short));
            for (int i = 0; i < usable; ++i)
            {
                pcm[i] = BitConverter.ToInt16(data, i * sizeof(short)) / 32768.0f;
            }
        }

        AudioClip clip = AudioClip.Create(soundName.ToString(), frames, channels, (int)sampleRate, false);

        if (!clip.SetData(pcm, 0))
        {
            Debug.LogErrorFormat("Couldn't set sound data of sound '{0}'! (hash key: 0x{1:X})", 
                                DescribeSound(soundNameString, soundName), 
                                clipNameHash);
        }

        SoundDB.Add(clipNameHash, clip);
        BFImportDiagnostics.Resolved(BFSourceKind.Sound,
                                     soundNameString ?? $"0x{clipNameHash:X}");

        return clip;
    }
}
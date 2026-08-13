using System.Collections.Generic;
using UnityEngine;
using LibSWBF2.Utils;
using LibSWBF2.Wrappers;

/// <summary>
/// Music and voice-over playback for the mission scripts' audio API.
///
/// Every stock era script calls SetAmbientMusic / SetVictoryMusic /
/// SetDefeatMusic and broadcasts voice-overs; all of it was dropped on the
/// floor, so matches ran silent apart from weapon fire and UI clicks.
///
/// Music lives in segmented sound streams inside the map's sound .lvl. This
/// decodes a whole segment into a static AudioClip and caches it, rather than
/// streaming it incrementally: native reads inside Unity's PCM callback run on
/// the audio thread, which is not worth the hazard for a few MB of music.
/// </summary>
public class PhxMusicManager
{
    public static PhxMusicManager Instance { get; private set; }

    static PhxEnvironment ENV => PhxGame.GetEnvironment();

    // Music crossfade length, and how far music ducks under a voice-over.
    const float CrossfadeTime = 2f;
    const float VODuckVolume = 0.45f;

    GameObject Host;
    AudioSource MusicA;
    AudioSource MusicB;
    AudioSource VOSource;

    bool MusicAActive = true;
    float FadeTimer;

    class StreamHandle
    {
        public string LvlPath;
        public string StreamName;
        public readonly List<string> Segments = new List<string>();
    }

    readonly Dictionary<int, StreamHandle> OpenStreams = new Dictionary<int, StreamHandle>();
    int NextStreamHandle = 1;

    // Per-team script configuration. Team numbers are 1-based.
    readonly Dictionary<int, string> AmbientMusic = new Dictionary<int, string>();
    readonly Dictionary<int, string> VictoryMusic = new Dictionary<int, string>();
    readonly Dictionary<int, string> DefeatMusic = new Dictionary<int, string>();
    readonly Dictionary<string, string> SoundEffects = new Dictionary<string, string>();

    // (hearingTeam, subjectTeam) -> voice over name
    readonly Dictionary<(int, int), string> BleedingVO = new Dictionary<(int, int), string>();
    readonly Dictionary<(int, int), string> LowReinforcementsVO = new Dictionary<(int, int), string>();

    readonly Dictionary<string, AudioClip> ClipCache = new Dictionary<string, AudioClip>();

    string CurrentAmbient;
    bool MatchEnded;

    public static void Create()
    {
        Destroy();
        Instance = new PhxMusicManager();
        Instance.Init();
    }

    public static void Destroy()
    {
        if (Instance == null) return;
        if (Instance.Host != null)
        {
            Object.Destroy(Instance.Host);
        }
        Instance = null;
    }

    void Init()
    {
        Host = new GameObject("PhxMusic");
        Object.DontDestroyOnLoad(Host);

        MusicA = CreateSource(PhxBF3.Config.MusicVolume);
        MusicB = CreateSource(0f);
        VOSource = CreateSource(PhxBF3.Config.VOVolume);
        MusicA.loop = true;
        MusicB.loop = true;
    }

    AudioSource CreateSource(float volume)
    {
        AudioSource src = Host.AddComponent<AudioSource>();
        src.playOnAwake = false;
        src.spatialBlend = 0f;      // 2D
        src.volume = volume;
        return src;
    }

    AudioSource ActiveMusic => MusicAActive ? MusicA : MusicB;
    AudioSource IdleMusic => MusicAActive ? MusicB : MusicA;

    /// <summary>Driven from PhxMatch.Tick - crossfades and VO ducking.</summary>
    public void Tick(float deltaTime)
    {
        if (Host == null) return;

        float target = PhxBF3.Config.MusicVolume;
        if (VOSource.isPlaying)
        {
            target *= VODuckVolume;
        }

        if (FadeTimer > 0f)
        {
            FadeTimer -= deltaTime;
            float t = Mathf.Clamp01(1f - FadeTimer / CrossfadeTime);
            ActiveMusic.volume = target * t;
            IdleMusic.volume = target * (1f - t);
            if (FadeTimer <= 0f)
            {
                IdleMusic.Stop();
            }
        }
        else
        {
            ActiveMusic.volume = target;
        }
    }

    // ------------------------------------------------------ script interface

    public int OpenStream(string lvlPath, string streamName)
    {
        int handle = NextStreamHandle++;
        OpenStreams[handle] = new StreamHandle { LvlPath = lvlPath, StreamName = streamName };

        // Which banks the scripts actually ask for. BF2 keeps voice-over in
        // stream banks (common.bnk, sound/global.lvl), and whether those are
        // among these is the difference between "VO is unwired" and "we can't
        // read .bnk at all" - two very different fixes.
        Debug.Log($"[VO/Music] script opened stream '{streamName}' from '{lvlPath}' (handle {handle}).");
        return handle;
    }

    public void AppendSegment(int handle, string segmentName)
    {
        if (OpenStreams.TryGetValue(handle, out StreamHandle stream))
        {
            stream.Segments.Add(segmentName);
        }
    }

    public void SetAmbientMusic(int teamIdx, string musicName)
    {
        AmbientMusic[teamIdx] = musicName;

        // Usually a no-op at the point it is called. Era scripts set ambient
        // music from ScriptInit, which runs before the match exists and before
        // the player has a team - so this returns early and the name is only
        // stored. OnMatchStart is what actually starts it, once there is a
        // player to pick a team's track for. Without that pairing the music is
        // configured and never heard, which is exactly what happened.
        PhxMatch match = PhxGame.GetMatch();
        if (match == null || match.Player == null || MatchEnded) return;
        if (teamIdx != 0 && teamIdx != match.Player.Team) return;

        PlayMusic(musicName, loop: true);
    }

    /// <summary>
    /// Start the ambient track the mission configured, now that there is a
    /// player and a team to choose it for.
    /// </summary>
    /// <remarks>
    /// Falls back to team 0, which is how the scripts express "everyone hears
    /// this" - most stock maps set only that one.
    /// </remarks>
    public void OnMatchStart()
    {
        MatchEnded = false;

        PhxMatch match = PhxGame.GetMatch();
        if (match == null || match.Player == null) return;

        int playerTeam = match.Player.Team;
        if (!AmbientMusic.TryGetValue(playerTeam, out string musicName) ||
            string.IsNullOrEmpty(musicName))
        {
            AmbientMusic.TryGetValue(0, out musicName);
        }

        if (string.IsNullOrEmpty(musicName))
        {
            Debug.Log("[VO/Music] No ambient music configured for this map; " +
                      "the mission script never called SetAmbientMusic.");
            return;
        }

        PlayMusic(musicName, loop: true);
    }
    public void SetVictoryMusic(int teamIdx, string soundName) => VictoryMusic[teamIdx] = soundName;
    public void SetDefeatMusic(int teamIdx, string soundName) => DefeatMusic[teamIdx] = soundName;
    public void SetSoundEffect(string eventName, string soundName) => SoundEffects[eventName] = soundName;

    public void SetBleedingVoiceOver(int hearingTeam, int subjectTeam, string voName)
        => BleedingVO[(hearingTeam, subjectTeam)] = voName;

    public void SetLowReinforcementsVoiceOver(int hearingTeam, int subjectTeam, string voName)
        => LowReinforcementsVO[(hearingTeam, subjectTeam)] = voName;

    /// <summary>Play a voice-over, if the local player is meant to hear it.</summary>
    public void BroadcastVoiceOver(string voName, int teamIdx)
    {
        PhxMatch match = PhxGame.GetMatch();
        if (match == null || match.Player == null) return;
        if (teamIdx != 0 && teamIdx != match.Player.Team) return;

        AudioClip clip = ResolveClip(voName);
        if (clip == null) return;

        VOSource.volume = PhxBF3.Config.VOVolume;
        VOSource.PlayOneShot(clip);
    }

    public void PlayBleedingVO(int hearingTeam, int subjectTeam)
        => PlayKeyedVO(BleedingVO, hearingTeam, subjectTeam);

    public void PlayLowReinforcementsVO(int hearingTeam, int subjectTeam)
        => PlayKeyedVO(LowReinforcementsVO, hearingTeam, subjectTeam);

    void PlayKeyedVO(Dictionary<(int, int), string> table, int hearingTeam, int subjectTeam)
    {
        if (table.TryGetValue((hearingTeam, subjectTeam), out string voName))
        {
            BroadcastVoiceOver(voName, hearingTeam);
        }
    }

    /// <summary>Match over: swap ambient music for victory or defeat.</summary>
    public void OnMatchEnd(int winningTeam)
    {
        MatchEnded = true;

        PhxMatch match = PhxGame.GetMatch();
        if (match == null) return;

        if (match.Player == null) return;
        int playerTeam = match.Player.Team;
        bool won = winningTeam == playerTeam || match.IsFriend(winningTeam, playerTeam);

        Dictionary<int, string> table = won ? VictoryMusic : DefeatMusic;
        if (!table.TryGetValue(playerTeam, out string musicName))
        {
            table.TryGetValue(0, out musicName);
        }
        if (string.IsNullOrEmpty(musicName)) return;

        PlayMusic(musicName, loop: false);
    }

    // ----------------------------------------------------------- playback

    void PlayMusic(string musicName, bool loop)
    {
        if (!PhxBF3.Config.MusicEnabled || string.IsNullOrEmpty(musicName)) return;
        if (CurrentAmbient == musicName && ActiveMusic.isPlaying) return;

        AudioClip clip = ResolveClip(musicName);
        if (clip == null) return;

        // Crossfade: the idle source becomes the new active one.
        AudioSource next = IdleMusic;
        next.clip = clip;
        next.loop = loop;
        next.volume = 0f;
        next.Play();

        MusicAActive = !MusicAActive;
        FadeTimer = CrossfadeTime;
        CurrentAmbient = musicName;
    }

    /// <summary>
    /// A script-supplied audio name to a playable clip. Most names resolve
    /// straight out of the mounted sound banks; music that only exists as a
    /// stream segment goes through the stream decoder below.
    /// </summary>
    AudioClip ResolveClip(string name)
    {
        if (string.IsNullOrEmpty(name)) return null;

        if (ClipCache.TryGetValue(name, out AudioClip cached))
        {
            return cached;
        }

        AudioClip clip = SoundLoader.Instance.LoadSound(name);
        if (clip == null)
        {
            clip = DecodeStreamSegment(name);
        }

        // Cached either way: a name that cannot be resolved once will not
        // resolve on the next of many calls either, and the lookup is noisy.
        ClipCache[name] = clip;
        return clip;
    }

    /// <summary>
    /// Decode a segment out of one of the streams the script opened. Returns
    /// null when the name is not a known segment or the stream cannot be read.
    /// </summary>
    AudioClip DecodeStreamSegment(string segmentName)
    {
        foreach (StreamHandle stream in OpenStreams.Values)
        {
            if (!stream.Segments.Contains(segmentName)) continue;

            AudioClip clip = DecodeSegment(stream, segmentName);
            if (clip != null) return clip;
        }
        return null;
    }

    // Voice-over and music both come through here, and every failure below used
    // to return null silently - so a mission whose VO never played looked
    // identical to one that had no VO. Say which step failed, once per
    // stream+segment, so the cause is identifiable from a single run.
    static readonly HashSet<string> ReportedStreamFailures = new HashSet<string>();

    static void ReportStreamFailure(StreamHandle handle, string segmentName, string reason)
    {
        string key = $"{handle.LvlPath}|{handle.StreamName}|{segmentName}|{reason}";
        if (!ReportedStreamFailures.Add(key)) return;

        Debug.LogWarning($"[VO/Music] '{segmentName}' from stream '{handle.StreamName}' " +
                         $"in '{handle.LvlPath}': {reason}");
    }

    AudioClip DecodeSegment(StreamHandle handle, string segmentName)
    {
        PhxPath resolved = ResolveLvlPath(handle.LvlPath);
        if (resolved == null)
        {
            ReportStreamFailure(handle, segmentName, "lvl path did not resolve on disk");
            return null;
        }

        Level level = FindLoadedLevel(handle.LvlPath);
        if (level == null)
        {
            ReportStreamFailure(handle, segmentName,
                "the lvl is not mounted - the script opened a bank we never loaded");
            return null;
        }

        FileReader reader = FileReader.FromFile(resolved.ToString());
        if (reader == null)
        {
            ReportStreamFailure(handle, segmentName, $"could not open '{resolved}' for reading");
            return null;
        }

        SoundStream stream = level.FindAndIndexSoundStream(reader, HashUtils.GetFNV(handle.StreamName));
        if (stream == null)
        {
            ReportStreamFailure(handle, segmentName, "stream not found in that lvl");
            return null;
        }
        if (!stream.SetSegment(HashUtils.GetFNV(segmentName)))
        {
            ReportStreamFailure(handle, segmentName, "segment not present in the stream");
            return null;
        }

        // Read in chunks until the segment runs dry.
        const int ChunkSamples = 65536;
        float[] chunk = new float[ChunkSamples];
        List<float> pcm = new List<float>(ChunkSamples * 4);

        int read;
        while ((read = stream.ReadSamplesUnity(chunk)) > 0)
        {
            for (int i = 0; i < read; ++i)
            {
                pcm.Add(chunk[i]);
            }
            if (read < ChunkSamples) break;
        }

        if (pcm.Count == 0)
        {
            ReportStreamFailure(handle, segmentName, "segment decoded to zero samples");
            return null;
        }

        int channels = (int)Mathf.Max(1, stream.NumChannels);
        AudioClip clip = AudioClip.Create(segmentName, pcm.Count / channels, channels, 44100, false);
        clip.SetData(pcm.ToArray(), 0);
        return clip;
    }

    /// <summary>Addon data wins over stock, matching the rest of the loader.</summary>
    PhxPath ResolveLvlPath(string lvlPath)
    {
        if (string.IsNullOrEmpty(lvlPath) || ENV == null) return null;

        string rel = lvlPath.Replace('\\', '/').ToLower();
        int idx = rel.IndexOf("_lvl_pc/");
        if (idx >= 0)
        {
            rel = rel.Substring(idx + "_lvl_pc/".Length);
        }

        if (ENV.AddonDataPath != null)
        {
            PhxPath addon = ENV.AddonDataPath / rel;
            if (addon.Exists() && addon.IsFile()) return addon;
        }

        PhxPath stock = ENV.GameDataPath / rel;
        return stock.Exists() && stock.IsFile() ? stock : null;
    }

    Level FindLoadedLevel(string lvlPath)
    {
        if (ENV == null) return null;

        string leaf = lvlPath.Replace('\\', '/');
        int slash = leaf.LastIndexOf('/');
        if (slash >= 0) leaf = leaf.Substring(slash + 1);

        for (int i = 0; i < ENV.Loaded.Count; ++i)
        {
            PhxEnvironment.LVL lvl = ENV.Loaded[i];
            string loadedLeaf = lvl.DisplayPath.GetLeaf().ToString();
            if (string.Equals(loadedLeaf, leaf, System.StringComparison.OrdinalIgnoreCase))
            {
                return lvl.Level;
            }
        }
        return null;
    }
}

using System;
#if UNITY_EDITOR
using UnityEditor;
#endif

public class CraMain
{
    CraPlaybackManager Players;
    CraAnimatorManager Animators;

    public CraMain()
    {
        Players = CraPlaybackManager.Get();
        Animators = CraAnimatorManager.Get();

#if UNITY_EDITOR
        EditorApplication.quitting += Destroy;
#endif
    }

    public void Tick()
    {
        Players.Tick();
        Animators.Tick();
    }

    public void Clear()
    {
        Players.Clear();
        Animators.Clear();
    }

    public void Destroy()
    {
        Players.Destroy();
        Players = null;

        Animators.Destroy();
        Animators = null;
    }
}

public class CraStatistics
{
    public CraMeasure PlayerData;
    public CraMeasure ClipData;
    public CraMeasure BakedClipTransforms;
    public CraMeasure BoneData;
    public CraMeasure Bones;
}

public static class CraSettings
{
    public const int STATE_NONE = -1;
    public const int MAX_PLAYERS = 32768;
    public const int MAX_LAYERS = 4096;
    public const int MAX_ANIMATORS = 2048;

    public const int MAX_PLAYER_DATA = MAX_PLAYERS / 4;

    // Sized for the stock content rather than for a guess.
    //
    // The transform pool is a bump allocator: Alloc only advances Head and the
    // only way to reclaim is Clear(). Cra also indexes baked frames directly
    // (FrameIndex = floor(FPS * Playback)) with no interpolation, so the bake
    // rate IS the playback rate - clips are baked at 120 to look smooth, and
    // that cost cannot be reduced without visible stepping.
    //
    // One human weapon bank is 18 clips; a soldier loads rifle, pistol and
    // bazooka plus 4 death and 4 hit states, so ~62 clips. A 2 second clip at
    // 120 fps over 20 bones is 240 * 20 = 4800 transforms, which puts one
    // species alone near 300k - over the old 262140 ceiling before a second
    // species or a hero bank is touched. That is why clips silently stopped
    // being settable: the pool ran out and every later SetClip failed.
    //
    // At 28 bytes per CraTransform (float3 + float4) this is ~29 MB, which is a
    // reasonable trade for animation that works.
    public const int MAX_CLIP_DATA = 1024;
    public const int MAX_BAKED_CLIP_TRANSFORMS = 65535 * 16;
    public const int MAX_BONE_DATA = 65535 * 8;
    public const int MAX_BONES = 65535 * 8;

    public static Func<string, int> BoneHashFunction;
}

using System.Collections.Generic;
using UnityEngine;
using LibSWBF2.Wrappers;
using LibSWBF2.Utils;

// UnityEngine.Joint is the physics component; this file means the skeleton
// joint. Aliased rather than fully qualified to match how the importer already
// disambiguates LibSWBF2 types that collide with UnityEngine ones - see
// LibMaterial and LibBone in SWBFModel.
using LibJoint = LibSWBF2.Wrappers.Joint;

public static class PhxAnimationLoader
{
    public static Container Con;

    static Dictionary<uint, CraClip> ClipDB = new Dictionary<uint, CraClip>();

    // Bind poses keyed by skeleton name. A null value is a remembered miss, so
    // a skeleton the container doesn't have is looked up once rather than once
    // per clip.
    static Dictionary<string, Dictionary<uint, LibJoint>> SkeletonDB =
        new Dictionary<string, Dictionary<uint, LibJoint>>();

    // Identical to UnityUtils.QuatFromLibSkel / Vec3FromLibSkel: rotation
    // (-x, y, z, -w), position (-x, y, z). Curves and bind poses come out of
    // LibSWBF2 in the same space, so they must be converted the same way or
    // they cannot be mixed.
    static readonly float[] ComponentMultipliers =
    {
        -1.0f,
         1.0f,
         1.0f,
        -1.0f,
        -1.0f,
         1.0f,
         1.0f
    };

    static PhxAnimationLoader()
    {
        CraSettings.BoneHashFunction = (string str) => (int)HashUtils.GetCRC(str);
    }

    /// <summary>
    /// Drop every cached clip and bind pose, and reclaim the Cra pools that
    /// hold them.
    /// </summary>
    /// <remarks>
    /// Clearing ClipDB alone leaks, permanently. Cra copies a clip's baked
    /// frames into a bump-allocated pool the first time a player is given that
    /// clip, and remembers it by CraClip REFERENCE. Throwing the references
    /// away here means the next map re-imports the same animations as brand new
    /// CraClip objects, misses the cache, and allocates the pool all over again
    /// - while the previous map's allocations can never be recovered, because
    /// the allocator only has a Head pointer and a Clear().
    ///
    /// Two or three map loads was enough to exhaust it, and the failure is
    /// silent in the worst way: SetClip starts returning false, so soldiers get
    /// players with no clip, "Cannot assign Transform ... No clip(s) set!"
    /// follows, and finally the Burst jobs index past the end of a buffer that
    /// was never filled. None of those name the real problem.
    ///
    /// Called from PhxEnvironment.Create, before the new environment exists, so
    /// no live player can be holding an index into what is being freed.
    /// </remarks>
    public static void ClearDB()
    {
        ClipDB.Clear();
        SkeletonDB.Clear();

        // Must happen together with the above or the two views of "which clips
        // exist" diverge, which is exactly how the pool leaked.
        CraPlaybackManager.Get()?.Clear();
    }

    /// <summary>
    /// <summary>
    /// How full Cra's clip-transform pool is, as a 0..1 fraction, or -1 when
    /// the figure is unavailable.
    /// </summary>
    /// <remarks>
    /// Worth surfacing because running this pool dry does not report itself in
    /// any way that points here. SetClip simply starts returning false, and
    /// what reaches the console is "Cannot assign Transform ... No clip(s)
    /// set!" from CraPlaybackManager, followed by Burst jobs indexing past the
    /// end of buffers that were never filled. Nothing in that chain mentions a
    /// budget.
    ///
    /// Cra only keeps these counters in the editor (CraPlaybackManager
    /// .Statistics is inside #if UNITY_EDITOR), so a player build reports -1
    /// rather than failing to compile.
    /// </remarks>
    public static float GetClipPoolUsage()
    {
#if UNITY_EDITOR
        CraPlaybackManager mgr = CraPlaybackManager.Get();
        if (mgr == null) return -1f;

        CraMeasure m = mgr.Statistics.BakedClipTransforms;
        return m.MaxElements > 0 ? (float)m.CurrentElements / m.MaxElements : -1f;
#else
        return -1f;
#endif
    }

    /// <summary>
    /// Log clip-pool headroom, and warn before it becomes a problem rather than
    /// after. Cheap enough to call once a map has finished loading.
    /// </summary>
    public static void ReportClipPoolUsage(string context = null)
    {
#if UNITY_EDITOR
        CraPlaybackManager mgr = CraPlaybackManager.Get();
        if (mgr == null) return;

        CraMeasure m = mgr.Statistics.BakedClipTransforms;
        if (m.MaxElements <= 0) return;

        float frac = (float)m.CurrentElements / m.MaxElements;
        string where = string.IsNullOrEmpty(context) ? "" : $" ({context})";

        string msg = $"[PhxAnimation] Clip transform pool{where}: " +
                     $"{m.CurrentElements}/{m.MaxElements} ({frac * 100f:F0}%), " +
                     $"{m.CurrentBytes / (1024 * 1024)} MB of {m.MaxBytes / (1024 * 1024)} MB. " +
                     $"{ClipDB.Count} clip(s) cached.";

        // 85% is where a hero bank or a second species would tip it over.
        if (frac > 0.85f)
        {
            Debug.LogWarning(msg + " Nearly exhausted - further clips will fail to " +
                             "bind and soldiers will animate incorrectly. Raise " +
                             "CraSettings.MAX_BAKED_CLIP_TRANSFORMS.");
        }
        else
        {
            Debug.Log(msg);
        }
#endif
    }
    /// <summary>
    /// The authored rest pose for a skeleton, as <c>BoneCRC -> Joint</c>.
    ///
    /// This is the half of the animation data the runtime never read. An
    /// AnimationBank keys a subset of the skeleton's joints - measured across
    /// stock content, every single animation leaves at least one joint alone -
    /// and the bones it does key are authored against this pose, not against
    /// the rest pose baked into the model's own bone hierarchy. The two
    /// disagree substantially: on stock soldiers the pelvis differs by over a
    /// unit and the upper arms by nearly 180 degrees.
    ///
    /// Left alone, that means a rig in two spaces at once - keyed bones driven
    /// into animation space, unkeyed bones still sitting where the model
    /// import put them. Returns null when the skeleton isn't in the container,
    /// in which case callers keep the previous behaviour.
    /// </summary>
    public static Dictionary<uint, LibJoint> GetBindPose(string skeletonName)
    {
        if (string.IsNullOrEmpty(skeletonName)) return null;

        if (SkeletonDB.TryGetValue(skeletonName, out var cached))
        {
            return cached;
        }

        Dictionary<uint, LibJoint> pose = null;
        AnimationSkeleton skel = Con?.Get<AnimationSkeleton>(skeletonName);
        if (skel != null)
        {
            LibJoint[] joints = skel.GetJoints();
            if (joints != null && joints.Length > 0)
            {
                pose = new Dictionary<uint, LibJoint>(joints.Length);
                foreach (LibJoint j in joints)
                {
                    pose[j.BoneCRC] = j;
                }
            }
        }

        SkeletonDB.Add(skeletonName, pose);
        return pose;
    }

    /// <summary>
    /// The bind-pose value for one of the seven curve components, already
    /// converted into Unity's space. Component order matches CraTransformCurve:
    /// 0-3 rotation xyzw, 4-6 position xyz.
    /// </summary>
    static float GetBindComponent(LibJoint joint, uint component)
    {
        float raw;
        switch (component)
        {
            case 0: raw = joint.BaseRotation.X; break;
            case 1: raw = joint.BaseRotation.Y; break;
            case 2: raw = joint.BaseRotation.Z; break;
            case 3: raw = joint.BaseRotation.W; break;
            case 4: raw = joint.BasePosition.X; break;
            case 5: raw = joint.BasePosition.Y; break;
            case 6: raw = joint.BasePosition.Z; break;
            default: return 0f;
        }
        return raw * ComponentMultipliers[component];
    }

    /// <summary>
    /// Fallback for a channel with no bind pose to fall back on: identity
    /// rotation, zero translation. Only reached when the skeleton is missing.
    /// </summary>
    static float GetIdentityComponent(uint component)
    {
        return component == 3 ? 1f : 0f;
    }

    /// <summary>
    /// Pose a rig at its skeleton's authored rest pose.
    /// </summary>
    /// <remarks>
    /// This is where the bind pose belongs. An AnimationBank keys only a subset
    /// of a skeleton's joints - measured across stock content, every animation
    /// leaves at least one alone - and the bones it does key are authored
    /// against the skeleton's rest pose, not against the one baked into the
    /// model's own bone hierarchy. Those two disagree badly: on stock soldiers
    /// the pelvis is over a unit out and the upper arms nearly 180 degrees.
    /// Leaving the untouched bones where the model import put them puts the rig
    /// in two spaces at once.
    ///
    /// Applying it to the hierarchy once, rather than adding constant bones to
    /// every clip, is what makes it affordable: Cra bakes FrameCount x
    /// BoneCount transforms per clip into a single fixed-size buffer
    /// (CraSettings.MAX_BAKED_CLIP_TRANSFORMS), so paying per clip exhausts it
    /// and then nothing can be played at all. Bones the animation does key are
    /// overwritten by playback anyway, so writing all of them here is both
    /// harmless and simpler than working out which.
    ///
    /// Safe to call more than once, and a no-op when the skeleton is unknown.
    /// </remarks>
    public static int ApplyBindPose(Transform root, string skeletonName)
    {
        if (root == null) return 0;

        Dictionary<uint, LibJoint> pose = GetBindPose(skeletonName);
        if (pose == null) return 0;

        int applied = 0;
        foreach (Transform bone in root.GetComponentsInChildren<Transform>(true))
        {
            // Same hash Cra binds curves with, so a name that resolves here
            // resolves there too.
            uint crc = HashUtils.GetCRC(bone.name);
            if (!pose.TryGetValue(crc, out LibJoint joint)) continue;

            bone.localRotation = new Quaternion(
                joint.BaseRotation.X * ComponentMultipliers[0],
                joint.BaseRotation.Y * ComponentMultipliers[1],
                joint.BaseRotation.Z * ComponentMultipliers[2],
                joint.BaseRotation.W * ComponentMultipliers[3]);

            bone.localPosition = new Vector3(
                joint.BasePosition.X * ComponentMultipliers[4],
                joint.BasePosition.Y * ComponentMultipliers[5],
                joint.BasePosition.Z * ComponentMultipliers[6]);

            ++applied;
        }
        return applied;
    }

    public static CraClip Import(string bankName, string animName, string skeletonName = null)
    {
        return Import(bankName, HashUtils.GetCRC(animName), animName, skeletonName);
    }

    public static CraClip Import(string bankName, uint animNameCRC, string clipNaming=null, string skeletonName=null)
    {
        CraClip clip;

        // The skeleton participates in the key: the same animation resolved
        // against two different skeletons is two different clips, and caching
        // them under one key would hand a human rig an ewok's rest pose.
        uint animID = HashUtils.GetCRC(bankName) * animNameCRC;
        if (!string.IsNullOrEmpty(skeletonName))
        {
            animID ^= HashUtils.GetCRC(skeletonName) * 2654435761u;
        }
        if (ClipDB.TryGetValue(animID, out clip))
        {
            return clip;
        }

        AnimationBank bank = Con.Get<AnimationBank>(bankName);
        if (bank == null)
        {
            Debug.LogError($"Cannot find AnimationBank '{bankName}'!");
            return null;
        }

        if (!bank.GetAnimationMetadata(animNameCRC, out int numFrames, out int numBones))
        {
            //Debug.LogError($"Cannot find Animation '{animNameCRC}' in AnimationBank '{bankName}'!");
            return null;
        }

        clip = new CraClip();
        clip.Name = string.IsNullOrEmpty(clipNaming) ? animNameCRC.ToString() : clipNaming;

        uint dummyroot = HashUtils.GetCRC("dummyroot");

        Dictionary<uint, LibJoint> bindPose = GetBindPose(skeletonName);

        uint[] boneCRCs = bank.GetBoneCRCs(animNameCRC);
        List<CraBone> bones = new List<CraBone>();
        for (int i = 0; i < boneCRCs.Length; ++i)
        {
            // no root motion
            if (boneCRCs[i] == dummyroot) continue;


            CraBone bone = new CraBone();
            bone.BoneHash = (int)boneCRCs[i];
            bone.Curve = new CraTransformCurve();

            // Assigned at declaration: the null check short-circuits, so the
            // compiler cannot see that TryGetValue ran and will not treat the
            // out parameter as definitely assigned.
            LibJoint joint = default;
            bool haveJoint = bindPose != null && bindPose.TryGetValue(boneCRCs[i], out joint);

            for (uint j = 0; j < 7; ++j)
            {
                if (!bank.GetCurve(animNameCRC, boneCRCs[i], j, out ushort[] indices, out float[] values))
                {
                    Debug.LogWarning($"Getting curve in animation '{animNameCRC}' of bone '{boneCRCs[i]}' at component '{j}' failed!");
                }
                else
                {
                    Debug.Assert(indices.Length == values.Length);

                    for (int k = 0; k < indices.Length; ++k)
                    {
                        int index = indices[k];
                        float time = index < numFrames ? index / 30.0f : numFrames / 30.0f;
                        float value = values[k] * ComponentMultipliers[j];

                        bone.Curve.Curves[j].EditKeys.Add(new CraKey(time, value));
                    }
                }

                // A channel with no keys is not survivable downstream: Cra's
                // GetEstimatedFrameCount indexes EditKeys[Count - 1] and its
                // Bake leaves BakedFrames null for CraTransformCurve to
                // dereference. Stock content always keys all seven, so this
                // costs nothing there and stops a malformed custom bank from
                // taking the level down.
                if (bone.Curve.Curves[j].EditKeys.Count == 0)
                {
                    float rest = haveJoint ? GetBindComponent(joint, j) : GetIdentityComponent(j);
                    bone.Curve.Curves[j].EditKeys.Add(new CraKey(0f, rest));
                }
            }

            bones.Add(bone);
        }

        // NOTE: unkeyed joints are deliberately NOT added to the clip.
        //
        // Adding a constant bone per unkeyed joint does produce the right
        // pose, but it pays for it on every clip: Cra bakes FrameCount x
        // BoneCount transforms into one fixed CraSettings
        // .MAX_BAKED_CLIP_TRANSFORMS buffer, so ~11 extra bones across every
        // clip a soldier loads exhausts it and then NO clip can be set at
        // all. ApplyBindPose poses the rig once instead, which costs nothing
        // per clip and leaves Cra driving only the bones that are animated.

        clip.SetBones(bones.ToArray());
        clip.Bake(120f);
        ClipDB.Add(animID, clip);
        return clip;
    }

    public static CraPlayer CreatePlayer(Transform root, string animBank, string animName, bool loop, string maskBone = null, string skeletonName = null)
    {
        CraClip clip = Import(animBank, animName, skeletonName);
        if (clip == null)
        {
            Debug.LogWarning($"Cannot find animation clip '{animName}' in bank '{animBank}'!");
            return CraPlayer.CreateEmpty();
        }

        CraPlayer player = CraPlayer.CreateNew();
        player.SetClip(clip);

        if (string.IsNullOrEmpty(maskBone))
        {
            player.Assign(root);
        }
        else
        {
            player.Assign(root, new CraMask(true, maskBone));
        }

        player.SetLooping(loop);
        return player;
    }

    public static CraPlayer CreatePlayer(Transform root, string[] animBanks, string animName, bool loop, string maskBone = null, string skeletonName = null)
    {
        CraClip clip = null;
        for (int i = 0; i < animBanks.Length; ++i)
        {
            clip = Import(animBanks[i], animName, skeletonName);
            if (clip != null)
            {
                break;
            }
        }
        if (clip == null)
        {
            Debug.LogWarning($"Cannot find animation clip '{animName}' in any of the specified banks '{animBanks}'!");
            return CraPlayer.CreateEmpty();
        }

        CraPlayer player = CraPlayer.CreateNew();
        player.SetClip(clip);

        if (string.IsNullOrEmpty(maskBone))
        {
            player.Assign(root);
        }
        else
        {
            player.Assign(root, new CraMask(true, maskBone));
        }

        player.SetLooping(loop);
        return player;
    }
}

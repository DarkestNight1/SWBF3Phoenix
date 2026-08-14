using System;
using System.Collections.Generic;
using UnityEngine;
using LibSWBF2.Wrappers;
using LibSWBF2.Enums;

#if UNITY_EDITOR
using UnityEditor;
using System.Reflection;
#endif



public class PhxAnimationGroup
{
    List<Animation> Animators;
    List<Vector3> StartPoints;
    List<string>    AnimationNames;

    public PhxAnimationGroup()
    {
        Animators = new List<Animation>();
        AnimationNames = new List<string>();
        StartPoints = new List<Vector3>();
    }

    public bool AddInstanceAnimationPair(Animation anim, string AnimationName)
    {
        StartPoints.Add(anim.gameObject.transform.position);
        Animators.Add(anim);
        AnimationNames.Add(AnimationName);

        return true;
    }


    public void Pause()
    {
        foreach (Animation anim in Animators)
        {
            anim.enabled = false;
        }
    }

    public void Play()
    {
        for (int i = 0; i < Animators.Count; i++)
        {
            if (Animators[i] == null) continue;

            // The anim root is created during initialisation, but a world can
            // name an instance that was already at the scene root, and a
            // destroyed instance leaves a null parent behind. Neither should
            // take out every other animation in the group.
            Transform tx = Animators[i].gameObject.transform;
            if (tx.parent != null)
            {
                tx.parent.position = StartPoints[i];
                tx.localPosition = Vector3.zero;
            }

            Animators[i].enabled = true;
            Animators[i].Play(AnimationNames[i]);
        }
    }

    public void Rewind()
    {
        for (int i = 0; i < Animators.Count; i++)
        {
            Animators[i].Rewind(AnimationNames[i]);
        }   
    }

    public void SetStartPoint()
    {
        for (int i = 0; i < Animators.Count; i++)
        {
            Transform tx = Animators[i].gameObject.transform;
            StartPoints[i] = tx.position;
        }
    }
}



public class PhxSceneAnimator
{
    Dictionary<string, AnimationClip> AnimDB;
    Dictionary<string, PhxAnimationGroup> AnimGroupDB;
   
    public PhxSceneAnimator()
    {
        AnimDB = new Dictionary<string, AnimationClip>();
        AnimGroupDB = new Dictionary<string, PhxAnimationGroup>();
    }


    public void InitializeWorldAnimations(World[] worlds)
    {
        Dictionary<string, WorldAnimation> WorldAnims = new Dictionary<string, WorldAnimation>();
        foreach (World world in worlds)
        {
            foreach (WorldAnimation worldAnim in world.GetAnimations())
            {
                WorldAnims[worldAnim.Name] = worldAnim;
            }
        }

        // Every instance name the loaded worlds actually define.
        //
        // A map lvl holds one world per game mode, and only the ones this mode
        // asked for are mounted. Coruscant's animation groups live in the
        // always-loaded base world but drive doors that exist only in
        // cor1_campaign, so a conquest match legitimately has nothing to bind
        // them to. Reporting that as missing content buries the real failures:
        // it was the whole of the "0 of 5 animations imported" figure.
        //
        // If a loaded world declares the instance and the scene lookup still
        // fails, that is a genuine import failure and is still reported.
        HashSet<string> declaredInstances = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (World world in worlds)
        {
            foreach (Instance inst in world.GetInstances())
            {
                if (inst != null && !string.IsNullOrEmpty(inst.Name))
                {
                    declaredInstances.Add(inst.Name);
                }
            }
        }

        foreach (World world in worlds)
        {
            foreach (WorldAnimationGroup animGroup in world.GetAnimationGroups())
            {
                // Both the guard and the store have to agree on case. They used
                // to disagree - ContainsKey on the raw name, store on the lower
                // one - so the guard never fired and a group named in two
                // worlds silently replaced the first one's animators.
                string groupKey = animGroup.Name.ToLower();
                if (AnimGroupDB.ContainsKey(groupKey)) continue;

                PhxAnimationGroup newAnimGroup = new PhxAnimationGroup();
                int bound = 0;
                int notInThisMode = 0;

                List<Tuple<string,string>> AnimInstPairs = animGroup.GetAnimationInstancePairs();
                foreach (var pair in AnimInstPairs)
                {
                    if (!WorldAnims.TryGetValue(pair.Item1, out WorldAnimation wldAnim))
                    {
                        BFImportDiagnostics.Missing(BFSourceKind.Animation, pair.Item1,
                                                    $"world animation group '{animGroup.Name}'");
                        continue;
                    }

                    GameObject instance = ResolveInstance(pair.Item2);
                    if (instance == null)
                    {
                        if (!declaredInstances.Contains(pair.Item2))
                        {
                            // The layer holding it is not part of this game
                            // mode. Absent by design, not a failure.
                            ++notInThisMode;
                            continue;
                        }

                        // Every miss used to be a silent continue, which is why
                        // a map full of dead machinery looked like a map with no
                        // machinery in it.
                        BFImportDiagnostics.Missing(BFSourceKind.Instance, pair.Item2,
                                                    $"world animation '{pair.Item1}'");
                        continue;
                    }

                    BFImportDiagnostics.Resolved(BFSourceKind.Animation, pair.Item1);

                    // Static batching bakes a renderer's transform into the
                    // combined mesh, so a batched object cannot be moved.
                    instance.isStatic = false;

                    Animation anim = instance.GetComponent<Animation>();
                    if (anim == null)
                    {
                        anim = instance.AddComponent<Animation>();
                    }

                    // The clip drives localPosition/localRotation, so the
                    // instance needs a parent holding its world placement -
                    // otherwise the animation's local-space keys are read as
                    // world space and the object teleports to the origin.
                    Transform parent = instance.transform.parent;
                    if (parent == null || parent.gameObject.name != instance.name + "_animroot")
                    {
                        GameObject animRoot = new GameObject(instance.name + "_animroot");
                        animRoot.transform.position = instance.transform.position;
                        animRoot.transform.rotation = instance.transform.rotation;
                        if (parent != null)
                        {
                            animRoot.transform.SetParent(parent, true);
                        }

                        instance.transform.SetParent(animRoot.transform, true);
                    }

                    AnimationCurve[] rKeys = AnimationLoader.Instance.GetWorldAnimationRotationCurves(wldAnim, instance.transform);
                    AnimationCurve[] pKeys = AnimationLoader.Instance.GetWorldAnimationPositionCurves(wldAnim, instance.transform);

                    if (rKeys == null && pKeys == null)
                    {
                        BFImportDiagnostics.Missing(BFSourceKind.Animation, wldAnim.Name,
                                                    $"no position or rotation curves for '{pair.Item2}'");
                        continue;
                    }

                    AnimationClip clip = new AnimationClip();
                    clip.legacy = true;
                    clip.name = wldAnim.Name;

                    if (rKeys != null)
                    {
                        // "localEulerAngles" is NOT an animatable property.
                        // Unity accepts the SetCurve call, warns once, and then
                        // drops the curve - which is exactly what "the machinery
                        // is parsed but nothing rotates" looks like from the
                        // outside. "localEulerAnglesRaw" is the real backing
                        // property and is what the editor writes for euler
                        // rotation curves.
                        clip.SetCurve("", typeof(Transform), "localEulerAnglesRaw.x", rKeys[0]);
                        clip.SetCurve("", typeof(Transform), "localEulerAnglesRaw.y", rKeys[1]);
                        clip.SetCurve("", typeof(Transform), "localEulerAnglesRaw.z", rKeys[2]);
                    }

                    if (pKeys != null)
                    {
                        clip.SetCurve("", typeof(Transform), "localPosition.x", pKeys[0]);
                        clip.SetCurve("", typeof(Transform), "localPosition.y", pKeys[1]);
                        clip.SetCurve("", typeof(Transform), "localPosition.z", pKeys[2]);
                    }

                    clip.wrapMode = wldAnim.IsLooping ? WrapMode.Loop : WrapMode.ClampForever;

                    anim.AddClip(clip, clip.name);
                    AnimDB[clip.name] = clip;

                    if (animGroup.PlaysAtStart)
                    {
                        anim.clip = clip;
                        anim.playAutomatically = true;
                        anim.Play();
                    }

                    newAnimGroup.AddInstanceAnimationPair(anim, clip.name);
                    ++bound;
                }

                // An empty group registered anyway would make PlayAnimation
                // return true having done nothing, which reads as "the script
                // is wrong" rather than "the group never bound".
                if (bound > 0)
                {
                    AnimGroupDB[groupKey] = newAnimGroup;
                }
                else if (notInThisMode == AnimInstPairs.Count && AnimInstPairs.Count > 0)
                {
                    // Whole group belongs to a layer this mode does not mount.
                    Debug.Log($"World animation group '{animGroup.Name}' animates objects " +
                              $"from a layer this game mode does not load - skipped.");
                }
                else if (AnimInstPairs.Count > 0)
                {
                    BFImportDiagnostics.Missing(BFSourceKind.Animation, animGroup.Name,
                                                "world animation group bound no instances");
                }
            }
        }
    }

    // GameObject.Find walks every root object in the scene, matches on name
    // only, and cannot see inactive objects. World instances are frequently
    // nested under their world root and are inactive while the level is still
    // loading, so it misses constantly. Index the scene once instead.
    Dictionary<string, GameObject> InstanceIndex;

    GameObject ResolveInstance(string name)
    {
        if (string.IsNullOrEmpty(name)) return null;

        GameObject direct = GameObject.Find(name);
        if (direct != null) return direct;

        if (InstanceIndex == null)
        {
            InstanceIndex = new Dictionary<string, GameObject>(StringComparer.OrdinalIgnoreCase);
            foreach (Transform tx in UnityEngine.Object.FindObjectsOfType<Transform>(true))
            {
                // First writer wins: an instance nearer the scene root is more
                // likely to be the one the world file means than a same-named
                // child deeper in some model's hierarchy.
                if (!InstanceIndex.ContainsKey(tx.name))
                {
                    InstanceIndex[tx.name] = tx.gameObject;
                }
            }
        }

        return InstanceIndex.TryGetValue(name, out GameObject found) ? found : null;
    }


    // Groups are stored under a lowercased key. Lua passes whatever case the
    // mission script author typed, so every lookup has to lower it too - these
    // three matched only when the script happened to agree with the world file.
    bool TryGetGroup(string name, out PhxAnimationGroup grp)
    {
        grp = null;
        return !string.IsNullOrEmpty(name) && AnimGroupDB.TryGetValue(name.ToLower(), out grp);
    }

    public bool PlayAnimation(string AnimationGroupName)
    {
        if (TryGetGroup(AnimationGroupName, out PhxAnimationGroup grp))
        {
            grp.Play();
            return true;
        }
        return false;
    }

    public bool RewindAnimation(string AnimationGroupName)
    {
        if (TryGetGroup(AnimationGroupName, out PhxAnimationGroup grp))
        {
            // Was calling Play(). Rewinding restarted the animation instead of
            // returning it to frame zero, so a script that rewound a mechanism
            // to reset it got the mechanism running again.
            grp.Rewind();
            return true;
        }

        return false;
    }

    public bool PauseAnimation(string AnimationGroupName)
    {
        if (TryGetGroup(AnimationGroupName, out PhxAnimationGroup grp))
        {
            grp.Pause();
            return true;
        }

        return false;
    }
}

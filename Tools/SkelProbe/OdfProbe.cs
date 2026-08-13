using System;
using System.Collections.Generic;
using LibSWBF2.Wrappers;
using LibSWBF2.Enums;

namespace SkelProbe
{
    /// <summary>
    /// Dump an entity class's declared properties.
    /// </summary>
    /// <remarks>
    /// GetAllProperties hands back hashed property names, and the hash cannot
    /// be reversed - but the VALUES are strings, and for animation properties
    /// the value is the name we are trying to learn. So probing the known
    /// property names by hash and printing what they hold answers "what is the
    /// walk cycle actually called" from the data rather than from guesswork.
    /// </remarks>
    static class OdfProbe
    {
        static readonly string[] Interesting =
        {
            "AnimationName", "SkeletonName", "GeometryName", "AnimatedBone",
            "WalkAnimation", "WalkingAnimation", "Animation",
            "FinAnimation", "MoveAnimation", "IdleAnimation",
            "WALKERSECTION", "WalkerHeight", "WalkerType",
            "AnimatedAddon", "AddonName",
            "TransformAnimation", "RollAnimation", "DeployAnimation",
            "ClassLabel", "ChunkGeometryName", "ChunkNodeName", "ChunkPhysics",
            "DestructionName", "ExplosionName", "MaxHealth", "Pilot9Pose", "PilotAnimation", "PilotPosition", "ThrowVelocity", "LaunchForce", "Velocity", "FuseTime", "TimeOut",
            "ExplosionName", "OrdnanceName", "TriggerRadius", "DetonateTime",
            "ArmedTime", "MaxRange", "ShotDelay", "GravityScale",
        };

        public static void Run(Level level, string nameFilter)
        {
            EntityClass[] classes;
            try { classes = level.Get<EntityClass>(); }
            catch { return; }

            foreach (EntityClass ec in classes)
            {
                if (ec == null || string.IsNullOrEmpty(ec.Name)) continue;
                if (nameFilter != "*" && !ec.Name.ToLowerInvariant().Contains(nameFilter)) continue;
            if (nameFilter == "*" && ec.ClassType != EEntityClassType.GameObjectClass) continue;

                Console.WriteLine($"### {ec.Name}  (base {ec.BaseClassName}, type {ec.ClassType})");

                foreach (string prop in Interesting)
                {
                    if (ec.GetProperty(prop, out string val) && !string.IsNullOrEmpty(val))
                    {
                        Console.WriteLine($"      {prop,-22} = {val}");
                    }
                }

                // Anything else whose value looks like an animation reference.
                ec.GetAllProperties(out uint[] hashes, out string[] values);
                var seen = new HashSet<string>();
                for (int i = 0; i < values.Length; ++i)
                {
                    string v = values[i];
                    if (string.IsNullOrEmpty(v) || v.Length > 40) continue;
                    string lv = v.ToLowerInvariant();
                    if ((lv.Contains("walk") || lv.Contains("roll") || lv.Contains("anim") ||
                         lv.Contains("deploy") || lv.Contains("stand")) && seen.Add(v))
                    {
                        Console.WriteLine($"      (unnamed prop #{hashes[i]}) = {v}");
                    }
                }
            }
        }
    }
}

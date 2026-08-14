using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using LibSWBF2.Wrappers;
using LibSWBF2.Enums;
using LibSWBF2;

namespace SkelProbe
{
    /// <summary>
    /// What collision a model actually declares, and for whom.
    /// </summary>
    /// <remarks>
    /// The importer maps a collider's mask onto a Unity layer, and anything it
    /// cannot map falls back to fully solid - a deliberate choice, because a
    /// missing floor is far worse than an over-solid wall. For a door that
    /// choice is exactly backwards: a door authored so soldiers pass through it
    /// becomes a wall.
    ///
    /// Set COLLISION_FILTER to a substring of the model name.
    /// </remarks>
    static class CollisionProbe
    {
        public static void Run(string path)
        {
            string filter = Environment.GetEnvironmentVariable("COLLISION_FILTER") ?? "";

            Container con = new Container();
            SWBF2Handle h = con.AddLevel(path);
            con.LoadLevels();

            for (int i = 0; i < 2400; ++i)
            {
                ELoadStatus s = con.GetStatus(h);
                if (s == ELoadStatus.Loaded || s == ELoadStatus.Failed) break;
                Thread.Sleep(50);
            }

            Level lvl = con.GetLevel(h, true);
            if (lvl == null)
            {
                Console.WriteLine($"=== {Path.GetFileName(path)}: not loaded");
                return;
            }

            Model[] models;
            try { models = lvl.Get<Model>(); }
            catch (Exception e) { Console.WriteLine($"=== {e.Message}"); return; }

            Console.WriteLine($"=== {Path.GetFileName(path)}: scanning {models.Length} model(s) " +
                              $"for '{filter}'");

            var maskTally = new SortedDictionary<uint, int>();
            int matched = 0, withMesh = 0, withPrims = 0, withNothing = 0;

            foreach (Model model in models)
            {
                if (model == null || model.Name == null) continue;
                if (filter.Length > 0 &&
                    model.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;

                ++matched;

                CollisionMesh mesh = null;
                try { mesh = model.GetCollisionMesh(); } catch { }

                CollisionPrimitive[] prims = null;
                try { prims = model.GetPrimitivesMasked(ECollisionMaskFlags.All); } catch { }

                bool hasMesh = mesh != null && mesh.VertexCount > 0;
                bool hasPrims = prims != null && prims.Length > 0;

                if (hasMesh) ++withMesh;
                if (hasPrims) ++withPrims;
                if (!hasMesh && !hasPrims) ++withNothing;

                Console.WriteLine($"  * {model.Name}  (skinned {model.IsSkinned})");

                if (hasMesh)
                {
                    uint m = (uint)mesh.MaskFlags;
                    maskTally.TryGetValue(m, out int n);
                    maskTally[m] = n + 1;
                    // Bounds decide whether a static mesh is the door frame or
                    // the door itself. A frame is a thin ring around the
                    // opening; a panel spans it.
                    string extent = "";
                    try
                    {
                        var verts = mesh.GetVertices<UnityLikeVec3>();
                        if (verts != null && verts.Length > 0)
                        {
                            float minX = verts[0].X, maxX = verts[0].X;
                            float minY = verts[0].Y, maxY = verts[0].Y;
                            float minZ = verts[0].Z, maxZ = verts[0].Z;
                            foreach (var v in verts)
                            {
                                if (v.X < minX) minX = v.X; if (v.X > maxX) maxX = v.X;
                                if (v.Y < minY) minY = v.Y; if (v.Y > maxY) maxY = v.Y;
                                if (v.Z < minZ) minZ = v.Z; if (v.Z > maxZ) maxZ = v.Z;
                            }
                            extent = $" size {maxX - minX:F1} x {maxY - minY:F1} x {maxZ - minZ:F1}";
                        }
                    }
                    catch { }

                    Console.WriteLine($"      collision mesh  {mesh.VertexCount,6} verts  " +
                                      $"node '{mesh.NodeName}'{extent}");
                }

                if (hasPrims)
                {
                    foreach (CollisionPrimitive p in prims)
                    {
                        if (p == null) continue;
                        uint m = (uint)p.MaskFlags;
                        maskTally.TryGetValue(m, out int n);
                        maskTally[m] = n + 1;
                        Console.WriteLine($"      primitive {p.PrimitiveType,-10} '{p.Name}' " +
                                          $"parent '{p.ParentName}'");
                    }
                }

                if (!hasMesh && !hasPrims)
                {
                    Console.WriteLine("      NO COLLISION AUTHORED");
                }
            }

            Console.WriteLine($"    matched {matched}: {withMesh} with a collision mesh, " +
                              $"{withPrims} with primitives, {withNothing} with neither");
            Console.WriteLine("    --- mask histogram ---");
            foreach (var kv in maskTally)
            {
                Console.WriteLine($"    mask {kv.Key,6} [{Describe((ECollisionMaskFlags)kv.Key)}]  x{kv.Value}");
            }
        }

        static string Describe(ECollisionMaskFlags f)
        {
            if ((uint)f == 0) return "none";
            var parts = new List<string>();
            if (f.HasFlag(ECollisionMaskFlags.Soldier)) parts.Add("Soldier");
            if (f.HasFlag(ECollisionMaskFlags.Vehicle)) parts.Add("Vehicle");
            if (f.HasFlag(ECollisionMaskFlags.Building)) parts.Add("Building");
            if (f.HasFlag(ECollisionMaskFlags.Terrain)) parts.Add("Terrain");
            if (f.HasFlag(ECollisionMaskFlags.Ordnance)) parts.Add("Ordnance");
            if (f.HasFlag(ECollisionMaskFlags.Flag)) parts.Add("Flag");
            if (f.HasFlag(ECollisionMaskFlags.All)) parts.Add("All");
            return parts.Count == 0 ? $"0x{(uint)f:x}" : string.Join("|", parts);
        }
    }
}

namespace SkelProbe
{
    /// <summary>Plain XYZ, so vertices can be read without a Unity reference.</summary>
    public struct UnityLikeVec3
    {
        public float X, Y, Z;
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using LibSWBF2.Wrappers;

namespace SkelProbe
{
    /// <summary>
    /// Reports what a map's terrain actually says, for the two questions the
    /// Unity side cannot answer about it.
    /// </summary>
    /// <remarks>
    /// <b>Grid unit size.</b> Terrain::GetHeightMap hands C# a uint32_t dimScale
    /// that the native side produces by casting tern INFO's GridUnitSize - a
    /// float - to an integer. From C# the result is indistinguishable from a
    /// grid size that was always whole, so the truncation cannot be seen from
    /// there at all. It can be measured, though: the vertex buffer is in world
    /// space, so its span divided by the grid dimension is the real unit size.
    /// Printing the stated value beside the measured one says whether the
    /// truncation is real, and on which maps.
    ///
    /// <b>Layer textures.</b> BFMapWater derives a map's water level from the
    /// terrain layer the artists painted the bed with, which only works if the
    /// bed is painted with a texture whose name says water. That is a claim
    /// about shipped data, so it is worth reading rather than assuming.
    ///
    /// Usage: SkelProbe --terrain &lt;lvl-or-directory&gt;
    /// </remarks>
    static class TerrainProbe
    {
        public static void Run(string path)
        {
            string name = Path.GetFileName(path);

            Level level;
            try { level = Level.FromFile(path); }
            catch (Exception e)
            {
                Console.WriteLine($"=== {name} : LOAD FAILED ({e.Message})");
                return;
            }
            if (level == null)
            {
                Console.WriteLine($"=== {name} : LOAD RETURNED NULL");
                return;
            }

            var worlds = level.Get<World>();
            Console.WriteLine($"=== {name} : {worlds.Length} world(s), {level.Get<Terrain>().Length} terrain(s) at level scope");
            foreach (Terrain t in level.Get<Terrain>()) Report(name, "(level scope)", t);
            foreach (World world in worlds)
            {
                Terrain terrain = null;
                try { terrain = world.GetTerrain(); }
                catch (Exception e)
                {
                    Console.WriteLine($"  {world.Name}: terrain read failed ({e.Message})");
                    continue;
                }
                if (terrain == null) continue;

                Report(name, world.Name, terrain);
            }
        }

        static void Report(string file, string worldName, Terrain terrain)
        {
            Console.WriteLine($"=== {file} / world '{worldName}'");

            terrain.GetHeightMap(out uint dim, out uint statedScale, out float[] _);

            // The vertex buffer is post-stitch and in world space, centred on
            // the origin - the same buffer BuildTerrainMesh consumes - so its
            // span is the terrain's true extent regardless of what the header
            // rounded the unit size to.
            float measuredExtent = 0f;
            try
            {
                var positions = terrain.GetPositionsBuffer<System.Numerics.Vector3>();
                if (positions != null && positions.Length > 0)
                {
                    float minX = float.MaxValue, maxX = float.MinValue;
                    float minZ = float.MaxValue, maxZ = float.MinValue;
                    foreach (var p in positions)
                    {
                        if (p.X < minX) minX = p.X;
                        if (p.X > maxX) maxX = p.X;
                        if (p.Z < minZ) minZ = p.Z;
                        if (p.Z > maxZ) maxZ = p.Z;
                    }
                    measuredExtent = Math.Max(maxX - minX, maxZ - minZ);
                }
            }
            catch (Exception e)
            {
                Console.WriteLine($"  position buffer unavailable ({e.Message})");
            }

            float statedExtent = dim * statedScale;
            float measuredScale = dim > 0 ? measuredExtent / dim : 0f;

            Console.WriteLine($"  grid dim        : {dim}");
            Console.WriteLine($"  unit size stated: {statedScale}   (integer, as C# receives it)");
            Console.WriteLine($"  unit size real  : {measuredScale:0.####}   (measured off the vertex buffer)");
            Console.WriteLine($"  extent stated   : {statedExtent}m");
            Console.WriteLine($"  extent measured : {measuredExtent:0.##}m");

            if (statedScale == 0)
            {
                Console.WriteLine("  >> TRUNCATED TO ZERO: bound is 0, so baked terrain lighting is");
                Console.WriteLine("     dropped and the blend UV divides by zero. This map reads flat.");
            }
            else if (Math.Abs(measuredExtent - statedExtent) > measuredScale * 2f)
            {
                float error = statedExtent > 0f
                    ? Math.Abs(measuredExtent - statedExtent) / measuredExtent * 100f : 100f;
                Console.WriteLine($"  >> TRUNCATED: stated extent is off by {error:0.#}%. Blend map and");
                Console.WriteLine("     baked lighting are misregistered against the ground by that much.");
            }
            else
            {
                Console.WriteLine("  ok: stated and measured agree, this map was never affected.");
            }

            float floor = terrain.HeightLowerBound, ceiling = terrain.HeightUpperBound;
            Console.WriteLine($"  height bounds   : {floor:0.##} .. {ceiling:0.##}");


            // The baked per-vertex terrain lighting. BuildTerrainMesh only
            // accepts this when its length matches the vertex count exactly,
            // and BuildBlendLightingScale returns null without it - which is
            // the codebase's own stated cause of terrain that "reads uniformly
            // flat". Whether that buffer arrives, and whether it carries any
            // variation when it does, is the whole question.
            try
            {
                byte[] colors = terrain.GetColorBuffer();
                var positions2 = terrain.GetPositionsBuffer<System.Numerics.Vector3>();
                int verts = positions2?.Length ?? 0;

                if (colors == null || colors.Length == 0)
                {
                    Console.WriteLine("  colour buffer   : ABSENT  >> no baked lighting, terrain reads FLAT");
                }
                else if (verts > 0 && colors.Length != verts * 4)
                {
                    Console.WriteLine($"  colour buffer   : {colors.Length} bytes for {verts} verts " +
                                      $"(expected {verts * 4}) >> REJECTED by BuildTerrainMesh, reads FLAT");
                }
                else
                {
                    double sum = 0; float min = 1f, max = 0f; int nonWhite = 0;
                    int n = colors.Length / 4;
                    for (int i = 0; i < n; ++i)
                    {
                        float luma = (0.299f * colors[i * 4] + 0.587f * colors[i * 4 + 1]
                                    + 0.114f * colors[i * 4 + 2]) / 255f;
                        sum += luma;
                        if (luma < min) min = luma;
                        if (luma > max) max = luma;
                        if (luma < 0.99f) ++nonWhite;
                    }
                    double mean = sum / Math.Max(1, n);
                    double varied = 100.0 * nonWhite / Math.Max(1, n);
                    Console.WriteLine($"  colour buffer   : {n} verts, luma min {min:0.###} max {max:0.###} " +
                                      $"mean {mean:0.###}, {varied:0.#}% below white");
                    if (max - min < 0.02f)
                    {
                        Console.WriteLine("     >> effectively CONSTANT: baked lighting carries no variation,");
                        Console.WriteLine("        so terrain shading comes only from normals. Reads FLAT.");
                    }
                }
            }
            catch (Exception e)
            {
                Console.WriteLine($"  colour buffer   : read failed ({e.Message})");
            }

            // Height histogram. A map whose water the munged data does not
            // carry still shows the basin the water sat in: a seabed is a
            // large, nearly level area at the bottom of the range, and it
            // stands out as one dominant bucket that its neighbours do not
            // share.
            try
            {
                var pos = terrain.GetPositionsBuffer<System.Numerics.Vector3>();
                if (pos != null && pos.Length > 0)
                {
                    float lo = float.MaxValue, hi = float.MinValue;
                    foreach (var p in pos) { if (p.Y < lo) lo = p.Y; if (p.Y > hi) hi = p.Y; }

                    const int Buckets = 24;
                    var counts = new int[Buckets];
                    float span = Math.Max(0.0001f, hi - lo);
                    foreach (var p in pos)
                    {
                        int b = (int)((p.Y - lo) / span * (Buckets - 1));
                        counts[Math.Clamp(b, 0, Buckets - 1)]++;
                    }

                    Console.WriteLine($"  height histogram ({lo:0.##} .. {hi:0.##}):");
                    int peak = 1;
                    foreach (int c in counts) peak = Math.Max(peak, c);
                    for (int b = 0; b < Buckets; ++b)
                    {
                        float from = lo + span * b / (Buckets - 1);
                        float pct = 100f * counts[b] / pos.Length;
                        int bar = (int)(40.0 * counts[b] / peak);
                        Console.WriteLine($"    {from,8:0.##}m {new string('#', bar).PadRight(40)} {pct,5:0.#}%");
                    }
                }
            }
            catch (Exception e) { Console.WriteLine($"  histogram failed ({e.Message})"); }

            // Percentiles through the basin. The histogram says where the flat
            // area is; these say where it stops, which is the shoreline and so
            // the water level.
            try
            {
                var pos2 = terrain.GetPositionsBuffer<System.Numerics.Vector3>();
                if (pos2 != null && pos2.Length > 0)
                {
                    var ys = new float[pos2.Length];
                    for (int i = 0; i < pos2.Length; ++i) ys[i] = pos2[i].Y;
                    Array.Sort(ys);
                    Console.Write("  percentiles     :");
                    foreach (int p in new[] { 40, 50, 55, 58, 60, 62, 65, 70, 80 })
                    {
                        int idx = Math.Clamp((int)((ys.Length - 1) * (p / 100.0)), 0, ys.Length - 1);
                        Console.Write($"  p{p}={ys[idx]:0.##}");
                    }
                    Console.WriteLine();
                }
            }
            catch { }
            var layers = new List<string>(terrain.LayerTextures);
            Console.WriteLine($"  layers ({layers.Count}):");
            bool anyWater = false;
            for (int i = 0; i < layers.Count; ++i)
            {
                string layer = layers[i] ?? "";
                bool water = layer.IndexOf("water", StringComparison.OrdinalIgnoreCase) >= 0
                          || layer.IndexOf("ocean", StringComparison.OrdinalIgnoreCase) >= 0
                          || layer.IndexOf("river", StringComparison.OrdinalIgnoreCase) >= 0;
                anyWater |= water;
                Console.WriteLine($"    [{i,2}] {layer}{(water ? "   <-- reads as WATER" : "")}");
            }

            Console.WriteLine(anyWater
                ? "  >> a water layer exists: BFMapWater can measure this map's water level."
                : "  >> NO water layer: BFMapWater cannot measure here, needs an override entry.");
            Console.WriteLine();
        }
    }
}

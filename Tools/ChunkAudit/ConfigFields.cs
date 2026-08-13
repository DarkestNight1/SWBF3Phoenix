using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

/// <summary>
/// Tallies the fields actually present inside SWBF2 config chunks.
/// </summary>
/// <remarks>
/// A config chunk stores field names as FNV-1a hashes, never as text, so
/// "which properties did the level designers author?" cannot be answered by
/// reading the file - only by hashing a candidate name and looking for it.
/// This collects every hash that occurs, how many arguments it carries and an
/// example value, then reverses the ones whose names can be guessed.
///
/// The residue is the point: a hash that appears on thousands of lights and
/// matches no name we know is authored data nobody is reading.
///
/// Layout, confirmed against cor1's lght chunk: each DATA payload is a 4-byte
/// name hash, a 1-byte argument count, that many 4-byte values, then four
/// trailing bytes - so the payload size is always 9 + 4*argc, which is a
/// strong enough shape to reject anything that is not a field.
/// </remarks>
internal static class ConfigFields
{
    sealed class FieldTally
    {
        public long Count;
        public byte Args;
        public string Example = "";
        public readonly HashSet<string> Parents = new HashSet<string>();
    }

    static readonly Dictionary<uint, FieldTally> Fields = new Dictionary<uint, FieldTally>();

    public static void Record(byte[] data, int at, int size, string parent)
    {
        if (size < 9) return;

        uint hash = BitConverter.ToUInt32(data, at);
        byte argc = data[at + 4];
        if (size != 9 + 4 * argc) return;

        if (!Fields.TryGetValue(hash, out FieldTally tally))
        {
            tally = new FieldTally { Args = argc };
            Fields.Add(hash, tally);
            tally.Example = Describe(data, at + 5, argc);
        }

        ++tally.Count;
        tally.Parents.Add(parent);
    }

    /// <summary>
    /// Render a field's arguments, guessing float against name-hash.
    /// </summary>
    /// <remarks>
    /// A string argument is stored as the hash of the string, and a hash
    /// reinterpreted as a float is almost always absurd - denormal, enormous,
    /// or NaN. Showing those as hashes rather than as numbers is what makes a
    /// texture or region reference recognisable in the output.
    /// </remarks>
    static string Describe(byte[] data, int at, int argc)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < argc && i < 4; ++i)
        {
            float f = BitConverter.ToSingle(data, at + i * 4);
            uint u = BitConverter.ToUInt32(data, at + i * 4);

            bool looksNumeric = !float.IsNaN(f) && !float.IsInfinity(f) &&
                                Math.Abs(f) < 1e7f && (f == 0f || Math.Abs(f) > 1e-6f);

            sb.Append(looksNumeric ? $"{f:0.###}" : $"#{u:x8}").Append(' ');
        }
        return sb.ToString().Trim();
    }

    static uint Fnv(string s)
    {
        uint h = 2166136261;
        foreach (char c in s) { h ^= (uint)(c | 0x20); h *= 16777619; }
        return h;
    }

    /// <summary>Names to try. Anything unmatched is reported as unknown.</summary>
    static readonly string[] Candidates =
    {
        // .lgt
        "Light", "GlobalLights", "Light1", "Light2", "Top", "Bottom", "Ambient", "Type", "Color",
        "Position", "Rotation", "Range", "Cone", "CastShadow", "CastSpecular", "Static",
        "Bidirectional", "Texture", "TileUV", "OffsetUV", "PS2BlendMode", "Region", "Intensity",
        "Specular", "Enabled", "Attenuation", "InnerCone", "OuterCone", "Falloff", "Volume",
        "Filter", "Shadow", "NoShadow", "Diffuse", "SpecularColor", "Name", "Scale", "Radius",
        "Width", "Height", "Length", "Angle", "Softness", "Bias", "Priority",
        // .sky
        "SkyObject", "DomeInfo", "DomeModel", "Geometry", "Movement", "TileU", "TileV", "SunInfo",
        "Sun", "Size", "Degree", "LowResTerrainTexture", "TerrainColor", "FogColor", "FogRanges",
        "FogRange", "NearSceneRange", "FarSceneRange", "AmbientColor", "TopAmbientColor",
        "BottomAmbientColor", "CharacterAmbientColor", "VehicleAmbientColor", "ShadowColor",
        "SunDirection", "ModelName", "HorizonColor", "ZenithColor", "CloudLayer", "Enable",
        "Density", "Speed", "Offset", "Alpha", "SkyFogColor", "TerrainFogColor", "FogFar",
        "FogNear", "AmbientTop", "AmbientBottom", "Brightness", "Emissive", "Detail", "Layer",
        "Blend", "Sort", "NoZWrite", "Additive", "Modulate", "Opacity", "SunPosition",
        "SunSize", "SunColor", "HaloColor", "HaloSize", "Star", "StarDistance", "Distance",
        "PC", "PS2", "XBOX", "Version", "Softness2", "Segment", "Patch", "Terrain",
    };

    public static void Report()
    {
        var byHash = new Dictionary<uint, string>();
        foreach (string name in Candidates) byHash[Fnv(name)] = name;

        int known = Fields.Keys.Count(h => byHash.ContainsKey(h));

        Console.WriteLine($"{Fields.Count} distinct field(s); {known} named, {Fields.Count - known} unknown\n");
        Console.WriteLine($"{"HASH",-9} {"ARGC",4} {"COUNT",9}  {"NAME",-22} EXAMPLE            PARENTS");
        Console.WriteLine(new string('-', 104));

        foreach (KeyValuePair<uint, FieldTally> e in Fields.OrderByDescending(e => e.Value.Count))
        {
            string name = byHash.TryGetValue(e.Key, out string n) ? n : "?";
            string parents = string.Join(",", e.Value.Parents.OrderBy(s => s).Take(3));
            Console.WriteLine($"{e.Key:x8}  {e.Value.Args,4} {e.Value.Count,9}  {name,-22} " +
                              $"{e.Value.Example,-18} {parents}");
        }
    }
}

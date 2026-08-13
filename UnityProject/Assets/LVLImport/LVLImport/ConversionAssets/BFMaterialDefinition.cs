using System;
using System.Collections.Generic;
using LibSWBF2.Enums;
using LibMaterial = LibSWBF2.Wrappers.Material;

/// <summary>
/// Immutable rendering-relevant view of an authored SWBF2 material.
///
/// Unity materials are a presentation cache and may be recreated whenever the
/// renderer changes.  This definition deliberately retains the source values
/// needed to interpret a MATL without treating the generated HDRP Material as
/// the source of truth.
/// </summary>
public sealed class BFMaterialDefinition
{
    public readonly string SourceName;
    public readonly string[] TextureNames;
    public readonly EMaterialFlags Flags;
    public readonly uint SpecularExponent;
    public readonly LibSWBF2.Types.Vector3 SpecularColor;
    public readonly LibSWBF2.Types.Vector3 DiffuseColor;
    public readonly string AttachedLight;
    public readonly uint Param1;
    public readonly uint Param2;

    public BFMaterialDefinition(LibMaterial source)
    {
        if (source == null) throw new ArgumentNullException(nameof(source));

        TextureNames = source.Textures == null
            ? Array.Empty<string>()
            : new List<string>(source.Textures).ToArray();
        // MATL does not expose a standalone material name through LibSWBF2;
        // the base texture plus flags is the stable identity used by the
        // original importer as well.
        SourceName = TextureNames.Length > 0 ? TextureNames[0] : string.Empty;
        Flags = source.MaterialFlags;
        SpecularExponent = source.SpecularExponent;
        SpecularColor = source.SpecularColor;
        DiffuseColor = source.DiffuseColor;
        AttachedLight = source.AttachedLight ?? string.Empty;
        Param1 = source.Param1;
        Param2 = source.Param2;
    }

    public string BaseTextureName => TextureNames.Length > 0 ? TextureNames[0] : string.Empty;
    public string BumpTextureName => TextureNames.Length > 1 ? TextureNames[1] : string.Empty;
    public bool Has(EMaterialFlags flag) => Flags.HasFlag(flag);
}

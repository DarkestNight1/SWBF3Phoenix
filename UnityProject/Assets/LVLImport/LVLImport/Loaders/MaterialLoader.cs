using System.IO;
using System.Collections.Generic;

using UnityEngine;
#if !LVLIMPORT_NO_EDITOR
using UnityEditor;
#endif

using LibSWBF2.Enums;

using LibMaterial = LibSWBF2.Wrappers.Material;
using UMaterial = UnityEngine.Material;


public class MaterialLoader : Loader
{
    // NOTE: Loading shaders/materials must NOT happen in static constructor,
    // otherwise building a project using this at runtime won't work!

    const string DefaultSTDShader = "ConversionAssets/SWBFStandard";
    const string DefaultTerrainSTDShader = "ConversionAssets/SWBFTerrain";

    UMaterial _DefaultMaterial = null;
    UMaterial _DefaultTerrainMaterial = null;
    UMaterial _DefaultHDRPTransparentMaterial = null;
    UMaterial _DefaultHDRPUnlitMaterial = null;

    UMaterial GetDefaultMaterial()
    {
        if (_DefaultMaterial == null)
        {
            if (UseHDRP)
            {
                _DefaultMaterial = Resources.Load<UMaterial>("HDRPLit");
            }
            else
            {
                _DefaultMaterial = new UMaterial(Shader.Find(DefaultSTDShader));
            }
        }
        return _DefaultMaterial;
    }

    public UMaterial GetDefaultTerrainMaterial()
    {
        if (_DefaultTerrainMaterial == null)
        {
            if (UseHDRP)
            {
                _DefaultTerrainMaterial = Resources.Load<UMaterial>("HDRPTerrain");
            }
            else
            {
                _DefaultTerrainMaterial = new UMaterial(Shader.Find(DefaultTerrainSTDShader));
            }

            // Terrain renders magenta when the material's shader cannot be
            // resolved. Testing for a null shader is NOT enough and was the
            // reason an earlier version of this guard never fired: when a
            // material references a shader GUID that no asset provides, Unity
            // substitutes Hidden/InternalErrorShader rather than leaving the
            // reference null. That is exactly what happened here - the terrain
            // material pointed at a shader GUID present nowhere in the project
            // - and it renders as the familiar magenta with nothing logged.
            if (!IsUsable(_DefaultTerrainMaterial))
            {
                Debug.LogError(
                    "Terrain material has no usable shader (unresolved shader reference, or " +
                    "the terrain shadergraph failed to compile) - terrain would render " +
                    "magenta. Falling back to the default lit material; terrain layer " +
                    "blending will be missing.");
                _DefaultTerrainMaterial = new UMaterial(GetDefaultMaterial());
            }
        }
        return _DefaultTerrainMaterial;
    }

    /// <summary>
    /// Whether a material will actually render, rather than showing Unity's
    /// magenta error colour. Covers the three distinct failures: the asset
    /// itself missing, a null shader, and - the one that is easy to miss - an
    /// unresolved shader reference, which Unity fills in with
    /// Hidden/InternalErrorShader instead of null.
    /// </summary>
    static bool IsUsable(UMaterial mat)
    {
        return mat != null
            && mat.shader != null
            && mat.shader.name != "Hidden/InternalErrorShader";
    }

    UMaterial GetDefaultTransparentMaterial()
    {
        if (_DefaultHDRPTransparentMaterial == null)
        {
            _DefaultHDRPTransparentMaterial = Resources.Load<UMaterial>("HDRPTransparent");
        }
        return _DefaultHDRPTransparentMaterial;
    }

    UMaterial GetDefaultUnlitMaterial()
    {
        if (_DefaultHDRPUnlitMaterial == null)
        {
            _DefaultHDRPUnlitMaterial = Resources.Load<UMaterial>("HDRPUnlit");
        }
        return _DefaultHDRPUnlitMaterial;
    }

    public static bool UseHDRP;


    public static MaterialLoader Instance { get; private set; } = null;

    static MaterialLoader()
    {
        Instance = new MaterialLoader();
    }

    private Dictionary<string, UMaterial> materialDataBase = new Dictionary<string, UMaterial>();
    // Keep source semantics beside the Unity-material cache.  A Unity Material
    // is a mutable HDRP implementation detail; this dictionary remains the
    // renderer-independent record used to recreate it after an import reset.
    private Dictionary<string, BFMaterialDefinition> definitions = new Dictionary<string, BFMaterialDefinition>();


    public void ResetDB()
    {
        // Reference-drop only, never Object.Destroy() - see the note in
        // TextureLoader.ResetDB(). Unreferenced materials are reclaimed by
        // Resources.UnloadUnusedAssets() in PhxScene.Destroy().
        materialDataBase.Clear();
        definitions.Clear();
    }

    static string DefinitionKey(LibMaterial mat)
    {
        if (mat == null) return string.Empty;
        string baseTexture = mat.Textures != null && mat.Textures.Count > 0 ? mat.Textures[0] : string.Empty;
        string textures = mat.Textures == null ? string.Empty : string.Join("|", mat.Textures);
        return baseTexture + "_" + ((uint)mat.MaterialFlags).ToString() + "_" +
               mat.SpecularExponent + "_" + mat.SpecularColor.X + "_" +
               mat.SpecularColor.Y + "_" + mat.SpecularColor.Z + "_" +
               mat.Param1 + "_" + mat.Param2 + "_" + textures + "_" +
               (mat.AttachedLight ?? string.Empty);
    }

    BFMaterialDefinition PreserveDefinition(LibMaterial mat)
    {
        string key = DefinitionKey(mat);
        if (!definitions.TryGetValue(key, out BFMaterialDefinition definition))
        {
            definition = new BFMaterialDefinition(mat);
            definitions.Add(key, definition);
            // A MATL has no name of its own, so the definition key doubles as
            // its identity in the import report - distinct materials count
            // distinctly, which is the number that matters there.
            BFImportDiagnostics.Resolved(BFSourceKind.Material, key);
        }
        return definition;
    }

    /// <summary>Returns the authored material record used for this import.</summary>
    public BFMaterialDefinition GetDefinition(LibMaterial mat)
    {
        return mat == null ? null : PreserveDefinition(mat);
    }


    /// <summary>
    /// Global scale on imported smoothness, so the whole game can be dialled
    /// back without re-importing. 1 = use the authored value as converted.
    /// </summary>
    public static float SmoothnessScale = 1.0f;

    /// <summary>
    /// Drive HDRP's specular response from the material data BF2 actually
    /// authored.
    /// </summary>
    /// <remarks>
    /// This replaces a hardcoded `_Metallic = 0; _Smoothness = 0` that ran on
    /// EVERY material. Zero smoothness means no specular highlight and no
    /// usable reflection, so SSR, reflection probes and the entire
    /// physically-based half of the pipeline were switched off game-wide -
    /// armour, glass, polished floors and concrete all shaded identically flat.
    ///
    /// The data to do better was already parsed and sitting unused: LibSWBF2
    /// exposes SpecularExponent, SpecularColor and a Specular material flag,
    /// none of which had a single reference anywhere in the project.
    ///
    /// Metallic stays 0 deliberately. BF2 predates metal/rough workflows and
    /// has no metalness channel to convert; inventing one would be guesswork,
    /// whereas the specular exponent is real authored data.
    /// </remarks>
    static void ApplySpecular(UMaterial material, LibMaterial mat, EMaterialFlags flags)
    {
        material.SetFloat("_Metallic", 0.0f);

        // No specular flag means the artist marked this surface as matte.
        // Respect that rather than giving everything a sheen.
        if (!flags.HasFlag(EMaterialFlags.Specular))
        {
            material.SetFloat("_Smoothness", 0.0f);
            return;
        }

        float smoothness = SmoothnessFromExponent(mat.SpecularExponent);

        // SpecularColor scales how strong the highlight is. A near-black
        // specular colour is the data's way of saying "flagged specular, but
        // barely any" - fold its brightness into smoothness, since HDRP's
        // metallic workflow has no separate specular-tint channel to put it in.
        LibSWBF2.Types.Vector3 spec = mat.SpecularColor;
        float specBrightness = Mathf.Clamp01(Mathf.Max(spec.X, Mathf.Max(spec.Y, spec.Z)));
        smoothness *= specBrightness;

        material.SetFloat("_Smoothness", Mathf.Clamp01(smoothness * SmoothnessScale));
    }

    /// <summary>
    /// Blinn-Phong specular exponent to a roughness-based smoothness.
    /// </summary>
    /// <remarks>
    /// The standard conversion is roughness = sqrt(2 / (exponent + 2)), which
    /// is what maps a 2005-era specular power onto a modern microfacet BRDF
    /// without inventing a curve. Exponent 0 falls out as fully rough, which is
    /// the right reading of "specular flagged but no power set".
    /// </remarks>
    static float SmoothnessFromExponent(uint exponent)
    {
        if (exponent == 0) return 0f;

        float roughness = Mathf.Sqrt(2f / (exponent + 2f));
        return Mathf.Clamp01(1f - roughness);
    }

    /// <summary>Strength of imported bump maps. 0 disables them entirely.</summary>
    public static float NormalMapStrength = 1.0f;

    /// <summary>
    /// Wire the material's authored bump map into HDRP's normal slot.
    /// </summary>
    /// <remarks>
    /// BF2 genuinely ships bump maps - the EMaterialFlags enum has a BumpMap
    /// bit and the second texture slot carries the map. Only Textures[0] was
    /// ever read, so all of it was discarded and every surface rendered
    /// geometrically flat.
    ///
    /// Slot ordering is measured, not assumed: over 752 materials in
    /// kam1.lvl + side/rep.lvl, exactly 47 carry the BumpMap flag and exactly
    /// 47 use a second texture slot. That one-to-one correspondence is what
    /// makes Textures[1] safe to treat as the bump map here.
    /// </remarks>
    static void ApplyBumpMap(UMaterial material, LibMaterial mat, EMaterialFlags flags)
    {
        if (!flags.HasFlag(EMaterialFlags.BumpMap)) return;
        if (NormalMapStrength <= 0f) return;
        if (mat.Textures == null || mat.Textures.Count < 2) return;

        string bumpName = mat.Textures[1];
        if (string.IsNullOrEmpty(bumpName)) return;

        // linear: a normal map is data, not colour. Read through the sRGB curve
        // its normals come out wrong and the lighting is consistently off.
        Texture2D bump = TextureLoader.Instance.ImportTexture(bumpName, false, false, true);
        if (bump == null) return;

        material.EnableKeyword("_NORMALMAP");
        material.EnableKeyword("_NORMALMAP_TANGENT_SPACE");
        material.SetTexture("_NormalMap", bump);
        material.SetFloat("_NormalScale", NormalMapStrength);
    }

    /// <summary>
    /// Derive modern PBR response from the stock diffuse texture.
    /// </summary>
    /// <remarks>
    /// The base colour is never touched - the artist's work is the artist's
    /// work. What is derived is only how light behaves on it: relief from the
    /// painted linework, occlusion from the painted-in shadow, smoothness from
    /// local contrast, and metallic where the surface is one that can be
    /// metal at all.
    ///
    /// Skipped for anything whose response is already decided by the material
    /// flags. A glowing panel, a transparent force field or a material that
    /// shipped its own bump map has an authored answer, and deriving a second
    /// one on top of it fights the original rather than modernising it.
    /// </remarks>
    void EnhanceFromStockTexture(UMaterial material, Texture2D source,
                                 BFMaterialDefinition definition, EMaterialFlags matFlags)
    {
        if (matFlags.HasFlag(EMaterialFlags.Glow) ||
            matFlags.HasFlag(EMaterialFlags.Transparent) ||
            !string.IsNullOrEmpty(definition.BumpTextureName))
        {
            return;
        }

        // The authored response is re-applied here and handed to the enhancer,
        // which bands its derived detail around it. ApplySpecular set the same
        // values earlier, but binding a mask map makes HDRP ignore those
        // scalars entirely - so without a remap the derived map silently owns
        // the whole material, which is how a hangar became chrome.
        float authoredSmoothness = BFMaterialInterpreter.ApplyAuthoredResponse(material, definition);
        BFMaterialEnhancer.Enhance(material, source, authoredSmoothness);
    }

    UMaterial LibMaterialToUnity(LibMaterial mat, string overrideTexture=null, bool unlit=false)
    {
        BFMaterialDefinition definition = PreserveDefinition(mat);
        string texName = definition.BaseTextureName;
        EMaterialFlags matFlags = definition.Flags;

        if (!string.IsNullOrEmpty(overrideTexture))
        {
            texName = overrideTexture;
        }

        if (texName == "")
        {
            return new UMaterial(GetDefaultMaterial());
        } 
        else 
        {
            string materialName = DefinitionKey(mat);

            if (!materialDataBase.ContainsKey(materialName))
            {
                UMaterial material = null;

                if (UseHDRP)
                {
                    Texture2D importedTex = TextureLoader.Instance.ImportTexture(texName);

                    // glowing materials use the textures alpha channel as glow map
                    // glowing materials can therefore NOT be transparent
                    if (matFlags.HasFlag(EMaterialFlags.Glow) && !matFlags.HasFlag(EMaterialFlags.Transparent))
                    {
                        material = new UMaterial(unlit ? GetDefaultUnlitMaterial() : GetDefaultMaterial());
                        material.EnableKeyword("_EMISSIVE_MAPPING_BASE");
                        material.EnableKeyword("_EMISSIVE_COLOR_MAP");
                        Texture2D glowMap = AlphaToGrayscale(texName);
                        if (glowMap != null)
                        {
                            material.SetTexture("_EmissiveColorMap", glowMap);
                        }
                        // Emissive strength, and how much exposure affects it.
                        //
                        // These were 30x and a weight of 0, meaning "thirty
                        // times white, and ignore exposure entirely". In an
                        // auto-exposed interior that is roughly five stops
                        // above white however dark the room gets, so every
                        // screen, strip light and console became a solid white
                        // blob smeared by bloom - which is most of what a
                        // Coruscant interior is made of.
                        //
                        // A weight above zero lets the highlight sit inside the
                        // exposed range instead of permanently outside it. Not
                        // 1: a light source SHOULD stay bright when the eye
                        // adapts to a bright scene, just not unboundedly.
                        material.SetColor("_EmissiveColor", Color.white * 6.0f);
                        material.SetFloat("_EmissiveExposureWeight", 0.55f);
                    }
                    else if (matFlags.HasFlag(EMaterialFlags.Transparent))
                    {
                        material = new UMaterial(GetDefaultTransparentMaterial());

                        // A material can carry Glow AND Transparent together -
                        // force fields, shield bubbles and holograms all do.
                        // The branch above deliberately excludes those (a glow
                        // map lives in the alpha channel, which transparency
                        // also wants), but dropping the glow entirely left them
                        // as near-invisible sheets of glass. Drive the emissive
                        // from the colour map instead of the alpha so both
                        // effects survive.
                        if (matFlags.HasFlag(EMaterialFlags.Glow) && importedTex != null)
                        {
                            material.EnableKeyword("_EMISSIVE_MAPPING_BASE");
                            material.EnableKeyword("_EMISSIVE_COLOR_MAP");
                            material.SetTexture("_EmissiveColorMap", importedTex);
                            material.SetColor("_EmissiveColor", Color.white * 6.0f);
                            material.SetFloat("_EmissiveExposureWeight", 0.55f);
                        }

                        //material.SetFloat("_AlphaCutoffEnable", 1.0f);
                        //material.SetFloat("_SurfaceType", 1.0f);
                        //material.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                        //material.EnableKeyword("_ENABLE_FOG_ON_TRANSPARENT");
                        //material.EnableKeyword("ENABLE_ALPHA");
                        //material.EnableKeyword("_DISABLE_SSR_TRANSPARENT");
                        //material.EnableKeyword("_ALPHATEST_ON");
                        //material.EnableKeyword("_ALPHATOMASK_ON");
                        //material.SetFloat("_TransparentZWrite", 1.0f);
                        //material.SetFloat("_EnableBlendModePreserveSpecularLighting", 0.0f);
                        //material.SetFloat("_AlphaDstBlend", 10.0f);
                        //material.SetFloat("_SrcBlend", 1.0f);
                        //material.SetFloat("_DstBlend", 10.0f);
                        //material.SetFloat("_ZWrite", 0.0f);
                        //material.SetFloat("_StencilRefDepth", 0.0f);
                        //material.SetFloat("_StencilRefGBuffer", 22.0f);
                        //material.SetFloat("_StencilRefMV", 32.0f);
                        //material.SetFloat("_ZTestDepthEqualForOpaque", 4.0f);
                        //material.SetOverrideTag("RenderType", "Transparent");
                        //material.renderQueue = 3000;
                    }
                    else
                    {
                        material = new UMaterial(unlit ? GetDefaultUnlitMaterial() : GetDefaultMaterial());
                    }

                    if (matFlags.HasFlag(EMaterialFlags.Doublesided))
                    {
                        // _DoubleSidedEnable on its own only tells the HDRP
                        // material inspector what to draw; the shader still
                        // culls backfaces until the keyword and cull modes are
                        // set too. Single-sided force fields, foliage cards and
                        // banners were therefore invisible from one side.
                        material.SetFloat("_DoubleSidedEnable", 1.0f);
                        material.EnableKeyword("_DOUBLESIDED_ON");
                        material.SetFloat("_CullMode", (float)UnityEngine.Rendering.CullMode.Off);
                        material.SetFloat("_CullModeForward", (float)UnityEngine.Rendering.CullMode.Off);
                        // Mirror matches the original renderer, which lit both
                        // faces from the same normal rather than flipping it.
                        material.SetFloat("_DoubleSidedNormalMode", 1.0f);
                    }

                    ApplySpecular(material, mat, matFlags);
                    ApplyBumpMap(material, mat, matFlags);

                    if (importedTex != null)
                    {
                        //material.EnableKeyword("_DISABLE_SSR_TRANSPARENT");
                        //material.EnableKeyword("_NORMALMAP_TANGENT_SPACE");
                        material.mainTexture = importedTex;

                        EnhanceFromStockTexture(material, importedTex, definition, matFlags);
                    }
                }
                else
                {
                    material = new UMaterial(GetDefaultMaterial());

#if !LVLIMPORT_NO_EDITOR
                    if (SaveAssets)
                    {
                        AssetDatabase.CreateAsset(material, Path.Combine(SaveDirectory, materialName + ".mat"));
                    }
#endif
                    material.SetFloat("_Glossiness", 0.0f);

                    if (matFlags.HasFlag(EMaterialFlags.Hardedged))
                    {
                        SetRenderMode(ref material, 1);
                    }
                    else if (matFlags.HasFlag(EMaterialFlags.Transparent))
                    {
                        SetRenderMode(ref material, 2);
                    }

                    if (matFlags.HasFlag(EMaterialFlags.Doublesided))
                    {
                        material.SetInt("_Cull",(int) UnityEngine.Rendering.CullMode.Off);
                    }

                    Texture2D importedTex = TextureLoader.Instance.ImportTexture(texName);
                    if (importedTex != null)
                    {
                        material.mainTexture = importedTex;
                    }

                    if (matFlags.HasFlag(EMaterialFlags.Glow))
                    {
                        material.EnableKeyword("_EMISSION");
                        material.SetTexture("_EmissionMap", importedTex);
                        material.SetColor("_EmissionColor", Color.white);
                    }

                    if (matFlags.HasFlag(EMaterialFlags.Scrolling))
                    {
                        // UV scrolling depends on UV direction it seems.  Will need to implement
                        // that in shader
                        material.SetFloat("_ScrollSpeedU", ((float) mat.Param1) / 100f);
                        material.SetFloat("_ScrollSpeedV", ((float) mat.Param2) / 100f);
                    }
                }

                material.name = materialName;

                // GPU instancing on every imported material.
                //
                // A SWBF2 model is split into one renderer per bone/segment,
                // and a map places hundreds of copies of the same few props -
                // so the same material is drawn thousands of times per frame
                // with nothing but a transform differing. That is exactly what
                // instancing exists for, and it was off, so each of those was
                // its own draw call.
                //
                // Materials are already shared through this cache, which is
                // the precondition instancing needs; without the cache this
                // flag would do nothing.
                material.enableInstancing = true;

                // Retain a stable source identity on the generated object for
                // diagnostics and material retranslation.  This is not used as
                // a rendering switch and cannot be altered by HDRP internals.
                material.SetOverrideTag("SWBF2_SourceMaterial", definition.SourceName);
                material.SetOverrideTag("SWBF2_AttachedLight", definition.AttachedLight);
                materialDataBase[materialName] = material;
            }

#if !LVLIMPORT_NO_EDITOR
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
#endif
            return materialDataBase[materialName];
        }

    }





    public UMaterial LoadMeshEffectMaterial(LibMaterial mat, string overrideTexture=null)
    {
        UMaterial LoadedMat = LibMaterialToUnity(mat, overrideTexture, true);
        if (!UseHDRP)
        {
            LoadedMat.EnableKeyword("_USE_VERTEX_COLORS");
        }        
        return LoadedMat;
    }



    public UMaterial LoadMaterial(LibMaterial mat, string overrideTexture, bool unlit = false)
    {
        UMaterial LoadedMat = LibMaterialToUnity(mat, overrideTexture, unlit);
        if (!UseHDRP)
        {
            LoadedMat.DisableKeyword("_USE_VERTEX_COLORS");
        }
        return LoadedMat;
    }


    /*From https://answers.unity.com/questions/1004666/change-material-rendering-mode-in-runtime.html */
    static void SetRenderMode(ref UMaterial standardShaderMaterial, int blendMode)
    {
        switch (blendMode)
        {
            case 0: //opaque
                standardShaderMaterial.SetFloat("_Mode",(float) blendMode);
                standardShaderMaterial.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.One);
                standardShaderMaterial.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.Zero);
                standardShaderMaterial.SetInt("_ZWrite", 1);
                standardShaderMaterial.DisableKeyword("_ALPHATEST_ON");
                standardShaderMaterial.DisableKeyword("_ALPHABLEND_ON");
                standardShaderMaterial.DisableKeyword("_ALPHAPREMULTIPLY_ON");
                standardShaderMaterial.renderQueue = -1;
                break;
            case 1: //cutout
                standardShaderMaterial.SetFloat("_Mode",(float) blendMode);
                standardShaderMaterial.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.One);
                standardShaderMaterial.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.Zero);
                standardShaderMaterial.SetInt("_ZWrite", 1);
                standardShaderMaterial.EnableKeyword("_ALPHATEST_ON");
                standardShaderMaterial.DisableKeyword("_ALPHABLEND_ON");
                standardShaderMaterial.DisableKeyword("_ALPHAPREMULTIPLY_ON");
                standardShaderMaterial.renderQueue = 2450;
                break;
            case 2: //fade
                standardShaderMaterial.SetFloat("_Mode",(float) blendMode);
                standardShaderMaterial.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                standardShaderMaterial.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                standardShaderMaterial.SetInt("_ZWrite", 0);
                standardShaderMaterial.DisableKeyword("_ALPHATEST_ON");
                standardShaderMaterial.EnableKeyword("_ALPHABLEND_ON");
                standardShaderMaterial.DisableKeyword("_ALPHAPREMULTIPLY_ON");
                standardShaderMaterial.renderQueue = 3000;
                break;
            case 3: //transparent
                standardShaderMaterial.SetFloat("_Mode",(float) blendMode);
                standardShaderMaterial.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.One);
                standardShaderMaterial.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                standardShaderMaterial.SetInt("_ZWrite", 0);
                standardShaderMaterial.DisableKeyword("_ALPHATEST_ON");
                standardShaderMaterial.DisableKeyword("_ALPHABLEND_ON");
                standardShaderMaterial.EnableKeyword("_ALPHAPREMULTIPLY_ON");
                standardShaderMaterial.renderQueue = 3000;
                break;
        }
    }

    /// <summary>
    /// Builds a glow map from a diffuse texture's alpha channel.
    ///
    /// Takes the texture NAME, not the imported Texture2D: imported textures
    /// are DXT-compressed with their CPU copy released, so they can no longer
    /// be read back as RGBA32. The source bytes come from the .lvl instead,
    /// which is where the alpha was authored anyway.
    ///
    /// Returns null when the texture is missing or unreadable; the caller
    /// leaves the material without an emissive map rather than failing the
    /// whole model import.
    /// </summary>
    static Texture2D AlphaToGrayscale(string texName)
    {
        if (!TextureLoader.Instance.TryGetPixelData(texName, out byte[] src, out int width, out int height))
        {
            return null;
        }

        Texture2D grayscale = new Texture2D(width, height, UnityEngine.TextureFormat.RGB24, false);
        int numPixels = width * height;
        byte[] dst = new byte[numPixels * 3];
        for (int i = 0; i < numPixels; ++i)
        {
            byte alpha = src[(i * 4) + 3];
            dst[(i * 3) + 0] = alpha;
            dst[(i * 3) + 1] = alpha;
            dst[(i * 3) + 2] = alpha;
        }
        grayscale.LoadRawTextureData(dst);
        grayscale.Apply();
        return grayscale;
    }



    // This is for special objects needing tempfixes.  Right now,
    // we don't have skydomes properly lit by their suns, so we just add 
    // emission to compensate
    public void PatchMaterial(GameObject obj, string patchType="")
    {
        List<Transform> transforms = UnityUtils.GetChildTransforms(obj.transform);
        transforms.Add(obj.transform);

        foreach (Transform tx in transforms)
        {
            var renderer = tx.gameObject.GetComponent<MeshRenderer>();

            if (renderer != null)
            {
                foreach (UMaterial mat in renderer.sharedMaterials)
                {
                    if (patchType.Equals("skydome"))
                    {
                        mat.EnableKeyword("_EMISSION");
                        mat.SetTexture("_EmissionMap", mat.GetTexture("_MainTex"));
                        mat.SetColor("_EmissionColor", Color.white);
                    }
                }
            }
        }
    }
}

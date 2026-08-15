using System;
using System.IO;
using System.Collections.Generic;

using UnityEngine;
#if !LVLIMPORT_NO_EDITOR
using UnityEditor;
#endif


public class TextureLoader : Loader
{

    public static TextureLoader Instance { get; private set; } = null;


    private Dictionary<string, Texture2D> texDataBase = new Dictionary<string, Texture2D>();


    static TextureLoader()
    {
        Instance = new TextureLoader();
    }


    public void ResetDB()
    {
        // Only drop the cache references - never Object.Destroy() the textures
        // here. This runs twice per map load (PhxEnvironment.Run and
        // PhxScene.Import), and destroying meant killing textures created
        // between those two points that are still live: the loadscreen image
        // being displayed at that moment is the reproducible case. Anything
        // genuinely unreferenced is reclaimed by the
        // Resources.UnloadUnusedAssets() in PhxScene.Destroy(), which is the
        // safe way to do this - it collects only what nothing points at.
        texDataBase.Clear();
    }

    /// <summary>
    /// Re-compress imported world textures to DXT on upload. Turn off to
    /// compare quality, or if a platform's compressor misbehaves.
    /// </summary>
    public static bool CompressWorldTextures = false;

    /// <summary>
    /// DXT works on 4x4 blocks, so anything not a multiple of 4 in both
    /// dimensions can't be block-compressed - Unity would silently decompress
    /// it again, costing time for nothing.
    /// </summary>
    static bool IsCompressible(int width, int height)
    {
        return width >= 4 && height >= 4 && (width % 4) == 0 && (height % 4) == 0;
    }

    public Texture2D ImportUITexture(string name, bool printError=true) => ImportTexture(name, true, printError);

    /// <summary>
    /// Raw RGBA bytes straight out of the .lvl, bypassing the imported
    /// Texture2D entirely.
    ///
    /// Anything that needs to read pixels on the CPU - glow maps take their
    /// mask from the diffuse alpha channel - must come through here rather
    /// than through <see cref="ImportTexture"/>. Imported textures are
    /// DXT-compressed and have their CPU copy freed on upload, so
    /// GetRawTextureData no longer hands back four bytes per pixel and
    /// indexing it as RGBA32 runs off the end of the array.
    /// </summary>
    public bool TryGetPixelData(string name, out byte[] rgba, out int width, out int height)
    {
        rgba = null;
        width = 0;
        height = 0;

        var tex = container.Get<LibSWBF2.Wrappers.Texture>(name);
        if (tex == null || !tex.IsConvertibleFormat || tex.Width <= 0 || tex.Height <= 0)
        {
            return false;
        }

        rgba = tex.GetBytesRGBA();
        width = tex.Width;
        height = tex.Height;
        return rgba != null && rgba.Length >= width * height * 4;
    }

    /// <param name="linear">
    /// Import as linear rather than sRGB. Required for data textures - a normal
    /// map read through the sRGB curve yields wrong normals, which shows up as
    /// lighting that is subtly but consistently off.
    /// </param>
    public Texture2D ImportTexture(string name, bool mirror=false, bool printError=true, bool linear=false)
    {
        // Colour space is baked into the created texture, so a name imported
        // once as sRGB must not be handed back for a linear request.
        string cacheKey = linear ? name + " linear" : name;
        if (texDataBase.ContainsKey(cacheKey))
        {
            return texDataBase[cacheKey];
        }

        var tex = container.Get<LibSWBF2.Wrappers.Texture>(name);

        // A format the wrapper can't convert yields garbage from GetBytesRGBA
        // rather than an error, so ask first instead of importing noise.
        if (tex != null && !tex.IsConvertibleFormat)
        {
            if (printError)
            {
                Debug.LogWarning($"Texture '{name}' is in a format LibSWBF2 cannot convert - skipped.");
            }

            // Distinguished from "not found" in the report: this one IS in the
            // mounted data and the mount is fine, so the fix is in the native
            // converter rather than in the data paths. Reporting both the same
            // way sends anyone reading the report looking for a missing lvl
            // that is not missing.
            BFImportDiagnostics.Missing(BFSourceKind.Texture, name,
                                        "present but in a format LibSWBF2 cannot convert");
            return null;
        }

        if (tex != null && tex.Height * tex.Width > 0)
        {
            // Mips matter: without them nothing in the pipeline can filter at
            // distance - every surface shimmers, and the global anisotropic /
            // mip-bias settings are no-ops. UI textures stay mipless (they are
            // drawn 1:1 and the chain would only cost memory).
            bool wantMips = !mirror;
            Texture2D newTexture = new Texture2D(tex.Width, tex.Height, TextureFormat.RGBA32, wantMips, linear);
            newTexture.name = tex.Name;

            byte[] data = tex.GetBytesRGBA();

            // Validate the decoded buffer against the dimensions the chunk
            // declares, before uploading it.
            //
            // The native decoder reports failures like "Image data of 131072
            // bytes matches no mip level of a 32x32 texture" and then returns
            // a buffer anyway - one that does not match the header. Uploading
            // it produced either garbage pixels or a silent partial write, and
            // in neither case did anything say which texture was wrong. A
            // header/payload disagreement is a decode failure and is now
            // treated as one, with both numbers recorded so the offending
            // asset is identifiable rather than merely suspected.
            int expected = tex.Width * tex.Height * 4;
            if (data == null || data.Length < expected)
            {
                if (printError)
                {
                    Debug.LogWarning($"Texture '{name}' declares {tex.Width}x{tex.Height} " +
                                     $"({expected} bytes) but decoded to " +
                                     $"{(data == null ? 0 : data.Length)} - skipped.");
                }
                BFImportDiagnostics.Missing(BFSourceKind.Texture, name,
                    $"header says {tex.Width}x{tex.Height} ({expected}B), decoder returned " +
                    $"{(data == null ? 0 : data.Length)}B");
                return null;
            }

            data = mirror ? MirrorVertically(data, tex.Width, tex.Height, 4) : data;

            // Derive the modern PBR maps here, from these bytes.
            //
            // This is the only point at which the pixels exist on the CPU: a
            // few lines below, the texture is block-compressed and applied with
            // makeNoLongerReadable, after which GetPixels32 throws. Deriving
            // from the finished Texture2D therefore fails for every world
            // texture in the game, silently, because the failure is a caught
            // exception that legitimately happens for other reasons too.
            //
            // Skipped for `mirror`, which is how UI textures come through -
            // an interface element gains nothing from surface relief.
            if (!mirror)
            {
                BFMaterialEnhancer.Prepare(tex.Name, data, tex.Width, tex.Height);
            }

            newTexture.SetPixelData(data, 0);
            newTexture.Apply(wantMips);
            newTexture.filterMode = FilterMode.Trilinear;
            newTexture.anisoLevel = 16;

            // Sample slightly sharper than 1:1. With TAA resolving the extra
            // aliasing this is the standard way to make minified textures read
            // as detailed, and it is what actually makes the upscaled ones look
            // upscaled.
            //
            // A global "_GlobalMipBias" was being set for this instead, which
            // is an HDRP 12+ internal and does nothing in 10.7. Per texture is
            // the equivalent that exists here.
            if (wantMips)
            {
                newTexture.mipMapBias = -0.5f;
            }

            // Re-compress world textures on the GPU.
            //
            // The source is DXT in the .lvl, but GetBytesRGBA hands back
            // uncompressed RGBA - so every texture was living in VRAM at
            // roughly 4-8x its shipped size. That is a real budget at 64v64 and
            // it fights the 4K upscale path for the same memory.
            //
            // UI textures are left uncompressed: they are drawn at 1:1 where
            // block artefacts are obvious, and there are few enough of them
            // that the memory doesn't matter.
            if (CompressWorldTextures && !mirror && IsCompressible(tex.Width, tex.Height))
            {
                // High quality: this runs once per texture at load, not per
                // frame, and the fast path visibly banded gradients on skies.
                newTexture.Compress(true);
                newTexture.Apply(wantMips, true);   // upload, then free the CPU copy
            }

#if !LVLIMPORT_NO_EDITOR
            string texPath = SaveDirectory + "/" + name + ".png";
            if (SaveAssets)
            {
                File.WriteAllBytes(texPath, newTexture.EncodeToPNG());
                // TODO: figure out how to save texture assets in an AssetEditing block without
                // lost refs after save/light bake...
                AssetDatabase.ImportAsset(texPath, ImportAssetOptions.Default);
                newTexture = (Texture2D) AssetDatabase.LoadAssetAtPath(texPath, typeof(Texture2D));
            }
#endif
            texDataBase[cacheKey] = newTexture;
            BFImportDiagnostics.Resolved(BFSourceKind.Texture, name);
            return newTexture;
        }
        // "noIcon" is BF2's sentinel for a class that deliberately has no icon,
        // not a missing asset, so warning about it is pure noise. Everything
        // else that fails here is genuinely absent from the loaded lvls -
        // usually mod content whose data files were never shipped.
        else if (!string.Equals(name, "noIcon", StringComparison.OrdinalIgnoreCase))
        {
            if (printError)
            {
                Debug.LogWarning($"Texture '{name}' failed to load!");
            }
            // Counted even when the caller asked for silence: printError only
            // says whether this particular lookup should be noisy, not whether
            // the texture is present. The import report is where the total
            // belongs.
            //
            // Two different failures reach here and they need different fixes,
            // so they are reported differently: a texture the container has
            // never heard of is a mounting problem, and one it has but which
            // has no usable pixels is a parsing problem in the native library.
            BFImportDiagnostics.Missing(BFSourceKind.Texture, name,
                tex == null
                    ? "not in any mounted level - check the addon/game data paths"
                    : $"in the data but unreadable ({tex.Width}x{tex.Height}) - " +
                      "LibSWBF2 could not decode it");
        }

        return null;
    }

    public Texture2DArray ImportTextures(string[] names, out float[] xDims, bool mirror = false)
    {
        int maxWidth = 0;
        int maxHeight = 0;
        xDims = new float[names.Length];

        LibSWBF2.Wrappers.Texture[] libTextures = new LibSWBF2.Wrappers.Texture[names.Length];
        for (int i = 0; i < names.Length; ++i)
        {
            libTextures[i] = container.Get<LibSWBF2.Wrappers.Texture>(names[i]);
            if (libTextures[i] != null)
            {
                maxWidth = Mathf.Max(maxWidth, libTextures[i].Width);
                maxHeight = Mathf.Max(maxHeight, libTextures[i].Height);
            }
            else
            {
                Debug.LogWarning($"Cannot find texture '{names[i]}'!");
            }
        }

        byte[] buffer = new byte[maxWidth * maxHeight * 4];

        Texture2DArray textures = new Texture2DArray(maxWidth, maxHeight, names.Length, TextureFormat.RGBA32, true);
        textures.filterMode = FilterMode.Trilinear;
        textures.anisoLevel = 16;
        for (int i = 0; i < libTextures.Length; ++i)
        {
            var tex = libTextures[i];
            if (tex == null)
            {
                continue;
            }

            // The same two guards ImportTexture has had all along, and this
            // path never did - despite being the one that builds terrain
            // layers.
            //
            // A format LibSWBF2 cannot decode, or a header whose dimensions
            // disagree with the payload, means GetBytesRGBA returns a buffer
            // shorter than the arithmetic below assumes. That is either an
            // exception out of Array.Copy, which aborts terrain import
            // entirely, or a partial row leaving the rest of the slice holding
            // the previous layer's pixels - the exact failure the comment
            // further down describes fixing once already.
            //
            // Skipping leaves the slice zeroed, which reads as a missing layer
            // rather than as some other layer's texture. That is the right way
            // for this to fail.
            if (!tex.IsConvertibleFormat)
            {
                Debug.LogWarning($"Terrain layer '{names[i]}' is in an unsupported format; " +
                                 "the layer will be blank.");
                BFImportDiagnostics.Missing(BFSourceKind.Texture, names[i], "terrain layer array");
                continue;
            }

            byte[] decoded = tex.GetBytesRGBA();
            int expected = tex.Width * tex.Height * 4;
            if (decoded == null || decoded.Length < expected)
            {
                Debug.LogWarning($"Terrain layer '{names[i]}' decoded to " +
                                 $"{(decoded == null ? 0 : decoded.Length)} bytes, expected " +
                                 $"{expected}; the layer will be blank.");
                BFImportDiagnostics.Missing(BFSourceKind.Texture, names[i], "terrain layer array");
                continue;
            }

            // Nothing below writes every byte on every path, so start each
            // layer from a clean buffer rather than from the last one's.
            Array.Clear(buffer, 0, buffer.Length);

            xDims[i] = tex.Width;

            if (tex.Width < textures.width || tex.Height < textures.height)
            {
                byte[] data = decoded;

                for (int row = 0; row < maxHeight; ++row)
                {
                    int dstWidth = textures.width * 4;
                    int dstStartIdx = row * textures.width * 4;
                    if (row < tex.Height)
                    {
                        int srcWidth = tex.Width * 4;
                        int srcStartIdx = row * tex.Width * 4;

                        Array.Copy(data, srcStartIdx, buffer, dstStartIdx, srcWidth);

                        // Clear from the end of the copied row, not from
                        // dstWidth - srcWidth.
                        //
                        // Those two are the same expression only when the
                        // source is exactly half the array's width, and
                        // wrong in both directions otherwise. A layer wider
                        // than half had the tail of its real pixels zeroed;
                        // a layer narrower than half left the gap between
                        // srcWidth and dstWidth - srcWidth untouched, still
                        // holding the previous layer's pixels - which is a
                        // terrain layer showing a different layer's texture,
                        // and on Hoth that is sand where the snow should be.
                        for (int x = srcWidth; x < dstWidth; ++x)
                        {
                            buffer[dstStartIdx + x] = 0;
                        }
                    }
                    else
                    {
                        for (int x = 0; x < dstWidth; ++x)
                        {
                            // fill remaining rows with 0
                            buffer[dstStartIdx + x] = 0;
                        }
                    }
                }
            }
            else
            {
                // Copy rather than rebind. Assigning the source array to
                // `buffer` made every later layer write into whatever array
                // this call happened to return, so stale pixels from one
                // layer could survive into the next.
                Array.Copy(decoded, buffer, Math.Min(decoded.Length, buffer.Length));
            }

            // Mirror using the ARRAY's dimensions, not the source texture's.
            // By this point a smaller texture has been padded into a
            // maxWidth x maxHeight buffer, so mirroring by the source's
            // height/stride walked the wrong rows and scrambled the result.
            buffer = mirror ? MirrorVertically(buffer, textures.width, textures.height, 4) : buffer;
            textures.SetPixelData(buffer, 0, i);
        }
        textures.Apply(true);
        return textures;
    }


    static byte[] MirrorVertically(byte[] data, int width, int height, int stride)
    {
        int byteWidth = width * stride;
        byte[] mirrored = new byte[data.Length];
        for (int rowIdx = 0; rowIdx < height; ++rowIdx)
        {
            int rowReverseIdx = height - rowIdx - 1;
            Array.Copy(data, rowReverseIdx * byteWidth, mirrored, rowIdx * byteWidth, byteWidth);
        }
        return mirrored;
    }
}

#ifndef BLENDTERRAINLAYERS_INCLUDED
#define BLENDTERRAINLAYERS_INCLUDED

/*
* Presentation-layer terrain response.
*
* Deformation, snow and wetness are published as GLOBAL shader values by
* BFTerrainInteractionSystem, BFSnowAccumulation and BFWetnessSystem
* (Shader.SetGlobalTexture / SetGlobalFloat). They are read here rather than
* wired through the Shader Graph on purpose: this custom function already
* receives world position, which is the only input any of them need, and
* adding graph inputs means editing the serialized .shadergraph - the exact
* thing that silently broke this node once before by leaving its HLSL
* referenced by a GUID that no longer resolved.
*
* Every term below is gated on a float that is zero when its system is not
* running, so a build with the presentation layer disabled samples nothing and
* renders precisely what it always did.
*
* WHAT THIS CANNOT DO from inside a custom function whose only output is a
* float3 colour: displace geometry, or write smoothness/normal. Footprints are
* therefore shading, not depth, and wet ground darkens without becoming
* glossier. Both of those need real graph outputs and are the remaining half
* of the job.
*/

TEXTURE2D(_BFTerrainDeformation);
SAMPLER(sampler_BFTerrainDeformation);

float  _BFTerrainDeformationEnabled;
float  _BFTerrainDeformationExtent;
float4 _BFTerrainDeformationCentre;

float  _BFSnowCoverage;
float  _BFSnowMaxSlopeCos;
float  _BFSnowLine;
float4 _BFSnowTint;

float  _BFWetness;
float  _BFPuddleDepth;

/*
* Geometric normal from screen-space derivatives of world position.
*
* The custom function is not given the surface normal and cannot be without a
* graph edit, but in a fragment shader the derivatives of world position across
* the pixel quad describe the surface plane directly. This is the face normal
* rather than the shaded one - no normal map, faceted across a triangle - which
* is exactly right here: snow settles on the ground's actual slope, not on the
* detail relief painted onto it.
*/
float3 BFTerrainGeometricNormal(float3 worldPos)
{
    float3 dpdx = ddx(worldPos);
    float3 dpdy = ddy(worldPos);
    float3 n = cross(dpdy, dpdx);

    float len = length(n);
    return len > 1e-6 ? n / len : float3(0.0, 1.0, 0.0);
}

/*
* Texture2DArray can only store textures of same size.
* But the layer textures differ in size, so there will be "smaller" textures inside the array,
* where the remaining pixels are filled with black (see TextureLoader.ImportTextures  <--  note the "S" at the end).
* 
* One workaround is to scale the UV of the affected
* 
* Because Unity does neither support passing float arrays, nor matrix4x4's (possible workaround for a float array length of 16) as parameters,
* we have to use the smallest available float4 parameter multiple times...
*/

/*
* Snow, wetness and deformation applied to the blended layer colour.
*
* Order matters and follows what physically happens to ground: snow settles on
* top of whatever is there, water soaks into what is exposed, and traffic
* disturbs the result. So snow is composited last but deformation is applied to
* it, because a footprint in fresh snow shows the compacted snow underneath -
* not the rock beneath the snow.
*/
float3 BFApplyTerrainResponse(float3 albedo, float3 worldPos)
{
    // ------------------------------------------------------------- wetness
    //
    // Wet ground is darker before it is shinier: a water film fills the
    // surface's pores and traps light that would otherwise scatter back out.
    //
    // Deliberately understated. The full darkening is only correct alongside
    // the rise in smoothness that pays it back as specular, and smoothness
    // needs a graph output this function does not have. Applying the physical
    // darkening without the physical highlight is just a dimmer map - which is
    // exactly the mistake the first version of these lighting profiles made
    // elsewhere. Restore this toward 0.62 when smoothness can be driven too.
    if (_BFWetness > 0.001)
    {
        albedo *= lerp(1.0, 0.86, saturate(_BFWetness));
    }

    // ------------------------------------------------------- deformation
    //
    // R depression, G displaced rim, B compaction. A depression is darker
    // (it is in its own shadow and the material there is packed); a rim is
    // brighter (loose material catching light at a new angle).
    float3 deform = 0.0;
    if (_BFTerrainDeformationEnabled > 0.5 && _BFTerrainDeformationExtent > 0.0)
    {
        float2 duv = (worldPos.xz - _BFTerrainDeformationCentre.xz) / _BFTerrainDeformationExtent + 0.5;

        // Outside the mask entirely - a map larger than its own terrain, or a
        // position off the edge - contributes nothing rather than clamping to
        // whatever the border texel happens to hold.
        if (all(duv > 0.0) && all(duv < 1.0))
        {
            deform = SAMPLE_TEXTURE2D(_BFTerrainDeformation, sampler_BFTerrainDeformation, duv).rgb;
        }
    }

    // ------------------------------------------------------------- snow
    //
    // Settles on upward faces, thins on slopes, and never on surfaces the map
    // profile says cannot hold it (coverage arrives already gated by that).
    float snow = 0.0;
    if (_BFSnowCoverage > 0.001)
    {
        float3 n = BFTerrainGeometricNormal(worldPos);

        // Remap dot(n, up) so the slope limit is where coverage reaches zero,
        // rather than fading linearly from vertical and leaving grey slush on
        // cliff faces.
        float slope = saturate((n.y - _BFSnowMaxSlopeCos) / max(1e-3, 1.0 - _BFSnowMaxSlopeCos));

        // A snow line only applies where the profile set one; the sentinel is
        // a large negative height meaning "everywhere".
        float altitude = _BFSnowLine > -9999.0
            ? saturate((worldPos.y - _BFSnowLine) / 50.0)
            : 1.0;

        snow = saturate(_BFSnowCoverage * slope * altitude);

        // Traffic clears it. Deep prints go through to the ground beneath,
        // which is what makes a churned path across a snowfield read as a path
        // rather than as a stain.
        snow *= saturate(1.0 - deform.r * 2.5);

        albedo = lerp(albedo, _BFSnowTint.rgb, snow);
    }

    // Deformation shading, applied after snow so a print in snow shows packed
    // snow rather than the rock under it.
    if (_BFTerrainDeformationEnabled > 0.5)
    {
        albedo *= lerp(1.0, 0.72, saturate(deform.r));   // depressed and packed
        albedo *= lerp(1.0, 1.18, saturate(deform.g));   // displaced rim
        albedo *= lerp(1.0, 0.88, saturate(deform.b));   // compaction
    }

    // Standing water pools in the depressions traffic just made, which is why
    // this is keyed off the deformation mask rather than applied flat.
    if (_BFPuddleDepth > 0.001)
    {
        albedo *= lerp(1.0, 0.55, saturate(_BFPuddleDepth * deform.r * 2.0));
    }

    return albedo;
}

void BlendTerrainLayers_float(
    UnitySamplerState ss,
    UnityTexture2DArray layers,
    float numLayers,
    float4 layerTexDims0,
    float4 layerTexDims1,
    float4 layerTexDims2,
    float4 layerTexDims3,
    UnityTexture2D blend0,
    UnityTexture2D blend1,
    UnityTexture2D blend2,
    UnityTexture2D blend3,
    float bound,
    float3 worldPos,
    out float3 output)
{
    float2 modulo = float2(24.0, 24.0);

    float2 gridLoc = abs(worldPos.xz);
    float2 uvTex = (gridLoc % modulo) / modulo;
    float2 bounds = float2(bound / 2.0, bound / 2.0);

    output = float3(0.0, 0.0, 0.0);
    int n = (int)numLayers;

    float4x4 layerTexDims;
    layerTexDims[0] = layerTexDims0;
    layerTexDims[1] = layerTexDims1;
    layerTexDims[2] = layerTexDims2;
    layerTexDims[3] = layerTexDims3;

    // crashes
    //UnityTexture2D blends[4];
    //blends[0] = blend0;
    //blends[1] = blend1;
    //blends[2] = blend2;
    //blends[3] = blend3;

    for (int i = 0; i < n; ++i)
    {
        float uvScale = layerTexDims[i / 4][i % 4];

        float3 layerCol = layerCol = SAMPLE_TEXTURE2D_ARRAY(layers, ss, uvTex * uvScale, i).rgb;

        //int blendIdx = i/ 4;
        float4 blend = float4(0, 0, 0, 0);
        
        if (i < 4)
        {
            blend = SAMPLE_TEXTURE2D(blend0, ss, (worldPos.xz + bounds) / (2.0 * bounds));
        }
        else if (i < 8)
        {
            blend = SAMPLE_TEXTURE2D(blend1, ss, (worldPos.xz + bounds) / (2.0 * bounds));
        }
        else if (i < 12)
        {
            blend = SAMPLE_TEXTURE2D(blend2, ss, (worldPos.xz + bounds) / (2.0 * bounds));
        }
        else if (i < 16)
        {
            blend = SAMPLE_TEXTURE2D(blend3, ss, (worldPos.xz + bounds) / (2.0 * bounds));
        }

        output += layerCol * blend[i % 4];
    }

    output = BFApplyTerrainResponse(output, worldPos);
}

#endif // BLENDTERRAINLAYERS_INCLUDED
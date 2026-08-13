#include "pch.h"
#include "BODY.h"
#include "FMT_.h"
#include "Logging/Logger.h"
#include "DirectX/DXHelpers.h"
#include "DirectX/DXTexCrossPlat.h"
#include "InternalHelpers.h"
#include "FileReader.h"
#include <algorithm>
#include <cstring>


namespace LibSWBF2::Chunks::LVL::LVL_texture
{
    using LVL::texture::FMT_;

    void BODY::RefreshSize()
    {
        THROW("Not implemented!");
    }

    void BODY::WriteToStream(FileWriter& stream)
    {
        THROW("Not implemented!");
    }

    // Bytes one surface of the given format occupies at the given dimensions.
    // Block compressed formats round up to whole 4x4 blocks; everything else
    // is a straight bytes-per-pixel product. Returns 0 for formats we cannot
    // size, which callers must treat as "unknown" rather than "empty".
    static size_t ExpectedSurfaceSize(D3DFORMAT format, size_t width, size_t height)
    {
        const size_t blocksW = (width  + 3) / 4;
        const size_t blocksH = (height + 3) / 4;

        switch (format)
        {
            case D3DFMT_DXT1:
                return blocksW * blocksH * 8;

            case D3DFMT_DXT2:
            case D3DFMT_DXT3:
            case D3DFMT_DXT4:
            case D3DFMT_DXT5:
                return blocksW * blocksH * 16;

            // 1 byte per pixel
            case D3DFMT_L8:
            case D3DFMT_A4L4:
            case D3DFMT_A8:
            case D3DFMT_P8:
            case D3DFMT_R3G3B2:
                return width * height;

            // 2 bytes per pixel. A4R4G4B4 belongs here and its omission meant
            // every texture in that format failed to match and was dropped -
            // a 128x128 A4R4G4B4 surface is exactly 32768 bytes.
            case D3DFMT_R5G6B5:
            case D3DFMT_A8L8:
            case D3DFMT_L16:
            case D3DFMT_A4R4G4B4:
            case D3DFMT_X4R4G4B4:
            case D3DFMT_A1R5G5B5:
            case D3DFMT_X1R5G5B5:
            case D3DFMT_A8R3G3B2:
            case D3DFMT_A8P8:
            case D3DFMT_V8U8:
                return width * height * 2;

            case D3DFMT_R8G8B8:
                return width * height * 3;

            // 4 bytes per pixel
            case D3DFMT_A8R8G8B8:
            case D3DFMT_R8G8B8A8:
            case D3DFMT_X8R8G8B8:
            case D3DFMT_A8B8G8R8:
            case D3DFMT_X8B8G8R8:
            case D3DFMT_A2B10G10R10:
            case D3DFMT_A2R10G10B10:
            case D3DFMT_G16R16:
                return width * height * 4;

            case D3DFMT_A16B16G16R16:
                return width * height * 8;

            default:
                return 0;
        }
    }

    void BODY::ReadFromStream(FileReader& stream)
    {
        BaseChunk::ReadFromStream(stream);
        Check(stream);

        const LVL_* lvl = dynamic_cast<const LVL_*>(GetParent());

        //                                                  FACE         FMT_
        const FMT_* fmt = dynamic_cast<const FMT_*>(lvl->GetParent()->GetParent());
        if (fmt == nullptr)
        {
            LOG_ERROR("Could not grab FMT parent!");
            BaseChunk::EnsureEnd(stream);
            return;
        }

        if (p_Image != nullptr)
        {
            delete p_Image;
            p_Image = nullptr;
        }

        size_t width = fmt->p_Info->m_Width;
        size_t height = fmt->p_Info->m_Height;
        // mip levels start at 0
        // divide resolution by 2 for each increasing mip level
        size_t div = (size_t)std::pow(2, lvl->p_Info->m_MipLevel);

        // Clamp to 1x1, NOT 2x2.
        //
        // The old 2x2 floor was a buffer overread waiting to happen: the
        // decoders in DXTexCrossPlat read width*height pixels out of the data
        // buffer, so decoding a genuine 1x1 mip as if it were 2x2 reads four
        // times its length - eight bytes out of a two byte R5G6B5 surface. That
        // corrupts the heap, and the damage only surfaces later as a crash in
        // operator delete[] inside ~CrossPlatImage, which is exactly the stack
        // captured in Crash_2026-08-08_141017477.
        //
        // 1x1 is safe here: CrossPlatImage's dimension check only requires a
        // power of two, and at true dimensions every decoder reads exactly
        // dataSize bytes.
        width = std::max(width / div, (size_t)1);
        height = std::max(height / div, (size_t)1);

        size_t dataSize = GetDataSize();
        D3DFORMAT d3dFormat = fmt->p_Info->m_Format;

        // The declared mip level cannot always be trusted - e.g. geo1.lvl
        // carries a 512x256 image claiming a mip level of 9 - and dividing by
        // it produces dimensions far smaller than the surface actually stored
        // here. The old check compared dataSize against width*height*4 and
        // discarded anything larger, which threw away perfectly good textures
        // on two counts: 4 bytes per pixel is only correct for the 32-bit
        // formats (R5G6B5 is 2, the DXT formats are block compressed), and the
        // comparison ran against the already mip-divided dimensions.
        //
        // Rather than guess, derive the dimensions from the data: walk the mip
        // chain of the full-size image and take the level whose surface size
        // matches dataSize exactly, preferring the level the chunk declared
        // when several match (tiny mips share a minimum block size). This
        // fixes the dimensions as a side effect, which matters beyond the
        // texture being present at all: ToRGBA decodes width*height pixels out
        // of this buffer, so dimensions too large for the data is a heap
        // overread.
        if (ExpectedSurfaceSize(d3dFormat, width, height) != dataSize)
        {
            const size_t declaredLevel = (size_t)lvl->p_Info->m_MipLevel;
            bool  recovered = false;
            size_t bestDelta = (size_t)-1;

            for (size_t level = 0; level < 16; ++level)
            {
                size_t w = std::max((size_t)(fmt->p_Info->m_Width  >> level), (size_t)1);
                size_t h = std::max((size_t)(fmt->p_Info->m_Height >> level), (size_t)1);

                if (ExpectedSurfaceSize(d3dFormat, w, h) != dataSize)
                {
                    continue;
                }

                size_t delta = level > declaredLevel
                             ? level - declaredLevel
                             : declaredLevel - level;
                if (delta < bestDelta)
                {
                    bestDelta = delta;
                    width     = w;
                    height    = h;
                    recovered = true;
                }
            }

            if (!recovered)
            {
                // Genuinely inconsistent: no mip level of this image, in this
                // format, is the size of the data we were handed. Skipping is
                // the only safe option, but say what was expected so the case
                // can be diagnosed rather than merely noted.
                stream.SkipBytes(dataSize);
                LOG_WARN("Image data of {} bytes matches no mip level of a {}x{} {} texture! Skipping image!",
                         dataSize, fmt->p_Info->m_Width, fmt->p_Info->m_Height,
                         LibSWBF2::D3DToString(d3dFormat));
                BaseChunk::EnsureEnd(stream);
                return;
            }
        }

        uint8_t* imageBufferPtr;
        p_Image = new DXTexCrossPlat::CrossPlatImage(width, height, d3dFormat, dataSize);
        imageBufferPtr = p_Image -> GetPixelsPtr();

        if (imageBufferPtr == nullptr || !stream.ReadBytes(imageBufferPtr, dataSize))
        {
            LOG_ERROR("Reading image data of size '{}' failed!", dataSize);
            BaseChunk::EnsureEnd(stream);
            return;
        }

        BaseChunk::EnsureEnd(stream);       
    }


    bool BODY::GetImageData(ETextureFormat format, uint16_t& width, uint16_t& height, const uint8_t*& data) const
    {
        if (p_Image == nullptr)
        {
            LOG_WARN("No image!");
            width = 0;
            height = 0;
            data = nullptr;
            return false;
        }

        if (!p_Image -> IsConvertibleTo(D3DFMT_R8G8B8A8))
        {
            data = nullptr;
            return false;
        }

        p_Image -> ConvertTo(D3DFMT_R8G8B8A8);

        width = p_Image->width;
        height = p_Image->height;
        data = p_Image -> GetPixelsPtr();

        return true;
    }

    BODY::~BODY()
    {
        delete p_Image;
    }

}
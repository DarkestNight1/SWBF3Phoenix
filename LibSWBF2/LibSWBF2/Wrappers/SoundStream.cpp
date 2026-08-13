#include "pch.h"
#include "SoundStream.h"
#include "Chunks/LVL/sound/Stream.h"
#include "Chunks/LVL/sound/StreamInfo.h"

#include "Audio/IMAADPCMDecoder.h"
#include "Audio/PCM16Decoder.h"

#include "FileReader.h"

#include "InternalHelpers.h"
#include "Types/SoundClip.h"
#include "Hashing.h"
#include <unordered_map>
#include <iostream>

namespace LibSWBF2::Wrappers
{
	using Types::SoundClip;




	bool SoundStream::SetFileReader(FileReader * reader)
	{
		p_Reader = reader;

		if (p_CurrentSegment != nullptr)
		{
			p_Reader -> SetPosition(p_CurrentSegment -> GetDataPosition());
		}

		return true;
	}

	bool SoundStream::SetFileStreamBuffer(uint8_t * buffer, size_t NumBytes)
	{
		p_StreamBuffer = buffer;
		m_SizeStreamBuffer = NumBytes;
		m_StreamBufferReadOffset = 0;
		m_StreamBufferDataSize = 0;

		return true;
	}

	int32_t SoundStream::ReadBytesFromStream(int32_t NumBytesToRead)
	{
		if (p_StreamBuffer == nullptr)
		{
			LOG_ERROR("Cannot read encoded bytes, stream buffer is not set!");
			return -1;
		}

		if (p_Reader == nullptr)
		{
			LOG_ERROR("Cannot read encoded bytes, stream reader is not set!");
			return -1;
		}

		if (p_CurrentSegment == nullptr)
		{
			LOG_ERROR("Cannot read encoded bytes, the segment to be read is not set!");
			return -1;
		}


		if (NumBytesToRead > m_SizeStreamBuffer)
		{
			NumBytesToRead = m_SizeStreamBuffer;
		}

		if (p_Reader != nullptr && p_CurrentSegment != nullptr)
		{
			size_t BytesLeft = p_CurrentSegment -> GetDataLength() - m_SegmentOffset;
			NumBytesToRead = NumBytesToRead > BytesLeft ? BytesLeft : NumBytesToRead;

			p_Reader -> ReadBytes(p_StreamBuffer, NumBytesToRead);

			m_SegmentOffset += NumBytesToRead;
			m_StreamBufferDataSize = NumBytesToRead;
			m_StreamBufferReadOffset = 0;

			return NumBytesToRead;  
		}
		else 
		{
			return 0;
		}
	}


    int32_t SoundStream::GetNumSamplesInBytes(int32_t NumBytes)
    {
    	return p_Decoder -> SamplesInBytes(NumBytes);
    }


    int32_t SoundStream::BytesLeftInSegment()
    {
    	return p_CurrentSegment == nullptr ? -1 : p_CurrentSegment -> GetDataLength() - m_SegmentOffset;
    }


    int32_t SoundStream::ReadSamples(void * samplesBuffer, size_t samplesBufferLength, 
        					size_t numSamplesToRead, ESoundFormat format)
    {
    	int32_t NumSamplesRead = 0;
    	int32_t NumBytesReadFromBuffer = 0;
    	int32_t NumBytesLeftInBuffer = 0;

    	size_t BytesRead;


    	// Loop on "is there anything left to decode", which means bytes still in
    	// the buffer as well as bytes still in the segment. Gating on the
    	// segment alone dropped everything held in the buffer once the last
    	// file read had consumed the segment - for a segment smaller than one
    	// buffer that is most of the audio, and for every other segment it is
    	// the tail.
    	while (NumSamplesRead < numSamplesToRead)
    	{
    		NumBytesLeftInBuffer = m_StreamBufferDataSize - m_StreamBufferReadOffset;

    		if (NumBytesLeftInBuffer <= 0)
    		{
    			if (BytesLeftInSegment() <= 0)
    			{
    				break;
    			}

    			ReadBytesFromStream(m_SizeStreamBuffer);
	    		NumBytesLeftInBuffer = m_StreamBufferDataSize - m_StreamBufferReadOffset;

	    		if (NumBytesLeftInBuffer <= 0)
	    		{
	    			break;
	    		}
    		}

    		int32_t NumSamplesDecoded;

	    	if (format == ESoundFormat::Unity)
	    	{
				NumSamplesDecoded = p_Decoder -> DecodeAndFillUnity(p_StreamBuffer + m_StreamBufferReadOffset, NumBytesLeftInBuffer, (float *) samplesBuffer + NumSamplesRead, (size_t) numSamplesToRead - NumSamplesRead, BytesRead);
	    	}
	    	else if (format == ESoundFormat::PCM16)
	    	{
				NumSamplesDecoded = p_Decoder -> DecodeAndFillPCM16(p_StreamBuffer + m_StreamBufferReadOffset, NumBytesLeftInBuffer, (int16_t *) samplesBuffer + NumSamplesRead, (size_t) numSamplesToRead - NumSamplesRead, BytesRead);
	    	}
	    	else 
	    	{
	    		return -1;
	    	}

	    	// A decoder that consumes nothing and produces nothing would spin
	    	// here forever now that the segment no longer bounds the loop.
	    	if (NumSamplesDecoded <= 0 && BytesRead == 0)
	    	{
	    		break;
	    	}

	    	NumSamplesRead += NumSamplesDecoded;

	    	m_StreamBufferReadOffset += BytesRead;
    	}

    	return NumSamplesRead;
    }




    /*
    int32_t SoundStream::ReadSamplesFromBytes(void * samplesBuffer, size_t samplesBufferLength, 
        							size_t numBytesToRead, ESoundFormat format, int32_t &bytesRead)
    {    	
    	if (format == ESoundFormat::Unity)
    	{
			return DecodeAndFillUnity(p_StreamBuffer, numBytesToRead, (float *) samplesBuffer, samplesBufferLength, bytesRead);
    	}
    	else if (format == ESoundFormat::PCM16)
    	{
			return DecodeAndFillPCM16(p_StreamBuffer, numBytesToRead, (int16_t *) samplesBuffer, samplesBufferLength, bytesRead);
    	}
    	else 
    	{
    		return -1;
    	}
    }
    */




    bool SoundStream::SetSegment(FNVHash segmentNameHash)
    {
    	if (p_Reader == nullptr)
    	{
    		LOG_ERROR("Cannot set segment, reader is unassigned!");
    		return false;
    	}

    	// FromChunk builds the decoder inside a loop over the substream count,
    	// so a stream declaring zero substreams leaves it null.
    	if (p_Decoder == nullptr)
    	{
    		LOG_ERROR("Cannot set segment, stream has no decoder (unsupported format?)!");
    		return false;
    	}

    	if (p_StreamBuffer == nullptr)
    	{
    		LOG_ERROR("Cannot set segment, stream buffer is unassigned!");
    		return false;
    	}

    	auto iter = p_NameToIndexMaps -> SoundHashToIndex.find(segmentNameHash);
    	if (iter == p_NameToIndexMaps -> SoundHashToIndex.end())
    	{
    		p_CurrentSegment = nullptr;
    		return false;
    	}
    	else 
    	{
    		p_CurrentSegment = &(m_Sounds[iter -> second]);
    		m_SegmentOffset = 0;
    		m_StreamBufferDataSize = 0;
    		m_StreamBufferReadOffset = 0;

    		p_Decoder -> Reset();

    		p_Reader -> SetPosition(p_CurrentSegment -> GetDataPosition());

    		return true;
    	}
    }



	bool SoundStream::FromChunk(Stream* streamChunk, SoundStream& out)
	{
		if (streamChunk == nullptr)
		{
			LOG_ERROR("Given Stream chunk was NULL!");
			return false;
		}

		out.p_StreamChunk = streamChunk;
		out.p_NameToIndexMaps = std::make_shared<SoundMapsWrapper>();

		for (int i = 0; i < streamChunk -> p_Info -> m_NumSubstreams; i++)
		{
			auto format = streamChunk -> p_Info -> m_Format;
			if (format == ESoundFormat::IMAADPCM)
			{
				out.p_Decoder = std::make_shared<IMAADPCMDecoder>(streamChunk -> p_Info -> m_NumChannels, streamChunk -> p_Info -> m_ChannelInterleave);
			}
			else if (format == ESoundFormat::PCM16)
			{
				out.p_Decoder = std::make_shared<PCM16Decoder>();
			}
		}


		List<SoundClip>& clips = streamChunk -> p_Info -> m_SoundHeaders;
		for (size_t i = 0; i < clips.Size(); ++i)
		{
			Sound sound;
			if (Sound::FromSoundClip(&clips[i], sound))
			{
				sound.m_Format = streamChunk -> p_Info -> m_Format;
				sound.m_NumChannels = streamChunk -> p_Info -> m_NumChannels;
				size_t index = out.m_Sounds.Add(sound);
				out.p_NameToIndexMaps->SoundHashToIndex.emplace(clips[i].m_NameHash, index);
			}
		}

		return true;
	}


	FNVHash SoundStream::GetHashedName() const
	{
		return p_StreamChunk -> p_Info -> m_Name;
	}

	ESoundFormat SoundStream::GetFormat() const
	{
		return p_StreamChunk -> p_Info -> m_Format;
	}

	uint32_t SoundStream::GetNumChannels() const
	{
		return p_StreamChunk -> p_Info -> m_NumChannels;
	}


	bool SoundStream::HasSegment(FNVHash segmentName) const
	{
		auto it = p_NameToIndexMaps->SoundHashToIndex.find(segmentName);
		return it != p_NameToIndexMaps->SoundHashToIndex.end();
	}


	bool SoundStream::HasData() const
	{
		return p_StreamChunk -> p_Data != nullptr;
	}

	
	uint32_t SoundStream::GetNumSubstreams() const
	{
		return p_StreamChunk -> p_Info -> m_NumSubstreams;	
	}
	

	uint32_t SoundStream::GetSubstreamInterleave() const
	{
		return p_StreamChunk -> p_Info -> m_SubstreamInterleave;
	}
	

	uint32_t SoundStream::GetChannelInterleave() const
	{
		return p_StreamChunk -> p_Info -> m_ChannelInterleave;
	}


	const List<Sound>& SoundStream::GetSounds() const
	{
		return m_Sounds;
	}

	const Sound* SoundStream::GetSound(const String& soundName) const
	{
		if (soundName.IsEmpty())
		{
			return nullptr;
		}

		return GetSound(FNV::Hash(soundName));
	}

	const Sound* SoundStream::GetSound(FNVHash soundHash) const
	{
		auto it = p_NameToIndexMaps->SoundHashToIndex.find(soundHash);
		if (it != p_NameToIndexMaps->SoundHashToIndex.end())
		{
			return &m_Sounds[it->second];
		}

		//LOG_WARN("Could not find Sound '{}'!", soundHash);
		return nullptr;
	}
}
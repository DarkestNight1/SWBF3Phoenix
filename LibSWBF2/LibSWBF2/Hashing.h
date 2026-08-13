#pragma once
#include "Types/LibString.h"
#include <unordered_map>
#include <string>
#include <string_view>

namespace LibSWBF2
{
	class CRC
	{
	private:
		static uint32_t m_Table32[256];
		static uint8_t m_ToLower[256];

	public:
		LIBSWBF2_API static CRCChecksum CalcLowerCRC(const char* str);
	};

	class FNV
	{
	public:
		static FNVHash Hash(const Types::String& str);
		static bool Lookup(FNVHash hash, Types::String& result);

		static constexpr FNVHash HashConstexpr(const std::string_view str);

#ifdef LOOKUP_CSV_PATH
	private:
		// The table, built exactly once on first use.
		//
		// This used to be a raw pointer driven from DllMain, which called
		// ReadLookupTable on DLL_THREAD_ATTACH and ReleaseLookupTable on
		// DLL_THREAD_DETACH - so every thread the host process happened to
		// start or finish rebuilt or DELETED a table that other threads were
		// reading through. In a Unity editor, whose job system and Burst churn
		// threads continuously, that is a use-after-free waiting for any
		// concurrent level load to walk into it.
		//
		// A function-local static gets exactly-once, thread-safe
		// initialisation from the C++11 runtime and is never torn down while
		// the process lives, so Lookup becomes a pure read.
		static const std::unordered_map<FNVHash, std::string>& LookupTable();

	public:
		static void ReadLookupTable();
		static void ReleaseLookupTable();
#endif // LOOKUP_CSV_PATH
	};


	constexpr FNVHash FNV::HashConstexpr(const std::string_view str)
	{
		constexpr uint32_t FNV_prime = 16777619;
		constexpr uint32_t offset_basis = 2166136261;

		uint32_t hash = offset_basis;

		for (auto c : str)
		{
			c |= 0x20;

			hash ^= c;
			hash *= FNV_prime;
		}

		return hash;
	}

	constexpr FNVHash operator""_fnv(const char* str, const std::size_t length)
	{
		return FNV::HashConstexpr({ str, length });
	}
}
#include "pch.h"

#ifdef WIN32

// Deliberately does nothing but succeed.
//
// This used to call FNV::ReadLookupTable on DLL_PROCESS_ATTACH *and*
// DLL_THREAD_ATTACH, and FNV::ReleaseLookupTable on DLL_THREAD_DETACH and
// DLL_PROCESS_DETACH. The thread notifications are the problem: they fire for
// every thread the host process creates or destroys, so any thread exiting
// anywhere in a Unity editor deleted the global FNV lookup table while other
// threads were reading through it. A concurrent level load - several worker
// threads reading STR chunks, each calling FNV::Lookup - then dereferenced
// freed memory, which is what crashed inside Hashing.cpp.
//
// It was also unsafe on its own terms: ReadLookupTable opens and parses a CSV,
// and doing file I/O inside DllMain runs it under the loader lock, where it
// can deadlock against any other module being loaded on another thread.
//
// The table now builds itself on first use via a function-local static, which
// is thread-safe by the C++11 standard and never torn down, so there is
// nothing for the loader to do here.
BOOL APIENTRY DllMain( HMODULE hModule,
                       DWORD  ul_reason_for_call,
                       LPVOID lpReserved
                     )
{
    return TRUE;
}

#endif

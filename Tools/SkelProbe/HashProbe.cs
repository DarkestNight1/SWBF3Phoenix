using System;
using LibSWBF2.Utils;

namespace SkelProbe
{
    /// <summary>
    /// Resolve an ODF property hash back to a name by trying candidates.
    /// </summary>
    /// <remarks>
    /// Property names are stored hashed, and lookup.csv does not carry the
    /// foliage ones. Since PhxClass binds a property by hashing the name it
    /// declares, getting a new class's bindings right means knowing which name
    /// produces which hash - so hash the plausible names and see which land.
    ///
    /// Usage: SkelProbe --hash Name1 Name2 ...
    /// </remarks>
    static class HashProbe
    {
        public static void Run(string[] names)
        {
            foreach (string n in names)
            {
                Console.WriteLine($"  0x{HashUtils.GetFNV(n):x8}  {n}");
            }
        }
    }
}

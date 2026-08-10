using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Collects what the importers could and could not resolve while building a
/// map.
/// </summary>
/// <remarks>
/// Import failures in this project are individually survivable by design - a
/// missing model costs one prop, not the level - which means they are also
/// individually invisible. Hundreds of them scroll past in the console as
/// separate warnings and nobody can tell whether a map imported at 99% or at
/// 60%.
///
/// This is the counting layer under that: every loader reports each
/// name it tried to resolve and whether it succeeded, so the end of a load can
/// state "1843 of 1901 models resolved" and name the 58. It is a pure
/// observer - reporting a miss never changes what the importer does about it.
/// </remarks>
public static class BFImportDiagnostics
{
    public sealed class Category
    {
        public readonly BFSourceKind Kind;
        public readonly HashSet<string> Requested = new HashSet<string>();
        public readonly HashSet<string> Resolved = new HashSet<string>();
        public readonly Dictionary<string, string> Missing = new Dictionary<string, string>();

        public Category(BFSourceKind kind) { Kind = kind; }
    }

    static readonly Dictionary<BFSourceKind, Category> Categories = new Dictionary<BFSourceKind, Category>();
    static readonly List<string> Failures = new List<string>();

    /// <summary>Import-time exceptions that cost a whole object.</summary>
    public static IReadOnlyList<string> HardFailures => Failures;

    public static void Reset()
    {
        Categories.Clear();
        Failures.Clear();
    }

    static Category Get(BFSourceKind kind)
    {
        if (!Categories.TryGetValue(kind, out Category category))
        {
            category = new Category(kind);
            Categories.Add(kind, category);
        }
        return category;
    }

    /// <summary>A name the importer looked up and found.</summary>
    public static void Resolved(BFSourceKind kind, string name)
    {
        if (string.IsNullOrEmpty(name)) return;

        Category category = Get(kind);
        category.Requested.Add(name);
        category.Resolved.Add(name);
        // A name that resolved on a later attempt is no longer missing; asset
        // lookups are retried through several fallbacks, so first-attempt
        // failures are routine and must not be reported as import loss.
        category.Missing.Remove(name);
    }

    /// <summary>
    /// A name the importer looked up and did not find.
    /// <paramref name="usedBy"/> names whatever needed it, which is the part
    /// that makes a miss actionable.
    /// </summary>
    public static void Missing(BFSourceKind kind, string name, string usedBy = null)
    {
        if (string.IsNullOrEmpty(name)) return;

        Category category = Get(kind);
        category.Requested.Add(name);
        if (category.Resolved.Contains(name)) return;

        if (category.Missing.TryGetValue(name, out string knownUsers))
        {
            if (!string.IsNullOrEmpty(usedBy) && !knownUsers.Contains(usedBy))
            {
                // Cap the trail: one missing shared model can be referenced by
                // hundreds of instances and the list is for diagnosis, not
                // completeness.
                if (knownUsers.Length < 200)
                {
                    category.Missing[name] = knownUsers + ", " + usedBy;
                }
            }
            return;
        }
        category.Missing.Add(name, usedBy ?? string.Empty);
    }

    /// <summary>An object that threw during import and was dropped entirely.</summary>
    public static void HardFailure(string what, string why)
    {
        Failures.Add($"{what}: {why}");
    }

    public static int RequestedCount(BFSourceKind kind) =>
        Categories.TryGetValue(kind, out Category c) ? c.Requested.Count : 0;

    public static int ResolvedCount(BFSourceKind kind) =>
        Categories.TryGetValue(kind, out Category c) ? c.Resolved.Count : 0;

    public static IReadOnlyDictionary<string, string> MissingOf(BFSourceKind kind) =>
        Categories.TryGetValue(kind, out Category c)
            ? (IReadOnlyDictionary<string, string>)c.Missing
            : new Dictionary<string, string>();

    public static IEnumerable<BFSourceKind> TrackedKinds => Categories.Keys;
}

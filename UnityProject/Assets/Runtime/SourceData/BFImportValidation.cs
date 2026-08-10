using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

/// <summary>Source vs imported counts for one category of authored data.</summary>
[Serializable]
public sealed class BFCategoryReport
{
    public string category;
    public int source;
    public int imported;

    /// <summary>Non-empty when the category has an authored count to compare against.</summary>
    public bool comparable;

    public bool IsLossy => comparable && imported < source;
}

/// <summary>One name the import asked for and could not find.</summary>
[Serializable]
public sealed class BFUnresolvedReference
{
    public string category;
    public string name;
    public string usedBy;
}

/// <summary>
/// Machine-readable account of what a map's import actually produced.
/// </summary>
/// <remarks>
/// Written as JSON next to the player data so it can be diffed between runs,
/// between builds and between stock and modded data. The point is to make
/// "did this map import completely?" a question with an answer, rather than
/// something inferred by walking a level looking for things that are not
/// there.
/// </remarks>
[Serializable]
public sealed class BFImportReport
{
    public string generatedUtc;
    public string levelName;
    public string worldName;
    public List<BFCategoryReport> categories = new List<BFCategoryReport>();
    public List<BFUnresolvedReference> unresolved = new List<BFUnresolvedReference>();
    public List<string> classesWithoutRuntimeType = new List<string>();
    public List<string> hardFailures = new List<string>();

    public bool HasImportLoss
    {
        get
        {
            for (int i = 0; i < categories.Count; ++i)
            {
                if (categories[i].IsLossy) return true;
            }
            return unresolved.Count > 0 || hardFailures.Count > 0;
        }
    }

    public string ToSummary()
    {
        var sb = new StringBuilder();
        sb.Append("[BFImport] ").Append(levelName).Append(' ').Append(worldName).AppendLine();
        for (int i = 0; i < categories.Count; ++i)
        {
            BFCategoryReport c = categories[i];
            sb.Append("  ").Append(c.category.PadRight(14));
            sb.Append(c.comparable ? $"{c.imported} / {c.source}" : $"{c.imported}");
            if (c.IsLossy) sb.Append("   <-- MISSING ").Append(c.source - c.imported);
            sb.AppendLine();
        }
        if (unresolved.Count > 0)
        {
            sb.Append("  unresolved references: ").Append(unresolved.Count).AppendLine();
        }
        if (classesWithoutRuntimeType.Count > 0)
        {
            sb.Append("  odf classes with no runtime type: ")
              .Append(classesWithoutRuntimeType.Count).AppendLine();
        }
        if (hardFailures.Count > 0)
        {
            sb.Append("  objects dropped by exceptions: ").Append(hardFailures.Count).AppendLine();
        }
        return sb.ToString();
    }
}

/// <summary>
/// Builds a <see cref="BFImportReport"/> by comparing the semantic source
/// database against what the importers reported building.
/// </summary>
public static class BFImportValidator
{
    /// <summary>Categories whose authored count the source database knows.</summary>
    static readonly BFSourceKind[] Counted =
    {
        BFSourceKind.World,
        BFSourceKind.Instance,
        BFSourceKind.EntityClass,
        BFSourceKind.Region,
        BFSourceKind.Barrier,
        BFSourceKind.HintNode,
        BFSourceKind.Light,
        BFSourceKind.Terrain,
        BFSourceKind.PlanningHub,
        BFSourceKind.PlanningArc,
        BFSourceKind.Path,
    };

    /// <summary>
    /// Categories the source database cannot count because the authored total
    /// is not enumerable from a world: an LVL holds every model in the game's
    /// shared data, not just this map's. For these, "requested by the import"
    /// is the only meaningful denominator.
    /// </summary>
    static readonly BFSourceKind[] Requested =
    {
        BFSourceKind.Model,
        BFSourceKind.Material,
        BFSourceKind.Texture,
        BFSourceKind.Sound,
        BFSourceKind.Effect,
        BFSourceKind.Config,
        BFSourceKind.Animation,
    };

    public static BFImportReport Build(BFSourceDatabase database, string worldName)
    {
        var report = new BFImportReport
        {
            generatedUtc = DateTime.UtcNow.ToString("o"),
            levelName = database == null ? string.Empty : database.LevelName,
            worldName = worldName ?? string.Empty,
        };

        if (database == null) return report;

        for (int i = 0; i < Counted.Length; ++i)
        {
            BFSourceKind kind = Counted[i];
            int source = database.SourceCount(kind);
            int imported = database.ImportedCount(kind);
            if (source == 0 && imported == 0) continue;

            report.categories.Add(new BFCategoryReport
            {
                category = kind.ToString(),
                source = source,
                imported = imported,
                comparable = true,
            });
        }

        for (int i = 0; i < Requested.Length; ++i)
        {
            BFSourceKind kind = Requested[i];
            int requested = BFImportDiagnostics.RequestedCount(kind);
            if (requested == 0) continue;

            report.categories.Add(new BFCategoryReport
            {
                category = kind.ToString(),
                source = requested,
                imported = BFImportDiagnostics.ResolvedCount(kind),
                comparable = true,
            });
        }

        foreach (BFSourceKind kind in BFImportDiagnostics.TrackedKinds)
        {
            foreach (KeyValuePair<string, string> miss in BFImportDiagnostics.MissingOf(kind))
            {
                report.unresolved.Add(new BFUnresolvedReference
                {
                    category = kind.ToString(),
                    name = miss.Key,
                    usedBy = miss.Value,
                });
            }
        }

        foreach (KeyValuePair<string, BFEntityClassDefinition> entry in database.Classes)
        {
            BFEntityClassDefinition def = entry.Value;
            if (def.HasRuntimeType) continue;

            // Root base class is what the registry dispatches on, so report it
            // rather than the odf name: fifty odfs sharing one unregistered
            // base class are one gap, not fifty.
            report.classesWithoutRuntimeType.Add(
                $"{def.RootBaseClassName} ({def.Name} x{def.InstanceCount})");
        }
        report.classesWithoutRuntimeType.Sort(StringComparer.OrdinalIgnoreCase);

        report.hardFailures.AddRange(BFImportDiagnostics.HardFailures);
        return report;
    }

    /// <summary>
    /// Build, log and write the report. The path is returned so callers can
    /// point a user at it; a failed write is reported and never throws into
    /// the load.
    /// </summary>
    public static string Emit(BFSourceDatabase database, string worldName)
    {
        BFImportReport report = Build(database, worldName);

        if (report.HasImportLoss)
        {
            Debug.LogWarning(report.ToSummary());
        }
        else
        {
            Debug.Log(report.ToSummary());
        }

        try
        {
            string fileName = string.IsNullOrEmpty(worldName)
                ? "bf2-import-validation.json"
                : $"bf2-import-validation-{Sanitize(worldName)}.json";
            string path = Path.Combine(Application.persistentDataPath, fileName);
            File.WriteAllText(path, JsonUtility.ToJson(report, true));
            Debug.Log($"[BFImport] validation report written to {path}");
            return path;
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[BFImport] could not write validation report: {e.Message}");
            return null;
        }
    }

    static string Sanitize(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (char c in name)
        {
            sb.Append(char.IsLetterOrDigit(c) || c == '_' || c == '-' ? c : '_');
        }
        return sb.ToString();
    }
}

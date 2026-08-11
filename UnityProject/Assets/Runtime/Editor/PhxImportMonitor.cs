using System.Collections.Generic;
using UnityEngine;
using UnityEditor;

/// <summary>
/// Reads the semantic source database of the running map back out: what the
/// data authored, what the import built, and what it could not resolve.
///
/// The same numbers go to a JSON file on every load (see
/// <see cref="BFImportValidator"/>); this is the interactive view for working
/// on a map without diffing files.
/// </summary>
public class PhxImportMonitor : EditorWindow
{
    Vector2 Scroll;
    BFImportReport Report;
    bool ShowUnresolved = true;
    bool ShowClasses = true;
    bool ShowFailures = true;

    [MenuItem("Phoenix/Import Monitor")]
    public static void OpenImportMonitor()
    {
        GetWindow<PhxImportMonitor>().Show();
    }

    /// <summary>
    /// Count how many imported segments got a semantic role.
    /// </summary>
    /// <remarks>
    /// Role inference is keyword matching against the artists' own tags, so
    /// the only way to know whether it works on real content is to count it on
    /// real content. A map where almost everything is Unknown means the
    /// vocabulary in <see cref="BFSegmentRoles"/> does not match how this
    /// content was named - which is a fixable, findable problem, and invisible
    /// without this.
    /// </remarks>
    static void ReportSegmentRoles()
    {
        BFSegmentIdentity[] segments = FindObjectsOfType<BFSegmentIdentity>(true);
        if (segments.Length == 0)
        {
            Debug.Log("[BFImport] No segment identities in the scene - either no models are " +
                      "loaded, or the importer did not attach them.");
            return;
        }

        var counts = new Dictionary<BFSegmentRole, int>();
        var examples = new Dictionary<BFSegmentRole, string>();
        for (int i = 0; i < segments.Length; ++i)
        {
            BFSegmentRole role = segments[i].Role;
            counts.TryGetValue(role, out int n);
            counts[role] = n + 1;

            if (!examples.ContainsKey(role)) examples[role] = segments[i].ToString();
        }

        var sb = new System.Text.StringBuilder();
        sb.Append("[BFImport] ").Append(segments.Length).AppendLine(" model segment(s):");
        foreach (KeyValuePair<BFSegmentRole, int> entry in counts)
        {
            sb.Append("  ").Append(entry.Key.ToString().PadRight(10))
              .Append(entry.Value.ToString().PadLeft(5))
              .Append("   e.g. ").AppendLine(examples[entry.Key]);
        }
        Debug.Log(sb.ToString());
    }

    void OnGUI()
    {
        BFSourceDatabase source = BFSourceDatabase.Active;
        if (source == null || source.IsEmpty)
        {
            EditorGUILayout.LabelField("No map data captured. Load a map first.");
            if (GUILayout.Button("Refresh")) Report = null;
            return;
        }

        EditorGUILayout.LabelField("Level", source.LevelName);

        if (GUILayout.Button("Rebuild report"))
        {
            Report = null;
        }
        if (Report == null)
        {
            Report = BFImportValidator.Build(source, PhxGame.GetEnvironment()?.GetWorldName() ?? "");
        }

        if (GUILayout.Button("Write report to disk"))
        {
            BFImportValidator.Emit(source, PhxGame.GetEnvironment()?.GetWorldName() ?? "");
        }

        Scroll = EditorGUILayout.BeginScrollView(Scroll);

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Imported / authored", EditorStyles.boldLabel);
        foreach (BFCategoryReport category in Report.categories)
        {
            string value = category.comparable
                ? $"{category.imported} / {category.source}"
                : category.imported.ToString();
            if (category.IsLossy)
            {
                value += $"   missing {category.source - category.imported}";
            }
            EditorGUILayout.LabelField(category.category, value);
        }

        EditorGUILayout.Space();
        ShowClasses = EditorGUILayout.Foldout(ShowClasses,
            $"ODF classes with no runtime type ({Report.classesWithoutRuntimeType.Count})");
        if (ShowClasses)
        {
            foreach (string entry in Report.classesWithoutRuntimeType)
            {
                EditorGUILayout.LabelField("   " + entry);
            }
        }

        EditorGUILayout.Space();
        ShowUnresolved = EditorGUILayout.Foldout(ShowUnresolved,
            $"Unresolved references ({Report.unresolved.Count})");
        if (ShowUnresolved)
        {
            foreach (BFUnresolvedReference miss in Report.unresolved)
            {
                EditorGUILayout.LabelField($"   [{miss.category}] {miss.name}", miss.usedBy);
            }
        }

        EditorGUILayout.Space();
        ShowFailures = EditorGUILayout.Foldout(ShowFailures,
            $"Objects dropped by exceptions ({Report.hardFailures.Count})");
        if (ShowFailures)
        {
            foreach (string failure in Report.hardFailures)
            {
                EditorGUILayout.LabelField("   " + failure);
            }
        }

        EditorGUILayout.Space();
        if (GUILayout.Button("Report segment roles in scene"))
        {
            ReportSegmentRoles();
        }

        EditorGUILayout.Space();
        EditorGUILayout.LabelField("Worlds", EditorStyles.boldLabel);
        IReadOnlyList<BFWorldDefinition> worlds = source.Worlds;
        for (int i = 0; i < worlds.Count; ++i)
        {
            BFWorldDefinition world = worlds[i];
            EditorGUILayout.LabelField(world.Name,
                $"{world.Instances.Count} inst, {world.Regions.Count} reg, " +
                $"{world.Barriers.Count} bar, {world.HintNodes.Count} hint, " +
                $"{world.Lights.Count} light, terrain: {(world.Terrain != null ? "yes" : "no")}");
        }

        EditorGUILayout.EndScrollView();
    }
}

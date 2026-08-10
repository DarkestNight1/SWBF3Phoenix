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

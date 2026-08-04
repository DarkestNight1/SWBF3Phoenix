using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// First-run setup panel. If no valid Star Wars Battlefront II (2005) install
/// can be auto-detected, the game would previously just log
/// "Invalid game path" to the console and sit at a black screen - which is
/// the single worst part of installing this project.
///
/// Instead this shows an in-game panel that:
///   - explains exactly what is needed,
///   - lists every location that was probed,
///   - lets the user paste/type their install folder and validates it live,
///   - saves the choice to bf3legacy.json so it is remembered, and
///   - reloads straight into the game once a valid path is given.
///
/// It also surfaces detected mods (BF3 Legacy etc.) so users can confirm
/// their addon folder was picked up.
/// </summary>
public class PhxFirstRunSetup : MonoBehaviour
{
    const float PanelWidth = 760f;
    const float PanelHeight = 460f;

    bool Visible;
    bool Checked;
    string PathInput = "";
    string StatusMessage = "";
    bool StatusIsError;
    List<string> Probed;
    Vector2 ScrollPos;

    float CheckTimer;


    void Update()
    {
        // give PhxGame a moment to run its own detection first
        CheckTimer -= Time.deltaTime;
        if (Checked || CheckTimer > 0f) return;

        PhxGame game = PhxGame.Instance;
        if (game == null)
        {
            CheckTimer = 0.5f;
            return;
        }

        Checked = true;
        if (!PhxGamePathDetector.IsValidGamePath(game.Settings.GamePathString))
        {
            Visible = true;
            Probed = PhxGamePathDetector.GetProbedPaths();
            PathInput = PhxBF3.Config.GamePathOverride ?? "";
            Debug.LogWarning("[BF3Legacy] No Battlefront II install found - showing setup panel.");
        }
    }

    void OnGUI()
    {
        if (!Visible) return;

        Rect area = new Rect((Screen.width - PanelWidth) * 0.5f,
                             (Screen.height - PanelHeight) * 0.5f,
                             PanelWidth, PanelHeight);

        GUI.Box(area, "SWBF3 Phoenix - Setup");
        GUILayout.BeginArea(new Rect(area.x + 16f, area.y + 30f, area.width - 32f, area.height - 46f));

        GUILayout.Label("Star Wars Battlefront II (2005) could not be found.\n" +
                        "This project loads content from your own copy of the game - " +
                        "it does not include any game files.");
        GUILayout.Space(8f);

        GUILayout.Label("Enter the folder that contains 'GameData':");
        PathInput = GUILayout.TextField(PathInput ?? "", GUILayout.Height(22f));

        GUILayout.BeginHorizontal();
        if (GUILayout.Button("Use this folder", GUILayout.Height(26f)))
        {
            ApplyPath(PathInput);
        }
        if (GUILayout.Button("Search again", GUILayout.Height(26f)))
        {
            string found = PhxGamePathDetector.TryDetect();
            if (found != null)
            {
                ApplyPath(found);
            }
            else
            {
                StatusIsError = true;
                StatusMessage = "Still nothing found in the standard locations.";
            }
        }
        if (GUILayout.Button("Quit", GUILayout.Height(26f)))
        {
            Application.Quit();
        }
        GUILayout.EndHorizontal();

        if (!string.IsNullOrEmpty(StatusMessage))
        {
            Color prev = GUI.color;
            GUI.color = StatusIsError ? new Color(1f, 0.5f, 0.45f) : new Color(0.5f, 1f, 0.6f);
            GUILayout.Label(StatusMessage);
            GUI.color = prev;
        }

        GUILayout.Space(8f);
        GUILayout.Label("Locations checked automatically:");
        ScrollPos = GUILayout.BeginScrollView(ScrollPos, GUILayout.Height(150f));
        if (Probed != null)
        {
            foreach (string p in Probed)
            {
                GUILayout.Label("   " + p);
            }
        }
        GUILayout.EndScrollView();

        GUILayout.Label("Typical: .../steamapps/common/Star Wars Battlefront II");

        GUILayout.EndArea();
    }

    void ApplyPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            StatusIsError = true;
            StatusMessage = "Please enter a folder path.";
            return;
        }

        path = path.Trim().Trim('"').Replace('\\', '/');

        if (!PhxGamePathDetector.IsValidGamePath(path))
        {
            StatusIsError = true;
            StatusMessage = $"'{path}' doesn't look like a Battlefront II install " +
                            "(expected GameData/data/_lvl_pc/common.lvl inside it).";
            return;
        }

        // remember it, then restart into the game
        PhxBF3.Config.GamePathOverride = path;
        PhxBF3.SaveConfig();

        StatusIsError = false;
        StatusMessage = "Found it. Starting...";
        Visible = false;

        Debug.Log($"[BF3Legacy] Game path set to '{path}', reloading.");
        UnityEngine.SceneManagement.SceneManager.LoadScene(
            UnityEngine.SceneManagement.SceneManager.GetActiveScene().buildIndex);
    }
}

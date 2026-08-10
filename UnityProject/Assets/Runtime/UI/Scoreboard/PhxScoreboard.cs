using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// BF2-style scoreboard, held open on Tab.
///
/// Two team columns side by side, each headed by the team name and its
/// remaining reinforcements, then one row per combatant showing Score, Kills
/// and Deaths - the same four columns and the same ordering (score descending)
/// the original game uses.
///
/// Built entirely in code rather than as a scene prefab: the rest of the UI
/// lives in PhxMainScene, but that scene is serialized YAML which cannot be
/// authored or verified outside the editor. Constructing the panel at runtime
/// keeps this reviewable as a diff and testable by simply pressing Tab.
/// </summary>
public class PhxScoreboard : MonoBehaviour
{
    static PhxMatch Match => PhxGame.GetMatch();

    // Legacy UnityEngine.UI.Text to match the rest of the UI (PhxHUD,
    // PhxButton, PhxCharacterItem all use it). Unity 2020.3 still ships the
    // builtin Arial that those scene objects reference.
    const string BuiltinFont = "Arial.ttf";

    const KeyCode ToggleKey = KeyCode.Tab;

    // Refresh rate while open. Rebuilding text every frame is wasted work for
    // numbers that change a few times a second at most.
    const float RefreshInterval = 0.25f;

    const int MaxRowsPerTeam = 36;   // TeamSize 32 plus headroom
    const float RowHeight = 26f;
    const float PanelWidth = 1500f;
    const float PanelHeight = 900f;
    const float ColumnWidth = 700f;

    // Column x offsets within a team column, and their widths.
    const float NameX = 0f, NameW = 380f;
    const float ScoreX = 390f, StatW = 100f;
    const float KillsX = 495f;
    const float DeathsX = 600f;

    Canvas Canvas;
    GameObject Panel;
    Text ResultText;
    Font Font;

    readonly List<TeamColumn> Columns = new List<TeamColumn>();
    readonly List<PhxPawnController> Scratch = new List<PhxPawnController>();

    float RefreshTimer;
    bool Visible;

    class Row
    {
        public GameObject Root;
        public Text Name, Score, Kills, Deaths;
    }

    class TeamColumn
    {
        public int TeamNum;
        public Text Header;
        public Text ColumnTitles;
        public readonly List<Row> Rows = new List<Row>();
    }

    void Awake()
    {
        Font = Resources.GetBuiltinResource<Font>(BuiltinFont);
        if (Font == null)
        {
            // Without a font every Text renders blank, which would look like a
            // broken panel rather than a missing resource - say so instead.
            Debug.LogWarning($"[BF3Legacy] Scoreboard disabled: builtin font '{BuiltinFont}' unavailable.");
            enabled = false;
            return;
        }

        Build();
        SetVisible(false);
    }

    void Update()
    {
        PhxMatch match = Match;
        if (match == null)
        {
            if (Visible) SetVisible(false);
            return;
        }

        // Held, not toggled - BF2 shows the board while the key is down. A
        // finished round pins it open, which is the only end-of-round feedback
        // there is until a proper results screen exists.
        bool wanted = Input.GetKey(ToggleKey) || match.IsMatchOver;
        if (wanted != Visible)
        {
            SetVisible(wanted);
            RefreshTimer = 0f;   // draw immediately on open
        }

        if (!Visible) return;

        RefreshTimer -= Time.unscaledDeltaTime;
        if (RefreshTimer <= 0f)
        {
            RefreshTimer = RefreshInterval;
            Refresh(match);
        }
    }

    void SetVisible(bool visible)
    {
        Visible = visible;
        if (Panel != null)
        {
            Panel.SetActive(visible);
        }
    }

    void Refresh(PhxMatch match)
    {
        if (match.IsMatchOver)
        {
            bool playerWon = match.Player != null && match.WinningTeam == match.Player.Team;
            ResultText.text = playerWon ? "VICTORY" : "DEFEAT";
            ResultText.color = playerWon ? new Color(0.6f, 1f, 0.6f) : new Color(1f, 0.55f, 0.55f);
        }
        else
        {
            ResultText.text = "";
        }

        for (int c = 0; c < Columns.Count; ++c)
        {
            TeamColumn col = Columns[c];
            PhxMatch.PhxTeam team = match.Teams[col.TeamNum - 1];

            int reinforcements = team.ReinforcementCount;
            string tickets = reinforcements < 0 ? "--" : reinforcements.ToString();
            col.Header.text = $"{team.Name}    {tickets}";
            col.Header.color = match.GetTeamColor(col.TeamNum);

            match.CollectScoreboard(col.TeamNum, Scratch);

            // BF2 orders by score, highest first; kills break ties so the board
            // does not shuffle arbitrarily between refreshes.
            Scratch.Sort((a, b) =>
            {
                int byScore = b.Score.CompareTo(a.Score);
                return byScore != 0 ? byScore : b.Kills.CompareTo(a.Kills);
            });

            for (int r = 0; r < col.Rows.Count; ++r)
            {
                Row row = col.Rows[r];
                if (r >= Scratch.Count)
                {
                    row.Root.SetActive(false);
                    continue;
                }

                PhxPawnController ctrl = Scratch[r];
                row.Root.SetActive(true);
                row.Name.text = string.IsNullOrEmpty(ctrl.DisplayName) ? "Trooper" : ctrl.DisplayName;
                row.Score.text = ctrl.Score.ToString();
                row.Kills.text = ctrl.Kills.ToString();
                row.Deaths.text = ctrl.Deaths.ToString();

                // The local player is highlighted so you can find yourself in a
                // 32-strong list.
                bool isPlayer = ReferenceEquals(ctrl, match.Player);
                Color rowColor = isPlayer ? new Color(1f, 0.92f, 0.45f) : Color.white;
                row.Name.color = rowColor;
                row.Score.color = rowColor;
                row.Kills.color = rowColor;
                row.Deaths.color = rowColor;
            }
        }
    }

    // ================= construction =====================================

    void Build()
    {
        GameObject canvasObj = new GameObject("PhxScoreboardCanvas");
        canvasObj.transform.SetParent(transform, false);

        Canvas = canvasObj.AddComponent<Canvas>();
        Canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        // Above the HUD, which is authored in the scene at the default order.
        Canvas.sortingOrder = 500;

        CanvasScaler scaler = canvasObj.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.matchWidthOrHeight = 0.5f;

        Panel = NewRect("Panel", canvasObj.transform, Vector2.zero, new Vector2(PanelWidth, PanelHeight));
        Image backdrop = Panel.AddComponent<Image>();
        backdrop.color = new Color(0f, 0f, 0f, 0.78f);

        ResultText = NewText(Panel.transform, "Result",
                             new Vector2(ColumnWidth * 0.5f, PanelHeight * 0.5f - 12f),
                             new Vector2(PanelWidth, 44f), 34, TextAnchor.MiddleCenter);

        // Team 1 left, team 2 right. Only the two playable sides get a column;
        // BF2's board is likewise always two-sided.
        Columns.Add(BuildColumn(1, -ColumnWidth * 0.5f - 20f));
        Columns.Add(BuildColumn(2, ColumnWidth * 0.5f + 20f));
    }

    TeamColumn BuildColumn(int teamNum, float xOffset)
    {
        TeamColumn col = new TeamColumn { TeamNum = teamNum };

        GameObject root = NewRect($"Team{teamNum}", Panel.transform,
                                  new Vector2(xOffset, 0f), new Vector2(ColumnWidth, PanelHeight));

        float top = PanelHeight * 0.5f - 40f;

        col.Header = NewText(root.transform, "Header", new Vector2(NameX, top),
                             new Vector2(ColumnWidth, 40f), 28, TextAnchor.MiddleLeft);

        col.ColumnTitles = NewText(root.transform, "Titles", new Vector2(NameX, top - 36f),
                                   new Vector2(ColumnWidth, 26f), 18, TextAnchor.MiddleLeft);
        col.ColumnTitles.color = new Color(0.7f, 0.7f, 0.7f);
        col.ColumnTitles.text = "";

        // Column headings sit at the same offsets as the row cells so they
        // actually line up - Arial is proportional, so a single padded string
        // would not.
        MakeHeading(root.transform, "Name", NameX, top - 36f, NameW, TextAnchor.MiddleLeft);
        MakeHeading(root.transform, "Score", ScoreX, top - 36f, StatW, TextAnchor.MiddleRight);
        MakeHeading(root.transform, "Kills", KillsX, top - 36f, StatW, TextAnchor.MiddleRight);
        MakeHeading(root.transform, "Deaths", DeathsX, top - 36f, StatW, TextAnchor.MiddleRight);

        float firstRowY = top - 70f;
        for (int i = 0; i < MaxRowsPerTeam; ++i)
        {
            float y = firstRowY - i * RowHeight;
            GameObject rowRoot = NewRect($"Row{i}", root.transform,
                                         new Vector2(0f, y), new Vector2(ColumnWidth, RowHeight));

            Row row = new Row
            {
                Root = rowRoot,
                Name = NewText(rowRoot.transform, "Name", new Vector2(NameX, 0f),
                               new Vector2(NameW, RowHeight), 18, TextAnchor.MiddleLeft),
                Score = NewText(rowRoot.transform, "Score", new Vector2(ScoreX, 0f),
                                new Vector2(StatW, RowHeight), 18, TextAnchor.MiddleRight),
                Kills = NewText(rowRoot.transform, "Kills", new Vector2(KillsX, 0f),
                                new Vector2(StatW, RowHeight), 18, TextAnchor.MiddleRight),
                Deaths = NewText(rowRoot.transform, "Deaths", new Vector2(DeathsX, 0f),
                                 new Vector2(StatW, RowHeight), 18, TextAnchor.MiddleRight),
            };

            rowRoot.SetActive(false);
            col.Rows.Add(row);
        }

        return col;
    }

    void MakeHeading(Transform parent, string label, float x, float y, float width, TextAnchor anchor)
    {
        Text t = NewText(parent, $"Head_{label}", new Vector2(x, y), new Vector2(width, 26f), 16, anchor);
        t.color = new Color(0.65f, 0.65f, 0.65f);
        t.text = label;
    }

    /// <summary>
    /// Anchored to the parent's centre with an explicit size, so every offset
    /// above is measured from the middle of the panel and stays put at any
    /// resolution.
    /// </summary>
    static GameObject NewRect(string name, Transform parent, Vector2 anchoredPos, Vector2 size)
    {
        GameObject obj = new GameObject(name, typeof(RectTransform));
        obj.transform.SetParent(parent, false);

        RectTransform rect = (RectTransform)obj.transform;
        rect.anchorMin = new Vector2(0.5f, 0.5f);
        rect.anchorMax = new Vector2(0.5f, 0.5f);
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.sizeDelta = size;
        rect.anchoredPosition = anchoredPos;
        return obj;
    }

    Text NewText(Transform parent, string name, Vector2 anchoredPos, Vector2 size,
                 int fontSize, TextAnchor anchor)
    {
        // x is a left edge for the caller's convenience; convert to the centre
        // pivot NewRect uses.
        GameObject obj = NewRect(name, parent,
                                 new Vector2(anchoredPos.x - ColumnWidth * 0.5f + size.x * 0.5f, anchoredPos.y),
                                 size);

        Text text = obj.AddComponent<Text>();
        text.font = Font;
        text.fontSize = fontSize;
        text.alignment = anchor;
        text.horizontalOverflow = HorizontalWrapMode.Overflow;
        text.verticalOverflow = VerticalWrapMode.Overflow;
        text.raycastTarget = false;
        text.text = "";
        return text;
    }
}

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using LibSWBF2.Enums;

public class PhxCharacterSelect : PhxMenuInterface
{
    static PhxGame Game => PhxGame.Instance;
    static PhxScene Scene => PhxGame.GetScene();
    static PhxMatch Match => PhxGame.GetMatch();
    static PhxCamera Camera => PhxGame.GetCamera();


    [Header("References")]
    public PhxUIMap Map;
    public PhxCharacterItem ItemPrefab;
    public RectTransform ListContents;
    public Button BtnSpawn;
    public Button BtnSwitchTeam;
    public Button BtnNextCamera;

    [Header("Settings")]
    public float ItemSpace = 5.0f;
    public float MaxItemHeight = 200f;

    List<PhxCharacterItem> Items = new List<PhxCharacterItem>();
    PhxClass CurrentSelection = null;
    List<IPhxControlableInstance> UnitPreviews = new List<IPhxControlableInstance>();
    PhxCommandpost SpawnCP;

    static int nameCounter = 0;

    public void UpdateCharacterList()
    {
        Clear();

        // Team numbers are 1-based. An unassigned player is still team 0, which
        // would index Teams[-1] and throw out of Start() - leaving the screen
        // half-built with dead buttons rather than simply empty.
        int teamNum = Match.Player.Team;
        if (teamNum < 1 || teamNum > Match.Teams.Length)
        {
            Debug.LogError($"Character selection opened with no valid player team (team {teamNum}); " +
                           "no units can be listed.");
            return;
        }

        PhxMatch.PhxTeam team = Match.Teams[teamNum - 1];
        if (team == null)
        {
            Debug.LogError($"Character selection opened but team {teamNum} does not exist.");
            return;
        }

        // One unhealthy class must not cost the whole roster. Add() builds a
        // live preview instance, which reaches deep into model/animation/weapon
        // loading - plenty of places for third-party data to throw. Unprotected,
        // the first such throw escapes to Start(), leaving the screen built but
        // empty: no classes, no selection, a dead Spawn button. Same
        // partial-failure principle as PhxScene.ImportInstances.
        foreach (var cl in team.UnitClasses)
        {
            try
            {
                Add(cl.Unit);
            }
            catch (System.Exception e)
            {
                Debug.LogError($"Unit class '{(cl.Unit != null ? cl.Unit.Name : "<null>")}' could not be " +
                               $"added to character selection and was skipped: {e}");
            }
        }

        // The hero, when the team has earned one and nobody has taken it. This
        // was a commented-out line referring to a variable that does not exist
        // in this scope, which is the whole reason heroes have never been
        // playable - the combat side of them has worked all along.
        //
        // Routed through GetAvailableHeroClass rather than reading HeroClass
        // directly, so PhxHeroRules decides: hero points, the unlock threshold,
        // one hero per team, and the slot being spent on death. The
        // AlwaysAllowHeroes testing override goes live with this, since it had
        // nothing to affect before.
        try
        {
            PhxClass hero = Match.GetAvailableHeroClass(teamNum);
            if (hero != null)
            {
                Add(hero);
            }
        }
        catch (System.Exception e)
        {
            Debug.LogError($"Hero class for team {teamNum} could not be added to " +
                           $"character selection and was skipped: {e}");
        }

        // An empty roster means every AddUnitClass call for this team failed to
        // resolve. Report it loudly and leave the list empty: the stock game
        // never offers you another faction's units, so borrowing one would hide
        // a data-loading fault behind gameplay that looks almost right. The
        // preceding "AddUnitClass: Could not find PhxClass '...'" warnings name
        // the classes that are actually missing.
        if (Items.Count == 0)
        {
            ReportEmptyRoster(teamNum);
        }
    }

    static readonly HashSet<int> ReportedEmptyRosters = new HashSet<int>();

    static void ReportEmptyRoster(int teamNum)
    {
        if (!ReportedEmptyRosters.Add(teamNum)) return;
        Debug.LogError($"Team {teamNum} has no spawnable unit class - every AddUnitClass call for it " +
                       "failed to resolve (see the warnings above for the class names). Character " +
                       "selection will be empty and spawning disabled until the side data those " +
                       "classes live in is loaded.");
    }

    public override void Clear()
    {
        CurrentSelection = null;

        // Destroy UI items
        foreach (var item in Items)
        {
            Destroy(item.gameObject);
        }
        Items.Clear();

        // Destroy unit preview instances. Isolated per preview: teardown of a
        // half-built instance can throw, and losing this loop midway would
        // leave previews alive and the lists inconsistent for the rebuild that
        // immediately follows.
        for (int i = 0; i < UnitPreviews.Count; ++i)
        {
            try
            {
                Scene.DestroyInstance(UnitPreviews[i].GetInstance());
            }
            catch (System.Exception e)
            {
                Debug.LogError($"Failed to destroy a character-select preview instance: {e}");
            }
        }
        UnitPreviews.Clear();
    }

    public void Add(PhxClass cl)
    {
        if (cl.ClassType != EEntityClassType.GameObjectClass)
        {
            Debug.LogError($"Cannot add odf class '{cl.Name}' as item to character selection!");
            return;
        }
         
        // CSP = Char Select Preview. A class whose model failed to import
        // still gets a list entry - one broken unit must not abort the whole
        // class list and leave the Spawn button with nothing selectable.
        IPhxControlableInstance preview = Scene.CreateInstance(cl, cl.Name+"_CSP" + nameCounter++, Vector3.zero, Quaternion.identity, false, Game.CharSelectTransform) as IPhxControlableInstance;
        if (preview != null)
        {
            preview.Fixate();
            UnitPreviews.Add(preview);
        }
        else
        {
            Debug.LogWarning($"Character select: no preview instance for class '{cl.Name}'!");
        }

        PhxCharacterItem item = Instantiate(ItemPrefab, ListContents);

        // Tracked from the moment it exists. Clear() destroys what's in Items,
        // so anything that throws between here and the end of this method would
        // otherwise strand a live UI object nothing owns.
        Items.Add(item);

        item.OnClicked += () =>
        {
            SetActive(item, cl, preview);
        };

        item.SetHeaderText(cl.LocalizedName);

        string detailText = "";
        PhxSoldier.ClassProperties soldier = cl as PhxSoldier.ClassProperties;
        if (soldier != null)
        {
            foreach (Dictionary<string, IPhxPropRef> section in soldier.Weapons)
            {
                if (section.TryGetValue("WeaponName", out IPhxPropRef nameVal))
                {
                    PhxProp<string> weapName = (PhxProp<string>)nameVal;
                    PhxClass weapClass = Scene.GetClass(weapName);
                    if(weapClass != null)
                    {
                        PhxProp<int> medalProp = weapClass.P.Get<PhxProp<int>>("MedalsTypeToUnlock");
                        if (medalProp != null && medalProp != 0)
                        {
                            // Skip medal/award weapons for display
                            continue;
                        }
                        detailText += weapClass.LocalizedName + '\n';
                    }
                }
            }
            item.SetDetailText(detailText);
        }

        if (CurrentSelection == null)
        {
            SetActive(item, cl, preview);
        }
        else
        {
            item.SetActive(false);
            preview?.GetInstance().gameObject.SetActive(false);
        }

        ReCalcItemSize();
    }

    void SetActive(PhxCharacterItem item, PhxClass cl, IPhxControlableInstance preview)
    {
        foreach (PhxCharacterItem it in Items)
        {
            it.SetActive(false);
        }
        item.SetActive(true);
        CurrentSelection = cl;

        foreach (PhxInstance inst in UnitPreviews)
        {
            inst.gameObject.SetActive(false);
        }

        if (preview != null)
        {
            preview.GetInstance().gameObject.SetActive(true);
            preview.PlayIntroAnim();
        }
    }

    void ReCalcItemSize()
    {
        // No entries means nothing to lay out - and dividing by Items.Count
        // below would hand every RectTransform a NaN size.
        if (Items.Count == 0) return;

        float availableHeight = ListContents.rect.height - (ItemSpace * (Items.Count - 1));
        float itemHeight = availableHeight / Items.Count;
        itemHeight = Mathf.Min(itemHeight, MaxItemHeight);

        for (int i = 0; i < Items.Count; ++i)
        {
            RectTransform trans = (RectTransform)Items[i].transform;
            trans.sizeDelta = new Vector2(ListContents.sizeDelta.x, itemHeight);
            trans.anchoredPosition = new Vector2(0f, i * -itemHeight + i * -ItemSpace);
        }
    }

    // Start is called before the first frame update
    void Start()
    {
        Debug.Assert(ItemPrefab    != null);
        Debug.Assert(ListContents  != null);
        Debug.Assert(BtnSpawn      != null);
        Debug.Assert(BtnSwitchTeam != null);
        Debug.Assert(BtnNextCamera != null);
        Debug.Assert(Map           != null);

        // This screen ships its own volume, authored to light the unit preview,
        // but the scene sets it to priority 0. The BF3 Legacy stack installs
        // global volumes at priority 100 (PhxModernLighting) and 90
        // (PhxGraphicsEnhancer), which therefore win and blow the preview model
        // out to a flat white silhouette. Character select is a full-screen UI
        // state, so its own volume should govern while it is up.
        Game.CharSelectPPVolume.priority = 200f;

        // For some reason, we have to trigger the volume in order
        // for it to be actually active...
        Game.CharSelectPPVolume.gameObject.SetActive(false);
        Game.CharSelectPPVolume.gameObject.SetActive(true);

        BtnSpawn.onClick.AddListener(SpawnClicked);
        BtnSwitchTeam.onClick.AddListener(SwitchTeamClicked);
        BtnNextCamera.onClick.AddListener(NextCameraClicked);
        Map.OnCPSelect += OnCPSelected;

        SelectOwnedCP();
        UpdateCharacterList();
    }

    // Post ownership changes while this screen is open, so keep the Spawn
    // button honest: disabled with a reason when the team holds nothing,
    // auto-selecting a post as soon as one is owned. A silently dead Spawn
    // button is indistinguishable from a broken game.
    float CPRecheckTimer;
    string SpawnLabelDefault;

    void Update()
    {
        CPRecheckTimer -= Time.deltaTime;
        if (CPRecheckTimer > 0f) return;
        CPRecheckTimer = 1f;

        if (SpawnCP == null || SpawnCP.Team != Match.Player.Team)
        {
            SpawnCP = null;
            SelectOwnedCP();
        }

        Text label = BtnSpawn.GetComponentInChildren<Text>();
        if (label != null && SpawnLabelDefault == null)
        {
            SpawnLabelDefault = label.text;
        }

        // CurrentSelection stays null when the team's roster failed to resolve
        // (see ReportEmptyRoster). There is genuinely nothing to spawn as, and
        // that needs to say so rather than sit there as a Spawn button that
        // silently does nothing.
        bool canSpawn = SpawnCP != null && CurrentSelection != null;
        BtnSpawn.interactable = canSpawn;
        if (label != null)
        {
            label.text = canSpawn ? SpawnLabelDefault
                        : SpawnCP == null ? "No command post available"
                        : "No unit available";
        }
    }

    void SelectOwnedCP()
    {
        PhxCommandpost[] cps = Scene.GetCommandPosts();
        for (int i = 0; i < cps.Length; ++i)
        {
            if (cps[i].Team == Match.Player.Team)
            {
                Map.SelectCP(cps[i]);
                break;
            }
        }
    }

    void OnCPSelected(PhxCommandpost cp)
    {
        if (Scene.GetCPCameraTransform(cp, out PhxTransform t))
        {
            Game.Camera.transform.position = t.Position;
            Game.Camera.transform.rotation = t.Rotation;
        }

        SpawnCP = cp;
    }
    
    void SpawnClicked()
    {
        // The selected post may have been captured while the menu was open.
        if (SpawnCP != null && SpawnCP.Team != Match.Player.Team)
        {
            SpawnCP = null;
            SelectOwnedCP();
        }

        if (CurrentSelection != null && SpawnCP != null)
        {
            Match.SpawnPlayer(CurrentSelection, SpawnCP);
        }
        else
        {
            Debug.LogWarning($"Spawn rejected: class={(CurrentSelection != null ? CurrentSelection.Name : "<none>")}, cp={(SpawnCP != null ? SpawnCP.name : "<none>")}");
            Game.PlayUISound(SoundLoader.Instance.LoadSound("ui_menuBack"), 1.2f);
        }
    }

    void SwitchTeamClicked()
    {
        Match.Player.Team = Match.Player.Team == 1 ? 2 : 1;
        UpdateCharacterList();

        PhxCommandpost cp = null;
        PhxCommandpost[] cps = Scene.GetCommandPosts();
        for (int i = 0; i < cps.Length; ++i)
        {
            cps[i].UpdateColor();
            cps[i].ChangeColorIcon();
            if (cps[i].Team == Match.Player.Team) { cp = cps[i]; }
        }

        if (cp != null)
        {
            Map.SelectCP(cp);
        } else
        {
            //Not sure what to do in this case
        }
    }

    void NextCameraClicked()
    {
        Camera.Fixed(Scene.GetNextCameraShot());
    }
}

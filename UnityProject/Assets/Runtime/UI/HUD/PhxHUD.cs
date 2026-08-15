using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

public class PhxHUD : PhxMenuInterface
{
    PhxGame GAME => PhxGame.Instance;
    PhxMatch Match => PhxGame.GetMatch();
    PhxTimerDB TimerDB => PhxGame.GetTimerDB();

    [Header("References")]
    public Text ReinforcementTeam1;
    public Text ReinforcementTeam2;
    public Text TimerDisplay;
    public Text VicDefTimerDisplay;
    public Text AmmoPrim;
    public PhxUIMap Map;
    public RawImage CaptureDisplay;
    public RawImage Crosshair;

    // Optional: built in code when the prefab has no slot wired, so the feed
    // works without a prefab edit.
    public Text ObjectiveFeed;

    [Header("Settings")]
    public float AimSpeed = 1f;

    Material CaptureMat;
    Material CrosshairMat;

    float AimFixation;


    public override void Clear()
    {
        
    }

    // Start is called before the first frame update
    void Start()
    {
        Debug.Assert(ReinforcementTeam1 != null);
        Debug.Assert(ReinforcementTeam2 != null);
        Debug.Assert(TimerDisplay       != null);
        Debug.Assert(VicDefTimerDisplay != null);
        Debug.Assert(AmmoPrim           != null);
        Debug.Assert(Map                != null);
        Debug.Assert(CaptureDisplay     != null);
        Debug.Assert(Crosshair          != null);

        ReinforcementTeam1.color = Match.GetTeamColor(1);
        ReinforcementTeam2.color = Match.GetTeamColor(2);

        CaptureMat = CaptureDisplay.materialForRendering;
        CaptureMat.SetTexture("_CaptureIcon", TextureLoader.Instance.ImportUITexture("hud_flag_timer"));

        CrosshairMat = Crosshair.materialForRendering;

        // Reticle size is taste, so it is a setting rather than a constant.
        //
        // The prefab default is 56 units against a 1920x1080 reference canvas -
        // about 5% of screen height. It was 128, which is nearly 12%: enormous
        // by shooter convention, where a reticle usually sits between 3% and
        // 6%. It cannot go much below this and stay useful, because this is not
        // a bare dot - the shader draws ammo and magazine as arcs around it,
        // and those stop being readable once the ring gets small.
        //
        // Zero means "leave the prefab alone", so there is only ever one source
        // of truth for the default.
        float crosshairSize = PhxBF3.Config.CrosshairSize;
        if (crosshairSize > 0f)
        {
            Crosshair.rectTransform.sizeDelta = new Vector2(crosshairSize, crosshairSize);
        }

        if (ObjectiveFeed == null)
        {
            ObjectiveFeed = CreateObjectiveFeedText();
        }

        if (ScopeOverlay == null)
        {
            BuildScopeOverlay();
        }

        BuildStatusPanel();
        BuildCombatFeedback();

        PhxHUDEvents.OnLocalPlayerDealtDamage += OnDealtDamage;
        PhxHUDEvents.OnLocalPlayerDamaged += OnDamageTaken;

        //Reticule.texture = TextureLoader.Instance.ImportUITexture("reticule_rifle");
    }

    void OnDestroy()
    {
        // Static events outlive this HUD - a map change would otherwise leave
        // the old instance subscribed and touching destroyed RectTransforms.
        PhxHUDEvents.OnLocalPlayerDealtDamage -= OnDealtDamage;
        PhxHUDEvents.OnLocalPlayerDamaged -= OnDamageTaken;
    }

    /// <summary>
    /// Top-centre objective text, matching the timer's font. Mission scripts
    /// have always published these messages; nothing drew them until now.
    /// </summary>
    Text CreateObjectiveFeedText()
    {
        GameObject obj = new GameObject("ObjectiveFeed", typeof(RectTransform));
        obj.transform.SetParent(TimerDisplay.transform.parent, false);

        RectTransform rect = (RectTransform)obj.transform;
        rect.anchorMin = new Vector2(0.5f, 1f);
        rect.anchorMax = new Vector2(0.5f, 1f);
        rect.pivot = new Vector2(0.5f, 1f);
        rect.anchoredPosition = new Vector2(0f, -70f);
        rect.sizeDelta = new Vector2(900f, 120f);

        Text text = obj.AddComponent<Text>();
        text.font = TimerDisplay.font;
        text.fontSize = Mathf.Max(14, TimerDisplay.fontSize - 4);
        text.alignment = TextAnchor.UpperCenter;
        text.color = GAME.Settings.ColorNeutral;
        text.raycastTarget = false;
        text.horizontalOverflow = HorizontalWrapMode.Wrap;
        text.verticalOverflow = VerticalWrapMode.Overflow;
        return text;
    }

    // ---------------------------------------------------------------------
    // Sniper scope
    //
    // Built in code, same as the objective feed above and for the same reason:
    // the prefab has no slot for it.
    //
    // Deliberately a vignette and crosshair drawn from primitives rather than
    // the stock com_1st_scope_universal model. The stock scope is a first
    // person mesh attached to the weapon, and there is no first-person weapon
    // rendering path here to hang it on - a flat overlay is what this codebase
    // can actually honour, and it is what the player reads anyway.
    // ---------------------------------------------------------------------

    RectTransform ScopeOverlay;
    Image ScopeVignette;

    void BuildScopeOverlay()
    {
        GameObject root = new GameObject("SniperScope", typeof(RectTransform));
        root.transform.SetParent(Crosshair.transform.parent, false);
        // Behind nothing in particular, but before the crosshair in the
        // hierarchy so the reticle would draw over it if both were ever shown.
        root.transform.SetSiblingIndex(Crosshair.transform.GetSiblingIndex());

        ScopeOverlay = (RectTransform)root.transform;
        ScopeOverlay.anchorMin = Vector2.zero;
        ScopeOverlay.anchorMax = Vector2.one;
        ScopeOverlay.offsetMin = Vector2.zero;
        ScopeOverlay.offsetMax = Vector2.zero;

        // The darkened surround. One stretched image tinted almost black: with
        // no scope texture to import, the readable cue is that everything
        // outside the sight picture goes dark.
        GameObject vignette = new GameObject("Vignette", typeof(RectTransform));
        vignette.transform.SetParent(ScopeOverlay, false);
        RectTransform vrect = (RectTransform)vignette.transform;
        vrect.anchorMin = Vector2.zero;
        vrect.anchorMax = Vector2.one;
        vrect.offsetMin = Vector2.zero;
        vrect.offsetMax = Vector2.zero;

        ScopeVignette = vignette.AddComponent<Image>();
        ScopeVignette.color = new Color(0f, 0f, 0f, 0.55f);
        ScopeVignette.raycastTarget = false;

        // Crosshair lines, four thin bars leaving a gap at the centre so the
        // target is never covered by the sight.
        AddScopeBar(new Vector2(0.5f, 0.5f), new Vector2(2f, 220f), new Vector2(0f, 150f));
        AddScopeBar(new Vector2(0.5f, 0.5f), new Vector2(2f, 220f), new Vector2(0f, -150f));
        AddScopeBar(new Vector2(0.5f, 0.5f), new Vector2(220f, 2f), new Vector2(150f, 0f));
        AddScopeBar(new Vector2(0.5f, 0.5f), new Vector2(220f, 2f), new Vector2(-150f, 0f));

        ScopeOverlay.gameObject.SetActive(false);
    }

    void AddScopeBar(Vector2 anchor, Vector2 size, Vector2 offset)
    {
        GameObject bar = new GameObject("ScopeBar", typeof(RectTransform));
        bar.transform.SetParent(ScopeOverlay, false);

        RectTransform rect = (RectTransform)bar.transform;
        rect.anchorMin = anchor;
        rect.anchorMax = anchor;
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.sizeDelta = size;
        rect.anchoredPosition = offset;

        Image img = bar.AddComponent<Image>();
        img.color = new Color(0f, 0f, 0f, 0.85f);
        img.raycastTarget = false;
    }

    void UpdateScope()
    {
        if (ScopeOverlay == null) return;

        PhxSoldier soldier = Match.Player.Pawn?.GetInstance() as PhxSoldier;
        bool scoped = soldier != null && soldier.IsScoped();

        if (ScopeOverlay.gameObject.activeSelf != scoped)
        {
            ScopeOverlay.gameObject.SetActive(scoped);
        }

        // The reticle and the scope are alternatives, never both: the scope
        // has its own sight, and leaving the ammo ring floating in the middle
        // of it would be worse than either alone.
        if (Crosshair.enabled == scoped)
        {
            Crosshair.enabled = !scoped;
        }
    }

    // ---------------------------------------------------------------------
    // Status panel: health, stamina, clips and the secondary item row.
    //
    // Built in code rather than added to the prefab, same as the objective
    // feed above: the prefab ships without these slots, and a HUD that
    // null-checks its way around missing wiring is easier to keep working than
    // one that needs every scene re-authored in lockstep with the code.
    // ---------------------------------------------------------------------

    RectTransform StatusPanel;
    RectTransform HealthFill;
    RectTransform StaminaFill;
    Image StaminaBackground;
    Text ClipsText;
    Text ItemsText;

    // BF2's health ramp: green while healthy, through amber, to red when
    // nearly dead.
    static readonly Color HealthHigh = new Color(0.30f, 0.85f, 0.30f);
    static readonly Color HealthMid = new Color(0.95f, 0.80f, 0.20f);
    static readonly Color HealthLow = new Color(0.90f, 0.20f, 0.15f);

    const float BarWidth = 220f;
    const float HealthBarHeight = 16f;
    const float StaminaBarHeight = 7f;

    // ---------------------------------------------------------------------
    // Stock artwork.
    //
    // The original game's HUD textures are mounted with the level and reachable
    // by name (the codebase already pulls hud_flag_icon, hud_flag_timer,
    // bf2_buttons_*). Where one resolves we use it; where it doesn't we keep
    // the code-drawn bar, so an incomplete or modded install degrades instead
    // of showing an empty HUD.
    // ---------------------------------------------------------------------

    /// <summary>
    /// First stock texture of <paramref name="candidates"/> that loads, or null.
    /// </summary>
    /// <remarks>
    /// printError:false matters - a missing name here is an expected outcome,
    /// not a fault, and the loader would otherwise log an error per candidate
    /// per element.
    /// </remarks>
    static Texture2D ResolveStockTexture(params string[] candidates)
    {
        for (int i = 0; i < candidates.Length; ++i)
        {
            Texture2D tex = TextureLoader.Instance.ImportUITexture(candidates[i], false);
            if (tex != null)
            {
                return tex;
            }
        }
        return null;
    }

    /// <summary>
    /// Log the HUD-ish texture names the mounted levels actually contain.
    /// </summary>
    /// <remarks>
    /// One line, once per session. The candidate lists below are informed
    /// guesses; this reports the ground truth so they can be replaced with the
    /// real names rather than extended by trial and error.
    /// </remarks>
    static bool ReportedStockTextures;

    void ReportAvailableStockTextures()
    {
        if (ReportedStockTextures) return;
        ReportedStockTextures = true;

        PhxEnvironment env = PhxGame.GetEnvironment();
        if (env == null) return;

        List<string> names = env.GetLoadedTextureNames("hud", "reticule", "icon", "bar", "ammo", "health");
        Debug.Log($"[HUD] {names.Count} candidate stock HUD texture(s) mounted: " +
                  (names.Count > 0 ? string.Join(", ", names) : "<none>"));
    }

    void BuildStatusPanel()
    {
        ReportAvailableStockTextures();

        StatusPanel = CreateRect("StatusPanel", TimerDisplay.transform.parent);
        StatusPanel.anchorMin = new Vector2(1f, 0f);
        StatusPanel.anchorMax = new Vector2(1f, 0f);
        StatusPanel.pivot = new Vector2(1f, 0f);
        StatusPanel.anchoredPosition = new Vector2(-40f, 40f);
        StatusPanel.sizeDelta = new Vector2(BarWidth, 120f);

        // Health bar, bottom of the stack.
        //
        // These are the game's real texture names, read out of ingame.lvl
        // rather than guessed: BF2 has no dedicated health-bar art. It draws
        // every meter with one neutral fill (hud_jetpack_and_energybar_fill)
        // over an empty trough (..._blankfill), tinted per meter - which is why
        // the same pair serves health, stamina and the jetpack.
        RectTransform healthBg = CreateBar("HealthBar", StatusPanel, 0f, BarWidth, HealthBarHeight,
                                           new Color(0f, 0f, 0f, 0.55f),
                                           "hud_jetpack_and_energybar_blankfill", "hud_black");
        HealthFill = CreateFill("HealthFill", healthBg, HealthHigh,
                                "hud_jetpack_and_energybar_fill", "hud_white");

        // Stamina bar sits directly above it, thinner.
        RectTransform staminaBg = CreateBar("StaminaBar", StatusPanel, HealthBarHeight + 4f,
                                            BarWidth, StaminaBarHeight, new Color(0f, 0f, 0f, 0.55f),
                                            "hud_jetpack_and_energybar_blankfill", "hud_black");
        StaminaBackground = staminaBg.GetComponent<Image>();
        StaminaFill = CreateFill("StaminaFill", staminaBg, new Color(0.55f, 0.80f, 1f),
                                 "hud_jetpack_and_energybar_fill", "hud_white");

        // Ammo clips, right-aligned above the bars.
        ClipsText = CreateLabel("Clips", StatusPanel, HealthBarHeight + StaminaBarHeight + 10f,
                                TextAnchor.LowerRight, TimerDisplay.fontSize);

        // Secondary items (grenades, detpacks, mines) above that.
        ItemsText = CreateLabel("Items", StatusPanel, HealthBarHeight + StaminaBarHeight + 40f,
                                TextAnchor.LowerRight, Mathf.Max(12, TimerDisplay.fontSize - 6));

        BuildWeaponIcon();
    }

    Image WeaponIcon;

    /// <summary>
    /// BF2 shows a silhouette of the equipped weapon beside the ammo readout.
    /// Built only if the artwork is there - an empty box would be worse than
    /// no box.
    /// </summary>
    void BuildWeaponIcon()
    {
        RectTransform rect = CreateRect("WeaponIcon", StatusPanel);
        rect.anchorMin = new Vector2(0f, 0f);
        rect.anchorMax = new Vector2(0f, 0f);
        rect.pivot = new Vector2(1f, 0f);
        rect.anchoredPosition = new Vector2(-8f, 0f);
        rect.sizeDelta = new Vector2(64f, 64f);

        WeaponIcon = rect.gameObject.AddComponent<Image>();
        WeaponIcon.raycastTarget = false;
        WeaponIcon.preserveAspect = true;
        WeaponIcon.gameObject.SetActive(false);
    }

    // The weapon an icon was last resolved for, so the lookup isn't repeated
    // every frame for the same gun.
    string WeaponIconFor;

    /// <summary>
    /// Stock icons that exist for equippable items, keyed by a substring of the
    /// weapon's odf name.
    /// </summary>
    /// <remarks>
    /// Read out of ingame.lvl, not guessed. BF2 ships no per-blaster icon - only
    /// the throwables and a handful of specials have artwork - so the icon is
    /// simply hidden for ordinary guns rather than showing a placeholder.
    /// </remarks>
    static readonly (string Match, string Texture)[] ItemIcons =
    {
        ("thermaldetonator", "hud_thermaldetonator"),
        ("grenade",          "hud_thermaldetonator"),
        ("detpack",          "hud_detpack_plunger"),
        ("bomb",             "hud_proton_bomb"),
        ("repair",           "hud_active_repair_icon"),
        ("fusioncutter",     "hud_active_repair_icon"),
        ("ammo",             "item_powerup_ammo"),
        ("health",           "item_powerup_health"),
    };

    void UpdateWeaponIcon(IPhxWeapon weap)
    {
        if (WeaponIcon == null) return;

        PhxInstance inst = weap?.GetInstance();
        string weaponName = inst != null ? inst.name : null;
        if (weaponName == WeaponIconFor) return;
        WeaponIconFor = weaponName;

        if (string.IsNullOrEmpty(weaponName))
        {
            WeaponIcon.gameObject.SetActive(false);
            return;
        }

        string lower = weaponName.ToLowerInvariant();
        Texture2D tex = null;
        for (int i = 0; i < ItemIcons.Length && tex == null; ++i)
        {
            if (lower.Contains(ItemIcons[i].Match))
            {
                tex = ResolveStockTexture(ItemIcons[i].Texture);
            }
        }

        if (tex != null)
        {
            WeaponIcon.sprite = Sprite.Create(tex, new Rect(0f, 0f, tex.width, tex.height),
                                              new Vector2(0.5f, 0.5f));
            WeaponIcon.color = Color.white;
        }
        WeaponIcon.gameObject.SetActive(tex != null);
    }

    static RectTransform CreateRect(string name, Transform parent)
    {
        GameObject obj = new GameObject(name, typeof(RectTransform));
        obj.transform.SetParent(parent, false);
        return (RectTransform)obj.transform;
    }

    /// <summary>A bar's background, anchored to the panel's bottom-left.</summary>
    /// <remarks>
    /// Uses the stock frame artwork when one of <paramref name="stockCandidates"/>
    /// resolves; otherwise falls back to a plain quad. An Image with no sprite
    /// draws a solid colour, which is all a bar strictly needs.
    /// </remarks>
    static RectTransform CreateBar(string name, Transform parent, float y, float width, float height,
                                   Color color, params string[] stockCandidates)
    {
        RectTransform rect = CreateRect(name, parent);
        rect.anchorMin = new Vector2(0f, 0f);
        rect.anchorMax = new Vector2(0f, 0f);
        rect.pivot = new Vector2(0f, 0f);
        rect.anchoredPosition = new Vector2(0f, y);
        rect.sizeDelta = new Vector2(width, height);

        Image img = rect.gameObject.AddComponent<Image>();
        img.raycastTarget = false;

        Texture2D stock = ResolveStockTexture(stockCandidates);
        if (stock != null)
        {
            img.sprite = Sprite.Create(stock, new Rect(0f, 0f, stock.width, stock.height),
                                       new Vector2(0.5f, 0.5f));
            img.color = Color.white;   // don't tint the game's own artwork
        }
        else
        {
            img.color = color;
        }
        return rect;
    }

    /// <summary>
    /// The coloured part of a bar. Stretches to its parent and is scaled by
    /// moving anchorMax.x, so the fill grows from the left edge.
    /// </summary>
    /// <remarks>
    /// Unlike a frame, a fill IS tinted even when it uses stock artwork - BF2's
    /// bar fill is a neutral gradient that the game colours per meter, which is
    /// how one texture serves health, energy and the jetpack.
    /// </remarks>
    static RectTransform CreateFill(string name, Transform parent, Color color,
                                    params string[] stockCandidates)
    {
        RectTransform rect = CreateRect(name, parent);
        rect.anchorMin = Vector2.zero;
        rect.anchorMax = Vector2.one;
        rect.pivot = new Vector2(0f, 0.5f);
        rect.offsetMin = new Vector2(1f, 1f);
        rect.offsetMax = new Vector2(-1f, -1f);

        Image img = rect.gameObject.AddComponent<Image>();
        img.color = color;
        img.raycastTarget = false;

        Texture2D stock = ResolveStockTexture(stockCandidates);
        if (stock != null)
        {
            img.sprite = Sprite.Create(stock, new Rect(0f, 0f, stock.width, stock.height),
                                       new Vector2(0.5f, 0.5f));
        }
        return rect;
    }

    Text CreateLabel(string name, Transform parent, float y, TextAnchor anchor, int fontSize)
    {
        RectTransform rect = CreateRect(name, parent);
        rect.anchorMin = new Vector2(0f, 0f);
        rect.anchorMax = new Vector2(1f, 0f);
        rect.pivot = new Vector2(0f, 0f);
        rect.anchoredPosition = new Vector2(0f, y);
        rect.sizeDelta = new Vector2(0f, 28f);

        Text text = rect.gameObject.AddComponent<Text>();
        text.font = TimerDisplay.font;
        text.fontSize = fontSize;
        text.alignment = anchor;
        text.color = GAME.Settings.ColorNeutral;
        text.raycastTarget = false;
        text.horizontalOverflow = HorizontalWrapMode.Overflow;
        text.verticalOverflow = VerticalWrapMode.Overflow;
        return text;
    }

    static void SetFill(RectTransform fill, float fraction)
    {
        if (fill == null) return;
        Vector2 max = fill.anchorMax;
        max.x = Mathf.Clamp01(fraction);
        fill.anchorMax = max;
    }

    void UpdateStatusPanel()
    {
        if (StatusPanel == null) return;

        // Only soldiers have health/stamina bars in BF2 - riding a vehicle
        // replaces this corner of the HUD, so hide it rather than show a stale
        // reading from the body we left behind.
        PhxSoldier soldier = Match.Player.Pawn?.GetInstance() as PhxSoldier;

        // A vehicle shows health and BOOST in the same corner. It used to hide
        // the panel outright when not on foot, which was right while vehicles
        // had no boost bar to show and is not any more.
        if (soldier == null)
        {
            UpdateVehicleStatus(Match.Player.Pawn?.GetInstance() as PhxVehicle);
            return;
        }

        StatusPanel.gameObject.SetActive(true);

        float health = soldier.HealthFraction;
        SetFill(HealthFill, health);
        HealthFill.GetComponent<Image>().color = health > 0.5f
            ? Color.Lerp(HealthMid, HealthHigh, (health - 0.5f) * 2f)
            : Color.Lerp(HealthLow, HealthMid, health * 2f);

        // A class with no EnergyBar can't sprint-drain at all; drawing a
        // permanently full bar for it would just be noise.
        bool hasStamina = soldier.MaxEnergy > 0f;
        StaminaBackground.gameObject.SetActive(hasStamina);
        if (hasStamina)
        {
            SetFill(StaminaFill, soldier.EnergyFraction);
        }

        UpdateAmmoText(soldier);
        UpdateItemsText(soldier);
    }

    /// <summary>
    /// Health and boost for whatever the player is driving.
    /// </summary>
    /// <remarks>
    /// The stamina bar is reused for boost energy rather than adding a second
    /// one: it occupies the same place, means the same thing (a spendable
    /// resource that refills), and a vehicle has no stamina of its own to
    /// compete with it.
    /// </remarks>
    void UpdateVehicleStatus(PhxVehicle vehicle)
    {
        if (vehicle == null)
        {
            StatusPanel.gameObject.SetActive(false);
            return;
        }

        StatusPanel.gameObject.SetActive(true);

        float maxHealth = vehicle.GetMaxHealth();
        float health = maxHealth > 0f ? Mathf.Clamp01(vehicle.GetHealth() / maxHealth) : 0f;
        SetFill(HealthFill, health);
        HealthFill.GetComponent<Image>().color = health > 0.5f
            ? Color.Lerp(HealthMid, HealthHigh, (health - 0.5f) * 2f)
            : Color.Lerp(HealthLow, HealthMid, health * 2f);

        // Only vehicles that actually have a boost get the bar.
        bool hasBoost = vehicle.CanBoost;
        StaminaBackground.gameObject.SetActive(hasBoost);
        if (hasBoost)
        {
            SetFill(StaminaFill, vehicle.GetEnergyFraction());
        }

        ClipsText.text = "";
        AmmoPrim.text = "-";
    }

    void UpdateAmmoText(PhxSoldier soldier)
    {
        // The icon tracks the SELECTED ITEM, not the primary gun: BF2 only
        // ships artwork for throwables and specials, so pointing this at the
        // rifle would leave it permanently blank.
        UpdateWeaponIcon(soldier.GetSecondaryWeapon());

        IPhxWeapon weap = soldier.GetPrimaryWeapon();
        if (weap == null)
        {
            ClipsText.text = "";
            return;
        }

        // "rounds in the magazine | whole magazines left in reserve", which is
        // what BF2 shows: reserve rounds are only meaningful in clip-sized
        // chunks because a reload always tops up a full magazine.
        int magSize = weap.GetMagazineSize();
        int clips = magSize > 0 ? weap.GetAvailableAmmo() / magSize : 0;
        ClipsText.text = $"{weap.GetMagazineAmmo()} | {clips}";
    }

    // Rebuilt every frame from the soldier's secondary channel, but only the
    // string is recomputed - no allocation of UI objects.
    readonly List<string> ItemEntries = new List<string>();

    void UpdateItemsText(PhxSoldier soldier)
    {
        ItemEntries.Clear();

        int count = soldier.GetWeaponCount(1);
        int equipped = soldier.GetEquippedWeaponIdx(1);
        for (int i = 0; i < count; ++i)
        {
            IPhxWeapon item = soldier.GetWeapon(1, i);
            if (item == null) continue;

            PhxInstance inst = item.GetInstance();
            string label = inst != null ? ShortItemName(inst.name) : "?";
            int ammo = item.GetTotalAmmo();

            // The selected item is marked, since the secondary fire key cycles
            // through them and you need to know which one is up.
            ItemEntries.Add(i == equipped ? $"[{label} {ammo}]" : $"{label} {ammo}");
        }

        ItemsText.text = ItemEntries.Count > 0 ? string.Join("   ", ItemEntries) : "";
    }

    // Cache: the trim below is pure string work on a name that never changes,
    // and this runs for every item every frame.
    readonly Dictionary<string, string> ShortItemNames = new Dictionary<string, string>();

    /// <summary>
    /// Turn an odf-derived instance name like "cis_weap_inf_thermaldetonator"
    /// into something that fits the HUD.
    /// </summary>
    string ShortItemName(string rawName)
    {
        if (ShortItemNames.TryGetValue(rawName, out string cached)) return cached;

        string shortened = rawName;
        int lastUnderscore = shortened.LastIndexOf('_');
        if (lastUnderscore >= 0 && lastUnderscore < shortened.Length - 1)
        {
            shortened = shortened.Substring(lastUnderscore + 1);
        }
        if (shortened.Length > 12)
        {
            shortened = shortened.Substring(0, 12);
        }

        shortened = shortened.ToUpperInvariant();
        ShortItemNames[rawName] = shortened;
        return shortened;
    }

    // ---------------------------------------------------------------------
    // Combat feedback: hit marker and damage direction indicator.
    // ---------------------------------------------------------------------

    RectTransform HitMarker;
    RectTransform DamageIndicator;

    float HitMarkerTimer;
    bool HitMarkerFatal;

    float DamageIndicatorTimer;
    Vector3 DamageFromPosition;

    const float HitMarkerDuration = 0.35f;
    const float DamageIndicatorDuration = 1.2f;

    void BuildCombatFeedback()
    {
        Transform canvas = TimerDisplay.transform.parent;

        // Hit marker: four short ticks around the centre, rotated into an X.
        HitMarker = CreateRect("HitMarker", canvas);
        HitMarker.anchorMin = new Vector2(0.5f, 0.5f);
        HitMarker.anchorMax = new Vector2(0.5f, 0.5f);
        HitMarker.pivot = new Vector2(0.5f, 0.5f);
        HitMarker.anchoredPosition = Vector2.zero;
        HitMarker.sizeDelta = new Vector2(40f, 40f);
        for (int i = 0; i < 4; ++i)
        {
            RectTransform tick = CreateRect("Tick" + i, HitMarker);
            tick.anchorMin = new Vector2(0.5f, 0.5f);
            tick.anchorMax = new Vector2(0.5f, 0.5f);
            tick.pivot = new Vector2(0.5f, 0.5f);
            tick.sizeDelta = new Vector2(10f, 2f);
            tick.anchoredPosition = new Vector2(
                (i < 2 ? -1f : 1f) * 12f,
                (i % 2 == 0 ? -1f : 1f) * 12f);
            tick.localRotation = Quaternion.Euler(0f, 0f, (i == 0 || i == 3) ? 45f : -45f);

            Image img = tick.gameObject.AddComponent<Image>();
            img.color = Color.white;
            img.raycastTarget = false;
        }
        HitMarker.gameObject.SetActive(false);

        // Damage direction: a bar offset above centre, rotated around the
        // screen centre to point at whatever hit us.
        DamageIndicator = CreateRect("DamageIndicator", canvas);
        DamageIndicator.anchorMin = new Vector2(0.5f, 0.5f);
        DamageIndicator.anchorMax = new Vector2(0.5f, 0.5f);
        DamageIndicator.pivot = new Vector2(0.5f, 0.5f);
        DamageIndicator.anchoredPosition = Vector2.zero;
        DamageIndicator.sizeDelta = new Vector2(240f, 240f);

        RectTransform arc = CreateRect("Arc", DamageIndicator);
        arc.anchorMin = new Vector2(0.5f, 0.5f);
        arc.anchorMax = new Vector2(0.5f, 0.5f);
        arc.pivot = new Vector2(0.5f, 0.5f);
        arc.anchoredPosition = new Vector2(0f, 110f);
        arc.sizeDelta = new Vector2(90f, 8f);

        Image arcImg = arc.gameObject.AddComponent<Image>();
        arcImg.color = GAME.Settings.ColorEnemy;
        arcImg.raycastTarget = false;

        DamageIndicator.gameObject.SetActive(false);
    }

    void OnDealtDamage(bool fatal)
    {
        HitMarkerTimer = HitMarkerDuration;
        HitMarkerFatal = fatal;
    }

    void OnDamageTaken(Vector3 sourcePosition)
    {
        DamageIndicatorTimer = DamageIndicatorDuration;
        DamageFromPosition = sourcePosition;
    }

    void UpdateCombatFeedback()
    {
        if (HitMarker == null || DamageIndicator == null) return;

        // Hit marker: fades out, and turns the enemy colour on a kill.
        if (HitMarkerTimer > 0f)
        {
            HitMarkerTimer -= Time.deltaTime;
            HitMarker.gameObject.SetActive(true);

            Color color = HitMarkerFatal ? GAME.Settings.ColorEnemy : Color.white;
            color.a = Mathf.Clamp01(HitMarkerTimer / HitMarkerDuration);
            foreach (Image tick in HitMarker.GetComponentsInChildren<Image>())
            {
                tick.color = color;
            }
        }
        else if (HitMarker.gameObject.activeSelf)
        {
            HitMarker.gameObject.SetActive(false);
        }

        // Damage direction: point the arc at the attacker, in screen space
        // relative to where the camera is looking rather than world north.
        if (DamageIndicatorTimer > 0f && Match.Player.Pawn != null)
        {
            DamageIndicatorTimer -= Time.deltaTime;
            DamageIndicator.gameObject.SetActive(true);

            Transform cam = GAME.Camera != null ? GAME.Camera.transform : null;
            Vector3 self = Match.Player.Pawn.GetInstance().transform.position;
            Vector3 toSource = DamageFromPosition - self;
            toSource.y = 0f;

            if (cam != null && toSource.sqrMagnitude > 0.001f)
            {
                Vector3 forward = cam.forward;
                forward.y = 0f;

                // Signed angle from where we're facing to where it came from;
                // the arc starts at the top of the screen, so a positive angle
                // (source to our right) rotates it clockwise, hence negated.
                float angle = Vector3.SignedAngle(forward.normalized, toSource.normalized, Vector3.up);
                DamageIndicator.localRotation = Quaternion.Euler(0f, 0f, -angle);
            }

            foreach (Image img in DamageIndicator.GetComponentsInChildren<Image>())
            {
                Color color = GAME.Settings.ColorEnemy;
                color.a = Mathf.Clamp01(DamageIndicatorTimer / DamageIndicatorDuration);
                img.color = color;
            }
        }
        else if (DamageIndicator.gameObject.activeSelf)
        {
            DamageIndicator.gameObject.SetActive(false);
        }
    }

    // Localization goes through native lookups, so cache what we resolve -
    // this runs every frame.
    readonly Dictionary<string, string> LocalizedObjectives = new Dictionary<string, string>();

    // How long a posted objective message stays on screen.
    const float ObjectiveMessageLifetime = 8f;
    const int MaxObjectiveLines = 3;

    void UpdateObjectiveFeed()
    {
        if (ObjectiveFeed == null) return;

        IReadOnlyList<PhxMatch.PhxObjectiveMessage> feed = Match.GetObjectiveFeed();
        float now = Match.GetMatchTime();

        ObjectiveLines.Clear();
        for (int i = feed.Count - 1; i >= 0 && ObjectiveLines.Count < MaxObjectiveLines; --i)
        {
            PhxMatch.PhxObjectiveMessage msg = feed[i];
            if (now - msg.PostedAt > ObjectiveMessageLifetime) break;  // older ones only get older
            if (msg.TeamIdx != 0 && msg.TeamIdx != Match.Player.Team) continue;

            if (!LocalizedObjectives.TryGetValue(msg.LocalizeKey, out string localized))
            {
                localized = PhxGame.GetEnvironment()?.GetLocalized(msg.LocalizeKey);
                if (string.IsNullOrEmpty(localized))
                {
                    localized = msg.LocalizeKey;
                }
                LocalizedObjectives[msg.LocalizeKey] = localized;
            }
            ObjectiveLines.Add(localized);
        }

        if (ObjectiveLines.Count == 0)
        {
            ObjectiveFeed.text = "";
            return;
        }

        // Newest last, so the feed reads top-down in the order posted.
        ObjectiveLines.Reverse();
        ObjectiveFeed.text = string.Join("\n", ObjectiveLines);
    }

    readonly List<string> ObjectiveLines = new List<string>();

    // Update is called once per frame
    void Update()
    {
        ReinforcementTeam1.text = Match.GetReinforcementCount(1).ToString();
        ReinforcementTeam2.text = Match.GetReinforcementCount(2).ToString();

        UpdateObjectiveFeed();
        UpdateScope();
        UpdateStatusPanel();
        UpdateCombatFeedback();

        if (Match.Player.Pawn != null)
        {
            Vector3 playerPos = Match.Player.Pawn.GetInstance().transform.position;
            Map.MapOffset.x = -playerPos.x;
            Map.MapOffset.y = -playerPos.z;

            IPhxWeapon weapPrim = Match.Player.Pawn.GetPrimaryWeapon();
            if (weapPrim != null)
            {
                int ammo = weapPrim.GetMagazineAmmo();
                int magazine = weapPrim.GetMagazineSize();

                float reloadProgress = weapPrim.GetReloadProgress();
                if (reloadProgress < 1f)
                {
                    int reload = Mathf.Min(magazine - ammo, weapPrim.GetAvailableAmmo());
                    ammo += (int)(reload * reloadProgress);
                }

                CrosshairMat.SetFloat("_Ammo", ammo);
                CrosshairMat.SetFloat("_Magazin", magazine);

                AmmoPrim.text = weapPrim.GetTotalAmmo().ToString();
            }
            else
            {
                CrosshairMat.SetFloat("_Ammo", 0);
                AmmoPrim.text = "-";
            }

            PhxInstance aim = Match.Player.GetAimObject();
            if (aim != null)
            {
                if (aim.Team != 0)
                {
                    AimFixation = Mathf.Clamp01(AimFixation + Time.deltaTime / AimSpeed);
                }
                else
                {
                    AimFixation = Mathf.Clamp01(AimFixation - Time.deltaTime / AimSpeed);
                }
                CrosshairMat.SetColor("_Color", Match.GetTeamColor(aim.Team));
            }
            else
            {
                AimFixation = Mathf.Clamp01(AimFixation - Time.deltaTime / AimSpeed);
                CrosshairMat.SetColor("_Color", Color.white);
            }

            CrosshairMat.SetFloat("_Fixation", AimFixation);
        }

        PhxCommandpost cp = Match.Player.CapturePost;
        bool displayCapture = cp != null && ((cp.Team != Match.Player.Team) || !Match.IsFriend(cp.CaptureTeam, Match.Player.Team));

        CaptureDisplay.gameObject.SetActive(displayCapture);
        if (displayCapture)
        {
            Color color = Match.GetTeamColor(cp.CaptureTeam);
            float progress = cp.GetCaptureProgress();
            if (cp.CaptureToNeutral)
            {
                color = Match.GetTeamColor(cp.Team);
                progress = 1f - progress;
            }

            // remap for more consistent HUD icon fill
            progress = Mathf.Lerp(0.1f, 0.95f, progress);

            CaptureMat.SetFloat("_CaptureProgress", progress);
            CaptureMat.SetColor("_CaptureColor", color);
            CaptureMat.SetFloat("_CaptureDispute", cp.CaptureDisputed ? 1f : 0f);
        }

        TimerDisplay.gameObject.SetActive(Match.ShowTimer.HasValue);
        if (Match.ShowTimer.HasValue)
        {
            TimerDB.GetTimer(Match.ShowTimer.Value, out var timer);

            TimeSpan t = TimeSpan.FromSeconds(timer.Time);
            TimerDisplay.text = string.Format("{0:D2}:{1:D2}", t.Minutes, t.Seconds);
            TimerDisplay.color = GAME.Settings.ColorNeutral;
        }

        (int? timerIdx, PhxMatch.PhxTeam.TimerDisplay display) = Match.GetTeamTimer(Match.Player.Team);
        VicDefTimerDisplay.gameObject.SetActive(timerIdx.HasValue);
        if (timerIdx.HasValue)
        {
            Color color = GAME.Settings.ColorNeutral;
            if (display == PhxMatch.PhxTeam.TimerDisplay.Defeat)
            {
                color = GAME.Settings.ColorEnemy;
            }
            else if (display == PhxMatch.PhxTeam.TimerDisplay.Victory)
            {
                color = GAME.Settings.ColorFriendly;
            }

            TimerDB.GetTimer(timerIdx.Value, out var timer);

            TimeSpan t = TimeSpan.FromSeconds(timer.Time);
            VicDefTimerDisplay.text = string.Format("{0:D2}:{1:D2}", t.Minutes, t.Seconds);
            VicDefTimerDisplay.color = color;
        }
    }
}

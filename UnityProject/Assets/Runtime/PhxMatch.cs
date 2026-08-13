using System;
using System.Collections.Generic;
using UnityEngine;

public class PhxMatch
{
    static PhxGame GAME => PhxGame.Instance;
    static PhxEnvironment ENV => PhxGame.GetEnvironment();
    static PhxScene RTS => PhxGame.GetScene();
    static PhxCamera CAM => PhxGame.GetCamera();
    static PhxTimerDB TDB => PhxGame.GetTimerDB();


    public int? ShowTimer = null;

    public PhxPlayerController Player { get; private set; }

    List<PhxPawnController> AIControllers = new List<PhxPawnController>();


    public enum PhxPlayerState
    {
        CharacterSelection,
        Spawned,
        FreeCam
    }

    public PhxPlayerState PlayerST { get; private set; } = PhxPlayerState.CharacterSelection;

    bool LVLsLoaded = false;
    bool AvailablePauseMenu = false;
    int NameCounter;


    public class PhxUnitClass
    {
        public PhxClass Unit = null;
        public int CountMin = 0;
        public int CountMax = int.MaxValue;

        public override int GetHashCode()
        {
            return Unit.GetHashCode();
        }
    }

    // TODO: verify if enough
    public const int MAX_TEAMS = 20;

    public class PhxTeam
    {
        public enum TimerDisplay
        {
            Victory, Defeat
        }

        public string Name = "UNKNOWN TEAM";
        public float Aggressiveness = 1.0f;
        public Texture2D Icon = null;
        public GameObject Hologram;
        public int UnitCount = 0;
        public int ReinforcementCount = -1; // default is infinite

        // True once this team has been given a positive reinforcement pool.
        // Distinguishes "ran out of tickets" (a defeat) from "never used
        // tickets" (space assault, and any mode scored by objectives).
        public bool SpentReinforcements;
        public float SpawnDelay = 1.0f;

        /// <summary>Fraction of SpawnDelay the wait may vary by, so a squad
        /// does not arrive in lockstep. Set by the mission script.</summary>
        public float SpawnDelaySpread = 0f;
        public HashSet<PhxUnitClass> UnitClasses = new HashSet<PhxUnitClass>();
        public PhxClass HeroClass = null;
        public bool[] Friends = new bool[MAX_TEAMS];

        // Scoreboard. BF2 shows reinforcements ("tickets") as the headline
        // number and score/kills/deaths per player; Points is the objective
        // score the mission scripts drive through AddTeamPoints, which is what
        // CTF and assault use instead of ticket bleed.
        public int Points = 0;
        public int Kills = 0;
        public int Deaths = 0;

        // Reinforcements lost per second while behind on command posts. The
        // mission script drives this through SetBleedRate; PhxMatch applies a
        // conquest default when nothing does.
        public float BleedRate = 0f;

        public int? Timer = null;
        public TimerDisplay TimerMode = TimerDisplay.Defeat;
    }

    public PhxTeam[] Teams { get; private set; } = new PhxTeam[MAX_TEAMS];

    Queue<(int, string, int, int)> TeamUnits = new Queue<(int, string, int, int)>();
    Queue<(int, string)> TeamHeroClass = new Queue<(int, string)>();
    Queue<(int, string)> TeamIcon = new Queue<(int, string)>();

    AudioClip UIBack;


    public PhxMatch()
    {
        for (int i = 0; i < MAX_TEAMS; ++i)
        {
            Teams[i] = new PhxTeam();

            // Everyone if befriended with themselfs
            Teams[i].Friends[i] = true;
        }

        GAME.OnRemoveMenu += OnRemoveMenu;

        Player = new PhxPlayerController();

        // Music/VO lives as long as the match: mission scripts configure it
        // during ScriptInit, which runs before the world is even built.
        PhxMusicManager.Create();
    }

    public Color GetTeamColor(int teamId)
    {
        if (teamId == 0)
        {
            return GAME.Settings.ColorNeutral;
        }
        else if (teamId == Player.Team || IsFriend(Player.Team, teamId))
        {
            return GAME.Settings.ColorFriendly;
        }
        else if (teamId >= 3)
        {
            return GAME.Settings.ColorLocals;
        }
        return GAME.Settings.ColorEnemy;
    }

    // Team numbers are 1-based (0 = neutral), the Teams array is 0-based -
    // same convention as getTeamName below.
    public GameObject GetTeamHologram(int teamId)
    {
        if (!CheckTeamIdx(--teamId)) return null;
        return Teams[teamId].Hologram;
    }

    public void SetTeamHologram(int teamId, GameObject hologram)
    {
        if (!CheckTeamIdx(--teamId)) return;
        Teams[teamId].Hologram = hologram;
    }

    public void SetPlayerState(PhxPlayerState st)
    {
        if (PlayerST == st) return;

        PlayerST = st;
        if (PlayerST == PhxPlayerState.CharacterSelection)
        {
            EnterCharacterSelection();
        }
        else if (PlayerST == PhxPlayerState.Spawned)
        {
            GAME.RemoveMenu();
            CAM.Follow(Player.Pawn);
            ShowHUD();

            Cursor.visible = false;
            Cursor.lockState = CursorLockMode.Locked;
        }
        else if (PlayerST == PhxPlayerState.FreeCam)
        {
            // Free Cam abandons the body. Leaving it alive turned every later
            // Spawn click into a silent "already spawned" rejection.
            IPhxControlableInstance pawn = Player.Pawn;
            if (pawn != null)
            {
                pawn.UnAssign();
                RTS.DestroyInstance(pawn.GetInstance());
            }

            CAM.Free();
            GAME.RemoveMenu();
            RemoveHUD();
        }
    }

    // The character-selection entry actions, callable regardless of the
    // current state. KillPlayer needs them even when PlayerST is already
    // CharacterSelection (e.g. Respawn pressed from the pause menu that was
    // opened over the spawn screen), where SetPlayerState early-returns.
    void EnterCharacterSelection()
    {
        ShowCharacterSelection();
        CAM.Fixed(RTS.GetNextCameraShot());
        RemoveHUD();
    }

    // Since we're doing multithreaded loading, calling Lua stuff like "AddUnitClass" in "ScriptInit()"
    // will yield no results, since the .lvl files containing those classes might still load at that point.
    // Workaround: Queue those calls and apply them after all .lvl files have been loaded
    public void ApplySchedule()
    {
        while (TeamUnits.Count > 0)
        {
            (int teamIdx, string className, int countMin, int countMax) = TeamUnits.Dequeue();
            PhxClass odf = RTS.GetClass(className);
            if (odf != null)
            {
                Teams[teamIdx].UnitClasses.Add(new PhxUnitClass { Unit = odf, CountMin = countMin, CountMax = countMax });
            }
            else
            {
                Debug.LogWarning($"AddUnitClass: '{className}' is unavailable - {RTS.DescribeMissingClass(className)}.");
            }
        }

        while (TeamHeroClass.Count > 0)
        {
            (int teamIdx, string className) = TeamHeroClass.Dequeue();
            PhxClass odf = RTS.GetClass(className);
            if (odf != null)
            {
                Teams[teamIdx].HeroClass = odf;
            }
            else
            {
                Debug.LogWarning($"SetHeroClass: Could not find PhxClass '{className}'!");
            }
        }

        while (TeamIcon.Count > 0)
        {
            (int teamIdx, string texName) = TeamIcon.Dequeue();
            Teams[teamIdx].Icon = TextureLoader.Instance.ImportUITexture(texName);
        }

        LVLsLoaded = true;
    }

    public void Destroy()
    {
        CAM.Fixed();

        GAME.OnMapLoaded -= StartMatch;
        GAME.OnRemoveMenu -= OnRemoveMenu;

        IPhxControlableInstance pawn = Player.Pawn;
        if (pawn != null)
        {
            pawn.UnAssign();
            RTS.DestroyInstance(pawn.GetInstance());
        }

        for (int i = 0; i < AIControllers.Count; ++i)
        {
            PhxPawnController c = AIControllers[i];
            IPhxControlableInstance aiPawn = c?.Pawn;
            if (aiPawn != null && aiPawn.GetInstance() != null)
            {
                aiPawn.UnAssign();
                RTS.DestroyInstance(aiPawn.GetInstance());
            }
        }
        AIControllers.Clear();

        PhxMusicManager.Destroy();
    }

    /// <summary>Seconds since the match started ticking.</summary>
    public float GetMatchTime() => MatchTime;

    // ================= Objective feed and map markers ==================
    //
    // Mission scripts announce objectives and drop map markers constantly.
    // These hold what the scripts publish so the HUD and map can render it;
    // the rendering itself belongs with the shell UI work, but the state has
    // to exist now or the calls that produce it abort their script.

    public struct PhxObjectiveMessage
    {
        public string LocalizeKey;
        public int TeamIdx;      // 0 = everyone
        public float PostedAt;
    }

    public struct PhxMapMarker
    {
        public string Kind;      // "region" or "class"
        public string Name;      // region name or class name
        public int TeamIdx;
        public string IconName;
    }

    readonly List<PhxObjectiveMessage> ObjectiveFeed = new List<PhxObjectiveMessage>();
    readonly List<PhxMapMarker> MapMarkers = new List<PhxMapMarker>();

    public IReadOnlyList<PhxObjectiveMessage> GetObjectiveFeed() => ObjectiveFeed;
    public IReadOnlyList<PhxMapMarker> GetMapMarkers() => MapMarkers;

    public void PostObjectiveMessage(string localizeKey, int teamIdx)
    {
        if (string.IsNullOrEmpty(localizeKey)) return;

        ObjectiveFeed.Add(new PhxObjectiveMessage
        {
            LocalizeKey = localizeKey,
            TeamIdx = teamIdx,
            PostedAt = MatchTime,
        });

        // Keep it bounded - a long round posts a lot of these.
        if (ObjectiveFeed.Count > 64)
        {
            ObjectiveFeed.RemoveRange(0, ObjectiveFeed.Count - 64);
        }
    }

    public void AddMapMarker(string kind, string name, int teamIdx, string iconName)
    {
        if (string.IsNullOrEmpty(name)) return;
        MapMarkers.Add(new PhxMapMarker
        {
            Kind = kind, Name = name, TeamIdx = teamIdx, IconName = iconName
        });
    }

    public void RemoveMapMarkers(string kind, string name)
    {
        MapMarkers.RemoveAll(m => m.Kind == kind &&
            (string.IsNullOrEmpty(name) || m.Name == name));
    }

    /// <summary>
    /// Whether score should count only human players. Set by the mission
    /// script; read by scoring when hero/deathmatch modes need it.
    /// </summary>
    public bool OnlyCountHumanKills;
    public bool OnlyCountHumanDeaths;

    float MatchTime;

    public void Tick(float deltaTime)
    {
        if (!IsMatchOver)
        {
            MatchTime += deltaTime;
        }

        Player.Tick(deltaTime);
        for (int i = 0; i < AIControllers.Count; ++i)
        {
            AIControllers[i].Tick(deltaTime);
        }

        TickAISpawn(deltaTime);
        TickBleed(deltaTime);
        TickAudioCues();
        PhxMusicManager.Instance?.Tick(deltaTime);

        if (AvailablePauseMenu && Player.CancelPressed)
        {
            if (GAME.IsMenuActive(GAME.PauseMenuPrefab))
            {
                GAME.RemoveMenu();
            }
            else
            {
                ShowMenu(GAME.PauseMenuPrefab);
            }

            if (UIBack == null)
            {
                UIBack = SoundLoader.Instance.LoadSound("ui_menuBack");
            }
            GAME.PlayUISound(UIBack, 1.2f);
        }
    }

    public void KillPlayer()
    {
        CAM.Fixed();

        IPhxControlableInstance pawn = Player.Pawn;
        if (pawn != null)
        {
            pawn.UnAssign();
            RTS.DestroyInstance(pawn.GetInstance());
        }

        // Outside the null check on purpose. When the player is killed in
        // combat the pawn has already released the controller by the time we
        // get here, and gating the state change on a live pawn left the player
        // permanently stuck watching their own corpse with no spawn menu.
        //
        // Run the entry actions unconditionally: SetPlayerState no-ops when the
        // state is already CharacterSelection, but the pause menu may have
        // replaced the spawn screen, which then must be re-shown.
        PlayerST = PhxPlayerState.CharacterSelection;
        EnterCharacterSelection();
    }

    public IPhxControlableInstance SpawnPlayer(PhxClass cl, PhxCommandpost cp)
    {
        GetSpawnPoint(cp, out Vector3 pos, out Quaternion rot);
        return SpawnPlayer(cl, pos, rot);
    }

    public IPhxControlableInstance SpawnPlayer(PhxClass cl, Vector3 position, Quaternion rotation)
    {
        // A live pawn is not a reason to refuse: the player can reach this via
        // Free Cam or menu paths that never killed them, and silently rejecting
        // the Spawn click is how the spawn screen "stops working". Replace it.
        if (Player.Pawn != null)
        {
            IPhxControlableInstance old = Player.Pawn;
            old.UnAssign();
            RTS.DestroyInstance(old.GetInstance());
        }

        PhxInstance created = RTS.CreateInstance(cl, "player" + NameCounter++, position, rotation, false);
        if (created == null)
        {
            Debug.LogError($"Failed to create an instance of spawn class '{cl.Name}'!");
            return null;
        }
        IPhxControlableInstance pawn = created as IPhxControlableInstance;
        if (pawn == null)
        {
            Debug.LogError($"Given spawn class '{cl.Name}' is not a IPhxControlableInstance!");
            return null;
        }

        // The controller knows the team; the pawn didn't. PhxPlayerController
        // sets its own Team, but nothing ever wrote it onto the spawned
        // instance, so the player's soldier stayed team 0 (neutral) - which is
        // what produced "Team index '-1' is out of range" the moment the player
        // stepped into a command post region, since IsFriend decrements to a
        // 0-based index. Set it before Assign so Init sees the right team.
        pawn.GetInstance().Team.Set(Player.Team);

        if (string.IsNullOrEmpty(Player.DisplayName))
        {
            Player.DisplayName = "Player";
        }

        pawn.Assign(Player);
        if (PlayerST == PhxPlayerState.Spawned)
        {
            // Already in the spawned state (we just replaced a live pawn), so
            // SetPlayerState would no-op and leave the camera tracking the
            // body we destroyed.
            CAM.Follow(Player.Pawn);
        }
        else
        {
            SetPlayerState(PhxPlayerState.Spawned);
        }

        Debug.Log($"Spawned player at pos: {position} - rot: {rotation}");

        // Mission scripts hook this to drive mode logic; space assault in
        // particular registers here during ScriptPreInit.
        PhxLuaEvents.Invoke(PhxLuaEvents.Event.OnCharacterSpawn, RTS.GetInstanceIndex(pawn.GetInstance()));

        return pawn;
    }

    public IPhxControlableInstance SpawnAI<T>(PhxClass cl, PhxCommandpost cp, int team = 0) where T : PhxPawnController, new()
    {
        GetSpawnPoint(cp, out Vector3 pos, out Quaternion rot);
        return SpawnAI<T>(cl, pos, rot, team);
    }

    /// <summary>
    /// Spawn transform for a command post. Prefers the post's authored spawn
    /// path; mod maps frequently ship posts without one, and refusing to spawn
    /// there makes the map unplayable, so fall back to the post itself.
    /// </summary>
    void GetSpawnPoint(PhxCommandpost cp, out Vector3 position, out Quaternion rotation)
    {
        Vector3 basePos = cp.transform.position;
        rotation = cp.transform.rotation;

        SWBFPath path = cp.SpawnPath.Get();

        // The original game's own mod-tools docs ("Getting Started", command
        // post section) have the level designer hand-place "at least 6 or 8"
        // spawn nodes around each post, each with its own authored position
        // and facing, specifically so a squad doesn't stack on one point. That
        // is the entire anti-stacking mechanism - there is no documented
        // runtime random offset layered on top of a chosen node, and the
        // com_bldg_controlzone.odf template exposes only SpawnPath, no scatter
        // radius. An earlier version of this method added one anyway
        // (SpawnScatterRadius, up to 4m in a random direction), and on a
        // platform map like Coruscant's Jedi Temple grounds that was enough to
        // walk a perfectly good node past the platform edge into open air,
        // where the void rescue below then grabbed whatever it found - a
        // skydome, a backdrop ship, empty space. Re-rolling to a DIFFERENT
        // node on retry is what the level design already provides for
        // avoiding stacked spawns; use that instead of inventing an offset.
        //
        // A candidate on top of (or inside) an existing soldier gets resolved
        // by PhysX depenetration, which launches somebody - usually straight
        // through the floor. Prefer a node with clearance; if every node
        // nearby is crowded, take the last one anyway.
        int soldierMask = PhxLayers.Soldier;
        position = basePos;
        for (int attempt = 0; attempt < 5; ++attempt)
        {
            Vector3 candidate = basePos;
            if (path != null)
            {
                SWBFPath.Node node = path.GetRandom();
                candidate = node.Position;
                // The node's own rotation is used too - it was being
                // discarded for Quaternion.identity, which left everyone
                // facing world +Z regardless of how the post was laid out.
                if (node.Rotation != default)
                {
                    rotation = node.Rotation;
                }
            }

            position = SettleOnGround(candidate, basePos);
            if (attempt == 4) break;
            if (!Physics.CheckSphere(position + Vector3.up * 0.95f, 1.2f, soldierMask, QueryTriggerInteraction.Ignore))
            {
                break;
            }
        }

        rotation = UprightSpawnRotation(rotation, cp.transform.rotation);
    }

    /// <summary>
    /// Strip pitch and roll from a spawn rotation, keeping only its facing.
    /// </summary>
    /// <remarks>
    /// Soldiers always spawn standing in BF2; pitch and roll on a spawn point
    /// are never meaningful. Enforcing that here makes a whole class of bug
    /// unreproducible rather than merely fixed.
    ///
    /// An inverted spawn is not a cosmetic problem: a soldier's capsule is
    /// centred at local +0.9, so upside down it hangs BELOW the transform
    /// origin, under the floor. Nothing is then under the origin to stand on,
    /// the grounded check fails, the soldier enters the Jump state - and
    /// MoveRotation, which would right it, only runs in Stand/Crouch/Sprint.
    /// With FreezeRotationX|Z holding the inversion in place, it falls out of
    /// the world permanently. That is exactly what a bad quaternion conversion
    /// on the authored path nodes produced.
    ///
    /// So whatever convention the source data or a future converter change
    /// hands us, the worst it can now cost is a wrong facing.
    /// </remarks>
    static Quaternion UprightSpawnRotation(Quaternion candidate, Quaternion fallback)
    {
        Vector3 forward = candidate * Vector3.forward;
        forward.y = 0f;

        // Straight up or straight down flattens to nothing - no facing can be
        // recovered from it, so use the post's own.
        if (forward.sqrMagnitude < 1e-4f)
        {
            forward = fallback * Vector3.forward;
            forward.y = 0f;

            if (forward.sqrMagnitude < 1e-4f)
            {
                return Quaternion.identity;
            }
        }

        return Quaternion.LookRotation(forward.normalized, Vector3.up);
    }

    /// <summary>
    /// Put a spawn point on the floor beneath it, without ever selecting a
    /// surface above the spawner's head.
    /// </summary>
    /// <remarks>
    /// An earlier version cast from 50 m up over a 200 m range and took the
    /// first hit. Under any roof, ceiling or ship hull - which is most interior
    /// command posts - the first hit is the structure ABOVE the spawn, so
    /// soldiers were placed on rooftops.
    ///
    /// Authored spawn points are already at the right height, so the ray starts
    /// just above the point and only looks a short way down: enough to settle
    /// onto terrain, never enough to find a ceiling. If nothing is underneath,
    /// the authored position is kept - it is better data than a raycast.
    /// </remarks>
    Vector3 SettleOnGround(Vector3 candidate, Vector3 fallback)
    {
        const float StartAbove = 1.5f;   // below head height, above small bumps
        const float MaxSettle = 8f;      // terrain slack, not a storey

        // Only surfaces a soldier genuinely collides with count as ground -
        // BF2 ships ordnance-only and vehicle-only collision meshes that a
        // soldier falls straight through. Shared with PhxSoldier's own grounded
        // check, so a point found solid here reads as solid there too.
        int mask = PhxLayers.SoldierGround;

        if (Physics.Raycast(candidate + Vector3.up * StartAbove, Vector3.down,
                            out RaycastHit hit, StartAbove + MaxSettle,
                            mask, QueryTriggerInteraction.Ignore))
        {
            return hit.point + Vector3.up * 0.1f;
        }

        // The candidate has no floor - over a ledge, or inside geometry. Try
        // the post's own fallback position before giving up on it.
        if (candidate != fallback &&
            Physics.Raycast(fallback + Vector3.up * StartAbove, Vector3.down,
                            out RaycastHit baseHit, StartAbove + MaxSettle,
                            mask, QueryTriggerInteraction.Ignore))
        {
            return baseHit.point + Vector3.up * 0.1f;
        }

        // Nothing within reach underfoot. The authored point may be BELOW the
        // world entirely: on hot1g_con a post resolved to y = -44.9 while the
        // terrain's lowest vertex is y = -18.9, so every soldier spawning
        // there - player and AI alike - appeared 26 m under the map with
        // nothing beneath them and simply fell forever.
        //
        // Sweep the whole vertical column from high above to find the real
        // surface at this x/z, and only use it when it sits ABOVE the authored
        // point. That rescues an underground spawn without disturbing a
        // legitimately elevated one (a post on a landing platform keeps its
        // authored height rather than being dragged down to the ground below).
        // Take the LOWEST surface that is still above the spawn - that is the
        // floor it should be standing on. A single downward ray would return
        // the topmost hit and park an interior spawn on the roof instead.
        const float ColumnTop = 1000f;
        const float ColumnRange = 4000f;
        RaycastHit[] column = Physics.RaycastAll(
            new Vector3(fallback.x, ColumnTop, fallback.z), Vector3.down,
            ColumnRange, mask, QueryTriggerInteraction.Ignore);

        float bestY = float.MaxValue;
        bool found = false;
        for (int i = 0; i < column.Length; ++i)
        {
            float y = column[i].point.y;
            if (y > fallback.y + 0.5f && y < bestY)
            {
                bestY = y;
                found = true;
            }
        }

        if (found)
        {
            if (ReportedBadSpawns.Add(fallback))
            {
                Debug.LogWarning($"Spawn point {fallback} is below the world; lifting it onto the " +
                                 $"lowest surface above it (y={bestY:F1}).");
            }
            return new Vector3(fallback.x, bestY + 0.1f, fallback.z);
        }

        // Nothing above, below, or anywhere in the column at this x/z: the
        // authored point is returned completely unmodified, with no collider
        // anywhere near it to stand on. Anyone spawning here falls forever, so
        // this stays reported even though the rest of the spawn tracing is gone.
        if (ReportedBadSpawns.Add(fallback))
        {
            Debug.LogWarning($"No standable ground found anywhere near spawn point {fallback} " +
                             $"(short probe, fallback probe, and {ColumnRange}m column all empty) - " +
                             "using the authored position as-is. Anything spawning here will fall. " +
                             DescribeRejectedSurfaces(fallback));
        }
        return fallback;
    }

    /// <summary>
    /// Names any collider near a failed spawn that is NOT standable, with its
    /// layer.
    /// </summary>
    /// <remarks>
    /// "No ground here" and "there is ground here but soldiers fall through it"
    /// look identical from the spawn's point of view and need opposite fixes -
    /// one is missing map geometry, the other is a collision-layer problem. BF2
    /// ships ordnance-only and vehicle-only collision meshes precisely so
    /// soldiers pass through them, so naming the layer of whatever IS there
    /// distinguishes the two cases without another debugging round.
    /// </remarks>
    static string DescribeRejectedSurfaces(Vector3 point)
    {
        Collider[] near = Physics.OverlapSphere(point, 2f, ~0, QueryTriggerInteraction.Ignore);
        if (near.Length == 0)
        {
            return "Nothing at all within 2m - the map geometry is genuinely missing here.";
        }

        HashSet<string> layers = new HashSet<string>();
        for (int i = 0; i < near.Length; ++i)
        {
            layers.Add(LayerMask.LayerToName(near[i].gameObject.layer));
        }
        return $"There ARE colliders within 2m, on layer(s): {string.Join(", ", layers)} - " +
               "none of which a soldier collides with.";
    }

    /// <summary>
    /// Spawn points already reported as broken. Both warnings above describe a
    /// fault in the map's own data, so they can't be fixed at runtime and only
    /// need saying once - and the AI respawn loop re-settles the same points
    /// many times a second.
    /// </summary>
    readonly HashSet<Vector3> ReportedBadSpawns = new HashSet<Vector3>();

    /// <summary>
    /// Classes whose instantiation has already been reported as failing. The
    /// respawn loop retries the same class several times a second, so without
    /// this one broken unit buries the console.
    /// </summary>
    readonly HashSet<string> ReportedSpawnFailures = new HashSet<string>();

    void ReportSpawnFailure(PhxClass cl, Exception e)
    {
        string name = cl == null ? "<null>" : cl.Name;
        if (!ReportedSpawnFailures.Add(name)) return;

        Debug.LogError($"Spawn class '{name}' failed to instantiate and will be " +
                       $"skipped for the rest of the match: {e}");
    }

    public IPhxControlableInstance SpawnAI<T>(PhxClass cl, Vector3 position, Quaternion rotation, int team = 0) where T : PhxPawnController, new()
    {
        IPhxControlableInstance player;
        try
        {
            player = RTS.CreateInstance(cl, "AI" + NameCounter++, position, rotation, false) as IPhxControlableInstance;
        }
        catch (Exception e)
        {
            // Instantiation walks the whole model/material/texture import chain,
            // so one malformed asset used to throw all the way out of Tick and
            // silently kill every system that ticks after the AI spawn -
            // including the player's own controller. Losing one bot is a far
            // better outcome than losing the frame.
            ReportSpawnFailure(cl, e);
            return null;
        }

        if (player == null)
        {
            Debug.LogError($"Given spawn class '{cl.Name}' is not a IPhxControlableInstance!");
            return null;
        }

        // Team must be on the instance before the controller takes possession.
        // PhxBF3AIController re-reads it every Tick so it would self-correct a
        // frame later, but anything that runs at Init time - team colouring,
        // command post registration - would have seen team 0 (neutral) first.
        if (team > 0)
        {
            player.GetInstance().Team.Set(team);
        }

        PhxPawnController controller = new T();
        controller.Team = team;

        // BF2's scoreboard lists bots by their unit name ("Clone Trooper"),
        // not an anonymous id, so take the class's localized name.
        controller.DisplayName = string.IsNullOrEmpty(cl.LocalizedName) ? cl.Name : cl.LocalizedName;

        AIControllers.Add(controller);
        player.Assign(controller);
        return player;
    }

    /// <summary>
    /// Give an existing (dead-pawn) controller a fresh body. Reusing the
    /// controller keeps its scoreboard entry and stops the controller list -
    /// which is ticked every frame - from growing with every respawn wave.
    /// </summary>
    IPhxControlableInstance RespawnAI(PhxPawnController controller, PhxClass cl, PhxCommandpost cp)
    {
        GetSpawnPoint(cp, out Vector3 pos, out Quaternion rot);

        IPhxControlableInstance pawn = RTS.CreateInstance(cl, "AI" + NameCounter++, pos, rot, false) as IPhxControlableInstance;
        if (pawn == null)
        {
            Debug.LogError($"Given spawn class '{cl.Name}' is not a IPhxControlableInstance!");
            return null;
        }

        if (controller.Team > 0)
        {
            pawn.GetInstance().Team.Set(controller.Team);
        }

        controller.DisplayName = string.IsNullOrEmpty(cl.LocalizedName) ? cl.Name : cl.LocalizedName;
        (controller as PhxBF3AIController)?.ResetForRespawn();
        pawn.Assign(controller);
        return pawn;
    }

    PhxPawnController FindIdleAIController(int teamNum)
    {
        for (int i = 0; i < AIControllers.Count; ++i)
        {
            PhxPawnController c = AIControllers[i];
            if (c == null || c.Team != teamNum) continue;
            if (c.Pawn == null || c.Pawn.GetInstance() == null) return c;
        }
        return null;
    }

    // ================= AI population =================================
    //
    // Nothing drove AI spawning before: SpawnAI existed but was only ever
    // called from the animation test scene, so live matches contained no AI at
    // all. This keeps each team topped up to the unit count its mission script
    // asked for, drawing classes from AddUnitClass and spawning at posts the
    // team currently holds - which is what makes it follow the game mode, since
    // conquest/CTF/assault all express themselves through post ownership.

    readonly float[] AISpawnTimers = new float[MAX_TEAMS];

    // Batch filling. A team more than this far below strength is treated as
    // "not yet deployed" rather than "taking casualties", and is filled in
    // groups instead of one body at a time.
    const int BatchFillThreshold = 4;

    // Cap per tick so a 64-a-side fill is spread over a few frames - creating
    // sixty soldiers in one frame is a visible hitch.
    const int MaxSpawnsPerTick = 6;

    // Delay between fill batches. Short, because the point is to get the battle
    // started, not to meter it.
    const float BatchFillInterval = 0.25f;

    // "I don't see any enemies" can mean they never spawned, spawned and died,
    // or spawned at a post on the far side of the map and are still walking.
    // Those need different fixes, so report the census periodically instead of
    // guessing which one it is.
    float AICensusTimer;

    void TickAISpawn(float deltaTime)
    {
        // A decided round should stop feeding bodies into it.
        if (IsMatchOver) return;

        PhxScene scene = PhxGame.GetScene();
        PhxCommandpost[] posts = scene?.GetCommandPosts();
        if (posts == null || posts.Length == 0) return;

        AICensusTimer -= deltaTime;
        if (AICensusTimer <= 0f)
        {
            AICensusTimer = 10f;
            ReportAICensus(posts);
        }

        // Team NUMBERS are 1-based everywhere they are observable - command
        // posts, pawn controllers, Lua - with 0 meaning neutral. The Teams
        // array is 0-based, which is why every public accessor here does
        // '--teamIdx'. Mixing the two silently pairs one team's roster with
        // another team's command posts, so keep the loop in team numbers and
        // convert only when indexing.
        for (int teamNum = 1; teamNum <= MAX_TEAMS; ++teamNum)
        {
            int slot = teamNum - 1;

            PhxTeam team = Teams[slot];
            if (team == null || team.UnitClasses.Count == 0) continue;

            // A mission that called AllowAISpawn(team, false) is describing a
            // side that gets no reinforcements - the whole shape of a "hold
            // out" scenario. Ignoring it turns those levels into a symmetric
            // fight.
            if (!PhxAIDirectives.IsSpawnAllowed(teamNum)) continue;

            AISpawnTimers[slot] -= deltaTime;
            if (AISpawnTimers[slot] > 0f) continue;

            // The player occupies one of their own team's slots.
            int target = team.UnitCount;
            if (Player != null && Player.Team == teamNum) target -= 1;
            if (target <= 0) continue;

            if (team.ReinforcementCount == 0) continue;

            int live = CountLiveAI(teamNum);
            int deficit = target - live;
            if (deficit <= 0) continue;

            // How many to field this tick. Trickling one unit per SpawnDelay is
            // fine for a stock 8-a-side skirmish but leaves a 64-a-side map
            // nearly empty for minutes - the battle never gets going. Fill hard
            // while badly under strength, then settle into the authored cadence
            // so mid-round reinforcement still feels like reinforcement.
            int batch = deficit > BatchFillThreshold
                ? Mathf.Min(deficit, MaxSpawnsPerTick)
                : 1;

            int spawned = 0;
            for (int n = 0; n < batch; ++n)
            {
                if (team.ReinforcementCount == 0) break;

                PhxCommandpost cp = PickSpawnPost(posts, teamNum);
                PhxClass unit = cp != null ? PickUnitClass(team, teamNum) : null;
                if (unit == null)
                {
                    // No held post or no usable class: back off rather than
                    // retry every frame, and say which, since a team that never
                    // fields anyone is otherwise invisible.
                    ReportSpawnBlocked(teamNum, cp == null
                        ? "holds no command post to spawn at"
                        : "has no unit class under its CountMax");
                    break;
                }

                PhxPawnController idle = FindIdleAIController(teamNum);
                IPhxControlableInstance ai = idle != null
                    ? RespawnAI(idle, unit, cp)
                    : SpawnAI<PhxBF3AIController>(unit, cp, teamNum);
                if (ai == null) break;

                if (team.ReinforcementCount > 0) team.ReinforcementCount--;
                spawned++;
            }

            if (spawned == 0)
            {
                AISpawnTimers[slot] = 2f;
                continue;
            }

            // While filling, come back quickly so the map populates in seconds
            // rather than minutes. Once at strength, respect the script's own
            // spawn cadence.
            AISpawnTimers[slot] = spawned > 1
                ? BatchFillInterval
                : NextSpawnInterval(team);
        }
    }

    /// <summary>
    /// The wait before this team's next AI arrives, jittered by the spread the
    /// mission script asked for.
    /// </summary>
    static float NextSpawnInterval(PhxTeam team)
    {
        float delay = Mathf.Max(team.SpawnDelay, 0.1f);
        if (team.SpawnDelaySpread <= 0f) return delay;

        float jitter = delay * team.SpawnDelaySpread;
        return Mathf.Max(delay + UnityEngine.Random.Range(-jitter, jitter), 0.1f);
    }

    /// <summary>
    /// One line per fielded team: how many AI are alive, how many the mission
    /// script asked for, reinforcements left, posts held, and how far the
    /// closest one is from the player. Distance is the useful part - AI that
    /// exist but are 300m away look identical to no AI at all from the ground.
    /// </summary>
    void ReportAICensus(PhxCommandpost[] posts)
    {
        Vector3 playerPos = Vector3.zero;
        bool havePlayer = Player?.Pawn?.GetInstance() != null;
        if (havePlayer)
        {
            playerPos = Player.Pawn.GetInstance().transform.position;
        }

        for (int teamNum = 1; teamNum <= MAX_TEAMS; ++teamNum)
        {
            PhxTeam team = Teams[teamNum - 1];
            if (team == null || team.UnitClasses.Count == 0) continue;

            int held = 0;
            for (int i = 0; i < posts.Length; ++i)
            {
                if (posts[i] != null && posts[i].Team == teamNum) held++;
            }

            float nearest = float.MaxValue;
            for (int i = 0; havePlayer && i < AIControllers.Count; ++i)
            {
                PhxPawnController c = AIControllers[i];
                if (c == null || c.Team != teamNum) continue;
                PhxInstance inst = c.Pawn?.GetInstance();
                if (inst == null) continue;
                nearest = Mathf.Min(nearest, Vector3.Distance(playerPos, inst.transform.position));
            }

            string near = nearest < float.MaxValue ? $"{nearest:F0}m" : "n/a";
            Debug.Log($"[BF3Legacy] AI census - team {teamNum}: " +
                      $"{CountLiveAI(teamNum)} alive / {team.UnitCount} wanted, " +
                      $"{team.ReinforcementCount} reinforcements, {held} post(s) held, nearest to player {near}");
        }
    }

    readonly HashSet<int> ReportedSpawnBlocks = new HashSet<int>();

    void ReportSpawnBlocked(int teamNum, string reason)
    {
        if (!ReportedSpawnBlocks.Add(teamNum)) return;
        Debug.LogWarning($"[BF3Legacy] Team {teamNum} is not fielding AI: {reason}.");
    }

    int CountLiveAI(int teamIdx)
    {
        int count = 0;
        for (int i = 0; i < AIControllers.Count; ++i)
        {
            PhxPawnController c = AIControllers[i];
            // Pawn is an interface reference, so a destroyed pawn is only
            // caught by going through GetInstance() for Unity's null check.
            if (c == null || c.Pawn == null || c.Pawn.GetInstance() == null) continue;
            if (c.Team == teamIdx) count++;
        }
        return count;
    }

    /// <summary>A random command post the team holds, or null if it holds none.</summary>
    /// <summary>
    /// Which of our posts to field the next reinforcement at.
    /// </summary>
    /// <remarks>
    /// Weighted toward the front: a post close to ground the enemy holds gets
    /// several times the chance of one deep in safe territory. Picking
    /// uniformly spread bodies evenly across the map, which sounds fair but
    /// means losing a post produces no response - reinforcements kept appearing
    /// far from the fighting and walking the whole way. Weighting by proximity
    /// to the nearest enemy post is what turns a captured post into a
    /// counter-attack.
    ///
    /// Still a reservoir sample, so no temporary list per tick - just a
    /// weighted one.
    /// </remarks>
    PhxCommandpost PickSpawnPost(PhxCommandpost[] posts, int teamIdx)
    {
        PhxCommandpost chosen = null;
        float totalWeight = 0f;

        foreach (PhxCommandpost cp in posts)
        {
            if (cp == null || cp.Team != teamIdx) continue;

            float weight = 1f + FrontlineWeight(posts, cp, teamIdx);
            totalWeight += weight;

            if (UnityEngine.Random.value < weight / totalWeight)
            {
                chosen = cp;
            }
        }
        return chosen;
    }

    /// <summary>
    /// Extra spawn weight for a post near enemy-held ground. 0 when nothing
    /// hostile is anywhere near it.
    /// </summary>
    float FrontlineWeight(PhxCommandpost[] posts, PhxCommandpost ours, int teamIdx)
    {
        const float ContestRange = 150f;   // beyond this a post is "rear area"
        const float MaxBonus = 3f;

        float nearest = float.MaxValue;
        for (int i = 0; i < posts.Length; ++i)
        {
            PhxCommandpost other = posts[i];
            if (other == null || other.Team == teamIdx) continue;

            float d = Vector3.Distance(ours.transform.position, other.transform.position);
            if (d < nearest) nearest = d;
        }

        if (nearest >= ContestRange) return 0f;
        return MaxBonus * (1f - nearest / ContestRange);
    }

    /// <summary>
    /// Next unit class to field. Classes below their CountMin come first so the
    /// mission's required mix fills out before extras; CountMax is a hard cap.
    /// </summary>
    PhxClass PickUnitClass(PhxTeam team, int teamIdx)
    {
        PhxClass fallback = null;
        foreach (PhxUnitClass uc in team.UnitClasses)
        {
            if (uc.Unit == null) continue;

            int inUse = CountLiveAIOfClass(teamIdx, uc.Unit);
            if (inUse >= uc.CountMax) continue;
            if (inUse < uc.CountMin) return uc.Unit;
            if (fallback == null) fallback = uc.Unit;
        }
        return fallback;
    }

    int CountLiveAIOfClass(int teamIdx, PhxClass cl)
    {
        int count = 0;
        for (int i = 0; i < AIControllers.Count; ++i)
        {
            PhxPawnController c = AIControllers[i];
            if (c == null || c.Pawn == null) continue;
            PhxInstance inst = c.Pawn.GetInstance();
            if (inst == null || c.Team != teamIdx) continue;
            if (inst.GetClassRef() == cl) count++;
        }
        return count;
    }

    public (int?, PhxTeam.TimerDisplay) GetTeamTimer(int teamIdx)
    {
        if (!CheckTeamIdx(--teamIdx) || !Teams[teamIdx].Timer.HasValue)
        {
            return (null, PhxTeam.TimerDisplay.Defeat);
        }

        TDB.GetTimer(Teams[teamIdx].Timer.Value, out PhxTimerDB.PhxTimer timer);
        if (!timer.InUse)
        {
            Teams[teamIdx].Timer = null;
            Teams[teamIdx].TimerMode = PhxTeam.TimerDisplay.Defeat;
        }

        return (Teams[teamIdx].Timer, Teams[teamIdx].TimerMode);
    }

    // ====================================================================================
    // Lua API start
    // ====================================================================================

    public void SetDefeatTimer(int? timerPtr, int teamIdx)
    {
        if (!CheckTeamIdx(--teamIdx)) return;
        Teams[teamIdx].Timer = timerPtr;
        Teams[teamIdx].TimerMode = PhxTeam.TimerDisplay.Defeat;
    }

    public void SetVictoryTimer(int? timerPtr, int teamIdx)
    {
        if (!CheckTeamIdx(--teamIdx)) return;
        Teams[teamIdx].Timer = timerPtr;
        Teams[teamIdx].TimerMode = PhxTeam.TimerDisplay.Victory;
    }

    public void SetTeamName(int teamIdx, string name)
    {
        if (!CheckTeamIdx(--teamIdx)) return;
        Teams[teamIdx].Name = name;
    }

    public String getTeamName(int teamIdx)
    {
        if (!CheckTeamIdx(--teamIdx)) return "";
        return Teams[teamIdx].Name;
    }

    public void SetTeamIcon(int teamIdx, string iconName)
    {
        if (!CheckTeamIdx(--teamIdx)) return;
        if (!LVLsLoaded)
        {
            TeamIcon.Enqueue((teamIdx, iconName));
        }
        else
        {
            Teams[teamIdx].Icon = TextureLoader.Instance.ImportUITexture(iconName);
        }
    }

    public void SetUnitCount(int teamIdx, int unitCount)
    {
        if (!CheckTeamIdx(--teamIdx)) return;

        // Stock BF2 scripts ask for small squads because the 2005 engine was
        // budgeted for consoles - typically 8-16 a side. TeamSize overrides
        // whatever the mission asked for; 0 keeps the map's own value. The
        // count is inclusive of the player, who occupies one of their team's
        // slots (see TickAISpawn), matching how BF2 counts a side.
        int configured = PhxBF3.Config.TeamSize;
        Teams[teamIdx].UnitCount = configured > 0 ? configured : unitCount;
    }

    // ================= Scoreboard =======================================

    public int GetTeamPoints(int teamIdx)
    {
        if (!CheckTeamIdx(--teamIdx)) return 0;
        return Teams[teamIdx].Points;
    }

    public void SetTeamPoints(int teamIdx, int points)
    {
        int teamNum = teamIdx;
        if (!CheckTeamIdx(--teamIdx)) return;
        if (Teams[teamIdx].Points == points) return;

        Teams[teamIdx].Points = points;
        NotifyTeamPoints(teamNum, points);
    }

    public void AddTeamPoints(int teamIdx, int points)
    {
        int teamNum = teamIdx;
        if (!CheckTeamIdx(--teamIdx)) return;
        if (points == 0) return;

        Teams[teamIdx].Points += points;
        NotifyTeamPoints(teamNum, Teams[teamIdx].Points);
    }

    // Score-driven modes (CTF, assault, hunt) express their win conditions
    // through these Lua callbacks; dropping them left those modes unable to
    // end. teamNum is 1-based, as scripts expect.
    void NotifyTeamPoints(int teamNum, int points)
    {
        PhxLuaEvents.Invoke(PhxLuaEvents.Event.OnTeamPointsChange, teamNum, points);
        PhxLuaEvents.InvokeParameterized(PhxLuaEvents.Event.OnTeamPointsChangeTeam, teamNum, teamNum, points);

        // Under hero rules this is what earns a team its hero, so the check
        // belongs wherever the score moves rather than on a timer.
        PhxHeroRules.NotifyTeamPoints(teamNum, points);
    }

    /// <summary>
    /// The team's hero odf, if it has one and the rules currently allow it to
    /// be fielded. Null otherwise - which is the normal case for a map that
    /// enables neither hero mode.
    /// </summary>
    public PhxClass GetAvailableHeroClass(int teamIdx)
    {
        if (!CheckTeamIdx(teamIdx - 1)) return null;

        PhxClass hero = Teams[teamIdx - 1].HeroClass;
        return hero != null && PhxHeroRules.CanSpawnHero(teamIdx) ? hero : null;
    }

    /// <summary>
    /// Announce a reinforcement ("ticket") count change. Objective.lua watches
    /// this to drive bleed messages and the end-of-round check.
    /// </summary>
    void NotifyTickets(int teamNum)
    {
        if (!CheckTeamIdx(teamNum - 1)) return;
        PhxLuaEvents.Invoke(PhxLuaEvents.Event.OnTicketCountChange, teamNum, Teams[teamNum - 1].ReinforcementCount);
    }

    /// <summary>Units a team currently has in the world, player included.</summary>
    public int GetTeamSize(int teamIdx)
    {
        if (!CheckTeamIdx(teamIdx - 1)) return 0;

        int count = CountLiveAI(teamIdx);
        if (Player != null && Player.Team == teamIdx && Player.Pawn?.GetInstance() != null)
        {
            count++;
        }
        return count;
    }

    /// <summary>
    /// The first team that considers <paramref name="teamIdx"/> an enemy.
    /// BF2 scripts assume a two-sided fight and use this to address "the other
    /// side" without hardcoding 1 and 2.
    /// </summary>
    public int GetOpposingTeam(int teamIdx)
    {
        if (!CheckTeamIdx(teamIdx - 1)) return 0;

        for (int other = 1; other <= MAX_TEAMS; ++other)
        {
            if (other == teamIdx) continue;
            if (Teams[other - 1] == null || Teams[other - 1].UnitClasses.Count == 0) continue;
            if (!IsFriend(teamIdx, other)) return other;
        }
        return 0;
    }

    /// <summary>
    /// Records a kill for the scoreboard and spends the victim's ticket.
    /// Reinforcements are the losing condition in conquest, so this is also
    /// what drives the match towards an end.
    ///
    /// <paramref name="killer"/> is null when nothing was attributable - a
    /// fall, drowning, or an out-of-bounds region. BF2 still charges the
    /// victim's team a reinforcement in that case, so the match can't stall
    /// just because nobody landed the hit.
    /// </summary>
    public void ReportKill(PhxPawnController killer, PhxPawnController victim, int victimTeam)
    {
        bool teamKill = killer != null && killer.Team == victimTeam;

        if (killer != null && !teamKill)
        {
            killer.Kills++;
            killer.Score += KillScore;
            if (killer.Team > 0 && CheckTeamIdx(killer.Team - 1))
            {
                Teams[killer.Team - 1].Kills++;
            }
        }
        else if (teamKill)
        {
            // BF2 docks the shooter for a team kill but does not credit the
            // other side, so the tally still reflects who is actually useful.
            killer.Score -= KillScore;
        }

        if (victim != null)
        {
            victim.Deaths++;

            // Feed the AI danger map: ground where our side keeps dying is
            // ground to approach differently.
            PhxInstance victimPawn = victim.Pawn?.GetInstance();
            if (victimPawn != null)
            {
                PhxAIDanger.ReportDeath(victimPawn.transform.position, victimTeam);
            }
        }

        if (victimTeam > 0 && CheckTeamIdx(victimTeam - 1))
        {
            PhxTeam victimTeamData = Teams[victimTeam - 1];
            victimTeamData.Deaths++;

            // -1 means infinite, which is how a team with no ticket limit is
            // expressed; leave those alone rather than counting down from -1.
            if (victimTeamData.ReinforcementCount > 0)
            {
                victimTeamData.ReinforcementCount--;
                NotifyTickets(victimTeam);
            }
        }
    }

    const int KillScore = 1;

    // ================= Reinforcement bleed / match end ==================
    //
    // Conquest is decided by reinforcements. The mission script owns both the
    // rule and the draining: ObjectiveConquest.lua computes a rate from command
    // post bleed values, reports it through SetBleedRate for the HUD, and runs a
    // timer whose elapse handler calls AddReinforcements(team, -1).
    //
    // The engine's part is only to provide those calls and to notice when a
    // team reaches zero.

    public bool IsMatchOver { get; private set; }
    public int WinningTeam { get; private set; }

    /// <summary>
    /// Record a team's bleed rate for display. This does NOT drain
    /// reinforcements.
    /// </summary>
    /// <remarks>
    /// ObjectiveConquest.lua is explicit about what this is for:
    ///     --setup the bleedrate display (i.e. how fast the score flashes in the HUD)
    ///     SetBleedRate(team, bleedRate)
    /// The drain itself is a Lua timer, whose elapse handler calls
    /// AddReinforcements(team, -1) and restarts the timer. Draining here as
    /// well - which this used to do - double-counts against the script.
    /// </remarks>
    public void SetBleedRate(int teamIdx, float rate)
    {
        if (!CheckTeamIdx(--teamIdx)) return;
        Teams[teamIdx].BleedRate = rate;
    }

    /// <summary>
    /// How long this team waits between putting AI into the world, and how
    /// much that wait varies.
    /// </summary>
    /// <remarks>
    /// The spread is a fraction of the delay, matching what the stock missions
    /// pass. It is clamped to the delay itself because a spread wider than the
    /// interval would let the low end reach zero, which is the lockstep spawn
    /// the spread exists to prevent.
    /// </remarks>
    public void SetSpawnDelay(int teamIdx, float delay, float spread)
    {
        if (!CheckTeamIdx(--teamIdx)) return;

        Teams[teamIdx].SpawnDelay = Mathf.Max(delay, 0.1f);
        Teams[teamIdx].SpawnDelaySpread = Mathf.Clamp01(spread);
    }

    // Bleed thresholds are NOT tracked here. ObjectiveConquest:AddBleedThreshold
    // is a script method that only fills its own self.bleedRates table; the
    // script then drives the engine through SetBleedRate above. Mirroring that
    // table on this side would be state nothing reads.

    /// <summary>
    /// Reinforcements are drained by the mission script's bleed timer, not
    /// here - see SetBleedRate. This only watches for the losing condition.
    /// </summary>
    void TickBleed(float deltaTime)
    {
        if (IsMatchOver) return;
        CheckReinforcementDefeat();
    }

    // There is deliberately no engine-side conquest bleed rule here.
    //
    // This previously invented one: "a reinforcement every 3s per command post
    // behind". BF2's real rule lives in ObjectiveConquest.lua and is a
    // threshold, not a per-post scale - a team bleeds at defaultBleedRate
    // (0.3333/s) only once its bleedPoints exceed half its total, where points
    // come from GetCommandPostBleedValue over posts the enemy holds. The script
    // also refuses to bleed a team below its living unit count, so bleed alone
    // can never empty a side.
    //
    // All of that is script behaviour driven through engine calls we already
    // provide (GetCommandPostBleedValue, SetBleedRate, the timer API,
    // AddReinforcements). Re-implementing it here fought with the script.

    int CountPostsHeld(PhxCommandpost[] posts, int teamNum)
    {
        int held = 0;
        for (int i = 0; i < posts.Length; ++i)
        {
            if (posts[i] != null && posts[i].Team == teamNum) held++;
        }
        return held;
    }

    /// <summary>
    /// End the round when a side that was fighting on tickets runs out of them.
    /// </summary>
    /// <remarks>
    /// Only applies to a team that actually HAD reinforcements and spent them.
    /// A count of zero does not mean "defeated" - it means "this mode doesn't
    /// use tickets", which is how space assault is set up: it scores by
    /// destroying the enemy capital ship's critical systems, and its objective
    /// script owns the win condition through MissionVictory/MissionDefeat.
    /// Treating a starting zero as a loss ended those maps in instant DEFEAT
    /// the moment they loaded.
    ///
    /// Same principle as the deliberate absence of an engine-side conquest
    /// bleed rule above: where the mission script owns a rule, the engine must
    /// not run a second copy of it.
    /// </remarks>
    void CheckReinforcementDefeat()
    {
        for (int teamNum = 1; teamNum <= MAX_TEAMS; ++teamNum)
        {
            PhxTeam team = Teams[teamNum - 1];
            if (team == null || team.UnitClasses.Count == 0) continue;
            if (team.ReinforcementCount != 0) continue;   // -1 is infinite
            if (!team.SpentReinforcements) continue;      // never had any to lose

            int winner = GetOpposingTeam(teamNum);
            if (winner > 0)
            {
                EndMatch(winner);
                return;
            }
        }
    }

    // ================= Audio cues ======================================
    //
    // Bleeding and low-reinforcement voice-overs are configured by the mission
    // script, but the script never gets told when those conditions start - the
    // engine has to notice and speak the line, once per transition.

    readonly bool[] WasBleeding = new bool[MAX_TEAMS];
    readonly bool[] WasLowReinforcements = new bool[MAX_TEAMS];

    // BF2's own warning threshold for "we're running out".
    const int LowReinforcementsThreshold = 50;

    void TickAudioCues()
    {
        PhxMusicManager music = PhxMusicManager.Instance;
        if (music == null || Player == null || IsMatchOver) return;

        int hearing = Player.Team;
        if (hearing <= 0) return;

        for (int teamNum = 1; teamNum <= MAX_TEAMS; ++teamNum)
        {
            PhxTeam team = Teams[teamNum - 1];
            if (team == null) continue;

            bool bleeding = team.BleedRate > 0f;
            if (bleeding && !WasBleeding[teamNum - 1])
            {
                music.PlayBleedingVO(hearing, teamNum);
            }
            WasBleeding[teamNum - 1] = bleeding;

            bool low = team.ReinforcementCount > 0 && team.ReinforcementCount <= LowReinforcementsThreshold;
            if (low && !WasLowReinforcements[teamNum - 1])
            {
                music.PlayLowReinforcementsVO(hearing, teamNum);
            }
            // Cleared when the count recovers, so a resupply can warn again.
            WasLowReinforcements[teamNum - 1] = low;
        }
    }

    /// <summary>
    /// Ends the round. Stops AI spawning and bleed so the world settles rather
    /// than continuing to fight over a decided match.
    /// </summary>
    public void EndMatch(int winningTeam)
    {
        if (IsMatchOver) return;

        IsMatchOver = true;
        WinningTeam = winningTeam;

        string name = winningTeam > 0 && CheckTeamIdx(winningTeam - 1)
            ? Teams[winningTeam - 1].Name
            : "Nobody";
        Debug.Log($"[BF3Legacy] Match over - {name} (team {winningTeam}) wins.");

        PhxMusicManager.Instance?.OnMatchEnd(winningTeam);
    }

    /// <summary>
    /// Every controller fighting for a team, player included, for the
    /// scoreboard. Keeps AIControllers private - callers get a filtered copy
    /// rather than the live list.
    /// </summary>
    public void CollectScoreboard(int teamNum, List<PhxPawnController> into)
    {
        if (into == null) return;
        into.Clear();

        if (Player != null && Player.Team == teamNum)
        {
            into.Add(Player);
        }

        for (int i = 0; i < AIControllers.Count; ++i)
        {
            PhxPawnController c = AIControllers[i];
            if (c == null || c.Team != teamNum) continue;

            // A controller whose pawn is gone is a dead bot awaiting respawn.
            // BF2 keeps them on the board, so no liveness filter here.
            into.Add(c);
        }
    }

    public void SetReinforcementCount(int teamIdx, int reinfCount)
    {
        if (!CheckTeamIdx(--teamIdx)) return;

        // Negative counts mean "infinite" and are a mode decision, not a
        // tuning number, so they are always left alone. A finite count is
        // scaled up to match the enlarged teams - see PhxBF3Config.
        int wanted = PhxBF3.Config.Reinforcements;
        if (reinfCount > 0 && wanted > 0)
        {
            reinfCount = wanted;
        }

        Teams[teamIdx].ReinforcementCount = reinfCount;
        if (reinfCount > 0)
        {
            Teams[teamIdx].SpentReinforcements = true;
        }
        NotifyTickets(teamIdx + 1);
    }

    public int GetReinforcementCount(int teamIdx)
    {
        if (!CheckTeamIdx(--teamIdx)) return 0;
        return Teams[teamIdx].ReinforcementCount;
    }

    public void AddReinforcements(int teamIdx, int reinfCount)
    {
        if (!CheckTeamIdx(--teamIdx)) return;
        if (reinfCount == 0) return;

        Teams[teamIdx].ReinforcementCount += reinfCount;
        NotifyTickets(teamIdx + 1);
    }

    public void AddUnitClass(int teamIdx, string className, int unitCountMin, int unitCountMax = int.MaxValue)
    {
        if (!CheckTeamIdx(--teamIdx)) return;

        if (!LVLsLoaded)
        {
            TeamUnits.Enqueue((teamIdx, className, unitCountMin, unitCountMax));
        }
        else
        {
            PhxClass odf = RTS.GetClass(className);
            if (odf != null)
            {
                Teams[teamIdx].UnitClasses.Add(new PhxUnitClass { Unit = odf, CountMin = unitCountMin, CountMax = unitCountMax });
            }
            else
            {
                Debug.LogWarning($"AddUnitClass: '{className}' is unavailable - {RTS.DescribeMissingClass(className)}.");
            }

            if (PlayerST == PhxPlayerState.CharacterSelection)
            {
                // refresh
                ShowCharacterSelection();
            }
        }
    }

    public void SetHeroClass(int teamIdx, string className)
    {
        if (!CheckTeamIdx(--teamIdx)) return;

        if (!LVLsLoaded)
        {
            TeamHeroClass.Enqueue((teamIdx, className));
        }
        else
        {
            PhxClass odf = RTS.GetClass(className);
            if (odf != null)
            {
                Teams[teamIdx].HeroClass = odf;
            }
            else
            {
                Debug.LogWarning($"SetHeroClass: Could not find PhxClass '{className}'!");
            }

            if (PlayerST == PhxPlayerState.CharacterSelection)
            {
                // refresh
                ShowCharacterSelection();
            }
        }
    }

    public void SetTeamAsEnemy(int teamIdx1, int teamIdx2)
    {
        if (!CheckTeamIdx(--teamIdx1, --teamIdx2)) return;
        Teams[teamIdx1].Friends[teamIdx2] = false;
    }

    public void SetTeamAsFriend(int teamIdx1, int teamIdx2)
    {
        if (!CheckTeamIdx(--teamIdx1, --teamIdx2)) return;
        Teams[teamIdx1].Friends[teamIdx2] = true;
    }

    public bool IsFriend(int teamIdx1, int teamIdx2)
    {
        if (!CheckTeamIdx(--teamIdx1, --teamIdx2)) return false;
        return Teams[teamIdx1].Friends[teamIdx2];
    }

    // ====================================================================================
    // Lua API end
    // ====================================================================================

    bool CheckTeamIdx(int teamIdx)
    {
        if (teamIdx < 0 || teamIdx >= MAX_TEAMS)
        {
            Debug.LogErrorFormat($"Team index '{teamIdx}' is out of range '{MAX_TEAMS}'!");
            return false;
        }
        return true;
    }

    bool CheckTeamIdx(int teamIdx1, int teamIdx2)
    {
        return CheckTeamIdx(teamIdx1) && CheckTeamIdx(teamIdx2);
    }

    // player get's to choose a side and character unit
    public void StartMatch()
    {
        AvailablePauseMenu = true;
        ShowCharacterSelection();
        CAM.Fixed(RTS.GetNextCameraShot());
    }

    T ShowMenu<T>(T menu) where T : PhxMenuInterface
    {
        Cursor.visible = true;
        Cursor.lockState = CursorLockMode.None;
        return GAME.ShowMenu(menu);
    }

    void ShowCharacterSelection()
    {
        ShowMenu(GAME.CharacterSelectPrefab);
    }

    void ShowHUD()
    {
        if (GAME.IsMenuActive(GAME.HUDPrefab))
        {
            return;
        }
        ShowMenu(GAME.HUDPrefab);
    }

    void RemoveHUD()
    {
        if (GAME.IsMenuActive(GAME.HUDPrefab))
        {
            GAME.RemoveMenu();
        }
    }

    void OnRemoveMenu(Type menuType)
    {
        if (menuType == GAME.PauseMenuPrefab.GetType())
        {
            if (PlayerST == PhxPlayerState.CharacterSelection)
            {
                ShowCharacterSelection();
            }
            else if (PlayerST == PhxPlayerState.Spawned)
            {
                ShowHUD();

                Cursor.visible = false;
                Cursor.lockState = CursorLockMode.Locked;
            }
        }
    }
}

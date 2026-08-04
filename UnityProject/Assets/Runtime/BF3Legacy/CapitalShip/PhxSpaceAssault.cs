using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Bridges the stock game's own space assault system onto the BF3 Legacy
/// capital ship model.
///
/// Every stock SWBF2 space map (and BF3 Legacy's space content) sets itself up
/// through Lua calls that Phoenix previously left as empty stubs:
///
///   SpaceAssaultEnable(true)
///   SpaceAssaultAddCriticalSystem("ATT_shieldgenerator", points, hudX, hudY)
///   SpaceAssaultLinkCriticalSystems(...)
///   AddSpaceAssaultDestroyPoints(killer, instName)
///
/// The critical system names refer to object *instances* placed in the map -
/// real, authored Star Destroyer / Republic cruiser geometry with real
/// hardpoints. By resolving those names to scene instances and attaching
/// PhxCapitalShipSubsystem components to them, the stock maps get the full
/// BF3 Legacy capital ship behaviour (shield gating, reactor progression,
/// boarding, staged destruction) driven by original game content instead of
/// greybox stand-ins.
///
/// Naming convention in stock maps: instances are prefixed by team, e.g.
/// "ATT_*" (attacker) and "DEF_*" (defender), which is how systems get
/// grouped onto the correct ship.
/// </summary>
public static class PhxSpaceAssault
{
    public class PhxCriticalSystemDef
    {
        public string InstanceName;
        public float PointValue;
        public Vector2 HudPosition;
        public bool ShowHudMarker;
        public PhxCapitalShipSubsystem Bound;   // null until resolved
    }

    public static bool Enabled { get; private set; }

    static readonly List<PhxCriticalSystemDef> Pending = new List<PhxCriticalSystemDef>();
    static readonly Dictionary<int, PhxCapitalShip> ShipsByTeam = new Dictionary<int, PhxCapitalShip>();
    static bool ResolveScheduled;

    public static IReadOnlyList<PhxCriticalSystemDef> CriticalSystems => Pending;


    public static void Reset()
    {
        Enabled = false;
        Pending.Clear();
        ShipsByTeam.Clear();
        ResolveScheduled = false;
    }

    /// <summary>Lua: SpaceAssaultEnable</summary>
    public static void SetEnabled(bool enable)
    {
        Enabled = enable;
        if (enable)
        {
            Debug.Log("[BF3Legacy] Space assault mode enabled - binding stock critical systems");
            ScheduleResolve();
        }
    }

    /// <summary>Lua: SpaceAssaultAddCriticalSystem</summary>
    public static void AddCriticalSystem(string name, float pointValue, float hudX, float hudY, bool showMarker)
    {
        if (string.IsNullOrEmpty(name)) return;

        Pending.Add(new PhxCriticalSystemDef
        {
            InstanceName = name,
            PointValue = pointValue,
            HudPosition = new Vector2(hudX, hudY),
            ShowHudMarker = showMarker,
        });
        ScheduleResolve();
    }

    static void ScheduleResolve()
    {
        // Lua runs during load, before instances exist - defer binding until
        // the scene is populated.
        if (ResolveScheduled) return;
        ResolveScheduled = true;

        PhxEnvironment env = PhxGame.GetEnvironment();
        if (env != null)
        {
            env.OnPostLoad += ResolveAll;
        }
    }

    /// <summary>
    /// Resolve every declared critical system name to a scene instance and
    /// attach subsystem behaviour, grouping systems onto a per-team ship.
    /// </summary>
    public static void ResolveAll()
    {
        ResolveScheduled = false;
        if (!Enabled) return;

        PhxScene scene = PhxGame.GetScene();
        if (scene == null) return;

        int bound = 0, missing = 0;

        foreach (PhxCriticalSystemDef def in Pending)
        {
            if (def.Bound != null) continue;

            PhxInstance inst = scene.GetInstance<PhxInstance>(def.InstanceName);
            if (inst == null)
            {
                missing++;
                continue;
            }

            int team = TeamFromInstanceName(def.InstanceName, inst.Team);
            PhxCapitalShip ship = GetOrCreateShip(team, inst.transform.position);

            PhxCapitalShipSubsystem sys = inst.gameObject.GetComponent<PhxCapitalShipSubsystem>();
            if (sys == null)
            {
                sys = inst.gameObject.AddComponent<PhxCapitalShipSubsystem>();
            }
            sys.Type = TypeFromName(def.InstanceName);
            sys.IsCritical = sys.Type != PhxCapitalShipSubsystem.PhxSubsystemType.MainReactor;
            // Stock space assault expects systems to be attackable by
            // starfighters from outside; only the reactor needs boarding.
            sys.IsInternal = sys.Type == PhxCapitalShipSubsystem.PhxSubsystemType.MainReactor;
            sys.MaxHealth = Mathf.Max(def.PointValue * 20f, 1500f);
            ship.RegisterSubsystem(sys);

            def.Bound = sys;
            bound++;
        }

        Debug.Log($"[BF3Legacy] Space assault: bound {bound} critical system(s) to " +
                  $"{ShipsByTeam.Count} ship(s)" +
                  (missing > 0 ? $", {missing} instance name(s) not found in scene" : ""));
    }

    static PhxCapitalShip GetOrCreateShip(int team, Vector3 nearPosition)
    {
        if (ShipsByTeam.TryGetValue(team, out PhxCapitalShip existing) && existing != null)
        {
            return existing;
        }

        // A stock map already contains the ship's geometry as world instances;
        // we only need a controller object to own the subsystems, positioned
        // near them so destruction effects play in the right place.
        PhxCapitalShip fromScene = PhxCapitalShip.GetShipOfTeam(team);
        if (fromScene != null)
        {
            ShipsByTeam[team] = fromScene;
            return fromScene;
        }

        GameObject go = new GameObject($"SpaceAssaultShip_Team{team}");
        go.transform.position = nearPosition;

        PhxCapitalShip ship = go.AddComponent<PhxCapitalShip>();
        ship.Team = team;
        ship.ShipName = $"Capital Ship (Team {team})";
        // Stock maps model the shield as its own critical system, so don't
        // also gate on a separate shield pool.
        ship.MaxShields = 1f;
        ship.ShieldRegenPerSecond = 0f;

        ShipsByTeam[team] = ship;
        return ship;
    }

    /// <summary>
    /// Stock instance names are team-prefixed (ATT_/DEF_). Fall back to the
    /// instance's own team when the prefix is absent.
    /// </summary>
    static int TeamFromInstanceName(string name, int fallbackTeam)
    {
        string lower = name.ToLowerInvariant();
        if (lower.StartsWith("att")) return 1;
        if (lower.StartsWith("def")) return 2;
        return fallbackTeam != 0 ? fallbackTeam : 1;
    }

    /// <summary>Infer subsystem role from the authored instance name.</summary>
    static PhxCapitalShipSubsystem.PhxSubsystemType TypeFromName(string name)
    {
        string n = name.ToLowerInvariant();
        if (n.Contains("shield")) return PhxCapitalShipSubsystem.PhxSubsystemType.ShieldGenerator;
        if (n.Contains("engine")) return PhxCapitalShipSubsystem.PhxSubsystemType.Engines;
        if (n.Contains("bridge")) return PhxCapitalShipSubsystem.PhxSubsystemType.Bridge;
        if (n.Contains("reactor") || n.Contains("core")) return PhxCapitalShipSubsystem.PhxSubsystemType.MainReactor;
        if (n.Contains("comm") || n.Contains("sensor")) return PhxCapitalShipSubsystem.PhxSubsystemType.Communications;
        if (n.Contains("life")) return PhxCapitalShipSubsystem.PhxSubsystemType.LifeSupport;
        if (n.Contains("turret") || n.Contains("auto")) return PhxCapitalShipSubsystem.PhxSubsystemType.AutoTurretMainframe;
        return PhxCapitalShipSubsystem.PhxSubsystemType.Communications;
    }

    /// <summary>Lua: AddSpaceAssaultDestroyPoints - score for killing a system.</summary>
    public static void AddDestroyPoints(string instanceName)
    {
        foreach (PhxCriticalSystemDef def in Pending)
        {
            if (def.InstanceName == instanceName)
            {
                Debug.Log($"[BF3Legacy] Space assault: {instanceName} destroyed " +
                          $"({def.PointValue} pts)");
                return;
            }
        }
    }
}

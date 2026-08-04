using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Registry of Battlefront III map recreations, compiled from public
/// documentation of the cancelled Free Radical game (leaked builds, the Free
/// Radical Archive) and, where BF3 documentation is thin, from the layouts of
/// Star Wars Battlefront: Elite Squadron (the PSP/DS release that reused BF3's
/// design and ground-to-space concept).
///
/// Each entry is DATA ONLY - names, layout descriptions, normalized command
/// post positions and landmark blocks. PhxBF3MapBuilder turns an entry into a
/// playable greybox; final art is expected to come from either hand-made
/// assets or the community BF3 Legacy mod's lvl files loaded through the
/// normal addon pipeline. No original game assets are included.
///
/// Positions are in a normalized -1..+1 map space, scaled by MapSize on build.
/// </summary>
public static class PhxBF3MapDatabase
{
    [Serializable]
    public class PhxLandmark
    {
        public string Name;
        public Vector3 Position;      // normalized
        public Vector3 Size;          // meters
        public PhxLandmarkShape Shape = PhxLandmarkShape.Box;

        public PhxLandmark(string name, Vector3 pos, Vector3 size, PhxLandmarkShape shape = PhxLandmarkShape.Box)
        {
            Name = name; Position = pos; Size = size; Shape = shape;
        }
    }

    public enum PhxLandmarkShape { Box, Cylinder, Platform, Tower }

    [Serializable]
    public class PhxCommandPostDef
    {
        public string Name;
        public Vector3 Position;      // normalized
        public int StartingTeam;      // 0 = neutral

        public PhxCommandPostDef(string name, Vector3 pos, int team)
        {
            Name = name; Position = pos; StartingTeam = team;
        }
    }

    public class PhxBF3MapDef
    {
        public string Id;
        public string DisplayName;
        public string Planet;
        public string Source;                  // where the layout knowledge comes from
        public string Description;
        public float MapSize = 400f;           // meters, edge to edge
        public bool GroundToSpace;             // BF3 "Vertical Battlefront"
        public bool HasIonCannon;
        public bool InAtmosphereSpaceLayer;    // space battle happens low over the map (Coruscant)
        public Color GroundColor = new Color(0.35f, 0.33f, 0.3f);
        public Color SkyTint = Color.white;
        public float SunIntensity = 1.2f;
        public Vector3 SunDirection = new Vector3(50f, -30f, 0f);
        public List<PhxCommandPostDef> CommandPosts = new List<PhxCommandPostDef>();
        public List<PhxLandmark> Landmarks = new List<PhxLandmark>();
    }

    static List<PhxBF3MapDef> _All;
    public static IReadOnlyList<PhxBF3MapDef> All => _All ?? (_All = BuildDatabase());

    public static PhxBF3MapDef Get(string id)
    {
        foreach (PhxBF3MapDef def in All)
        {
            if (string.Equals(def.Id, id, StringComparison.OrdinalIgnoreCase)) return def;
        }
        return null;
    }

    static List<PhxBF3MapDef> BuildDatabase()
    {
        List<PhxBF3MapDef> maps = new List<PhxBF3MapDef>();

        // ------------------------------------------------------------------
        // CORUSCANT - the BF3 flagship. Leaked build: city rooftop/plaza combat,
        // objectives included destroying comm towers with starfighters; players
        // could fly straight up into a capital ship battle raging low over the
        // city (in-atmosphere space layer), with the Senate dome on the skyline.
        // ------------------------------------------------------------------
        PhxBF3MapDef coruscant = new PhxBF3MapDef
        {
            Id = "bf3_coruscant",
            DisplayName = "Coruscant: City Skyline",
            Planet = "Coruscant",
            Source = "Free Radical BF3 leaked build (city plaza, comm towers, senate skyline, ground-to-space)",
            Description = "Rooftop plazas and skylanes among Galactic City towers. " +
                          "Comm towers are destructible side objectives; the space " +
                          "battle rages directly overhead - fly up to join it.",
            MapSize = 500f,
            GroundToSpace = true,
            HasIonCannon = true,
            InAtmosphereSpaceLayer = true,
            GroundColor = new Color(0.45f, 0.44f, 0.48f),
            SkyTint = new Color(1f, 0.85f, 0.7f),   // Coruscant dusk
            SunIntensity = 1.4f,
            SunDirection = new Vector3(20f, 140f, 0f),
        };
        coruscant.CommandPosts.Add(new PhxCommandPostDef("cp_plaza", new Vector3(0f, 0f, 0f), 0));
        coruscant.CommandPosts.Add(new PhxCommandPostDef("cp_landing_west", new Vector3(-0.7f, 0f, 0.1f), 1));
        coruscant.CommandPosts.Add(new PhxCommandPostDef("cp_landing_east", new Vector3(0.7f, 0f, -0.1f), 2));
        coruscant.CommandPosts.Add(new PhxCommandPostDef("cp_skybridge", new Vector3(0f, 0.06f, 0.6f), 0));
        coruscant.CommandPosts.Add(new PhxCommandPostDef("cp_ion_control", new Vector3(-0.1f, 0f, -0.65f), 0));
        coruscant.Landmarks.Add(new PhxLandmark("senate_dome", new Vector3(0f, 0f, 1.6f), new Vector3(180f, 90f, 180f), PhxLandmarkShape.Cylinder));
        coruscant.Landmarks.Add(new PhxLandmark("comm_tower_a", new Vector3(-0.5f, 0f, 0.5f), new Vector3(12f, 120f, 12f), PhxLandmarkShape.Tower));
        coruscant.Landmarks.Add(new PhxLandmark("comm_tower_b", new Vector3(0.55f, 0f, 0.45f), new Vector3(12f, 140f, 12f), PhxLandmarkShape.Tower));
        coruscant.Landmarks.Add(new PhxLandmark("skyscraper_block_w", new Vector3(-0.85f, 0f, -0.5f), new Vector3(60f, 200f, 60f)));
        coruscant.Landmarks.Add(new PhxLandmark("skyscraper_block_e", new Vector3(0.85f, 0f, 0.7f), new Vector3(60f, 240f, 60f)));
        coruscant.Landmarks.Add(new PhxLandmark("central_plaza", new Vector3(0f, -0.002f, 0f), new Vector3(120f, 2f, 120f), PhxLandmarkShape.Platform));
        maps.Add(coruscant);

        // ------------------------------------------------------------------
        // CATO NEIMOIDIA - bridge cities strung between rock arches over the
        // fog canyon. Documented via BF3 Legacy mod reconstructions of the
        // original Free Radical map.
        // ------------------------------------------------------------------
        PhxBF3MapDef cato = new PhxBF3MapDef
        {
            Id = "bf3_cato_neimoidia",
            DisplayName = "Cato Neimoidia: Bridge City",
            Planet = "Cato Neimoidia",
            Source = "Free Radical BF3 map, reconstructed by the BF3 Legacy mod team",
            Description = "A Neimoidian bridge city suspended between rock arches. " +
                          "Long sightlines across the spans, vertical drops into fog. " +
                          "Control of the central palace decides the battle.",
            MapSize = 450f,
            GroundToSpace = true,
            GroundColor = new Color(0.5f, 0.42f, 0.32f),
            SkyTint = new Color(0.9f, 0.8f, 0.65f),
            SunIntensity = 1.3f,
            SunDirection = new Vector3(35f, 60f, 0f),
        };
        cato.CommandPosts.Add(new PhxCommandPostDef("cp_palace", new Vector3(0f, 0.04f, 0f), 0));
        cato.CommandPosts.Add(new PhxCommandPostDef("cp_bridge_west", new Vector3(-0.6f, 0.02f, 0f), 1));
        cato.CommandPosts.Add(new PhxCommandPostDef("cp_bridge_east", new Vector3(0.6f, 0.02f, 0f), 2));
        cato.CommandPosts.Add(new PhxCommandPostDef("cp_vineyard", new Vector3(0f, 0f, -0.55f), 0));
        cato.Landmarks.Add(new PhxLandmark("rock_arch_west", new Vector3(-0.9f, 0f, 0f), new Vector3(50f, 160f, 90f)));
        cato.Landmarks.Add(new PhxLandmark("rock_arch_east", new Vector3(0.9f, 0f, 0f), new Vector3(50f, 160f, 90f)));
        cato.Landmarks.Add(new PhxLandmark("bridge_span", new Vector3(0f, 0.018f, 0f), new Vector3(380f, 8f, 60f), PhxLandmarkShape.Platform));
        cato.Landmarks.Add(new PhxLandmark("palace", new Vector3(0f, 0.04f, 0.2f), new Vector3(90f, 45f, 70f), PhxLandmarkShape.Cylinder));
        maps.Add(cato);

        // ------------------------------------------------------------------
        // DANTOOINE - open plains; the leaked build's version added an extended
        // cave system with secret areas beneath the grassland.
        // ------------------------------------------------------------------
        PhxBF3MapDef dantooine = new PhxBF3MapDef
        {
            Id = "bf3_dantooine",
            DisplayName = "Dantooine: Plains & Caves",
            Planet = "Dantooine",
            Source = "Free Radical BF3 leaked build (extended cave system with secret areas)",
            Description = "Rolling savanna dotted with boulders, with a cave network " +
                          "running under the eastern hills - a flanking route the " +
                          "AI director loves.",
            MapSize = 550f,
            GroundToSpace = true,
            GroundColor = new Color(0.4f, 0.5f, 0.28f),
            SkyTint = new Color(0.8f, 0.9f, 1f),
            SunIntensity = 1.5f,
            SunDirection = new Vector3(55f, 30f, 0f),
        };
        dantooine.CommandPosts.Add(new PhxCommandPostDef("cp_homestead", new Vector3(-0.55f, 0f, 0.4f), 1));
        dantooine.CommandPosts.Add(new PhxCommandPostDef("cp_ruins", new Vector3(0.55f, 0f, -0.4f), 2));
        dantooine.CommandPosts.Add(new PhxCommandPostDef("cp_river_crossing", new Vector3(0f, 0f, 0f), 0));
        dantooine.CommandPosts.Add(new PhxCommandPostDef("cp_cave_mouth", new Vector3(0.65f, -0.01f, 0.5f), 0));
        dantooine.Landmarks.Add(new PhxLandmark("cave_hill", new Vector3(0.7f, 0f, 0.55f), new Vector3(120f, 35f, 120f), PhxLandmarkShape.Cylinder));
        dantooine.Landmarks.Add(new PhxLandmark("jedi_ruins", new Vector3(0.55f, 0f, -0.4f), new Vector3(40f, 18f, 40f)));
        dantooine.Landmarks.Add(new PhxLandmark("homestead", new Vector3(-0.55f, 0f, 0.4f), new Vector3(30f, 8f, 30f), PhxLandmarkShape.Cylinder));
        maps.Add(dantooine);

        // ------------------------------------------------------------------
        // BESPIN - platform city in the clouds, returning from BF1/BF2 with
        // BF3's verticality.
        // ------------------------------------------------------------------
        PhxBF3MapDef bespin = new PhxBF3MapDef
        {
            Id = "bf3_bespin",
            DisplayName = "Bespin: Platforms",
            Planet = "Bespin",
            Source = "Free Radical BF3 map list; layout inspired by classic Bespin: Platforms",
            Description = "Floating extraction platforms linked by narrow walkways " +
                          "above the cloud sea. Falling is lethal; starfighters rule " +
                          "the gaps between platforms.",
            MapSize = 420f,
            GroundToSpace = true,
            GroundColor = new Color(0.85f, 0.75f, 0.65f),
            SkyTint = new Color(1f, 0.75f, 0.55f),   // eternal Bespin sunset
            SunIntensity = 1.1f,
            SunDirection = new Vector3(15f, 200f, 0f),
        };
        bespin.CommandPosts.Add(new PhxCommandPostDef("cp_central_platform", new Vector3(0f, 0.05f, 0f), 0));
        bespin.CommandPosts.Add(new PhxCommandPostDef("cp_north_pad", new Vector3(0f, 0.05f, 0.7f), 1));
        bespin.CommandPosts.Add(new PhxCommandPostDef("cp_south_pad", new Vector3(0f, 0.05f, -0.7f), 2));
        bespin.CommandPosts.Add(new PhxCommandPostDef("cp_refinery", new Vector3(0.65f, 0.05f, 0f), 0));
        bespin.Landmarks.Add(new PhxLandmark("central_platform", new Vector3(0f, 0.045f, 0f), new Vector3(110f, 6f, 110f), PhxLandmarkShape.Platform));
        bespin.Landmarks.Add(new PhxLandmark("north_platform", new Vector3(0f, 0.045f, 0.7f), new Vector3(80f, 6f, 80f), PhxLandmarkShape.Platform));
        bespin.Landmarks.Add(new PhxLandmark("south_platform", new Vector3(0f, 0.045f, -0.7f), new Vector3(80f, 6f, 80f), PhxLandmarkShape.Platform));
        bespin.Landmarks.Add(new PhxLandmark("refinery_platform", new Vector3(0.65f, 0.045f, 0f), new Vector3(70f, 6f, 70f), PhxLandmarkShape.Platform));
        bespin.Landmarks.Add(new PhxLandmark("tibanna_spire", new Vector3(0.65f, 0.05f, 0f), new Vector3(16f, 60f, 16f), PhxLandmarkShape.Tower));
        maps.Add(bespin);

        // ------------------------------------------------------------------
        // DESOLATION STATION - BF3's original deep-space shipyard setting,
        // an Imperial construction station. Space-only battlefield with
        // capital ships as the primary objective.
        // ------------------------------------------------------------------
        PhxBF3MapDef desolation = new PhxBF3MapDef
        {
            Id = "bf3_desolation_station",
            DisplayName = "Desolation Station",
            Planet = "Deep Space",
            Source = "Free Radical BF3 original setting (leaked build map list)",
            Description = "An Imperial shipyard among the asteroids. Pure vertical " +
                          "battlefront: fighters, boarding runs and capital ship " +
                          "destruction decide everything - there is no ground.",
            MapSize = 600f,
            GroundToSpace = true,
            InAtmosphereSpaceLayer = false,
            GroundColor = new Color(0.1f, 0.1f, 0.12f),
            SkyTint = new Color(0.15f, 0.15f, 0.25f),
            SunIntensity = 0.9f,
            SunDirection = new Vector3(10f, -80f, 0f),
        };
        desolation.CommandPosts.Add(new PhxCommandPostDef("cp_station_core", new Vector3(0f, 0.1f, 0f), 0));
        desolation.CommandPosts.Add(new PhxCommandPostDef("cp_drydock_a", new Vector3(-0.6f, 0.15f, 0.3f), 1));
        desolation.CommandPosts.Add(new PhxCommandPostDef("cp_drydock_b", new Vector3(0.6f, 0.15f, -0.3f), 2));
        desolation.Landmarks.Add(new PhxLandmark("station_ring", new Vector3(0f, 0.1f, 0f), new Vector3(150f, 40f, 150f), PhxLandmarkShape.Cylinder));
        desolation.Landmarks.Add(new PhxLandmark("asteroid_a", new Vector3(-0.8f, 0.05f, -0.6f), new Vector3(70f, 60f, 80f), PhxLandmarkShape.Cylinder));
        desolation.Landmarks.Add(new PhxLandmark("asteroid_b", new Vector3(0.75f, 0.2f, 0.65f), new Vector3(90f, 70f, 60f), PhxLandmarkShape.Cylinder));
        desolation.Landmarks.Add(new PhxLandmark("construction_frame", new Vector3(0f, 0.25f, 0.5f), new Vector3(200f, 30f, 60f)));
        maps.Add(desolation);

        // ------------------------------------------------------------------
        // TATOOINE - Mos Eisley outskirts, in every Battlefront and confirmed
        // for BF3; Elite Squadron's version added the ground-to-space link.
        // ------------------------------------------------------------------
        PhxBF3MapDef tatooine = new PhxBF3MapDef
        {
            Id = "bf3_tatooine",
            DisplayName = "Tatooine: Mos Eisley Outskirts",
            Planet = "Tatooine",
            Source = "Free Radical BF3 map list; Elite Squadron ground-to-space layout",
            Description = "Domed adobe outskirts around a landing field. Tight alley " +
                          "fighting in town, open dune flanks, with the space battle " +
                          "reachable straight off the landing pads.",
            MapSize = 480f,
            GroundToSpace = true,
            HasIonCannon = true,
            GroundColor = new Color(0.78f, 0.65f, 0.45f),
            SkyTint = new Color(1f, 0.92f, 0.75f),
            SunIntensity = 1.7f,   // twin suns
            SunDirection = new Vector3(60f, 100f, 0f),
        };
        tatooine.CommandPosts.Add(new PhxCommandPostDef("cp_cantina_district", new Vector3(-0.5f, 0f, 0.3f), 1));
        tatooine.CommandPosts.Add(new PhxCommandPostDef("cp_landing_field", new Vector3(0.5f, 0f, -0.3f), 2));
        tatooine.CommandPosts.Add(new PhxCommandPostDef("cp_market", new Vector3(0f, 0f, 0.1f), 0));
        tatooine.CommandPosts.Add(new PhxCommandPostDef("cp_dune_ridge", new Vector3(0.1f, 0.01f, 0.65f), 0));
        tatooine.Landmarks.Add(new PhxLandmark("cantina", new Vector3(-0.5f, 0f, 0.35f), new Vector3(35f, 10f, 25f), PhxLandmarkShape.Cylinder));
        tatooine.Landmarks.Add(new PhxLandmark("hangar_dome", new Vector3(0.55f, 0f, -0.35f), new Vector3(45f, 15f, 45f), PhxLandmarkShape.Cylinder));
        tatooine.Landmarks.Add(new PhxLandmark("moisture_farm", new Vector3(-0.2f, 0f, -0.6f), new Vector3(20f, 6f, 20f)));
        tatooine.Landmarks.Add(new PhxLandmark("adobe_block_a", new Vector3(-0.15f, 0f, 0.25f), new Vector3(30f, 8f, 30f)));
        tatooine.Landmarks.Add(new PhxLandmark("adobe_block_b", new Vector3(0.15f, 0f, 0.35f), new Vector3(25f, 7f, 25f)));
        maps.Add(tatooine);

        return maps;
    }
}

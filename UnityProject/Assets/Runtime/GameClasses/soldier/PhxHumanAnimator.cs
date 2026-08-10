using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;
using UnityEngine.Profiling;

public static class PhxAnimationBanks
{
    public struct PhxAnimBank
    {
        public string StandSprint;
        public string StandRun;
        public string StandWalk;
        public string StandIdle;
        public string StandBackward;
        public string StandReload;
        public string StandShootPrimary;
        public string StandShootSecondary;
        public string StandAlertIdle;
        public string StandAlertWalk;
        public string StandAlertRun;
        public string StandAlertBackward;
        public string Jump;
        public string Fall;
        public string LandSoft;
        public string LandHard;
        public string TurnLeft;
        public string TurnRight;
    }

    public static readonly Dictionary<string, Dictionary<string, PhxAnimBank>> Banks = new Dictionary<string, Dictionary<string, PhxAnimBank>>()
    {
        { 
            "human", new Dictionary<string, PhxAnimBank>() 
            { 
                { 
                    "rifle", 
                    new PhxAnimBank
                    {
                        StandIdle = "human_rifle_stand_idle_emote_full", 
                        StandRun = "human_rifle_stand_runforward",
                        StandWalk = "human_rifle_stand_walkforward",
                        StandSprint = "human_rifle_sprint_full",
                        StandBackward = "human_rifle_stand_runbackward",
                        StandReload = "human_rifle_stand_reload_full",
                        StandShootPrimary = "human_rifle_stand_shoot_full",
                        StandShootSecondary = "human_rifle_stand_shoot_secondary_full",
                        StandAlertIdle = "human_rifle_standalert_idle_emote_full",
                        StandAlertWalk = "human_rifle_standalert_walkforward",
                        StandAlertRun = "human_rifle_standalert_runforward",
                        StandAlertBackward = "human_rifle_standalert_runbackward",
                        Jump = "human_rifle_jump",
                        Fall = "human_rifle_fall",
                        LandSoft = "human_rifle_landsoft",
                        LandHard = "human_rifle_landhard",
                        TurnLeft = "human_rifle_stand_turnleft",
                        TurnRight = "human_rifle_stand_turnright"
                    }
                },
                {
                    "pistol",
                    new PhxAnimBank
                    {
                        StandIdle = "human_tool_stand_idle_emote",                          // tool
                        StandRun = "human_pistol_stand_runforward",
                        StandWalk = "human_pistol_stand_walkforward",
                        StandSprint = "human_pistol_sprint",
                        StandBackward = "human_tool_stand_runbackward",                     // tool
                        StandReload = "human_pistol_stand_reload",
                        StandShootPrimary = "human_pistol_stand_shoot",
                        StandShootSecondary = "human_rifle_stand_shoot_secondary_full",     // rifle
                        StandAlertIdle = "human_pistol_standalert_idle_emote",
                        StandAlertWalk = "human_pistol_standalert_walkforward_full",
                        StandAlertRun = "human_pistol_standalert_runforward_full",
                        StandAlertBackward = "human_pistol_standalert_runbackward",
                        Jump = "human_tool_jump",
                        Fall = "human_tool_fall",                                           // tool
                        LandSoft = "human_tool_landsoft",                                   // tool
                        LandHard = "human_tool_landhard",                                   // tool
                        TurnLeft = "human_rifle_stand_turnleft",                            // rifle
                        TurnRight = "human_rifle_stand_turnright"                           // rifle
                    }
                },
                { 
                    "bazooka", 
                    new PhxAnimBank
                    {
                        StandIdle = "human_bazooka_stand_idle_emote", 
                        StandRun = "human_bazooka_stand_runforward",
                        StandWalk = "human_bazooka_stand_walkforward",
                        StandSprint = "human_bazooka_sprint",
                        StandBackward = "human_bazooka_stand_runbackward",
                        StandReload = "human_bazooka_stand_reload_full",
                        StandShootPrimary = "human_bazooka_stand_shoot_full",
                        StandShootSecondary = "human_bazooka_stand_shoot_secondary",
                        StandAlertIdle = "human_bazooka_standalert_idle_emote",
                        StandAlertWalk = "human_bazooka_standalert_walkforward",
                        StandAlertRun = "human_bazooka_standalert_runforward",
                        StandAlertBackward = "human_bazooka_standalert_runbackward",
                        Jump = "human_bazooka_jump",
                        Fall = "human_bazooka_fall",
                        LandSoft = "human_bazooka_landsoft",
                        LandHard = "human_bazooka_landhard",
                        TurnLeft = "human_rifle_stand_turnleft",
                        TurnRight = "human_rifle_stand_turnright"
                    }
                },
            } 
        }
    };
}

//public enum PhxAnimState
//{
//    Walk,
//    Sprint,
//    Jump,
//    Fall,
//    Land,
//    TurnLeft,
//    TurnRight
//}

public struct PhxHumanAnimator
{
    //[Range(-1f, 1f)]
    //public float Forward;
    //public PhxAnimState State;

    public int StandSprint => Banks[CurrentBankIdx].StandSprint;
    public int StandRun => Banks[CurrentBankIdx].StandRun;
    public int StandWalk => Banks[CurrentBankIdx].StandWalk;
    public int StandIdle => Banks[CurrentBankIdx].StandIdle;
    public int StandBackward => Banks[CurrentBankIdx].StandBackward;
    public int StandReload => Banks[CurrentBankIdx].StandReload;
    public int StandShootPrimary => Banks[CurrentBankIdx].StandShootPrimary;
    public int StandShootSecondary => Banks[CurrentBankIdx].StandShootSecondary;
    public int StandAlertIdle => Banks[CurrentBankIdx].StandAlertIdle;
    public int StandAlertWalk => Banks[CurrentBankIdx].StandAlertWalk;
    public int StandAlertRun => Banks[CurrentBankIdx].StandAlertRun;
    public int StandAlertBackward => Banks[CurrentBankIdx].StandAlertBackward;
    public int Jump => Banks[CurrentBankIdx].Jump;
    public int Fall => Banks[CurrentBankIdx].Fall;
    public int LandSoft => Banks[CurrentBankIdx].LandSoft;
    public int LandHard => Banks[CurrentBankIdx].LandHard;
    public int TurnLeft => Banks[CurrentBankIdx].TurnLeft;
    public int TurnRight => Banks[CurrentBankIdx].TurnRight;

    struct PhxAnimBank
    {
        public int StandSprint;
        public int StandRun;
        public int StandWalk;
        public int StandIdle;
        public int StandBackward;
        public int StandReload;
        public int StandShootPrimary;
        public int StandShootSecondary;
        public int StandAlertIdle;
        public int StandAlertWalk;
        public int StandAlertRun;
        public int StandAlertBackward;
        public int Jump;
        public int Fall;
        public int LandSoft;
        public int LandHard;
        public int TurnLeft;
        public int TurnRight;
    }

    static readonly string[] HUMANM_BANKS = 
    {
        "human_0",
        "human_1",
        "human_2",
        "human_3",
        "human_4",
        "human_sabre"
    };

    public CraAnimator Anim { get; private set; }
    Dictionary<string, CraPlayer> ClipPlayers;

    PhxAnimBank[] Banks;
    int CurrentBankIdx;
    Dictionary<string, int> NameToBankIdx;

    /// <summary>
    /// Direction a death or hit came from, relative to the victim's facing.
    /// The order matches the order of the state arrays below.
    /// </summary>
    public enum PhxImpactDir { Front = 0, Back = 1, Left = 2, Right = 3 }

    // BF2 ships death and hit reactions only in the rifle bank - the lookup
    // table has human_rifle_stand_death_* and human_rifle_stand_hit* but no
    // pistol or bazooka equivalents - so they are registered once and shared
    // by every weapon bank rather than per bank like locomotion.
    static readonly string[] DEATH_ANIMS =
    {
        "human_rifle_stand_death_forward",
        "human_rifle_stand_death_backward",
        "human_rifle_stand_death_left",
        "human_rifle_stand_death_right",
    };

    static readonly string[] HIT_ANIMS =
    {
        "human_rifle_stand_hitfront",
        "human_rifle_stand_hitback",
        "human_rifle_stand_hitleft",
        "human_rifle_stand_hitright",
    };

    int[] DeathStates;
    int[] HitStates;


    public PhxHumanAnimator(Transform root, string[] weaponAnimBanks)
    {
        Anim = CraAnimator.CreateNew(2, 128);
        ClipPlayers = new Dictionary<string, CraPlayer>();
        NameToBankIdx = new Dictionary<string, int>();
        CurrentBankIdx = 0;

        // This is a struct, so every field has to be definitely assigned before
        // the constructor may call any instance method (GetPlayer below is one).
        // Allocating here rather than at the point of use keeps that rule
        // satisfied; the contents are filled in after the banks are built.
        DeathStates = new int[DEATH_ANIMS.Length];
        HitStates = new int[HIT_ANIMS.Length];

        if (weaponAnimBanks == null || weaponAnimBanks.Length == 0)
        {
            // fallback
            weaponAnimBanks = new string[] { "rifle" };
        }

        Banks = new PhxAnimBank[weaponAnimBanks.Length];
        for (int i = 0; i < Banks.Length; ++i)
        {
            if (PhxAnimationBanks.Banks["human"].TryGetValue(weaponAnimBanks[i], out PhxAnimationBanks.PhxAnimBank bank))
            {
                Banks[i].StandIdle = Anim.AddState(0, GetPlayer(root, HUMANM_BANKS, bank.StandIdle, true));
                Banks[i].StandWalk = Anim.AddState(0, GetPlayer(root, HUMANM_BANKS, bank.StandWalk, true));
                Banks[i].StandRun = Anim.AddState(0, GetPlayer(root, HUMANM_BANKS, bank.StandRun, true));
                Banks[i].StandSprint = Anim.AddState(0, GetPlayer(root, HUMANM_BANKS, bank.StandSprint, true));
                Banks[i].StandBackward = Anim.AddState(0, GetPlayer(root, HUMANM_BANKS, bank.StandBackward, true));
                Banks[i].StandReload = Anim.AddState(1, GetPlayer(root, HUMANM_BANKS, bank.StandReload, false, "bone_a_spine"));
                Banks[i].StandShootPrimary = Anim.AddState(1, GetPlayer(root, HUMANM_BANKS, bank.StandShootPrimary, false, "bone_a_spine"));
                Banks[i].StandShootSecondary = Anim.AddState(1, GetPlayer(root, HUMANM_BANKS, bank.StandShootSecondary, false, "bone_a_spine"));
                Banks[i].StandAlertIdle = Anim.AddState(0, GetPlayer(root, HUMANM_BANKS, bank.StandAlertIdle, true));
                Banks[i].StandAlertWalk = Anim.AddState(0, GetPlayer(root, HUMANM_BANKS, bank.StandAlertWalk, true));
                Banks[i].StandAlertRun = Anim.AddState(0, GetPlayer(root, HUMANM_BANKS, bank.StandAlertRun, true));
                Banks[i].StandAlertBackward = Anim.AddState(0, GetPlayer(root, HUMANM_BANKS, bank.StandAlertBackward, true));
                Banks[i].Jump = Anim.AddState(0, GetPlayer(root, HUMANM_BANKS, bank.Jump, false));
                Banks[i].Fall = Anim.AddState(0, GetPlayer(root, HUMANM_BANKS, bank.Fall, true));
                Banks[i].LandSoft = Anim.AddState(0, GetPlayer(root, HUMANM_BANKS, bank.LandSoft, false));
                Banks[i].LandHard = Anim.AddState(0, GetPlayer(root, HUMANM_BANKS, bank.LandHard, false));
                Banks[i].TurnLeft = Anim.AddState(0, GetPlayer(root, HUMANM_BANKS, bank.TurnLeft, false));
                Banks[i].TurnRight = Anim.AddState(0, GetPlayer(root, HUMANM_BANKS, bank.TurnRight, false));

                NameToBankIdx.Add(weaponAnimBanks[i], i);
            }
            else
            {
                Debug.LogWarning($"Unknown weapon animation bank '{weaponAnimBanks[i]}'!");
            }
        }

        // Death plays on layer 0 (full body, replacing locomotion). Hit
        // reactions play on layer 1 masked to the spine, so a soldier flinches
        // from the waist up while still running - which is how BF2 reads.
        for (int i = 0; i < DEATH_ANIMS.Length; ++i)
        {
            DeathStates[i] = Anim.AddState(0, GetPlayer(root, HUMANM_BANKS, DEATH_ANIMS[i], false));
        }

        for (int i = 0; i < HIT_ANIMS.Length; ++i)
        {
            HitStates[i] = Anim.AddState(1, GetPlayer(root, HUMANM_BANKS, HIT_ANIMS[i], false, "bone_a_spine"));
        }

        Anim.SetState(0, StandIdle);
        Anim.SetState(1, CraSettings.STATE_NONE);

        Anim.AddOnStateFinishedListener(StateFinished);
    }

    public void PlayIntroAnim()
    {
        Anim.SetState(1, StandReload);
    }

    /// <summary>
    /// Which way an impact arrived, expressed in the victim's own frame.
    /// Front/back is decided first because a shot from behind reading as a
    /// side hit looks far worse than the reverse.
    /// </summary>
    public static PhxImpactDir ImpactDirFrom(Transform victim, Vector3 worldImpactPos)
    {
        Vector3 toImpact = worldImpactPos - victim.position;
        toImpact.y = 0f;
        if (toImpact.sqrMagnitude < 0.0001f)
        {
            return PhxImpactDir.Front;
        }
        toImpact.Normalize();

        float fwd = Vector3.Dot(victim.forward, toImpact);
        float right = Vector3.Dot(victim.right, toImpact);

        if (Mathf.Abs(fwd) >= Mathf.Abs(right))
        {
            return fwd >= 0f ? PhxImpactDir.Front : PhxImpactDir.Back;
        }
        return right >= 0f ? PhxImpactDir.Right : PhxImpactDir.Left;
    }

    /// <summary>
    /// Collapse in the direction of the shot. Clears the upper body layer so a
    /// half-played reload or shoot animation cannot fight the death pose.
    /// </summary>
    public void PlayDeath(PhxImpactDir dir)
    {
        if (DeathStates == null) return;

        Anim.SetState(1, CraSettings.STATE_NONE);
        Anim.SetState(0, DeathStates[(int)dir]);
    }

    /// <summary>
    /// Flinch from a non-fatal hit. Deliberately does not touch layer 0, so
    /// the soldier keeps moving and shooting through it.
    /// </summary>
    public void PlayHitReaction(PhxImpactDir dir)
    {
        if (HitStates == null) return;

        Anim.SetState(1, HitStates[(int)dir]);
    }

    public void SetAnimBank(string bankName)
    {
        if (string.IsNullOrEmpty(bankName))
        {
            return;
        }

        if (NameToBankIdx.TryGetValue(bankName, out int idx))
        {
            CurrentBankIdx = idx;
        }
    }

    CraPlayer GetPlayer(Transform root, string[] animBanks, string animName, bool loop, string maskBone = null)
    {
        if (ClipPlayers.TryGetValue(animName, out CraPlayer player))
        {
            return player;
        }
        player = PhxAnimationLoader.CreatePlayer(root, animBanks, animName, loop, maskBone);
        ClipPlayers.Add(animName, player);
        return player;
    }

    void StateFinished(int layerIdx)
    {
        if (layerIdx == 1)
        {
            Anim.SetState(1, CraSettings.STATE_NONE);
        }
    }


    public void SetActive(bool status = true)
    {
        CraPlayer state0 = Anim.GetCurrentState(0);
        state0.Reset();

        // Not sure why, but this causes an array OoB exception, despite 
        // the fact that Anim clearly has 2 layers...
        //CraPlayer state1 = Anim.GetCurrentState(1);
        //state1.Reset();
    }
}
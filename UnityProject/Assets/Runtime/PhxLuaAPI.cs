using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

public static class PhxLuaAPI
{
	public class Unicode : Attribute { }

	static PhxGame GAME => PhxGame.Instance;
	static PhxEnvironment ENV => PhxGame.GetEnvironment();
	static PhxScene RTS => PhxGame.GetScene();
	static PhxMatch MT => PhxGame.GetMatch();
	static PhxLuaRuntime RT => PhxGame.GetLuaRuntime();
	static PhxTimerDB TDB => PhxGame.GetTimerDB();
	static Lua L => RT.GetLua();


	public static string ScriptCB_GetPlatform()
	{
		// "PC", "XBox", "PS2"
		return "PC";
	}

	public static (float, float, float, float) ScriptCB_GetScreenInfo()
	{
		return (Screen.width, Screen.height, 0.0f, Screen.width / Screen.height);
	}

	public static string ScriptCB_GetOnlineService()
	{
		return "GameSpy";
	}

	public static (string, int) ScriptCB_GetLanguage()
	{
		// TODO: second parameter '0' needs verification
		return ("english", 0);
	}

	public static int ScriptCB_GetCONMaxTimeLimit()
	{
		return 60 * 60;
	}

	public static int ScriptCB_GetCONNumBots()
	{
		return 0;
	}

	public static void ScriptCB_SetNumBots(int numBots)
	{

	}

	public static int ScriptCB_GetCTFNumBots()
	{
		return 0;
	}

	public static float ScriptCB_GetCTFMaxTimeLimit()
	{
		return 0.0f;
	}

	public static int ScriptCB_GetCTFCaptureLimit()
	{
		return 0;
	}

	// ===============================================================================================================
	// Per-mode limits requested by the stock objective scripts.
	//
	// Discovered by dumping the constant tables of BF2's own compiled scripts
	// (Tools/ScriptDump) rather than guessed: objectiveassault asks for the
	// assault score limit, objectivetdm for the hunt limits, and so on. These
	// mirror the CON/CTF pattern already above - a value of 0 means "no limit",
	// which is what the scripts treat as unset.
	//
	// They matter out of proportion to their size: an unimplemented Lua global
	// raises an error that abandons the rest of the calling script, so a single
	// missing limit disabled a whole mode during setup.
	// ===============================================================================================================

	public static int ScriptCB_GetAssaultScoreLimit()
	{
		return 0;
	}

	public static int ScriptCB_GetASSNumBots()
	{
		return 0;
	}

	public static float ScriptCB_GetHuntMaxTimeLimit()
	{
		return 0.0f;
	}

	public static int ScriptCB_GetHuntScoreLimit()
	{
		return 0;
	}

	public static bool ScriptCB_ShowHuntScoreLimit()
	{
		return false;
	}

	public static int ScriptCB_GetUberScoreLimit()
	{
		return UberScoreLimit;
	}

	public static void ScriptCB_SetUberScoreLimit(int limit)
	{
		UberScoreLimit = limit;
	}

	static int UberScoreLimit = 0;

	/// <summary>Seconds since the match began.</summary>
	public static float ScriptCB_GetMissionTime()
	{
		return MT != null ? MT.GetMatchTime() : 0f;
	}

	public static bool ScriptCB_IsMissionSetupSaved()
	{
		// I think this is the "galactic conquest" special items setup (e.g. sabotage, extra health, ...)
		// see setup_teams.lua:12
		return false;
	}

	public static int ScriptCB_LoadMissionSetup()
	{
		// I think this is the "galactic conquest" special items setup (e.g. sabotage, extra health, ...)
		// see setup_teams.lua:13
		return 0;
	}

	public static void ScriptCB_SetDopplerFactor(float factor)
	{

	}

	public static int ScriptCB_GetNumCameras()
	{
		// Apparently used to determine number of view ports in split screen
		// 1 = no split screen
		return 1;
	}

	[return: Unicode]
	public static string ScriptCB_getlocalizestr(string localizePath)
	{
		return ENV.GetLocalized(localizePath, false);
	}

	[return: Unicode]
	public static string ScriptCB_getlocalizestr(string localizePath, bool bReturnNullIfNotFound)
	{
		return ENV.GetLocalized(localizePath, bReturnNullIfNotFound);
	}

	[return: Unicode]
	public static string ScriptCB_tounicode(string ansiString)
	{
		string res = Encoding.Unicode.GetString(Encoding.Convert(Encoding.ASCII, Encoding.Unicode, Encoding.ASCII.GetBytes(ansiString)));
		return res;
	}

	public static string ScriptCB_ununicode([Unicode] string unicodeString)
	{
		string res = Encoding.ASCII.GetString(Encoding.Convert(Encoding.Unicode, Encoding.ASCII, Encoding.Unicode.GetBytes(unicodeString)));
		return res;
	}

	[return: Unicode]
	public static string ScriptCB_usprintf([Unicode] object[] args)
	{
		Debug.Assert(args.Length > 1);

		// first argument is the localize path, the rest are format placements
		string localizePath = args[0] as string;
		object[] placements = new object[args.Length - 1];
		Array.Copy(args, 1, placements, 0, placements.Length);

		string localized = ENV.GetLocalized(localizePath);
		string res = PhxHelpers.Format(localized, placements);
		return res;
	}

	public static bool ScriptCB_IsFileExist(string path)
	{
		PhxPath p =  GAME.StdLVLPC / path;
		return p.IsFile() && p.Exists();
	}

	public static void ScriptCB_OpenMovie(string mvsPath, string unkwn1)
	{

	}

	public static void ScriptCB_CloseMovie()
	{

	}

	public static void ScriptCB_PlayInGameMovie(string mvsName, string movieName)
	{

	}

	public static void ScriptCB_SetGameRules(string ruleName)
	{
		// Known values are:
		// - "campaign"
		// - "instantaction"
		// - "mp"
	}

	public static void ScriptCB_DoFile(string scriptName)
	{
		ENV.Execute(scriptName);
	}

	/// <summary>
	/// Lua: ScriptCB_SndPlaySound - a one-shot interface sound.
	/// </summary>
	/// <remarks>
	/// Front-end only, and deliberately routed through the UI audio path
	/// rather than the world one: these are button clicks and screen
	/// transitions, so they must not be positioned, occluded or affected by
	/// whatever the camera is standing in.
	/// </remarks>
	public static void ScriptCB_SndPlaySound(string soundName)
	{
		if (string.IsNullOrEmpty(soundName)) return;

		AudioClip clip = SoundLoader.Instance?.LoadSound(soundName);
		if (clip == null) return;

		PhxGame.Instance?.PlayUISound(clip);
	}

	/// <summary>
	/// Lua: ScriptCB_GetControlMode - how the player is driving the menus.
	/// </summary>
	/// <remarks>
	/// The shell branches on this to decide whether to show a cursor and
	/// mouse-over states or a highlighted selection moved by a d-pad. Phoenix
	/// is a PC build, so the answer is fixed until gamepad input exists;
	/// returning the honest value beats leaving the global nil, which is what
	/// makes the shell take the console path by accident.
	/// </remarks>
	public static string ScriptCB_GetControlMode()
	{
		return "mouse";
	}

	/// <summary>
	/// Lua: ScriptCB_GetPausingViewport - which split-screen view opened the
	/// pause menu. Always the first: Phoenix has one viewport.
	/// </summary>
	public static int ScriptCB_GetPausingViewport()
	{
		return 0;
	}

	/// <summary>Lua: ScriptCB_PopScreen - leave the topmost shell screen.</summary>
	public static void ScriptCB_PopScreen()
	{
		ReportStub(nameof(ScriptCB_PopScreen));
	}

	/// <summary>Lua: ShowPopup - modal shell dialog.</summary>
	public static void ShowPopup(params object[] args)
	{
		ReportStub(nameof(ShowPopup));
	}

	/// <summary>Lua: ShowSelectionTextPopup - modal shell dialog with choices.</summary>
	public static void ShowSelectionTextPopup(params object[] args)
	{
		ReportStub(nameof(ShowSelectionTextPopup));
	}

	// Online-service and profile plumbing. These exist so the stock shell
	// scripts run to completion rather than dying on a nil global; Phoenix has
	// no GameSpy back end, no profiles and no memory card, and the honest
	// answer to every one of them is "nothing pending, nothing dirty". They
	// return rather than stub-report because the shell polls several of them
	// every frame, which would bury the log.
	public static bool ScriptCB_IsLoginDone() => true;
	public static bool ScriptCB_IsCurProfileDirty() => false;
	public static bool ScriptCB_GetMixConfigChanged() => false;
	public static void ScriptCB_Logout() { }
	public static void ScriptCB_MarkMemoryCard() { }
	public static void ScriptCB_UpdateQuickmatch() { }
	public static void ScriptCB_UpdateLeave() { }
	public static void ScriptCB_SetAutoAcquireControllers(bool enable) { }
	public static void ScriptCB_UpdateJoin() { }
	public static void ScriptCB_UpdateSessionList() { }

	/// <summary>
	/// Lua: ScriptCB_GetSafeScreenInfo - the screen area safe to draw in.
	/// </summary>
	/// <remarks>
	/// The console title-safe margin, which on a PC monitor is the whole
	/// screen. Returning the full extent rather than the usual 90% inset is
	/// the accurate answer here, and the shell lays its screens out against
	/// whatever this reports - so an inset that exists for CRT overscan would
	/// just leave a border on a display that never had the problem.
	/// </remarks>
	public static (float, float, float, float) ScriptCB_GetSafeScreenInfo()
	{
		return ScriptCB_GetScreenInfo();
	}

	public static bool ScriptCB_AutoNetJoin()
	{
		return false;
	}

	public static void ScriptCB_SetAmHost(bool value)
	{
		// We are a networking Host
		// Create socket here?
	}

	public static bool ScriptCB_GetAmHost()
	{
		// Are we the host?
		return false;
	}

	public static void ScriptCB_SetInNetGame(bool value)
	{
		// Guess: Enabled or Disables multiplayer?
	}

	public static bool ScriptCB_InNetGame()
	{
		// Guess: Are we in a multiplayer game?
		return false;
	}

	public static bool ScriptCB_InNetSession()
	{
		// Guess: Are we connected to someone?
		return false;
	}

	public static void ScriptCB_SetDedicated(bool value)
	{
		// We are a dedicated networking Host ?
	}

	public static bool ScriptCB_IsDedicated()
	{
		return false;
	}

	public static bool ScriptCB_InMultiplayer()
	{
		return false;
	}

	public static bool ScriptCB_NetWasHost()
	{
		return false;
	}

	public static bool ScriptCB_NetWasDedicated()
	{
		return false;
	}

	public static bool ScriptCB_NetWasDedicatedQuit()
	{
		return false;
	}

	public static bool ScriptCB_NetWasClient()
	{
		return false;
	}

	public static bool ScriptCB_IsAutoNet()
	{
		return false;
	}

	// Unknown parameters and return value
	public static void ScriptCB_EndAutoNet()
	{

	}

	public static string ScriptCB_GetAutoNetMode()
	{
		// Unknown return values, appears only once in a print statement in ifs_mp_autonet.lua
		return "";
	}

	public static void ScriptCB_SetGameName(string name)
	{

	}

	public static string ScriptCB_GetGameName()
	{
		return "GAME";
	}

	public static bool ScriptCB_IsInShell()
	{
		return false;
	}

	public static void ScriptCB_OpenNetShell(bool value)
	{
		// false - login access only?
	}

	public static void ScriptCB_CloseNetShell(bool close)
	{
		// seems to be always called with 'true'
	}

	public static bool ScriptCB_IsNetworkOn()
	{
		return false;
	}

	public static bool ScriptCB_IsBootInvitePending()
    {
		return false;
    }

	public static bool ScriptCB_GetAutoAssignTeams()
    {
		// Whether auto assign teams is enabled or not
		return false;
    }

	public static void ScriptCB_SetConnectType(string type)
    {
		// type can be:
		//    - "lan"
		//    - "wan"
		//    - "direct"
    }

	public static string ScriptCB_GetConnectType()
    {
		return "lan";
    }

	public static void ScriptCB_EnableJournal()
    {

    }

	public static void ScriptCB_EnablePlayback()
    {

    }

	public static (int, string) ScriptCB_GetLatestError()
    {
		// return values are:
		//    - Error Level
		//        - Starting at 0, with 0 being 'info' or no error?
		//    - Error Message
		return (0, "");
    }

	public static (int, string) ScriptCB_GetError()
	{
		// return values are:
		//    - Error Level
		//        - Starting at 0, with 0 being 'info' or no error?
		//        - == 6  -->  'in session error'?
		//        - >= 8  -->  login error?
		//    - Error Message
		return (0, "");
	}

	public static void ScriptCB_ClearError()
    {

    }

	public static void SetPS2ModelMemory(int PS2Mem)
	{
		
	}

	public static void StealArtistHeap(int numBytes)
	{
		
	}

	/// <summary>
	/// How hard a team presses. 1 is normal; lower holds ground, higher pushes.
	/// </summary>
	public static void SetTeamAggressiveness(int teamIdx, float aggr)
	{
		PhxAIDirectives.SetAggressiveness(teamIdx, aggr);
	}

	public static void SetMemoryPoolSize(string poolName, int size)
	{
		
	}

	public static void ClearWalkers()
	{
		
	}

	public static void AddWalkerType(int pairOfLegs, int unkwn1)
	{
		
	}
	public static int? AddAIGoal(int teamIdx, string goalName, float goalWeight)
	{
		return PhxAIGoals.Add(teamIdx, goalName, goalWeight);
	}

	public static int? AddAIGoal(int teamIdx, string goalName, float goalWeight, string captureRegion)
	{
		return PhxAIGoals.Add(teamIdx, goalName, goalWeight, captureRegion);
	}

	public static int? AddAIGoal(int teamIdx, string goalName, float goalWeight, string captureRegion, int flagPtr)
	{
		return PhxAIGoals.Add(teamIdx, goalName, goalWeight, captureRegion, flagPtr);
	}

	public static void DeleteAIGoal(int? goalPtr)
    {
		if (goalPtr.HasValue) PhxAIGoals.Delete(goalPtr.Value);
    }

	public static void ClearAIGoals(int teamIdx)
    {
		PhxAIGoals.ClearTeam(teamIdx);
    }

	/// <summary>How far a vehicle's arrival is noticed by AI on foot.</summary>
	public static void SetAIVehicleNotifyRadius(float radius)
    {
		PhxAIDirectives.SetVehicleNotifyRadius(radius);
    }

	/// <summary>
	/// Mission-set AI skill for a team ("medium" / "hard"). Never lowers the
	/// player's chosen difficulty - see PhxAIDirectives.SetDifficulty.
	/// </summary>
	public static void SetAIDifficulty(int teamIdx, int unkwn1, string difficulty)
    {
		PhxAIDirectives.SetDifficulty(teamIdx, difficulty);
	}

	/// <summary>Whether a team may put new AI into the world at all.</summary>
	public static void AllowAISpawn(int teamIdx, bool allow)
    {
		PhxAIDirectives.SetSpawnAllowed(teamIdx, allow);
    }

	/// <summary>
	/// How long a team waits between putting AI into the world, for every team.
	/// </summary>
	/// <remarks>
	/// The second argument is a spread: stock missions pass a delay and a
	/// fraction, and the engine picks somewhere in that band each time so a
	/// squad does not appear in lockstep. Passing the pair through rather than
	/// keeping the delay alone is what stops six units materialising on the
	/// same frame at a freshly captured post.
	/// </remarks>
	public static void SetSpawnDelay(float delay, float spread)
	{
		for (int teamIdx = 1; teamIdx < PhxMatch.MAX_TEAMS; ++teamIdx)
		{
			MT.SetSpawnDelay(teamIdx, delay, spread);
		}
	}

	/// <summary>Spawn cadence for one team - see <see cref="SetSpawnDelay"/>.</summary>
	/// <remarks>
	/// Missions use this to make one side trickle in while the other floods:
	/// an assault defender spawning every two seconds against an attacker
	/// spawning every ten is the shape of the fight, not a detail.
	/// </remarks>
	public static void SetSpawnDelayTeam(float delay, float spread, int teamIdx)
	{
		MT.SetSpawnDelay(teamIdx, delay, spread);
	}

	public static void SetHeroClass(int teamIdx, string className)
	{
		MT.SetHeroClass(teamIdx, className);
	}

	/// <summary>
	/// Campaign hero availability: the mission decides when its hero exists,
	/// not the scoreboard. Pairs with SetHeroClass, which names the odf.
	/// </summary>
	public static void EnableSPScriptedHeroes()
    {
		PhxHeroRules.EnableScriptedHeroes();
    }

    public static void EnableAIAutoBalance()
    {

    }

	/// <summary>
	/// Turn one of the map's authored flyer routes on or off, so a mission can
	/// stop transports running a pickup or capture leg during a phase where
	/// they shouldn't.
	/// </summary>
	public static void EnableFlyerPath(string pathName, bool enable)
    {
		// Examples:
		//   EnableFlyerPath('pickup', 0)
		//   EnableFlyerPath('capture', 0)
		PhxAIDirectives.SetFlyerPathEnabled(pathName, enable);
	}

	public static void SetTeamAsEnemy(int teamIdx1, int teamIdx2)
	{
		MT.SetTeamAsEnemy(teamIdx1, teamIdx2);
	}

	public static void SetTeamAsFriend(int teamIdx1, int teamIdx2)
	{
		MT.SetTeamAsFriend(teamIdx1, teamIdx2);
	}

	public static void SetTeamName(int teamIdx, string name)
	{
		MT.SetTeamName(teamIdx, name);
	}

	public static void SetUnitCount(int teamIdx, int numUnits)
	{
		MT.SetUnitCount(teamIdx, numUnits);
	}

	public static void AddUnitClass(int teamIdx, string className, int unitCountMin)
	{
		MT.AddUnitClass(teamIdx, className, unitCountMin);
	}

	public static void AddUnitClass(int teamIdx, string className, int unitCountMin, int unitCountMax)
	{
		MT.AddUnitClass(teamIdx, className, unitCountMin, unitCountMax);
	}

	public static void SetDenseEnvironment(string isDense)
	{
		// Passed as a string rather than a bool by the scripts; "false" is by
		// far the common case, so anything that isn't recognisably true is
		// treated as false rather than as an error.
		PhxAIDirectives.SetDenseEnvironment(
			string.Equals(isDense, "true", System.StringComparison.OrdinalIgnoreCase) ||
			isDense == "1");
	}

	public static void SetMinFlyHeight(float height)
	{
		PhxAIDirectives.SetFlyHeights(height, PhxAIDirectives.MaxFlyHeight);
	}

	public static void SetMaxFlyHeight(float height)
	{
		PhxAIDirectives.SetFlyHeights(PhxAIDirectives.MinFlyHeight, height);
	}

	public static void SetMinPlayerFlyHeight(float height)
    {
		PhxAIDirectives.SetPlayerFlyHeights(height, PhxAIDirectives.MaxPlayerFlyHeight);
    }

	public static void SetMaxPlayerFlyHeight(float height)
	{
		PhxAIDirectives.SetPlayerFlyHeights(PhxAIDirectives.MinPlayerFlyHeight, height);
	}

	/// <summary>
	/// Which team the mission considers the attacker. Shapes whether AI pushes
	/// objectives or holds them.
	/// </summary>
	public static void SetAttackingTeam(int teamIdx)
	{
		PhxAIDirectives.SetAttackingTeam(teamIdx);
	}

	public static void AddCameraShot(float quatW, float quatX, float quatY, float quatZ, float posX, float posY, float posZ)
	{
		RTS.AddCameraShot(
			UnityUtils.Vec3FromLibWorld( new LibSWBF2.Types.Vector3 { X = posX,  Y = posY,  Z = posZ } ),
			UnityUtils.QuatFromLibSkel( new LibSWBF2.Types.Vector4 { X = quatX, Y = quatY, Z = quatZ, W = quatW } )
		);
	}

	public static void SetTeamIcon(int teamIdx, string iconName, string hudIconName, string flagIconName)
	{
		
	}

	public static void SetTeamIcon(int teamIdx, string iconName)
	{
		MT.SetTeamIcon(teamIdx, iconName);
	}

	/// <summary>Reinforcement losses per second (e.g. 0.33).</summary>
	public static void SetBleedRate(int teamIdx, float rate)
	{
		MT.SetBleedRate(teamIdx, rate);
	}

	// AddCommandPost and AddBleedThreshold are deliberately NOT defined here.
	// The ModTools sources show both are methods on the objective class, not
	// engine API:
	//     function ObjectiveConquest:AddCommandPost(cp)
	//     function ObjectiveConquest:AddBleedThreshold(team, threshold, rate)
	// The latter only fills self.bleedRates; the engine side of conquest bleed
	// is GetCommandPostBleedValue and SetBleedRate, both already implemented.
	// Defining globals with those names would be dead code.

	public static int GetReinforcementCount(int teamIdx)
	{
		return MT.GetReinforcementCount(teamIdx);
	}

	public static void SetReinforcementCount(int teamIdx, int count)
	{
		MT.SetReinforcementCount(teamIdx, count);
	}

	public static void AddReinforcements(int teamIdx, int count)
	{
		MT.AddReinforcements(teamIdx, count);
	}

	// The handle is opaque to scripts - they only ever hand it back to
	// AudioStreamAppendSegments - so an incrementing id is enough.
	public static int OpenAudioStream(string lvlPath, string streamName)
	{
		return PhxMusicManager.Instance?.OpenStream(lvlPath, streamName) ?? 0;
	}

	public static void AudioStreamAppendSegments(string lvlPath, string voiceOverName, int audioStream)
	{
		PhxMusicManager.Instance?.AppendSegment(audioStream, voiceOverName);
	}

	// The two team indices are "who hears it" and "who it is about" - a team
	// gets a different line for its own bleeding than for the enemy's.
	public static void SetBleedingVoiceOver(int teamIdx1, int teamIdx2, string voiceOverName, int unkwn1)
	{
		PhxMusicManager.Instance?.SetBleedingVoiceOver(teamIdx1, teamIdx2, voiceOverName);
	}

	public static void SetLowReinforcementsVoiceOver(int teamIdx1, int teamIdx2, string voiceOverName, float unkwn1, int unkwn2)
	{
		PhxMusicManager.Instance?.SetLowReinforcementsVoiceOver(teamIdx1, teamIdx2, voiceOverName);
	}

	public static void SetOutOfBoundsVoiceOver(int teamIdx, string soundName)
	{
		// Recorded only: there is no out-of-bounds state to trigger it from
		// yet (AddDeathRegion is itself unimplemented).
		PhxMusicManager.Instance?.SetSoundEffect($"outofbounds{teamIdx}", soundName);
	}

	public static void BroadcastVoiceOver(string voName, int teamIdx)
    {
		PhxMusicManager.Instance?.BroadcastVoiceOver(voName, teamIdx);
    }

	public static void SetAmbientMusic(int teamIdx, float unkwn1, string musicName, int unkwn2, int unkwn3)
	{
		PhxMusicManager.Instance?.SetAmbientMusic(teamIdx, musicName);
	}

	public static void SetVictoryMusic(int teamIdx, string soundName)
	{
		PhxMusicManager.Instance?.SetVictoryMusic(teamIdx, soundName);
	}

	public static void SetDefeatMusic(int teamIdx, string soundName)
	{
		PhxMusicManager.Instance?.SetDefeatMusic(teamIdx, soundName);
	}

	public static void SetSoundEffect(string eventName, string soundName)
	{
		PhxMusicManager.Instance?.SetSoundEffect(eventName, soundName);
	}

	public static void ScaleSoundParameter(string soundName, string paramName, float scale)
    {
		// Runtime DSP parameter scaling; nothing consumes it yet.
    }

	public static void SetMapNorthAngle(int unkwn1)
	{

	}

	public static void SetMapNorthAngle(float angle, int unkwn1)
	{
		
	}

	public static void AISnipeSuitabilityDist(float distance)
	{
		
	}

	public static void FillAsteroidRegion(string regionName, string asteroidClass, int numAsteroids, 
										float u0, float u1, float u2, 
										float v0, float v1, float v2)
	{

	}

	/// <summary>
	/// Score-driven hero availability: a team earns its hero, one player has
	/// them at a time, and losing them costs the slot until it is earned again.
	/// </summary>
	public static void EnableSPHeroRules()
	{
		PhxHeroRules.EnableHeroRules();
	}

	public static void AddDeathRegion(string regionName)
	{
		
	}

	public static void AddLandingRegion(string regionName)
    {

    }

	public static void SetProperty(string instName, string propName, object propValue)
	{
		PhxScene scene = PhxGame.GetScene();
		// Debug.LogFormat("Setting property: {0} of instance: {1} to value: {2}", propName, instName, propValue);
		scene?.SetProperty(instName, propName, propValue);
	}

	public static void SetClassProperty(string className, string propName, object propValue)
	{
		PhxScene scene = PhxGame.GetScene();
		scene?.SetClassProperty(className, propName, propValue);
	}

	public static void SetObjectTeam(string instName, int teamIdx)
    {
		SetProperty(instName, "Team", teamIdx);
	}

	public static float GetObjectHealth(string instName)
    {
		return 0f;
    }

	public static void DisableBarriers(string barrierName)
	{
		PhxNavGraph.Instance.SetBarriersEnabled(barrierName, false);
	}

	public static void EnableBarriers(string barrierName)
	{
		PhxNavGraph.Instance.SetBarriersEnabled(barrierName, true);
	}

	public static void PlayAnimation(string animName)
	{
		RTS.Animator.PlayAnimation(animName.ToLower());
	}

	public static void PlayAnimationFromTo(string animName, float start, float end)
	{
		
	}

	public static void PauseAnimation(string animName)
    {
		RTS.Animator.PauseAnimation(animName.ToLower());
    }

	public static void RewindAnimation(string animName)
	{
		RTS.Animator.RewindAnimation(animName.ToLower());
	}

	public static void BlockPlanningGraphArcs(string planNodeName)
	{
		PhxNavGraph.Instance.SetHubArcsBlocked(planNodeName, true);
	}

	public static void BlockPlanningGraphArcs(int planNode)
	{
		PhxNavGraph.Instance.SetHubArcsBlocked(planNode, true);
	}

	public static void UnblockPlanningGraphArcs(string planNodeName)
    {
		PhxNavGraph.Instance.SetHubArcsBlocked(planNodeName, false);
    }

	public static void UnblockPlanningGraphArcs(int planNode)
	{
		PhxNavGraph.Instance.SetHubArcsBlocked(planNode, false);
	}

	public static void SetUberMode(bool enable)
	{
		Debug.Log(enable ? "Enabled" : "Disabled" + " Uber mode");
	}

	public static void SetGroundFlyerMap(bool enable)
    {

    }

	public static void SetDefenderSnipeRange(float range)
    {

    }

	public static int GetCommandPostTeam(int? cpPtr)
    {
		if (!cpPtr.HasValue)
        {
			return 0;
        }			
		PhxCommandpost cp = RTS.GetInstance<PhxCommandpost>(cpPtr.Value);
		if (cp != null)
        {
			return cp.Team;
        }
		Debug.LogWarning($"Illegal CommandPost Lua pointer '{cpPtr.Value}'!");
		return 0;
    }

	/// <summary>
	/// How much this command post contributes to <paramref name="teamIdx"/>'s
	/// bleed position: 1 while they hold it, 0 otherwise. Scripts sum this
	/// across posts to decide who is behind.
	/// </summary>
	public static float GetCommandPostBleedValue(string cpName, int teamIdx)
	{
		PhxCommandpost cp = RTS?.GetInstance<PhxCommandpost>(cpName);
		return cp != null && cp.Team == teamIdx ? 1.0f : 0.0f;
	}

	public static void AICanCaptureCP(string cpName, int teamIdx, bool canCapture)
    {

    }

	public static void MapHideCommandPosts()
    {
		MapHideCommandPosts(true);
	}

	public static void MapHideCommandPosts(bool hide)
	{

	}

	// ===============================================================================================================
	// CTF / 1-flag. Entirely additive: PhxFlag only exists on objects a mission
	// script registers through AddFlag, so conquest and the other modes behave
	// exactly as before on maps that never call these.
	// ===============================================================================================================

	static PhxFlag.PhxFlagMode FlagMode = PhxFlag.PhxFlagMode.Ctf;

	public static void SetFlagGameplayType(string typeName)
    {
		// "1flag" is the neutral single-flag variant; anything else is CTF.
		FlagMode = !string.IsNullOrEmpty(typeName) && typeName.ToLower().Contains("1flag")
			? PhxFlag.PhxFlagMode.OneFlag
			: PhxFlag.PhxFlagMode.Ctf;
	}

	static PhxFlag ResolveFlag(int? flagPtr)
	{
		if (!flagPtr.HasValue || RTS == null) return null;

		PhxInstance inst = RTS.GetInstance<PhxInstance>(flagPtr.Value);
		if (inst == null) return null;

		PhxFlag flag = inst.gameObject.GetComponent<PhxFlag>();
		return flag != null ? flag : inst.gameObject.AddComponent<PhxFlag>();
	}

	public static void AddFlag(int? flagPtr)
	{
		PhxFlag flag = ResolveFlag(flagPtr);
		if (flag == null)
		{
			Debug.LogWarning("AddFlag called with an instance that could not be resolved.");
			return;
		}

		flag.Mode = FlagMode;

		// In CTF the flag belongs to whichever team's object it is; the neutral
		// 1-flag has no owner.
		PhxInstance inst = flag.GetComponent<PhxInstance>();
		flag.HomeTeam = FlagMode == PhxFlag.PhxFlagMode.OneFlag || inst == null ? 0 : inst.Team;
	}

	public static void AddFlagHomeRegion(int? flagPtr, string regionName)
	{
		PhxFlag flag = ResolveFlag(flagPtr);
		if (flag == null || RTS == null) return;

		flag.HomeRegion = RTS.GetRegion(regionName);
		if (flag.HomeRegion == null)
		{
			Debug.LogWarning($"AddFlagHomeRegion: region '{regionName}' not found; " +
			                 "the flag will score at its home position instead.");
		}
	}

	public static void AddFlagCapturePoints(int? flagPtr, int points)
	{
		PhxFlag flag = ResolveFlag(flagPtr);
		if (flag != null)
		{
			flag.CapturePoints = points;
		}
	}

	public static void SetFlagIcon(int? flagPtr, string iconName)
	{
		// Minimap iconography; the map UI does not draw flags yet.
	}

	public static void OnFlagPickUp(PhxLuaRuntime.LFunction callback)
	{
		PhxLuaEvents.Register(PhxLuaEvents.Event.OnFlagPickUp, callback);
	}

	public static void OnFlagPickUpTeam(PhxLuaRuntime.LFunction callback, int teamIdx)
	{
		PhxLuaEvents.Register(PhxLuaEvents.Event.OnFlagPickUpTeam, callback, teamIdx);
	}

	public static void OnFlagDrop(PhxLuaRuntime.LFunction callback)
	{
		PhxLuaEvents.Register(PhxLuaEvents.Event.OnFlagDrop, callback);
	}

	public static void OnFlagDropTeam(PhxLuaRuntime.LFunction callback, int teamIdx)
	{
		PhxLuaEvents.Register(PhxLuaEvents.Event.OnFlagDropTeam, callback, teamIdx);
	}

	public static void OnFlagReset(PhxLuaRuntime.LFunction callback)
	{
		PhxLuaEvents.Register(PhxLuaEvents.Event.OnFlagReset, callback);
	}

	public static void SetAIViewMultiplier(float multiplier)
    {

    }

	public static void AddMissionObjective(int teamIdx, string localizePath)
	{

	}

	public static void AddMissionObjective(int teamIdx, string colorName, string localizePath)
	{

	}

	public static void ActivateObjective(string objectiveName)
    {

    }

	public static void CompleteObjective(string objectiveName)
	{

	}

	public static void MissionVictory(object teams)
    {
		// teams can either be one int (1), or a table of ints {1,2}
		int[] teamIndices = ToTeamArray(teams);
		if (teamIndices == null || teamIndices.Length == 0)
		{
			Debug.LogWarning($"MissionVictory called with unusable teams argument '{teams}'");
			return;
		}

		// A table means a shared victory; the first entry is the one the round
		// is reported under, which is how BF2 presents co-op sides.
		MT.EndMatch(teamIndices[0]);
	}

	public static int? CreateTimer(string timerName)
    {
		return TDB.CreateTimer(timerName);
    }

	public static void DestroyTimer(int? timer)
	{
		if (MT.ShowTimer == timer)
        {
			MT.ShowTimer = null;
		}
		TDB.DestroyTimer(timer);
	}

	public static void StartTimer(int? timer)
	{
		TDB.StartTimer(timer);
	}

	public static void StopTimer(int? timer)
    {
		TDB.StopTimer(timer);
	}

	public static void SetTimerRate(int? timer, float rate)
	{
		TDB.SetTimerRate(timer, rate);
	}

	public static void SetTimerValue(int? timer, float value)
    {
		TDB.SetTimerValue(timer, value);
	}

	/// <summary>Current value of a timer, in seconds.</summary>
	/// <remarks>
	/// The setter existed without a getter, and mission scripts poll this from
	/// their timer callbacks. Because a missing Lua global raises a runtime
	/// error rather than returning nil, its absence aborted whatever script was
	/// running - which is what filled the log with
	/// "attempt to call global `GetTimerValue' (a nil value)".
	/// </remarks>
	public static float GetTimerValue(int? timer)
	{
		return TDB.GetTimerValue(timer);
	}

	public static void ReleaseTimerElapse(int? timerCallback)
    {
		// Some scripts call this to notify their timer callback
		// to be obsolete, so that it can be deleted.
		// Example:

		//eventPtr = OnTimerElapse(
		//	function()
		//		ReleaseTimerElapse(eventPtr)
		//		eventPtr = nil
		//	end,
		//	some_timer
		//)
	}

	public static void ShowTimer(int? timer)
    {
		// only one neutral timer can be shown at a time.
		// when called with 'nil', hide the timer
		MT.ShowTimer = timer;
	}

	public static void SetDefeatTimer(int? timer, int teamIdx)
    {
		// only one defeat/victory timer can be shown at a time.
		// when called with 'nil', hide the timer
		MT.SetDefeatTimer(timer, teamIdx);
	}

	public static void SetVictoryTimer(int? timer, int teamIdx)
	{
		// only one victory/defeat timer can be shown at a time.
		// when called with 'nil', hide the timer
		MT.SetVictoryTimer(timer, teamIdx);
	}

	public static int? FindTimer(string timerName)
    {
		return TDB.FindTimer(timerName);
    }

	public static int GetObjectTeam(string objName)
    {
		PhxInstance inst = RTS.GetInstance<PhxInstance>(objName);
		return inst != null ? inst.Team : 0;
    }

	public static int GetObjectTeam(int objPtr)
	{
		return RTS.GetInstance<PhxInstance>(objPtr).Team;
	}

	public static bool IsObjectAlive(string objName)
	{
		return RTS.IsObjectAlive(objName);
	}

	public static void KillObject(string objName)
    {

    }

	public static void SetNumBirdTypes(int num)
    {

    }

	public static void SetBirdType(int birdIdx, float unkwn1, string typeName)
    {

    }

	public static void SetNumFishTypes(int num)
    {

    }

	public static void SetFishType(int fishIdx, float unkwn1, string typeName)
	{

	}

	/// <summary>
	/// Whether jet troopers may take a JETJUMP hint node without a confirmed
	/// landing on the far side.
	/// </summary>
	public static void SetAllowBlindJetJumps(int num)
	{
		PhxAIDirectives.SetAllowBlindJetJumps(num != 0);
	}

	public static void SetAIDamageThreshold(string objName, float threshold)
    {

    }

	public static void SetParticleLODBias(int bias)
    {

    }

	public static void SetMaxCollisionDistance(float distance)
	{

	}

	public static void SetWorldExtents(float distance)
	{

	}

	/// <summary>
	/// Opaque handle for a region, which scripts pass back into region calls -
	/// e.g. MapRemoveRegionMarker(GetRegion(flag.captureRegion)) in
	/// ObjectiveCTF.lua. Returning a constant 0, as this did, made every region
	/// alias to the same one.
	/// </summary>
	public static int GetRegion(string regionName)
    {
		return RTS != null ? RTS.GetRegionHandle(regionName) : -1;
    }

	public static string GetRegionName(int region)
	{
		return RTS != null ? RTS.GetRegionName(region) : "";
	}

	public static void ActivateRegion(string regionName)
	{

    }

	public static void DeactivateRegion(string regionName)
	{

	}

	public static void ReleaseEnterRegion(int regionEventRef)
    {

    }

	// ===============================================================================================================
	// Scoreboard. Conquest ends on reinforcements, but CTF/assault/hunt score
	// through team points, so the stock objective scripts drive both. These were
	// present in the BF2 script API (verified against the shipped mission.lvl
	// and common.lvl string tables) but unimplemented here, so any mode that
	// scored by points could never reach its win condition.
	// ===============================================================================================================

	// ===============================================================================================================
	// Character queries.
	//
	// Mission scripts pass instance indices around as "character" handles and
	// query them constantly - GetCharacterTeam alone is referenced by six of
	// the eight stock objective scripts (see Tools/ScriptDump). Because an
	// unimplemented Lua global raises an error and abandons the rest of the
	// script, each of these missing was enough to disable a whole mode.
	// ===============================================================================================================

	// ===============================================================================================================
	// Objective presentation: on-screen text and map markers.
	//
	// The state these write to lives on PhxMatch; drawing it is part of the
	// shell UI work. Implemented now regardless, because the calls themselves
	// are what abort objectivectf, objectivegoto and objectiveoneflagctf when
	// missing.
	// ===============================================================================================================

	/// <summary>Objective line shown on screen. teamIdx 0 addresses everyone.</summary>
	public static void ShowMessageText(string localizeKey, int teamIdx = 0)
	{
		MT?.PostObjectiveMessage(localizeKey, teamIdx);
	}

	/// <summary>Larger objective popup - same feed, scripts treat it as emphasis.</summary>
	public static void ShowObjectiveTextPopup(string localizeKey, int teamIdx = 0)
	{
		MT?.PostObjectiveMessage(localizeKey, teamIdx);
	}

	// Signatures taken from the ModTools sources rather than guessed. e.g.
	// ObjectiveGoto.lua:
	//     MapAddRegionMarker(self.regionName, self.mapIcon, 2.5, self.teamATT, "YELLOW", true)
	// and ObjectiveCTF.lua, which passes a handle instead of a name:
	//     MapAddRegionMarker(GetRegion(flag.captureRegion), flag.capRegionMarker, 4.0, ...)
	// My first attempt had three parameters in the wrong order, so these calls
	// could never have bound.

	public static void MapAddRegionMarker(string regionName, string iconName, float scale,
	                                      int teamIdx, string colour, bool visible)
	{
		MT?.AddMapMarker("region", regionName, teamIdx, iconName);
	}

	public static void MapAddRegionMarker(int region, string iconName, float scale,
	                                      int teamIdx, string colour, bool visible)
	{
		MapAddRegionMarker(GetRegionName(region), iconName, scale, teamIdx, colour, visible);
	}

	public static void MapAddClassMarker(string className, string iconName, float scale,
	                                     int teamIdx, string colour, bool visible)
	{
		MT?.AddMapMarker("class", className, teamIdx, iconName);
	}

	public static void MapRemoveClassMarker(string className)
	{
		MT?.RemoveMapMarkers("class", className);
	}

	// MapRemoveRegionMarker lives with the other pre-existing marker stubs
	// further down this file - adding a second one here was what produced
	// "CS0111: already defines a member called 'MapRemoveRegionMarker'".

	/// <summary>Restrict kill scoring to human players (hero/deathmatch rules).</summary>
	public static void OnlyCountHumanKills(bool onlyHumans)
	{
		if (MT != null) MT.OnlyCountHumanKills = onlyHumans;
	}

	public static void OnlyCountHumanDeaths(bool onlyHumans)
	{
		if (MT != null) MT.OnlyCountHumanDeaths = onlyHumans;
	}

	/// <summary>World file this mission is running, without extension.</summary>
	public static string GetWorldFilename()
	{
		return ENV != null ? ENV.GetWorldName() : "";
	}

	/// <summary>Galactic Conquest / campaign rather than instant action.</summary>
	public static bool IsCampaign()
	{
		return false;
	}

	/// <summary>Team of a character, or 0 (neutral) if the handle is stale.</summary>
	public static int GetCharacterTeam(int charIdx)
	{
		PhxInstance inst = RTS?.GetInstance(charIdx);
		return inst != null ? inst.Team.Get() : 0;
	}

	/// <summary>Whether this character is the local player rather than AI.</summary>
	public static bool IsCharacterHuman(int charIdx)
	{
		PhxInstance inst = RTS?.GetInstance(charIdx);
		if (inst == null) return false;

		IPhxControlableInstance playerPawn = MT?.Player?.Pawn;
		return playerPawn != null && ReferenceEquals(playerPawn.GetInstance(), inst);
	}

	/// <summary>Whether a character currently stands inside a named region.</summary>
	public static bool IsCharacterInRegion(int charIdx, string regionName)
	{
		PhxInstance inst = RTS?.GetInstance(charIdx);
		if (inst == null || string.IsNullOrEmpty(regionName)) return false;

		PhxRegion region = RTS.GetRegion(regionName);
		if (region == null || region.Collider == null) return false;

		// Bounds test rather than trigger bookkeeping: scripts ask this at
		// arbitrary moments, not only on the frame a trigger fired.
		return region.Collider.bounds.Contains(inst.transform.position);
	}

	// GetGameTimeLimit is likewise a script method, not engine API:
	//     function ObjectiveConquest:GetGameTimeLimit()
	//         return ScriptCB_GetCONMaxTimeLimit()
	// and that ScriptCB_ is already implemented above.

	public static int GetTeamPoints(int teamIdx)
	{
		return MT.GetTeamPoints(teamIdx);
	}

	public static void SetTeamPoints(int teamIdx, int points)
	{
		MT.SetTeamPoints(teamIdx, points);
	}

	public static void AddTeamPoints(int teamIdx, int points)
	{
		MT.AddTeamPoints(teamIdx, points);
	}

	/// <summary>Units a team has in the world right now, player included.</summary>
	public static int GetTeamSize(int teamIdx)
	{
		return MT.GetTeamSize(teamIdx);
	}

	public static int GetNumTeamMembersAlive(int teamIdx)
	{
		return MT.GetTeamSize(teamIdx);
	}

	public static int GetOpposingTeam(int teamIdx)
	{
		return MT.GetOpposingTeam(teamIdx);
	}

	// BF2's own scripts use both spellings interchangeably.
	public static int GetOppositeTeam(int teamIdx)
	{
		return MT.GetOpposingTeam(teamIdx);
	}

	/// <summary>
	/// Team arguments in the BF2 script API are either a single number or a
	/// Lua table of them ({1,2}), so every entry point taking "teams" has to
	/// accept both.
	/// </summary>
	static int[] ToTeamArray(object teams)
	{
		if (teams == null) return null;

		if (teams is PhxLuaRuntime.Table table)
		{
			List<int> result = new List<int>();
			foreach (KeyValuePair<object, object> entry in table)
			{
				try { result.Add(Convert.ToInt32(entry.Value)); }
				catch { /* non-numeric entry, skip */ }
			}
			return result.Count > 0 ? result.ToArray() : null;
		}

		try { return new int[] { Convert.ToInt32(teams) }; }
		catch { return null; }
	}

	/// <summary>
	/// Counterpart to MissionVictory - the losing side's scripts call this.
	/// Without it a mode that only ever calls MissionDefeat (hunt, assault
	/// defence) could never end.
	/// </summary>
	public static void MissionDefeat(object teams)
	{
		int[] teamIndices = ToTeamArray(teams);
		if (teamIndices == null) return;

		// A defeat for one side is a victory for whoever opposes it, which is
		// how BF2 resolves a two-sided match from a single call.
		for (int i = 0; i < teamIndices.Length; ++i)
		{
			int winner = MT.GetOpposingTeam(teamIndices[i]);
			if (winner > 0)
			{
				MissionVictory(winner);
				return;
			}
		}
	}

	public static void ShowTeamPoints(int teamIdx, bool show)
    {

    }

	public static int? GetObjectPtr(string objName)
    {
		return RTS.GetInstanceIndex(objName);
	}

	public static string GetEntityName(int? objPtr)
    {
		if (objPtr.HasValue)
        {
			return RTS.GetInstance<PhxInstance>(objPtr.Value).name;
        }
		return null;
    }

	public static void MapAddEntityMarker(string objName, string iconName, float size, int teamIdx, string color, bool unkwn1)
    {

    }

	public static void MapAddEntityMarker(string objName, string iconName, float size, int teamIdx, string color, bool unkwn1, bool unkwn2, bool unkwn3)
	{

	}

	public static void MapAddEntityMarker(string objName, string iconName, float size, int teamIdx, string color, bool unkwn1, bool unkwn2, bool unkwn3, bool unkwn4)
	{

	}

	public static void MapRemoveEntityMarker(object objectPtr, int teamIdx)
    {

    }

	public static void MapRemoveRegionMarker(int region)
	{
		// Scripts also address regions by index. We key markers by name, so
		// resolve through the scene's region list first.
		MT?.RemoveMapMarkers("region", RTS?.GetRegionName(region));
	}

	public static void MapRemoveRegionMarker(string regionName)
    {
		MT?.RemoveMapMarkers("region", regionName);
    }

	public static int GetFlagCarrier(string flagName)
    {
		return 0;
    }

	public static void SpaceAssaultEnable(bool enable)
    {
		PhxSpaceAssault.SetEnabled(enable);
    }

	public static void SpaceAssaultSetupBitmaps(
		object shipBitmapATT, object shipBitmapDEF,
		object shieldBitmapATT, object shieldBitmapDEF,
		object criticalSystemBitmapATT, object criticalSystemBitmapDEF)
	{

	}

	public static void SpaceAssaultAddCriticalSystem(string name, float pointValue, float hudPosX, float hudPosY)
    {
		SpaceAssaultAddCriticalSystem(name, pointValue, hudPosX, hudPosY, true);
	}

	public static void SpaceAssaultAddCriticalSystem(string name, float pointValue, float hudPosX, float hudPosY, bool displayHudMarker)
	{
		PhxSpaceAssault.AddCriticalSystem(name, pointValue, hudPosX, hudPosY, displayHudMarker);
	}

	public static void SpaceAssaultLinkCriticalSystems(object obj)
    {
		// Systems are grouped onto their ship by team prefix when resolved,
		// so the explicit link table isn't needed; re-resolve in case this
		// call arrives after further systems were declared.
		PhxSpaceAssault.ResolveAll();
    }

	public static void AddSpaceAssaultDestroyPoints(object killer, string instName)
    {
		PhxSpaceAssault.AddDestroyPoints(instName);
    }

	public static void EnableBuildingLockOn(string instName, bool lockOn)
    {

    }

	// ================= Space assault / mission plumbing =====================
	//
	// Every function below is called by the stock SPACE mission and objective
	// scripts and did not exist. In Lua 5.0 calling a nil global raises an
	// error that aborts the whole chunk at that point - so the FIRST of these
	// killed the script, and everything after it (objectives, team setup, AI
	// goals) never ran. That is why space maps spawned a unit into a match with
	// no gameplay in it.
	//
	// Where the behaviour isn't modelled yet the function is a deliberate no-op
	// that returns a sane value: an honest stub keeps the script running, which
	// is the entire problem being solved here. Each logs once so the remaining
	// gaps stay visible instead of silently pretending to work.

	static readonly HashSet<string> ReportedLuaStubs = new HashSet<string>();

	static void ReportStub(string fn)
	{
		if (!ReportedLuaStubs.Add(fn)) return;
		Debug.Log($"[Lua] '{fn}' is a stub - call accepted so the script continues, " +
		          "but the behaviour is not modelled yet.");
	}

	/// <summary>Lua: SpaceAssaultGetScoreLimit - called by objectivespaceassault.</summary>
	public static float SpaceAssaultGetScoreLimit()
	{
		// The objective script divides by this and compares against it, so a
		// zero would be worse than a guess. Matches the stock space score cap.
		return 100f;
	}

	/// <summary>Lua: OnCharacterSpawn - fires whenever any character spawns.</summary>
	public static void OnCharacterSpawn(PhxLuaRuntime.LFunction callback)
	{
		// callback parameters:
		// - characterId
		PhxLuaEvents.Register(PhxLuaEvents.Event.OnCharacterSpawn, callback);
	}

	/// <summary>
	/// Lua: ReleaseCharacterSpawn / ReleaseObjectKill - drop a registration.
	/// </summary>
	/// <remarks>
	/// No-ops, matching the existing ReleaseTimerElapse/ReleaseEnterRegion
	/// above: callbacks live for the map and are cleared on teardown, so
	/// nothing leaks across a round. What matters is that these EXIST - a
	/// missing global aborts the calling chunk, which is what broke the space
	/// scripts.
	/// </remarks>
	public static void ReleaseCharacterSpawn(object handle)
	{
	}

	public static void ReleaseObjectKill(object handle)
	{
	}

	/// <summary>
	/// Lua: ReleaseTeamPointsChange. OnTeamPointsChange was implemented but its
	/// release counterpart was not, and objectivespaceassault calls it - so the
	/// objective script died on cleanup even once the rest ran.
	/// </summary>
	public static void ReleaseTeamPointsChange(object handle)
	{
	}

	/// <summary>
	/// Lua: ReleaseCharacterDeath. Same no-op contract as the releases above.
	/// </summary>
	/// <remarks>
	/// OnCharacterDeath was implemented but this was not, and objectivetdm
	/// calls it during cleanup - so team deathmatch died on teardown even
	/// though the rest of the mode had everything it needed.
	/// </remarks>
	public static void ReleaseCharacterDeath(object handle)
	{
	}

	/// <summary>
	/// Lua: CanCharacterInteractWithFlag - may this character take the flag?
	/// </summary>
	/// <remarks>
	/// ObjectiveOneFlagCTF asks before offering the pickup prompt. The rule
	/// itself lives on PhxFlag so the prompt and the actual pickup cannot
	/// disagree.
	/// </remarks>
	public static bool CanCharacterInteractWithFlag(int charIdx, int? flagPtr)
	{
		PhxFlag flag = ResolveFlag(flagPtr);
		if (flag == null) return false;

		PhxInstance inst = RTS?.GetInstance(charIdx);
		PhxSoldier soldier = inst != null ? inst.GetComponent<PhxSoldier>() : null;
		return soldier != null && flag.CanInteract(soldier);
	}

	/// <summary>
	/// Lua: AddAssaultDestroyPoints - award a team for destroying a target.
	/// </summary>
	/// <remarks>
	/// Assault scores by destroying objectives rather than by holding ground,
	/// so this is the mode's entire scoring path.
	/// </remarks>
	public static void AddAssaultDestroyPoints(int teamIdx, int points)
	{
		MT?.AddTeamPoints(teamIdx, points);
	}

	/// <summary>
	/// Lua: GetObjectLastHitWeaponClass - what last damaged this object.
	/// </summary>
	/// <remarks>
	/// ObjectiveAssault uses it to decide whether a target counts as destroyed
	/// by the attacking team. The damage path carries an instigator but not the
	/// weapon class, so there is nothing truthful to return yet and this
	/// answers with an empty string.
	///
	/// It exists because a missing global aborts the whole calling chunk: with
	/// it absent, objectiveassault stops at the first target hit and the mode
	/// never resolves. Returning "" degrades one scoring condition instead.
	/// Threading the weapon class through PhxDamage.Apply would fix it
	/// properly.
	/// </remarks>
	public static string GetObjectLastHitWeaponClass(object obj)
	{
		return "";
	}

	/// <summary>Lua: EnableLockOn - missile lock against a named object.</summary>
	public static void EnableLockOn(object obj, bool enable)
	{
		ReportStub(nameof(EnableLockOn));
	}

	/// <summary>Lua: PlayVO - mission voice-over.</summary>
	public static void PlayVO(params object[] args)
	{
		if (args == null || args.Length == 0) return;

		string sound = args[0] as string;
		if (string.IsNullOrEmpty(sound)) return;

		// Team 0 = heard by everyone, which is the right default for a mission
		// VO that didn't name an audience.
		int team = 0;
		for (int i = 1; i < args.Length; ++i)
		{
			if (args[i] is double d) { team = (int)d; break; }
			if (args[i] is int t) { team = t; break; }
		}

		PhxMusicManager.Instance?.BroadcastVoiceOver(sound, team);
	}

	/// <summary>Lua: EntityFlyerTakeOff - send a parked flyer up.</summary>
	public static void EntityFlyerTakeOff(int? objPtr)
	{
		FlyerFor(objPtr)?.BeginTakeOff();
	}

	/// <summary>Lua: EntityFlyerInitAsLanded - the flyer starts parked.</summary>
	public static void EntityFlyerInitAsLanded(int? objPtr)
	{
		FlyerFor(objPtr)?.InitAsLanded();
	}

	static PhxFlyer FlyerFor(int? objPtr)
	{
		if (!objPtr.HasValue) return null;
		return RTS.GetInstance<PhxInstance>(objPtr.Value) as PhxFlyer;
	}

	/// <summary>
	/// Lua: RespawnObject - put a destroyed mission object back.
	/// </summary>
	/// <remarks>
	/// Missions use this on the props a phase depends on: a shield generator
	/// the next objective needs intact, a turret that has to be there when the
	/// defenders arrive. Reviving the existing instance rather than spawning a
	/// replacement is what keeps the object pointer the script is holding
	/// valid, and keeps it in whatever region and marker lists it was added to.
	/// </remarks>
	public static void RespawnObject(int? objPtr)
	{
		if (!objPtr.HasValue) return;

		PhxInstance inst = RTS.GetInstance<PhxInstance>(objPtr.Value);
		if (inst == null) return;

		if (inst is IPhxDestructible destructible)
		{
			destructible.Restore();
			return;
		}

		// Nothing authored destruction for it, so "respawned" just means alive
		// and visible again.
		inst.gameObject.SetActive(true);
	}

	/// <summary>Lua: PlayMovieWithTransition.</summary>
	public static void PlayMovieWithTransition(params object[] args)
	{
		ReportStub(nameof(PlayMovieWithTransition));
	}

	/// <summary>Lua: SetMissionEndMovie.</summary>
	public static void SetMissionEndMovie(params object[] args)
	{
		ReportStub(nameof(SetMissionEndMovie));
	}

	public static void DisableSmallMapMiniMap()
    {

    }

	public static void ReadDataFile(params object[] args)
	{
		// NOTE: ReadDataFile has dynamic parameters and can be either called like:
		// - ReadDataFile("file.lvl", "subLVL1", "subLVL2", ...)
		// - ReadDataFile("file.lvl;subLVL1;subLVL2")
		// or potentially as a mixture. Though I personally didn't see a mixture yet.

		string path = "";

		List<string> subLVLs = new List<string>();
		for (int i = 0; i < args.Length; ++i)
        {
			string arg = (string)args[i];
			string[] splits = arg.Split(';');
			subLVLs.AddRange(splits);

			if (i == 0)
            {
				path = splits[0];
				subLVLs.RemoveAt(0);
			}
		}

		bool bLoadFromAddon = path.StartsWith("dc:", StringComparison.InvariantCultureIgnoreCase);
		if (bLoadFromAddon)
        {
			path = path.Remove(0, 3);
		}

		/*
		string subLVLsStr = "";
		foreach (string subLVL in subLVLs)
		{
			subLVLsStr += (subLVL + ", ");
		}
		Debug.LogFormat("Called ReadDataFile from path: '{0}' with subLVLs: {1}", path, subLVLsStr);
		*/

		ENV.ScheduleRel(path, subLVLs.ToArray(), bLoadFromAddon);
	}

	public static void AddDownloadableContent(string threeLetterName, string scriptName, int levelMemoryModifier)
	{
		GAME.RegisterAddonScript(scriptName, threeLetterName);
	}

	// Called by the addon Lua compatibility shim (PhxBF3LegacyCompat) whenever a
	// mod declares a game mode or era the stock shell doesn't ship with, e.g.
	// BF3 Legacy's "Orbital Assault" and its two BF3-specific eras. Empty
	// strings mean "the mod didn't say", and keep whatever default we have.
	public static void PhxBF3RegisterGameMode(string key, string displayName, string about, string icon)
	{
		PhxBF3LegacyContent.RegisterModeInfo(key, displayName, about, icon);
	}




	// ===============================================================================================================
	// Event Callbacks
	// ===============================================================================================================

	public static void OnCharacterDeath(PhxLuaRuntime.LFunction callback)
    {
		PhxLuaEvents.Register(PhxLuaEvents.Event.OnCharacterDeath, callback);
		// callback parameters:
		// - killedCharacterIdx
		// - killerCharacterIdx (nil when nobody gets the credit)
    }
	public static void OnCharacterDeathTeam(PhxLuaRuntime.LFunction callback, int teamIdx)
	{
		// Keyed by the team the killed character belonged to.
		PhxLuaEvents.Register(PhxLuaEvents.Event.OnCharacterDeathTeam, callback, teamIdx);
	}
	public static void OnTicketCountChange(PhxLuaRuntime.LFunction callback)
	{
		// TicketCount seems to be the reinforcement count, see Objective.lua:192
		PhxLuaEvents.Register(PhxLuaEvents.Event.OnTicketCountChange, callback);
		// callback parameters:
		// - teamIdx
		// - ticketCount
	}
	public static void OnTimerElapse(PhxLuaRuntime.LFunction callback, int timer)
	{
		PhxLuaEvents.Register(PhxLuaEvents.Event.OnTimerElapse, callback, timer);
	}
	public static void OnEnterRegion(PhxLuaRuntime.LFunction callback, string regionName)
    {
		PhxLuaEvents.Register(PhxLuaEvents.Event.OnEnterRegion, callback, regionName);
	}
	public static void OnEnterRegionTeam(PhxLuaRuntime.LFunction callback, string regionName, int teamIdx)
	{
		PhxLuaEvents.Register(PhxLuaEvents.Event.OnEnterRegionTeam, callback, (regionName, teamIdx));
		// callback parameters:
		// - region    (string?)
		// - carrier   (ptr?)
	}
	public static void OnLeaveRegion(PhxLuaRuntime.LFunction callback, string regionName)
    {
		PhxLuaEvents.Register(PhxLuaEvents.Event.OnLeaveRegion, callback, regionName);

	}
	public static void OnFinishCapture(PhxLuaRuntime.LFunction callback)
	{
		PhxLuaEvents.Register(PhxLuaEvents.Event.OnFinishCapture, callback);
		// callback paramters:
		// - postPtr
	}
	public static void OnFinishCaptureName(PhxLuaRuntime.LFunction callback, string cpName)
	{
		PhxLuaEvents.Register(PhxLuaEvents.Event.OnFinishCaptureName, callback, cpName);
		// callback paramters:
		// - postPtr
	}
	public static void OnFinishCaptureTeam(PhxLuaRuntime.LFunction callback, int teamIdx)
	{
		PhxLuaEvents.Register(PhxLuaEvents.Event.OnFinishCaptureTeam, callback, teamIdx);
		// callback paramters:
		// - postPtr
	}
	public static void OnFinishNeutralize(PhxLuaRuntime.LFunction callback)
	{
		PhxLuaEvents.Register(PhxLuaEvents.Event.OnFinishNeutralize, callback);
		// callback paramters:
		// - postPtr
	}
	public static void OnCommandPostRespawn(PhxLuaRuntime.LFunction callback)
	{
		// callback paramters:
		// - postPtr
	}
	public static void OnCommandPostKill(PhxLuaRuntime.LFunction callback)
	{
		// callback paramters:
		// - postPtr
	}
	public static void OnObjectKillName(PhxLuaRuntime.LFunction callback, string objName)
	{
		PhxLuaEvents.Register(PhxLuaEvents.Event.OnObjectKillName, callback, objName.ToLower());
	}
	public static void OnObjectKillTeam(PhxLuaRuntime.LFunction callback, int teamIdx)
	{
		PhxLuaEvents.Register(PhxLuaEvents.Event.OnObjectKillTeam, callback, teamIdx);
	}
	public static void OnObjectKillClass(PhxLuaRuntime.LFunction callback, string className)
	{
		PhxLuaEvents.Register(PhxLuaEvents.Event.OnObjectKillClass, callback, className.ToLower());
	}
	public static void OnObjectRespawnName(PhxLuaRuntime.LFunction callback, string objName)
	{
		PhxLuaEvents.Register(PhxLuaEvents.Event.OnObjectRespawnName, callback, objName.ToLower());		
	}

	public static void OnObjectRepairName(PhxLuaRuntime.LFunction callback, string objName)
	{
		// callback paramters:
		// - objPtr
		// - characterId
	}

	public static void OnObjectDamageName(PhxLuaRuntime.LFunction callback, string objName)
    {
		PhxLuaEvents.Register(PhxLuaEvents.Event.OnObjectDamageName, callback, objName.ToLower());
		// callback parameters:
		// - objIdx
		// - attackerIdx (nil when the damage has no owner)
    }

	public static void OnTeamPointsChange(PhxLuaRuntime.LFunction callback)
    {
		PhxLuaEvents.Register(PhxLuaEvents.Event.OnTeamPointsChange, callback);
		// callback parameters:
		// - teamIdx
		// - points
    }

	public static void OnTeamPointsChangeTeam(PhxLuaRuntime.LFunction callback, int teamIdx)
	{
		PhxLuaEvents.Register(PhxLuaEvents.Event.OnTeamPointsChangeTeam, callback, teamIdx);
	}

	// ================================================================
	// Campaign
	// ================================================================
	//
	// BF2 ships its campaign in Lua - ifs_campaign_menu, campaign_data, the
	// briefing and battle-card screens - and Phoenix already runs that Lua.
	// What it needs underneath is this set of callbacks. None of them existed,
	// which is why the campaign has never been reachable: the first thing the
	// campaign shell does is ask the engine what the player has already done,
	// and an unimplemented callback leaves the script without an answer.
	//
	// State lives in BFCampaign, which persists it beside the BF3 Legacy
	// config. Nothing routes the player into the campaign shell yet, so these
	// stay dormant in practice - they run only when campaign Lua calls them.

	/// <summary>How far the player has got. 0 for a key never reached.</summary>
	public static float ScriptCB_GetSPProgress(string key)
	{
		return BFCampaign.GetProgress(key);
	}

	/// <summary>
	/// Record progress. Also accepts the no-key form some scripts use, where
	/// the value is overall campaign progress.
	/// </summary>
	public static void ScriptCB_SetSPProgress(string key, float value)
	{
		BFCampaign.SetProgress(key, value);
	}

	public static void ScriptCB_SetSPProgress(float value)
	{
		BFCampaign.SetProgress("campaign", value);
	}

	/// <summary>Copy the live campaign state into a named slot.</summary>
	public static void ScriptCB_SaveCampaignState(string name)
	{
		BFCampaign.SaveState(name);
	}

	public static void ScriptCB_SaveCampaignState()
	{
		BFCampaign.SaveState(null);
	}

	/// <summary>Make a saved slot live. No name means the most recent.</summary>
	public static bool ScriptCB_LoadCampaignState(string name)
	{
		return BFCampaign.LoadState(name);
	}

	public static bool ScriptCB_LoadCampaignState()
	{
		return BFCampaign.LoadState(null);
	}

	/// <summary>Start a fresh campaign. Saved slots are kept.</summary>
	public static void ScriptCB_ClearCampaignState()
	{
		BFCampaign.ClearState();
	}

	public static bool ScriptCB_IsCampaignStateSaved()
	{
		return BFCampaign.HasSavedState;
	}

	/// <summary>
	/// Number of saved campaigns.
	/// </summary>
	/// <remarks>
	/// Stock almost certainly hands back a table of names here. PhxLuaRuntime
	/// can push numbers, strings, booleans and functions to Lua but not tables,
	/// so a count is the most useful thing that can be returned truthfully. A
	/// shell that only asks "are there saves" gets a correct answer; one that
	/// wants to list them by name needs table-push support in the runtime
	/// first, and BFCampaign.GetSavedNames is already there to feed it.
	/// </remarks>
	public static int ScriptCB_GetSavedCampaignList()
	{
		return BFCampaign.GetSavedNames().Count;
	}

	/// <summary>
	/// The shell entering a mission.
	/// </summary>
	/// <remarks>
	/// Variadic because the argument shape is not documented and differs
	/// between the campaign and instant-action paths. The first string argument
	/// that looks like a map script is taken as the mission; anything else is
	/// recorded and ignored rather than dropped silently, so a shape we did not
	/// anticipate shows up in the log instead of as a mission that never
	/// starts.
	/// </remarks>
	public static void ScriptCB_EnterMission(params object[] args)
	{
		string mission = null;
		for (int i = 0; i < args.Length; ++i)
		{
			if (args[i] is string s && !string.IsNullOrEmpty(s))
			{
				mission = s;
				break;
			}
		}

		if (string.IsNullOrEmpty(mission))
		{
			Debug.LogWarning($"[BFCampaign] ScriptCB_EnterMission called with {args.Length} " +
							 "argument(s) and no map script among them; ignoring.");
			return;
		}

		BFCampaign.QueueMission(mission);
		Debug.Log($"[BFCampaign] Mission requested: '{mission}'.");
	}

	/// <summary>Mission names the shell wants remembered, in order.</summary>
	public static void ScriptCB_SetMissionNames(params object[] args)
	{
		var names = new System.Collections.Generic.List<string>();
		for (int i = 0; i < args.Length; ++i)
		{
			if (args[i] is string s && !string.IsNullOrEmpty(s)) names.Add(s);
		}
		BFCampaign.SetMissionNames(names);
	}

	public static void ScriptCB_ClearMissionSetup()
	{
		BFCampaign.ClearMissionSetup();
	}

	/// <summary>Queue a mission for the shell's mission list.</summary>
	public static void ScriptCB_SaveMissionSetup(params object[] args)
	{
		for (int i = 0; i < args.Length; ++i)
		{
			if (args[i] is string s && !string.IsNullOrEmpty(s))
			{
				BFCampaign.QueueMission(s);
			}
		}
	}

	public static int ScriptCB_GetMaxMissionQueue()
	{
		return BFCampaign.MaxMissionQueue;
	}

	public static void ScriptCB_UnlockUnlockable(string name)
	{
		BFCampaign.Unlock(name);
		BFCampaign.Save();
	}

	public static bool ScriptCB_UnlockableState(string name)
	{
		return BFCampaign.IsUnlocked(name);
	}

	public static bool ScriptCB_GetInTrainingMission()
	{
		return BFCampaign.InTrainingMission;
	}

	public static void ScriptCB_SetInTrainingMission(bool inTraining)
	{
		BFCampaign.InTrainingMission = inTraining;
	}
}

public static class PhxLuaEvents
{
	static PhxEnvironment ENV { get { return PhxGame.GetEnvironment(); } }
	static PhxLuaRuntime RT { get { return PhxGame.GetLuaRuntime(); } }
	static Lua L { get { return RT.GetLua(); } }

	public enum Event
	{
		OnEnterRegion,
		OnEnterRegionTeam,
		OnLeaveRegion,
		OnTimerElapse,
		OnFinishCapture,
		OnFinishCaptureName,
		OnFinishCaptureTeam,
		OnFinishNeutralize,
		OnObjectKillName,
		OnObjectRespawnName,
		OnFlagPickUp,
		OnFlagPickUpTeam,
		OnFlagDrop,
		OnFlagDropTeam,
		OnFlagReset,
		OnCharacterDeath,
		OnCharacterDeathTeam,
		OnTicketCountChange,
		OnObjectKillTeam,
		OnObjectKillClass,
		OnObjectDamageName,
		OnTeamPointsChange,
		OnTeamPointsChangeTeam,
		OnCharacterSpawn,
	}

	/// <summary>
	/// Maps parameterized callbacks. For example, Someone in Lua could call something like 'OnEnterRegion', 
	/// providing a callback and the name of the region this event should react to.
	/// Hence, we need to store who wants to listen to what exactly.
	/// </summary>
	/// <typeparam name="T"></typeparam>
	class CallbackDict<T>
    {
		Dictionary<T, List<PhxLuaRuntime.LFunction>> Callbacks = new Dictionary<T, List<PhxLuaRuntime.LFunction>>();

		public int AddCallback(T key, PhxLuaRuntime.LFunction callback)
		{
			if (Callbacks.TryGetValue(key, out List<PhxLuaRuntime.LFunction> callbacks))
			{
				callbacks.Add(callback);
				return callbacks.Count - 1;
			}
			callbacks = new List<PhxLuaRuntime.LFunction>() { callback };
			Callbacks.Add(key, callbacks);
			return callbacks.Count - 1;
		}

		public void RemoveCallback(T key, int idx)
        {
			if (Callbacks.TryGetValue(key, out List<PhxLuaRuntime.LFunction> callbacks))
			{
				callbacks.RemoveAt(idx);
			}
		}

		public void Invoke(T key, object[] args)
        {
			if (Callbacks.TryGetValue(key, out List<PhxLuaRuntime.LFunction> callbacks))
			{
				for (int i = 0; i < callbacks.Count; ++i)
                {
					callbacks[i].Invoke(args);
				}
			}
		}
	}

	static Dictionary<Event, CallbackDict<object>> ParameterizedCallbacks = new Dictionary<Event, CallbackDict<object>>();
	static Dictionary<Event, List<PhxLuaRuntime.LFunction>> Callbacks = new Dictionary<Event, List<PhxLuaRuntime.LFunction>>();


	static CallbackDict<object> Get(Event ev)
	{
		if (ParameterizedCallbacks.TryGetValue(ev, out CallbackDict<object> inner))
		{
			return inner;
		}

		inner = new CallbackDict<object>();
		ParameterizedCallbacks.Add(ev, inner);
		return inner;
	}

	public static void Clear()
    {
		ParameterizedCallbacks.Clear();
		Callbacks.Clear();
	}

	public static void Register(Event ev, PhxLuaRuntime.LFunction callback)
	{
		if (Callbacks.TryGetValue(ev, out var callbacks))
        {
			callbacks.Add(callback);
			return;
		}
		Callbacks.Add(ev, new List<PhxLuaRuntime.LFunction>() { callback });
	}

	public static void Register(Event ev, PhxLuaRuntime.LFunction callback, object key)
	{
		Get(ev).AddCallback(key, callback);
	}

	// To be called by environment
	public static void Invoke(Event ev, params object[] eventArgs)
	{
		// No logging here on purpose. Death/points/ticket events fire many
		// times a second in a live match; logging every invocation (and every
		// event nobody registered for) buried the console.
		if (Callbacks.TryGetValue(ev, out List<PhxLuaRuntime.LFunction> callbacks))
		{
			for (int i = 0; i < callbacks.Count; ++i)
			{
				callbacks[i].Invoke(eventArgs);
			}
		}
	}

	// To be called by environment
	public static void InvokeParameterized(Event ev, object key, params object[] eventArgs)
    {
		Get(ev).Invoke(key, eventArgs);
	}
}

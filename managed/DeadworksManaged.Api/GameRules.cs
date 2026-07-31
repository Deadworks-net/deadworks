namespace DeadworksManaged.Api;

/// <summary>
/// Provides access to CCitadelGameRules data. Automatically resolved when the
/// citadel_gamerules proxy entity is created/destroyed.
/// </summary>
public static unsafe class GameRules
{
	private static nint _proxyPtr;
	private static nint _gameRulesPtr;

	// CCitadelGameRulesProxy -> CCitadelGameRules
	private static readonly SchemaAccessor<nint> _pGameRules = new("CCitadelGameRulesProxy"u8, "m_pGameRules"u8);

	// CCitadelGameRules
	private static readonly SchemaAccessor<float> _levelStartTime = new("CCitadelGameRules"u8, "m_fLevelStartTime"u8);
	private static readonly SchemaAccessor<float> _gameStartTime = new("CCitadelGameRules"u8, "m_flGameStartTime"u8);
	private static readonly SchemaAccessor<float> _gameStateStartTime = new("CCitadelGameRules"u8, "m_flGameStateStartTime"u8);
	private static readonly SchemaAccessor<float> _gameStateEndTime = new("CCitadelGameRules"u8, "m_flGameStateEndTime"u8);
	private static readonly SchemaAccessor<float> _roundStartTime = new("CCitadelGameRules"u8, "m_flRoundStartTime"u8);
	private static readonly SchemaAccessor<uint> _gameState = new("CCitadelGameRules"u8, "m_eGameState"u8);
	private static readonly SchemaAccessor<uint> _matchMode = new("CCitadelGameRules"u8, "m_eMatchMode"u8);
	private static readonly SchemaAccessor<uint> _gameMode = new("CCitadelGameRules"u8, "m_eGameMode"u8);
	private static readonly SchemaAccessor<int> _midbossKillCount = new("CCitadelGameRules"u8, "m_iMidbossKillCount"u8);
	private static readonly SchemaAccessor<int> _amberRejuvCount = new("CCitadelGameRules"u8, "m_iAmberRejuvCount"u8);
	private static readonly SchemaAccessor<int> _sapphireRejuvCount = new("CCitadelGameRules"u8, "m_iSapphireRejuvCount"u8);
	private static readonly SchemaAccessor<float> _nextMidBossSpawnTime = new("CCitadelGameRules"u8, "m_tNextMidBossSpawnTime"u8);
	private static readonly SchemaAccessor<float> _matchClockAtLastUpdate = new("CCitadelGameRules"u8, "m_flMatchClockAtLastUpdate"u8);
	private static readonly SchemaAccessor<ulong> _matchID = new("CCitadelGameRules"u8, "m_unMatchID"u8);
	private static readonly SchemaAccessor<byte> _serverPaused = new("CCitadelGameRules"u8, "m_bServerPaused"u8);

	// CGameRules fields
	private static readonly SchemaAccessor<byte> _gamePaused = new("CGameRules"u8, "m_bGamePaused"u8);
	private static readonly SchemaAccessor<int> _totalPausedTicks = new("CGameRules"u8, "m_nTotalPausedTicks"u8);
	private static readonly SchemaAccessor<int> _pauseStartTick = new("CGameRules"u8, "m_nPauseStartTick"u8);

	/// <summary>Whether the game rules entity is currently active and resolved.</summary>
	public static bool IsValid => _gameRulesPtr != 0;

	/// <summary>Raw pointer to the CCitadelGameRules instance. Zero if not resolved.</summary>
	public static nint Pointer => _gameRulesPtr;

	// CCitadelGameRules

	public static float LevelStartTime => _gameRulesPtr != 0 ? _levelStartTime.Get(_gameRulesPtr) : 0f;
	public static float GameStartTime => _gameRulesPtr != 0 ? _gameStartTime.Get(_gameRulesPtr) : 0f;
	public static float GameStateStartTime => _gameRulesPtr != 0 ? _gameStateStartTime.Get(_gameRulesPtr) : 0f;
	public static float GameStateEndTime => _gameRulesPtr != 0 ? _gameStateEndTime.Get(_gameRulesPtr) : 0f;
	public static float RoundStartTime => _gameRulesPtr != 0 ? _roundStartTime.Get(_gameRulesPtr) : 0f;
	public static EGameState GameState => _gameRulesPtr != 0 ? (EGameState)_gameState.Get(_gameRulesPtr) : EGameState.Invalid;
	public static ECitadelMatchMode MatchMode => _gameRulesPtr != 0 ? (ECitadelMatchMode)_matchMode.Get(_gameRulesPtr) : ECitadelMatchMode.Invalid;
	public static ECitadelGameMode GameMode => _gameRulesPtr != 0 ? (ECitadelGameMode)_gameMode.Get(_gameRulesPtr) : ECitadelGameMode.Invalid;
	public static int MidbossKillCount => _gameRulesPtr != 0 ? _midbossKillCount.Get(_gameRulesPtr) : 0;
	public static int AmberRejuvCount => _gameRulesPtr != 0 ? _amberRejuvCount.Get(_gameRulesPtr) : 0;
	public static int SapphireRejuvCount => _gameRulesPtr != 0 ? _sapphireRejuvCount.Get(_gameRulesPtr) : 0;
	public static float NextMidBossSpawnTime => _gameRulesPtr != 0 ? _nextMidBossSpawnTime.Get(_gameRulesPtr) : 0f;
	public static float MatchClockAtLastUpdate => _gameRulesPtr != 0 ? _matchClockAtLastUpdate.Get(_gameRulesPtr) : 0f;
	public static ulong MatchID => _gameRulesPtr != 0 ? _matchID.Get(_gameRulesPtr) : 0;
	public static bool ServerPaused => _gameRulesPtr != 0 && _serverPaused.Get(_gameRulesPtr) != 0;

	/// <summary>Calls the real CCitadelGameRules::ChangeGameState, running the engine's normal transition logic.</summary>
	public static void ChangeGameState(EGameState state)
	{
		if (_gameRulesPtr != 0)
			NativeInterop.ChangeGameState((void*)_gameRulesPtr, (int)state);
	}

	/// <summary>
	/// Overrides the target used by the engine's internal WaitingForPlayersToJoin roster check.
	/// The engine's own roster is populated by matchmaking/party data, which is always empty on a
	/// direct-connect dedicated server, so the check (and the total it caches into the
	/// client-networked HUD field) reflects a real target instead of always reading empty/zero.
	/// Pass 0 to disable the override and restore the engine's native behavior.
	/// </summary>
	public static void SetWaitingForPlayersRequiredCount(uint count) => NativeInterop.SetWaitingForPlayersRequiredCount(count);

	/// <summary>
	/// Sets m_flGameStartTime, which the match clock (<see cref="GameClock"/> and the client's HUD
	/// timer) is computed relative to. On a server without real matchmaking, this field is never
	/// reset once the pregame flow actually reaches GameInProgress, so the clock counts from map
	/// load instead of from when the match really starts.
	/// </summary>
	public static void SetGameStartTime(float time)
	{
		if (_gameRulesPtr != 0)
			_gameStartTime.Set(_gameRulesPtr, time);
	}

	/// <summary>
	/// Sets m_flGameStateEndTime, which drives the client's countdown display for states that show
	/// one (e.g. PreGameWait's "Game starting..."). The engine's own ChangeGameState only writes a
	/// real value here when an internal eligibility check passes; on servers where that check never
	/// passes, the field is left at a sentinel and the client shows no countdown number.
	/// </summary>
	public static void SetGameStateEndTime(float time)
	{
		if (_gameRulesPtr != 0)
			_gameStateEndTime.Set(_gameRulesPtr, time);
	}

	// CGameRules

	public static bool GamePaused => _gameRulesPtr != 0 && _gamePaused.Get(_gameRulesPtr) != 0;
	public static int TotalPausedTicks => _gameRulesPtr != 0 ? _totalPausedTicks.Get(_gameRulesPtr) : 0;
	public static int PauseStartTick => _gameRulesPtr != 0 ? _pauseStartTick.Get(_gameRulesPtr) : 0;

	/// <summary>
	/// Returns the current game clock in seconds, accounting for pauses.
	/// Returns 0 if game rules or global vars are unavailable.
	/// </summary>
	public static float GameClock {
		get {
			if (_gameRulesPtr == 0 || !GlobalVars.IsValid)
				return 0f;

			if (GamePaused)
			{
				if (GlobalVars.CurTime > PauseStartTick * GlobalVars.IntervalPerTick)
					return (PauseStartTick - TotalPausedTicks) * GlobalVars.IntervalPerTick - GameStartTime;
			}

			return GlobalVars.CurTime - TotalPausedTicks * GlobalVars.IntervalPerTick - GameStartTime;
		}
	}

	internal static void OnEntitySpawned(CBaseEntity entity)
	{
		if (entity.DesignerName != "citadel_gamerules")
			return;

		_proxyPtr = entity.Handle;
		_gameRulesPtr = _pGameRules.Get(_proxyPtr);

		if (_gameRulesPtr != 0)
			Console.WriteLine($"[GameRules] Resolved CCitadelGameRules: 0x{_gameRulesPtr:X}");
		else
			Console.WriteLine("[GameRules] CCitadelGameRulesProxy found but m_pGameRules is null");
	}

	internal static void OnEntityDeleted(CBaseEntity entity)
	{
		if (entity.Handle != _proxyPtr || _proxyPtr == 0)
			return;

		Console.WriteLine("[GameRules] CCitadelGameRules entity destroyed");
		_proxyPtr = 0;
		_gameRulesPtr = 0;
	}
}

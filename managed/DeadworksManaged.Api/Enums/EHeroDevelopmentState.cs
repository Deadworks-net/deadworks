namespace DeadworksManaged.Api;

/// <summary>How far along a hero is, from <see cref="CitadelHeroData.DevelopmentState"/>. Only <see cref="Release"/> heroes are in public matches.</summary>
public enum EHeroDevelopmentState : byte {
	InDevelopment = 0x0,
	DebugOnly = 0x1,
	RunInBotTests = 0x2,
	InternalPlayable = 0x3,
	ExperimentalPlayable = 0x4,
	PreRelease = 0x5,
	Release = 0x6,
}

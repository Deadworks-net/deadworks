using System.Runtime.InteropServices;
using System.Text;

namespace DeadworksManaged.Api;

/// <summary>Wrapper around native CitadelHeroData_t (VData). Obtain via HeroType.GetHeroData().</summary>
public unsafe class CitadelHeroData {
	private static ReadOnlySpan<byte> Class => "CitadelHeroData_t"u8;

	public nint Pointer { get; }

	internal CitadelHeroData(nint ptr) => Pointer = ptr;

	public bool IsValid => Pointer != 0;

	private string? ReadCUtlString(SchemaAccessor<nint> accessor) {
		var strPtr = accessor.Get(Pointer);
		return strPtr == 0 ? null : Marshal.PtrToStringUTF8(strPtr);
	}

	private static readonly SchemaAccessor<int> _heroID = new(Class, "m_HeroID"u8);
	public int HeroID => _heroID.Get(Pointer);

	private static readonly SchemaAccessor<nint> _sortName = new(Class, "m_strHeroSortName"u8);
	public string? SortName => ReadCUtlString(_sortName);

	private static readonly SchemaAccessor<nint> _searchName = new(Class, "m_strHeroSearchName"u8);
	public string? SearchName => ReadCUtlString(_searchName);

	// CPanoramaImageName is 16 bytes laid out like a CBufferString: int32 length and int32 capacity, both with flags
	// in their top two bits, then the characters inline (capacity has 0x40000000) or a pointer to them.
	private string? ReadPanoramaImageName(SchemaAccessor<byte> accessor) {
		var address = accessor.GetAddress(Pointer);
		int length = *(int*)address & 0x3FFFFFFF;
		int capacity = *(int*)(address + 4);
		if (length == 0 || length > 1024) return null;
		if ((capacity & 0x40000000) != 0) return Marshal.PtrToStringUTF8(address + 8, length);
		var chars = *(nint*)(address + 8);
		return chars == 0 ? null : Marshal.PtrToStringUTF8(chars, length);
	}

	private static readonly SchemaAccessor<byte> _iconImageSmall = new(Class, "m_strIconImageSmall"u8);
	/// <summary>The small square portrait, as an image source for <see cref="UI.UI.Image"/>, e.g. <c>file://{images}/heroes/bull_sm.psd</c>.</summary>
	public string? IconImageSmall => ReadPanoramaImageName(_iconImageSmall);

	private static readonly SchemaAccessor<byte> _iconHeroCard = new(Class, "m_strIconHeroCard"u8);
	/// <summary>The tall hero card art, as an image source for <see cref="UI.UI.Image"/>.</summary>
	public string? IconHeroCard => ReadPanoramaImageName(_iconHeroCard);

	private static readonly SchemaAccessor<byte> _topBarVertical = new(Class, "m_strTopBarVertical"u8);
	/// <summary>The narrow portrait from the HUD's top bar, as an image source for <see cref="UI.UI.Image"/>.</summary>
	public string? TopBarVertical => ReadPanoramaImageName(_topBarVertical);

	private static readonly SchemaAccessor<byte> _minimapImage = new(Class, "m_strMinimapImage"u8);
	/// <summary>The minimap icon, as an image source for <see cref="UI.UI.Image"/>.</summary>
	public string? MinimapImage => ReadPanoramaImageName(_minimapImage);

	private static readonly SchemaAccessor<uint> _colorUI = new(Class, "m_colorUI"u8);
	public byte ColorR => (byte)(_colorUI.Get(Pointer) & 0xFF);
	public byte ColorG => (byte)((_colorUI.Get(Pointer) >> 8) & 0xFF);
	public byte ColorB => (byte)((_colorUI.Get(Pointer) >> 16) & 0xFF);
	public byte ColorA => (byte)((_colorUI.Get(Pointer) >> 24) & 0xFF);

	private static readonly SchemaAccessor<int> _modelSkin = new(Class, "m_nModelSkin"u8);
	public int ModelSkin => _modelSkin.Get(Pointer);

	private static readonly SchemaAccessor<float> _stealthSpeed = new(Class, "m_flStealthSpeedMetersPerSecond"u8);
	public float StealthSpeedMetersPerSecond => _stealthSpeed.Get(Pointer);

	private static readonly SchemaAccessor<byte> _inDevelopment = new(Class, "m_bInDevelopment"u8);
	public bool InDevelopment => _inDevelopment.Get(Pointer) != 0;

	private static readonly SchemaAccessor<byte> _assignedPlayersOnly = new(Class, "m_bAssignedPlayersOnly"u8);
	public bool AssignedPlayersOnly => _assignedPlayersOnly.Get(Pointer) != 0;

	private static readonly SchemaAccessor<byte> _newPlayerRecommended = new(Class, "m_bNewPlayerRecommended"u8);
	public bool NewPlayerRecommended => _newPlayerRecommended.Get(Pointer) != 0;

	private static readonly SchemaAccessor<byte> _laneTestingRecommended = new(Class, "m_bLaneTestingRecommended"u8);
	public bool LaneTestingRecommended => _laneTestingRecommended.Get(Pointer) != 0;

	private static readonly SchemaAccessor<byte> _needsTesting = new(Class, "m_bNeedsTesting"u8);
	public bool NeedsTesting => _needsTesting.Get(Pointer) != 0;

	private static readonly SchemaAccessor<byte> _limitedTesting = new(Class, "m_bLimitedTesting"u8);
	public bool LimitedTesting => _limitedTesting.Get(Pointer) != 0;

	private static readonly SchemaAccessor<byte> _disabled = new(Class, "m_bDisabled"u8);
	public bool Disabled => _disabled.Get(Pointer) != 0;

	private static readonly SchemaAccessor<byte> _playerSelectable = new(Class, "m_bPlayerSelectable"u8);
	public bool PlayerSelectable => _playerSelectable.Get(Pointer) != 0;

	private static readonly SchemaAccessor<byte> _prereleaseOnly = new(Class, "m_bPrereleaseOnly"u8);
	public bool PrereleaseOnly => _prereleaseOnly.Get(Pointer) != 0;

	private static readonly SchemaAccessor<int> _complexity = new(Class, "m_nComplexity"u8);
	public int Complexity => _complexity.Get(Pointer);

	private static readonly SchemaAccessor<int> _allyBotDifficulty = new(Class, "m_nAllyBotDifficulty"u8);
	public int AllyBotDifficulty => _allyBotDifficulty.Get(Pointer);

	private static readonly SchemaAccessor<int> _enemyBotDifficulty = new(Class, "m_nEnemyBotDifficulty"u8);
	public int EnemyBotDifficulty => _enemyBotDifficulty.Get(Pointer);

	private static readonly SchemaAccessor<float> _minLowHealthPct = new(Class, "m_flMinLowHealthPercentage"u8);
	public float MinLowHealthPercentage => _minLowHealthPct.Get(Pointer);

	private static readonly SchemaAccessor<float> _maxLowHealthPct = new(Class, "m_flMaxLowHealthPercentage"u8);
	public float MaxLowHealthPercentage => _maxLowHealthPct.Get(Pointer);

	private static readonly SchemaAccessor<float> _minMidHealthPct = new(Class, "m_flMinMidHealthPercentage"u8);
	public float MinMidHealthPercentage => _minMidHealthPct.Get(Pointer);

	private static readonly SchemaAccessor<float> _maxMidHealthPct = new(Class, "m_flMaxMidHealthPercentage"u8);
	public float MaxMidHealthPercentage => _maxMidHealthPct.Get(Pointer);

	private static readonly SchemaAccessor<int> _heroType = new(Class, "m_eHeroType"u8);
	public int HeroTypeValue => _heroType.Get(Pointer);

	public bool AvailableInGame => PlayerSelectable && !Disabled && !InDevelopment && !NeedsTesting && !PrereleaseOnly && !LimitedTesting;

	/// <summary>Read any schema field by name at runtime.</summary>
	public T GetField<T>(ReadOnlySpan<byte> fieldName) where T : unmanaged {
		var accessor = new SchemaAccessor<T>(Class, fieldName);
		return accessor.Get(Pointer);
	}
}

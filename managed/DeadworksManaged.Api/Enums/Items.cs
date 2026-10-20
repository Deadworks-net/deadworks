namespace DeadworksManaged.Api;

public enum UpgradeFlags {
	Owned = 1 << 0,
	Enhanced = 1 << 1,
	Corrupted = 1 << 7,
}

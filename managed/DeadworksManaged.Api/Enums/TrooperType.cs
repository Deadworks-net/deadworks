namespace DeadworksManaged.Api;

/// <summary>A kind of lane trooper, for <see cref="CNPC_Trooper.Create"/>.</summary>
public enum TrooperType {
	/// <summary>The ranged trooper (<c>trooper_normal</c>).</summary>
	Normal = 1,
	/// <summary>The trooper that heals other troopers (<c>trooper_medic</c>).</summary>
	Medic = 2,
	/// <summary>The melee trooper (<c>trooper_melee</c>).</summary>
	Melee = 3,
}

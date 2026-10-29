namespace DeadworksManaged.Api;

/// <summary>A kind of lane trooper, for <see cref="CNPC_Trooper.Create"/>.</summary>
public enum TrooperType {
	/// <summary>Ranged trooper (<c>trooper_normal</c>).</summary>
	Normal = 1,
	/// <summary>Heals other troopers (<c>trooper_medic</c>).</summary>
	Medic = 2,
	/// <summary>Melee trooper (<c>trooper_melee</c>).</summary>
	Melee = 3,
}

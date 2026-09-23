namespace DeadworksManaged.Api.Utils;

/// <summary>
/// Common things to do to a player's hero: hold them in place and kill them. With
/// <c>using DeadworksManaged.Api.Utils;</c> they're available on every <see cref="CCitadelPlayerPawn"/>.
/// </summary>
/// <example>
/// <code>
/// // A 3 second countdown where nobody can move or be hurt.
/// foreach (var player in Players.GetAll())
///     player.GetHeroPawn()?.Freeze(duration: 3f);
/// </code>
/// </example>
public static class PawnExtensions {
	/// <summary>The modifier <see cref="Freeze"/> uses to stop a hero moving. It's the root from Paige's and Grey Talon's abilities.</summary>
	public const string FreezeModifier = "modifier_bookworm_immobilize";

	/// <summary>The modifier <see cref="Freeze"/> uses for invulnerability. It's the protection heroes get inside the hideout.</summary>
	public const string InvulnerableModifier = "modifier_citadel_in_hideout_zone";

	// The modifiers each hero got from Freeze, so Unfreeze removes exactly those and leaves any the game applied alone.
	private static readonly Dictionary<uint, List<(CBaseModifier Modifier, bool IsRoot)>> _applied = new();

	extension(CCitadelPlayerPawn pawn) {
		/// <summary>
		/// Holds the hero in place. Until the freeze ends the player can only look around: they can't move, jump, dash,
		/// shoot or use abilities. With <paramref name="invulnerable"/>, which is the default, weapons and abilities can't
		/// hurt them either. <see cref="Kill"/> still works on a frozen hero.
		/// </summary>
		/// <param name="duration">Seconds until the freeze wears off by itself. Leave it out to freeze until <see cref="Unfreeze"/>.</param>
		/// <param name="invulnerable">Also protect the hero from damage for the same time.</param>
		/// <returns>False if the game couldn't apply the freeze.</returns>
		public bool Freeze(float? duration = null, bool invulnerable = true) {
			using var kv = new KeyValues3();
			if (duration.HasValue)
				kv.SetFloat("duration", duration.Value);

			if (pawn.AddModifier(FreezeModifier, kv) is not { } root)
				return false;
			if (!_applied.TryGetValue(pawn.EntityHandle, out var applied))
				_applied[pawn.EntityHandle] = applied = [];
			Track(applied, root, isRoot: true);
			if (invulnerable && pawn.AddModifier(InvulnerableModifier, kv) is { } protection)
				Track(applied, protection, isRoot: false);
			return true;
		}

		/// <summary>True while the hero is frozen by <see cref="Freeze"/>.</summary>
		public bool IsFrozen => StillApplied(pawn).Any(entry => entry.IsRoot);

		/// <summary>
		/// Ends a freeze from <see cref="Freeze"/>, including its invulnerability. Safe to call when the hero isn't frozen.
		/// </summary>
		public void Unfreeze() {
			foreach (var (modifier, _) in StillApplied(pawn))
				pawn.RemoveModifier(modifier);
			_applied.Remove(pawn.EntityHandle);
		}

		/// <summary>
		/// Kills the hero. The damage skips resistances and protection, so it works even on an invulnerable hero, and
		/// the game treats it as an ordinary death: they wait out the usual death timer unless you pass
		/// <paramref name="respawnImmediately"/>. A freeze from <see cref="Freeze"/> is lifted first, so they don't come
		/// back frozen.
		/// </summary>
		/// <param name="respawnImmediately">Bring the hero straight back instead of making them wait.</param>
		/// <returns>True if the hero was killed.</returns>
		public bool Kill(bool respawnImmediately = false) {
			if (!pawn.IsAlive)
				return false;
			pawn.Unfreeze();
			pawn.Hurt(1_000_000f);
			if (pawn.Health > 0 && pawn.IsAlive)
				return false;

			if (respawnImmediately) {
				// The game sets the respawn time while it processes the death, after Hurt returns, so clear it a tick later.
				TimerResolver.RunNextTick(() => {
					if (pawn.IsValid && !pawn.IsAlive)
						pawn.RespawnTime = 0f;
				});
			}
			return true;
		}
	}

	// Freezing a frozen hero gets back the modifiers it already has (the game refreshes a modifier rather than adding a
	// second copy), so each is tracked once and Unfreeze removes it once.
	private static void Track(List<(CBaseModifier Modifier, bool IsRoot)> applied, CBaseModifier modifier, bool isRoot) {
		if (!applied.Exists(entry => entry.Modifier.Handle == modifier.Handle))
			applied.Add((modifier, isRoot));
	}

	// Our modifiers that are still on the hero. Timed ones leave by themselves, and the game frees them when they do.
	private static List<(CBaseModifier Modifier, bool IsRoot)> StillApplied(CCitadelPlayerPawn pawn) {
		if (!_applied.TryGetValue(pawn.EntityHandle, out var applied))
			return [];
		var present = pawn.IsValid ? pawn.ModifierProp?.Modifiers ?? [] : [];
		applied.RemoveAll(entry => !present.Any(modifier => modifier.Handle == entry.Modifier.Handle));
		if (applied.Count == 0)
			_applied.Remove(pawn.EntityHandle);
		return [.. applied];
	}
}

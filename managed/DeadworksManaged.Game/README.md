# DeadworksManaged.Game

Everything the game describes about itself, as C#: schema classes and enums, entities and
their spawn key values, inputs and outputs, console variables and commands, and hero,
ability, item and modifier names. It is generated from one build of the
[Deadworks modding database](https://deadworks.net/db) and committed, so each game update
is a pull request that shows what the game changed.

`DeadworksManaged.Api` is the curated layer: safe, documented, and small. This assembly is
the raw layer underneath it, under the game's own names. Reach for it when the curated API
does not wrap what you need. The host loads it next to the API, so a plugin references it
the same way and does not ship a copy.

```csharp
using DeadworksManaged.Api;
using DeadworksManaged.Game;

// Any curated wrapper has .Schema: every field the game declares for it.
float stamina = pawn.Schema.m_CCitadelAbilityComponent.m_ResourceStamina.m_flCurrentValue;

// An entity is the generated class of what it is.
foreach (var trooper in GameEntities.OfType<Schema.CNPC_Trooper>()) trooper.m_iLane = 1;
if (entity.Schema is Schema.CDynamicProp prop) prop.InputColor(new Color32(255, 0, 0));

// A function per entity the game can create, with the key values that entity reads.
var text = Spawn.point_worldtext(new() { origin = position, message = "hello", targetname = "sign" });

ConVars.citadel_trooper_gold_reward.Value = 40;
pawn.AddItem(ItemNames.upgrade_fleetfoot_boots);

[EntityOutputHook(EntityNames.trigger_multiple, Outputs.CBaseTrigger.OnStartTouch)]
```

## What is in it

| | |
|---|---|
| `Schema.*` | A class per schema class the server uses, with real inheritance, and every schema enum. |
| `entity.Schema`, `GameEntities` | From a curated wrapper, a handle or the entity list to the generated class. |
| `Spawn.*`, `Keys.*` | A spawn function per entity class, and its key values as typed properties. |
| `Input…()` methods, `Inputs.*`, `Outputs.*` | Entity inputs as methods, and every input and output name. |
| `ConVars.*`, `Commands.*` | Console variables with their declared types, and console commands. |
| `HeroNames`, `AbilityNames`, `ItemNames`, `ModifierNames`, `EntityNames` | Every name the curated API takes as a string. |
| `GameBuild` | The build the assembly was generated from. |

## How it behaves

- **Offsets are looked up in the running game**, by class and field name, the first time a
  field is used. A game update that moves a field needs nothing. One that renames or removes
  a field makes that member throw `SchemaFieldMissingException`, naming the field.
  `dw_schema_verify` checks every generated field against the running game at once.
- **A view does not outlive its entity.** An entity is found again through its handle on
  every access, and a struct inside it through its owner. Once the entity is gone,
  `IsValid` is false and member access throws instead of reading freed memory.
- **Writes are networked.** Setting a networked field tells the game, through the struct's
  own network chain if it has one and otherwise through the entity it is stored in.
- **Raw means raw.** A setter is a plain write. Where a plain write is known to break
  something (`m_nLevel`, the move type), the setter carries an `[Obsolete]` warning that
  says what to use; add to `DeadworksManaged.GameGen/overrides.json` when you find another.
- **Every field is present.** A field whose type has no typed mapping yet is a `RawField`:
  its address and the game's name for its type.
- A spawn function exists for every entity class except abilities, items and players. That
  does not promise an entity works alone on a dedicated server.
- `ConVars` lists what a dedicated server could have; a few rendering and Panorama
  variables are not registered on one. `Exists` tells.

## Regenerating

`.github/workflows/update-game.yml` regenerates from the newest build and opens a pull
request. By hand:

```sh
dotnet run --project managed/DeadworksManaged.GameGen -- [--build N|latest] [--report FILE] [--check-api]
```

`Generated/` is rewritten to match the dump exactly: unchanged files are left alone and
files for things the game no longer has are deleted. `--check-api` fails when a handwritten
`SchemaAccessor` in `DeadworksManaged.Api` names a field the build does not have, and the
report lists them.

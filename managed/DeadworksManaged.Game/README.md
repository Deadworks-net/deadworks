# DeadworksManaged.Game

Everything the game describes about itself, as C#: schema classes and enums, entities and
their spawn key values, inputs and outputs, console variables and commands, and hero,
ability, item and modifier names. It is generated from one build of the
[Deadworks modding database](https://deadworks.net/db) and committed, so each game update
is a pull request that shows what the game changed.

`DeadworksManaged.Api` is the curated layer: safe, documented, and small. This assembly is
the raw layer underneath it, under the game's own names. Reach for it when the curated API
does not wrap what you need.

## Using it

The host loads the assembly next to the API, so a plugin references it the same way and
does not ship a copy:

```xml
<Reference Include="DeadworksManaged.Game">
  <HintPath>C:\Program Files (x86)\Steam\steamapps\common\Deadlock\game\bin\win64\managed\DeadworksManaged.Game.dll</HintPath>
  <Private>false</Private>
</Reference>
```

```csharp
using DeadworksManaged.Api;
using DeadworksManaged.Game;

// Any curated wrapper has .Schema: every field the game declares for it.
var pawn = caller.GetHeroPawn()!.Schema;
float stamina = pawn.m_CCitadelAbilityComponent.m_ResourceStamina.m_flCurrentValue;
pawn.m_flRespawnTime = 5f;

// And back: .Entity is the curated wrapper, typed where one exists.
pawn.Entity?.AddItem(ItemNames.upgrade_fleetfoot_boots);

// An entity is the generated class of what it is.
foreach (var trooper in GameEntities.OfType<Schema.CNPC_Trooper>()) trooper.m_iLane = 1;
if (entity.Schema is Schema.CDynamicProp prop) prop.InputColor(new Color32(255, 0, 0));

// A function per entity the game can create, with the key values that entity reads.
var sign = Spawn.point_worldtext(new() { origin = position, message = "hello", targetname = "sign" });

// A unit, pickup or breakable is created from its data entry.
var guardian = Spawn.npc_boss_tier1(new() { origin = position, teamnumber = 2 }, beforeSpawn: boss => boss.m_iLane = 1);

// The data an ability, unit or modifier was created from: properties by name, the modifiers
// it defines, sounds, particles, weapon numbers.
if (ability.VData is { } data) {
    string? cooldown = data.m_mapAbilityProperties["AbilityCooldown"]?.m_strValue;
    string? intrinsic = data.m_AutoIntrinsicModifiers.FirstOrDefault()?.EntryName;
}

if (!ConVars.sv_cheats) ConVars.sv_cheats.Value = true;
ConCommands.changelevel("dl_midtown");

[EntityOutputHook(EntityNames.trigger_multiple, Outputs.CBaseTrigger.OnStartTouch)]
```

## What is in it

| | |
|---|---|
| `Schema.*` | A class per schema class the server uses, with real inheritance, and every schema enum. |
| `wrapper.Schema`, `view.Entity` | Between a curated wrapper and its generated class, both ways. `.Schema` needs only `using DeadworksManaged.Api`. |
| `GameEntities` | The entity list, by index or handle, as generated classes. |
| `Spawn.*`, `Keys.*` | A spawn function per entity and per data entry that survives being spawned, and its key values as typed properties. |
| `VData` | On an ability, a unit or a modifier: the data entry it was created from, as its own class. |
| `Input…()` methods, `Inputs.*`, `Outputs.*` | Entity inputs as methods, and every input and output name. |
| `ConVars.*`, `ConCommands.*` | Console variables with their declared types, and console commands. |
| `HeroNames`, `AbilityNames`, `ItemNames`, `ModifierNames`, `EntityNames`, `SubclassNames` | Every name the curated API takes as a string. |
| `GameBuild` | The build the assembly was generated from. |

## How it behaves

- **Offsets are looked up in the running game**, by class and field name, the first time a
  field is used. A game update that moves a field needs nothing. One that renames or removes
  a field makes that member throw `SchemaFieldMissingException`, naming the field.
  `dw_schema_verify` checks every generated field against the running game at once.
- **A view does not outlive its entity.** An entity is found again through its handle on
  every access, and a struct inside it through its owner. Once the entity is gone,
  `IsValid` is false and member access throws instead of reading freed memory.
- **Two views of one object are equal**, with `==` as well as `Equals`.
- **`.Schema` looks the entity up each time.** Keep the view in a local when you read
  several fields.
- **Writes are networked.** Setting a networked field tells the game, through the struct's
  own network chain if it has one and otherwise through the entity it is stored in.
- **Raw means raw.** A setter is a plain write. Where a plain write is known to break
  something, the setter carries an `[Obsolete]` warning that says what to use. That is
  every field the curated API exposes read-only beside a `SetX` method, found by the
  generator, plus the entries in `DeadworksManaged.GameGen/overrides.json`.
- **Every field is present.** A field whose type has no typed mapping yet is a `RawField`:
  its address and the game's name for its type.
- **Collections are views.** A vector or fixed array is a list; a `CUtlDict` or
  `CUtlOrderedMap` is a `SchemaDict`, `SchemaMap` or one of their plain-value forms, read by
  key or enumerated in the order the game filled it. The collections are read-only; what
  they hold can be written through.
- **The game types some members as a base class.** `As<T>()` on an entity checks the
  class; `Cast<T>()` on any view and `AsSchema<T>()` on any curated wrapper do not.

## Spawning

What the game can create and what survives being created are different lists, and only
trying tells them apart. The spawn survey (`tools/spawn-survey`) spawns every candidate name
alone on a real dedicated server; `DeadworksManaged.GameGen/spawn-survey.json` is its result,
and the generator reads it:

- A name that took the server down, hung it or was refused has no function.
- Every function's documentation says how it fared: lived, or removed itself at once (it
  may need key values or another entity), and whether its model was missing on the map.
- A unit, pickup or breakable is created from its data entry (`Spawn.trooper_melee`). The
  entity those entries are made from has a function under its own name only where the
  survey saw it survive without one: a pickup does, a trooper does not.
- A name the survey has not tried (new in a later build) still gets a function, and says so.

Also:

- An entity's model has to be loaded. One the map does not already use needs
  `Precache.AddResource` in `OnPrecacheResources`, or the engine reports a nonresident asset.
- There is no function for abilities, items or players: a hero holds the first two
  (`AddAbility`, `AddItem`) and the engine makes the last.
- Living for half a second alone is all the survey checks. It does not check that the
  entity looks or behaves right.

## Console variables

`ConVars` has the variables a dedicated server registers. The survey also checks which of
the variables the game declares a server never registers (rendering and Panorama ones,
which no flag marks) and the generator leaves those out.

## Regenerating

`.github/workflows/update-game.yml` regenerates from the newest build and opens a pull
request. By hand:

```sh
dotnet run --project managed/DeadworksManaged.GameGen -- --help
```

`Generated/` is rewritten to match the dump exactly: unchanged files are left alone and
files for things the game no longer has are deleted. `--check-api` fails when a handwritten
`SchemaAccessor` in `DeadworksManaged.Api` names a field the build does not have, and the
report lists them.

The spawn survey is run by hand, on a machine with the game, after an update that adds or
changes entities. It needs a private copy of the server, because it crashes its servers on
purpose:

```sh
dotnet run --project managed/DeadworksManaged.GameGen -- --survey-dir WORK
dotnet build tools/spawn-survey/SpawnSurvey -c Release -o WORK/plugin   # then copy SpawnSurvey.dll to the server's managed/plugins
python tools/spawn-survey/survey.py --server-dir "<server>/game/bin/win64" --work WORK
dotnet run --project managed/DeadworksManaged.GameGen
```

It takes about half an hour for 800 names on three servers. The servers run on a Windows
desktop of their own, so their console windows and anything else they open stay off your
screen, and the plugin ends a server silently when it faults. A few crashes cannot be
intercepted that way and make Windows raise an error box; the runner closes it at once and
says at the end how many it closed (2 in the build 6759 run).

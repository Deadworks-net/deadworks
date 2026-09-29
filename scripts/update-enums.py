#!/usr/bin/env python3
"""
Checks Deadworks' copies of the game's enums against the game and updates them.

The Deadworks modding database (https://deadworks.net/db) dumps every Deadlock
build: its schema enums, and each hero's ID from heroes.vdata. Valve renumbers
enums such as EModifierState when it inserts values, so a plugin using a stale
value silently does something else. This script rewrites every value that
moved and adds members the game gained, in the managed API and the native
SDK alike. Members the game no longer has are reported and left for a person
to remove.

Usage: python scripts/update-enums.py [--build N] [--schemas FILE] [--report FILE]
"""

import argparse
import json
from collections import Counter
import re
import sys
import urllib.request
from pathlib import Path

API = "https://deadworks.net/api/moddb/builds"
MANAGED = "managed/DeadworksManaged.Api/Enums/"
NATIVE = "deadworks/src/SDK/"

# Each copy of a game enum: the file and the enum in it, and the game enum it
# mirrors. `prefixes` are stripped from the game's names before matching them
# to ours (as is the prefix all of them share), `aliases` map our name to the
# game's where they differ by more than that, and `subset` enums only mirror
# the members Deadworks uses, so the game's others are not added. Members are
# added with Pascal case names, or with the game's own where `game_names`.
ENUMS = [
    {"file": MANAGED + "AbilitySlot.cs", "enum": "EAbilitySlot", "game": "EAbilitySlots_t", "prefixes": ["ESlot_Ability_", "ESlot_"], "subset": True},
    {"file": MANAGED + "CameraSetting.cs", "enum": "CameraSetting", "game": "CameraParam", "subset": True,
     "aliases": {"Target": "k_EParam_TargetPosition", "VerticalOffset": "k_EParam_VertOffset", "HorizontalOffset": "k_EParam_HorizOffset"}},
    {"file": MANAGED + "ECitadelDamageType.cs", "enum": "ECitadelDamageType", "game": "ECitadelDamageType"},
    {"file": MANAGED + "ECitadelGameMode.cs", "enum": "ECitadelGameMode", "game": "ECitadelGameMode", "aliases": {"OneVOneTest": "k_ECitadelGameMode_1v1Test"}},
    {"file": MANAGED + "ECitadelMatchMode.cs", "enum": "ECitadelMatchMode", "game": "ECitadelMatchMode", "aliases": {"Calibration": "k_ECitadelMatchMode_NewPlayerPlacement"}},
    {"file": MANAGED + "ECurrencySource.cs", "enum": "ECurrencySource", "game": "ECurrencySource"},
    {"file": MANAGED + "ECurrencyType.cs", "enum": "ECurrencyType", "game": "ECurrencyType"},
    {"file": MANAGED + "EGameState.cs", "enum": "EGameState", "game": "EGameState"},
    {"file": MANAGED + "EKnockDownTypes.cs", "enum": "EKnockDownTypes", "game": "EKnockDownTypes"},
    {"file": MANAGED + "EModifierEvent.cs", "enum": "EModifierEvent", "game": "EModifierEvent"},
    {"file": MANAGED + "EntityFlags.cs", "enum": "EntityFlags", "game": "Flags_t"},
    {"file": MANAGED + "FieldType.cs", "enum": "FieldType", "game": "fieldtype_t", "subset": True},
    {"file": MANAGED + "HorizontalJustify.cs", "enum": "HorizontalJustify", "game": "PointWorldTextJustifyHorizontal_t"},
    {"file": MANAGED + "ImbueEffects.cs", "enum": "ImbueEffects", "game": "ECitadelTargetAbilityEffects",
     "prefixes": ["CITADEL_TARGET_ABILITY_BEHAVIOR_IMBUE_"], "aliases": {"ActiveNonUltimate": "CITADEL_TARGET_ABILITY_BEHAVIOR_IMBUE_ACTIVE_NON_ULT"}},
    {"file": MANAGED + "InputButton.cs", "enum": "InputButton", "game": "InputBitMask_t", "subset": True},
    {"file": MANAGED + "LifeState.cs", "enum": "LifeState", "game": "LifeState_t"},
    {"file": MANAGED + "ModifierState.cs", "enum": "EModifierState", "game": "EModifierState",
     "aliases": {"SilenceMovementAbilities": "MODIFIER_STATE_SILENCE_MOVEMENT_ABILITES"}},
    {"file": MANAGED + "MoveType.cs", "enum": "MoveType", "game": "MoveType_t"},
    {"file": MANAGED + "ObserverMode_t.cs", "enum": "ObserverMode_t", "game": "ObserverMode_t", "prefixes": ["OBS_MODE_"]},
    {"file": MANAGED + "SolidType.cs", "enum": "SolidType", "game": "SolidType_t"},
    {"file": MANAGED + "TakeDamageFlags.cs", "enum": "TakeDamageFlags", "game": "TakeDamageFlags_t", "prefixes": ["DFLAG_"]},
    {"file": MANAGED + "VerticalJustify.cs", "enum": "VerticalJustify", "game": "PointWorldTextJustifyVertical_t"},
    {"file": NATIVE + "Enums.hpp", "enum": "ECurrencyType", "game": "ECurrencyType"},
    {"file": NATIVE + "Enums.hpp", "enum": "ECurrencySource", "game": "ECurrencySource"},
    {"file": NATIVE + "Enums.hpp", "enum": "EModifierEvent", "game": "EModifierEvent", "game_names": True},
    {"file": NATIVE + "CitadelAbilityVData.hpp", "enum": "ECitadelTargetAbilityEffects", "game": "ECitadelTargetAbilityEffects"},
]
HEROES = {"file": MANAGED + "Heroes.cs", "enum": "Heroes"}

# Game members that count or bound the others rather than name a value.
SENTINEL = re.compile(r"^NUM_|_LAST$|_LAST_|LASTDFLAG|_MAX_BITS$|_INVALID$|^EMax|Count$|_FIRST_MOD_SPECIFIC_BIT$")
MEMBER = re.compile(r"^(?P<indent>[ \t]*)(?P<name>\w+)(?P<gap>\s*)=\s*(?P<expr>[^,/\n]+?)\s*,", re.M)
UNDERLYING_BITS = {"byte": 8, "sbyte": 8, "uint8_t": 8, "int8_t": 8, "ushort": 16, "short": 16, "uint16_t": 16, "int16_t": 16,
                   "ulong": 64, "long": 64, "uint64_t": 64, "int64_t": 64}


def get_project_root() -> Path:
    return Path(__file__).resolve().parent.parent


def fetch_json(url: str):
    print(f"Fetching {url} ...")
    # The site turns away Python's default user agent.
    request = urllib.request.Request(url, headers={"User-Agent": "deadworks-update-enums (+https://github.com/Deadworks-net/deadworks)"})
    with urllib.request.urlopen(request, timeout=120) as response:
        return json.load(response, parse_int=exact_int)


def exact_int(text: str) -> int:
    """The site writes JSON from JavaScript, so a 64-bit flag such as 1 << 56 arrives as the
    double 72057594037927940. Going through float gets the exact value back."""
    value = int(text)
    return int(float(text)) if abs(value) > 1 << 53 else value


def read_source(path: Path) -> str:
    """The file as it is, CRLF or LF, so writing it back changes only what was updated."""
    with open(path, encoding="utf-8", newline="") as f:
        return f.read()


def common_prefix(names: list[str]) -> str:
    """The longest prefix up to an underscore that most names share: LIFE_ for LIFE_ALIVE, which
    NUM_LIFESTATES doesn't share."""
    counts = Counter(name[:i + 1] for name in names for i, c in enumerate(name) if c == "_")
    return max((p for p, n in counts.items() if n * 2 >= len(names)), key=len, default="")


def key(name: str) -> str:
    return re.sub(r"[^a-z0-9]", "", name.lower())


def pascal(name: str) -> str:
    """MODIFIER_STATE_NO_TARGET's remainder NO_TARGET -> NoTarget; EItemGooseEgg stays as it is."""
    if "_" not in name and not name.isupper():
        return name[:1].upper() + name[1:]
    return "".join(part[:1].upper() + part[1:].lower() for part in name.split("_") if part)


def evaluate(expr: str, known: dict[str, int]) -> int | None:
    expr = re.sub(r"(?<=[0-9A-Fa-f])(?:ul|UL|u|U|l|L)\b", "", expr.strip())
    try:
        return int(eval(expr, {"__builtins__": {}}, dict(known)))
    except Exception:
        return None


class Enum:
    """One enum declaration in a source file: where its body is, how wide it is, and whether it is [Flags]."""

    def __init__(self, text: str, name: str):
        declaration = re.search(rf"\benum\s+(?:class\s+)?{name}\b\s*(?::\s*(\w+))?", text)
        if not declaration:
            raise ValueError(f"no enum {name}")
        self.start = text.index("{", declaration.end()) + 1
        self.end = text.index("}", self.start)
        self.bits = UNDERLYING_BITS.get(declaration.group(1), 32)
        self.mask = (1 << self.bits) - 1
        self.flags = "[Flags]" in text[max(0, declaration.start() - 300):declaration.start()].split("}")[-1]

    def members(self, body: str) -> list[tuple[re.Match, int | None]]:
        out, known = [], {}
        for m in MEMBER.finditer(body):
            value = evaluate(m.group("expr"), known)
            if value is not None:
                known[m.group("name")] = value
            out.append((m, value))
        return out


def formatted(value: int, like: str, bits: int) -> str:
    """A value written the way the member it replaces (or sits beside) was."""
    value &= (1 << bits) - 1
    like = like.strip()
    if "<<" in like and value and value & (value - 1) == 0:
        suffix = re.search(r"1(\w*)\s*<<", like).group(1)
        return f"1{suffix} << {value.bit_length() - 1}"
    if like.lower().startswith("0x"):
        digits = f"{value:x}"
        return "0x" + (digits.upper() if any(c.isupper() for c in like[2:]) else digits)
    return str(value)


def is_combination(value: int | None, others: list[int], mask: int) -> bool:
    """In a [Flags] enum, None, All and members made of other members' flags (AllItems) are ours, not the game's."""
    if value is None:
        return False
    value &= mask
    if value in (0, mask):
        return True
    flags = {v & mask for v in others if v > 0 and v & (v - 1) == 0}
    return value & (value - 1) != 0 and all(1 << b in flags for b in range(mask.bit_length()) if value >> b & 1)


def insert_member(body: str, enum: Enum, name: str, value: int) -> str:
    """Adds a member after the last one with a lower value, formatted like it."""
    members = [(v & enum.mask, m) for m, v in enum.members(body) if v is not None]
    lower = [m for v, m in members if v < value]
    anchor = max(lower, key=lambda m: m.start()) if lower else members[0][1]
    # Columns stay lined up where the file lines them up.
    gap = anchor.group("gap") if len(anchor.group("gap")) <= 1 else " " * max(1, len(anchor.group("name")) + len(anchor.group("gap")) - len(name))
    newline = "\r\n" if "\r\n" in body else "\n"
    line = f"{anchor.group('indent')}{name}{gap}= {formatted(value, anchor.group('expr'), enum.bits)},{newline}"
    # A new line goes after the whole line of the member before it, trailing comment and all.
    at = body.index("\n", anchor.end()) + 1 if lower else anchor.start()
    return body[:at] + line + body[at:]


def update_enum(path: Path, spec: dict, game: dict[str, int]) -> list[str]:
    """Rewrites one enum in `path` to match `game` (name -> value). Returns what it changed or found."""
    text = read_source(path)
    enum = Enum(text, spec["enum"])
    # EGameState_Init is Init; ESlot_Ability_Held is Held or Ability_Held; CITADEL_TARGET_ABILITY_BEHAVIOR_IMBUE_ACTIVE
    # is Active or ImbueActive, so every shorter prefix of the one most names share is tried too.
    shared = common_prefix(list(game))
    prefixes = [*spec.get("prefixes", []), *(shared[:i + 1] for i in range(len(shared) - 1, -1, -1) if shared[i] == "_")]
    stripped = {}
    for name in game:
        for short in [name] + [name[len(p):] for p in prefixes if name.startswith(p)]:
            stripped.setdefault(key(short), name)
    aliases = spec.get("aliases", {})

    body = text[enum.start:enum.end]
    ours = enum.members(body)
    # New members are named like the existing ones: whichever prefix they drop.
    ours_keys = {key(m.group("name")) for m, _ in ours}
    naming = max(prefixes, key=lambda p: sum(key(n[len(p):]) in ours_keys for n in game if n.startswith(p)), default="")
    known = {m.group("name"): v for m, v in ours if v is not None}
    notes, matched, kept, replacements = [], set(), set(), {}
    for m, have in ours:
        name = m.group("name")
        game_name = aliases.get(name) or stripped.get(key(name))
        if not game_name:
            if enum.flags and is_combination(have, [v for n, v in known.items() if n != name], enum.mask):
                kept.add(have & enum.mask)
            else:
                notes.append(f"`{name}` is not in the game any more")
            continue
        matched.add(game_name)
        want = game[game_name] & enum.mask
        if have is None or have & enum.mask != want:
            replacements[m] = formatted(want, m.group("expr"), enum.bits)
            notes.append(f"`{name}` {have} -> {want}")
    for m in sorted(replacements, key=lambda m: m.start(), reverse=True):
        s, e = m.span("expr")
        body = body[:s] + replacements[m] + body[e:]

    if not spec.get("subset"):
        # A game member with the value of one we matched is an alias (ESlot_Signature_First).
        represented = {game[n] & enum.mask for n in matched} | kept
        missing = [(n, v & enum.mask) for n, v in game.items() if n not in matched and not SENTINEL.search(n) and v & enum.mask not in represented]
        for game_name, value in sorted(missing, key=lambda item: item[1]):
            short = game_name[len(naming):] if naming and game_name.startswith(naming) else \
                next((game_name[len(p):] for p in prefixes if game_name.startswith(p)), game_name)
            name = game_name if spec.get("game_names") else pascal(short)
            body = insert_member(body, enum, "_" + name if name[:1].isdigit() else name, value)
            notes.append(f"added `{name}` = {value}")

    path.write_text(text[:enum.start] + body + text[enum.end:], encoding="utf-8", newline="")
    return notes


def update_heroes(path: Path, heroes: list[dict]) -> list[str]:
    """Heroes mirrors each hero's m_HeroID. HeroTypeExtensions turns a member into its hero
    name by lowercasing it (Inferno -> hero_inferno), so the name has to match the game's too."""
    game = {}
    for hero in heroes:
        hero_id = (hero.get("vdata") or {}).get("m_HeroID")
        # hero_base is what the heroes inherit from, with ID 0.
        if isinstance(hero_id, int) and hero_id > 0 and hero["name"].startswith("hero_"):
            game[hero["name"]] = hero_id
    by_id = {hero_id: name for name, hero_id in game.items()}
    text = read_source(path)
    enum = Enum(text, HEROES["enum"])
    body = text[enum.start:enum.end]
    ours = {m.group("name"): v for m, v in enum.members(body)}
    notes = []
    for name, value in ours.items():
        game_id = game.get("hero_" + name.lower())
        if game_id is None and value in by_id:
            notes.append(f"`{name}` is hero ID {value}, which the game calls {by_id[value]}")
        elif game_id is None:
            notes.append(f"`{name}` is not in the game any more")
        elif game_id != value:
            notes.append(f"`{name}` is hero ID {game_id}, not {value}")
    for hero_name, hero_id in sorted(game.items(), key=lambda item: item[1]):
        if hero_id not in ours.values():
            name = pascal(hero_name[len("hero_"):])
            body = insert_member(body, enum, name, hero_id)
            notes.append(f"added `{name}` = {hero_id} ({hero_name})")
    path.write_text(text[:enum.start] + body + text[enum.end:], encoding="utf-8", newline="")
    return notes


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    parser.add_argument("--build", default="latest", help="Deadlock build to match (default: the newest dumped)")
    parser.add_argument("--schemas", help="a DumpSource2 schemas.json to read instead of the database's")
    parser.add_argument("--report", help="write what changed here, as Markdown")
    args = parser.parse_args()

    project_root = get_project_root()
    schemas = json.loads(Path(args.schemas).read_text(encoding="utf-8"), parse_int=exact_int) if args.schemas else fetch_json(f"{API}/{args.build}/schemas.json")
    vdata = fetch_json(f"{API}/{args.build}/vdata.json")
    # The same enum is in several modules; the server's is the one plugins run against.
    game_enums = {}
    for enum in sorted(schemas["enums"], key=lambda e: e["module"] != "server"):
        game_enums.setdefault(enum["name"], {m["name"]: m["value"] for m in enum.get("members", [])})

    report = [f"Enums checked against Deadlock build {vdata['version']}.", ""]
    for spec in ENUMS:
        where = f"{Path(spec['file']).name} {spec['enum']}"
        game = game_enums.get(spec["game"])
        if game is None:
            report.append(f"- **{where}**: the game has no `{spec['game']}` any more")
            continue
        report += [f"- **{where}** ({spec['game']}): {note}" for note in update_enum(project_root / spec["file"], spec, game)]
    report += [f"- **Heroes.cs Heroes**: {note}" for note in update_heroes(project_root / HEROES["file"], vdata["heroes"])]

    text = "\n".join(report) + "\n"
    print(text)
    if args.report:
        Path(args.report).write_text(text, encoding="utf-8")


if __name__ == "__main__":
    main()

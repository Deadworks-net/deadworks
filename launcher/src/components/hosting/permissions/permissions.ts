// Deadworks' permission files (game/bin/win64/configs/permissions/) and the rules it applies to them,
// mirrored from managed/Permissions/*.cs so the Admins tab can show who can run what without asking the
// server. A TypeScript port of deadworks-web's app/hosting/permissions.js; keep the two in step.
//
// The tab must never show less access than the server grants, so the files are read exactly the way
// the server reads them (see "The files as the server reads them" below), not with JSON.parse and
// lowercase key lookups.

export const PERMISSIONS_DIR = "bin/win64/configs/permissions";
export const ROLES_PATH = `${PERMISSIONS_DIR}/roles.jsonc`;
export const PLAYERS_PATH = `${PERMISSIONS_DIR}/players.jsonc`;
export const OVERRIDES_PATH = `${PERMISSIONS_DIR}/overrides.jsonc`;
export const GENERATED_DIR = `${PERMISSIONS_DIR}/generated`;
export const DEFAULT_ROLE = "default";

/** Paths (relative to the server's `game` folder) to file text, null when the file is missing. */
export type PermissionFiles = Record<string, string | null>;

export interface RoleDef {
  permissions?: string[];
  inherits?: string[];
  immunity?: number | null;
}
export type RoleMap = Record<string, RoleDef | null>;

/** A roles.jsonc entry with every field filled in. */
export interface RoleEntry {
  permissions: string[];
  inherits: string[];
  immunity: number | null;
}

/** A players.jsonc entry as written in the file. */
export interface RawPlayer {
  name?: string | null;
  roles?: string[];
  permissions?: string[];
  immunity?: number | null;
}
export type PlayerMap = Record<string, RawPlayer | null>;

/** A players.jsonc entry with every field filled in. */
export interface PlayerEntry {
  name: string | null;
  roles: string[];
  permissions: string[];
  immunity: number | null;
}

/** Anything with roles and grants of its own: a listed player, or a bare role list. */
export type EntryLike = {
  roles?: string[] | null;
  permissions?: string[] | null;
  immunity?: number | null;
} | null | undefined;

export interface Person {
  /** The key as written in players.jsonc. */
  key: string;
  /** SteamID64, or null when the key is not a SteamID. */
  id: string | null;
  entry: PlayerEntry;
}

/** A command as listed in generated/<Plugin>.jsonc. */
export interface CommandInfo {
  name: string;
  aliases?: string[];
  description?: string;
  /** The permission in effect when the file was written. */
  permission?: string;
  /** What the plugin asks for; only present when an override was in effect. */
  declaredPermission?: string;
  targetImmunity?: string;
}

export interface DeclaredPermission {
  tag: string;
  description?: string;
  declaredBy?: string[];
}

export interface PluginInfo {
  /** File name without .jsonc. */
  file: string;
  /** The generated file's path, as keyed in the files map. */
  path: string;
  plugin: string;
  core: boolean;
  commands: CommandInfo[];
  permissions: DeclaredPermission[];
}

export interface Problem {
  level: "error" | "warn" | "info";
  text: string;
  path?: string;
}

/**
 * The files as the server reads them: one role per name, one entry per player and one override per
 * command, each being the one the server uses when the file says it more than once.
 */
export interface PermissionModel {
  /** Keyed by the name as first written, which is the name the server shows. */
  roles: RoleMap;
  /** Keyed like `people`. */
  players: PlayerMap;
  /** Keyed by command as the server normalizes it: "cmd" or "plugin:cmd", without "!", "/" or "dw_". */
  overrides: Record<string, string>;
  plugins: PluginInfo[];
  /** One per player. Keys that are not SteamIDs are listed too, so they can be fixed. */
  people: Person[];
  problems: Problem[];
}

// Drops // and /* */ comments, leaving anything inside strings alone.
export const stripComments = (text: string): string =>
  String(text).replace(/("(?:[^"\\]|\\.)*")|\/\/[^\n]*|\/\*[\s\S]*?\*\//g, (_m, str: string | undefined) => str || "");

const baseName = (path: string): string => path.slice(path.lastIndexOf("/") + 1);

// The permission files are read with AllowTrailingCommas, unlike plugin configs.
const plainJson = (text: string): string =>
  stripComments(text).replace(
    /("(?:[^"\\]|\\.)*")|,(\s*[}\]])/g,
    (_m, str: string | undefined, close: string | undefined) => str || close || ""
  );
export function parsePermissionFile(text: string | null | undefined): { value: unknown; error: string | null } {
  if (text == null) return { value: null, error: null };
  try {
    return { value: JSON.parse(plainJson(text)) as unknown, error: null };
  } catch (e) {
    return { value: null, error: e instanceof Error ? e.message : String(e) };
  }
}

// Only individual accounts count (SteamIds.IsIndividual): the base plus a 32-bit account number other than 0.
const BASE = BigInt("76561197960265728");
const LAST = BASE + BigInt("4294967295");
const individual = (id: bigint): string | null => (id > BASE && id <= LAST ? String(id) : null);

// SteamID64 from what someone typed: a SteamID64, Steam2 (STEAM_1:0:123), Steam3 ([U:1:246]) or a /profiles/ link.
export function parseSteamId(input: unknown): string | null {
  const s = String(input || "").trim();
  let m: RegExpMatchArray | null;
  if ((m = s.match(/steamcommunity\.com\/profiles\/(\d{17})/i))) return individual(BigInt(m[1]));
  if ((m = s.match(/^\[?U:1:(\d+)\]?$/i))) return individual(BASE + BigInt(m[1]));
  return parsePlayerKey(s);
}
// SteamID64 from a players.jsonc key, or null for a key the server skips. Stricter than what the
// dialogs accept, because it has to agree with SteamIds.TryParse: no links, and Steam3 needs its brackets.
export function parsePlayerKey(key: string): string | null {
  const s = key.trim();
  let m: RegExpMatchArray | null;
  if (/^\d{17}$/.test(s)) return individual(BigInt(s));
  if ((m = s.match(/^STEAM_[0-5]:([01]):(\d+)$/i))) return individual(BASE + BigInt(m[2]) * BigInt(2) + BigInt(m[1]));
  if ((m = s.match(/^\[U:1:(\d+)\]$/i))) return individual(BASE + BigInt(m[1]));
  return null;
}
export const steam3 = (id64: string): string => `[U:1:${BigInt(id64) - BASE}]`;

// Whether two names are the same to the server, which compares role names, property names and
// command names with .NET's OrdinalIgnoreCase: character by character, by simple uppercase. That is
// not toLowerCase(): the Kelvin sign is not "k" and "ß" is not "ẞ", while "σ" and "ς" are the same.
// .NET keeps dotless i and long s apart from "I" and "S" (checked against .NET 10).
const foldChar = (c: string): string => {
  if (c === "\u0131" || c === "\u017f") return c;
  const upper = c.toUpperCase();
  return [...upper].length === 1 ? upper : c;
};
export const foldName = (name: string): string =>
  /^[ -~]*$/.test(name) ? name.toUpperCase() : Array.from(name, foldChar).join("");
export const sameName = (a: string, b: string): boolean => foldName(a) === foldName(b);

// One grant: "*", "a.b.*" or "a.b.c", optionally "-" to deny. Wildcards only count as a whole last segment.
export interface Grant {
  raw: string;
  deny: boolean;
  rank: number;
  all?: true;
  prefix?: string;
  exact?: string;
}
export const normalize = (p: unknown): string => String(p).trim().toLowerCase();
export function parseGrant(raw: string): Grant | null {
  let text = normalize(raw);
  let deny = false;
  if (text.startsWith("-")) {
    deny = true;
    text = text.slice(1).trim();
  }
  if (!text) return null;
  if (text === "*") return { raw, deny, all: true, rank: 0 };
  if (text.endsWith(".*")) {
    const prefix = text.slice(0, -1);
    return { raw, deny, prefix, rank: prefix.split(".").length };
  }
  if (text.includes("*")) return null;
  return { raw, deny, exact: text, rank: Infinity };
}
const matches = (g: Grant, perm: string): boolean =>
  g.all || (g.exact ? g.exact === perm : perm.startsWith(g.prefix ?? ""));
// Within one list the most specific matching grant wins, and a deny wins a tie.
function decideList(grants: Grant[], perm: string): Grant | null {
  let best: Grant | null = null;
  for (const g of grants) {
    if (matches(g, perm) && (!best || g.rank > best.rank || (g.rank === best.rank && g.deny && !best.deny))) best = g;
  }
  return best;
}
const grantsOf = (list: string[] | null | undefined): Grant[] =>
  (list || []).map((g) => parseGrant(g)).filter((g): g is Grant => g !== null);

interface RoleDecision {
  allowed: boolean;
  grant: string;
  role: string;
}

// A role's own grants decide if any match; otherwise any inherited role allowing is enough.
function decideRole(roles: RoleMap, name: string, perm: string, path: string[] = []): RoleDecision | null {
  const role = findRole(roles, name);
  if (!role || path.includes(foldName(name))) return null;
  const own = decideList(grantsOf(role.permissions), perm);
  if (own) return { allowed: !own.deny, grant: own.raw, role: name };
  let denied: RoleDecision | null = null;
  for (const parent of role.inherits || []) {
    const r = decideRole(roles, parent, perm, [...path, foldName(name)]);
    if (r?.allowed) return r;
    if (r && !denied) denied = r;
  }
  return denied;
}
// The last role of that name, as the server keeps it (PermissionManager.Normalize). The model's roles
// already hold one per name; this also holds for a map that does not.
export function findRole(roles: RoleMap, name: string): RoleDef | null | undefined {
  const wanted = foldName(String(name));
  let found: RoleDef | null | undefined;
  for (const [key, role] of Object.entries(roles)) if (foldName(key) === wanted) found = role;
  return found;
}
export const playerRoles = (entry: EntryLike): string[] => [
  DEFAULT_ROLE,
  ...(entry?.roles || []).filter((r) => r && !sameName(r, DEFAULT_ROLE)),
];

export interface Verdict {
  allowed: boolean;
  /** The grant that decided it. */
  grant?: string;
  /** "player", or "role <name>" with where it was inherited from. */
  source?: string | null;
  /** Set instead of a grant when nothing had to decide. */
  reason?: string;
}

// Whether a player (or a bare role list) has a permission, and the grant that decided it.
export function explain(model: Pick<PermissionModel, "roles">, entry: EntryLike, permission: string): Verdict {
  const perm = normalize(permission);
  if (!perm) return { allowed: true, reason: "Anyone can run it." };
  const own = decideList(grantsOf(entry?.permissions), perm);
  if (own) return { allowed: !own.deny, grant: own.raw, source: "player" };
  let denied: Verdict | null = null;
  for (const name of playerRoles(entry)) {
    const r = decideRole(model.roles, name, perm);
    const source = r && `role ${name}${r.role !== name ? ` (inherited from ${r.role})` : ""}`;
    if (r?.allowed) return { allowed: true, grant: r.grant, source };
    if (r && !denied) denied = { allowed: false, grant: r.grant, source };
  }
  return denied || { allowed: false, reason: "No grant matches." };
}
export function describe(result: Verdict): string {
  if (result.reason) return result.reason;
  return `${result.allowed ? "Allowed" : "Denied"} by "${result.grant}" from ${result.source === "player" ? "their own entry" : result.source}`;
}

// A role's own immunity, or the highest of the roles it inherits; a player's override, or the highest of their roles.
export function roleImmunity(roles: RoleMap, name: string, path: string[] = []): number {
  const role = findRole(roles, name);
  if (!role || path.includes(foldName(name))) return 0;
  if (Number.isInteger(role.immunity)) return role.immunity as number;
  return Math.max(0, ...(role.inherits || []).map((p) => roleImmunity(roles, p, [...path, foldName(name)])));
}
export function playerImmunity(roles: RoleMap, entry: EntryLike): { value: number; source: string | null } {
  if (Number.isInteger(entry?.immunity)) return { value: entry?.immunity as number, source: "set on the player" };
  let best: { value: number; source: string | null } = { value: 0, source: null };
  for (const name of playerRoles(entry)) {
    const v = roleImmunity(roles, name);
    if (v > best.value) best = { value: v, source: name };
  }
  return best;
}
export function inheritChain(roles: RoleMap, name: string, path: string[] = []): string[] {
  const role = findRole(roles, name);
  if (!role || path.includes(foldName(name))) return [];
  return (role.inherits || []).flatMap((p) => [p, ...inheritChain(roles, p, [...path, foldName(name)])]);
}

export interface ResolvedCommand {
  permission: string;
  overridden: boolean;
  /** The overrides.jsonc key that matched. */
  key?: string;
}

// The permission a command needs after overrides.jsonc: "Plugin:cmd" beats "cmd". `overrides` is the
// model's, keyed the way the server normalizes them (CommandOverrides.Resolve).
export function resolveCommand(
  overrides: Record<string, string> | null | undefined,
  plugin: Pick<PluginInfo, "plugin" | "file">,
  command: CommandInfo
): ResolvedCommand {
  const entries = Object.entries(overrides || {});
  const names = [command.name, ...(command.aliases || [])];
  for (const q of [plugin.plugin, plugin.file]) {
    for (const n of names) {
      const hit = entries.find(([k]) => sameName(k, `${q}:${n}`));
      if (hit) return { permission: hit[1], overridden: true, key: hit[0] };
    }
  }
  for (const n of names) {
    const hit = entries.find(([k]) => sameName(k, n));
    if (hit) return { permission: hit[1], overridden: true, key: hit[0] };
  }
  return { permission: command.declaredPermission ?? command.permission ?? "", overridden: false };
}

interface GeneratedFile {
  plugin?: string;
  commands?: CommandInfo[];
  permissions?: DeclaredPermission[];
}

// ---- The files as the server reads them ----
//
// Deadworks reads roles.jsonc, players.jsonc and overrides.jsonc with System.Text.Json and
// PropertyNameCaseInsensitive (ReadOptions in JsonPermissionStore.cs and CommandOverrides.cs), then
// copies them into dictionaries that ignore case. JSON.parse with lowercase lookups disagrees with
// that in ways that hide access the server grants:
//   - "Roles", "ROLES" and "roles" are one property;
//   - a property written more than once, in any capitalisation, has the value written last;
//   - role names, SteamIDs and command names that mean the same thing are one entry, and the last
//     one wins (PermissionManager.Normalize, JsonPermissionStore.ParsePlayers, CommandOverrides.Set).
// So a file is parsed keeping every member in file order, and read by the same rules. All of this was
// checked against the server's own code running on .NET 10.

/** One member of a JSON object: its name as written, and its value. */
type Member<T> = [key: string, value: T];

/** A JSON object with its members in file order, repeated names included (JSON.parse keeps one of each). */
class Members {
  constructor(readonly list: Member<Json>[]) {}
}
/** Stands for a number that is not a whole number an int can hold. Deadworks refuses those wherever these files take a number. */
const NOT_WHOLE = Symbol("not a whole number");
type Json = null | boolean | number | string | typeof NOT_WHOLE | Json[] | Members;

const STRING = /"(?:[^"\\]|\\.)*"/y;
const WORD = /[^\s,\]}]+/y;
const SPACE = /\s*/y;

// `json` has already passed JSON.parse; this walks it again without losing repeated names or their order.
function parseInOrder(json: string): Json {
  let at = 0;
  const take = (pattern: RegExp): string => {
    pattern.lastIndex = at;
    const m = pattern.exec(json);
    if (!m) throw new SyntaxError(`Unexpected text at position ${at}`);
    at = pattern.lastIndex;
    return m[0];
  };
  const peek = (): string => {
    take(SPACE);
    return json[at];
  };
  const value = (): Json => {
    const open = peek();
    if (open === "{" || open === "[") {
      const close = open === "{" ? "}" : "]";
      const members: Member<Json>[] = [];
      const items: Json[] = [];
      at++;
      for (let first = true; peek() !== close; first = false) {
        if (!first) at++; // the comma
        if (open === "[") {
          items.push(value());
          continue;
        }
        peek();
        const key = JSON.parse(take(STRING)) as string;
        peek();
        at++; // the colon
        members.push([key, value()]);
      }
      at++;
      return open === "{" ? new Members(members) : items;
    }
    if (open === '"') return JSON.parse(take(STRING)) as string;
    const word = take(WORD);
    const parsed = JSON.parse(word) as number | boolean | null;
    if (typeof parsed !== "number") return parsed;
    return /^-?\d+$/.test(word) && parsed >= -2147483648 && parsed <= 2147483647 ? parsed : NOT_WHOLE;
  };
  return value();
}

/** A shape System.Text.Json refuses. The server then cannot read the file at all, so neither does the tab. */
class Refused extends Error {}

interface Reading<T> {
  /** The file's entries in file order, every repeat included. */
  members: Member<T>[];
  /** Why the server cannot read the file, or null. */
  error: string | null;
  /** Things the file says that the server reads differently from how they look. */
  notes: Problem[];
}

function readFileAs<T>(text: string | null | undefined, read: (root: Json, notes: Problem[]) => Member<T>[]): Reading<T> {
  if (text == null) return { members: [], error: null, notes: [] };
  const json = plainJson(text);
  try {
    JSON.parse(json);
  } catch (e) {
    return { members: [], error: e instanceof Error ? e.message : String(e), notes: [] };
  }
  const notes: Problem[] = [];
  try {
    return { members: read(parseInOrder(json), notes), error: null, notes };
  } catch (e) {
    if (!(e instanceof Refused)) throw e;
    return { members: [], error: e.message, notes: [] };
  }
}

const quoted = (names: string[]): string => names.map((n) => `"${n}"`).join(", ");

type FieldKind = "text" | "list" | "whole";
const ROLE_FIELDS: Record<string, FieldKind> = { permissions: "list", inherits: "list", immunity: "whole" };
const PLAYER_FIELDS: Record<string, FieldKind> = { name: "text", roles: "list", permissions: "list", immunity: "whole" };

function readValue(raw: Json, kind: FieldKind, what: string): string | string[] | number | null {
  if (raw === null) return null;
  if (kind === "text") {
    if (typeof raw === "string") return raw;
    throw new Refused(`${what} must be text in quotes, so Deadworks cannot read the file`);
  }
  if (kind === "whole") {
    if (typeof raw === "number") return raw;
    throw new Refused(`${what} must be a whole number, so Deadworks cannot read the file`);
  }
  if (Array.isArray(raw) && raw.every((v) => v === null || typeof v === "string")) {
    // Blank and null items are dropped (JsonPermissionStore.Clean, PermissionManager.CleanList).
    return raw.filter((v): v is string => typeof v === "string" && v.trim() !== "");
  }
  throw new Refused(`${what} must be a list like ["a", "b"], so Deadworks cannot read the file`);
}

// One role or player entry: each known property matched ignoring case, the last one written winning.
// Every repeat is still checked, since the server reads (and can refuse) each one before replacing it.
function readFields(value: Json, fields: Record<string, FieldKind>, file: string, owner: string, notes: Problem[]) {
  const out: Record<string, string | string[] | number | null> = {};
  if (value === null) return out;
  if (!(value instanceof Members)) throw new Refused(`${owner} must be an object in { }, so Deadworks cannot read the file`);
  const written: Record<string, string[]> = {};
  for (const [key, raw] of value.list) {
    const field = Object.keys(fields).find((f) => sameName(f, key));
    if (!field) continue;
    out[field] = readValue(raw, fields[field], `"${key}" of ${owner}`);
    written[field] = [...(written[field] || []), key];
  }
  for (const [field, keys] of Object.entries(written)) {
    if (keys.length > 1) {
      const spelled = new Set(keys).size > 1 ? ` (written ${quoted(keys)})` : "";
      notes.push({
        level: "warn",
        text: `${file}: ${owner} sets "${field}" ${keys.length} times${spelled}. Deadworks uses the last one and ignores the rest; so does this tab.`,
      });
    } else if (keys[0] !== field) {
      notes.push({
        level: "info",
        text: `${file}: ${owner} writes "${field}" as "${keys[0]}". Deadworks ignores capitals in these names, so it still counts.`,
      });
    }
  }
  return out;
}

function readEntries<T>(root: Json, readOne: (key: string, value: Json) => T): Member<T>[] {
  if (root === null) return [];
  if (!(root instanceof Members)) throw new Refused("the file must hold one object in { }, so Deadworks cannot read it");
  return root.list.map(([key, value]) => [key, readOne(key, value)]);
}

const readRoles = (text: string | null | undefined): Reading<RoleEntry> =>
  readFileAs(text, (root, notes) =>
    readEntries(root, (key, value) => {
      const f = readFields(value, ROLE_FIELDS, "roles.jsonc", `role "${key}"`, notes);
      return {
        permissions: (f.permissions as string[] | null | undefined) ?? [],
        inherits: (f.inherits as string[] | null | undefined) ?? [],
        immunity: (f.immunity as number | null | undefined) ?? null,
      };
    })
  );

const readPlayers = (text: string | null | undefined): Reading<PlayerEntry> =>
  readFileAs(text, (root, notes) =>
    readEntries(root, (key, value) => {
      const f = readFields(value, PLAYER_FIELDS, "players.jsonc", `the entry "${key}"`, notes);
      return {
        name: (f.name as string | null | undefined) ?? null,
        roles: (f.roles as string[] | null | undefined) ?? [],
        permissions: (f.permissions as string[] | null | undefined) ?? [],
        immunity: (f.immunity as number | null | undefined) ?? null,
      };
    })
  );

// overrides.jsonc's commands as written: null where the file says null, which the server reads as "".
const readOverrides = (text: string | null | undefined): Reading<string | null> =>
  readFileAs(text, (root, notes) => {
    if (root === null) return [];
    if (!(root instanceof Members)) throw new Refused("the file must hold one object in { }, so Deadworks cannot read it");
    let commands: Member<string | null>[] = [];
    const written: string[] = [];
    for (const [key, value] of root.list) {
      if (!sameName(key, "commands")) continue;
      written.push(key);
      if (value !== null && !(value instanceof Members)) {
        throw new Refused(`"${key}" must map command names to permission names in { }, so Deadworks cannot read the file`);
      }
      // A later "commands" replaces an earlier one whole; the two are not merged.
      commands = (value?.list ?? []).map(([command, permission]) => {
        if (permission !== null && typeof permission !== "string") {
          throw new Refused(`"${command}" under "${key}" must be a permission name in quotes, so Deadworks cannot read the file`);
        }
        return [command, permission];
      });
    }
    if (written.length > 1) {
      const spelled = new Set(written).size > 1 ? ` (written ${quoted(written)})` : "";
      notes.push({
        level: "warn",
        text: `overrides.jsonc has "commands" ${written.length} times${spelled}. Deadworks uses the last one and ignores everything in the rest; so does this tab.`,
      });
    } else if (written.length === 1 && written[0] !== "commands") {
      notes.push({
        level: "info",
        text: `overrides.jsonc writes "commands" as "${written[0]}". Deadworks ignores capitals in that name, so it still counts.`,
      });
    }
    return commands;
  });

// What a .NET Dictionary holds once System.Text.Json has filled it: a key written twice keeps its
// first position and takes its last value.
function asDictionary<T>(members: Member<T>[]): Member<T>[] {
  const index = new Map<string, number>();
  const out: Member<T>[] = [];
  for (const member of members) {
    const i = index.get(member[0]);
    if (i === undefined) index.set(member[0], out.push(member) - 1);
    else out[i] = member;
  }
  return out;
}

/** The members that mean the same thing to the server, and the one it uses. */
interface Group<T> {
  /** Every key written for it, in file order. */
  keys: string[];
  /** The first key, which is the name the server keeps. */
  first: string;
  /** The key and value the server ends up with. */
  key: string;
  value: T;
}

// The server copies the dictionary into one keyed by `identity`, later members replacing earlier
// ones. `identity` is null for a key it skips.
function grouped<T>(members: Member<T>[], identity: (key: string) => string | null): Map<string, Group<T>> {
  const groups = new Map<string, Group<T>>();
  for (const [key, value] of asDictionary(members)) {
    const id = identity(key);
    if (id === null) continue;
    const group = groups.get(id);
    if (group) Object.assign(group, { key, value });
    else groups.set(id, { keys: [], first: key, key, value });
  }
  for (const [key] of members) {
    const id = identity(key);
    if (id !== null) groups.get(id)?.keys.push(key);
  }
  return groups;
}

// "... 3 times (written "a", "A", "a"). Deadworks uses the last one written "a" and ignores the rest; so does this tab."
// `why` says how differently written keys come to be the same thing.
function repeated(what: string, group: Group<unknown>, why = ""): Problem {
  const varied = new Set(group.keys).size > 1;
  const used = !varied
    ? "the last one"
    : group.keys.filter((k) => k === group.key).length > 1
      ? `the last one written "${group.key}"`
      : `the one written "${group.key}"`;
  const spelled = varied ? ` (written ${quoted(group.keys)})` : "";
  return {
    level: "warn",
    text: `${what} ${group.keys.length} times${spelled}. ${varied ? why : ""}Deadworks uses ${used} and ignores the rest; so does this tab.`,
  };
}

// Role names ignore case, and a blank one is skipped (PermissionManager.Normalize).
const roleIdentity = (key: string): string | null => (key.trim() === "" ? null : foldName(key));

// An overrides.jsonc key the way the server keeps it: "dw_ban", "!ban" and "/ban" all mean "ban",
// and a plugin can be named in front of a colon (CommandOverrides.NormalizeKey).
const trimPrefix = (name: string): string => {
  const n = name.trim();
  if (n.startsWith("!") || n.startsWith("/")) return n.slice(1);
  return /^dw_/i.test(n) ? n.slice(3) : n;
};
export function overrideKey(key: string): string {
  const k = key.trim();
  const colon = k.indexOf(":");
  return colon >= 0 ? `${k.slice(0, colon).trim()}:${trimPrefix(k.slice(colon + 1))}` : trimPrefix(k);
}
const overrideIdentity = (key: string): string => foldName(overrideKey(key));

// One row per player, the entry being the last one written for their SteamID under any spelling of
// it (JsonPermissionStore.ParsePlayers). Keys the server skips are listed as they are.
function peopleOf(members: Member<PlayerEntry>[]): { people: Person[]; groups: Map<string, Group<PlayerEntry>> } {
  const groups = grouped(members, parsePlayerKey);
  const people = asDictionary(members).flatMap(([key, entry]): Person[] => {
    const id = parsePlayerKey(key);
    return id === null || groups.get(id)?.key === key ? [{ key, id, entry }] : [];
  });
  return { people, groups };
}

// Everything the tab shows, from the files as read. `files` maps paths to text (null when missing).
export function buildModel(files: PermissionFiles): PermissionModel {
  const problems: Problem[] = [];
  const take = <T>(path: string, label: string, reading: Reading<T>, consequence = ""): Member<T>[] => {
    if (reading.error) problems.push({ level: "error", text: `${label} has an error: ${reading.error}${consequence}`, path });
    problems.push(...reading.notes);
    return reading.members;
  };
  // The server reads these two together (JsonPermissionStore.LoadRolesAsync), so an error in either costs it both.
  const unread =
    ". Until it is fixed, a running server keeps the roles and players it read before, and one that starts like this gives nobody any role.";
  const roleMembers = take(ROLES_PATH, "roles.jsonc", readRoles(files[ROLES_PATH]), unread);
  const playerMembers = take(PLAYERS_PATH, "players.jsonc", readPlayers(files[PLAYERS_PATH]), unread);
  const overrideMembers = take(
    OVERRIDES_PATH,
    "overrides.jsonc",
    readOverrides(files[OVERRIDES_PATH]),
    ". If the server starts like this, players cannot run any command until it is fixed."
  );

  const roleGroups = [...grouped(roleMembers, roleIdentity).values()];
  const roles: RoleMap = Object.fromEntries(roleGroups.map((g) => [g.first, g.value]));
  for (const g of roleGroups) {
    if (g.keys.length > 1) problems.push(repeated(`roles.jsonc defines the role "${g.first}"`, g, "Role names ignore capitals. "));
  }
  if (roleMembers.some(([key]) => roleIdentity(key) === null)) {
    problems.push({ level: "warn", text: "roles.jsonc has a role with a blank name, which Deadworks skips." });
  }

  const { people, groups: playerGroups } = peopleOf(playerMembers);
  for (const [id, g] of playerGroups) {
    if (g.keys.length > 1) problems.push(repeated(`players.jsonc lists ${g.value.name ? `${g.value.name} (${id})` : id}`, g));
  }
  const players: PlayerMap = Object.fromEntries(people.map((p) => [p.key, p.entry]));

  const overrideGroups = [...grouped(overrideMembers, overrideIdentity).values()];
  // The server trims the permission, and reads null as "" (CommandOverrides.Set).
  const overrides: Record<string, string> = Object.fromEntries(
    overrideGroups.map((g) => [overrideKey(g.first), (g.value ?? "").trim()])
  );
  for (const g of overrideGroups) {
    const command = overrideKey(g.first);
    if (g.keys.length > 1) problems.push(repeated(`overrides.jsonc sets the command "${command}"`, g));
    if (g.value === null) {
      problems.push({
        level: "warn",
        text: `overrides.jsonc sets the command "${command}" to null. Deadworks reads that as "", so anyone can run it.`,
      });
    }
  }

  const plugins = Object.entries(files)
    .filter(([p, t]) => p.startsWith(GENERATED_DIR + "/") && t != null)
    .map(([path, text]): PluginInfo | null => {
      const { value, error } = parsePermissionFile(text);
      const file = baseName(path).replace(/\.jsonc$/i, "");
      if (error || !value || typeof value !== "object") return null;
      const generated = value as GeneratedFile;
      return {
        file,
        path,
        plugin: generated.plugin || file,
        core: file.toLowerCase() === "deadworks",
        commands: generated.commands || [],
        permissions: generated.permissions || [],
      };
    })
    .filter((p): p is PluginInfo => p !== null)
    .sort((a, b) => Number(b.core) - Number(a.core) || a.plugin.localeCompare(b.plugin));

  const model: PermissionModel = { roles, players, overrides, plugins, people, problems };

  for (const p of people) {
    if (!p.id) problems.push({ level: "warn", text: `players.jsonc: "${p.key}" is not a SteamID, so Deadworks skips it.` });
    for (const r of p.entry.roles) {
      if (!findRole(roles, r)) {
        problems.push({ level: "warn", text: `${p.entry.name || p.key} has the role "${r}", which roles.jsonc does not define.` });
      }
    }
  }
  for (const [name, role] of Object.entries(roles)) {
    for (const parent of role?.inherits || []) {
      if (!findRole(roles, parent)) problems.push({ level: "warn", text: `Role "${name}" inherits "${parent}", which does not exist.` });
    }
    if (inheritChain(roles, name).some((n) => sameName(n, name))) {
      problems.push({ level: "warn", text: `Role "${name}" inherits itself through a loop; Deadworks ignores the loop.` });
    }
  }
  // Grants that match nothing any loaded plugin declares: a typo, or a plugin that is not installed.
  const declared = new Set(
    plugins
      .flatMap((pl) => [
        ...pl.permissions.map((p) => normalize(p.tag)),
        ...pl.commands.map((c) => normalize(resolveCommand(overrides, pl, c).permission)),
      ])
      .filter(Boolean)
  );
  if (plugins.length) {
    const unused = (grant: string, where: string) => {
      const g = parseGrant(grant);
      if (!g || g.all) return;
      const used = g.exact ? declared.has(g.exact) : [...declared].some((d) => d.startsWith(g.prefix ?? ""));
      if (!used) problems.push({ level: "info", text: `${where} grants "${grant}", which no installed plugin uses.` });
    };
    for (const [name, role] of Object.entries(roles)) for (const g of role?.permissions || []) unused(g, `Role "${name}"`);
    for (const p of people) for (const g of p.entry.permissions) unused(g, p.entry.name || p.key);
  }
  return model;
}

// Every permission a plugin declares, for suggestions in the editors.
export const declaredPermissions = (model: Pick<PermissionModel, "plugins">): string[] =>
  [...new Set(model.plugins.flatMap((p) => p.permissions.map((x) => normalize(x.tag))))].sort();

// Who passes a command's permission: roles that grant it to their holders, and listed players.
export function whoCan(model: PermissionModel, permission: string): { anyone: boolean; roles: string[]; people: Person[] } {
  if (!normalize(permission)) return { anyone: true, roles: [], people: [] };
  const roles = Object.keys(model.roles).filter((r) => explain(model, { roles: [r] }, permission).allowed);
  const people = model.people.filter((p) => explain(model, p.entry, permission).allowed);
  return { anyone: roles.some((r) => sameName(r, DEFAULT_ROLE)), roles, people };
}

// What Deadworks writes into roles.jsonc when it first starts; used until the server has written its own.
export const DEFAULT_ROLES: RoleMap = { default: { permissions: [] }, admin: { permissions: ["*"], immunity: 100 } };

export class PermissionError extends Error {
  status: number;
  constructor(message: string, status = 400) {
    super(message);
    this.status = status;
  }
}

// A file's entries for a change to work on, in file order. A key written twice is already one entry
// (as it is to the server); entries that only mean the same thing are still separate.
function membersToEdit<T>(text: string | null | undefined, label: string, read: (text: string) => Reading<T>): Member<T>[] | null {
  if (text == null) return null;
  const { members, error } = read(text);
  // deadworks-web sends people to its Files tab here; in the launcher the JSON button is the way in.
  if (error) {
    throw new PermissionError(`${label} has an error, so it was left alone. Fix it with the JSON button first. (${error})`, 409);
  }
  return asDictionary(members);
}

// Takes out every entry that is `mine` and puts `next` (if any) where the first one that `keepsSpot`
// was, or at the end. Removing all of them is the point: the server uses the last entry that means
// the same thing, so one left behind under another spelling could override what was just saved.
function replaceMembers<T>(
  members: Member<T>[],
  mine: (key: string) => boolean,
  next: Member<T> | null,
  keepsSpot: (key: string) => boolean = mine
): Member<T>[] {
  const out: Member<T>[] = [];
  let spot = -1;
  for (const member of members) {
    if (!mine(member[0])) out.push(member);
    else if (spot < 0 && keepsSpot(member[0])) spot = out.length;
  }
  if (next) out.splice(spot < 0 ? out.length : spot, 0, next);
  return out;
}
const optionalImmunity = (value: unknown): number | null => {
  if (value === "" || value == null) return null;
  const n = Number(value);
  if (!Number.isInteger(n) || n < 0 || n > 1000000) {
    throw new PermissionError("Immunity must be a whole number, or empty to use the roles' immunity.");
  }
  return n;
};
const cleanList = (list: unknown, label: string, check: (value: string) => boolean): string[] => {
  if (list == null) return [];
  if (!Array.isArray(list) || list.length > 200) throw new PermissionError(`Invalid ${label}.`);
  const out = [...new Set(list.map((v: unknown) => String(v).trim()).filter(Boolean))];
  for (const v of out) {
    if (v.length > 120 || !check(v)) throw new PermissionError(`"${v}" is not a valid ${label.replace(/s$/, "")}.`);
  }
  return out;
};
const ROLE_NAME = /^[a-z0-9_-]{1,40}$/;

/** One change from the Admins tab. */
export type PermissionChange =
  | {
      action: "player-save";
      steamId: string;
      /** Refuse when the player is already listed. */
      adding?: boolean;
      name?: string | null;
      roles?: string[];
      permissions?: string[];
      /** A whole number, or empty to use the roles' immunity. */
      immunity?: number | string | null;
    }
  | { action: "player-remove"; steamId: string | null }
  | {
      action: "role-save";
      name: string;
      /** The role being edited, null when creating one. */
      original?: string | null;
      permissions?: string[];
      inherits?: string[];
      immunity?: number | string | null;
    }
  | { action: "role-delete"; name: string }
  | {
      action: "override-save";
      key: string;
      /** The override to drop first, as the model keys it. Every spelling of it in the file goes. */
      previous?: string | null;
      /** null removes the override, "" makes the command public. */
      permission: string | null;
    }
  | {
      action: "file-save";
      path: string;
      contents: string;
      /** The text the file was opened with, null when it did not exist. */
      base: string | null;
    };

export type PlannedChange =
  | { files: Record<string, string>; audit: string | null; conflict?: false }
  /** A file-save whose base is stale: nothing to write, `contents` is the file as it is now. */
  | { files: Record<string, never>; audit: null; conflict: true; contents: string | null };

// One change from the Admins tab, applied to the files as they are now. Returns the files to write
// and a line for the audit log.
//
// Unlike deadworks-web, players.jsonc is only read by the changes that use it: there the Files tab
// repairs a broken file, here the JSON dialog (a file-save) is the only way to.
export function applyChange(files: PermissionFiles, input: PermissionChange): PlannedChange {
  const playersToEdit = () => membersToEdit(files[PLAYERS_PATH], "players.jsonc", readPlayers) ?? [];
  const rolesToEdit = () =>
    membersToEdit(files[ROLES_PATH], "roles.jsonc", readRoles) ??
    Object.entries(DEFAULT_ROLES).map(([key, role]): Member<RoleEntry> => [
      key,
      { permissions: role?.permissions ?? [], inherits: role?.inherits ?? [], immunity: role?.immunity ?? null },
    ]);
  const isRole = (key: string, role: string): boolean => roleIdentity(key) !== null && sameName(key, role);
  const name = (v: unknown): string | null => (typeof v === "string" ? v.trim().slice(0, 64) : "") || null;
  switch (input.action) {
    case "player-save": {
      const players = playersToEdit();
      const roles = rolesToEdit();
      const id = parseSteamId(input.steamId);
      if (!id) throw new PermissionError("Enter a SteamID64, a Steam2 or Steam3 ID, or a steamcommunity.com/profiles/ link.");
      // Every key that is this player, however it is written; the server uses the last.
      const mine = (key: string) => parsePlayerKey(key) === id;
      const prior = players.filter(([key]) => mine(key)).pop()?.[1];
      if (input.adding && prior) throw new PermissionError(`${prior.name || id} is already listed. Edit their row instead.`, 409);
      const list = cleanList(input.roles, "roles", () => true);
      const unknown = list.filter((r) => !roles.some(([key]) => isRole(key, r)) && !(prior?.roles || []).includes(r));
      if (unknown.length) throw new PermissionError(`There is no role called ${unknown.join(", ")}.`);
      const grants = cleanList(input.permissions, "permissions", (g) => Boolean(parseGrant(g)));
      const entry = { name: name(input.name), roles: list, permissions: grants, immunity: optionalImmunity(input.immunity) };
      return {
        files: { [PLAYERS_PATH]: renderPlayerMembers(replaceMembers(players, mine, [id, entry], (key) => key === id)) },
        audit: `${prior ? "updated" : "added"} ${name(input.name) || id} (${id}): ${list.join(", ") || "no roles"}`,
      };
    }
    case "player-remove": {
      const players = playersToEdit();
      const id = parseSteamId(input.steamId);
      const mine = (key: string) => parsePlayerKey(key) === id;
      const last = id ? players.filter(([key]) => mine(key)).pop() : undefined;
      if (!last) throw new PermissionError("That player is no longer listed.", 404);
      return {
        files: { [PLAYERS_PATH]: renderPlayerMembers(replaceMembers(players, mine, null)) },
        audit: `removed ${last[1].name || last[0]} (${id})`,
      };
    }
    case "role-save": {
      let players = playersToEdit();
      let roles = rolesToEdit();
      const roleName = String(input.name || "").trim().toLowerCase();
      const original = input.original ? String(input.original).toLowerCase() : null;
      const defined = (role: string) => roles.some(([key]) => isRole(key, role));
      if (!ROLE_NAME.test(roleName)) throw new PermissionError("Role names can use lowercase letters, numbers, - and _.");
      if (original && !defined(original)) throw new PermissionError(`There is no role called ${original} any more.`, 404);
      if (original !== roleName && defined(roleName)) throw new PermissionError(`There is already a role called ${roleName}.`, 409);
      if (original === DEFAULT_ROLE && roleName !== DEFAULT_ROLE) throw new PermissionError("The default role cannot be renamed.");
      // roles.jsonc first, so it is written before players.jsonc.
      const out: Record<string, string> = { [ROLES_PATH]: "" };
      const renaming = original !== null && original !== roleName;
      if (renaming) {
        const renamed = (names: string[]) => names.map((r) => (sameName(r, original) ? roleName : r));
        players = players.map(([key, p]) => [key, { ...p, roles: renamed(p.roles) }]);
        roles = roles.map(([key, r]) => [key, { ...r, inherits: renamed(r.inherits) }]);
        out[PLAYERS_PATH] = renderPlayerMembers(players);
      }
      // Every definition of this role, whatever its capitals; the server uses the last.
      const mine = (key: string) => isRole(key, original ?? roleName);
      const others = roles.filter(([key]) => !mine(key));
      const inherits = cleanList(input.inherits, "inherited roles", (r) => others.some(([key]) => isRole(key, r)));
      const entry = {
        permissions: cleanList(input.permissions, "permissions", (g) => Boolean(parseGrant(g))),
        inherits,
        immunity: optionalImmunity(input.immunity),
      };
      // Saved in place, so the role keeps its spot in the file, and under the name it had unless it is renamed.
      const key = renaming ? roleName : (roles.find(([k]) => mine(k))?.[0] ?? roleName);
      out[ROLES_PATH] = renderRoleMembers(replaceMembers(roles, mine, [key, entry]));
      return {
        files: out,
        audit: `${original ? "updated" : "created"} role ${roleName}${renaming ? ` (was ${original})` : ""}`,
      };
    }
    case "role-delete": {
      const players = playersToEdit();
      const roles = rolesToEdit();
      const roleName = String(input.name || "").toLowerCase();
      const mine = (key: string) => isRole(key, roleName);
      const has = (p: PlayerEntry) => p.roles.some((r) => sameName(r, roleName));
      if (roleName === DEFAULT_ROLE) throw new PermissionError("The default role cannot be deleted; it is what every player gets.");
      if (!roles.some(([key]) => mine(key))) throw new PermissionError(`There is no role called ${roleName}.`, 404);
      const holders = peopleOf(players).people.filter((p) => has(p.entry));
      if (holders.length) {
        throw new PermissionError(`${holders.length} player${holders.length === 1 ? " still has" : "s still have"} this role. Take it from them first.`, 409);
      }
      const left = replaceMembers(roles, mine, null).map(
        ([key, r]): Member<RoleEntry> => [key, { ...r, inherits: r.inherits.filter((x) => !sameName(x, roleName)) }]
      );
      const out: Record<string, string> = { [ROLES_PATH]: renderRoleMembers(left) };
      // Nobody the tab lists holds it, but an entry the server ignores (an earlier repeat of a listed
      // player) still can. Take it from those too, so it cannot come back with a role of this name.
      if (players.some(([, p]) => has(p))) {
        out[PLAYERS_PATH] = renderPlayerMembers(players.map(([key, p]) => [key, { ...p, roles: p.roles.filter((r) => !sameName(r, roleName)) }]));
      }
      return { files: out, audit: `deleted role ${roleName}` };
    }
    case "override-save": {
      const commands = membersToEdit(files[OVERRIDES_PATH], "overrides.jsonc", readOverrides) ?? [];
      const key = String(input.key || "").trim();
      if (!/^[A-Za-z0-9_.-]{1,80}(:[A-Za-z0-9_-]{1,80})?$/.test(key)) throw new PermissionError("Invalid command.");
      const previous = input.previous ? String(input.previous) : null;
      // Every entry for this command, however it is written ("ban", "BAN", "!ban"); the server uses the last.
      const targets = [key, ...(previous === null ? [] : [previous])].map(overrideIdentity);
      const mine = (k: string) => targets.includes(overrideIdentity(k));
      let next: Member<string | null> | null = null;
      if (input.permission != null) {
        const permission = String(input.permission).trim();
        if (permission && (!parseGrant(permission) || permission.includes("*") || permission.startsWith("-"))) {
          throw new PermissionError(`"${permission}" is not a permission name.`);
        }
        next = [key, permission];
      }
      return {
        files: { [OVERRIDES_PATH]: renderOverrideMembers(replaceMembers(commands, mine, next, (k) => k === key && k !== previous)) },
        audit: input.permission == null ? `removed the override for ${key}` : `set ${key} to ${String(input.permission).trim() || "anyone"}`,
      };
    }
    case "file-save": {
      // A whole file from the JSON editor. `base` is the text it was opened with (null for a file that
      // did not exist); if the file has changed since, nothing is written and the current text comes back.
      if (![ROLES_PATH, PLAYERS_PATH, OVERRIDES_PATH].includes(input.path)) {
        throw new PermissionError("Only roles.jsonc, players.jsonc and overrides.jsonc can be edited here.", 403);
      }
      if (typeof input.contents !== "string" || input.contents.length > 512 * 1024) throw new PermissionError("The file is too large.", 413);
      if ((files[input.path] ?? null) !== (input.base ?? null)) {
        return { files: {}, audit: null, conflict: true, contents: files[input.path] ?? null };
      }
      const { value, error } = parsePermissionFile(input.contents);
      if (error) throw new PermissionError(`This is not valid JSON: ${error}`);
      if (!value || typeof value !== "object" || Array.isArray(value)) throw new PermissionError("The file must hold one JSON object.");
      // A broken overrides.jsonc stops players running any command, so it is held to what the server
      // accepts, under whatever capitals "commands" is written with. null is refused too: the server
      // would take it, as "anyone can run it".
      if (input.path === OVERRIDES_PATH) {
        const commands = readOverrides(input.contents);
        if (commands.error || commands.members.some(([, permission]) => permission === null)) {
          throw new PermissionError('"commands" must map command names to permission names.');
        }
      }
      return { files: { [input.path]: input.contents }, audit: `edited ${baseName(input.path)}` };
    }
    default:
      throw new PermissionError("Unknown permissions action.");
  }
}

// Files are written the way Deadworks writes them (header, then indented camelCase JSON with empty
// fields left out), so a change from the launcher looks the same as one from dw_role_grant.
//
// Every entry is written out this way, not only the one that changed, as dw_role_grant does to
// players.jsonc: lowercase property names, and for a property the file had more than once, the value
// the server was using. So a rewrite never changes what the server reads, and no differently
// capitalised copy of a property is left for it to prefer. Entries stay in file order under the
// keys they had, which is what keeps "the last one wins" pointing at the same entry.
const dropEmpty = (fields: Record<string, unknown>): Record<string, unknown> =>
  Object.fromEntries(Object.entries(fields).filter(([, v]) => v != null));
const trimPlayer = (e: RawPlayer) =>
  dropEmpty({
    name: e.name || null,
    roles: e.roles?.length ? e.roles : null,
    permissions: e.permissions?.length ? e.permissions : null,
    immunity: Number.isInteger(e.immunity) ? e.immunity : null,
  });
const trimRole = (r: RoleDef) =>
  dropEmpty({
    permissions: r.permissions || [],
    inherits: r.inherits?.length ? r.inherits : null,
    immunity: Number.isInteger(r.immunity) ? r.immunity : null,
  });
// JSON.stringify(object, null, 2), from a list: an object cannot hold a key twice, and moves keys
// that look like numbers to the front.
const renderMembers = (members: Member<unknown>[]): string =>
  members.length === 0
    ? "{}"
    : `{\n${members.map(([k, v]) => `  ${JSON.stringify(k)}: ${JSON.stringify(v, null, 2).replace(/\n/g, "\n  ")}`).join(",\n")}\n}`;
const renderPlayerMembers = (players: Member<RawPlayer | null>[]): string =>
  PLAYERS_HEADER + renderMembers(players.map(([k, v]) => [k, trimPlayer(v || {})])) + "\n";
const renderRoleMembers = (roles: Member<RoleDef | null>[]): string =>
  ROLES_HEADER + renderMembers(roles.map(([k, v]) => [k, trimRole(v || {})])) + "\n";
const renderOverrideMembers = (commands: Member<string | null>[]): string =>
  OVERRIDES_HEADER + `{\n  "commands": ${renderMembers(commands.map(([k, v]) => [k, (v ?? "").trim()])).replace(/\n/g, "\n  ")}\n}\n`;
export const renderPlayers = (players: PlayerMap): string => renderPlayerMembers(Object.entries(players));
export const renderRoles = (roles: RoleMap): string => renderRoleMembers(Object.entries(roles));
export const renderOverrides = (commands: Record<string, string>): string => renderOverrideMembers(Object.entries(commands));

export const ROLES_HEADER = `// Roles for the Deadworks permission system.
//
// "permissions" takes exact permissions ("admin.moderation.ban"), wildcards that stop at dots
// ("admin.moderation.*", or "*" for everything), and denies with a leading "-" ("-admin.moderation.ban").
// Within one role the most specific grant wins, so ["*", "-admin.moderation.ban"] is everything except banning.
//
// "inherits" pulls in other roles. A role's own permissions beat what it inherits, so a role can undo a
// deny from a role it inherits. "immunity" stops lower-immunity players from targeting holders of this
// role with commands like kick or ban; without one, a role has the highest immunity of the roles it inherits.
//
// A player has a permission if any of their roles gives it: a deny in one role never takes away what another
// role gives. To take something away from one player, put the deny in their players.jsonc entry.
//
// "default" applies to every player, listed in players.jsonc or not.
//
// Every plugin's permissions are listed in generated/<Plugin>.jsonc.
// Run dw_perm_reload after editing.
`;
export const PLAYERS_HEADER = `// Players and the roles they hold. Keys can be SteamID64, Steam2 or Steam3 IDs.
//
//   "76561197960287930": {
//     "name": "wisp",                            // just a note
//     "roles": ["admin"],
//     "permissions": ["-admin.moderation.ban"],  // checked before any role, so this beats them
//     "immunity": 90                             // replaces the roles' immunity
//   }
//
// dw_role_grant, dw_role_revoke, dw_perm_grant and dw_perm_revoke rewrite this file: they keep your
// entries but not your comments (only this header is kept). If the file has an error they leave it alone.
// Run dw_perm_reload after editing by hand.
`;
export const OVERRIDES_HEADER = `// Change the permission a command needs without changing the plugin.
//   "ban": "my.custom.permission"   remaps every command called ban
//   "AdminPlugin:kick": ""          makes one plugin's kick public
// Run dw_perm_reload after editing.
`;

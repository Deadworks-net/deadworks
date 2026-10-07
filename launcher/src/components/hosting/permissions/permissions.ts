// Deadworks' permission files (game/bin/win64/configs/permissions/) and the rules it applies to them,
// mirrored from managed/Permissions/*.cs so the Admins tab can show who can run what without asking the
// server. A TypeScript port of deadworks-web's app/hosting/permissions.js; keep the two in step.

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

export interface PermissionModel {
  roles: RoleMap;
  players: PlayerMap;
  overrides: Record<string, string>;
  plugins: PluginInfo[];
  people: Person[];
  problems: Problem[];
}

// Drops // and /* */ comments, leaving anything inside strings alone.
export const stripComments = (text: string): string =>
  String(text).replace(/("(?:[^"\\]|\\.)*")|\/\/[^\n]*|\/\*[\s\S]*?\*\//g, (_m, str: string | undefined) => str || "");

const baseName = (path: string): string => path.slice(path.lastIndexOf("/") + 1);

// The permission files are read with AllowTrailingCommas, unlike plugin configs.
export function parsePermissionFile(text: string | null | undefined): { value: unknown; error: string | null } {
  if (text == null) return { value: null, error: null };
  const stripped = stripComments(text).replace(
    /("(?:[^"\\]|\\.)*")|,(\s*[}\]])/g,
    (_m, str: string | undefined, close: string | undefined) => str || close || ""
  );
  try {
    return { value: JSON.parse(stripped) as unknown, error: null };
  } catch (e) {
    return { value: null, error: e instanceof Error ? e.message : String(e) };
  }
}

// SteamID64 from a SteamID64, Steam2 (STEAM_1:0:123), Steam3 ([U:1:246]) or a /profiles/ link.
const BASE = BigInt("76561197960265728");
export function parseSteamId(input: unknown): string | null {
  const s = String(input || "").trim();
  let m: RegExpMatchArray | null;
  if ((m = s.match(/steamcommunity\.com\/profiles\/(\d{17})/i))) return m[1];
  if (/^\d{17}$/.test(s)) return BigInt(s) > BASE ? s : null;
  if ((m = s.match(/^STEAM_[0-5]:([01]):(\d+)$/i))) return String(BASE + BigInt(m[2]) * BigInt(2) + BigInt(m[1]));
  if ((m = s.match(/^\[?U:1:(\d+)\]?$/i))) return String(BASE + BigInt(m[1]));
  return null;
}
export const steam3 = (id64: string): string => `[U:1:${BigInt(id64) - BASE}]`;

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
  if (!role || path.includes(name.toLowerCase())) return null;
  const own = decideList(grantsOf(role.permissions), perm);
  if (own) return { allowed: !own.deny, grant: own.raw, role: name };
  let denied: RoleDecision | null = null;
  for (const parent of role.inherits || []) {
    const r = decideRole(roles, parent, perm, [...path, name.toLowerCase()]);
    if (r?.allowed) return r;
    if (r && !denied) denied = r;
  }
  return denied;
}
export function findRole(roles: RoleMap, name: string): RoleDef | null | undefined {
  const wanted = String(name).toLowerCase();
  const key = Object.keys(roles).find((k) => k.toLowerCase() === wanted);
  return key === undefined ? undefined : roles[key];
}
export const playerRoles = (entry: EntryLike): string[] => [
  DEFAULT_ROLE,
  ...(entry?.roles || []).filter((r) => r && r.toLowerCase() !== DEFAULT_ROLE),
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
  if (!role || path.includes(name.toLowerCase())) return 0;
  if (Number.isInteger(role.immunity)) return role.immunity as number;
  return Math.max(0, ...(role.inherits || []).map((p) => roleImmunity(roles, p, [...path, name.toLowerCase()])));
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
  if (!role || path.includes(name.toLowerCase())) return [];
  return (role.inherits || []).flatMap((p) => [p, ...inheritChain(roles, p, [...path, name.toLowerCase()])]);
}

export interface ResolvedCommand {
  permission: string;
  overridden: boolean;
  /** The overrides.jsonc key that matched. */
  key?: string;
}

// The permission a command needs after overrides.jsonc: "Plugin:cmd" beats "cmd".
export function resolveCommand(
  overrides: Record<string, string> | null | undefined,
  plugin: Pick<PluginInfo, "plugin" | "file">,
  command: CommandInfo
): ResolvedCommand {
  const entries = Object.entries(overrides || {});
  const names = [command.name, ...(command.aliases || [])];
  for (const q of [plugin.plugin, plugin.file]) {
    for (const n of names) {
      const hit = entries.find(([k]) => k.toLowerCase() === `${q}:${n}`.toLowerCase());
      if (hit) return { permission: hit[1], overridden: true, key: hit[0] };
    }
  }
  for (const n of names) {
    const hit = entries.find(([k]) => k.toLowerCase() === n.toLowerCase());
    if (hit) return { permission: hit[1], overridden: true, key: hit[0] };
  }
  return { permission: command.declaredPermission ?? command.permission ?? "", overridden: false };
}

interface GeneratedFile {
  plugin?: string;
  commands?: CommandInfo[];
  permissions?: DeclaredPermission[];
}

// Everything the tab shows, from the files as read. `files` maps paths to text (null when missing).
export function buildModel(files: PermissionFiles): PermissionModel {
  const problems: Problem[] = [];
  const read = (path: string, label: string): unknown => {
    const { value, error } = parsePermissionFile(files[path]);
    if (error) problems.push({ level: "error", text: `${label} has an error: ${error}`, path });
    return value;
  };
  const roles = (read(ROLES_PATH, "roles.jsonc") || {}) as RoleMap;
  const players = (read(PLAYERS_PATH, "players.jsonc") || {}) as PlayerMap;
  const overridesFile = read(OVERRIDES_PATH, "overrides.jsonc") as { commands?: Record<string, string> } | null;
  const overridesProblem = problems.find((p) => p.path === OVERRIDES_PATH);
  if (overridesProblem) {
    overridesProblem.text += ". If the server starts like this, players cannot run any command until it is fixed.";
  }
  const overrides = overridesFile?.commands || {};
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

  const people: Person[] = Object.entries(players).map(([key, entry]) => ({
    key,
    id: parseSteamId(key),
    entry: {
      name: entry?.name ?? null,
      roles: entry?.roles || [],
      permissions: entry?.permissions || [],
      immunity: entry?.immunity ?? null,
    },
  }));
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
    if (inheritChain(roles, name).some((n) => n.toLowerCase() === name.toLowerCase())) {
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
  return { anyone: roles.some((r) => r.toLowerCase() === DEFAULT_ROLE), roles, people };
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

function readFile<T extends object>(files: PermissionFiles, path: string, fallback: T, label: string): T {
  if (files[path] == null) return structuredClone(fallback);
  const { value, error } = parsePermissionFile(files[path]);
  // deadworks-web sends people to its Files tab here; in the launcher the JSON button is the way in.
  if (error) {
    throw new PermissionError(`${label} has an error, so it was left alone. Fix it with the JSON button first. (${error})`, 409);
  }
  if (!value || typeof value !== "object" || Array.isArray(value)) {
    throw new PermissionError(`${label} must hold an object. Fix it with the JSON button first.`, 409);
  }
  return value as T;
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
const keyFor = (players: PlayerMap, id: string): string | undefined =>
  Object.keys(players).find((k) => parseSteamId(k) === id);

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
      /** The overrides.jsonc key to drop first. */
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
  const readPlayers = () => readFile<PlayerMap>(files, PLAYERS_PATH, {}, "players.jsonc");
  const readRoles = () => readFile<RoleMap>(files, ROLES_PATH, DEFAULT_ROLES, "roles.jsonc");
  const name = (v: unknown): string | null => (typeof v === "string" ? v.trim().slice(0, 64) : "") || null;
  switch (input.action) {
    case "player-save": {
      const players = readPlayers();
      const roles = readRoles();
      const id = parseSteamId(input.steamId);
      if (!id) throw new PermissionError("Enter a SteamID64, a Steam2 or Steam3 ID, or a steamcommunity.com/profiles/ link.");
      const existing = keyFor(players, id);
      const prior = existing === undefined ? undefined : players[existing];
      if (input.adding && existing) throw new PermissionError(`${prior?.name || id} is already listed. Edit their row instead.`, 409);
      const list = cleanList(input.roles, "roles", () => true);
      const unknown = list.filter((r) => !findRole(roles, r) && !(prior?.roles || []).includes(r));
      if (unknown.length) throw new PermissionError(`There is no role called ${unknown.join(", ")}.`);
      const grants = cleanList(input.permissions, "permissions", (g) => Boolean(parseGrant(g)));
      if (existing && existing !== id) delete players[existing];
      players[id] = { name: name(input.name), roles: list, permissions: grants, immunity: optionalImmunity(input.immunity) };
      return {
        files: { [PLAYERS_PATH]: renderPlayers(players) },
        audit: `${existing ? "updated" : "added"} ${name(input.name) || id} (${id}): ${list.join(", ") || "no roles"}`,
      };
    }
    case "player-remove": {
      const players = readPlayers();
      const id = parseSteamId(input.steamId);
      const key = id ? keyFor(players, id) : undefined;
      if (!key) throw new PermissionError("That player is no longer listed.", 404);
      const label = players[key]?.name || key;
      delete players[key];
      return { files: { [PLAYERS_PATH]: renderPlayers(players) }, audit: `removed ${label} (${id})` };
    }
    case "role-save": {
      const players = readPlayers();
      const roles = readRoles();
      const roleName = String(input.name || "").trim().toLowerCase();
      const original = input.original ? String(input.original).toLowerCase() : null;
      if (!ROLE_NAME.test(roleName)) throw new PermissionError("Role names can use lowercase letters, numbers, - and _.");
      if (original && !findRole(roles, original)) throw new PermissionError(`There is no role called ${original} any more.`, 404);
      if (original !== roleName && findRole(roles, roleName)) throw new PermissionError(`There is already a role called ${roleName}.`, 409);
      if (original === DEFAULT_ROLE && roleName !== DEFAULT_ROLE) throw new PermissionError("The default role cannot be renamed.");
      // roles.jsonc first, so it is written before players.jsonc.
      const out: Record<string, string> = { [ROLES_PATH]: "" };
      if (original && original !== roleName) {
        // Renamed in place, so the role keeps its spot in the file.
        const oldKey = Object.keys(roles).find((k) => k.toLowerCase() === original);
        for (const k of Object.keys(roles)) {
          const v = roles[k];
          delete roles[k];
          roles[k === oldKey ? roleName : k] = v;
        }
        for (const p of Object.values(players)) {
          if (p?.roles) p.roles = p.roles.map((r) => (r.toLowerCase() === original ? roleName : r));
        }
        for (const r of Object.values(roles)) {
          if (r?.inherits) r.inherits = r.inherits.map((x) => (x.toLowerCase() === original ? roleName : x));
        }
        out[PLAYERS_PATH] = renderPlayers(players);
      }
      const inherits = cleanList(input.inherits, "inherited roles", (r) => Boolean(findRole(roles, r)) && r.toLowerCase() !== roleName);
      const key = Object.keys(roles).find((k) => k.toLowerCase() === roleName) || roleName;
      roles[key] = {
        permissions: cleanList(input.permissions, "permissions", (g) => Boolean(parseGrant(g))),
        inherits,
        immunity: optionalImmunity(input.immunity),
      };
      out[ROLES_PATH] = renderRoles(roles);
      return {
        files: out,
        audit: `${original ? "updated" : "created"} role ${roleName}${original && original !== roleName ? ` (was ${original})` : ""}`,
      };
    }
    case "role-delete": {
      const players = readPlayers();
      const roles = readRoles();
      const roleName = String(input.name || "").toLowerCase();
      const key = Object.keys(roles).find((k) => k.toLowerCase() === roleName);
      if (roleName === DEFAULT_ROLE) throw new PermissionError("The default role cannot be deleted; it is what every player gets.");
      if (!key) throw new PermissionError(`There is no role called ${roleName}.`, 404);
      const holders = Object.values(players).filter((p) => (p?.roles || []).some((r) => r.toLowerCase() === roleName));
      if (holders.length) {
        throw new PermissionError(`${holders.length} player${holders.length === 1 ? " still has" : "s still have"} this role. Take it from them first.`, 409);
      }
      delete roles[key];
      for (const r of Object.values(roles)) if (r?.inherits) r.inherits = r.inherits.filter((x) => x.toLowerCase() !== roleName);
      return { files: { [ROLES_PATH]: renderRoles(roles) }, audit: `deleted role ${roleName}` };
    }
    case "override-save": {
      const overrides = readFile<{ commands?: Record<string, string> }>(files, OVERRIDES_PATH, { commands: {} }, "overrides.jsonc");
      const commands = { ...(overrides.commands || {}) };
      const key = String(input.key || "").trim();
      if (!/^[A-Za-z0-9_.-]{1,80}(:[A-Za-z0-9_-]{1,80})?$/.test(key)) throw new PermissionError("Invalid command.");
      if (input.previous) delete commands[String(input.previous)];
      if (input.permission != null) {
        const permission = String(input.permission).trim();
        if (permission && (!parseGrant(permission) || permission.includes("*") || permission.startsWith("-"))) {
          throw new PermissionError(`"${permission}" is not a permission name.`);
        }
        commands[key] = permission;
      }
      return {
        files: { [OVERRIDES_PATH]: renderOverrides(commands) },
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
      const commands = (value as { commands?: unknown }).commands;
      if (
        input.path === OVERRIDES_PATH &&
        commands != null &&
        (typeof commands !== "object" || Array.isArray(commands) || Object.values(commands).some((v) => typeof v !== "string"))
      ) {
        throw new PermissionError('"commands" must map command names to permission names.');
      }
      return { files: { [input.path]: input.contents }, audit: `edited ${baseName(input.path)}` };
    }
    default:
      throw new PermissionError("Unknown permissions action.");
  }
}

// Files are written the way Deadworks writes them (header, then indented camelCase JSON with empty
// fields left out), so a change from the launcher looks the same as one from dw_role_grant.
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
export const renderPlayers = (players: PlayerMap): string =>
  PLAYERS_HEADER + JSON.stringify(Object.fromEntries(Object.entries(players).map(([k, v]) => [k, trimPlayer(v || {})])), null, 2) + "\n";
export const renderRoles = (roles: RoleMap): string =>
  ROLES_HEADER + JSON.stringify(Object.fromEntries(Object.entries(roles).map(([k, v]) => [k, trimRole(v || {})])), null, 2) + "\n";
export const renderOverrides = (commands: Record<string, string>): string =>
  OVERRIDES_HEADER + JSON.stringify({ commands }, null, 2) + "\n";

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

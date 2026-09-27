// Local server hosting: the contract between the HOST tab and
// src-tauri/src/hosting. Every type here mirrors a serde struct with
// `rename_all = "camelCase"`; keep the two in step.

import { invoke } from "@tauri-apps/api/core";
import { listen, type UnlistenFn } from "@tauri-apps/api/event";

// ── Install (the shared base game install + Deadworks + .NET) ──

export type BaseSource = "client" | "steamcmd";
export type CopyMode = "copy" | "hardlink";

export interface DriveInfo {
  /** e.g. "H:\\" */
  root: string;
  freeBytes: number;
  totalBytes: number;
  /** Same volume as the Deadlock client install, so hardlinks are possible. */
  sameAsClient: boolean;
}

/** Everything the setup screen needs before the user commits to an install. */
export interface SetupCheck {
  /** Detected Deadlock client install (`...\\Deadlock\\game`), if any. */
  clientGameDir: string | null;
  clientBuildId: string | null;
  /** Steam is mid-update (or the install is incomplete); copying now would copy a half-patched game. */
  clientUpdating: boolean;
  /** Bytes a full copy needs (manifest total), 0 when unknown. */
  requiredBytes: number;
  drives: DriveInfo[];
  /** Default install folder: the drive with the most free space. */
  suggestedRoot: string;
}

export interface InstallOptions {
  root: string;
  source: BaseSource;
  copyMode: CopyMode;
  /** SteamCMD only. The password is passed through once and never stored. */
  steamUsername?: string;
  steamPassword?: string;
  /** Copy files that differ from Steam's manifest anyway (after the user saw the list). */
  allowModified?: boolean;
}

export interface BaseInfo {
  source: BaseSource;
  copyMode: CopyMode;
  buildId: string;
  sizeBytes: number;
  /** Files whose content differed from Steam's manifest when copied (e.g. modded client). */
  modifiedFiles: string[];
}

export type TaskKind = "install" | "update" | "verify" | "uninstall";

/** SteamCMD asks for these mid-login; answer with `hostingSteamcmdInput`. */
export type SteamPrompt = "password" | "guard_code" | "mobile_confirm" | null;

export interface TaskProgress {
  kind: TaskKind;
  /** Machine-readable stage: "game" | "deadworks" | "dotnet" | "steamcmd" | "trees" | "done" */
  stage: string;
  /** Human sentence for the current step, e.g. "Copying game files". */
  label: string;
  bytesDone: number;
  bytesTotal: number;
  filesDone: number;
  filesTotal: number;
  steamPrompt: SteamPrompt;
  /** Set when the task failed; the task is then finished. */
  error: string | null;
  /**
   * Client files whose SHA1 differs from Steam's manifest (mods replacing game files). When
   * non-empty alongside `error`, offer "Repair with Steam" (`hosting.repairClient`) + Retry, and
   * "Use them anyway" (re-run install with `allowModified: true`).
   */
  modifiedFiles: string[];
  finished: boolean;
}

export interface UpdateState {
  /** Build the client (or Steam, for SteamCMD installs) is on. */
  availableBuildId: string | null;
  /** Build the shared base install is on. */
  installedBuildId: string | null;
  deadworksInstalled: string | null;
  deadworksLatest: string | null;
  /** Something newer is waiting to be applied (applies when every server is stopped or empty). */
  pending: boolean;
  /** Why nothing can run right now, e.g. "Waiting for Deadworks to support game build 25379491". */
  holdReason: string | null;
  /** Unix seconds. */
  lastCheck: number | null;
}

export interface HostingOverview {
  installed: boolean;
  root: string | null;
  base: BaseInfo | null;
  dotnetVersion: string | null;
  task: TaskProgress | null;
  updates: UpdateState;
  servers: ServerSummary[];
}

// ── Servers ──

export type NetworkMode = "sdr" | "port_forward" | "lan";

export interface CvarEntry {
  key: string;
  value: string;
}

/** A server profile. Everything the Settings / Plugins / Content / Advanced tabs edit. */
export interface ServerConfig {
  id: string;
  name: string;
  /** Empty = no password. */
  password: string;
  maxPlayers: number;
  map: string;
  port: number;
  network: NetworkMode;
  /** Show in the Deadworks server browser (only meaningful for port_forward). */
  listed: boolean;
  cheats: boolean;
  hibernateWhenEmpty: boolean;
  /** Library plugin ids enabled on this server. */
  plugins: string[];
  cvars: CvarEntry[];
  /** Extra launch arguments, one token each (no spaces). */
  launchArgs: string[];
  /** File names of addon VPKs in this server's content folder that are enabled. */
  contentAddons: string[];
  /** File names of map VPKs in this server's content folder that are enabled. */
  extraMaps: string[];
}

export type ServerState =
  | "stopped"
  | "starting"
  | "running"
  | "stopping"
  | "crashed"
  | "waiting_for_deadworks"
  | "updating";

export interface PlayerInfo {
  /** Engine user id from `status`, -1 when unknown. */
  userId: number;
  slot: number;
  steamId64: string;
  name: string;
  pingMs: number;
  connectedSeconds: number;
  team: number;
  hero: string;
}

export interface NetworkInfo {
  mode: NetworkMode;
  port: number;
  lanIps: string[];
  publicIp: string | null;
  reachability: "unknown" | "checking" | "open" | "closed" | "error";
  /** SDR identity, e.g. "[A:1:1234567890:12345]"; changes on every restart. */
  sdrId: string | null;
  /** The SDR id differs from the one the user last copied: they need to re-share. */
  sdrIdChanged: boolean;
  /** Ready-to-paste console command (includes the password), null until known. */
  connectCommand: string | null;
}

export interface ServerRuntime {
  id: string;
  state: ServerState;
  pid: number | null;
  /** Unix seconds. */
  startedAt: number | null;
  players: PlayerInfo[];
  cpuPercent: number;
  memoryBytes: number;
  /** Human explanation for crashed / waiting states. */
  message: string | null;
  exitCode: number | null;
  network: NetworkInfo;
}

export interface ServerSummary {
  config: ServerConfig;
  runtime: ServerRuntime;
}

export interface ConsoleLine {
  seq: number;
  /** "out" = server output, "in" = a command the user sent, "sys" = launcher message. */
  kind: "out" | "in" | "sys";
  text: string;
}

// ── Plugins & content ──

export interface PluginEntry {
  /** DLL name without extension; also the key in plugins.jsonc. */
  id: string;
  /** All files that ship with the plugin (main DLL first, then dependencies). */
  files: string[];
  sizeBytes: number;
  /** Unix seconds. */
  importedAt: number;
}

export interface PluginConfigFile {
  pluginId: string;
  /** `configs/<Plugin>/<Plugin>.jsonc` once the server has generated it. */
  exists: boolean;
  path: string;
}

export interface ContentFile {
  fileName: string;
  kind: "addon" | "map";
  sizeBytes: number;
}

// ── Commands ──

export const hosting = {
  overview: () => invoke<HostingOverview>("hosting_overview"),
  setupCheck: () => invoke<SetupCheck>("hosting_setup_check"),
  install: (options: InstallOptions) => invoke<void>("hosting_install", { options }),
  cancelTask: () => invoke<void>("hosting_cancel_task"),
  steamcmdInput: (value: string) => invoke<void>("hosting_steamcmd_input", { value }),
  checkUpdates: () => invoke<UpdateState>("hosting_check_updates"),
  applyUpdates: () => invoke<void>("hosting_apply_updates"),
  verify: () => invoke<void>("hosting_verify"),
  /** Opens steam://validate/1422450 so Steam restores the client's modified game files. */
  repairClient: () => invoke<void>("hosting_repair_client"),
  uninstall: () => invoke<void>("hosting_uninstall"),
  maps: () => invoke<string[]>("hosting_maps"),

  createServer: (name: string, network: NetworkMode) =>
    invoke<ServerConfig>("hosting_create_server", { name, network }),
  updateServer: (config: ServerConfig) => invoke<ServerConfig>("hosting_update_server", { config }),
  duplicateServer: (id: string) => invoke<ServerConfig>("hosting_duplicate_server", { id }),
  deleteServer: (id: string) => invoke<void>("hosting_delete_server", { id }),

  start: (id: string) => invoke<void>("hosting_start", { id }),
  stop: (id: string) => invoke<void>("hosting_stop", { id }),
  restart: (id: string) => invoke<void>("hosting_restart", { id }),
  runtime: (id: string) => invoke<ServerRuntime>("hosting_runtime", { id }),
  consoleHistory: (id: string) => invoke<ConsoleLine[]>("hosting_console_history", { id }),
  sendCommand: (id: string, command: string) => invoke<void>("hosting_send_command", { id, command }),
  /** Kicks by player slot (PlayerInfo.slot). */
  kick: (id: string, slot: number) => invoke<void>("hosting_kick", { id, slot }),
  /** The user copied the connect command: clears `sdrIdChanged`. */
  markShared: (id: string) => invoke<void>("hosting_mark_shared", { id }),
  checkReachability: (id: string) => invoke<NetworkInfo>("hosting_check_reachability", { id }),
  openFolder: (id: string | null) => invoke<void>("hosting_open_folder", { id }),

  pluginLibrary: () => invoke<PluginEntry[]>("hosting_plugin_library"),
  /** Paths to .dll files, .zip archives or folders. Returns the imported entries. */
  importPlugins: (paths: string[]) => invoke<PluginEntry[]>("hosting_import_plugins", { paths }),
  removePlugin: (pluginId: string) => invoke<void>("hosting_remove_plugin", { pluginId }),
  /** Live on a running server (hot-load / dw_plugin), persisted to the profile. */
  setPluginEnabled: (id: string, pluginId: string, enabled: boolean) =>
    invoke<ServerConfig>("hosting_set_plugin_enabled", { id, pluginId, enabled }),
  pluginConfigs: (id: string) => invoke<PluginConfigFile[]>("hosting_plugin_configs", { id }),
  readPluginConfig: (id: string, pluginId: string) =>
    invoke<string>("hosting_read_plugin_config", { id, pluginId }),
  /** Saves and, on a running server, runs dw_reloadconfig. */
  writePluginConfig: (id: string, pluginId: string, text: string) =>
    invoke<void>("hosting_write_plugin_config", { id, pluginId, text }),
  /** Deletes the file; the plugin regenerates defaults on next load. */
  resetPluginConfig: (id: string, pluginId: string) =>
    invoke<void>("hosting_reset_plugin_config", { id, pluginId }),

  content: (id: string) => invoke<ContentFile[]>("hosting_content", { id }),
  importContent: (id: string, paths: string[], kind: "addon" | "map") =>
    invoke<ContentFile[]>("hosting_import_content", { id, paths, kind }),
  removeContent: (id: string, fileName: string) =>
    invoke<void>("hosting_remove_content", { id, fileName }),
};

// ── Events ──

export const onHostingTask = (cb: (t: TaskProgress) => void): Promise<UnlistenFn> =>
  listen<TaskProgress>("hosting://task", (e) => cb(e.payload));

/** Fired whenever a server's runtime changes (state, players, meters every ~2 s while running). */
export const onServerRuntime = (cb: (r: ServerRuntime) => void): Promise<UnlistenFn> =>
  listen<ServerRuntime>("hosting://runtime", (e) => cb(e.payload));

/** Batched console output, at most every 100 ms per server. */
export const onConsoleLines = (
  cb: (p: { id: string; lines: ConsoleLine[] }) => void
): Promise<UnlistenFn> =>
  listen<{ id: string; lines: ConsoleLine[] }>("hosting://console", (e) => cb(e.payload));

/** Fired when anything in the overview changes (install finished, updates found, servers added). */
export const onHostingChanged = (cb: () => void): Promise<UnlistenFn> =>
  listen<null>("hosting://changed", () => cb());

import type { NetworkMode, ServerConfig, ServerRuntime, ServerState } from "@/lib/hosting";

export function formatBytes(bytes: number): string {
  if (!Number.isFinite(bytes) || bytes <= 0) return "0 B";
  const units = ["B", "KB", "MB", "GB", "TB"];
  let value = bytes;
  let unit = 0;
  while (value >= 1024 && unit < units.length - 1) {
    value /= 1024;
    unit++;
  }
  const digits = value >= 100 || unit === 0 ? 0 : 1;
  return `${value.toFixed(digits)} ${units[unit]}`;
}

/** "45s", "3m 12s", "1h 04m", "2d 3h". */
export function formatDuration(totalSeconds: number): string {
  const s = Math.max(0, Math.floor(totalSeconds));
  if (s < 60) return `${s}s`;
  const m = Math.floor(s / 60);
  if (m < 60) return `${m}m ${String(s % 60).padStart(2, "0")}s`;
  const h = Math.floor(m / 60);
  if (h < 24) return `${h}h ${String(m % 60).padStart(2, "0")}m`;
  return `${Math.floor(h / 24)}d ${h % 24}h`;
}

export function formatPercent(fraction: number): string {
  return `${Math.round(Math.min(1, Math.max(0, fraction)) * 100)}%`;
}

/** Unix seconds to a short local date/time. */
export function formatTimestamp(unixSeconds: number): string {
  return new Date(unixSeconds * 1000).toLocaleString(undefined, {
    dateStyle: "medium",
    timeStyle: "short",
  });
}

const STATE_LABELS: Record<ServerState, string> = {
  stopped: "Stopped",
  starting: "Starting",
  running: "Running",
  stopping: "Stopping",
  crashed: "Crashed",
  waiting_for_deadworks: "Waiting for a Deadworks update",
  updating: "Updating",
};

export function stateLabel(state: ServerState): string {
  return STATE_LABELS[state];
}

export type StateTone = "ok" | "busy" | "off" | "bad" | "warn";

export function stateTone(state: ServerState): StateTone {
  switch (state) {
    case "running":
      return "ok";
    case "starting":
    case "stopping":
    case "updating":
      return "busy";
    case "crashed":
      return "bad";
    case "waiting_for_deadworks":
      return "warn";
    case "stopped":
      return "off";
  }
}

/** The server process exists (or is about to), so config edits wait for a restart. */
export function isLive(state: ServerState): boolean {
  return state === "running" || state === "starting" || state === "stopping";
}

export const NETWORK_LABELS: Record<NetworkMode, string> = {
  sdr: "SDR",
  port_forward: "Port forwarding",
  lan: "Same network (LAN)",
};

/** Backend errors arrive as plain strings from Rust; anything else is unexpected. */
export function errorMessage(e: unknown): string {
  if (typeof e === "string" && e.trim()) return e;
  if (e instanceof Error && e.message) return e.message;
  if (typeof e === "object" && e !== null && "message" in e) {
    const msg = (e as { message: unknown }).message;
    if (typeof msg === "string" && msg) return msg;
  }
  return "Something went wrong. Please try again.";
}

/** Map VPK file name to the map name the server loads. */
export function mapNameFromFile(fileName: string): string {
  return fileName.replace(/\.vpk$/i, "");
}

/** A runtime for a profile the backend hasn't reported on yet. */
export function emptyRuntime(config: ServerConfig): ServerRuntime {
  return {
    id: config.id,
    state: "stopped",
    pid: null,
    startedAt: null,
    players: [],
    cpuPercent: 0,
    memoryBytes: 0,
    message: null,
    exitCode: null,
    network: {
      mode: config.network,
      port: config.port,
      lanIps: [],
      publicIp: null,
      reachability: "unknown",
      sdrId: null,
      sdrIdChanged: false,
      connectCommand: null,
    },
    moderation: false,
  };
}

export function readLocal(key: string): string | null {
  try {
    return window.localStorage.getItem(key);
  } catch {
    return null;
  }
}

export function writeLocal(key: string, value: string): void {
  try {
    window.localStorage.setItem(key, value);
  } catch {
    // Storage can be unavailable; remembering is only a convenience.
  }
}

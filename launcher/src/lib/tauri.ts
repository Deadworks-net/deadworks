import { getCurrentWindow } from "@tauri-apps/api/window";
import { WebviewWindow } from "@tauri-apps/api/webviewWindow";
import { invoke } from "@tauri-apps/api/core";
import { listen, type UnlistenFn } from "@tauri-apps/api/event";
import { load, type Store } from "@tauri-apps/plugin-store";
import type {
  Server,
  ConnectResult,
  DownloadProgress,
  BootstrapStatus,
} from "./types";

// ── Window controls ──

const appWindow = getCurrentWindow();

export function minimizeWindow() {
  appWindow.minimize();
}

export async function toggleMaximize() {
  if (await appWindow.isMaximized()) {
    appWindow.unmaximize();
  } else {
    appWindow.maximize();
  }
}

export function closeWindow() {
  appWindow.close();
}

export async function openSettingsWindow() {
  try {
    const existing = await WebviewWindow.getByLabel("settings");
    if (existing !== null) {
      try {
        await existing.unminimize();
        await existing.setFocus();
        return;
      } catch (error) {
        console.error("Error focusing settings window:", error);
      }
    }
  } catch (error) {
    console.error("Error retrieving settings window:", error);
  }

  const webview = new WebviewWindow("settings", {
    url: "/settings",
    title: "Settings",
    width: 900,
    height: 700,
    center: true,
    decorations: false,
    resizable: true,
  });

  webview.once("tauri://error", function (e) {
    console.error("Error opening settings window:", e);
  });
}

export async function openServerInfoWindow(server: Server, apiUrl?: string) {
  const label = `server-info-${server.id}`;
  try {
    const existing = await WebviewWindow.getByLabel(label);
    if (existing !== null) {
      try {
        await existing.unminimize();
        await existing.setFocus();
        return;
      } catch (error) {
        console.error("Error focusing server info window:", error);
      }
    }
  } catch (error) {
    console.error("Error retrieving server info window:", error);
  }

  const params = new URLSearchParams({
    server: encodeURIComponent(JSON.stringify(server)),
    ...(apiUrl ? { apiUrl } : {}),
  });

  const webview = new WebviewWindow(label, {
    url: `/server-info?${params}`,
    title: server.name,
    width: 400,
    height: 600,
    center: true,
    decorations: false,
    resizable: true,
  });

  webview.once("tauri://error", function (e) {
    console.error("Error opening server info window:", e);
  });
}

// ── Store ──

let storePromise: Promise<Store> | null = null;

export function getStore(): Promise<Store> {
  if (!storePromise) {
    storePromise = load("settings.json");
  }
  return storePromise;
}

// ── Server API ──

const DEFAULT_API_URL = "https://api.deadworks.net";

export function getApiUrl(apiEndpoint: string): string {
  if (import.meta.env.DEV && apiEndpoint === "local") {
    return "http://localhost:8787";
  }
  return import.meta.env.VITE_API_URL || DEFAULT_API_URL;
}

export async function fetchServers(apiUrl: string): Promise<Server[]> {
  const res = await fetch(`${apiUrl}/api/servers`, { cache: "no-store" });
  if (!res.ok) throw new Error(`HTTP ${res.status}`);
  const data: { servers: Server[] } = await res.json();
  return data.servers;
}

export function pingServer(addr: string): Promise<number> {
  return invoke<number>("ping_server", { addr });
}

export function prepareAndConnect(
  serverId: string,
  addr: string
): Promise<ConnectResult> {
  return invoke<ConnectResult>("prepare_and_connect", {
    serverId,
    addr,
  });
}

export function listenDownloadProgress(
  callback: (progress: DownloadProgress) => void
): Promise<UnlistenFn> {
  return listen<DownloadProgress>("download-progress", (event) => {
    callback(event.payload);
  });
}

// ── Bootstrap addon ──

/** On-disk state only; no network call, so this is safe to ask for on mount. */
export function bootstrapStatus(): Promise<BootstrapStatus> {
  return invoke<BootstrapStatus>("bootstrap_status");
}

/** Apply a staged update now. Still `restart_required` means Deadlock has it open. */
export function retryBootstrapInstall(): Promise<BootstrapStatus> {
  return invoke<BootstrapStatus>("retry_bootstrap_install");
}

export function listenBootstrapStatus(
  callback: (status: BootstrapStatus) => void
): Promise<UnlistenFn> {
  return listen<BootstrapStatus>("bootstrap-status", (event) => {
    callback(event.payload);
  });
}


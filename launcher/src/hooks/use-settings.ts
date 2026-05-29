import { useState, useEffect, useCallback } from "react";
import { listen } from "@tauri-apps/api/event";
import { emitTo } from "@tauri-apps/api/event";
import { getStore, getApiUrl } from "@/lib/tauri";

export interface Settings {
  apiEndpoint: string;
  setApiEndpoint: (endpoint: string) => Promise<void>;
  apiUrl: string;
  telemetryEnabled: boolean;
  setTelemetryEnabled: (enabled: boolean) => Promise<void>;
  launcherBackgroundPath: string | null;
  setLauncherBackgroundPath: (path: string | null) => Promise<void>;
  launcherBackgroundZoom: number;
  setLauncherBackgroundZoom: (zoom: number) => Promise<void>;
  launcherBackgroundPositionX: number;
  setLauncherBackgroundPositionX: (position: number) => Promise<void>;
  launcherBackgroundPositionY: number;
  setLauncherBackgroundPositionY: (position: number) => Promise<void>;
  resetLauncherBackgroundLayout: () => Promise<void>;
}

interface SettingsPayload {
  apiEndpoint: string;
  telemetryEnabled: boolean;
  launcherBackgroundPath: string | null;
  launcherBackgroundZoom: number;
  launcherBackgroundPositionX: number;
  launcherBackgroundPositionY: number;
}

const DEFAULT_BACKGROUND_ZOOM = 100;
const DEFAULT_BACKGROUND_POSITION = 50;

function clamp(value: number, min: number, max: number) {
  return Math.min(max, Math.max(min, value));
}

export function useSettings(): Settings {
  const [apiEndpoint, setApiEndpointState] = useState("prod");
  const [telemetryEnabled, setTelemetryEnabledState] = useState(true);
  const [launcherBackgroundPath, setLauncherBackgroundPathState] = useState<string | null>(null);
  const [launcherBackgroundZoom, setLauncherBackgroundZoomState] = useState(DEFAULT_BACKGROUND_ZOOM);
  const [launcherBackgroundPositionX, setLauncherBackgroundPositionXState] = useState(DEFAULT_BACKGROUND_POSITION);
  const [launcherBackgroundPositionY, setLauncherBackgroundPositionYState] = useState(DEFAULT_BACKGROUND_POSITION);

  useEffect(() => {
    getStore().then(async (store) => {
      const endpoint = await store.get<string>("api_endpoint");
      if (endpoint) setApiEndpointState(endpoint);
      const telemetry = await store.get<boolean>("telemetry_enabled");
      if (telemetry !== undefined && telemetry !== null) {
        setTelemetryEnabledState(telemetry);
      }
      const backgroundPath = await store.get<string>("launcher_background_path");
      setLauncherBackgroundPathState(backgroundPath || null);
      const backgroundZoom = await store.get<number>("launcher_background_zoom");
      setLauncherBackgroundZoomState(clamp(backgroundZoom ?? DEFAULT_BACKGROUND_ZOOM, 100, 250));
      const backgroundPositionX = await store.get<number>("launcher_background_position_x");
      setLauncherBackgroundPositionXState(clamp(backgroundPositionX ?? DEFAULT_BACKGROUND_POSITION, 0, 100));
      const backgroundPositionY = await store.get<number>("launcher_background_position_y");
      setLauncherBackgroundPositionYState(clamp(backgroundPositionY ?? DEFAULT_BACKGROUND_POSITION, 0, 100));
    });
  }, []);

  useEffect(() => {
    const unlisten = listen<SettingsPayload>("settings-changed", (event) => {
      setApiEndpointState(event.payload.apiEndpoint);
      setTelemetryEnabledState(event.payload.telemetryEnabled);
      setLauncherBackgroundPathState(event.payload.launcherBackgroundPath);
      setLauncherBackgroundZoomState(event.payload.launcherBackgroundZoom);
      setLauncherBackgroundPositionXState(event.payload.launcherBackgroundPositionX);
      setLauncherBackgroundPositionYState(event.payload.launcherBackgroundPositionY);
    });
    return () => { unlisten.then((fn) => fn()); };
  }, []);

  const emit = useCallback((next: SettingsPayload) => {
    emitTo("main", "settings-changed", next);
  }, []);

  const emitCurrent = useCallback((overrides: Partial<SettingsPayload>) => {
    emit({
      apiEndpoint,
      telemetryEnabled,
      launcherBackgroundPath,
      launcherBackgroundZoom,
      launcherBackgroundPositionX,
      launcherBackgroundPositionY,
      ...overrides,
    });
  }, [
    emit,
    apiEndpoint,
    telemetryEnabled,
    launcherBackgroundPath,
    launcherBackgroundZoom,
    launcherBackgroundPositionX,
    launcherBackgroundPositionY,
  ]);

  const setApiEndpoint = useCallback(async (endpoint: string) => {
    setApiEndpointState(endpoint);
    const store = await getStore();
    await store.set("api_endpoint", endpoint);
    await store.save();
    emitCurrent({ apiEndpoint: endpoint });
  }, [emitCurrent]);

  const setTelemetryEnabled = useCallback(async (enabled: boolean) => {
    setTelemetryEnabledState(enabled);
    const store = await getStore();
    await store.set("telemetry_enabled", enabled);
    await store.save();
    emitCurrent({ telemetryEnabled: enabled });
  }, [emitCurrent]);

  const setLauncherBackgroundPath = useCallback(async (path: string | null) => {
    setLauncherBackgroundPathState(path);
    const store = await getStore();
    if (path) {
      await store.set("launcher_background_path", path);
    } else {
      await store.delete("launcher_background_path");
    }
    await store.save();
    emitCurrent({ launcherBackgroundPath: path });
  }, [emitCurrent]);

  const setLauncherBackgroundZoom = useCallback(async (zoom: number) => {
    const next = clamp(zoom, 100, 250);
    setLauncherBackgroundZoomState(next);
    const store = await getStore();
    await store.set("launcher_background_zoom", next);
    await store.save();
    emitCurrent({ launcherBackgroundZoom: next });
  }, [emitCurrent]);

  const setLauncherBackgroundPositionX = useCallback(async (position: number) => {
    const next = clamp(position, 0, 100);
    setLauncherBackgroundPositionXState(next);
    const store = await getStore();
    await store.set("launcher_background_position_x", next);
    await store.save();
    emitCurrent({ launcherBackgroundPositionX: next });
  }, [emitCurrent]);

  const setLauncherBackgroundPositionY = useCallback(async (position: number) => {
    const next = clamp(position, 0, 100);
    setLauncherBackgroundPositionYState(next);
    const store = await getStore();
    await store.set("launcher_background_position_y", next);
    await store.save();
    emitCurrent({ launcherBackgroundPositionY: next });
  }, [emitCurrent]);

  const resetLauncherBackgroundLayout = useCallback(async () => {
    setLauncherBackgroundZoomState(DEFAULT_BACKGROUND_ZOOM);
    setLauncherBackgroundPositionXState(DEFAULT_BACKGROUND_POSITION);
    setLauncherBackgroundPositionYState(DEFAULT_BACKGROUND_POSITION);
    const store = await getStore();
    await store.delete("launcher_background_zoom");
    await store.delete("launcher_background_position_x");
    await store.delete("launcher_background_position_y");
    await store.save();
    emitCurrent({
      launcherBackgroundZoom: DEFAULT_BACKGROUND_ZOOM,
      launcherBackgroundPositionX: DEFAULT_BACKGROUND_POSITION,
      launcherBackgroundPositionY: DEFAULT_BACKGROUND_POSITION,
    });
  }, [emitCurrent]);

  return {
    apiEndpoint,
    setApiEndpoint,
    apiUrl: getApiUrl(apiEndpoint),
    telemetryEnabled,
    setTelemetryEnabled,
    launcherBackgroundPath,
    setLauncherBackgroundPath,
    launcherBackgroundZoom,
    setLauncherBackgroundZoom,
    launcherBackgroundPositionX,
    setLauncherBackgroundPositionX,
    launcherBackgroundPositionY,
    setLauncherBackgroundPositionY,
    resetLauncherBackgroundLayout,
  };
}

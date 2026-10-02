import { useState, useEffect, useCallback } from "react";
import { listen, emitTo } from "@tauri-apps/api/event";
import { getStore, getApiUrl } from "@/lib/tauri";

export type Theme = "light" | "dark" | "system";

export interface Settings {
  apiEndpoint: string;
  setApiEndpoint: (endpoint: string) => void;
  apiUrl: string;
  telemetryEnabled: boolean;
  setTelemetryEnabled: (enabled: boolean) => void;
  theme: Theme;
  setTheme: (theme: Theme) => void;
}

interface SettingsPayload {
  apiEndpoint: string;
  telemetryEnabled: boolean;
  theme: Theme;
}

export function useSettings(): Settings {
  const [apiEndpoint, setApiEndpointState] = useState("prod");
  const [telemetryEnabled, setTelemetryEnabledState] = useState(true);
  const [theme, setThemeState] = useState<Theme>("system");

  useEffect(() => {
    getStore().then(async (store) => {
      const endpoint = await store.get<string>("api_endpoint");
      if (endpoint) setApiEndpointState(endpoint);

      const telemetry = await store.get<boolean>("telemetry_enabled");
      if (telemetry !== undefined && telemetry !== null) {
        setTelemetryEnabledState(telemetry);
      }

      const storedTheme = await store.get<Theme>("theme");
      if (storedTheme) setThemeState(storedTheme);
    });
  }, []);

  useEffect(() => {
    const unlisten = listen<SettingsPayload>("settings-changed", (event) => {
      setApiEndpointState(event.payload.apiEndpoint);
      setTelemetryEnabledState(event.payload.telemetryEnabled);
      setThemeState(event.payload.theme);
    });
    return () => { unlisten.then((fn) => fn()); };
  }, []);

  const emit = useCallback((next: SettingsPayload) => {
    emitTo("main", "settings-changed", next);
  }, []);

  const setApiEndpoint = useCallback(async (endpoint: string) => {
    setApiEndpointState(endpoint);
    const store = await getStore();
    await store.set("api_endpoint", endpoint);
    await store.save();
    emit({ apiEndpoint: endpoint, telemetryEnabled, theme });
  }, [emit, telemetryEnabled, theme]);

  const setTelemetryEnabled = useCallback(async (enabled: boolean) => {
    setTelemetryEnabledState(enabled);
    const store = await getStore();
    await store.set("telemetry_enabled", enabled);
    await store.save();
    emit({ apiEndpoint, telemetryEnabled: enabled, theme });
  }, [emit, apiEndpoint, theme]);

  const setTheme = useCallback(async (newTheme: Theme) => {
    setThemeState(newTheme);
    const store = await getStore();
    await store.set("theme", newTheme);
    await store.save();

    localStorage.setItem("app-theme", newTheme);

    emit({ apiEndpoint, telemetryEnabled, theme: newTheme });
  }, [emit, apiEndpoint, telemetryEnabled]);

  return {
    apiEndpoint,
    setApiEndpoint,
    apiUrl: getApiUrl(apiEndpoint),
    telemetryEnabled,
    setTelemetryEnabled,
    theme,
    setTheme,
  };
}

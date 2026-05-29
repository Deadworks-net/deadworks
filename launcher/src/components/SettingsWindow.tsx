import { useState, useEffect } from "react";
import { getCurrentWindow } from "@tauri-apps/api/window";
import { emitTo } from "@tauri-apps/api/event";
import { invoke } from "@tauri-apps/api/core";
import { open } from "@tauri-apps/plugin-dialog";
import { useSettings } from "@/hooks/use-settings";
import { getStore } from "@/lib/tauri";
import { cn } from "@/lib/utils";
import styles from "./SettingsWindow.module.css";

const NAV_ITEMS = [
  { id: "general", label: "General" },
  ...(import.meta.env.DEV ? [{ id: "developer", label: "Developer" }] : []),
];

function SettingRow({
  title,
  description,
  control,
}: {
  title: string;
  description: string;
  control: React.ReactNode;
}) {
  return (
    <div className={styles.settingRow}>
      <div className={styles.settingInfo}>
        <div className={styles.settingLabel}>{title}</div>
        <div className={styles.settingDesc}>{description}</div>
      </div>
      <div>{control}</div>
    </div>
  );
}

export default function SettingsWindow() {
  const {
    apiEndpoint,
    setApiEndpoint,
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
  } = useSettings();
  const [activeSection, setActiveSection] = useState("general");
  const [autostart, setAutostart] = useState(false);
  const [detectedPath, setDetectedPath] = useState<string | null>(null);
  const [currentPath, setCurrentPath] = useState<string | null>(null);
  const [isOverridden, setIsOverridden] = useState(false);
  const [gameDirError, setGameDirError] = useState<string | null>(null);
  const [backgroundError, setBackgroundError] = useState<string | null>(null);
  const win = getCurrentWindow();

  useEffect(() => {
    invoke<boolean>("plugin:autostart|is_enabled").then(setAutostart).catch(() => {});
  }, []);

  useEffect(() => {
    invoke<string>("get_detected_game_dir")
      .then(setDetectedPath)
      .catch(() => setDetectedPath(null));

    getStore().then(async (store) => {
      const override = await store.get<string>("game_dir_override");
      if (override) {
        setCurrentPath(override);
        setIsOverridden(true);
      } else {
        invoke<string>("get_detected_game_dir")
          .then(setCurrentPath)
          .catch(() => {});
        setIsOverridden(false);
      }
    });
  }, []);

  const handleBrowseGameDir = async () => {
    const selected = await open({ directory: true, title: "Select Deadlock game folder" });
    if (!selected) return;
    try {
      await invoke("set_game_dir", { path: selected });
      const store = await getStore();
      await store.set("game_dir_override", selected);
      await store.save();
      setCurrentPath(selected);
      setIsOverridden(true);
      setGameDirError(null);
    } catch (e) {
      setGameDirError(typeof e === "string" ? e : String(e));
    }
  };

  const handleResetGameDir = async () => {
    invoke("reset_game_dir");
    const store = await getStore();
    await store.delete("game_dir_override");
    await store.save();
    setCurrentPath(detectedPath);
    setIsOverridden(false);
    setGameDirError(null);
  };

  const handleBrowseBackground = async () => {
    const selected = await open({
      title: "Select launcher background image",
      filters: [
        { name: "Images", extensions: ["png", "jpg", "jpeg", "webp", "gif"] },
      ],
    });
    const path = Array.isArray(selected) ? selected[0] : selected;
    if (!path) return;
    try {
      await invoke("validate_launcher_background", { path });
      await setLauncherBackgroundPath(path);
      setBackgroundError(null);
    } catch (e) {
      setBackgroundError(typeof e === "string" ? e : String(e));
    }
  };

  const toggleAutostart = async (enabled: boolean) => {
    try {
      await invoke(enabled ? "plugin:autostart|enable" : "plugin:autostart|disable");
      setAutostart(enabled);
      const store = await getStore();
      await store.set("autostart_set", true);
      await store.save();
    } catch (e) {
      console.error("Failed to toggle autostart:", e);
    }
  };

  return (
    <div className={styles.window}>
      {/* Titlebar */}
      <div className={styles.titlebar} data-tauri-drag-region>
        <div className={styles.titlebarLeft}>
          <span className={styles.titlebarTitle}>SETTINGS</span>
        </div>
        <div className={styles.windowControls}>
          <button onClick={() => win.minimize()} className={styles.winBtn} aria-label="Minimize">
            <svg width="10" height="1" viewBox="0 0 10 1">
              <rect width="10" height="1" fill="currentColor" />
            </svg>
          </button>
          <button onClick={() => win.toggleMaximize()} className={styles.winBtn} aria-label="Maximize">
            <svg width="10" height="10" viewBox="0 0 10 10">
              <rect width="10" height="10" fill="none" stroke="currentColor" strokeWidth="1" />
            </svg>
          </button>
          <button onClick={() => win.close()} className={cn(styles.winBtn, styles.winClose)} aria-label="Close">
            <svg width="10" height="10" viewBox="0 0 10 10">
              <line x1="0" y1="0" x2="10" y2="10" stroke="currentColor" strokeWidth="1.2" />
              <line x1="10" y1="0" x2="0" y2="10" stroke="currentColor" strokeWidth="1.2" />
            </svg>
          </button>
        </div>
      </div>

      {/* Body */}
      <div className={styles.body}>
        {/* Left sidebar */}
        <div className={styles.sidebar}>
          <div className={styles.navList}>
            {NAV_ITEMS.map((item) => (
              <button
                key={item.id}
                onClick={() => setActiveSection(item.id)}
                className={cn(
                  styles.navItem,
                  activeSection === item.id && styles.navItemActive
                )}
              >
                {item.label}
              </button>
            ))}
          </div>
        </div>

        {/* Right content */}
        <div className={styles.content}>
          {activeSection === "general" && (
            <>
              <h2 className={styles.sectionTitle}>General</h2>
              <div className={styles.sectionSubtitle}>Startup</div>

              <SettingRow
                title="Launch on Startup"
                description="Automatically start Deadworks when you log in"
                control={
                  <button
                    className={cn(styles.toggle, autostart && styles.toggleOn)}
                    onClick={() => toggleAutostart(!autostart)}
                    role="switch"
                    aria-checked={autostart}
                  >
                    <span className={styles.toggleThumb} />
                  </button>
                }
              />

              <div className={styles.sectionSubtitle}>Appearance</div>

              <div className={styles.settingRow}>
                <div className={styles.settingInfo}>
                  <div className={styles.settingLabel}>Launcher Background</div>
                  <div className={styles.settingDesc}>
                    Choose a local image to display behind the launcher
                  </div>
                  <div className={styles.pathDisplay}>
                    {launcherBackgroundPath || "Default gradient"}
                  </div>
                </div>
                <div className={styles.pathButtons}>
                  <button className={styles.devBtn} onClick={handleBrowseBackground}>
                    Browse
                  </button>
                  {launcherBackgroundPath && (
                    <button className={styles.devBtn} onClick={() => setLauncherBackgroundPath(null)}>
                      Reset
                    </button>
                  )}
                </div>
              </div>
              {backgroundError && (
                <div className={styles.inlineError}>{backgroundError}</div>
              )}

              {launcherBackgroundPath && (
                <div className={styles.backgroundLayoutControls}>
                  <div className={styles.sliderRow}>
                    <label className={styles.sliderLabel} htmlFor="background-zoom">
                      Zoom <span>{launcherBackgroundZoom}%</span>
                    </label>
                    <input
                      id="background-zoom"
                      className={styles.slider}
                      type="range"
                      min="100"
                      max="250"
                      step="1"
                      value={launcherBackgroundZoom}
                      onChange={(e) => setLauncherBackgroundZoom(Number(e.target.value))}
                    />
                  </div>
                  <div className={styles.sliderRow}>
                    <label className={styles.sliderLabel} htmlFor="background-position-x">
                      Horizontal <span>{launcherBackgroundPositionX}%</span>
                    </label>
                    <input
                      id="background-position-x"
                      className={styles.slider}
                      type="range"
                      min="0"
                      max="100"
                      step="1"
                      value={launcherBackgroundPositionX}
                      onChange={(e) => setLauncherBackgroundPositionX(Number(e.target.value))}
                    />
                  </div>
                  <div className={styles.sliderRow}>
                    <label className={styles.sliderLabel} htmlFor="background-position-y">
                      Vertical <span>{launcherBackgroundPositionY}%</span>
                    </label>
                    <input
                      id="background-position-y"
                      className={styles.slider}
                      type="range"
                      min="0"
                      max="100"
                      step="1"
                      value={launcherBackgroundPositionY}
                      onChange={(e) => setLauncherBackgroundPositionY(Number(e.target.value))}
                    />
                  </div>
                  <button className={styles.devBtn} onClick={resetLauncherBackgroundLayout}>
                    Reset Position
                  </button>
                </div>
              )}

              <div className={styles.sectionSubtitle}>Game Location</div>

              <div className={styles.settingRow}>
                <div className={styles.settingInfo}>
                  <div className={styles.settingLabel}>Deadlock Install Path</div>
                  <div className={styles.settingDesc}>
                    {isOverridden ? "Manually set" : "Auto-detected"}
                    {isOverridden && detectedPath && (
                      <> — detected: <span className={styles.pathMono}>{detectedPath}</span></>
                    )}
                  </div>
                  <div className={styles.pathDisplay}>
                    {currentPath || "Not found — please set manually"}
                  </div>
                  {gameDirError && (
                    <div className={styles.pathError}>{gameDirError}</div>
                  )}
                </div>
                <div className={styles.pathButtons}>
                  <button className={styles.devBtn} onClick={handleBrowseGameDir}>
                    Browse
                  </button>
                  {isOverridden && (
                    <button className={styles.devBtn} onClick={handleResetGameDir}>
                      Reset
                    </button>
                  )}
                </div>
              </div>

              <div className={styles.sectionSubtitle}>Updates</div>

              <SettingRow
                title="Check for Updates"
                description="Check if a new version of the launcher is available"
                control={
                  <button
                    className={styles.devBtn}
                    onClick={() => emitTo("main", "check-for-updates")}
                  >
                    Check Now
                  </button>
                }
              />

              <div className={styles.sectionSubtitle}>Privacy</div>

              <SettingRow
                title="Share anonymous usage"
                description="Sends a random ID, app version, and OS so we can see install counts and daily active users. No personal data. Toggle off to disable."
                control={
                  <button
                    className={cn(styles.toggle, telemetryEnabled && styles.toggleOn)}
                    onClick={() => setTelemetryEnabled(!telemetryEnabled)}
                    role="switch"
                    aria-checked={telemetryEnabled}
                  >
                    <span className={styles.toggleThumb} />
                  </button>
                }
              />
            </>
          )}

          {activeSection === "developer" && (
            <>
              <h2 className={styles.sectionTitle}>Developer</h2>
              <div className={styles.sectionSubtitle}>Developer Tools</div>

              <SettingRow
                title="API Endpoint"
                description="Switch between production and local API for testing"
                control={
                  <select
                    value={apiEndpoint}
                    onChange={(e) => setApiEndpoint(e.target.value)}
                    className={styles.select}
                  >
                    <option value="prod">Production (api.deadworks.net)</option>
                    <option value="local">Local (localhost:8787)</option>
                  </select>
                }
              />

              <SettingRow
                title="Test Update UI"
                description="Simulate an available update to preview the update manager"
                control={
                  <button
                    className={styles.devBtn}
                    onClick={() => emitTo("main", "test-update-ui")}
                  >
                    Trigger
                  </button>
                }
              />
            </>
          )}
        </div>
      </div>
    </div>
  );
}

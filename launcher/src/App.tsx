import { useEffect, useState, type CSSProperties } from "react";
import { invoke } from "@tauri-apps/api/core";
import Titlebar from "@/components/Titlebar";
import ServersPage from "@/components/ServersPage";
import UpdateManager from "@/components/UpdateManager";
import ConnectDialog from "@/components/ConnectDialog";
import DeepLinkErrorDialog from "@/components/DeepLinkErrorDialog";
import { useSettings } from "@/hooks/use-settings";
import { useDeepLink } from "@/hooks/use-deep-link";
import { getStore } from "@/lib/tauri";
import styles from "./App.module.css";

export default function App() {
  const settings = useSettings();
  const { request, clear } = useDeepLink(settings.apiUrl);
  const [backgroundDataUrl, setBackgroundDataUrl] = useState<string | null>(null);
  const backgroundStyle = backgroundDataUrl
    ? ({
        backgroundImage: `linear-gradient(135deg, rgba(20, 20, 20, 0.22) 0%, rgba(0, 0, 0, 0.38) 100%), url("${backgroundDataUrl}")`,
        backgroundPosition: `center, ${settings.launcherBackgroundPositionX}% ${settings.launcherBackgroundPositionY}%`,
        backgroundSize: `cover, ${settings.launcherBackgroundZoom}%`,
      } satisfies CSSProperties)
    : undefined;

  // On first launch, enable autostart by default
  useEffect(() => {
    getStore().then(async (store) => {
      const hasBeenSet = await store.get<boolean>("autostart_set");
      if (!hasBeenSet) {
        await invoke("plugin:autostart|enable").catch(() => {});
        await store.set("autostart_set", true);
        await store.save();
      }
    });
  }, []);

  useEffect(() => {
    if (!settings.launcherBackgroundPath) {
      setBackgroundDataUrl(null);
      return;
    }

    let cancelled = false;
    invoke<string>("get_launcher_background_data_url", {
      path: settings.launcherBackgroundPath,
    })
      .then((dataUrl) => {
        if (!cancelled) setBackgroundDataUrl(dataUrl);
      })
      .catch((error) => {
        console.error("Failed to load launcher background:", error);
        if (!cancelled) setBackgroundDataUrl(null);
      });

    return () => {
      cancelled = true;
    };
  }, [settings.launcherBackgroundPath]);

  return (
    <>
      <div className={styles.background} style={backgroundStyle} aria-hidden="true" />
      <Titlebar />
      <main className={styles.main}>
        <ServersPage apiUrl={settings.apiUrl} />
      </main>
      {request?.server && (
        <ConnectDialog
          key={request.requestId}
          server={request.server}
          onClose={clear}
        />
      )}
      {request?.error && (
        <DeepLinkErrorDialog
          key={`err-${request.requestId}`}
          message={request.error}
          onClose={clear}
        />
      )}
      <UpdateManager />
    </>
  );
}

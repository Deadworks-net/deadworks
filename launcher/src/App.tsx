import { useEffect } from "react";
import { invoke } from "@tauri-apps/api/core";
import Titlebar from "@/components/Titlebar";
import ServersPage from "@/components/ServersPage";
import UpdateManager from "@/components/UpdateManager";
import ConnectDialog from "@/components/ConnectDialog";
import DeepLinkErrorDialog from "@/components/DeepLinkErrorDialog";
import GameinfoErrorDialog from "@/components/GameinfoErrorDialog";
import BootstrapRestartDialog from "@/components/BootstrapRestartDialog";
import { useSettings } from "@/hooks/use-settings";
import { useDeepLink } from "@/hooks/use-deep-link";
import { getStore } from "@/lib/tauri";
import styles from "./App.module.css";

export default function App() {
  const settings = useSettings();
  const { request, clear } = useDeepLink(settings.apiUrl);

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

  return (
    <>
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
      {/* Both can be up at once (the game holding files causes either), and the
          gameinfo failure is the worse one — render it last so it lands on top. */}
      <BootstrapRestartDialog />
      <GameinfoErrorDialog />
      <UpdateManager />
    </>
  );
}

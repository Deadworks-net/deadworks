import { useEffect, useState } from "react";
import { invoke } from "@tauri-apps/api/core";
import Titlebar from "@/components/Titlebar";
import ServersPage from "@/components/ServersPage";
import MainNav, { type MainTab } from "@/components/MainNav";
import HostPage from "@/components/hosting/HostPage";
import UpdateManager from "@/components/UpdateManager";
import ConnectDialog from "@/components/ConnectDialog";
import DeepLinkErrorDialog from "@/components/DeepLinkErrorDialog";
import GameinfoErrorDialog from "@/components/GameinfoErrorDialog";
import BootstrapRestartDialog from "@/components/BootstrapRestartDialog";
import { useSettings } from "@/hooks/use-settings";
import { useDeepLink } from "@/hooks/use-deep-link";
import { getStore } from "@/lib/tauri";
import { cn } from "@/lib/utils";
import styles from "./App.module.css";

const TAB_KEY = "deadworks.mainTab";

function loadTab(): MainTab {
  try {
    return window.localStorage.getItem(TAB_KEY) === "host" ? "host" : "servers";
  } catch {
    return "servers";
  }
}

export default function App() {
  const settings = useSettings();
  const { request, clear } = useDeepLink(settings.apiUrl);
  const [tab, setTab] = useState<MainTab>(loadTab);
  // Pages stay mounted once visited so switching tabs doesn't refetch/re-ping.
  const [hostVisited, setHostVisited] = useState(tab === "host");

  const changeTab = (next: MainTab) => {
    setTab(next);
    if (next === "host") setHostVisited(true);
    try {
      window.localStorage.setItem(TAB_KEY, next);
    } catch {
      // Remembering the tab is only a convenience.
    }
  };

  const nav = <MainNav tab={tab} onChange={changeTab} />;

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
        <div className={cn(styles.pane, tab !== "servers" && styles.paneHidden)}>
          <ServersPage apiUrl={settings.apiUrl} nav={nav} />
        </div>
        {hostVisited && (
          <div className={cn(styles.pane, tab !== "host" && styles.paneHidden)}>
            <HostPage nav={nav} active={tab === "host"} />
          </div>
        )}
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

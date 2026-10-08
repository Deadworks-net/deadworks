import { useEffect, useState, type ReactNode } from "react";
import type { InstallOptions } from "@/lib/hosting";
import { useHosting } from "./use-hosting";
import { retryTask } from "./task-retry";
import { readLocal, writeLocal } from "./format";
import { ErrorNote, Loading } from "./ui";
import WelcomeScreen from "./WelcomeScreen";
import InstallScreen, { type InstallDraft } from "./InstallScreen";
import TaskProgressView from "./TaskProgressView";
import ServerRail from "./ServerRail";
import ServerPage, { rememberSubTab } from "./ServerPage";
import CreateServerFlow from "./CreateServerFlow";
import { ManagedHostingCard } from "./ManagedHostingCta";
import ui from "./ui.module.css";
import styles from "./HostPage.module.css";

const SELECTED_KEY = "deadworks.hosting.selected";

interface HostPageProps {
  /** Brand + top-level tabs, owned by App. */
  nav: ReactNode;
  /** The HOST tab is on screen. */
  active: boolean;
}

export default function HostPage({ nav, active }: HostPageProps) {
  const { overview, servers, task, loading, error, actions } = useHosting();
  const [screen, setScreen] = useState<"welcome" | "install">("welcome");
  const [lastInstall, setLastInstall] = useState<InstallDraft | null>(null);
  const [selectedId, setSelectedId] = useState<string | null>(() => readLocal(SELECTED_KEY));
  const [creating, setCreating] = useState(false);
  /** The right end of the toolbar, which the selected server's controls render into. */
  const [toolbarSlot, setToolbarSlot] = useState<HTMLDivElement | null>(null);

  const select = (id: string) => {
    setSelectedId(id);
    setCreating(false);
    writeLocal(SELECTED_KEY, id);
  };

  // Keep the selection pointing at a server that exists.
  useEffect(() => {
    if (servers.length > 0 && !servers.some((s) => s.config.id === selectedId)) {
      setSelectedId(servers[0].config.id);
    }
  }, [servers, selectedId]);

  const install = (options: InstallOptions) => {
    setLastInstall({
      root: options.root,
      source: options.source,
      copyMode: options.copyMode,
      steamUsername: options.steamUsername,
      allowModified: options.allowModified,
    });
    actions.install(options);
  };

  const holdReason = overview?.updates.holdReason ?? null;
  const taskView = task && (
    <TaskProgressView
      task={task}
      compact={!!overview?.installed}
      onCancel={actions.cancelTask}
      onDismiss={actions.dismissTask}
      onSteamInput={actions.steamcmdInput}
      onRepairClient={actions.repairClient}
      onUseModified={
        task.kind === "install" && lastInstall?.source === "client"
          ? () => install({ ...lastInstall, allowModified: true })
          : undefined
      }
      onRetry={() => retryTask(task, actions, lastInstall, () => setScreen("install"))}
    />
  );

  let body: ReactNode;
  if (!overview) {
    body = (
      <div className={styles.card}>
        {error && !loading ? (
          <div style={{ padding: 24 }}>
            <ErrorNote
              message={`Couldn't load hosting: ${error}`}
              actionLabel="Try again"
              onAction={actions.reload}
            />
          </div>
        ) : (
          <Loading />
        )}
      </div>
    );
  } else if (overview.platform === "unsupported") {
    body = (
      <div className={styles.card}>
        <div className={ui.centered}>
          <p className={styles.welcomeText}>Hosting requires Windows or Linux.</p>
          <ManagedHostingCard />
        </div>
      </div>
    );
  } else if (!overview.installed) {
    if (task) body = <div className={styles.card}>{taskView}</div>;
    else if (screen === "install")
      body = <InstallScreen initial={lastInstall} onBack={() => setScreen("welcome")} onInstall={install} />;
    else body = <WelcomeScreen onStart={() => setScreen("install")} />;
  } else {
    const selected = servers.find((s) => s.config.id === selectedId) ?? servers[0];
    const showCreate = creating || !selected;
    body = (
      <>
        {taskView}
        {holdReason && <ErrorNote warn message={holdReason} actionLabel="Try anyway" onAction={actions.clearHold} />}
        <div className={styles.layout}>
          {servers.length > 0 && (
            <ServerRail
              servers={servers}
              selectedId={selected?.config.id ?? null}
              creating={showCreate}
              onSelect={select}
              onNew={() => setCreating(true)}
            />
          )}
          <div className={styles.main}>
            {showCreate ? (
              <CreateServerFlow
                servers={servers}
                actions={actions}
                onCancel={selected ? () => setCreating(false) : null}
                onDone={(id) => {
                  rememberSubTab("settings");
                  select(id);
                }}
              />
            ) : (
              <ServerPage
                server={selected}
                actions={actions}
                holdReason={holdReason}
                toolbarSlot={toolbarSlot}
                active={active}
                onSelect={select}
                onDeleted={() => setSelectedId(null)}
              />
            )}
          </div>
        </div>
      </>
    );
  }

  return (
    <div className={styles.page}>
      <div className={styles.toolbar}>
        {nav}
        <div className={styles.toolbarRight} ref={setToolbarSlot} />
      </div>
      <div className={styles.body}>{body}</div>
    </div>
  );
}

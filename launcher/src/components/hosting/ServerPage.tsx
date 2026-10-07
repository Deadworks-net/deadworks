import { useCallback, useState } from "react";
import { hosting, type ServerSummary } from "@/lib/hosting";
import { cn } from "@/lib/utils";
import type { HostingActions } from "./use-hosting";
import { useConsole } from "./use-console";
import { readLocal, writeLocal } from "./format";
import { ErrorNote, useAction } from "./ui";
import ServerHeader from "./ServerHeader";
import OverviewTab from "./tabs/OverviewTab";
import ConsoleTab, { ConsoleLog } from "./tabs/ConsoleTab";
import PluginsTab from "./tabs/PluginsTab";
import AdminsTab, { type AdminPrefill } from "./tabs/AdminsTab";
import SettingsTab from "./tabs/SettingsTab";
import ContentTab from "./tabs/ContentTab";
import AdvancedTab from "./tabs/AdvancedTab";
import ui from "./ui.module.css";
import styles from "./ServerPage.module.css";

const SUB_TABS = ["overview", "console", "plugins", "admins", "settings", "content", "advanced"] as const;
export type SubTab = (typeof SUB_TABS)[number];
const SUB_TAB_KEY = "deadworks.hosting.subTab";

function loadSubTab(): SubTab {
  const saved = readLocal(SUB_TAB_KEY);
  return SUB_TABS.find((t) => t === saved) ?? "overview";
}

const CRASH_LINES = 50;

function CrashPanel({ server, lines, actions }: {
  server: ServerSummary;
  lines: ReturnType<typeof useConsole>["lines"];
  actions: HostingActions;
}) {
  const restart = useAction();
  return (
    <div className={styles.crash}>
      <ErrorNote
        message={restart.error ?? server.runtime.message ?? "The server stopped unexpectedly."}
        actionLabel="Restart"
        onAction={() => restart.run(() => actions.start(server.config.id))}
      />
      <div className={styles.crashLog}>
        <ConsoleLog lines={lines.slice(-CRASH_LINES)} />
      </div>
      <div>
        <button className={ui.linkBtn} onClick={() => hosting.openFolder(server.config.id).catch(() => {})}>
          Open logs folder
        </button>
      </div>
    </div>
  );
}

interface ServerPageProps {
  server: ServerSummary;
  actions: HostingActions;
  holdReason: string | null;
  /** The HOST tab is on screen (window-wide file drops go to the Plugins tab). */
  active: boolean;
  onSelect: (id: string) => void;
  onDeleted: () => void;
}

export default function ServerPage({ server, actions, holdReason, active, onSelect, onDeleted }: ServerPageProps) {
  const [tab, setTab] = useState<SubTab>(loadSubTab);
  /** A player picked with "Make admin" on the Overview tab, handed to the Admins tab once. */
  const [adminPrefill, setAdminPrefill] = useState<AdminPrefill | null>(null);
  const clearAdminPrefill = useCallback(() => setAdminPrefill(null), []);
  const log = useConsole(server.config.id);
  const id = server.config.id;

  const changeTab = (next: SubTab) => {
    setTab(next);
    writeLocal(SUB_TAB_KEY, next);
  };

  return (
    <>
      <ServerHeader server={server} actions={actions} holdReason={holdReason} />
      <div className={styles.subtabs} role="tablist">
        {SUB_TABS.map((t) => (
          <button
            key={t}
            role="tab"
            aria-selected={tab === t}
            className={cn(styles.subtab, tab === t && styles.subtabActive)}
            onClick={() => changeTab(t)}
          >
            {t}
          </button>
        ))}
      </div>

      {tab === "console" ? (
        <div className={styles.contentFill}>
          <ConsoleTab key={id} server={server} log={log} />
        </div>
      ) : (
        <div className={styles.content}>
          {server.runtime.state === "crashed" && tab === "overview" && (
            <CrashPanel server={server} lines={log.lines} actions={actions} />
          )}
          {tab === "overview" && (
            <OverviewTab
              key={id}
              server={server}
              actions={actions}
              onMakeAdmin={(player) => {
                setAdminPrefill({ steamId: player.steamId64, name: player.name });
                changeTab("admins");
              }}
            />
          )}
          {tab === "plugins" && <PluginsTab key={id} server={server} actions={actions} dropActive={active} />}
          {tab === "admins" && (
            <AdminsTab key={id} server={server} prefill={adminPrefill} onPrefillUsed={clearAdminPrefill} />
          )}
          {tab === "settings" && <SettingsTab key={id} server={server} actions={actions} />}
          {tab === "content" && <ContentTab key={id} server={server} actions={actions} />}
          {tab === "advanced" && (
            <AdvancedTab
              key={id}
              server={server}
              actions={actions}
              onDuplicated={(copyId) => {
                onSelect(copyId);
                changeTab("settings");
              }}
              onDeleted={onDeleted}
            />
          )}
        </div>
      )}
    </>
  );
}

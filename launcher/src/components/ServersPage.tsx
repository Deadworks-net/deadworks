import { useState, useCallback, type ReactNode } from "react";
import { Menu } from "@tauri-apps/api/menu";
import { invoke } from "@tauri-apps/api/core";
import { useServers } from "@/hooks/use-servers";
import ServerTable from "@/components/ServerTable";
import ConnectDialog from "@/components/ConnectDialog";
import { openServerInfoWindow } from "@/lib/tauri";
import type { Server } from "@/lib/types";
import { cn } from "@/lib/utils";
import styles from "./ServersPage.module.css";

interface ServersPageProps {
  apiUrl: string;
  /** Brand + top-level tabs, owned by App. */
  nav: ReactNode;
}

export default function ServersPage({ apiUrl, nav }: ServersPageProps) {
  const {
    pings,
    selectedServer,
    selectServer,
    searchQuery,
    setSearchQuery,
    filteredServers,
    refreshServers,
    isLoading,
    error,
    sortColumn,
    sortDirection,
    toggleSort,
  } = useServers(apiUrl);

  const [connectingServer, setConnectingServer] = useState<Server | null>(null);

  const handleConnect = useCallback(
    (server: Server) => {
      if (!server.online) return;
      setConnectingServer(server);
    },
    []
  );

  const handleDoubleClick = useCallback(
    (server: Server) => {
      handleConnect(server);
    },
    [handleConnect]
  );

  const handleContextMenu = useCallback(
    async (server: Server, e: React.MouseEvent) => {
      e.preventDefault();
      selectServer(server);

      const menu = await Menu.new({
        items: [
          {
            id: "ctx_info",
            text: "View Server Info",
            action: () => openServerInfoWindow(server, apiUrl),
          },
          {
            id: "ctx_connect",
            text: "Connect",
            enabled: server.online,
            action: () => handleConnect(server),
          },
          {
            id: "ctx_copy",
            text: "Copy IP to Clipboard",
            action: () => navigator.clipboard.writeText(server.address),
          },
        ],
      });
      await menu.popup();
    },
    [handleConnect, selectServer, apiUrl]
  );

  return (
    <div className={styles.page}>
      {/* Toolbar */}
      <div className={styles.toolbar}>
        {nav}
        <div className={styles.toolbarRight}>
          <div className={styles.searchBox}>
            <svg
              width="14"
              height="14"
              viewBox="0 0 24 24"
              fill="none"
              stroke="currentColor"
              strokeWidth="2"
              className={styles.searchIcon}
            >
              <circle cx="11" cy="11" r="8" />
              <line x1="21" y1="21" x2="16.65" y2="16.65" />
            </svg>
            <input
              type="text"
              value={searchQuery}
              onChange={(e) => setSearchQuery(e.target.value)}
              placeholder="Search servers..."
              className={styles.searchInput}
            />
          </div>
          <button onClick={refreshServers} title="Refresh" className={styles.refreshBtn}>
            {isLoading ? (
              <span className={styles.loadingDots}>
                <span className={styles.dot} />
                <span className={styles.dot} />
                <span className={styles.dot} />
              </span>
            ) : (
              <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2">
                <polyline points="23 4 23 10 17 10" />
                <path d="M20.49 15a9 9 0 1 1-2.12-9.36L23 10" />
              </svg>
            )}
          </button>
        </div>
      </div>

      {/* Card wrapping table + detail */}
      <div className={styles.card}>
        <ServerTable
          servers={filteredServers}
          pings={pings}
          selectedServerId={selectedServer?.id ?? null}
          onSelect={selectServer}
          onDoubleClick={handleDoubleClick}
          onContextMenu={handleContextMenu}
          isLoading={isLoading}
          error={error}
          sortColumn={sortColumn}
          sortDirection={sortDirection}
          onToggleSort={toggleSort}
        />

      </div>

      <div className={styles.connectBtnWrap}>
        <button
          className={cn(styles.launchBtn, styles.launchBtnSpacer)}
          onClick={() => invoke("launch_deadlock")}
        >
          Launch Deadlock
        </button>
        <button
          className={cn(styles.connectBtn, !selectedServer?.online && styles.connectBtnDisabled)}
          disabled={!selectedServer?.online}
          onClick={() => selectedServer && handleConnect(selectedServer)}
        >
          Connect
        </button>
      </div>

      {/* Connect dialog */}
      {connectingServer && (
        <ConnectDialog
          server={connectingServer}
          onClose={() => setConnectingServer(null)}
        />
      )}
    </div>
  );
}

import { useEffect, useState, useCallback } from "react";
import { getCurrentWebviewWindow } from "@tauri-apps/api/webviewWindow";
import { connectAnyway, fetchServers, prepareAndConnect, recordId } from "@/lib/tauri";
import type { Server } from "@/lib/types";
import styles from "./ServerInfoWindow.module.css";

export default function ServerInfoWindow() {
  const [server, setServer] = useState<Server | null>(null);
  const [apiUrl, setApiUrl] = useState("");
  const [joining, setJoining] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    const params = new URLSearchParams(window.location.search);
    const raw = params.get("server");
    if (raw) {
      try {
        setServer(JSON.parse(decodeURIComponent(raw)));
      } catch {}
    }
    const api = params.get("apiUrl");
    if (api) setApiUrl(api);
  }, []);

  const handleRefresh = useCallback(async () => {
    if (!server || !apiUrl) return;
    try {
      const servers = await fetchServers(apiUrl);
      const updated = servers.find((s) => s.id === server.id);
      if (updated) setServer(updated);
    } catch (e) {
      console.error("Failed to refresh server:", e);
    }
  }, [server, apiUrl]);

  // Same wording the backend uses; see ConnectDialog.
  const mismatch = error !== null && error.includes("does not match the version the server runs");

  const handleConnect = async () => {
    if (!server?.online || joining) return;
    // A second press after a failure is the player choosing to go regardless.
    const anyway = error !== null;
    const acceptMismatch = mismatch;
    setJoining(true);
    setError(null);
    try {
      const result = anyway
        ? await connectAnyway(recordId(server), server.raw_address, acceptMismatch)
        : await prepareAndConnect(recordId(server), server.raw_address);
      if (result.success) {
        getCurrentWebviewWindow().close();
      } else {
        setError(result.message);
      }
    } catch (e) {
      setError(String(e));
    } finally {
      setJoining(false);
    }
  };

  const handleClose = () => {
    getCurrentWebviewWindow().close();
  };

  if (!server) return null;

  const rows = [
    { label: "Address", value: server.address },
    { label: "Map", value: server.map || "\u2014" },
    { label: "Players", value: `${server.player_count} / ${server.max_players}` },
  ];

  return (
    <div className={styles.window}>
      <div className={styles.titlebar} data-tauri-drag-region>
        <button className={styles.closeBtn} onClick={handleClose}>
          <svg width="10" height="10" viewBox="0 0 10 10" fill="none" stroke="currentColor" strokeWidth="1.5">
            <line x1="1" y1="1" x2="9" y2="9" />
            <line x1="9" y1="1" x2="1" y2="9" />
          </svg>
        </button>
      </div>

      <div className={styles.content}>
        <div className={styles.header}>
          <h2 className={styles.name}>{server.name}</h2>
          <span className={styles.status}>
            <span className={server.online ? styles.dotOnline : styles.dotOffline} />
            {server.online ? "Online" : "Offline"}
          </span>
        </div>

        <div className={styles.body}>
          {rows.map((row, i) => (
            <div key={row.label}>
              <div className={styles.row}>
                <span className={styles.label}>{row.label}</span>
                <span className={styles.value}>{row.value}</span>
              </div>
              {i < rows.length - 1 && <div className={styles.divider} />}
            </div>
          ))}
        </div>

        {server.players.length > 0 && (
          <div className={styles.players}>
            <h4 className={styles.playersTitle}>Players ({server.players.length})</h4>
            <table className={styles.playerTable}>
              <thead>
                <tr>
                  <th>Name</th>
                  <th>Hero</th>
                  <th>K</th>
                  <th>D</th>
                  <th>A</th>
                </tr>
              </thead>
              <tbody>
                {server.players.map((p, i) => (
                  <tr key={i}>
                    <td>{p.name}</td>
                    <td>{p.hero}</td>
                    <td>{p.kills}</td>
                    <td>{p.deaths}</td>
                    <td>{p.assists}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}

        {error !== null && (
          <p className={styles.error}>
            {error}
            <span className={styles.errorHint}>
              {mismatch
                ? "Server content differs from the download host's content. Some custom content " +
                  "may be missing or broken. Consider letting the server operator know you " +
                  "received this warning."
                : "Joining anyway tries once more, then connects with whatever content is " +
                  "already installed, so some of it may be missing or out of date."}
            </span>
          </p>
        )}

        <div className={styles.footer}>
          <button
            className={styles.connectBtn}
            disabled={!server.online || joining}
            onClick={handleConnect}
          >
            {joining ? "CONNECTING..." : error !== null ? "CONNECT ANYWAY" : "CONNECT"}
          </button>
          <button className={styles.actionBtn} onClick={handleRefresh}>
            REFRESH
          </button>
          <button className={styles.actionBtn} onClick={handleClose}>
            CLOSE
          </button>
        </div>
      </div>
    </div>
  );
}

import { useState } from "react";
import type { NetworkInfo, PlayerInfo, ServerSummary } from "@/lib/hosting";
import { cn } from "@/lib/utils";
import type { HostingActions } from "../use-hosting";
import { formatBytes, formatDuration, NETWORK_LABELS } from "../format";
import { ConfirmDialog, useAction } from "../ui";
import ui from "../ui.module.css";
import styles from "./tabs.module.css";

function Meter({ label, value, unit }: { label: string; value: string; unit?: string }) {
  return (
    <div className={styles.meter}>
      <span className={styles.meterLabel}>{label}</span>
      <span className={styles.meterValue}>
        {value}
        {unit && <span className={styles.meterUnit}>{unit}</span>}
      </span>
    </div>
  );
}

function PlayerTable({
  players,
  running,
  onKick,
}: {
  players: PlayerInfo[];
  running: boolean;
  onKick: (p: PlayerInfo) => void;
}) {
  if (players.length === 0) {
    return (
      <div className={cn(styles.list, styles.listEmpty)}>
        {running ? "No players connected." : "The server isn't running."}
      </div>
    );
  }
  return (
    <div className={styles.list}>
      <table className={ui.table}>
        <thead>
          <tr>
            <th style={{ width: "36%" }}>Name</th>
            <th style={{ width: "14%" }}>Ping</th>
            <th style={{ width: "16%" }}>Time</th>
            <th>Hero</th>
            <th style={{ width: 70 }} aria-label="Actions" />
          </tr>
        </thead>
        <tbody>
          {players.map((p) => (
            <tr key={p.slot}>
              <td title={p.steamId64}>{p.name}</td>
              <td className={ui.num}>{p.pingMs} ms</td>
              <td className={ui.num}>{formatDuration(p.connectedSeconds)}</td>
              <td>{p.hero || "—"}</td>
              <td>
                <button className={cn(ui.btn, ui.btnSmall, ui.btnDanger)} onClick={() => onKick(p)}>
                  Kick
                </button>
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

const REACHABILITY: Record<NetworkInfo["reachability"], { text: string; tone: string }> = {
  unknown: { text: "Not checked yet", tone: "off" },
  checking: { text: "Checking...", tone: "busy" },
  open: { text: "Players can reach your server", tone: "ok" },
  closed: { text: "Players can't reach it yet. Follow the steps below, then check again.", tone: "bad" },
  error: { text: "The check didn't work. Try again in a minute.", tone: "warn" },
};

function NetworkCard({ server, actions, running }: { server: ServerSummary; actions: HostingActions; running: boolean }) {
  const net = server.runtime.network;
  const port = net.port || server.config.port;
  const check = useAction();
  const reach = REACHABILITY[net.reachability];
  const lanIps = net.lanIps.length > 0 ? net.lanIps : null;

  return (
    <div className={ui.card}>
      <div className={ui.cardTitle}>Network · {NETWORK_LABELS[server.config.network]}</div>

      {server.config.network === "sdr" && (
        <div className={styles.kv}>
          <span className={styles.kvKey}>Address</span>
          <span className={cn(styles.kvValue, ui.mono)}>
            {net.sdrId ?? <span className={ui.note}>Appears once the server is running</span>}
          </span>
          <span />
          <span className={ui.note}>Uses Steam's relay network. This address changes each time the server restarts.</span>
        </div>
      )}

      {server.config.network === "port_forward" && (
        <>
          <div className={styles.kv}>
            <span className={styles.kvKey}>Public address</span>
            <span className={cn(styles.kvValue, ui.mono)}>
              {net.publicIp ? `${net.publicIp}:${port}` : <span className={ui.note}>Shown once the server is running</span>}
            </span>
            <span className={styles.kvKey}>Status</span>
            <span className={cn(styles.kvValue, ui.row)}>
              <span className={cn(ui.dot, ui[`tone-${reach.tone}`])} aria-hidden />
              <span className={cn(ui.grow, ui[`textTone-${reach.tone}`])}>{reach.text}</span>
              <button
                className={cn(ui.btn, ui.btnSmall)}
                disabled={!running || check.busy || net.reachability === "checking"}
                title={running ? undefined : "Start the server first"}
                onClick={() => check.run(() => actions.checkReachability(server.config.id))}
              >
                Check
              </button>
            </span>
          </div>
          {check.error && <div className={ui.errorText}>{check.error}</div>}
          {net.reachability !== "open" && (
            <ol className={styles.steps}>
              <li>Open your router's settings page (often <code>192.168.1.1</code> or <code>192.168.0.1</code>).</li>
              <li>
                Forward <strong>UDP</strong> port <code>{port}</code> to this PC
                {lanIps ? (
                  <>
                    {" "}
                    ({lanIps.map((ip, i) => (
                      <span key={ip}>
                        {i > 0 && " or "}
                        <code>{ip}</code>
                      </span>
                    ))}
                    )
                  </>
                ) : null}
                . You don't need to forward TCP.
              </li>
              <li>Start the server, then press Check.</li>
            </ol>
          )}
        </>
      )}

      {server.config.network === "lan" && (
        <div className={styles.kv}>
          <span className={styles.kvKey}>Addresses</span>
          <span className={cn(styles.kvValue, ui.mono)}>
            {lanIps ? lanIps.map((ip) => <div key={ip}>{`${ip}:${port}`}</div>) : <span className={ui.note}>No network found</span>}
          </span>
          <span />
          <span className={ui.note}>Only people on the same Wi-Fi or router as this PC can join.</span>
        </div>
      )}
    </div>
  );
}

export default function OverviewTab({ server, actions }: { server: ServerSummary; actions: HostingActions }) {
  const { runtime, config } = server;
  const [kicking, setKicking] = useState<PlayerInfo | null>(null);
  const running = runtime.state === "running";
  const uptime = running && runtime.startedAt ? Date.now() / 1000 - runtime.startedAt : null;

  return (
    <div className={cn(styles.tab, styles.stack)}>
      <div className={styles.meters}>
        <Meter label="CPU" value={running ? `${Math.round(runtime.cpuPercent)}` : "—"} unit={running ? "%" : undefined} />
        <Meter label="Memory" value={running ? formatBytes(runtime.memoryBytes) : "—"} />
        <Meter
          label="Players"
          value={`${runtime.players.length}`}
          unit={`/ ${config.maxPlayers}${uptime != null ? ` · up ${formatDuration(uptime)}` : ""}`}
        />
      </div>

      <div>
        <div className={styles.subTitle} style={{ marginTop: 0 }}>Players</div>
        <PlayerTable players={runtime.players} running={running} onKick={setKicking} />
      </div>

      <NetworkCard server={server} actions={actions} running={running} />

      {kicking && (
        <ConfirmDialog
          title={`Kick ${kicking.name}?`}
          message="They're removed from the server but can join again."
          confirmLabel="Kick"
          danger
          onConfirm={() => actions.kick(config.id, kicking.slot)}
          onClose={() => setKicking(null)}
        />
      )}
    </div>
  );
}

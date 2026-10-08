import { useState } from "react";
import type { NetworkInfo, PlayerInfo, ServerSummary } from "@/lib/hosting";
import { cn } from "@/lib/utils";
import type { HostingActions } from "../use-hosting";
import { formatBytes, formatDuration, NETWORK_LABELS } from "../format";
import { ConfirmDialog, Modal, useAction } from "../ui";
import { parseSteamId } from "../permissions/permissions";
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

/** A person with a Steam account, so they can be banned or given a role. */
function hasSteamAccount(p: PlayerInfo): boolean {
  return !p.bot && parseSteamId(p.steamId64) !== null;
}

function PlayerTable({
  players,
  running,
  moderation,
  onKick,
  onBan,
  onMakeAdmin,
}: {
  players: PlayerInfo[];
  running: boolean;
  /** The server has Deadworks' ban command. */
  moderation: boolean;
  onKick: (p: PlayerInfo) => void;
  onBan: (p: PlayerInfo) => void;
  onMakeAdmin: (p: PlayerInfo) => void;
}) {
  if (players.length === 0) {
    return (
      <div className={cn(styles.list, styles.listEmpty)}>
        {running ? "No players." : "Server isn't running."}
      </div>
    );
  }
  return (
    <div className={styles.list} style={{ overflowX: "auto" }}>
      {/* Scrolls sideways in a narrow window instead of squeezing the name away. */}
      <table className={ui.table} style={{ minWidth: 620 }}>
        <thead>
          <tr>
            <th>Name</th>
            <th style={{ width: 68 }}>Ping</th>
            <th style={{ width: 76 }}>Time</th>
            <th style={{ width: "13%" }}>Hero</th>
            <th style={{ width: 236 }} aria-label="Actions" />
          </tr>
        </thead>
        <tbody>
          {players.map((p) => {
            const person = hasSteamAccount(p);
            return (
              <tr key={p.slot}>
                <td title={p.steamId64 || undefined}>
                  <div className={styles.playerName}>
                    <span className={styles.playerNameText}>{p.name}</span>
                    {(p.bot || p.roles.length > 0) && (
                      <span className={styles.playerTags} title={p.roles.join(", ") || undefined}>
                        {p.bot && <span className={styles.botTag}>bot</span>}
                        {p.roles.map((r) => (
                          <span key={r} className={styles.rolePill}>
                            {r}
                          </span>
                        ))}
                      </span>
                    )}
                  </div>
                </td>
                <td className={ui.num}>{p.pingMs} ms</td>
                <td className={ui.num}>{formatDuration(p.connectedSeconds)}</td>
                <td>{p.hero || "—"}</td>
                <td>
                  <div className={styles.playerActions}>
                    {person && p.roles.length === 0 && (
                      <button className={cn(ui.btn, ui.btnSmall)} onClick={() => onMakeAdmin(p)}>
                        Make admin
                      </button>
                    )}
                    <button className={cn(ui.btn, ui.btnSmall, ui.btnDanger)} onClick={() => onKick(p)}>
                      Kick
                    </button>
                    {person && (
                      <button
                        className={cn(ui.btn, ui.btnSmall, ui.btnDanger)}
                        disabled={!moderation}
                        title={moderation ? undefined : "Requires the Admin plugin."}
                        onClick={() => onBan(p)}
                      >
                        Ban
                      </button>
                    )}
                  </div>
                </td>
              </tr>
            );
          })}
        </tbody>
      </table>
    </div>
  );
}

const BAN_PRESETS: { label: string; minutes: number }[] = [
  { label: "1 hour", minutes: 60 },
  { label: "1 day", minutes: 60 * 24 },
  { label: "1 week", minutes: 60 * 24 * 7 },
  { label: "Permanent", minutes: 0 },
];

function BanDialog({
  player,
  onBan,
  onClose,
}: {
  player: PlayerInfo;
  /** `minutes` 0 = permanent. */
  onBan: (minutes: number, reason: string) => Promise<void>;
  onClose: () => void;
}) {
  const [choice, setChoice] = useState<number | "custom">(60 * 24);
  const [custom, setCustom] = useState("");
  const [reason, setReason] = useState("");
  const { run, busy, error } = useAction();

  const customMinutes = Number(custom);
  const customValid = custom.trim() !== "" && Number.isInteger(customMinutes) && customMinutes > 0;
  const minutes = choice === "custom" ? (customValid ? customMinutes : null) : choice;

  const ban = async () => {
    if (minutes == null) return;
    if (await run(() => onBan(minutes, reason.trim()))) onClose();
  };

  return (
    <Modal
      title={`Ban ${player.name}?`}
      onClose={busy ? () => {} : onClose}
      actions={
        <>
          <button type="button" className={ui.btn} onClick={onClose} disabled={busy}>
            Cancel
          </button>
          <button
            type="button"
            className={cn(ui.btn, ui.btnDanger)}
            onClick={ban}
            disabled={busy || minutes == null}
          >
            {busy ? "Working..." : "Ban"}
          </button>
        </>
      }
    >

      <div className={ui.field}>
        <div className={ui.label}>Duration</div>
        <div className={styles.presets} role="radiogroup" aria-label="Duration">
          {BAN_PRESETS.map((preset) => (
            <button
              key={preset.label}
              type="button"
              role="radio"
              aria-checked={choice === preset.minutes}
              className={cn(ui.btn, ui.btnSmall, choice === preset.minutes && styles.presetActive)}
              onClick={() => setChoice(preset.minutes)}
            >
              {preset.label}
            </button>
          ))}
          <button
            type="button"
            role="radio"
            aria-checked={choice === "custom"}
            className={cn(ui.btn, ui.btnSmall, choice === "custom" && styles.presetActive)}
            onClick={() => setChoice("custom")}
          >
            Custom
          </button>
        </div>
        {choice === "custom" && (
          <>
            <input
              className={cn(ui.input, ui.num, custom.trim() !== "" && !customValid && ui.inputInvalid)}
              style={{ marginTop: 8 }}
              type="number"
              min={1}
              step={1}
              value={custom}
              onChange={(e) => setCustom(e.target.value)}
              placeholder="Minutes"
              aria-label="Ban length in minutes"
              autoFocus
            />
            <div className={ui.hint}>
              {customValid ? formatDuration(customMinutes * 60) : "Enter whole minutes."}
            </div>
          </>
        )}
      </div>

      <div className={ui.field} style={{ marginBottom: 0 }}>
        <label className={ui.label} htmlFor="ban-reason">
          Reason (optional)
        </label>
        <input
          id="ban-reason"
          className={ui.input}
          value={reason}
          maxLength={120}
          onChange={(e) => setReason(e.target.value)}
        />
      </div>
      {error && <div className={ui.errorText}>{error}</div>}
    </Modal>
  );
}

const REACHABILITY: Record<NetworkInfo["reachability"], { text: string; tone: string }> = {
  unknown: { text: "Not checked", tone: "off" },
  checking: { text: "Checking...", tone: "busy" },
  open: { text: "Reachable", tone: "ok" },
  closed: { text: "Not reachable. Forward the port as described below.", tone: "bad" },
  error: { text: "Check failed. Try again shortly.", tone: "warn" },
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
            {net.sdrId ?? <span className={ui.note}>Available once running</span>}
          </span>
        </div>
      )}

      {server.config.network === "port_forward" && (
        <>
          <div className={styles.kv}>
            <span className={styles.kvKey}>Public address</span>
            <span className={cn(styles.kvValue, ui.mono)}>
              {net.publicIp ? `${net.publicIp}:${port}` : <span className={ui.note}>Available once running</span>}
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
              <li>Open the router's configuration page (usually <code>192.168.1.1</code> or <code>192.168.0.1</code>).</li>
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
                .
              </li>
              <li>Start the server and click Check.</li>
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
        </div>
      )}
    </div>
  );
}

interface OverviewTabProps {
  server: ServerSummary;
  actions: HostingActions;
  /** Opens the Permissions tab with the add dialog filled in for this player. */
  onMakeAdmin: (player: PlayerInfo) => void;
}

export default function OverviewTab({ server, actions, onMakeAdmin }: OverviewTabProps) {
  const { runtime, config } = server;
  const [kicking, setKicking] = useState<PlayerInfo | null>(null);
  const [banning, setBanning] = useState<PlayerInfo | null>(null);
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
        <PlayerTable
          players={runtime.players}
          running={running}
          moderation={runtime.moderation}
          onKick={setKicking}
          onBan={setBanning}
          onMakeAdmin={onMakeAdmin}
        />
      </div>

      <NetworkCard server={server} actions={actions} running={running} />

      {kicking && (
        <ConfirmDialog
          title={`Kick ${kicking.name}?`}
          confirmLabel="Kick"
          danger
          onConfirm={() => actions.kick(config.id, kicking.slot)}
          onClose={() => setKicking(null)}
        />
      )}

      {banning && (
        <BanDialog
          player={banning}
          onBan={(minutes, reason) => actions.ban(config.id, banning.steamId64, minutes, reason)}
          onClose={() => setBanning(null)}
        />
      )}
    </div>
  );
}

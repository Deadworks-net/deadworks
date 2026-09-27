import { useEffect, useMemo, useState } from "react";
import { hosting, type ServerSummary } from "@/lib/hosting";
import { cn } from "@/lib/utils";
import type { HostingActions } from "../use-hosting";
import { isLive, mapNameFromFile } from "../format";
import { NetworkCards, SwitchRow, useAction } from "../ui";
import { useConfigDraft } from "./use-config-draft";
import ui from "../ui.module.css";
import styles from "./tabs.module.css";

const FIELDS = [
  "name",
  "password",
  "maxPlayers",
  "map",
  "port",
  "network",
  "listed",
  "cheats",
  "hibernateWhenEmpty",
] as const;

let mapsCache: Promise<string[]> | null = null;
function loadMaps(): Promise<string[]> {
  if (!mapsCache) {
    mapsCache = hosting.maps().catch((e) => {
      mapsCache = null;
      throw e;
    });
  }
  return mapsCache;
}

export default function SettingsTab({ server, actions }: { server: ServerSummary; actions: HostingActions }) {
  const { draft, update, dirty, reset, merged } = useConfigDraft(server.config, FIELDS);
  const [showPassword, setShowPassword] = useState(false);
  const [maps, setMaps] = useState<string[]>([]);
  const [savedNote, setSavedNote] = useState(false);
  const save = useAction();
  const live = isLive(server.runtime.state);

  useEffect(() => {
    loadMaps()
      .then(setMaps)
      .catch(() => setMaps([]));
  }, []);

  const mapOptions = useMemo(() => {
    const all = [...maps, ...server.config.extraMaps.map(mapNameFromFile)];
    if (draft.map && !all.includes(draft.map)) all.unshift(draft.map);
    return Array.from(new Set(all));
  }, [maps, server.config.extraMaps, draft.map]);

  const nameError = !draft.name.trim() ? "Give your server a name." : null;
  const playersError =
    !Number.isInteger(draft.maxPlayers) || draft.maxPlayers < 1 || draft.maxPlayers > 31
      ? "Pick between 1 and 31 players."
      : null;
  const portError =
    !Number.isInteger(draft.port) || draft.port < 1024 || draft.port > 65535
      ? "Use a port between 1024 and 65535."
      : draft.port === 27015
        ? "27015 is Deadlock's own port and can break the game on this PC. Pick another."
        : null;
  const invalid = !!(nameError || playersError || portError);
  const canList = draft.network === "port_forward";

  const doSave = () =>
    save.run(async () => {
      const next = merged();
      await actions.updateServer({ ...next, name: next.name.trim(), listed: canList && next.listed });
      setSavedNote(true);
    });

  return (
    <div className={styles.tab}>
      <div className={ui.field}>
        <label className={ui.label} htmlFor="srv-name">Server name</label>
        <input
          id="srv-name"
          className={cn(ui.input, nameError && ui.inputInvalid)}
          value={draft.name}
          maxLength={64}
          onChange={(e) => update("name", e.target.value)}
        />
        {nameError && <div className={ui.errorText}>{nameError}</div>}
      </div>

      <div className={styles.formGrid}>
        <div className={ui.field}>
          <label className={ui.label} htmlFor="srv-pass">Password</label>
          <div className={ui.row}>
            <input
              id="srv-pass"
              className={ui.input}
              type={showPassword ? "text" : "password"}
              value={draft.password}
              autoComplete="off"
              placeholder="No password"
              onChange={(e) => update("password", e.target.value)}
            />
            <button className={cn(ui.btn, ui.btnSmall)} onClick={() => setShowPassword((s) => !s)}>
              {showPassword ? "Hide" : "Show"}
            </button>
          </div>
          <div className={ui.hint}>Leave empty to let anyone with the address join.</div>
        </div>

        <div className={ui.field}>
          <label className={ui.label} htmlFor="srv-map">Map</label>
          <select id="srv-map" className={ui.select} value={draft.map} onChange={(e) => update("map", e.target.value)}>
            {mapOptions.length === 0 && <option value="">Loading maps...</option>}
            {mapOptions.map((m) => (
              <option key={m} value={m}>
                {m}
              </option>
            ))}
          </select>
        </div>

        <div className={ui.field}>
          <label className={ui.label} htmlFor="srv-players">Max players</label>
          <div className={styles.sliderRow}>
            <input
              className={styles.slider}
              type="range"
              min={1}
              max={31}
              value={Math.min(31, Math.max(1, draft.maxPlayers || 1))}
              onChange={(e) => update("maxPlayers", Number(e.target.value))}
              aria-label="Max players slider"
            />
            <input
              id="srv-players"
              className={cn(ui.input, ui.num, playersError && ui.inputInvalid)}
              style={{ width: 64 }}
              type="number"
              min={1}
              max={31}
              value={Number.isNaN(draft.maxPlayers) ? "" : draft.maxPlayers}
              onChange={(e) => update("maxPlayers", e.target.valueAsNumber)}
            />
          </div>
          {playersError && <div className={ui.errorText}>{playersError}</div>}
        </div>

        <div className={ui.field}>
          <label className={ui.label} htmlFor="srv-port">Port</label>
          <input
            id="srv-port"
            className={cn(ui.input, ui.num, portError && ui.inputInvalid)}
            type="number"
            min={1024}
            max={65535}
            value={Number.isNaN(draft.port) ? "" : draft.port}
            onChange={(e) => update("port", e.target.valueAsNumber)}
          />
          {portError ? (
            <div className={ui.errorText}>{portError}</div>
          ) : (
            <div className={ui.hint}>
              {draft.network === "port_forward"
                ? "Forward this UDP port on your router."
                : "Usually fine as it is. Each server needs its own port."}
            </div>
          )}
        </div>
      </div>

      <div className={ui.label} style={{ marginTop: 4 }}>How players connect</div>
      <NetworkCards value={draft.network} onChange={(m) => update("network", m)} />

      <div style={{ marginTop: 8 }}>
        <SwitchRow
          title="Show in Deadworks server browser"
          description={
            canList
              ? "Anyone can find and join from the SERVERS tab. Leave off to share the address yourself."
              : "Only port-forwarded servers can be listed. The browser can't connect to SDR or LAN servers."
          }
          checked={canList && draft.listed}
          disabled={!canList}
          onChange={(v) => update("listed", v)}
        />
        <SwitchRow
          title="Cheats"
          description="Allows cheat commands (sv_cheats) for everyone on the server."
          checked={draft.cheats}
          onChange={(v) => update("cheats", v)}
        />
        <SwitchRow
          title="Sleep when empty"
          description="Uses almost no CPU while nobody is connected."
          checked={draft.hibernateWhenEmpty}
          onChange={(v) => update("hibernateWhenEmpty", v)}
        />
      </div>

      <div className={styles.saveBar}>
        <span className={styles.saveBarNote}>
          {save.error ? (
            <span className={ui.errorText}>{save.error}</span>
          ) : dirty ? (
            live ? "Takes effect after a restart." : "You have unsaved changes."
          ) : savedNote ? (
            <span className={ui.noteOk}>{live ? "Saved. Restart the server to apply." : "Saved."}</span>
          ) : null}
        </span>
        <button className={ui.btn} disabled={!dirty || save.busy} onClick={reset}>
          Undo changes
        </button>
        <button className={ui.btnPrimary} disabled={!dirty || invalid || save.busy} onClick={doSave}>
          {save.busy ? "Saving..." : "Save"}
        </button>
      </div>
    </div>
  );
}

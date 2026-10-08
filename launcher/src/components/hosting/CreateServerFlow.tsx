import { useState, type FormEvent } from "react";
import type { NetworkMode, ServerSummary } from "@/lib/hosting";
import { cn } from "@/lib/utils";
import type { HostingActions } from "./use-hosting";
import { ErrorNote, NetworkCards, useAction } from "./ui";
import PluginsTab from "./tabs/PluginsTab";
import ui from "./ui.module.css";
import styles from "./HostPage.module.css";

interface CreateServerFlowProps {
  servers: ServerSummary[];
  actions: HostingActions;
  /** The HOST tab is on screen (for plugin file drops). */
  active: boolean;
  /** Null when this is the first server and there's nothing to go back to. */
  onCancel: (() => void) | null;
  onDone: (id: string) => void;
}

function defaultName(count: number): string {
  return count === 0 ? "My Deadworks Server" : `My Deadworks Server ${count + 1}`;
}

export default function CreateServerFlow({ servers, actions, active, onCancel, onDone }: CreateServerFlowProps) {
  const [name, setName] = useState(() => defaultName(servers.length));
  const [network, setNetwork] = useState<NetworkMode>("sdr");
  const [createdId, setCreatedId] = useState<string | null>(null);
  const create = useAction();

  const created = createdId ? servers.find((s) => s.config.id === createdId) : undefined;
  const start = useAction();

  if (createdId) {
    const startNow = () =>
      start.run(async () => {
        await actions.start(createdId);
        onDone(createdId);
      });
    return (
      <div className={styles.scroll}>
        <div className={styles.form} style={{ maxWidth: 720, paddingBottom: 0 }}>
          <h2 className={styles.formTitle}>Add plugins</h2>
          <p className={styles.formSubtitle}>
            Optional. Plugins can be changed later on the Plugins tab.
          </p>
        </div>
        {created ? (
          <PluginsTab server={created} actions={actions} dropActive={active} />
        ) : (
          <div className={styles.form}>
            <ErrorNote message="Server created, but it hasn't loaded yet." actionLabel="Reload" onAction={actions.reload} />
          </div>
        )}
        <div className={styles.form} style={{ maxWidth: 720, paddingTop: 0 }}>
          {start.error && (
            <div className={styles.section}>
              <ErrorNote message={start.error} actionLabel="Try again" onAction={startNow} />
            </div>
          )}
          <div className={styles.formActions}>
            <button className={cn(ui.linkBtn, styles.formActionsLeft)} onClick={() => onDone(createdId)}>
              Start later
            </button>
            <button className={cn(ui.btnPrimary, ui.btnLarge)} disabled={start.busy} onClick={startNow}>
              {start.busy ? "Starting..." : "Start server"}
            </button>
          </div>
          <div className={ui.hint} style={{ textAlign: "right" }}>
            The first start opens a Windows Firewall prompt.
          </div>
        </div>
      </div>
    );
  }

  const trimmed = name.trim();
  const doCreate = () =>
    create.run(async () => {
      const config = await actions.createServer(trimmed, network);
      setCreatedId(config.id);
    });
  const submit = (e: FormEvent) => {
    e.preventDefault();
    if (trimmed) doCreate();
  };

  return (
    <div className={styles.scroll}>
      <form className={styles.form} onSubmit={submit}>
        <h2 className={styles.formTitle}>{servers.length === 0 ? "Create your first server" : "New server"}</h2>
        <p className={styles.formSubtitle}>Both can be changed later.</p>

        <div className={ui.field}>
          <label className={ui.label} htmlFor="new-name">Server name</label>
          <input
            id="new-name"
            className={ui.input}
            value={name}
            maxLength={64}
            autoFocus
            onChange={(e) => setName(e.target.value)}
          />
        </div>

        <div className={styles.section}>
          <div className={ui.label}>Network</div>
          <NetworkCards value={network} onChange={setNetwork} />
        </div>

        {create.error && (
          <div className={styles.section}>
            <ErrorNote message={create.error} actionLabel="Try again" onAction={doCreate} />
          </div>
        )}

        <div className={styles.formActions}>
          {onCancel && (
            <button type="button" className={cn(ui.linkBtn, styles.formActionsLeft)} onClick={onCancel}>
              Cancel
            </button>
          )}
          {!trimmed && <span className={styles.disabledReason}>Enter a name.</span>}
          <button type="submit" className={cn(ui.btnPrimary, ui.btnLarge)} disabled={!trimmed || create.busy}>
            {create.busy ? "Creating..." : "Create server"}
          </button>
        </div>
      </form>
    </div>
  );
}

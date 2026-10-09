import { useState, type FormEvent } from "react";
import type { NetworkMode, ServerSummary } from "@/lib/hosting";
import { cn } from "@/lib/utils";
import type { HostingActions } from "./use-hosting";
import { ErrorNote, NetworkCards, useAction } from "./ui";
import ui from "./ui.module.css";
import styles from "./HostPage.module.css";

interface CreateServerFlowProps {
  servers: ServerSummary[];
  actions: HostingActions;
  /** Null when this is the first server and there's nothing to go back to. */
  onCancel: (() => void) | null;
  onDone: (id: string) => void;
}

function defaultName(count: number): string {
  return count === 0 ? "My Deadworks Server" : `My Deadworks Server ${count + 1}`;
}

export default function CreateServerFlow({ servers, actions, onCancel, onDone }: CreateServerFlowProps) {
  const [name, setName] = useState(() => defaultName(servers.length));
  const [network, setNetwork] = useState<NetworkMode>("sdr");
  const create = useAction();

  const trimmed = name.trim();
  const doCreate = () =>
    create.run(async () => {
      const config = await actions.createServer(trimmed, network);
      onDone(config.id);
    });
  const submit = (e: FormEvent) => {
    e.preventDefault();
    if (trimmed) doCreate();
  };

  return (
    <div className={styles.scroll}>
      <form className={styles.form} onSubmit={submit}>
        <h2 className={styles.formTitle}>{servers.length === 0 ? "Create your first server" : "New server"}</h2>

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

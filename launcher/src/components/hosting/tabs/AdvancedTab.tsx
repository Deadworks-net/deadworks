import { useState } from "react";
import { hosting, type ServerSummary } from "@/lib/hosting";
import { cn } from "@/lib/utils";
import type { HostingActions } from "../use-hosting";
import { isLive } from "../format";
import { ConfirmDialog, useAction } from "../ui";
import { useConfigDraft } from "./use-config-draft";
import ui from "../ui.module.css";
import styles from "./tabs.module.css";

const FIELDS = ["cvars", "launchArgs"] as const;

const hasSpace = (s: string) => /\s/.test(s);

function RemoveButton({ label, onClick }: { label: string; onClick: () => void }) {
  return (
    <button className={styles.iconBtn} onClick={onClick} aria-label={label} title={label}>
      <svg width="10" height="10" viewBox="0 0 10 10" aria-hidden>
        <line x1="1" y1="1" x2="9" y2="9" stroke="currentColor" strokeWidth="1.3" />
        <line x1="9" y1="1" x2="1" y2="9" stroke="currentColor" strokeWidth="1.3" />
      </svg>
    </button>
  );
}

interface AdvancedTabProps {
  server: ServerSummary;
  actions: HostingActions;
  onDuplicated: (id: string) => void;
  onDeleted: () => void;
}

export default function AdvancedTab({ server, actions, onDuplicated, onDeleted }: AdvancedTabProps) {
  const { id } = server.config;
  const { draft, update, dirty, reset, merged } = useConfigDraft(server.config, FIELDS);
  const [confirmDelete, setConfirmDelete] = useState(false);
  const save = useAction();
  const misc = useAction();
  const live = isLive(server.runtime.state);
  const stopped = server.runtime.state === "stopped" || server.runtime.state === "crashed";

  const cvarErrors = draft.cvars.map((c) =>
    !c.key.trim() ? "Enter a name." : hasSpace(c.key.trim()) ? "Names can't contain spaces." : null
  );
  const argErrors = draft.launchArgs.map((a) =>
    !a.trim() ? "Enter a parameter or remove this row." : hasSpace(a.trim()) ? "One parameter per row, no spaces." : null
  );
  const invalid = cvarErrors.some(Boolean) || argErrors.some(Boolean);

  const doSave = () =>
    save.run(() => {
      const next = merged();
      return actions.updateServer({
        ...next,
        cvars: next.cvars.map((c) => ({ key: c.key.trim(), value: c.value })),
        launchArgs: next.launchArgs.map((a) => a.trim()),
      });
    });

  return (
    <div className={styles.tab}>
      <div className={styles.header} style={{ marginBottom: 6 }}>
        <span className={styles.headerTitle}>ConVars</span>
        <button
          className={cn(ui.btn, ui.btnSmall)}
          onClick={() => update("cvars", [...draft.cvars, { key: "", value: "" }])}
        >
          Add
        </button>
      </div>
      <div className={styles.editTable}>
        {draft.cvars.length === 0 && <div className={ui.note}>None.</div>}
        {draft.cvars.map((c, i) => (
          <div key={i}>
            <div className={styles.editRow}>
              <input
                className={cn(ui.input, ui.mono, cvarErrors[i] && ui.inputInvalid)}
                placeholder="name"
                value={c.key}
                spellCheck={false}
                aria-label={`ConVar ${i + 1} name`}
                onChange={(e) =>
                  update("cvars", draft.cvars.map((x, j) => (j === i ? { ...x, key: e.target.value } : x)))
                }
              />
              <input
                className={cn(ui.input, ui.mono)}
                placeholder="value"
                value={c.value}
                spellCheck={false}
                aria-label={`ConVar ${i + 1} value`}
                onChange={(e) =>
                  update("cvars", draft.cvars.map((x, j) => (j === i ? { ...x, value: e.target.value } : x)))
                }
              />
              <RemoveButton
                label="Remove ConVar"
                onClick={() => update("cvars", draft.cvars.filter((_, j) => j !== i))}
              />
            </div>
            {cvarErrors[i] && <div className={ui.errorText}>{cvarErrors[i]}</div>}
          </div>
        ))}
      </div>

      <div className={styles.header} style={{ marginTop: 20, marginBottom: 6 }}>
        <span className={styles.headerTitle}>Launch parameters</span>
        <button className={cn(ui.btn, ui.btnSmall)} onClick={() => update("launchArgs", [...draft.launchArgs, ""])}>
          Add
        </button>
      </div>
      <div className={ui.hint} style={{ marginTop: 0, marginBottom: 8 }}>
        One per row, e.g. <code className={styles.inlineCode}>-dev</code>.
      </div>
      <div className={styles.editTable}>
        {draft.launchArgs.length === 0 && <div className={ui.note}>None.</div>}
        {draft.launchArgs.map((a, i) => (
          <div key={i}>
            <div className={styles.editRow}>
              <input
                className={cn(ui.input, ui.mono, argErrors[i] && ui.inputInvalid)}
                value={a}
                spellCheck={false}
                aria-label={`Launch parameter ${i + 1}`}
                onChange={(e) => update("launchArgs", draft.launchArgs.map((x, j) => (j === i ? e.target.value : x)))}
              />
              <RemoveButton
                label="Remove parameter"
                onClick={() => update("launchArgs", draft.launchArgs.filter((_, j) => j !== i))}
              />
            </div>
            {argErrors[i] && <div className={ui.errorText}>{argErrors[i]}</div>}
          </div>
        ))}
      </div>

      <div className={styles.subTitle} style={{ marginTop: 26 }}>Manage</div>
      <div className={styles.stack}>
        <div className={ui.rowSpread}>
          <div className={ui.switchTitle}>Server folder</div>
          <button className={ui.btn} onClick={() => misc.run(() => hosting.openFolder(id))}>
            Open folder
          </button>
        </div>
        <div className={ui.rowSpread}>
          <div className={ui.switchTitle}>Duplicate</div>
          <button
            className={ui.btn}
            disabled={misc.busy}
            onClick={() =>
              misc.run(async () => {
                const copy = await actions.duplicateServer(id);
                onDuplicated(copy.id);
              })
            }
          >
            Duplicate
          </button>
        </div>
        <div className={ui.rowSpread}>
          <div>
            <div className={ui.switchTitle}>Delete server</div>
            {!stopped && <div className={ui.switchDesc}>Only works while the server is stopped.</div>}
          </div>
          <button
            className={cn(ui.btn, ui.btnDanger)}
            disabled={!stopped}
            onClick={() => setConfirmDelete(true)}
          >
            Delete
          </button>
        </div>
        {misc.error && <div className={ui.errorText}>{misc.error}</div>}
      </div>

      {(dirty || save.error) && (
        <div className={styles.saveBar}>
          <span className={styles.saveBarNote}>
            {save.error ? (
              <span className={ui.errorText}>{save.error}</span>
            ) : (
              live && "Takes effect on restart."
            )}
          </span>
          <button className={ui.btn} disabled={save.busy} onClick={reset}>
            Discard
          </button>
          <button className={ui.btnPrimary} disabled={invalid || save.busy} onClick={doSave}>
            {save.busy ? "Saving..." : "Save"}
          </button>
        </div>
      )}

      {confirmDelete && (
        <ConfirmDialog
          title={`Delete ${server.config.name}?`}
          message="Deletes the server's settings, logs and content. Cannot be undone."
          confirmLabel="Delete"
          danger
          onConfirm={async () => {
            await actions.deleteServer(id);
            onDeleted();
          }}
          onClose={() => setConfirmDelete(false)}
        />
      )}
    </div>
  );
}

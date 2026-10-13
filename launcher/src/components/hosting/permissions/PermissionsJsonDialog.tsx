import { useMemo, useState } from "react";
import { cn } from "@/lib/utils";
import { errorMessage } from "../format";
import JsoncEditor, { jsoncProblems } from "../JsoncEditor";
import { ConfirmDialog, Modal } from "../ui";
import {
  GENERATED_DIR,
  OVERRIDES_PATH,
  PLAYERS_PATH,
  parsePermissionFile,
  renderOverrides,
  renderPlayers,
} from "./permissions";
import type { ActResult, AdminsSnapshot, PermissionsSource } from "./source";
import ui from "../ui.module.css";

interface PermissionsJsonDialogProps {
  /** One of the permission files, as keyed in the snapshot. */
  path: string;
  snapshot: AdminsSnapshot;
  source: PermissionsSource;
  onClose: () => void;
  onSaved: (result: ActResult) => void;
}

/**
 * One permission file in the JSONC editor. The plugin listings in generated/ open read-only, since
 * Deadworks rewrites them every time the plugin loads.
 */
export default function PermissionsJsonDialog({ path, snapshot, source, onClose, onSaved }: PermissionsJsonDialogProps) {
  const name = path.slice(path.lastIndexOf("/") + 1);
  const readOnly = path.startsWith(`${GENERATED_DIR}/`);
  const empty = path === PLAYERS_PATH ? renderPlayers({}) : path === OVERRIDES_PATH ? renderOverrides({}) : "";

  // Read once: the snapshot can refresh underneath an open editor.
  const [opened] = useState(() => ({
    text: snapshot.files[path] ?? empty,
    base: snapshot.missing.includes(path) ? null : (snapshot.files[path] ?? null),
  }));
  const [original, setOriginal] = useState(opened.text);
  const [text, setText] = useState(opened.text);
  /** What the file held when this text was loaded; a save is refused if the file has moved on. */
  const [base, setBase] = useState<string | null>(opened.base);
  const [editorKey, setEditorKey] = useState(0);
  const [conflict, setConflict] = useState<{ contents: string | null } | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);
  const [confirmDiscard, setConfirmDiscard] = useState(false);

  const marks = useMemo(() => jsoncProblems(text), [text]);
  const invalid = useMemo(() => parsePermissionFile(text).error, [text]);
  const dirty = text !== original;
  const blocked = marks.length > 0 || invalid != null;

  const close = () => (dirty ? setConfirmDiscard(true) : onClose());

  const save = async () => {
    if (saving || readOnly || blocked) return;
    setSaving(true);
    setError(null);
    try {
      const result = await source.act({ action: "file-save", path, contents: text, base });
      if (result.conflict) {
        setConflict({ contents: result.contents ?? null });
        return;
      }
      onSaved(result);
    } catch (e) {
      setError(errorMessage(e));
    } finally {
      setSaving(false);
    }
  };

  const loadDiskCopy = () => {
    if (!conflict) return;
    const now = conflict.contents ?? empty;
    setOriginal(now);
    setText(now);
    setBase(conflict.contents);
    setEditorKey((k) => k + 1);
    setConflict(null);
  };

  const keepMine = () => {
    if (!conflict) return;
    setBase(conflict.contents);
    setConflict(null);
  };

  return (
    <>
      <Modal
        wide
        title={name}
        onClose={saving ? () => {} : close}
        actions={
          <>
            <span style={{ marginRight: "auto" }}>
              {error ? (
                <span className={ui.errorText}>{error}</span>
              ) : readOnly ? null : marks.length > 0 ? (
                <span className={ui.errorText}>
                  Fix the {marks.length === 1 ? "highlighted error" : `${marks.length} highlighted errors`} to save.
                </span>
              ) : invalid ? (
                <span className={ui.errorText}>Invalid JSON: {invalid}</span>
              ) : (
                dirty && <span className={ui.note}>Unsaved changes</span>
              )}
            </span>
            <button type="button" className={ui.btn} onClick={close} disabled={saving}>
              Close
            </button>
            {!readOnly && (
              <button
                type="button"
                className={ui.btnPrimary}
                onClick={save}
                disabled={!dirty || blocked || saving || conflict != null}
              >
                {saving ? "Saving..." : "Save"}
              </button>
            )}
          </>
        }
      >
        <div style={{ height: "100%", display: "flex", flexDirection: "column", gap: 8 }}>
          <div className={ui.hint} style={{ marginTop: 0 }}>
            <span className={ui.mono}>{path}</span>
            {readOnly && " · Read-only"}
          </div>
          {conflict && (
            <div className={ui.warnBox} role="alert">
              <span className={ui.errorBoxText}>{name} changed on disk.</span>
              <button type="button" className={cn(ui.btn, ui.btnSmall)} onClick={loadDiskCopy}>
                Reload from disk
              </button>
              <button type="button" className={cn(ui.btn, ui.btnSmall)} onClick={keepMine}>
                Keep mine
              </button>
            </div>
          )}
          <div style={{ flex: 1, minHeight: 0 }}>
            <JsoncEditor
              key={editorKey}
              initialValue={original}
              readOnly={readOnly}
              onChange={(next) => {
                setText(next);
                setError(null);
              }}
            />
          </div>
        </div>
      </Modal>

      {confirmDiscard && (
        <ConfirmDialog
          title="Discard changes?"
          confirmLabel="Discard"
          danger
          onConfirm={async () => onClose()}
          onClose={() => setConfirmDiscard(false)}
        />
      )}
    </>
  );
}

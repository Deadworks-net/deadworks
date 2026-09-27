import { useEffect, useMemo, useState } from "react";
import { hosting, type PluginConfigFile } from "@/lib/hosting";
import { cn } from "@/lib/utils";
import { errorMessage } from "./format";
import JsoncEditor, { jsoncProblems } from "./JsoncEditor";
import { ConfirmDialog, ErrorNote, Loading, Modal, useAction } from "./ui";
import ui from "./ui.module.css";

interface PluginConfigEditorProps {
  serverId: string;
  file: PluginConfigFile;
  running: boolean;
  onClose: () => void;
  /** The file was saved or reset. */
  onChanged: () => void;
}

export default function PluginConfigEditor({ serverId, file, running, onClose, onChanged }: PluginConfigEditorProps) {
  const [original, setOriginal] = useState<string | null>(null);
  const [text, setText] = useState("");
  const [loadError, setLoadError] = useState<string | null>(null);
  const [loadKey, setLoadKey] = useState(0);
  const [savedNote, setSavedNote] = useState<string | null>(null);
  const [confirmReset, setConfirmReset] = useState(false);
  const [confirmDiscard, setConfirmDiscard] = useState(false);
  const save = useAction();

  useEffect(() => {
    if (!file.exists) return;
    let cancelled = false;
    setOriginal(null);
    setLoadError(null);
    hosting
      .readPluginConfig(serverId, file.pluginId)
      .then((contents) => {
        if (cancelled) return;
        setOriginal(contents);
        setText(contents);
      })
      .catch((e) => {
        if (!cancelled) setLoadError(errorMessage(e));
      });
    return () => {
      cancelled = true;
    };
  }, [serverId, file.pluginId, file.exists, loadKey]);

  const problems = useMemo(() => jsoncProblems(text), [text]);
  const dirty = original != null && text !== original;
  const title = `${file.pluginId} settings`;

  const close = () => (dirty ? setConfirmDiscard(true) : onClose());

  if (!file.exists) {
    return (
      <Modal
        title={title}
        onClose={onClose}
        actions={<button className={ui.btn} onClick={onClose}>Close</button>}
      >
        {running
          ? "This plugin doesn't have any settings."
          : "Start the server once to create this plugin's settings."}
      </Modal>
    );
  }

  const doSave = () =>
    save.run(async () => {
      await hosting.writePluginConfig(serverId, file.pluginId, text);
      setOriginal(text);
      setSavedNote(running ? "Saved and applied live." : "Saved. It applies when the server starts.");
      onChanged();
    });

  return (
    <>
      <Modal
        wide
        title={title}
        onClose={close}
        actions={
          <>
            <button
              className={cn(ui.btn, ui.btnDanger)}
              style={{ marginRight: "auto" }}
              onClick={() => setConfirmReset(true)}
              disabled={save.busy}
            >
              Reset to defaults
            </button>
            {save.error ? (
              <span className={ui.errorText}>{save.error}</span>
            ) : problems.length > 0 ? (
              <span className={ui.errorText}>
                Fix the {problems.length === 1 ? "highlighted problem" : `${problems.length} highlighted problems`} before
                saving.
              </span>
            ) : (
              savedNote && !dirty && <span className={ui.noteOk}>{savedNote}</span>
            )}
            <button className={ui.btn} onClick={close}>
              Close
            </button>
            <button
              className={ui.btnPrimary}
              onClick={doSave}
              disabled={!dirty || problems.length > 0 || save.busy}
            >
              {save.busy ? "Saving..." : "Save"}
            </button>
          </>
        }
      >
        <div style={{ height: "100%", display: "flex", flexDirection: "column", gap: 8 }}>
          <div className={ui.hint} style={{ marginTop: 0 }}>
            <span className={ui.mono}>{file.path}</span>
          </div>
          <div style={{ flex: 1, minHeight: 0 }}>
            {loadError ? (
              <ErrorNote
                message={`We couldn't open this file: ${loadError}`}
                actionLabel="Try again"
                onAction={() => setLoadKey((k) => k + 1)}
              />
            ) : original == null ? (
              <Loading />
            ) : (
              <JsoncEditor
                key={`${file.pluginId}:${loadKey}`}
                initialValue={original}
                onChange={(next) => {
                  setText(next);
                  setSavedNote(null);
                }}
              />
            )}
          </div>
        </div>
      </Modal>

      {confirmReset && (
        <ConfirmDialog
          title="Reset to defaults?"
          message={`This deletes your changes to ${file.pluginId}'s settings. The plugin writes fresh defaults the next time it loads.`}
          confirmLabel="Reset"
          danger
          onConfirm={async () => {
            await hosting.resetPluginConfig(serverId, file.pluginId);
            onChanged();
            onClose();
          }}
          onClose={() => setConfirmReset(false)}
        />
      )}

      {confirmDiscard && (
        <ConfirmDialog
          title="Discard changes?"
          message="You have unsaved changes to these settings."
          confirmLabel="Discard"
          danger
          onConfirm={async () => onClose()}
          onClose={() => setConfirmDiscard(false)}
        />
      )}
    </>
  );
}

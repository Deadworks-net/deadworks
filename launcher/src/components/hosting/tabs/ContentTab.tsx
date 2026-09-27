import { useCallback, useEffect, useState } from "react";
import { open } from "@tauri-apps/plugin-dialog";
import { hosting, type ContentFile, type ServerSummary } from "@/lib/hosting";
import { cn } from "@/lib/utils";
import type { HostingActions } from "../use-hosting";
import { errorMessage, formatBytes, isLive, mapNameFromFile } from "../format";
import { ConfirmDialog, ErrorNote, Loading, Toggle, useAction } from "../ui";
import ui from "../ui.module.css";
import styles from "./tabs.module.css";

type Kind = ContentFile["kind"];

const SECTIONS: { kind: Kind; title: string; empty: string; field: "contentAddons" | "extraMaps" }[] = [
  {
    kind: "addon",
    title: "Addons",
    empty: "No addons yet. Addons are .vpk files that change models, sounds or UI.",
    field: "contentAddons",
  },
  {
    kind: "map",
    title: "Maps",
    empty: "No extra maps yet. Add a map's .vpk file to play on it.",
    field: "extraMaps",
  },
];

export default function ContentTab({ server, actions }: { server: ServerSummary; actions: HostingActions }) {
  const { id } = server.config;
  const [files, setFiles] = useState<ContentFile[] | null>(null);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [removing, setRemoving] = useState<ContentFile | null>(null);
  const change = useAction();

  const load = useCallback(async () => {
    try {
      setFiles(await hosting.content(id));
      setLoadError(null);
    } catch (e) {
      setLoadError(errorMessage(e));
    }
  }, [id]);

  useEffect(() => {
    load();
  }, [load]);

  const importFiles = async (kind: Kind) => {
    const picked = await open({
      multiple: true,
      title: kind === "map" ? "Choose map files" : "Choose addon files",
      filters: [{ name: "VPK files", extensions: ["vpk"] }],
    });
    if (!picked || picked.length === 0) return;
    change.run(async () => {
      const added = await hosting.importContent(id, picked, kind);
      await load();
      // New content is switched on straight away; that's why it was imported.
      const field = kind === "map" ? "extraMaps" : "contentAddons";
      const current = server.config[field];
      const next = [...current, ...added.map((f) => f.fileName).filter((n) => !current.includes(n))];
      if (next.length !== current.length) await actions.updateServer({ ...server.config, [field]: next });
    });
  };

  const setEnabled = (file: ContentFile, enabled: boolean) => {
    const field = file.kind === "map" ? "extraMaps" : "contentAddons";
    const current = server.config[field];
    const next = enabled ? [...current, file.fileName] : current.filter((n) => n !== file.fileName);
    change.run(() => actions.updateServer({ ...server.config, [field]: next }));
  };

  if (loadError && !files) {
    return (
      <div className={styles.tab}>
        <ErrorNote message={`We couldn't list this server's content: ${loadError}`} actionLabel="Try again" onAction={load} />
      </div>
    );
  }
  if (!files) return <Loading />;

  return (
    <div className={styles.tab}>
      {change.error && (
        <div style={{ marginBottom: 12 }}>
          <ErrorNote message={change.error} actionLabel="OK" onAction={change.clearError} />
        </div>
      )}

      {SECTIONS.map((section, i) => {
        const list = files.filter((f) => f.kind === section.kind);
        return (
          <div key={section.kind} style={{ marginTop: i === 0 ? 0 : 20 }}>
            <div className={styles.header}>
              <span className={styles.headerTitle}>{section.title}</span>
              <button className={ui.btn} disabled={change.busy} onClick={() => importFiles(section.kind)}>
                Add {section.kind === "map" ? "maps" : "addons"}
              </button>
            </div>
            {list.length === 0 ? (
              <div className={cn(styles.list, styles.listEmpty)}>{section.empty}</div>
            ) : (
              <div className={styles.list}>
                {list.map((f) => (
                  <div key={f.fileName} className={styles.listRow}>
                    <Toggle
                      checked={server.config[section.field].includes(f.fileName)}
                      label={`Use ${f.fileName} on this server`}
                      disabled={change.busy}
                      onChange={(next) => setEnabled(f, next)}
                    />
                    <div className={styles.listMain}>
                      <div className={styles.listName}>
                        {section.kind === "map" ? mapNameFromFile(f.fileName) : f.fileName}
                      </div>
                      <div className={styles.listMeta}>{formatBytes(f.sizeBytes)}</div>
                    </div>
                    <button className={cn(ui.btn, ui.btnSmall, ui.btnDanger)} onClick={() => setRemoving(f)}>
                      Remove
                    </button>
                  </div>
                ))}
              </div>
            )}
          </div>
        );
      })}

      <div className={ui.hint} style={{ marginTop: 14 }}>
        {isLive(server.runtime.state)
          ? "Content changes take effect after a restart."
          : "Enabled maps show up in the map list under Settings."}
      </div>

      {removing && (
        <ConfirmDialog
          title={`Remove ${removing.fileName}?`}
          message="The file is deleted from this server's content folder."
          confirmLabel="Remove"
          danger
          onConfirm={async () => {
            await hosting.removeContent(id, removing.fileName);
            await load();
            await actions.reload();
          }}
          onClose={() => setRemoving(null)}
        />
      )}
    </div>
  );
}

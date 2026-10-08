import { Suspense, lazy, useCallback, useEffect, useRef, useState } from "react";
import { open } from "@tauri-apps/plugin-dialog";
import { getCurrentWebview } from "@tauri-apps/api/webview";
import { hosting, onHostingChanged, type PluginConfigFile, type PluginEntry, type ServerSummary } from "@/lib/hosting";
import { cn } from "@/lib/utils";
import type { HostingActions } from "../use-hosting";
import { errorMessage, formatBytes, isLive } from "../format";
import { ConfirmDialog, ErrorNote, Loading, Toggle, useAction } from "../ui";
import ui from "../ui.module.css";
import styles from "./tabs.module.css";

// CodeMirror is most of the bundle; load it only when someone opens a config.
const PluginConfigEditor = lazy(() => import("../PluginConfigEditor"));

interface PluginsTabProps {
  server: ServerSummary;
  actions: HostingActions;
  /** Accept files dropped anywhere on the window (only while this is on screen). */
  dropActive: boolean;
}

/** Files dropped or picked are imported into the shared library and switched on for this server. */
export default function PluginsTab({ server, actions, dropActive }: PluginsTabProps) {
  const { id } = server.config;
  const state = server.runtime.state;
  const [library, setLibrary] = useState<PluginEntry[] | null>(null);
  const [libraryError, setLibraryError] = useState<string | null>(null);
  const [configs, setConfigs] = useState<PluginConfigFile[]>([]);
  const [editing, setEditing] = useState<PluginConfigFile | null>(null);
  const [removing, setRemoving] = useState<PluginEntry | null>(null);
  const [dragging, setDragging] = useState(false);
  const [importNote, setImportNote] = useState<string | null>(null);
  const importing = useAction();
  const toggling = useAction();

  const loadLibrary = useCallback(async () => {
    try {
      setLibrary(await hosting.pluginLibrary());
      setLibraryError(null);
    } catch (e) {
      setLibraryError(errorMessage(e));
    }
  }, []);

  const loadConfigs = useCallback(async () => {
    try {
      setConfigs(await hosting.pluginConfigs(id));
    } catch {
      // Configure falls back to "start the server once" when the list is unknown.
      setConfigs([]);
    }
  }, [id]);

  useEffect(() => {
    loadLibrary();
  }, [loadLibrary]);

  // The library is shared: another server's page (or the Settings window) may change it.
  useEffect(() => {
    const pending = onHostingChanged(() => {
      loadLibrary();
      loadConfigs();
    });
    return () => {
      pending.then((unlisten) => unlisten());
    };
  }, [loadLibrary, loadConfigs]);

  // Plugins write their config files on first load, so refresh as the server comes up.
  useEffect(() => {
    loadConfigs();
  }, [loadConfigs, state]);

  const enabledRef = useRef(server.config.plugins);
  enabledRef.current = server.config.plugins;

  const runImport = importing.run;
  const importPaths = useCallback(
    (paths: string[]) =>
      runImport(async () => {
        setImportNote(null);
        const added = await hosting.importPlugins(paths);
        await loadLibrary();
        for (const p of added) {
          if (!enabledRef.current.includes(p.id)) await actions.setPluginEnabled(id, p.id, true);
        }
        setImportNote(
          added.length === 0
            ? "No plugins found."
            : `Added ${added.map((p) => p.id).join(", ")}.`
        );
      }),
    [runImport, loadLibrary, actions, id]
  );

  useEffect(() => {
    if (!dropActive) return;
    let disposed = false;
    let unlisten: (() => void) | null = null;
    getCurrentWebview()
      .onDragDropEvent((event) => {
        const p = event.payload;
        if (p.type === "enter" || p.type === "over") setDragging(true);
        else if (p.type === "leave") setDragging(false);
        else if (p.type === "drop") {
          setDragging(false);
          if (p.paths.length > 0) importPaths(p.paths);
        }
      })
      .then((fn) => {
        if (disposed) fn();
        else unlisten = fn;
      })
      .catch(() => {});
    return () => {
      disposed = true;
      unlisten?.();
      setDragging(false);
    };
  }, [dropActive, importPaths]);

  const pickFiles = async () => {
    const picked = await open({
      multiple: true,
      title: "Choose plugins",
      filters: [{ name: "Deadworks plugins", extensions: ["dll", "zip"] }],
    });
    if (picked && picked.length > 0) importPaths(picked);
  };

  const pickFolder = async () => {
    const picked = await open({ directory: true, title: "Choose a plugin folder" });
    if (typeof picked === "string") importPaths([picked]);
  };

  const running = isLive(state);

  return (
    <div className={styles.tab}>
      <div className={styles.header}>
        <span className={styles.headerTitle}>Plugin library</span>
        <div className={ui.row}>
          <button className={ui.btn} onClick={pickFolder} disabled={importing.busy}>
            Add folder
          </button>
          <button className={ui.btnPrimary} onClick={pickFiles} disabled={importing.busy}>
            {importing.busy ? "Adding..." : "Add plugins"}
          </button>
        </div>
      </div>

      {importing.error && (
        <div style={{ marginBottom: 10 }}>
          <ErrorNote message={importing.error} actionLabel="Choose again" onAction={pickFiles} />
        </div>
      )}
      {toggling.error && (
        <div style={{ marginBottom: 10 }}>
          <ErrorNote message={toggling.error} actionLabel="OK" onAction={toggling.clearError} />
        </div>
      )}
      {importNote && <div className={ui.noteOk} style={{ marginBottom: 10 }}>{importNote}</div>}

      {libraryError && !library ? (
        <ErrorNote message={`Couldn't load plugins: ${libraryError}`} actionLabel="Try again" onAction={loadLibrary} />
      ) : !library ? (
        <Loading />
      ) : library.length === 0 ? (
        <div className={cn(styles.list, styles.listEmpty)}>
          No plugins. Drop <strong>.dll</strong> or <strong>.zip</strong> files here, or use Add plugins.
        </div>
      ) : (
        <div className={styles.list}>
          {library.map((p) => {
            const enabled = server.config.plugins.includes(p.id);
            const config = configs.find((c) => c.pluginId === p.id);
            return (
              <div key={p.id} className={styles.listRow}>
                <Toggle
                  checked={enabled}
                  label={`Enable ${p.id}`}
                  disabled={toggling.busy}
                  onChange={(next) => toggling.run(() => actions.setPluginEnabled(id, p.id, next))}
                />
                <div className={styles.listMain}>
                  <div className={styles.listName}>{p.id}</div>
                  <div className={styles.listMeta}>
                    {formatBytes(p.sizeBytes)}
                  </div>
                </div>
                <button
                  className={cn(ui.btn, ui.btnSmall)}
                  disabled={!enabled}
                  title={enabled ? undefined : "Requires the plugin to be enabled"}
                  onClick={() =>
                    setEditing(config ?? { pluginId: p.id, exists: false, path: "" })
                  }
                >
                  Configure
                </button>
                <button className={cn(ui.btn, ui.btnSmall, ui.btnDanger)} onClick={() => setRemoving(p)}>
                  Remove
                </button>
              </div>
            );
          })}
        </div>
      )}

      <div className={ui.hint} style={{ marginTop: 10 }}>
        {running
          ? "Plugins load and unload without a restart."
          : "The library is shared by all servers. Plugins are enabled per server."}
      </div>

      {dragging && (
        <div className={styles.dropOverlay}>
          <div className={styles.dropBox}>
            <div className={styles.dropTitle}>Drop to add plugins</div>
            <div className={styles.dropHint}>to {server.config.name}</div>
          </div>
        </div>
      )}

      {editing && (
        <Suspense fallback={null}>
          <PluginConfigEditor
            serverId={id}
            file={editing}
            running={running}
            onClose={() => setEditing(null)}
            onChanged={loadConfigs}
          />
        </Suspense>
      )}

      {removing && (
        <ConfirmDialog
          title={`Remove ${removing.id}?`}
          message="Removes the plugin from the library and disables it on every server."
          confirmLabel="Remove"
          danger
          onConfirm={async () => {
            await hosting.removePlugin(removing.id);
            await loadLibrary();
            await actions.reload();
          }}
          onClose={() => setRemoving(null)}
        />
      )}
    </div>
  );
}

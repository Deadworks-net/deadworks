import { useState, type ReactNode } from "react";
import { hosting } from "@/lib/hosting";
import { useHosting } from "./use-hosting";
import { formatBytes, formatTimestamp, isLive } from "./format";
import { retryTask } from "./task-retry";
import TaskProgressView from "./TaskProgressView";
import { ConfirmDialog, ErrorNote, Loading, useAction } from "./ui";
import sw from "../SettingsWindow.module.css";

function Row({ title, description, control }: { title: string; description: ReactNode; control?: ReactNode }) {
  return (
    <div className={sw.settingRow}>
      <div className={sw.settingInfo}>
        <div className={sw.settingLabel}>{title}</div>
        <div className={sw.settingDesc}>{description}</div>
      </div>
      {control && <div className={sw.pathButtons}>{control}</div>}
    </div>
  );
}

/** Settings window → Hosting. Shares its state source with the HOST tab. */
export default function HostingSettingsSection() {
  const { overview, servers, task, error, actions } = useHosting();
  const [confirmUninstall, setConfirmUninstall] = useState(false);
  const act = useAction();
  const check = useAction();

  if (!overview) {
    return error ? (
      <ErrorNote message={`We couldn't load hosting details: ${error}`} actionLabel="Try again" onAction={actions.reload} />
    ) : (
      <Loading />
    );
  }

  const { updates, base } = overview;
  const taskRunning = !!task && !task.finished;
  const anyLive = servers.some((s) => isLive(s.runtime.state));

  // Without the install form here, a failed install is retried from the HOST tab.
  const retryInHere = () => {
    if (task) retryTask(task, actions, null, () => {});
  };
  const taskView = task && (
    <div style={{ marginTop: 12 }}>
      <TaskProgressView
        compact
        task={task}
        onCancel={actions.cancelTask}
        onDismiss={actions.dismissTask}
        onSteamInput={actions.steamcmdInput}
        onRepairClient={actions.repairClient}
        onRetry={retryInHere}
      />
    </div>
  );

  if (!overview.installed) {
    return (
      <>
        <h2 className={sw.sectionTitle}>Hosting</h2>
        {taskView}
        <Row
          title="Not set up"
          description="Open the HOST tab in the main window to set up hosting and create your first server."
        />
      </>
    );
  }

  return (
    <>
      <h2 className={sw.sectionTitle}>Hosting</h2>
      {updates.holdReason && (
        <div style={{ marginTop: 12 }}>
          <ErrorNote warn message={updates.holdReason} />
        </div>
      )}
      {taskView}
      {act.error && (
        <div style={{ marginTop: 12 }}>
          <ErrorNote message={act.error} actionLabel="OK" onAction={act.clearError} />
        </div>
      )}
      {check.error && (
        <div style={{ marginTop: 12 }}>
          <ErrorNote
            message={`We couldn't check for updates: ${check.error}`}
            actionLabel="Try again"
            onAction={() => check.run(actions.checkUpdates)}
          />
        </div>
      )}

      <div className={sw.sectionSubtitle}>Install</div>
      <div className={sw.settingRow}>
        <div className={sw.settingInfo}>
          <div className={sw.settingLabel}>Location</div>
          <div className={sw.settingDesc}>
            {base ? `Game files use ${formatBytes(base.sizeBytes)}` : "Size unknown"}
            {overview.dotnetVersion && ` · .NET ${overview.dotnetVersion}`}
          </div>
          <div className={sw.pathDisplay}>{overview.root ?? "Unknown"}</div>
        </div>
        <div className={sw.pathButtons}>
          <button className={sw.devBtn} onClick={() => act.run(() => hosting.openFolder(null))}>
            Open folder
          </button>
        </div>
      </div>

      {base && (
        <Row
          title="Game files"
          description={
            <>
              {base.source === "client" ? "Copied from your Deadlock install" : "Downloaded with SteamCMD"}
              {" · "}
              {base.copyMode === "hardlink"
                ? "linked (Steam can't update Deadlock while a server is running)"
                : "full copy"}
              {base.modifiedFiles.length > 0 &&
                ` · ${base.modifiedFiles.length} modded file${base.modifiedFiles.length === 1 ? "" : "s"} kept`}
            </>
          }
          control={
            <button
              className={sw.devBtn}
              disabled={taskRunning}
              onClick={() => actions.verify()}
            >
              Verify game files
            </button>
          }
        />
      )}

      <div className={sw.sectionSubtitle}>Updates</div>
      <Row
        title="Deadworks"
        description={`Installed ${updates.deadworksInstalled ?? "—"} · latest ${updates.deadworksLatest ?? "—"}`}
      />
      <Row
        title="Game version"
        description={`Installed build ${updates.installedBuildId ?? base?.buildId ?? "—"} · available ${updates.availableBuildId ?? "—"}`}
      />
      <Row
        title={updates.pending ? "An update is ready" : "Automatic updates"}
        description={
          <>
            {updates.pending
              ? "It installs when every server is stopped or empty, so nobody's game is interrupted."
              : "Updates install by themselves, but never in the middle of a game."}
            {updates.lastCheck != null && ` Last checked ${formatTimestamp(updates.lastCheck)}.`}
          </>
        }
        control={
          <>
            <button
              className={sw.devBtn}
              disabled={check.busy || taskRunning}
              onClick={() => check.run(actions.checkUpdates)}
            >
              {check.busy ? "Checking..." : "Check for updates"}
            </button>
            {updates.pending && (
              <button className={sw.devBtn} disabled={taskRunning} onClick={() => actions.applyUpdates()}>
                Apply now
              </button>
            )}
          </>
        }
      />

      <div className={sw.sectionSubtitle}>Remove</div>
      <Row
        title="Uninstall hosting"
        description={
          anyLive
            ? "Stop all your servers first."
            : "Deletes the game files, every server and your plugin library from this PC."
        }
        control={
          <button
            className={sw.devBtn}
            disabled={anyLive || taskRunning}
            onClick={() => setConfirmUninstall(true)}
          >
            Uninstall
          </button>
        }
      />

      {confirmUninstall && (
        <ConfirmDialog
          title="Uninstall hosting?"
          message={`This deletes ${overview.root ?? "the hosting folder"}, including all ${servers.length} server${servers.length === 1 ? "" : "s"} and your plugin library. Your Deadlock game is not touched.`}
          confirmLabel="Uninstall"
          danger
          onConfirm={async () => {
            await actions.uninstall();
          }}
          onClose={() => setConfirmUninstall(false)}
        />
      )}
    </>
  );
}

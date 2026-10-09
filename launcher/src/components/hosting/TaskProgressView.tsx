import { useEffect, useRef, useState, type FormEvent } from "react";
import type { TaskKind, TaskProgress } from "@/lib/hosting";
import { cn } from "@/lib/utils";
import { formatBytes, formatDuration, formatPercent } from "./format";
import { ErrorNote, ProgressBar, useAction } from "./ui";
import ui from "./ui.module.css";
import styles from "./HostPage.module.css";

const TITLES: Record<TaskKind, { running: string; failed: string; done: string }> = {
  install: { running: "Setting up hosting", failed: "Setup didn't finish", done: "Hosting is ready" },
  update: { running: "Updating", failed: "Update didn't finish", done: "Up to date" },
  verify: { running: "Verifying game files", failed: "Verification didn't finish", done: "Game files verified" },
  uninstall: { running: "Uninstalling", failed: "Uninstall didn't finish", done: "Hosting removed" },
};

/** Rough time left, from the average rate since the current stage began. */
function useEta(task: TaskProgress): number | null {
  const start = useRef<{ stage: string; at: number; bytes: number } | null>(null);
  if (!start.current || start.current.stage !== task.stage || task.bytesDone < start.current.bytes) {
    start.current = { stage: task.stage, at: Date.now(), bytes: task.bytesDone };
  }
  const elapsed = (Date.now() - start.current.at) / 1000;
  const done = task.bytesDone - start.current.bytes;
  if (task.finished || task.bytesTotal <= 0 || elapsed < 3 || done <= 0) return null;
  return (task.bytesTotal - task.bytesDone) / (done / elapsed);
}

function SteamPromptBox({ task, onSubmit }: { task: TaskProgress; onSubmit: (v: string) => Promise<void> }) {
  const [value, setValue] = useState("");
  const { run, busy, error } = useAction();
  useEffect(() => setValue(""), [task.steamPrompt]);

  if (task.steamPrompt === "mobile_confirm") {
    return (
      <div className={styles.prompt}>
        <div className={styles.taskLabel}>Approve the login in your Steam Mobile app.</div>
      </div>
    );
  }
  if (task.steamPrompt !== "guard_code" && task.steamPrompt !== "password") return null;

  const isCode = task.steamPrompt === "guard_code";
  const submit = (e: FormEvent) => {
    e.preventDefault();
    if (value.trim()) run(() => onSubmit(value.trim()));
  };

  return (
    <form className={styles.prompt} onSubmit={submit}>
      <label className={ui.label} htmlFor="steam-prompt">
        {isCode ? "Steam Guard code" : "Steam password"}
      </label>
      <div className={ui.row}>
        <input
          id="steam-prompt"
          className={cn(ui.input, isCode && ui.mono)}
          type={isCode ? "text" : "password"}
          autoComplete="off"
          autoFocus
          value={value}
          maxLength={isCode ? 10 : 256}
          onChange={(e) => setValue(e.target.value)}
          placeholder={isCode ? "e.g. 7XK2Q" : ""}
        />
        <button type="submit" className={ui.btnPrimary} disabled={busy || !value.trim()}>
          Continue
        </button>
      </div>
      {!isCode && <div className={ui.hint}>The password is not stored.</div>}
      {error && <div className={ui.errorText}>{error}</div>}
    </form>
  );
}

const MODIFIED_SHOWN = 20;

/** The client install has files changed by mods; the server needs Steam's originals. */
function ModifiedFilesBox({
  files,
  onRetry,
  onRepairClient,
  onUseModified,
}: {
  files: string[];
  onRetry: () => void;
  onRepairClient: () => Promise<void>;
  onUseModified?: () => void;
}) {
  const repair = useAction();
  const [repairStarted, setRepairStarted] = useState(false);
  const shown = files.slice(0, MODIFIED_SHOWN);
  const more = files.length - shown.length;

  return (
    <div className={styles.modified}>
      <div className={styles.taskLabel}>
        The server requires unmodified files.
      </div>
      <ul className={styles.modifiedList}>
        {shown.map((f) => (
          <li key={f}>{f}</li>
        ))}
        {more > 0 && <li className={styles.modifiedMore}>and {more.toLocaleString()} more</li>}
      </ul>
      {repairStarted && !repair.error && (
        <div className={ui.noteOk}>Steam is verifying the game files. Retry once it finishes.</div>
      )}
      {repair.error && <div className={ui.errorText}>{repair.error}</div>}
      <div className={styles.taskActions}>
        {onUseModified && (
          <button className={cn(ui.linkBtn, styles.formActionsLeft)} onClick={onUseModified}>
            Use anyway
          </button>
        )}
        <button
          className={ui.btn}
          disabled={repair.busy}
          onClick={async () => {
            if (await repair.run(onRepairClient)) setRepairStarted(true);
          }}
        >
          Repair with Steam
        </button>
        <button className={ui.btnPrimary} onClick={onRetry}>
          Retry
        </button>
      </div>
    </div>
  );
}

interface TaskProgressViewProps {
  task: TaskProgress;
  onCancel: () => Promise<void>;
  onRetry: () => void;
  onDismiss: () => void;
  onSteamInput: (value: string) => Promise<void>;
  onRepairClient: () => Promise<void>;
  /** Re-run the install accepting modified files; omitted when the options aren't known. */
  onUseModified?: () => void;
  /** A one-row banner instead of the full-page view. */
  compact?: boolean;
}

export default function TaskProgressView({
  task,
  onCancel,
  onRetry,
  onDismiss,
  onSteamInput,
  onRepairClient,
  onUseModified,
  compact,
}: TaskProgressViewProps) {
  const cancel = useAction();
  const eta = useEta(task);
  const titles = TITLES[task.kind];
  const fraction = task.bytesTotal > 0 ? task.bytesDone / task.bytesTotal : null;
  const failed = task.error != null;
  const done = task.finished && !failed;
  const modified = failed && task.modifiedFiles.length > 0;

  // A finished background task (usually an automatic update) doesn't need a click to go away.
  useEffect(() => {
    if (!compact || !done) return;
    const t = window.setTimeout(onDismiss, 6000);
    return () => window.clearTimeout(t);
  }, [compact, done, onDismiss]);
  const modifiedBox = modified && (
    <ModifiedFilesBox
      files={task.modifiedFiles}
      onRetry={onRetry}
      onRepairClient={onRepairClient}
      onUseModified={onUseModified}
    />
  );

  const stats = !task.finished && (
    <div className={styles.taskStats}>
      <span>
        {task.bytesTotal > 0 &&
          `${formatBytes(task.bytesDone)} of ${formatBytes(task.bytesTotal)} · ${formatPercent(fraction ?? 0)}`}
        {task.bytesTotal > 0 && task.filesTotal > 0 && " · "}
        {task.filesTotal > 0 &&
          `${task.filesDone.toLocaleString()} of ${task.filesTotal.toLocaleString()} files`}
      </span>
      {eta != null && <span>About {formatDuration(eta)} left</span>}
    </div>
  );

  if (compact) {
    return (
      <div className={styles.banner}>
        <div className={styles.bannerRow}>
          <strong>{failed ? titles.failed : done ? titles.done : titles.running}</strong>
          <span className={styles.bannerLabel}>{failed ? task.error : done ? "" : task.label}</span>
          {failed && !modified && (
            <button className={cn(ui.btn, ui.btnSmall)} onClick={onRetry}>
              Try again
            </button>
          )}
          {task.finished ? (
            <button className={cn(ui.btn, ui.btnSmall)} onClick={onDismiss}>
              {failed ? "Close" : "OK"}
            </button>
          ) : (
            <button
              className={cn(ui.btn, ui.btnSmall)}
              disabled={cancel.busy}
              onClick={() => cancel.run(onCancel)}
            >
              Cancel
            </button>
          )}
        </div>
        {!task.finished && <ProgressBar value={fraction} />}
        {stats}
        {cancel.error && <div className={ui.errorText}>{cancel.error}</div>}
        <SteamPromptBox task={task} onSubmit={onSteamInput} />
        {modifiedBox}
      </div>
    );
  }

  return (
    <div className={ui.centered}>
      <div className={styles.task}>
        <div className={styles.taskTitle}>
          {modified ? "Modded game files found" : failed ? titles.failed : done ? titles.done : titles.running}
        </div>
        {modified ? (
          modifiedBox
        ) : failed ? (
          <ErrorNote message={task.error ?? ""} actionLabel="Try again" onAction={onRetry} />
        ) : done ? (
          <div className={styles.taskActions}>
            <button className={ui.btnPrimary} onClick={onDismiss}>
              Continue
            </button>
          </div>
        ) : (
          <>
            <div className={styles.taskLabel}>{task.label}</div>
            <ProgressBar value={fraction} />
            {stats}
            <SteamPromptBox task={task} onSubmit={onSteamInput} />
            {cancel.error && <div className={ui.errorText}>{cancel.error}</div>}
            <div className={styles.taskActions}>
              <button className={ui.btn} disabled={cancel.busy} onClick={() => cancel.run(onCancel)}>
                {cancel.busy ? "Cancelling..." : "Cancel"}
              </button>
            </div>
          </>
        )}
        {failed && (
          <div className={styles.taskActions}>
            <button className={ui.linkBtn} onClick={onDismiss}>
              Back
            </button>
          </div>
        )}
      </div>
    </div>
  );
}

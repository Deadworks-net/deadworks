import { useEffect, useMemo, useRef, useState } from "react";
import { check, type Update } from "@tauri-apps/plugin-updater";
import { relaunch } from "@tauri-apps/plugin-process";
import { ask } from "@tauri-apps/plugin-dialog";
import { hosting } from "@/lib/hosting";
import { listen } from "@tauri-apps/api/event";
import { cn } from "@/lib/utils";
import styles from "./UpdateManager.module.css";

type DownloadEvent =
  | { event: "Started"; data: { contentLength: number } }
  | { event: "Progress"; data: { chunkLength: number } }
  | { event: "Finished" };

type UpdateState =
  | "idle"
  | "checking"
  | "available"
  | "downloading"
  | "ready"
  | "error";

function formatBytes(bytes: number): string {
  if (!bytes) return "0 B";
  const units = ["B", "KB", "MB", "GB"];
  const exp = Math.min(Math.floor(Math.log(bytes) / Math.log(1024)), units.length - 1);
  const value = bytes / 1024 ** exp;
  return `${value.toFixed(value < 10 && exp > 0 ? 1 : 0)} ${units[exp]}`;
}

// Inline SVG icons matching existing app style
function RocketIcon() {
  return (
    <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
      <path d="M4.5 16.5c-1.5 1.26-2 5-2 5s3.74-.5 5-2c.71-.84.7-2.13-.09-2.91a2.18 2.18 0 0 0-2.91-.09z" />
      <path d="m12 15-3-3a22 22 0 0 1 2-3.95A12.88 12.88 0 0 1 22 2c0 2.72-.78 7.5-6 11a22.35 22.35 0 0 1-4 2z" />
      <path d="M9 12H4s.55-3.03 2-4c1.62-1.08 5 0 5 0" />
      <path d="M12 15v5s3.03-.55 4-2c1.08-1.62 0-5 0-5" />
    </svg>
  );
}

function CheckIcon() {
  return (
    <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
      <path d="M22 11.08V12a10 10 0 1 1-5.93-9.14" />
      <polyline points="22 4 12 14.01 9 11.01" />
    </svg>
  );
}

function CloseIcon() {
  return (
    <svg width="12" height="12" viewBox="0 0 10 10">
      <line x1="1" y1="1" x2="9" y2="9" stroke="currentColor" strokeWidth="1.2" />
      <line x1="9" y1="1" x2="1" y2="9" stroke="currentColor" strokeWidth="1.2" />
    </svg>
  );
}

function RefreshIcon() {
  return (
    <svg width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
      <polyline points="23 4 23 10 17 10" />
      <path d="M20.49 15a9 9 0 1 1-2.12-9.36L23 10" />
    </svg>
  );
}

export default function UpdateManager() {
  const [state, setState] = useState<UpdateState>("idle");
  const [update, setUpdate] = useState<Update | null>(null);
  const [dismissed, setDismissed] = useState(false);
  const [showNotes, setShowNotes] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [downloadedBytes, setDownloadedBytes] = useState(0);
  const [totalBytes, setTotalBytes] = useState(0);
  const totalBytesRef = useRef(0);
  const [testMode, setTestMode] = useState(false);

  useEffect(() => {
    if (import.meta.env.DEV) return;

    let cancelled = false;

    const runCheck = async () => {
      setState("checking");
      try {
        const result = await check();
        if (!cancelled && result) {
          setUpdate(result);
          setState("available");
        } else if (!cancelled) {
          setState("idle");
        }
      } catch (err) {
        if (!cancelled) {
          console.error("Update check failed:", err);
          setError(err instanceof Error ? err.message : "Failed to check for updates");
          setState("error");
        }
      }
    };

    const timeout = window.setTimeout(runCheck, 1500);
    return () => {
      cancelled = true;
      window.clearTimeout(timeout);
    };
  }, []);

  // Dev: listen for test trigger from settings window
  useEffect(() => {
    const unlisten = listen("test-update-ui", () => {
      const fakeUpdate = {
        version: "0.99.0",
        date: new Date().toISOString(),
        body: "This is a simulated update for testing the update manager UI.\n\n- New feature A\n- Bug fix B\n- Performance improvement C",
        downloadAndInstall: (cb: (event: DownloadEvent) => void) =>
          new Promise<void>((resolve) => {
            const total = 15_000_000;
            cb({ event: "Started", data: { contentLength: total } });
            let downloaded = 0;
            const interval = setInterval(() => {
              const chunk = Math.min(1_500_000, total - downloaded);
              downloaded += chunk;
              cb({ event: "Progress", data: { chunkLength: chunk } });
              if (downloaded >= total) {
                clearInterval(interval);
                cb({ event: "Finished" });
                resolve();
              }
            }, 200);
          }),
      } as unknown as Update;

      setTestMode(true);
      setUpdate(fakeUpdate);
      setDismissed(false);
      setError(null);
      setState("available");
    });

    return () => { unlisten.then((fn) => fn()); };
  }, []);

  const progress = useMemo(() => {
    if (!totalBytes) return 0;
    return Math.min(100, Math.round((downloadedBytes / totalBytes) * 100));
  }, [downloadedBytes, totalBytes]);

  const handleDismiss = () => {
    setDismissed(true);
    if (state !== "error") setError(null);
  };

  const handleRetry = async () => {
    setDismissed(false);
    setError(null);
    setDownloadedBytes(0);
    setTotalBytes(0);
    totalBytesRef.current = 0;
    setUpdate(null);

    try {
      setState("checking");
      const result = await check();
      if (result) {
        setUpdate(result);
        setState("available");
      } else {
        setState("idle");
      }
    } catch (err) {
      console.error("Update check failed:", err);
      setError(err instanceof Error ? err.message : "Failed to check for updates");
      setState("error");
    }
  };

  // Listen for manual update check from settings
  useEffect(() => {
    const unlisten = listen("check-for-updates", () => {
      handleRetry();
    });
    return () => { unlisten.then((fn) => fn()); };
  }, []);

  const handleInstall = async () => {
    if (!update) return;
    if (!testMode && !(await stopServersForUpdate())) return;

    setState("downloading");
    setDismissed(false);
    setDownloadedBytes(0);
    setTotalBytes(0);
    totalBytesRef.current = 0;
    setError(null);

    try {
      await update.downloadAndInstall((event: DownloadEvent) => {
        switch (event.event) {
          case "Started":
            totalBytesRef.current = event.data.contentLength;
            setTotalBytes(event.data.contentLength);
            break;
          case "Progress":
            setDownloadedBytes((prev) => {
              const next = prev + event.data.chunkLength;
              return totalBytesRef.current ? Math.min(next, totalBytesRef.current) : next;
            });
            break;
          case "Finished":
            setDownloadedBytes((prev) =>
              totalBytesRef.current ? totalBytesRef.current : prev
            );
            break;
        }
      });
      setState("ready");
    } catch (err) {
      console.error("Update install failed:", err);
      setError(err instanceof Error ? err.message : "Failed to install update");
      setState("error");
    }
  };

  const handleRestart = async () => {
    if (testMode) {
      setTestMode(false);
      setUpdate(null);
      setState("idle");
      return;
    }
    if (!(await stopServersForUpdate())) return;
    try {
      await relaunch();
    } catch (err) {
      console.error("Relaunch failed:", err);
      setError(err instanceof Error ? err.message : "Failed to restart");
    }
  };

  // Nothing to show
  if (state === "idle" || state === "checking") return null;

  // Dismissed floating badge
  const showBadge = dismissed && (state === "available" || state === "ready");
  if (showBadge) {
    return (
      <button
        className={cn(styles.badge, state === "ready" && styles.badgeReady)}
        onClick={() => setDismissed(false)}
      >
        {state === "ready" ? <CheckIcon /> : <RefreshIcon />}
        {state === "ready" ? "Restart to update" : "Update available"}
      </button>
    );
  }

  // Error panel
  if (!dismissed && state === "error" && error) {
    return (
      <div className={cn(styles.panel, styles.panelError)}>
        <div className={styles.header}>
          <div className={styles.headerInfo}>
            <div className={cn(styles.title, styles.titleError)}>Update issue</div>
            <div className={styles.errorMessage}>{error}</div>
          </div>
          <button className={styles.dismissBtn} onClick={() => { setDismissed(true); setState("idle"); }} aria-label="Dismiss">
            <CloseIcon />
          </button>
        </div>
        <div className={styles.buttons}>
          <button className={styles.primaryBtn} onClick={handleRetry}>TRY AGAIN</button>
          <button className={styles.secondaryBtn} onClick={() => { setDismissed(true); setState("idle"); }}>CLOSE</button>
        </div>
      </div>
    );
  }

  // Main update panel (available / downloading / ready)
  if (dismissed || !update) return null;

  const releaseDate = update.date
    ? (() => { const d = new Date(update.date); return isNaN(d.getTime()) ? null : d.toLocaleDateString(); })()
    : null;

  return (
    <div className={styles.panel}>
      <div className={styles.header}>
        <div className={styles.headerInfo}>
          <div className={styles.title}>
            <RocketIcon />
            {state === "ready" ? "Update installed" : "Update available"}
          </div>
          <div className={styles.version}>
            Version {update.version}
            {releaseDate ? ` \u2022 Released ${releaseDate}` : ""}
          </div>
        </div>
        <button className={styles.dismissBtn} onClick={handleDismiss} aria-label="Dismiss">
          <CloseIcon />
        </button>
      </div>

      {update.body ? (
        <>
          <button className={styles.notesToggle} onClick={() => setShowNotes((v) => !v)}>
            {showNotes ? "Hide release notes" : "View release notes"}
          </button>
          {showNotes && (
            <div className={styles.notesContent}>{update.body.trim()}</div>
          )}
        </>
      ) : null}

      {state === "downloading" && (
        <div className={styles.progressSection}>
          <div className={styles.progressTrack}>
            <div className={styles.progressBar} style={{ width: `${progress}%` }} />
          </div>
          <div className={styles.progressText}>
            Downloading {formatBytes(downloadedBytes)}
            {totalBytes ? ` of ${formatBytes(totalBytes)}` : ""}...
          </div>
        </div>
      )}

      {state === "ready" && (
        <div className={styles.readySection}>
          <div className={styles.readyMessage}>
            <CheckIcon />
            Update has been installed.
          </div>
          <div className={styles.readyHint}>
            Restart now to apply, or choose later.
          </div>
          <div className={styles.buttons}>
            <button className={styles.primaryBtn} onClick={handleRestart}>RESTART NOW</button>
            <button className={styles.secondaryBtn} onClick={handleDismiss}>LATER</button>
          </div>
        </div>
      )}

      {state === "available" && (
        <div className={styles.buttons}>
          <button className={styles.primaryBtn} onClick={handleInstall}>INSTALL & RESTART</button>
          <button className={styles.secondaryBtn} onClick={handleDismiss}>LATER</button>
        </div>
      )}
    </div>
  );
}

const LIVE_STATES = ["starting", "running", "stopping"];

/**
 * Installing an update restarts the launcher, and hosted servers die with it.
 * Ask first, then stop them cleanly. False means the user cancelled.
 */
async function stopServersForUpdate(): Promise<boolean> {
  const liveIds = async () => {
    try {
      const o = await hosting.overview();
      return o.servers.filter((s) => LIVE_STATES.includes(s.runtime.state)).map((s) => s.config.id);
    } catch {
      return [];
    }
  };
  const live = await liveIds();
  if (live.length === 0) return true;
  const ok = await ask(
    `${live.length === 1 ? "A server you're hosting is" : `${live.length} servers you're hosting are`} running. ` +
      "Updating restarts the launcher, which stops " +
      (live.length === 1 ? "it." : "them."),
    { title: "Stop servers and update?", kind: "warning", okLabel: "Stop and update", cancelLabel: "Not now" }
  );
  if (!ok) return false;
  await Promise.all(live.map((id) => hosting.stop(id).catch(() => undefined)));
  for (let i = 0; i < 40 && (await liveIds()).length > 0; i++) {
    await new Promise((r) => setTimeout(r, 500));
  }
  return true;
}

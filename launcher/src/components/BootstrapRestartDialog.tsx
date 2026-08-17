import { useEffect, useRef, useState } from "react";
import {
  bootstrapStatus,
  listenBootstrapStatus,
  retryBootstrapInstall,
} from "@/lib/tauri";
import type { BootstrapStatus } from "@/lib/types";
import styles from "./ConnectDialog.module.css";

/**
 * Surfaces a bootstrap update that is downloaded but cannot be installed while
 * Deadlock holds the live VPK open.
 *
 * The background poller retries the swap every 30s, so this modal is not what
 * makes the update land — without it the only sign is a line on the launcher's
 * stdout, and the player keeps joining servers on the old build with no idea
 * that quitting the game once is all it takes.
 */
export default function BootstrapRestartDialog() {
  const [pending, setPending] = useState(0);
  const [retrying, setRetrying] = useState(false);
  const [error, setError] = useState<string | null>(null);
  // Dismissing has to survive the poller re-announcing the same version every
  // 30s; a newer staged version is worth asking about again.
  const dismissed = useRef(0);

  useEffect(() => {
    const apply = (status: BootstrapStatus) => {
      const version = status.restart_required ? status.pending_version : 0;
      setPending(version > dismissed.current ? version : 0);
    };

    // Covers the launcher starting up while the game is already running: the
    // status is on disk from last session and no event is coming.
    bootstrapStatus().then(apply).catch(() => {});

    const unlisten = listenBootstrapStatus(apply);
    return () => {
      unlisten.then((stop) => stop()).catch(() => {});
    };
  }, []);

  if (!pending) return null;

  // A retry that finds the game still running changes nothing on screen: the
  // standing message already says exactly what to do, and swapping it for a
  // rephrasing of itself just makes the box flicker between two sentences.
  // Only a genuine failure — one that needs different words — replaces it.
  const retry = async () => {
    setRetrying(true);
    setError(null);
    try {
      const status = await retryBootstrapInstall();
      if (!status.restart_required) setPending(0);
    } catch (e) {
      setError(String(e));
    } finally {
      setRetrying(false);
    }
  };

  const close = () => {
    dismissed.current = pending;
    setPending(0);
  };

  return (
    <div className={styles.overlay}>
      <div className={styles.box}>
        <h3 className={styles.title}>Update pending</h3>
        <p className={styles.dialogMessage}>
          {error ?? "Please close Deadlock to finish updating the launcher."}
        </p>
        <div className={styles.actions}>
          <button onClick={retry} disabled={retrying} className={styles.cancelBtn}>
            {retrying ? "RETRYING..." : "RETRY"}
          </button>
          <button onClick={close} className={styles.cancelBtn}>
            CLOSE
          </button>
        </div>
      </div>
    </div>
  );
}

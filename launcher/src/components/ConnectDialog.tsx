import { useState, useEffect, useRef } from "react";
import { prepareAndConnect, connectAnyway, listenDownloadProgress, recordId } from "@/lib/tauri";
import type { Server } from "@/lib/types";
import styles from "./ConnectDialog.module.css";
import { cn } from "@/lib/utils";

interface ConnectDialogProps {
  server: Server;
  onClose: () => void;
}

export default function ConnectDialog({ server, onClose }: ConnectDialogProps) {
  const [status, setStatus] = useState("Initializing...");
  const [progress, setProgress] = useState<number | null>(null);
  const [error, setError] = useState<string | null>(null);
  const startedRef = useRef(false);
  // Read by the progress listener, which captured its own first render - so a ref, not state.
  const anywayRef = useRef(false);
  // The server runs more content than it could list; said once the join goes through.
  const partialRef = useRef(false);

  useEffect(() => {
    if (startedRef.current) return;
    startedRef.current = true;

    let unlisten: (() => void) | null = null;

    async function run() {
      unlisten = await listenDownloadProgress((p) => {
        if (p.status === "fetching") {
          setStatus("Checking server content...");
        } else if (p.status === "checking") {
          // Verifying a map means reading a gigabyte off disk, so show how far along it is.
          const pct = p.total_bytes > 0 ? Math.min(100, Math.round((p.bytes_downloaded / p.total_bytes) * 100)) : null;
          setStatus(
            `Verifying ${p.name}...${pct === null ? "" : ` ${pct}%`} (${p.item_index + 1}/${p.total_items})`
          );
          setProgress(pct);
        } else if (p.status === "downloading") {
          const pct = p.total_bytes > 0 ? Math.round((p.bytes_downloaded / p.total_bytes) * 100) : 0;
          setStatus(`Downloading ${p.name}... ${pct}% (${p.item_index + 1}/${p.total_items})`);
          setProgress(pct);
        } else if (p.status === "decompressing") {
          const pct = p.total_bytes > 0 ? Math.min(100, Math.round((p.bytes_downloaded / p.total_bytes) * 100)) : 0;
          setStatus(`Decompressing ${p.name}... ${pct}% (${p.item_index + 1}/${p.total_items})`);
          setProgress(pct);
        } else if (p.status === "ready") {
          setStatus(`${p.name} verified (${p.item_index + 1}/${p.total_items})`);
          setProgress(100);
        } else if (p.status === "incomplete") {
          partialRef.current = true;
        } else if (p.status === "connecting") {
          // Nothing was verified on the anyway path; that was the point of asking.
          setStatus(
            partialRef.current
              ? "Connecting. This server could not list all of its content, so some of it may be missing."
              : anywayRef.current
                ? "Connecting..."
                : "All content verified. Connecting..."
          );
          setProgress(100);
        }
      });

      try {
        setStatus("Checking server content...");
        const result = await prepareAndConnect(recordId(server), server.raw_address);
        if (result.success) {
          setStatus(result.message);
          setTimeout(onClose, 2000);
        } else {
          setError(result.message);
        }
      } catch (e) {
        const msg = typeof e === "string" ? e : String(e);
        setError(
          msg.includes("FILE_IN_USE")
            ? "One of this server's files is currently loaded in Deadlock. " +
                "Please fully disconnect or quit the game, then try joining again."
            : msg
        );
      }
    }

    run();

    return () => {
      unlisten?.();
    };
  }, [server, onClose]);

  // Go again with the mismatch tolerated. This re-downloads, so clear the error and hand
  // the dialog back to the progress listener rather than sitting on a dead message.
  async function joinAnyway() {
    // Decided by the error on screen now, before it is cleared.
    const acceptMismatch = mismatch;
    anywayRef.current = true;
    setError(null);
    setProgress(null);
    setStatus("Checking server content...");
    try {
      const result = await connectAnyway(recordId(server), server.raw_address, acceptMismatch);
      setStatus(result.message);
      setTimeout(onClose, 2000);
    } catch (e) {
      setError(typeof e === "string" ? e : String(e));
    }
  }

  const failed = error !== null;
  // The one failure the player can do something about, and the one the backend can act on:
  // the host is serving a different build than the server runs. Matched on text because
  // that is how the rest of this path classifies errors across the command boundary.
  const mismatch = failed && error.includes("does not match the version the server runs");

  return (
    <div className={styles.overlay} onClick={(e) => e.target === e.currentTarget && onClose()}>
      <div className={styles.box}>
        <h3 className={styles.title}>{failed ? "Could not join server" : "Connecting to server..."}</h3>
        <p className={styles.serverName}>{server.name}</p>
        <p className={styles.addr}>{server.address}</p>

        {failed ? (
          <p className={styles.dialogMessage}>
            {error}
            <span className={styles.hint}>
              {mismatch
                ? "Server content differs from the download host's content. Some custom content " +
                  "may be missing or broken. Consider letting the server operator know you " +
                  "received this warning."
                : "Joining anyway tries once more, then connects with whatever content is " +
                  "already installed, so some of it may be missing or out of date."}
            </span>
          </p>
        ) : (
          <>
            <div className={styles.progressTrack}>
              <div
                className={cn(styles.progressBar, progress == null && styles.progressIndeterminate)}
                style={progress != null ? { width: `${progress}%` } : undefined}
              />
            </div>
            <p className={styles.status}>{status}</p>
          </>
        )}

        <div className={styles.actions}>
          {failed && (
            <button onClick={joinAnyway} className={styles.cancelBtn}>
              CONNECT ANYWAY
            </button>
          )}
          <button onClick={onClose} className={styles.cancelBtn}>CANCEL</button>
        </div>
      </div>
    </div>
  );
}

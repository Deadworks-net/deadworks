import { useEffect, useState } from "react";
import { invoke } from "@tauri-apps/api/core";
import styles from "./ConnectDialog.module.css";

/**
 * Surfaces a failed gameinfo.gi patch from launcher startup.
 *
 * Nothing else reports it: the game launches perfectly happily against an
 * unpatched file and simply never mounts our search paths, so the first thing
 * the player notices is missing UI in-game. The usual cause is Deadlock (or
 * Deadlock Mod Manager) holding the file, which is why RETRY is worth offering
 * — closing them and retrying beats restarting the launcher.
 */
export default function GameinfoErrorDialog() {
  const [message, setMessage] = useState<string | null>(null);
  const [retrying, setRetrying] = useState(false);

  useEffect(() => {
    // The startup patch runs during Tauri setup, so it has already finished by
    // the time we mount — no need to listen for it.
    invoke<string | null>("gameinfo_error")
      .then(setMessage)
      .catch(() => {});
  }, []);

  if (!message) return null;

  const retry = async () => {
    setRetrying(true);
    try {
      await invoke("retry_gameinfo_patch");
      setMessage(null);
    } catch (e) {
      setMessage(String(e));
    } finally {
      setRetrying(false);
    }
  };

  return (
    <div className={styles.overlay}>
      <div className={styles.box}>
        <h3 className={styles.title}>Could not patch gameinfo.gi</h3>
        <p className={styles.dialogMessage}>{message}</p>
        <div className={styles.actions}>
          <button onClick={retry} disabled={retrying} className={styles.cancelBtn}>
            {retrying ? "RETRYING..." : "RETRY"}
          </button>
          <button onClick={() => setMessage(null)} className={styles.cancelBtn}>
            CLOSE
          </button>
        </div>
      </div>
    </div>
  );
}

import { useCallback, useEffect, useMemo, useState } from "react";
import { open } from "@tauri-apps/plugin-dialog";
import { hosting, type BaseSource, type CopyMode, type DriveInfo, type InstallOptions, type SetupCheck } from "@/lib/hosting";
import { cn } from "@/lib/utils";
import { errorMessage, formatBytes } from "./format";
import { ErrorNote, Loading } from "./ui";
import ui from "./ui.module.css";
import styles from "./HostPage.module.css";

/** Used when Steam's manifests don't tell us the size. */
const FALLBACK_REQUIRED = 40 * 1024 ** 3;
/** Linked installs still need Deadworks, .NET and some headroom. */
const LINKED_REQUIRED = 2 * 1024 ** 3;
const FOLDER_NAME = "Deadworks Servers";

function driveFor(path: string, drives: DriveInfo[]): DriveInfo | null {
  const p = path.trim().toUpperCase();
  return drives.find((d) => p.startsWith(d.root.toUpperCase())) ?? null;
}

function isAbsoluteWindowsPath(path: string): boolean {
  return /^[A-Za-z]:\\/.test(path.trim());
}

function driveLetter(root: string): string {
  return root.replace(/\\$/, "");
}

export type InstallDraft = Omit<InstallOptions, "steamPassword">;

interface InstallScreenProps {
  /** Previous choices, e.g. after a failed attempt. Never includes the password. */
  initial: InstallDraft | null;
  onBack: () => void;
  onInstall: (options: InstallOptions) => void;
}

function SpaceBar({ drive, required }: { drive: DriveInfo; required: number }) {
  const used = drive.totalBytes - drive.freeBytes;
  const enough = drive.freeBytes >= required;
  const pct = (n: number) => `${drive.totalBytes > 0 ? (n / drive.totalBytes) * 100 : 0}%`;
  return (
    <>
      <div className={styles.spaceBar} aria-hidden>
        <div className={styles.spaceUsed} style={{ width: pct(used) }} />
        <div
          className={enough ? styles.spaceNeeded : styles.spaceNeededBad}
          style={{ width: pct(Math.min(required, drive.freeBytes)) }}
        />
      </div>
      <div className={styles.spaceLegend}>
        <span>Needs {formatBytes(required)}</span>
        <span className={enough ? undefined : ui["textTone-bad"]}>
          {formatBytes(drive.freeBytes)} free on {driveLetter(drive.root)}
        </span>
      </div>
    </>
  );
}

export default function InstallScreen({ initial, onBack, onInstall }: InstallScreenProps) {
  const [check, setCheck] = useState<SetupCheck | null>(null);
  const [checkError, setCheckError] = useState<string | null>(null);
  const [checking, setChecking] = useState(true);

  const [root, setRoot] = useState(initial?.root ?? "");
  const [source, setSource] = useState<BaseSource>(initial?.source ?? "client");
  const [copyMode, setCopyMode] = useState<CopyMode>(initial?.copyMode ?? "copy");
  const [username, setUsername] = useState(initial?.steamUsername ?? "");
  const [password, setPassword] = useState("");

  const runCheck = useCallback(async () => {
    setChecking(true);
    setCheckError(null);
    try {
      const result = await hosting.setupCheck();
      setCheck(result);
      setRoot((r) => r || result.suggestedRoot);
      if (!result.clientGameDir) setSource("steamcmd");
    } catch (e) {
      setCheckError(errorMessage(e));
    } finally {
      setChecking(false);
    }
  }, []);

  useEffect(() => {
    runCheck();
  }, [runCheck]);

  const drive = useMemo(() => (check ? driveFor(root, check.drives) : null), [check, root]);
  const canLink = source === "client" && !!drive?.sameAsClient;
  const effectiveMode: CopyMode = canLink ? copyMode : "copy";
  const fullSize = check && check.requiredBytes > 0 ? check.requiredBytes : FALLBACK_REQUIRED;
  const required = effectiveMode === "hardlink" ? LINKED_REQUIRED : fullSize;

  const browse = async () => {
    const picked = await open({
      directory: true,
      title: "Choose where to keep your servers",
      defaultPath: root || undefined,
    });
    if (typeof picked !== "string") return;
    // Picking a bare drive would scatter files over its root.
    setRoot(/^[A-Za-z]:\\?$/.test(picked) ? `${picked.replace(/\\?$/, "\\")}${FOLDER_NAME}` : picked);
  };

  if (checking && !check) return <div className={styles.card}><Loading label="Looking for Deadlock and free space..." /></div>;
  if (!check) {
    return (
      <div className={styles.card}>
        <div className={ui.centered}>
          <ErrorNote
            message={`We couldn't check this PC: ${checkError ?? "unknown error"}`}
            actionLabel="Try again"
            onAction={runCheck}
          />
          <button className={ui.linkBtn} onClick={onBack}>Back</button>
        </div>
      </div>
    );
  }

  const clientUnavailable = !check.clientGameDir
    ? "We couldn't find Deadlock on this PC."
    : check.clientUpdating
      ? "Steam is updating Deadlock right now. Wait for it to finish, then check again."
      : null;

  const blocker = !root.trim()
    ? "Choose where to install."
    : !isAbsoluteWindowsPath(root)
      ? "Pick a full folder path, like D:\\Deadworks Servers."
      : drive && drive.freeBytes < required
        ? `Not enough space on ${driveLetter(drive.root)}. Pick a drive with at least ${formatBytes(required)} free.`
        : source === "client" && clientUnavailable
          ? clientUnavailable
          : source === "steamcmd" && (!username.trim() || !password)
            ? "Enter your Steam username and password."
            : null;

  const install = () => {
    if (blocker) return;
    onInstall({
      root: root.trim(),
      source,
      copyMode: effectiveMode,
      ...(source === "steamcmd" ? { steamUsername: username.trim(), steamPassword: password } : {}),
    });
  };

  return (
    <div className={styles.card}>
      <div className={styles.scroll}>
        <div className={styles.form}>
          <h2 className={styles.formTitle}>Set up hosting</h2>
          <p className={styles.formSubtitle}>
            This happens once. Every server you create shares these files.
          </p>

          <div className={styles.section}>
            <label className={ui.label} htmlFor="install-root">Install location</label>
            <div className={ui.row}>
              <input
                id="install-root"
                className={cn(ui.input, ui.mono)}
                value={root}
                onChange={(e) => setRoot(e.target.value)}
                spellCheck={false}
              />
              <button className={ui.btn} onClick={browse}>Browse</button>
            </div>
            <div className={styles.driveChips}>
              {check.drives.map((d) => (
                <button
                  key={d.root}
                  className={cn(styles.driveChip, drive?.root === d.root && styles.driveChipActive)}
                  onClick={() => setRoot(`${d.root.replace(/\\?$/, "\\")}${FOLDER_NAME}`)}
                  title={`${formatBytes(d.freeBytes)} free of ${formatBytes(d.totalBytes)}`}
                >
                  {driveLetter(d.root)} · {formatBytes(d.freeBytes)} free
                </button>
              ))}
            </div>
            {drive ? (
              <SpaceBar drive={drive} required={required} />
            ) : (
              root.trim() && <div className={ui.hint}>We can't tell how much space is free there.</div>
            )}
          </div>

          <div className={styles.section}>
            <div className={ui.label}>Game files</div>
            {source === "client" ? (
              <>
                {check.clientGameDir && (
                  <div className={styles.detected}>
                    Copied from your Deadlock install at{" "}
                    <span className={styles.detectedPath}>{check.clientGameDir}</span>. Only the game's own
                    files come along, so mods and anything else in that folder stay out of your servers.
                  </div>
                )}
                {clientUnavailable && (
                  <div style={{ marginTop: 8 }}>
                    <ErrorNote warn message={clientUnavailable} actionLabel="Check again" onAction={runCheck} />
                  </div>
                )}

                {canLink && (
                  <div style={{ marginTop: 12 }}>
                    <div className={ui.label}>Disk space</div>
                    <div className={ui.choiceGrid} role="radiogroup" aria-label="Disk space">
                      <button
                        role="radio"
                        aria-checked={copyMode === "copy"}
                        className={cn(ui.choice, copyMode === "copy" && ui.choiceSelected)}
                        onClick={() => setCopyMode("copy")}
                      >
                        <span className={ui.choiceTitle}>Full copy</span>
                        <span className={ui.choiceDesc}>
                          Uses {formatBytes(fullSize)}. Works independently of your game.
                        </span>
                      </button>
                      <button
                        role="radio"
                        aria-checked={copyMode === "hardlink"}
                        className={cn(ui.choice, copyMode === "hardlink" && ui.choiceSelected)}
                        onClick={() => setCopyMode("hardlink")}
                      >
                        <span className={ui.choiceTitle}>Link to your game files</span>
                        <span className={ui.choiceDesc}>
                          Uses almost no extra space, but Steam can't update Deadlock while a server is running.
                        </span>
                      </button>
                    </div>
                  </div>
                )}

                <div style={{ marginTop: 10 }}>
                  <button className={ui.linkBtn} onClick={() => setSource("steamcmd")}>
                    Download with SteamCMD instead
                  </button>
                </div>
              </>
            ) : (
              <div className={styles.steamBox}>
                <div className={styles.detected}>
                  Download the game straight from Steam. You need a Steam account that owns Deadlock.
                </div>
                <div className={styles.twoCol}>
                  <div className={ui.field}>
                    <label className={ui.label} htmlFor="steam-user">Steam username</label>
                    <input
                      id="steam-user"
                      className={ui.input}
                      autoComplete="username"
                      value={username}
                      onChange={(e) => setUsername(e.target.value)}
                      spellCheck={false}
                    />
                  </div>
                  <div className={ui.field}>
                    <label className={ui.label} htmlFor="steam-pass">Password</label>
                    <input
                      id="steam-pass"
                      className={ui.input}
                      type="password"
                      autoComplete="current-password"
                      value={password}
                      onChange={(e) => setPassword(e.target.value)}
                    />
                  </div>
                </div>
                <div className={ui.hint}>Your password is used once to log in and is never stored.</div>
                {check.clientGameDir && (
                  <div style={{ marginTop: 10 }}>
                    <button className={ui.linkBtn} onClick={() => setSource("client")}>
                      Copy from my Deadlock install instead
                    </button>
                  </div>
                )}
              </div>
            )}
          </div>

          <div className={styles.formActions}>
            <button className={cn(ui.linkBtn, styles.formActionsLeft)} onClick={onBack}>Back</button>
            {blocker && <span className={styles.disabledReason}>{blocker}</span>}
            <button className={cn(ui.btnPrimary, ui.btnLarge)} disabled={!!blocker} onClick={install}>
              Install
            </button>
          </div>
        </div>
      </div>
    </div>
  );
}

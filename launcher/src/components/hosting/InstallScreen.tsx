import { useCallback, useEffect, useMemo, useState } from "react";
import { open } from "@tauri-apps/plugin-dialog";
import {
  hosting,
  type BaseSource,
  type CopyMode,
  type DriveInfo,
  type HostPlatform,
  type InstallOptions,
  type SetupCheck,
} from "@/lib/hosting";
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

/** Path rules for the platform the servers will live on. Linux paths are case-sensitive and use "/". */
interface PathRules {
  platform: HostPlatform;
  separator: string;
}

function pathRules(check: SetupCheck): PathRules {
  const linux = check.platform === "linux";
  return { platform: check.platform, separator: check.pathSeparator || (linux ? "/" : "\\") };
}

function foldCase(path: string, rules: PathRules): string {
  return rules.platform === "linux" ? path : path.toUpperCase();
}

/** A drive root without its trailing separator: "H:\" becomes "H:", "/home" stays, "/" becomes "". */
function rootStem(root: string, rules: PathRules): string {
  const folded = foldCase(root, rules);
  return folded.endsWith(rules.separator) ? folded.slice(0, -rules.separator.length) : folded;
}

/** The drive (Windows) or mount point (Linux) a path lives on: the longest root it sits under. */
function driveFor(path: string, drives: DriveInfo[], rules: PathRules): DriveInfo | null {
  const p = foldCase(path.trim(), rules);
  if (!p) return null;
  let best: DriveInfo | null = null;
  let bestLength = -1;
  for (const d of drives) {
    const stem = rootStem(d.root, rules);
    const inside = p === stem || p.startsWith(stem + rules.separator);
    if (inside && stem.length > bestLength) {
      best = d;
      bestLength = stem.length;
    }
  }
  return best;
}

function isAbsolutePath(path: string, rules: PathRules): boolean {
  const p = path.trim();
  return rules.platform === "linux" ? p.startsWith("/") : /^[A-Za-z]:\\/.test(p);
}

function examplePath(rules: PathRules): string {
  return rules.platform === "linux" ? `/home/me/${FOLDER_NAME}` : `D:\\${FOLDER_NAME}`;
}

/** "H:" on Windows; the mount path ("/", "/home") on Linux. */
function driveLabel(root: string, rules: PathRules): string {
  return rules.platform === "linux" ? root : root.replace(/\\$/, "");
}

function joinFolder(root: string, rules: PathRules): string {
  return `${root.endsWith(rules.separator) ? root : root + rules.separator}${FOLDER_NAME}`;
}

/** Picking a bare drive or mount point would scatter files over its root. */
function isBareRoot(path: string, drives: DriveInfo[], rules: PathRules): boolean {
  if (rules.platform !== "linux" && /^[A-Za-z]:\\?$/.test(path)) return true;
  const stem = rootStem(path, rules);
  return drives.some((d) => rootStem(d.root, rules) === stem);
}

export type InstallDraft = Omit<InstallOptions, "steamPassword">;

interface InstallScreenProps {
  /** Previous choices, e.g. after a failed attempt. Never includes the password. */
  initial: InstallDraft | null;
  onBack: () => void;
  onInstall: (options: InstallOptions) => void;
}

function SpaceBar({ drive, label, required }: { drive: DriveInfo; label: string; required: number }) {
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
        <span>Requires {formatBytes(required)}</span>
        <span className={enough ? undefined : ui["textTone-bad"]}>
          {formatBytes(drive.freeBytes)} free on {label}
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
      if (!result.steamcmdAvailable) setSource("client");
      else if (!result.clientGameDir) setSource("steamcmd");
    } catch (e) {
      setCheckError(errorMessage(e));
    } finally {
      setChecking(false);
    }
  }, []);

  useEffect(() => {
    runCheck();
  }, [runCheck]);

  const rules = useMemo(() => (check ? pathRules(check) : null), [check]);
  const drive = useMemo(
    () => (check && rules ? driveFor(root, check.drives, rules) : null),
    [check, rules, root]
  );
  const canLink = source === "client" && !!drive?.sameAsClient;
  const effectiveMode: CopyMode = canLink ? copyMode : "copy";
  const fullSize = check && check.requiredBytes > 0 ? check.requiredBytes : FALLBACK_REQUIRED;
  const required = effectiveMode === "hardlink" ? LINKED_REQUIRED : fullSize;

  const browse = async () => {
    const picked = await open({
      directory: true,
      title: "Choose install location",
      defaultPath: root || undefined,
    });
    if (typeof picked !== "string" || !check || !rules) return;
    setRoot(isBareRoot(picked, check.drives, rules) ? joinFolder(picked, rules) : picked);
  };

  if (checking && !check) return <div className={styles.card}><Loading label="Checking this PC..." /></div>;
  if (!check || !rules) {
    return (
      <div className={styles.card}>
        <div className={ui.centered}>
          <ErrorNote
            message={`Couldn't check this PC: ${checkError ?? "unknown error"}`}
            actionLabel="Try again"
            onAction={runCheck}
          />
          <button className={ui.linkBtn} onClick={onBack}>Back</button>
        </div>
      </div>
    );
  }

  const linux = check.platform === "linux";
  const driveName = drive ? driveLabel(drive.root, rules) : "";
  // The folder the backend suggests on a drive beats its bare root (on Linux, "/" isn't ours to write to).
  const suggestedDrive = driveFor(check.suggestedRoot, check.drives, rules);
  const folderOn = (d: DriveInfo) =>
    suggestedDrive?.root === d.root ? check.suggestedRoot : joinFolder(d.root, rules);

  const clientUnavailable = !check.clientGameDir
    ? check.steamcmdAvailable
      ? "Deadlock is not installed."
      : "Deadlock is not installed. Install it through Steam."
    : check.clientUpdating
      ? "Steam is updating Deadlock. Wait for it to finish."
      : null;

  const blocker =
    check.missingTools.length > 0
      ? "Install the missing tools first."
      : !root.trim()
        ? "Requires an install location."
        : !isAbsolutePath(root, rules)
          ? `Requires an absolute path, e.g. ${examplePath(rules)}.`
          : drive && drive.freeBytes < required
            ? `Not enough space on ${driveName}. Requires ${formatBytes(required)}.`
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
            Installs the game files shared by all servers.
            {linux && " On Linux, servers run under Wine."}
          </p>

          {check.missingTools.length > 0 && (
            <div className={styles.section} style={{ display: "flex", flexDirection: "column", gap: 8 }}>
              {check.missingTools.map((sentence) => (
                <ErrorNote
                  key={sentence}
                  warn
                  message={sentence}
                  actionLabel={checking ? "Checking..." : "Check again"}
                  onAction={runCheck}
                />
              ))}
            </div>
          )}

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
                  onClick={() => setRoot(folderOn(d))}
                  title={`${formatBytes(d.freeBytes)} free of ${formatBytes(d.totalBytes)}`}
                >
                  {driveLabel(d.root, rules)} · {formatBytes(d.freeBytes)} free
                </button>
              ))}
            </div>
            {drive ? (
              <SpaceBar drive={drive} label={driveName} required={required} />
            ) : (
              root.trim() && <div className={ui.hint}>Free space unknown.</div>
            )}
          </div>

          <div className={styles.section}>
            <div className={ui.label}>Game files</div>
            {source === "client" ? (
              <>
                {check.clientGameDir && (
                  <div className={styles.detected}>
                    Copies the game from{" "}
                    <span className={styles.detectedPath}>{check.clientGameDir}</span>. Skips mods and other files that are not part of the game.
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
                          Copies every file. Uses {formatBytes(fullSize)}.
                        </span>
                      </button>
                      <button
                        role="radio"
                        aria-checked={copyMode === "hardlink"}
                        className={cn(ui.choice, copyMode === "hardlink" && ui.choiceSelected)}
                        onClick={() => setCopyMode("hardlink")}
                      >
                        <span className={ui.choiceTitle}>Hard link</span>
                        <span className={ui.choiceDesc}>
                          Hard-links the files instead of copying them. Steam cannot update Deadlock while a server is running.
                        </span>
                      </button>
                    </div>
                  </div>
                )}

                {check.steamcmdAvailable && (
                  <div style={{ marginTop: 10 }}>
                    <button className={ui.linkBtn} onClick={() => setSource("steamcmd")}>
                      Use SteamCMD instead
                    </button>
                  </div>
                )}
              </>
            ) : (
              <div className={styles.steamBox}>
                <div className={styles.detected}>
                  Downloads the game with SteamCMD. Requires a Steam account that owns Deadlock.
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
                <div className={ui.hint}>The password is not stored.</div>
                {check.clientGameDir && (
                  <div style={{ marginTop: 10 }}>
                    <button className={ui.linkBtn} onClick={() => setSource("client")}>
                      Copy from the installed game instead
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

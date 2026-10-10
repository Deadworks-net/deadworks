import { useEffect, useState, type ReactNode } from "react";
import { createPortal } from "react-dom";
import type { ServerSummary } from "@/lib/hosting";
import { cn } from "@/lib/utils";
import type { HostingActions } from "./use-hosting";
import { ErrorNote, useAction } from "./ui";
import ui from "./ui.module.css";
import styles from "./ServerPage.module.css";

/** The header is on screen while streaming; don't show the password in plain text. */
function maskPassword(command: string): string {
  return command.replace(/password "[^"]*"/, 'password "••••"');
}

type Control = "start" | "stop" | "restart";

const ICONS: Record<Control, ReactNode> = {
  start: <polygon points="6 3 20 12 6 21" fill="currentColor" />,
  stop: <rect x="4" y="4" width="16" height="16" rx="2" fill="currentColor" />,
  restart: (
    <g fill="none" stroke="currentColor" strokeWidth="2.4" strokeLinecap="round" strokeLinejoin="round">
      <polyline points="23 4 23 10 17 10" />
      <path d="M20.49 15a9 9 0 1 1-2.12-9.36L23 10" />
    </g>
  ),
};

function Icon({ name }: { name: Control }) {
  return (
    <svg width="14" height="14" viewBox="0 0 24 24" aria-hidden>
      {ICONS[name]}
    </svg>
  );
}

interface ServerControlsProps {
  server: ServerSummary;
  actions: HostingActions;
  /** Set while updates are held back; starting would fail anyway. */
  holdReason: string | null;
  /** The controls render here, beside the top-level tabs. The rail already shows the name and state. */
  toolbarSlot: HTMLElement | null;
}

export default function ServerControls({ server, actions, holdReason, toolbarSlot }: ServerControlsProps) {
  const { id } = server.config;
  const { state, network } = server.runtime;
  const control = useAction();
  const copy = useAction();
  const [lastControl, setLastControl] = useState<Control | null>(null);
  const [copied, setCopied] = useState(false);

  useEffect(() => {
    if (!copied) return;
    const t = window.setTimeout(() => setCopied(false), 2000);
    return () => window.clearTimeout(t);
  }, [copied]);

  const canStart = (state === "stopped" || state === "crashed") && !holdReason;
  /** Once a stop is asked for, only Start is shown. */
  const up = state === "running" || state === "starting";
  const canRestart = state === "running";
  const running = state === "running";
  const startBlocked =
    state === "waiting_for_deadworks"
      ? "Waiting for a Deadworks update for the new game build."
      : state === "updating"
        ? "Updating..."
        : holdReason;
  const copyHint = network.connectCommand
    ? `${maskPassword(network.connectCommand)}\nRun in the game's developer console (F7).`
    : "Getting address...";

  const doControl = (which: Control) => {
    setLastControl(which);
    control.run(() => actions[which](id));
  };

  const copyConnect = () =>
    copy.run(async () => {
      if (!network.connectCommand) return;
      await navigator.clipboard.writeText(network.connectCommand);
      setCopied(true);
      await actions.markShared(id);
    });

  return (
    <>
      {toolbarSlot &&
        createPortal(
          <div className={styles.controls}>
            {network.sdrIdChanged && (
              <span className={cn(ui.pill, ui.pillWarn)}>Address changed</span>
            )}
            {copied && <span className={ui.noteOk}>Copied</span>}
            {!up ? (
              <button
                className={ui.btnPrimary}
                disabled={!canStart || control.busy}
                title={startBlocked ?? undefined}
                onClick={() => doControl("start")}
              >
                <Icon name={state === "crashed" ? "restart" : "start"} />
                {state === "crashed" ? "Restart" : "Start"}
              </button>
            ) : (
              <>
                <button
                  className={cn(ui.btn, styles.iconControl, styles.stopControl)}
                  disabled={control.busy}
                  aria-label="Stop"
                  title="Stop"
                  onClick={() => doControl("stop")}
                >
                  <Icon name="stop" />
                </button>
                <button
                  className={cn(ui.btn, styles.iconControl)}
                  disabled={!canRestart || control.busy}
                  aria-label="Restart"
                  title="Restart"
                  onClick={() => doControl("restart")}
                >
                  <Icon name="restart" />
                </button>
              </>
            )}
            {up && (
              <button
                className={running ? ui.btnPrimary : ui.btn}
                disabled={!network.connectCommand || copy.busy}
                title={copyHint}
                onClick={copyConnect}
              >
                Copy connect command
              </button>
            )}
          </div>,
          toolbarSlot,
        )}

      {(control.error || copy.error) && (
        <div className={styles.header}>
          {control.error ? (
            <ErrorNote
              message={control.error}
              actionLabel="Try again"
              onAction={() => lastControl && doControl(lastControl)}
            />
          ) : (
            copy.error && (
              <ErrorNote message={`Couldn't copy: ${copy.error}`} actionLabel="Try again" onAction={copyConnect} />
            )
          )}
        </div>
      )}
    </>
  );
}

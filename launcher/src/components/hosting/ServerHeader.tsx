import { useEffect, useState } from "react";
import type { ServerSummary } from "@/lib/hosting";
import { cn } from "@/lib/utils";
import type { HostingActions } from "./use-hosting";
import { ErrorNote, StateBadge, useAction } from "./ui";
import ui from "./ui.module.css";
import styles from "./ServerPage.module.css";

/** The header is on screen while streaming; don't show the password in plain text. */
function maskPassword(command: string): string {
  return command.replace(/password "[^"]*"/, 'password "••••"');
}

interface ServerHeaderProps {
  server: ServerSummary;
  actions: HostingActions;
  /** Set while updates are held back; starting would fail anyway. */
  holdReason: string | null;
}

export default function ServerHeader({ server, actions, holdReason }: ServerHeaderProps) {
  const { id, name } = server.config;
  const { state, network } = server.runtime;
  const control = useAction();
  const copy = useAction();
  const [lastControl, setLastControl] = useState<"start" | "stop" | "restart" | null>(null);
  const [copied, setCopied] = useState(false);

  useEffect(() => {
    if (!copied) return;
    const t = window.setTimeout(() => setCopied(false), 2000);
    return () => window.clearTimeout(t);
  }, [copied]);

  const canStart = (state === "stopped" || state === "crashed") && !holdReason;
  const canStop = state === "running" || state === "starting";
  const canRestart = state === "running";
  const running = state === "running";
  const startBlocked =
    state === "waiting_for_deadworks"
      ? "Waiting for a Deadworks update that supports the new game version."
      : state === "updating"
        ? "Updating. The server can start when that's done."
        : holdReason;

  const doControl = (which: "start" | "stop" | "restart") => {
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
    <div className={styles.header}>
      <div className={styles.titleRow}>
        <span className={styles.name} title={name}>{name}</span>
        <StateBadge state={state} />
        {network.sdrIdChanged && (
          <span className={cn(ui.pill, ui.pillWarn)}>New address, share it again</span>
        )}
      </div>

      <div className={styles.controls}>
        {state === "stopped" || state === "crashed" || state === "waiting_for_deadworks" || state === "updating" ? (
          <button
            className={ui.btnPrimary}
            disabled={!canStart || control.busy}
            title={startBlocked ?? undefined}
            onClick={() => doControl("start")}
          >
            {state === "crashed" ? "Restart" : "Start"}
          </button>
        ) : (
          <>
            <button className={ui.btn} disabled={!canStop || control.busy} onClick={() => doControl("stop")}>
              Stop
            </button>
            <button className={ui.btn} disabled={!canRestart || control.busy} onClick={() => doControl("restart")}>
              Restart
            </button>
          </>
        )}

        <div className={styles.copyWrap}>
          {copied && <span className={ui.noteOk}>Copied</span>}
          <button
            className={running ? ui.btnPrimary : ui.btn}
            disabled={!network.connectCommand || copy.busy}
            title={network.connectCommand ? network.connectCommand : "Available once the server is running"}
            onClick={copyConnect}
          >
            Copy connect command
          </button>
        </div>
      </div>

      {control.error ? (
        <ErrorNote
          message={control.error}
          actionLabel="Try again"
          onAction={() => lastControl && doControl(lastControl)}
        />
      ) : copy.error ? (
        <ErrorNote message={`Couldn't copy: ${copy.error}`} actionLabel="Try again" onAction={copyConnect} />
      ) : (
        <div
          className={styles.help}
          title={network.connectCommand ? "If F7 does nothing, turn on the console in Deadlock's settings." : undefined}
        >
          {startBlocked && !canStop ? (
            startBlocked
          ) : network.connectCommand ? (
            <>
              <code className={styles.connectPreview}>{maskPassword(network.connectCommand)}</code>
              Players paste this into the in-game console (F7).
            </>
          ) : state === "starting" ? (
            "Getting the server's address..."
          ) : (
            "Start the server to get an address players can join."
          )}
        </div>
      )}
    </div>
  );
}

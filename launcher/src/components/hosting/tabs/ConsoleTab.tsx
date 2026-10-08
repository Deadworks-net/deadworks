import { memo, useLayoutEffect, useMemo, useRef, useState, type KeyboardEvent } from "react";
import { hosting, type ServerSummary } from "@/lib/hosting";
import { cn } from "@/lib/utils";
import type { KeyedLine, UseConsole } from "../use-console";
import { isLive } from "../format";
import { ErrorNote, Loading, useAction } from "../ui";
import ui from "../ui.module.css";
import styles from "./tabs.module.css";

const HISTORY_MAX = 100;

const Line = memo(function Line({ line }: { line: KeyedLine }) {
  return (
    <div className={cn(styles.line, line.kind === "in" && styles.lineIn, line.kind === "sys" && styles.lineSys)}>
      {line.text}
    </div>
  );
});

/** Just the log, auto-scrolling unless the user scrolled up. Also used on the crash panel. */
export function ConsoleLog({ lines, filter }: { lines: KeyedLine[]; filter?: string }) {
  const logRef = useRef<HTMLDivElement>(null);
  const [stuck, setStuck] = useState(true);
  const stuckRef = useRef(true);

  const visible = useMemo(() => {
    const f = filter?.trim().toLowerCase();
    return f ? lines.filter((l) => l.text.toLowerCase().includes(f)) : lines;
  }, [lines, filter]);

  useLayoutEffect(() => {
    const el = logRef.current;
    if (el && stuckRef.current) el.scrollTop = el.scrollHeight;
  }, [visible]);

  const onScroll = () => {
    const el = logRef.current;
    if (!el) return;
    const atBottom = el.scrollHeight - el.scrollTop - el.clientHeight < 24;
    if (atBottom !== stuckRef.current) {
      stuckRef.current = atBottom;
      setStuck(atBottom);
    }
  };

  const jump = () => {
    const el = logRef.current;
    if (!el) return;
    stuckRef.current = true;
    setStuck(true);
    el.scrollTop = el.scrollHeight;
  };

  return (
    <div className={styles.consoleWrap}>
      <div ref={logRef} className={styles.consoleLog} onScroll={onScroll} role="log" aria-live="off">
        {visible.length === 0 ? (
          <div className={styles.lineSys}>{filter?.trim() ? "No matching lines." : "No output yet."}</div>
        ) : (
          visible.map((l) => <Line key={l.key} line={l} />)
        )}
      </div>
      {!stuck && (
        <button className={cn(ui.btn, ui.btnSmall, styles.jumpBtn)} onClick={jump}>
          Jump to bottom
        </button>
      )}
    </div>
  );
}

export default function ConsoleTab({ server, log: con }: { server: ServerSummary; log: UseConsole }) {
  const [filter, setFilter] = useState("");
  const [command, setCommand] = useState("");
  const [history, setHistory] = useState<string[]>([]);
  // Index into history while browsing with the arrow keys; null = editing a new line.
  const [historyIndex, setHistoryIndex] = useState<number | null>(null);
  const send = useAction();
  const live = isLive(server.runtime.state);

  const submit = async () => {
    const cmd = command.trim();
    if (!cmd || !live) return;
    const ok = await send.run(() => hosting.sendCommand(server.config.id, cmd));
    if (ok) {
      setHistory((h) => [...h.filter((x) => x !== cmd), cmd].slice(-HISTORY_MAX));
      setHistoryIndex(null);
      setCommand("");
    }
  };

  const onKeyDown = (e: KeyboardEvent<HTMLInputElement>) => {
    if (e.key === "Enter") {
      e.preventDefault();
      submit();
    } else if (e.key === "ArrowUp" && history.length > 0) {
      e.preventDefault();
      const next = historyIndex == null ? history.length - 1 : Math.max(0, historyIndex - 1);
      setHistoryIndex(next);
      setCommand(history[next]);
    } else if (e.key === "ArrowDown" && historyIndex != null) {
      e.preventDefault();
      const next = historyIndex + 1;
      if (next >= history.length) {
        setHistoryIndex(null);
        setCommand("");
      } else {
        setHistoryIndex(next);
        setCommand(history[next]);
      }
    }
  };

  return (
    <div className={styles.tabFill}>
      <div className={styles.consoleBar}>
        <input
          className={ui.input}
          style={{ maxWidth: 260 }}
          placeholder="Filter"
          value={filter}
          onChange={(e) => setFilter(e.target.value)}
          aria-label="Filter console lines"
        />
        <span className={ui.note} style={{ marginLeft: "auto" }}>
          {con.lines.length.toLocaleString()} lines
        </span>
      </div>

      {con.error && <ErrorNote message={`Couldn't load earlier output: ${con.error}`} />}
      {con.loading ? <Loading /> : <ConsoleLog lines={con.lines} filter={filter} />}

      <div className={styles.consoleInput}>
        <input
          className={cn(ui.input, ui.mono)}
          placeholder={live ? "Command, e.g. status" : "Start the server to send commands"}
          value={command}
          disabled={!live}
          onChange={(e) => {
            setCommand(e.target.value);
            setHistoryIndex(null);
          }}
          onKeyDown={onKeyDown}
          spellCheck={false}
          aria-label="Console command"
        />
        <button className={ui.btn} onClick={submit} disabled={!live || !command.trim() || send.busy}>
          Send
        </button>
      </div>
      {send.error && <div className={ui.errorText}>{send.error}</div>}
    </div>
  );
}

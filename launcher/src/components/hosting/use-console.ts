import { useEffect, useRef, useState } from "react";
import { hosting, onConsoleLines, type ConsoleLine } from "@/lib/hosting";
import { errorMessage } from "./format";

export const CONSOLE_MAX_LINES = 5000;

/** A console line with a key that stays unique even if the backend's seq restarts. */
export interface KeyedLine extends ConsoleLine {
  key: number;
}

export interface UseConsole {
  lines: KeyedLine[];
  loading: boolean;
  error: string | null;
}

function cap(lines: KeyedLine[]): KeyedLine[] {
  return lines.length > CONSOLE_MAX_LINES ? lines.slice(lines.length - CONSOLE_MAX_LINES) : lines;
}

export function useConsole(id: string | null): UseConsole {
  const [lines, setLines] = useState<KeyedLine[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const nextKey = useRef(0);

  useEffect(() => {
    setLines([]);
    setError(null);
    if (!id) {
      setLoading(false);
      return;
    }
    setLoading(true);

    let cancelled = false;
    // Live lines that arrive before the history call returns wait here, then
    // get de-duplicated against the history by seq.
    let pending: ConsoleLine[] | null = [];
    const keyed = (ls: ConsoleLine[]): KeyedLine[] => ls.map((l) => ({ ...l, key: nextKey.current++ }));

    const unlisten = onConsoleLines((p) => {
      if (cancelled || p.id !== id) return;
      if (pending) {
        pending.push(...p.lines);
        return;
      }
      setLines((prev) => cap(prev.concat(keyed(p.lines))));
    });

    const settle = (history: ConsoleLine[]) => {
      const lastSeq = history.length ? history[history.length - 1].seq : -Infinity;
      const tail = (pending ?? []).filter((l) => l.seq > lastSeq);
      pending = null;
      setLines(cap(keyed(history.concat(tail))));
      setLoading(false);
    };

    hosting
      .consoleHistory(id)
      .then((history) => {
        if (!cancelled) settle(history);
      })
      .catch((e) => {
        if (cancelled) return;
        setError(errorMessage(e));
        settle([]);
      });

    return () => {
      cancelled = true;
      unlisten.then((fn) => fn()).catch(() => {});
    };
  }, [id]);

  return { lines, loading, error };
}

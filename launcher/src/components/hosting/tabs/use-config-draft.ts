import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import type { ServerConfig } from "@/lib/hosting";

function pick<K extends keyof ServerConfig>(config: ServerConfig, fields: readonly K[]): Pick<ServerConfig, K> {
  const out = {} as Pick<ServerConfig, K>;
  for (const f of fields) out[f] = config[f];
  return out;
}

/**
 * An editable copy of some ServerConfig fields. Follows the saved config while
 * untouched; `merged()` lays the edits over the latest saved config so fields
 * edited elsewhere (plugin toggles, content) aren't clobbered on save.
 */
export function useConfigDraft<K extends keyof ServerConfig>(config: ServerConfig, fields: readonly K[]) {
  const saved = useMemo(() => pick(config, fields), [config, fields]);
  const savedKey = JSON.stringify(saved);
  const [draft, setDraft] = useState(saved);
  const baseline = useRef(savedKey);

  useEffect(() => {
    const previous = baseline.current;
    baseline.current = savedKey;
    setDraft((current) =>
      JSON.stringify(current) === previous ? (JSON.parse(savedKey) as Pick<ServerConfig, K>) : current
    );
  }, [savedKey]);

  const dirty = JSON.stringify(draft) !== savedKey;

  const update = useCallback(<F extends K>(field: F, value: ServerConfig[F]) => {
    setDraft((d) => ({ ...d, [field]: value }));
  }, []);

  const reset = useCallback(() => setDraft(JSON.parse(savedKey) as Pick<ServerConfig, K>), [savedKey]);

  const merged = useCallback((): ServerConfig => ({ ...config, ...draft }), [config, draft]);

  return { draft, setDraft, update, dirty, reset, merged };
}

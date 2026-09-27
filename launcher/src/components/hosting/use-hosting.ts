import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import {
  hosting,
  onHostingChanged,
  onHostingTask,
  onServerRuntime,
  type HostingOverview,
  type InstallOptions,
  type NetworkMode,
  type ServerConfig,
  type ServerRuntime,
  type ServerSummary,
  type TaskKind,
  type TaskProgress,
} from "@/lib/hosting";
import { emptyRuntime, errorMessage } from "./format";

function startingTask(kind: TaskKind, label: string): TaskProgress {
  return {
    kind,
    stage: "starting",
    label,
    bytesDone: 0,
    bytesTotal: 0,
    filesDone: 0,
    filesTotal: 0,
    steamPrompt: null,
    error: null,
    modifiedFiles: [],
    finished: false,
  };
}

export interface HostingActions {
  reload: () => Promise<void>;
  install: (options: InstallOptions) => Promise<void>;
  verify: () => Promise<void>;
  applyUpdates: () => Promise<void>;
  uninstall: () => Promise<void>;
  cancelTask: () => Promise<void>;
  dismissTask: () => void;
  steamcmdInput: (value: string) => Promise<void>;
  /** Asks Steam to verify (and so restore) the client's modified files. */
  repairClient: () => Promise<void>;
  checkUpdates: () => Promise<void>;
  createServer: (name: string, network: NetworkMode) => Promise<ServerConfig>;
  updateServer: (config: ServerConfig) => Promise<ServerConfig>;
  duplicateServer: (id: string) => Promise<ServerConfig>;
  deleteServer: (id: string) => Promise<void>;
  start: (id: string) => Promise<void>;
  stop: (id: string) => Promise<void>;
  restart: (id: string) => Promise<void>;
  /** By PlayerInfo.slot; userId can be -1. */
  kick: (id: string, slot: number) => Promise<void>;
  markShared: (id: string) => Promise<void>;
  checkReachability: (id: string) => Promise<void>;
  setPluginEnabled: (id: string, pluginId: string, enabled: boolean) => Promise<ServerConfig>;
}

export interface UseHosting {
  overview: HostingOverview | null;
  servers: ServerSummary[];
  /** Install / update / verify / uninstall progress. Failed tasks stay until dismissed. */
  task: TaskProgress | null;
  loading: boolean;
  /** Only set when the overview itself can't be loaded. */
  error: string | null;
  actions: HostingActions;
}

/**
 * The HOST tab's state: the overview, kept current from the backend's events.
 * Backend-driven tasks (install, verify, ...) never throw from their actions;
 * their failures land in `task.error` so the progress view can offer a retry.
 */
export function useHosting(): UseHosting {
  const [overview, setOverview] = useState<HostingOverview | null>(null);
  const [task, setTask] = useState<TaskProgress | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const overviewRef = useRef(overview);
  overviewRef.current = overview;

  // Coalesce bursts of hosting://changed into at most one trailing refetch.
  const inFlight = useRef(false);
  const again = useRef(false);

  const reload = useCallback(async () => {
    if (inFlight.current) {
      again.current = true;
      return;
    }
    inFlight.current = true;
    try {
      do {
        again.current = false;
        try {
          const next = await hosting.overview();
          setOverview(next);
          setError(null);
          // Keep finished tasks (until dismissed) and our optimistic "starting"
          // placeholder even if the backend doesn't report them.
          setTask((prev) => next.task ?? (prev && (prev.finished || prev.stage === "starting") ? prev : null));
        } catch (e) {
          setError(errorMessage(e));
        }
      } while (again.current);
    } finally {
      inFlight.current = false;
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    reload();
    const subs = [
      onHostingChanged(() => {
        reload();
      }),
      onHostingTask((t) => {
        if (t.finished && !t.error && (t.kind === "install" || t.kind === "uninstall")) {
          setTask(null);
        } else {
          setTask(t);
        }
        if (t.finished) reload();
      }),
      onServerRuntime((r) => {
        const current = overviewRef.current;
        if (current && !current.servers.some((s) => s.config.id === r.id)) {
          reload();
          return;
        }
        setOverview((prev) =>
          prev
            ? {
                ...prev,
                servers: prev.servers.map((s) => (s.config.id === r.id ? { ...s, runtime: r } : s)),
              }
            : prev
        );
      }),
    ];
    return () => {
      subs.forEach((p) => p.then((unlisten) => unlisten()).catch(() => {}));
    };
  }, [reload]);

  const patchServer = useCallback(
    (id: string, fn: (s: ServerSummary) => ServerSummary) =>
      setOverview((prev) =>
        prev ? { ...prev, servers: prev.servers.map((s) => (s.config.id === id ? fn(s) : s)) } : prev
      ),
    []
  );

  const upsertConfig = useCallback((config: ServerConfig) => {
    setOverview((prev) => {
      if (!prev) return prev;
      const exists = prev.servers.some((s) => s.config.id === config.id);
      const servers = exists
        ? prev.servers.map((s) => (s.config.id === config.id ? { ...s, config } : s))
        : [...prev.servers, { config, runtime: emptyRuntime(config) }];
      return { ...prev, servers };
    });
  }, []);

  const patchRuntime = useCallback(
    (id: string, fn: (r: ServerRuntime) => ServerRuntime) =>
      patchServer(id, (s) => ({ ...s, runtime: fn(s.runtime) })),
    [patchServer]
  );

  const runTask = useCallback(
    async (kind: TaskKind, label: string, fn: () => Promise<void>) => {
      setTask(startingTask(kind, label));
      try {
        await fn();
      } catch (e) {
        const message = errorMessage(e);
        setTask((prev) => ({
          ...(prev ?? startingTask(kind, label)),
          kind,
          error: prev?.error ?? message,
          finished: true,
          steamPrompt: null,
        }));
      }
    },
    []
  );

  const actions = useMemo<HostingActions>(
    () => ({
      reload,
      install: (options) => runTask("install", "Getting ready", () => hosting.install(options)),
      verify: () => runTask("verify", "Checking game files", () => hosting.verify()),
      applyUpdates: () => runTask("update", "Applying updates", () => hosting.applyUpdates()),
      uninstall: () => runTask("uninstall", "Removing hosting files", () => hosting.uninstall()),
      cancelTask: () => hosting.cancelTask(),
      dismissTask: () => setTask(null),
      steamcmdInput: async (value) => {
        await hosting.steamcmdInput(value);
        setTask((prev) => (prev ? { ...prev, steamPrompt: null } : prev));
      },
      repairClient: () => hosting.repairClient(),
      checkUpdates: async () => {
        const updates = await hosting.checkUpdates();
        setOverview((prev) => (prev ? { ...prev, updates } : prev));
      },
      createServer: async (name, network) => {
        const config = await hosting.createServer(name, network);
        upsertConfig(config);
        return config;
      },
      updateServer: async (config) => {
        const saved = await hosting.updateServer(config);
        upsertConfig(saved);
        return saved;
      },
      duplicateServer: async (id) => {
        const config = await hosting.duplicateServer(id);
        upsertConfig(config);
        return config;
      },
      deleteServer: async (id) => {
        await hosting.deleteServer(id);
        setOverview((prev) =>
          prev ? { ...prev, servers: prev.servers.filter((s) => s.config.id !== id) } : prev
        );
      },
      start: (id) => hosting.start(id),
      stop: (id) => hosting.stop(id),
      restart: (id) => hosting.restart(id),
      kick: async (id, slot) => {
        await hosting.kick(id, slot);
        patchRuntime(id, (r) => ({ ...r, players: r.players.filter((p) => p.slot !== slot) }));
      },
      markShared: async (id) => {
        await hosting.markShared(id);
        patchRuntime(id, (r) => ({ ...r, network: { ...r.network, sdrIdChanged: false } }));
      },
      checkReachability: async (id) => {
        patchRuntime(id, (r) => ({ ...r, network: { ...r.network, reachability: "checking" } }));
        try {
          const network = await hosting.checkReachability(id);
          patchRuntime(id, (r) => ({ ...r, network }));
        } catch (e) {
          patchRuntime(id, (r) => ({ ...r, network: { ...r.network, reachability: "error" } }));
          throw e;
        }
      },
      setPluginEnabled: async (id, pluginId, enabled) => {
        const config = await hosting.setPluginEnabled(id, pluginId, enabled);
        upsertConfig(config);
        return config;
      },
    }),
    [reload, runTask, upsertConfig, patchRuntime]
  );

  return {
    overview,
    servers: overview?.servers ?? [],
    task,
    loading,
    error,
    actions,
  };
}

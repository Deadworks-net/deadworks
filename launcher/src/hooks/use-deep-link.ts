import { useCallback, useEffect, useRef, useState } from "react";
import { invoke } from "@tauri-apps/api/core";
import { serverInfo } from "@/lib/tauri";
import { listen, type UnlistenFn } from "@tauri-apps/api/event";
import type { Server } from "@/lib/types";

type DeepLinkPayload =
  | { kind: "id"; value: string }
  | { kind: "ip"; value: string }
  | { kind: "error"; value: string };

export interface DeepLinkRequest {
  requestId: number;
  server: Server | null;
  error: string | null;
}

export function useDeepLink(apiUrl: string) {
  const [request, setRequest] = useState<DeepLinkRequest | null>(null);
  const counterRef = useRef(0);
  const apiUrlRef = useRef(apiUrl);

  useEffect(() => {
    apiUrlRef.current = apiUrl;
  }, [apiUrl]);

  const resolvePayload = useCallback(async (p: DeepLinkPayload) => {
    const requestId = ++counterRef.current;
    if (p.kind === "error") {
      setRequest({ requestId, server: null, error: p.value });
      return;
    }
    // A link by address is answered by the server itself, so it works for any server that is
    // up, whether or not the Deadworks API lists it or can be reached. The API is only asked
    // when the server stays silent, which is how one started with -nomaster looks.
    if (p.kind === "ip") {
      const info = await serverInfo(p.value).catch(() => null);
      if (info) {
        setRequest({
          requestId,
          error: null,
          server: {
            id: "",
            name: info.name || p.value,
            address: p.value,
            raw_address: p.value,
            country: "",
            online: true,
            player_count: info.players,
            max_players: info.max_players,
            map: info.map,
            players: [],
            mods: [],
            content_addons: [],
            extra_maps: [],
            last_heartbeat: null,
          },
        });
        return;
      }
    }
    const base = apiUrlRef.current;
    const url =
      p.kind === "id"
        ? `${base}/api/servers/${encodeURIComponent(p.value)}`
        : `${base}/api/servers/lookup?address=${encodeURIComponent(p.value)}`;
    try {
      const res = await fetch(url, { cache: "no-store" });
      if (res.status === 404) {
        const msg =
          p.kind === "ip"
            ? `No online server found at ${p.value}`
            : `Server ${p.value} not found`;
        setRequest({ requestId, server: null, error: msg });
        return;
      }
      if (!res.ok) {
        setRequest({
          requestId,
          server: null,
          error: `Lookup failed (HTTP ${res.status})`,
        });
        return;
      }
      const server = (await res.json()) as Server;
      setRequest({ requestId, server, error: null });
    } catch (e) {
      setRequest({ requestId, server: null, error: String(e) });
    }
  }, []);

  useEffect(() => {
    let unlisten: UnlistenFn | null = null;

    (async () => {
      unlisten = await listen<DeepLinkPayload>("deep-link://connect", (evt) => {
        resolvePayload(evt.payload);
      });
      // Idempotent: backend sets ready=true and replays any pending URLs via
      // the same event we just subscribed to.
      await invoke("deep_link_ready");
    })();

    return () => {
      unlisten?.();
    };
  }, [resolvePayload]);

  const clear = useCallback(() => setRequest(null), []);
  return { request, clear };
}

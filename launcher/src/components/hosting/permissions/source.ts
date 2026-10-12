// The Permissions tab's data source for a local server: the permission files on disk, read and written
// through the backend. The files stay the source of truth, so grants made in game with dw_role_grant
// and hand edits show up as they are.

import { hosting, type PermissionsSnapshot } from "@/lib/hosting";
import {
  applyChange,
  DEFAULT_ROLES,
  OVERRIDES_PATH,
  PLAYERS_PATH,
  renderRoles,
  ROLES_PATH,
  type PermissionChange,
  type PermissionFiles,
} from "./permissions";

export interface AdminsSnapshot {
  /** The files as shown: until the server has written roles.jsonc, the roles Deadworks will create. */
  files: PermissionFiles;
  /** Which of roles.jsonc, players.jsonc and overrides.jsonc are not on disk yet. */
  missing: string[];
  running: boolean;
  writable: boolean;
  /** roles.jsonc exists, so Deadworks has run on this server at least once. */
  started: boolean;
  localSteamId: string | null;
  localSteamName: string | null;
}

export interface ActResult {
  changed: boolean;
  /** A file-save whose file changed since it was opened; `contents` is the text on disk now. */
  conflict?: boolean;
  contents?: string | null;
  /** The running server confirmed it reloaded. */
  live?: boolean;
  liveReason?: string | null;
}

export interface PermissionsSource {
  load: () => Promise<AdminsSnapshot>;
  act: (change: PermissionChange) => Promise<ActResult>;
}

const EDITABLE = [ROLES_PATH, PLAYERS_PATH, OVERRIDES_PATH];

function toSnapshot(raw: PermissionsSnapshot): AdminsSnapshot {
  const shown = { ...raw.files };
  if (shown[ROLES_PATH] == null) shown[ROLES_PATH] = renderRoles(DEFAULT_ROLES);
  return {
    files: shown,
    // Lets the JSON editor create a file that is not there yet instead of treating the defaults as on disk.
    missing: EDITABLE.filter((p) => raw.files[p] == null),
    running: raw.running,
    writable: true,
    started: raw.files[ROLES_PATH] != null,
    localSteamId: raw.localSteamId,
    localSteamName: raw.localSteamName,
  };
}

type Attempt =
  | { stale: false; result: ActResult }
  /** A file changed between reading and writing; `contents` has what the differing files hold now. */
  | { stale: true; contents: Record<string, string | null> };

export function localSource(serverId: string): PermissionsSource {
  // The change is worked out from the files as they are right now (not the ones on screen), so an
  // in-game grant made a moment ago is kept, not overwritten.
  const attempt = async (change: PermissionChange): Promise<Attempt> => {
    const raw = await hosting.permissions(serverId);
    const planned = applyChange(raw.files, change);
    if (planned.conflict) return { stale: false, result: { changed: false, conflict: true, contents: planned.contents } };
    const paths = Object.keys(planned.files);
    if (paths.length === 0) return { stale: false, result: { changed: false } };

    const expected: Record<string, string | null> = {};
    for (const path of paths) expected[path] = raw.files[path] ?? null;
    const written = await hosting.writePermissions(serverId, expected, planned.files);
    if (written.conflict) return { stale: true, contents: written.contents };
    return { stale: false, result: { changed: written.changed, live: written.live, liveReason: written.liveReason } };
  };

  return {
    load: async () => toSnapshot(await hosting.permissions(serverId)),
    act: async (change) => {
      let outcome = await attempt(change);
      if (!outcome.stale) return outcome.result;

      if (change.action === "file-save") {
        // The editor decides: load what is on disk now, or keep its own text and save over it.
        const now =
          change.path in outcome.contents
            ? outcome.contents[change.path]
            : (await hosting.permissions(serverId)).files[change.path];
        return { changed: false, conflict: true, contents: now ?? null };
      }

      // A structured change is recomputed from the fresh files, so one retry is safe.
      outcome = await attempt(change);
      if (!outcome.stale) return outcome.result;
      throw new Error("The permission files changed while saving. Try again.");
    },
  };
}

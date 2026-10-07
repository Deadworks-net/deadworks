import {
  Component,
  Fragment,
  Suspense,
  lazy,
  useCallback,
  useEffect,
  useMemo,
  useRef,
  useState,
  type ReactNode,
} from "react";
import type { PlayerInfo, ServerSummary } from "@/lib/hosting";
import { cn } from "@/lib/utils";
import { errorMessage, isLive } from "../format";
import { ConfirmDialog, ErrorNote, Loading, Modal } from "../ui";
import {
  DEFAULT_ROLE,
  OVERRIDES_PATH,
  PLAYERS_PATH,
  ROLES_PATH,
  buildModel,
  declaredPermissions,
  describe,
  explain,
  findRole,
  inheritChain,
  parseGrant,
  parseSteamId,
  playerImmunity,
  resolveCommand,
  roleImmunity,
  sameName,
  steam3,
  whoCan,
  type CommandInfo,
  type PermissionChange,
  type PermissionModel,
  type Person,
  type PluginInfo,
  type Problem,
} from "../permissions/permissions";
import { localSource, type ActResult, type AdminsSnapshot } from "../permissions/source";
import ui from "../ui.module.css";
import styles from "./tabs.module.css";
import css from "./AdminsTab.module.css";

// CodeMirror is most of the bundle; load it only when someone opens a file.
const PermissionsJsonDialog = lazy(() => import("../permissions/PermissionsJsonDialog"));

/** A player to open the add dialog for, e.g. from "Make admin" on the Overview tab. */
export interface AdminPrefill {
  steamId: string;
  name: string;
}

interface AdminsTabProps {
  server: ServerSummary;
  /** Opens the add dialog (or the player's row, when already listed) once the files are read. */
  prefill: AdminPrefill | null;
  onPrefillUsed: () => void;
}

type View = "people" | "roles" | "commands" | "check" | "errors";
const VIEWS: [View, string][] = [
  ["people", "People"],
  ["roles", "Roles"],
  ["commands", "Commands"],
  ["check", "Check"],
  ["errors", "Errors"],
];

type DialogState =
  | { kind: "player"; person?: Person; prefill?: AdminPrefill }
  | { kind: "role"; name?: string }
  | { kind: "override"; plugin: PluginInfo; command: CommandInfo }
  | { kind: "json"; path: string }
  | { kind: "confirm"; title: string; body: string; action: string; run: () => Promise<unknown> };

type PlayerSave = Omit<Extract<PermissionChange, { action: "player-save" }>, "action">;
type RoleSave = Omit<Extract<PermissionChange, { action: "role-save" }>, "action">;
type OverrideSave = Omit<Extract<PermissionChange, { action: "override-save" }>, "action">;

const displayName = (p: Person): string => p.entry.name || p.id || p.key;
const plural = (n: number, one: string, many = `${one}s`): string => `${n} ${n === 1 ? one : many}`;

/** Hand-edited files can hold shapes the views don't expect; show a way to fix them instead of a blank window. */
class ViewBoundary extends Component<
  { resetKey: unknown; fallback: ReactNode; children: ReactNode },
  { failed: boolean }
> {
  state = { failed: false };

  static getDerivedStateFromError() {
    return { failed: true };
  }

  componentDidUpdate(prev: { resetKey: unknown }) {
    if (this.state.failed && prev.resetKey !== this.props.resetKey) this.setState({ failed: false });
  }

  render() {
    return this.state.failed ? this.props.fallback : this.props.children;
  }
}

export default function AdminsTab({ server, prefill, onPrefillUsed }: AdminsTabProps) {
  const id = server.config.id;
  const state = server.runtime.state;
  const source = useMemo(() => localSource(id), [id]);
  const [snapshot, setSnapshot] = useState<AdminsSnapshot | null>(null);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  /** Why the last save didn't reach the running server. */
  const [liveReason, setLiveReason] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [view, setView] = useState<View>("people");
  const [dialog, setDialog] = useState<DialogState | null>(null);

  const loadSeq = useRef(0);
  const reload = useCallback(async () => {
    const seq = ++loadSeq.current;
    try {
      const next = await source.load();
      if (seq !== loadSeq.current) return;
      setSnapshot(next);
      setLoadError(null);
    } catch (e) {
      if (seq === loadSeq.current) setLoadError(errorMessage(e));
    }
  }, [source]);

  // Deadworks writes roles.jsonc and each plugin's command list as the server starts.
  useEffect(() => {
    reload();
  }, [reload, state]);

  const built = useMemo(() => {
    if (!snapshot) return null;
    try {
      return buildModel(snapshot.files);
    } catch {
      return null;
    }
  }, [snapshot]);

  const act = useCallback(
    async (change: PermissionChange): Promise<ActResult> => {
      setBusy(true);
      setError(null);
      setLiveReason(null);
      try {
        const result = await source.act(change);
        await reload();
        setDialog(null);
        // Saved, but a running server did not pick it up: say so, since in-game access is what matters.
        if (result.changed && !result.live && result.liveReason) setLiveReason(result.liveReason);
        return result;
      } catch (e) {
        setError(errorMessage(e));
        throw e;
      } finally {
        setBusy(false);
      }
    },
    [source, reload]
  );

  useEffect(() => {
    if (!prefill || !built) return;
    const id64 = parseSteamId(prefill.steamId);
    const listed = id64 ? built.people.find((p) => p.id === id64) : undefined;
    setView("people");
    setDialog(listed ? { kind: "player", person: listed } : { kind: "player", prefill });
    onPrefillUsed();
  }, [prefill, built, onPrefillUsed]);

  if (!snapshot) {
    return (
      <div className={styles.tab}>
        {loadError ? (
          <ErrorNote
            message={`We couldn't read the permission files: ${loadError}`}
            actionLabel="Try again"
            onAction={reload}
          />
        ) : (
          <Loading label="Reading the permission files..." />
        )}
      </div>
    );
  }

  const openJson = (path: string) => setDialog({ kind: "json", path });
  const closeDialog = () => {
    setDialog(null);
    setError(null);
  };
  const unreadable = <Unreadable onJson={openJson} />;
  const jsonDialog = dialog?.kind === "json" && (
    <Suspense fallback={null}>
      <PermissionsJsonDialog
        key={dialog.path}
        path={dialog.path}
        snapshot={snapshot}
        source={source}
        onClose={() => setDialog(null)}
        onSaved={async (result) => {
          await reload();
          setDialog(null);
          setError(null);
          setLiveReason(result.changed && !result.live ? (result.liveReason ?? null) : null);
        }}
      />
    </Suspense>
  );

  const running = state === "running";
  const statusLine = (
    <div className={css.status}>
      <span>
        {running
          ? "The server is running. Changes apply right away."
          : "Changes apply the next time the server starts."}
        {!snapshot.started &&
          " Deadworks creates these files the first time the server starts. Until then you see the roles it starts with."}
      </span>
      <button type="button" className={ui.linkBtn} onClick={reload}>
        Reload
      </button>
    </div>
  );

  if (!built) {
    return (
      <div className={styles.tab}>
        <div className={styles.header}>
          <span className={styles.headerTitle}>Permissions and admins</span>
        </div>
        {statusLine}
        {unreadable}
        {jsonDialog}
      </div>
    );
  }

  const model = built;
  const writable = snapshot.writable && !busy;
  const fileErrors = model.problems.filter((p) => p.level === "error");
  const counts: Record<View, number> = {
    people: model.people.length,
    roles: Object.keys(model.roles).length,
    commands: model.plugins.reduce((n, p) => n + p.commands.length, 0),
    check: 0,
    errors: model.problems.length,
  };

  const localId = snapshot.localSteamId ? parseSteamId(snapshot.localSteamId) : null;
  const me: AdminPrefill | null =
    localId && !model.people.some((p) => p.id === localId)
      ? { steamId: localId, name: snapshot.localSteamName ?? "" }
      : null;
  // A save that was not picked up only matters while there is a server to pick it up.
  const showLiveReason = liveReason != null && (isLive(state) || snapshot.running);

  return (
    <div className={styles.tab}>
      <div className={styles.header}>
        <span className={styles.headerTitle}>Permissions and admins</span>
        <div className={css.views} role="tablist" aria-label="Admin views">
          {VIEWS.map(([key, title]) => (
            <button
              key={key}
              type="button"
              role="tab"
              aria-selected={view === key}
              className={cn(
                css.view,
                view === key && css.viewActive,
                key === "errors" && counts.errors > 0 && view !== key && css.viewWarn
              )}
              onClick={() => setView(key)}
            >
              {title}
              {counts[key] > 0 && <span className={css.viewCount}>{counts[key]}</span>}
            </button>
          ))}
        </div>
      </div>

      {statusLine}

      <div className={css.notices}>
        {loadError && (
          <ErrorNote
            message={`We couldn't read the permission files: ${loadError}`}
            actionLabel="Try again"
            onAction={reload}
          />
        )}
        {showLiveReason && (
          <ErrorNote warn message={liveReason} actionLabel="OK" onAction={() => setLiveReason(null)} />
        )}
        {error && !dialog && <ErrorNote message={error} actionLabel="OK" onAction={() => setError(null)} />}
        {fileErrors.map(({ text, path }, i) => (
          <ErrorNote
            key={`${i}:${text}`}
            message={text}
            actionLabel={path ? "Open the file" : undefined}
            onAction={path ? () => openJson(path) : undefined}
          />
        ))}
      </div>

      <ViewBoundary resetKey={snapshot} fallback={unreadable}>
        {view === "people" && (
          <People
            model={model}
            writable={writable}
            canAddMe={me != null}
            onJson={() => openJson(PLAYERS_PATH)}
            onAdd={() => setDialog({ kind: "player" })}
            onAddMe={() => me && setDialog({ kind: "player", prefill: me })}
            onEdit={(person) => setDialog({ kind: "player", person })}
            onRemove={(p) =>
              setDialog({
                kind: "confirm",
                title: `Remove ${displayName(p)}?`,
                body: "They lose every role and permission on this server, and keep only what the default role gives everyone.",
                action: "Remove",
                run: () => act({ action: "player-remove", steamId: p.id }),
              })
            }
          />
        )}
        {view === "roles" && (
          <Roles
            model={model}
            writable={writable}
            onJson={() => openJson(ROLES_PATH)}
            onAdd={() => setDialog({ kind: "role" })}
            onEdit={(name) => setDialog({ kind: "role", name })}
            onDelete={(name) =>
              setDialog({
                kind: "confirm",
                title: `Delete role ${name}?`,
                body: "Roles that inherit it stop doing so.",
                action: "Delete",
                run: () => act({ action: "role-delete", name }),
              })
            }
          />
        )}
        {view === "commands" && (
          <Commands
            model={model}
            writable={writable}
            onJson={openJson}
            onOverride={(plugin, command) => setDialog({ kind: "override", plugin, command })}
          />
        )}
        {view === "check" && <Check model={model} />}
        {view === "errors" && <Problems problems={model.problems} />}

        {dialog?.kind === "player" && (
          <PlayerDialog
            key={dialog.person?.key ?? dialog.prefill?.steamId ?? "new"}
            model={model}
            person={dialog.person}
            prefill={dialog.prefill}
            connected={server.runtime.players}
            busy={busy}
            error={error}
            onClose={closeDialog}
            onSave={(v) => act({ action: "player-save", ...v }).catch(() => {})}
          />
        )}
        {dialog?.kind === "role" && (
          <RoleDialog
            model={model}
            name={dialog.name}
            busy={busy}
            error={error}
            onClose={closeDialog}
            onSave={(v) => act({ action: "role-save", ...v }).catch(() => {})}
          />
        )}
        {dialog?.kind === "override" && (
          <OverrideDialog
            model={model}
            plugin={dialog.plugin}
            command={dialog.command}
            busy={busy}
            error={error}
            onClose={closeDialog}
            onSave={(v) => act({ action: "override-save", ...v }).catch(() => {})}
          />
        )}
      </ViewBoundary>

      {jsonDialog}
      {dialog?.kind === "confirm" && (
        <ConfirmDialog
          title={dialog.title}
          message={dialog.body}
          confirmLabel={dialog.action}
          danger
          onConfirm={dialog.run}
          onClose={closeDialog}
        />
      )}
    </div>
  );
}

function Unreadable({ onJson }: { onJson: (path: string) => void }) {
  return (
    <div className={cn(styles.list, styles.listEmpty)}>
      <div className={css.emptyTitle}>These files can't be shown</div>
      <p>One of the permission files holds something this tab doesn't understand. Open the file to fix it.</p>
      <div className={css.emptyActions}>
        {[ROLES_PATH, PLAYERS_PATH, OVERRIDES_PATH].map((path) => (
          <button key={path} type="button" className={cn(ui.btn, css.fileBtn)} onClick={() => onJson(path)}>
            {path.slice(path.lastIndexOf("/") + 1)}
          </button>
        ))}
      </div>
    </div>
  );
}

function SectionHead({ title, children }: { title: string; children?: ReactNode }) {
  return (
    <div className={css.sectionHead}>
      <span className={styles.headerTitle}>{title}</span>
      <div className={css.tools}>{children}</div>
    </div>
  );
}

const None = ({ text = "None" }: { text?: string }) => <span className={css.muted}>{text}</span>;

/** A grant or permission name. Long ones wrap after a dot, never inside a part or after the leading "-". */
function Grant({ value }: { value: string }) {
  const g = parseGrant(value);
  const parts = value.split(".");
  return (
    <code className={cn(css.grant, g?.deny && css.grantDeny, !g && css.grantBad)}>
      {parts.map((part, i) => (
        <Fragment key={i}>
          <span className={css.grantPart}>{i < parts.length - 1 ? `${part}.` : part}</span>
          {i < parts.length - 1 && <wbr />}
        </Fragment>
      ))}
    </code>
  );
}

function RoleChip({ name, model }: { name: string; model: PermissionModel }) {
  const defined = Object.keys(model.roles).some((k) => sameName(k, name));
  return <span className={cn(styles.rolePill, !defined && css.roleMissing)}>{name}</span>;
}

function People({
  model,
  writable,
  canAddMe,
  onJson,
  onAdd,
  onAddMe,
  onEdit,
  onRemove,
}: {
  model: PermissionModel;
  writable: boolean;
  /** The Steam account signed in on this PC is known and not listed yet. */
  canAddMe: boolean;
  onJson: () => void;
  onAdd: () => void;
  onAddMe: () => void;
  onEdit: (person: Person) => void;
  onRemove: (person: Person) => void;
}) {
  const [query, setQuery] = useState("");
  const q = query.trim().toLowerCase();
  const rows = model.people.filter(
    (p) => !q || [p.entry.name, p.key, ...p.entry.roles].some((v) => String(v || "").toLowerCase().includes(q))
  );
  const addButtons = (
    <>
      {canAddMe && (
        <button type="button" className={ui.btn} disabled={!writable} onClick={onAddMe}>
          Add me
        </button>
      )}
      <button type="button" className={ui.btnPrimary} disabled={!writable} onClick={onAdd}>
        Add admin
      </button>
    </>
  );

  return (
    <section>
      <SectionHead title="People">
        <input
          className={cn(ui.input, css.search)}
          type="search"
          placeholder="Search name, SteamID or role"
          value={query}
          onChange={(e) => setQuery(e.target.value)}
          aria-label="Search people"
        />
        <button type="button" className={ui.btn} onClick={onJson}>
          JSON
        </button>
        {addButtons}
      </SectionHead>

      {model.people.length > 0 ? (
        <div className={cn(styles.list, css.scroll)}>
          <table className={cn(ui.table, css.table)}>
            <thead>
              <tr>
                <th>Player</th>
                <th>Roles</th>
                <th>Own permissions</th>
                <th>Immunity</th>
                <th aria-label="Actions" />
              </tr>
            </thead>
            <tbody>
              {rows.map((p) => (
                <tr key={p.key}>
                  <td>
                    <div className={css.player}>
                      <strong>{p.entry.name || <None text="No name" />}</strong>
                      <code>{p.id || p.key}</code>
                    </div>
                  </td>
                  <td>
                    <div className={css.chips}>
                      {p.entry.roles.length > 0 ? (
                        p.entry.roles.map((r) => <RoleChip key={r} name={r} model={model} />)
                      ) : (
                        <None />
                      )}
                    </div>
                  </td>
                  <td>
                    <div className={css.chips}>
                      {p.entry.permissions.length > 0 ? (
                        p.entry.permissions.map((g) => <Grant key={g} value={g} />)
                      ) : (
                        <None />
                      )}
                    </div>
                  </td>
                  <td className={ui.num}>{playerImmunity(model.roles, p.entry).value}</td>
                  <td>
                    <div className={css.rowActions}>
                      <button
                        type="button"
                        className={cn(ui.btn, ui.btnSmall)}
                        disabled={!writable}
                        onClick={() => onEdit(p)}
                      >
                        Edit
                      </button>
                      <button
                        type="button"
                        className={cn(ui.btn, ui.btnSmall, ui.btnDanger)}
                        disabled={!writable || !p.id}
                        title={p.id ? undefined : "This entry is not a SteamID. Fix it in the JSON."}
                        onClick={() => onRemove(p)}
                      >
                        Remove
                      </button>
                    </div>
                  </td>
                </tr>
              ))}
              {rows.length === 0 && (
                <tr>
                  <td colSpan={5} className={css.muted}>
                    Nobody matches "{query}".
                  </td>
                </tr>
              )}
            </tbody>
          </table>
        </div>
      ) : (
        <div className={cn(styles.list, styles.listEmpty)}>
          <div className={css.emptyTitle}>No admins yet</div>
          <p>Everyone has only what the default role gives. Add yourself or someone you trust.</p>
          <div className={css.emptyActions}>{addButtons}</div>
        </div>
      )}
      <div className={ui.hint}>{plural(model.people.length, "listed player")}</div>
    </section>
  );
}

function Roles({
  model,
  writable,
  onJson,
  onAdd,
  onEdit,
  onDelete,
}: {
  model: PermissionModel;
  writable: boolean;
  onJson: () => void;
  onAdd: () => void;
  onEdit: (name: string) => void;
  onDelete: (name: string) => void;
}) {
  const holders = (name: string) => model.people.filter((p) => p.entry.roles.some((r) => sameName(r, name)));
  return (
    <section>
      <SectionHead title="Roles">
        <button type="button" className={ui.btn} onClick={onJson}>
          JSON
        </button>
        <button type="button" className={ui.btnPrimary} disabled={!writable} onClick={onAdd}>
          New role
        </button>
      </SectionHead>
      <div className={cn(styles.list, css.scroll)}>
        <table className={cn(ui.table, css.table)}>
          <thead>
            <tr>
              <th>Role</th>
              <th>Permissions</th>
              <th>Inherits</th>
              <th>Immunity</th>
              <th>Held by</th>
              <th aria-label="Actions" />
            </tr>
          </thead>
          <tbody>
            {Object.entries(model.roles).map(([name, role]) => {
              const held = holders(name);
              const chain = inheritChain(model.roles, name);
              const grants = role?.permissions || [];
              const isDefault = sameName(name, DEFAULT_ROLE);
              return (
                <tr key={name}>
                  <td>
                    <RoleChip name={name} model={model} />
                  </td>
                  <td>
                    <div className={css.chips}>
                      {grants.length > 0 ? (
                        grants.map((g) => <Grant key={g} value={g} />)
                      ) : (
                        <None text={chain.length > 0 ? "Nothing of its own" : "None"} />
                      )}
                    </div>
                  </td>
                  <td>
                    <div className={css.chips}>
                      {chain.length > 0 ? (
                        chain.map((r, i) => <RoleChip key={r + i} name={r} model={model} />)
                      ) : (
                        <None />
                      )}
                    </div>
                  </td>
                  <td className={ui.num}>
                    {roleImmunity(model.roles, name)}
                    {!Number.isInteger(role?.immunity) && chain.length > 0 && (
                      <div className={css.muted}>inherited</div>
                    )}
                  </td>
                  <td className={css.held}>
                    {isDefault ? (
                      <None text="Everyone" />
                    ) : held.length > 0 ? (
                      held.map(displayName).join(", ")
                    ) : (
                      <None text="Nobody" />
                    )}
                  </td>
                  <td>
                    <div className={css.rowActions}>
                      <button
                        type="button"
                        className={cn(ui.btn, ui.btnSmall)}
                        disabled={!writable}
                        onClick={() => onEdit(name)}
                      >
                        Edit
                      </button>
                      <button
                        type="button"
                        className={cn(ui.btn, ui.btnSmall, ui.btnDanger)}
                        style={isDefault ? { visibility: "hidden" } : undefined}
                        disabled={!writable || held.length > 0 || isDefault}
                        title={held.length > 0 ? "Take this role from everyone first" : undefined}
                        onClick={() => onDelete(name)}
                      >
                        Delete
                      </button>
                    </div>
                  </td>
                </tr>
              );
            })}
          </tbody>
        </table>
      </div>
      <div className={ui.hint}>{plural(Object.keys(model.roles).length, "role")}</div>
    </section>
  );
}

function Commands({
  model,
  writable,
  onJson,
  onOverride,
}: {
  model: PermissionModel;
  writable: boolean;
  onJson: (path: string) => void;
  onOverride: (plugin: PluginInfo, command: CommandInfo) => void;
}) {
  const [query, setQuery] = useState("");
  const q = query.trim().toLowerCase();

  if (model.plugins.length === 0) {
    return (
      <div className={cn(styles.list, styles.listEmpty)}>
        <div className={css.emptyTitle}>No command list yet</div>
        <p>
          Deadworks writes the list of commands each plugin adds when the server starts. Start the server once, then
          come back.
        </p>
        <div className={css.emptyActions}>
          <button type="button" className={ui.btn} onClick={() => onJson(OVERRIDES_PATH)}>
            Overrides JSON
          </button>
        </div>
      </div>
    );
  }

  const sections = model.plugins
    .map((plugin) => ({
      plugin,
      commands: plugin.commands.filter(
        (c) =>
          !q ||
          [c.name, ...(c.aliases ?? []), c.description, c.permission].some((v) =>
            String(v || "").toLowerCase().includes(q)
          )
      ),
    }))
    .filter((s) => !q || s.commands.length > 0);

  return (
    <>
      <SectionHead title="Commands">
        <input
          className={cn(ui.input, css.search)}
          type="search"
          placeholder="Search commands"
          value={query}
          onChange={(e) => setQuery(e.target.value)}
          aria-label="Search commands"
        />
        <button type="button" className={ui.btn} onClick={() => onJson(OVERRIDES_PATH)}>
          Overrides JSON
        </button>
      </SectionHead>

      {sections.length === 0 && (
        <div className={cn(styles.list, styles.listEmpty)}>No command matches "{query}".</div>
      )}

      {sections.map(({ plugin, commands }) => (
        <section key={plugin.path} className={css.plugin}>
          <SectionHead title={plugin.core ? "Deadworks core" : plugin.plugin}>
            <button
              type="button"
              className={cn(ui.btn, ui.btnSmall)}
              title="Deadworks writes this file. It opens read-only."
              onClick={() => onJson(plugin.path)}
            >
              JSON
            </button>
          </SectionHead>
          <div className={cn(styles.list, css.scroll)}>
            <table className={cn(ui.table, css.table)}>
              <thead>
                <tr>
                  <th>Command</th>
                  <th>What it does</th>
                  <th>Needs</th>
                  <th>Who can run it</th>
                  <th aria-label="Override" />
                </tr>
              </thead>
              <tbody>
                {commands.map((c) => {
                  const r = resolveCommand(model.overrides, plugin, c);
                  const who = whoCan(model, r.permission);
                  const aliases = c.aliases ?? [];
                  const declared = c.declaredPermission ?? c.permission ?? "";
                  return (
                    <tr key={c.name}>
                      <td className={css.nowrap}>
                        <code className={css.command}>!{c.name}</code>
                        {aliases.length > 0 && (
                          <div className={css.muted}>also {aliases.map((a) => `!${a}`).join(", ")}</div>
                        )}
                      </td>
                      <td className={css.description}>{c.description}</td>
                      <td>
                        {r.permission ? (
                          <Grant value={r.permission} />
                        ) : (
                          <span className={cn(ui.pill, ui["textTone-ok"])}>Anyone</span>
                        )}
                        {r.overridden && (
                          <div className={css.overridden}>
                            <span
                              className={cn(ui.pill, ui.pillWarn)}
                              title={`The plugin asks for ${declared || "no permission"}`}
                            >
                              Overridden
                            </span>
                          </div>
                        )}
                      </td>
                      <td>
                        <div className={css.chips}>
                          {who.anyone ? (
                            <None text="Everyone" />
                          ) : who.roles.length > 0 ? (
                            who.roles.map((n) => <RoleChip key={n} name={n} model={model} />)
                          ) : (
                            <None text="Only the server console" />
                          )}
                          {!who.anyone &&
                            who.people
                              .filter((p) => !p.entry.roles.some((role) => who.roles.some((r) => sameName(r, role))))
                              .map((p) => (
                                <span key={p.key} className={css.person}>
                                  {displayName(p)}
                                </span>
                              ))}
                        </div>
                      </td>
                      <td>
                        <div className={css.rowActions}>
                          <button
                            type="button"
                            className={cn(ui.btn, ui.btnSmall)}
                            disabled={!writable}
                            onClick={() => onOverride(plugin, c)}
                          >
                            Change
                          </button>
                        </div>
                      </td>
                    </tr>
                  );
                })}
              </tbody>
            </table>
          </div>
          <div className={ui.hint}>{plural(plugin.commands.length, "command")}</div>
        </section>
      ))}
    </>
  );
}

function Check({ model }: { model: PermissionModel }) {
  const [who, setWho] = useState(model.people[0]?.id || "");
  const [query, setQuery] = useState("kick");
  const entry = model.people.find((p) => p.id === who)?.entry || null;
  const name = query.trim().replace(/^[!/]|^dw_/i, "");
  const hits = !name
    ? []
    : name.includes(".")
      ? [{ key: name, label: name, permission: name }]
      : model.plugins.flatMap((pl) =>
          pl.commands
            .filter((c) => [c.name, ...(c.aliases ?? [])].some((n) => sameName(n, name)))
            .map((c) => ({
              key: pl.file + c.name,
              label: `!${c.name} (${pl.core ? "core" : pl.plugin})`,
              permission: resolveCommand(model.overrides, pl, c).permission,
            }))
        );
  const commandNames = [...new Set(model.plugins.flatMap((p) => p.commands.map((c) => c.name)))];

  return (
    <section>
      <SectionHead title="Check access" />
      <div className={styles.formGrid}>
        <div className={ui.field}>
          <label className={ui.label} htmlFor="perm-check-who">
            Player
          </label>
          <select id="perm-check-who" className={ui.select} value={who} onChange={(e) => setWho(e.target.value)}>
            {model.people.map(
              (p) =>
                p.id && (
                  <option key={p.key} value={p.id}>
                    {displayName(p)}
                  </option>
                )
            )}
            <option value="">Anyone else (default role only)</option>
          </select>
        </div>
        <div className={ui.field}>
          <label className={ui.label} htmlFor="perm-check-query">
            Command or permission
          </label>
          <input
            id="perm-check-query"
            className={ui.input}
            value={query}
            onChange={(e) => setQuery(e.target.value)}
            list="perm-check-commands"
            placeholder="ban, or admin.moderation.ban"
            spellCheck={false}
          />
          <datalist id="perm-check-commands">
            {commandNames.map((c) => (
              <option key={`c:${c}`} value={c} />
            ))}
            {declaredPermissions(model).map((p) => (
              <option key={`p:${p}`} value={p} />
            ))}
          </datalist>
        </div>
      </div>
      <div className={css.verdicts}>
        {name && hits.length === 0 && <p className={css.muted}>No installed plugin has a command called {name}.</p>}
        {hits.map((h) => {
          const r = explain(model, entry, h.permission);
          return (
            <div key={h.key} className={cn(css.verdict, r.allowed ? css.verdictAllowed : css.verdictDenied)}>
              <strong>{r.allowed ? "Can run" : "Cannot run"}</strong>
              <span>
                {h.label}
                {h.permission && h.permission !== h.label && (
                  <>
                    {" · needs "}
                    <code className={css.grant}>{h.permission}</code>
                  </>
                )}
              </span>
              <span className={css.muted}>{describe(r)}</span>
            </div>
          );
        })}
      </div>
    </section>
  );
}

function Problems({ problems }: { problems: Problem[] }) {
  return (
    <section>
      <SectionHead title="Errors" />
      {problems.length > 0 ? (
        <ul className={cn(styles.list, css.problems)}>
          {problems.map((p, i) => (
            <li key={`${i}:${p.text}`} className={css[`problem-${p.level}`]}>
              {p.text}
            </li>
          ))}
        </ul>
      ) : (
        <div className={cn(styles.list, styles.listEmpty)}>
          <div className={css.emptyTitle}>No errors</div>
          <p>The permission files read cleanly, and every role and permission they mention exists.</p>
        </div>
      )}
    </section>
  );
}

// Grants as removable chips, with suggestions from what the installed plugins declare.
function GrantInput({
  id,
  value,
  onChange,
  suggestions,
}: {
  id: string;
  value: string[];
  onChange: (next: string[]) => void;
  suggestions: string[];
}) {
  const [text, setText] = useState("");
  const typed = text.trim();
  const valid = typed !== "" && parseGrant(typed) !== null;
  const add = () => {
    if (!valid || value.includes(typed)) return;
    onChange([...value, typed]);
    setText("");
  };
  return (
    <div className={css.grantInput}>
      <div className={css.chips}>
        {value.map((v) => (
          <span key={v} className={css.chipEdit}>
            <Grant value={v} />
            <button type="button" aria-label={`Remove ${v}`} onClick={() => onChange(value.filter((x) => x !== v))}>
              ×
            </button>
          </span>
        ))}
        {value.length === 0 && <None />}
      </div>
      <div className={ui.row}>
        <input
          id={id}
          className={cn(ui.input, ui.mono)}
          value={text}
          list={`${id}-list`}
          placeholder="admin.moderation.kick, admin.moderation.*, or -admin.moderation.ban"
          spellCheck={false}
          onChange={(e) => setText(e.target.value)}
          onKeyDown={(e) => {
            if (e.key === "Enter") {
              e.preventDefault();
              add();
            }
          }}
        />
        <button type="button" className={ui.btn} disabled={!valid} onClick={add}>
          Add
        </button>
      </div>
      {typed !== "" && !valid && (
        <div className={ui.errorText}>Use dots between parts, and * only as the last part.</div>
      )}
      <datalist id={`${id}-list`}>
        {suggestions
          .flatMap((s) => [s, `-${s}`])
          .map((s) => (
            <option key={s} value={s} />
          ))}
        <option value="*" />
      </datalist>
    </div>
  );
}

// What a role lets its holders do, in a few words: the last part of each grant, inherited roles first.
function roleSummary(model: PermissionModel, name: string): string {
  const role = findRole(model.roles, name);
  if (!role) return "";
  const grants = role.permissions || [];
  if (grants.some((g) => g.trim() === "*")) return "Every command";
  const words = grants.flatMap((g) => {
    const p = parseGrant(g);
    if (!p) return [];
    if (p.all) return [p.deny ? "nothing" : "everything"];
    const last = (p.exact ?? (p.prefix ?? "").slice(0, -1)).split(".").pop() ?? "";
    return [`${p.deny ? "no " : ""}${last}${p.prefix ? " (all)" : ""}`];
  });
  const parts = [...(role.inherits || []).map((r) => `all of ${r}`), ...words];
  if (parts.length === 0) return "Nothing yet";
  return parts.length > 5 ? `${parts.slice(0, 5).join(", ")} +${parts.length - 5} more` : parts.join(", ");
}

// One list of roles: tick the ones this player holds. Each row says what the role gives.
function RolePicker({
  model,
  names,
  selected,
  onToggle,
}: {
  model: PermissionModel;
  names: string[];
  selected: string[];
  onToggle: (name: string) => void;
}) {
  return (
    <div className={css.picker} role="group" aria-label="Roles">
      {names.map((r) => {
        const on = selected.some((s) => sameName(s, r));
        return (
          <label key={r} className={cn(css.pickerRow, on && css.pickerRowOn)}>
            <input type="checkbox" checked={on} onChange={() => onToggle(r)} />
            <span className={css.pickerCheck} aria-hidden>
              {on && (
                <svg width="10" height="10" viewBox="0 0 12 12" fill="none" stroke="currentColor" strokeWidth="2.5">
                  <path d="M2 6.5l2.8 2.8L10 3.5" />
                </svg>
              )}
            </span>
            <span className={css.pickerText}>
              <strong>{r}</strong>
              <span>{roleSummary(model, r)}</span>
            </span>
            <span className={css.pickerImmunity} title="Immunity">
              {roleImmunity(model.roles, r)}
            </span>
          </label>
        );
      })}
    </div>
  );
}

// A role is held under whatever capitals the file gives it, so "Admin" ticks (and unticks) "admin".
const toggleRole = (list: string[], role: string): string[] =>
  list.some((x) => sameName(x, role)) ? list.filter((x) => !sameName(x, role)) : [...list, role];

function PlayerDialog({
  model,
  person,
  prefill,
  connected,
  busy,
  error,
  onClose,
  onSave,
}: {
  model: PermissionModel;
  /** The row being edited; absent when adding. */
  person?: Person;
  prefill?: AdminPrefill;
  /** Players on the server right now, to pick from when adding. */
  connected: PlayerInfo[];
  busy: boolean;
  error: string | null;
  onClose: () => void;
  onSave: (values: PlayerSave) => void;
}) {
  const adminRole = Object.keys(model.roles).find((k) => sameName(k, "admin"));
  const [steamId, setSteamId] = useState(person?.id ?? prefill?.steamId ?? "");
  const [name, setName] = useState(person?.entry.name ?? prefill?.name ?? "");
  const [roles, setRoles] = useState<string[]>(person?.entry.roles ?? (adminRole ? [adminRole] : []));
  const [permissions, setPermissions] = useState<string[]>(person?.entry.permissions ?? []);
  const [immunity, setImmunity] = useState(person?.entry.immunity != null ? String(person.entry.immunity) : "");
  const [extrasOpen] = useState(permissions.length > 0 || immunity !== "");

  const id = parseSteamId(steamId);
  const roleNames = Object.keys(model.roles).filter((r) => !sameName(r, DEFAULT_ROLE));
  const toggle = (r: string) => setRoles((rs) => toggleRole(rs, r));

  const candidates = useMemo(() => {
    const seen = new Set(model.people.map((p) => p.id));
    const out: { id: string; name: string }[] = [];
    for (const p of connected) {
      const id64 = p.bot ? null : parseSteamId(p.steamId64);
      if (!id64 || seen.has(id64)) continue;
      seen.add(id64);
      out.push({ id: id64, name: p.name });
    }
    return out;
  }, [connected, model.people]);

  const formId = "perm-player-form";
  return (
    <Modal
      medium
      title={person ? `Edit ${displayName(person)}` : "Add admin"}
      onClose={busy ? () => {} : onClose}
      actions={
        <>
          <button type="button" className={ui.btn} onClick={onClose} disabled={busy}>
            Cancel
          </button>
          <button type="submit" form={formId} className={ui.btnPrimary} disabled={busy || !id}>
            {busy ? "Saving..." : person ? "Save" : "Add admin"}
          </button>
        </>
      }
    >
      <form
        id={formId}
        className={css.form}
        onSubmit={(e) => {
          e.preventDefault();
          if (busy || !id) return;
          onSave({ adding: !person, steamId, name, roles, permissions, immunity });
        }}
      >
        {error && <ErrorNote message={error} />}

        {!person && candidates.length > 0 && (
          <div className={ui.field}>
            <label className={ui.label} htmlFor="perm-player-connected">
              Connected players
            </label>
            <select
              id="perm-player-connected"
              className={ui.select}
              value={id && candidates.some((c) => c.id === id) ? id : ""}
              onChange={(e) => {
                const picked = candidates.find((c) => c.id === e.target.value);
                if (!picked) return;
                setSteamId(picked.id);
                setName(picked.name);
              }}
            >
              <option value="">Pick someone who is on the server now</option>
              {candidates.map((c) => (
                <option key={c.id} value={c.id}>
                  {c.name}
                </option>
              ))}
            </select>
          </div>
        )}

        <div className={ui.field}>
          <label className={ui.label} htmlFor="perm-player-id">
            SteamID
          </label>
          <input
            id="perm-player-id"
            className={cn(ui.input, ui.mono)}
            value={steamId}
            onChange={(e) => setSteamId(e.target.value)}
            placeholder="76561197960287930"
            disabled={Boolean(person)}
            autoFocus={!person && !prefill}
            spellCheck={false}
            required
          />
          <div className={ui.hint}>
            {id
              ? `SteamID64 ${id} · ${steam3(id)}`
              : "A SteamID64, STEAM_1:0:123, [U:1:246], or a steamcommunity.com/profiles/ link."}
          </div>
        </div>

        <div className={ui.field}>
          <label className={ui.label} htmlFor="perm-player-name">
            Name
          </label>
          <input
            id="perm-player-name"
            className={ui.input}
            value={name}
            onChange={(e) => setName(e.target.value)}
            placeholder="Who this is"
            maxLength={64}
          />
          <div className={ui.hint}>A note for you. Deadworks never uses it to match players.</div>
        </div>

        <fieldset className={css.fieldset}>
          <legend className={ui.label}>Roles</legend>
          {roleNames.length > 0 ? (
            <RolePicker model={model} names={roleNames} selected={roles} onToggle={toggle} />
          ) : (
            <div className={ui.hint}>There are no roles besides default yet. Create one on the Roles view.</div>
          )}
          {roles
            .filter((r) => !roleNames.some((n) => sameName(n, r)))
            .map((r) => (
              <div key={r} className={cn(ui.hint, css.warnText)}>
                They also have "{r}", which isn't defined.{" "}
                <button type="button" className={ui.linkBtn} onClick={() => toggle(r)}>
                  Remove it
                </button>
              </div>
            ))}
        </fieldset>

        <details className={css.details} open={extrasOpen}>
          <summary className={ui.label}>Extra permissions and immunity</summary>
          <div className={ui.field}>
            <label className={ui.label} htmlFor="perm-player-grants">
              Own permissions
            </label>
            <GrantInput
              id="perm-player-grants"
              value={permissions}
              onChange={setPermissions}
              suggestions={declaredPermissions(model)}
            />
            <div className={ui.hint}>
              Checked before any role, so a deny here takes something away even if a role gives it.
            </div>
          </div>
          <div className={ui.field}>
            <label className={ui.label} htmlFor="perm-player-immunity">
              Immunity override
            </label>
            <input
              id="perm-player-immunity"
              className={cn(ui.input, ui.num)}
              type="number"
              min={0}
              value={immunity}
              onChange={(e) => setImmunity(e.target.value)}
              placeholder={`From roles: ${playerImmunity(model.roles, { roles }).value}`}
            />
          </div>
        </details>
      </form>
    </Modal>
  );
}

function RoleDialog({
  model,
  name,
  busy,
  error,
  onClose,
  onSave,
}: {
  model: PermissionModel;
  /** The role being edited; absent when creating one. */
  name?: string;
  busy: boolean;
  error: string | null;
  onClose: () => void;
  onSave: (values: RoleSave) => void;
}) {
  const role = name ? model.roles[name] : null;
  const [roleName, setRoleName] = useState(name ?? "");
  const [permissions, setPermissions] = useState<string[]>(role?.permissions ?? []);
  const [inherits, setInherits] = useState<string[]>(role?.inherits ?? []);
  const [immunity, setImmunity] = useState(role?.immunity != null ? String(role.immunity) : "");
  const isDefault = name != null && sameName(name, DEFAULT_ROLE);
  const others = Object.keys(model.roles).filter((r) => r !== name && !sameName(r, DEFAULT_ROLE));

  const formId = "perm-role-form";
  return (
    <Modal
      medium
      title={name ? `Edit role ${name}` : "New role"}
      onClose={busy ? () => {} : onClose}
      actions={
        <>
          <button type="button" className={ui.btn} onClick={onClose} disabled={busy}>
            Cancel
          </button>
          <button type="submit" form={formId} className={ui.btnPrimary} disabled={busy || !roleName.trim()}>
            {busy ? "Saving..." : "Save role"}
          </button>
        </>
      }
    >
      <form
        id={formId}
        className={css.form}
        onSubmit={(e) => {
          e.preventDefault();
          if (busy || !roleName.trim()) return;
          onSave({ original: name ?? null, name: roleName, permissions, inherits, immunity });
        }}
      >
        {error && <ErrorNote message={error} />}

        <div className={styles.formGrid}>
          <div className={ui.field}>
            <label className={ui.label} htmlFor="perm-role-name">
              Name
            </label>
            <input
              id="perm-role-name"
              className={ui.input}
              value={roleName}
              onChange={(e) => setRoleName(e.target.value)}
              placeholder="moderator"
              disabled={isDefault}
              autoFocus={!name}
              spellCheck={false}
              required
            />
            {isDefault && <div className={ui.hint}>Every player has this role.</div>}
          </div>
          <div className={ui.field}>
            <label className={ui.label} htmlFor="perm-role-immunity">
              Immunity
            </label>
            <input
              id="perm-role-immunity"
              className={cn(ui.input, ui.num)}
              type="number"
              min={0}
              value={immunity}
              onChange={(e) => setImmunity(e.target.value)}
              placeholder={
                inherits.length > 0
                  ? `Inherited: ${Math.max(0, ...inherits.map((r) => roleImmunity(model.roles, r)))}`
                  : "0"
              }
            />
          </div>
        </div>

        <fieldset className={css.fieldset}>
          <legend className={ui.label}>Permissions</legend>
          <GrantInput
            id="perm-role-grants"
            value={permissions}
            onChange={setPermissions}
            suggestions={declaredPermissions(model)}
          />
        </fieldset>

        {others.length > 0 && (
          <fieldset className={css.fieldset}>
            <legend className={ui.label}>Inherits</legend>
            <RolePicker
              model={model}
              names={others}
              selected={inherits}
              onToggle={(r) => setInherits((xs) => toggleRole(xs, r))}
            />
          </fieldset>
        )}
      </form>
    </Modal>
  );
}

function OverrideDialog({
  model,
  plugin,
  command,
  busy,
  error,
  onClose,
  onSave,
}: {
  model: PermissionModel;
  plugin: PluginInfo;
  command: CommandInfo;
  busy: boolean;
  error: string | null;
  onClose: () => void;
  onSave: (values: OverrideSave) => void;
}) {
  const current = resolveCommand(model.overrides, plugin, command);
  // What the plugin itself asks for, whatever override was in effect when the list was written.
  const declared = command.declaredPermission ?? command.permission ?? "";
  const [mode, setMode] = useState<"plugin" | "anyone" | "custom">(
    !current.overridden ? "plugin" : current.permission ? "custom" : "anyone"
  );
  const [permission, setPermission] = useState(current.overridden ? current.permission : declared);
  const key = `${plugin.plugin}:${command.name}`;
  const save = () =>
    onSave({
      key,
      previous: current.key ?? null,
      permission: mode === "plugin" ? null : mode === "anyone" ? "" : permission,
    });

  return (
    <Modal
      title={`Who can run !${command.name}`}
      onClose={busy ? () => {} : onClose}
      actions={
        <>
          <button type="button" className={ui.btn} onClick={onClose} disabled={busy}>
            Cancel
          </button>
          <button
            type="button"
            className={ui.btnPrimary}
            disabled={busy || (mode === "custom" && !parseGrant(permission))}
            onClick={save}
          >
            {busy ? "Saving..." : "Save"}
          </button>
        </>
      }
    >
      {error && (
        <div style={{ marginBottom: 10 }}>
          <ErrorNote message={error} />
        </div>
      )}
      {command.description && <p>{command.description}</p>}
      <div className={css.options} role="radiogroup" aria-label="Who can run it">
        <label>
          <input type="radio" name="perm-override-mode" checked={mode === "plugin"} onChange={() => setMode("plugin")} />
          <span>
            As the plugin decides:{" "}
            {declared ? <code className={css.grant}>{declared}</code> : "anyone"}
          </span>
        </label>
        <label>
          <input type="radio" name="perm-override-mode" checked={mode === "anyone"} onChange={() => setMode("anyone")} />
          <span>Anyone can run it</span>
        </label>
        <label>
          <input type="radio" name="perm-override-mode" checked={mode === "custom"} onChange={() => setMode("custom")} />
          <span>Require a different permission</span>
        </label>
        {mode === "custom" && (
          <input
            className={cn(ui.input, ui.mono)}
            value={permission}
            onChange={(e) => setPermission(e.target.value)}
            list="perm-override-list"
            placeholder="my.custom.permission"
            spellCheck={false}
            autoFocus
          />
        )}
        <datalist id="perm-override-list">
          {declaredPermissions(model).map((p) => (
            <option key={p} value={p} />
          ))}
        </datalist>
      </div>
      <div className={ui.hint}>
        Saved in overrides.jsonc as <code className={css.grant}>{key}</code>, so it only changes this plugin's command.
      </div>
    </Modal>
  );
}

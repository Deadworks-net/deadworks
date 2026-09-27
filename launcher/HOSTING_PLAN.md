# Local server hosting in the launcher: plan

Branch `launcher/hosting`, worktree `H:\deadworks-dev\nexus-hosting`. The whole worktree lives on H:,
so `node_modules`, `src-tauri/target` and test installs never touch C:.

## Goal

A non-technical user clicks **Host → Create server**, picks a name and how friends will connect,
optionally drops in plugin DLLs, and presses **Start**. The launcher handles everything else:

- game files
- Deadworks
- .NET
- gameinfo edits
- cfgs
- ports
- the console
- updates

## Implementation status (2026-09-25)

Implemented on this branch (uncommitted):

- `src-tauri/src/hosting/*`: the backend.
- `src/components/hosting/*` and `src/lib/hosting.ts`: the HOST tab.
- The Settings → Hosting section.
- `managed/` additions `dw_host_status` and `dw_kick`.

It was driven end to end through the real UI and IPC (WebView2 remote debugging, build 6698, Deadworks
v0.4.16).

### Verified

- **Install from the client.**
  - Manifest-driven: 3,862 files, 36.5 GB, about 45 s on NVMe.
  - Every file is SHA1-checked, and the launcher-patched `gameinfo.gi` is recovered to Steam's hash.
  - It then downloads Deadworks and the private .NET 10.0.12.
- **Cancel and retry.** A folder is marked as ours on the first write, so a retry reuses it.
- **Create and start a server** from the UI. It is up in about 5 s with an SDR address, and the
  meters update.
- **Console.**
  - Live log.
  - Commands are typed into the hidden console, and each command's echo is shown as the input line.
  - `status` polling stays hidden, and the public IP is read from it.
- **Plugins.**
  - Import from a folder or a single DLL. Host assemblies and the source generator are excluded.
  - Enable and disable live (`dw_plugin`, re-registers commands).
  - JSONC config edit with validation, applied live (`dw_reloadconfig`).
- **Network modes.**
  - SDR: gameinfo patch, identity, and the "address changed, share again" flag across restarts.
  - Port-forward: public IP and password in the connect command, browser listing.
  - LAN: LAN IP, forced unlisted.
- **Content.** VPK import rejects non-VPKs; accepted files are linked into `citadel/maps` or
  `deadworks_mods/vpks` and advertised.
- **Profiles.** Duplicate (next free port, no browser credentials copied); two servers running at
  once; delete.
- **Stop and crash.**
  - Stop takes about 1 s (`quit`).
  - A launcher crash takes its servers down with it (job object).
  - A crash shows its message, the last console lines, and Restart.
- **Unsupported build.** Pinning v0.4.12 on 6698:
  1. It is detected (`Failed to find signature`) and the hold is shown.
  2. Starting is refused.
  3. The updater finds v0.4.16, applies it, lifts the hold, and restarts the held server by itself.
- **Game update.** A simulated older base build is detected from the client appmanifest. The running
  idle server is stopped, the base is re-synced (nothing to copy) and the generation bumped, and the
  server restarts.
- **A real Deadlock update (2026-09-25, build 25379491 → 25535087, game version 6698 → 6701).**
  1. About 30 s after launcher start, the updater picked it up from the client appmanifest and the
     new depotcache manifests.
  2. It copied only the 21 changed files (719 MB), all SHA1-verified, none modified.
  3. Server trees relinked on the next start.
  4. Deadworks v0.4.16 runs on 6701: every signature resolved and the plugins loaded.
- **Verify.** 36.5 GB re-hashed in 46 s. A corrupted base file is repaired from the client, and
  server trees relink to it.
- **SteamCMD.**
  - Bootstrap and self-update work.
  - Login runs under ConPTY. SteamCMD buffers its stdout on a pipe, so its prompts never arrive
    there; ConPTY fixes that.
  - The `password:` prompt reaches the UI, the answer is typed in, and a wrong password reads
    "Steam rejected the password".

### Needs a human (or deploys)

- **A client joining over SDR, port-forward and LAN.** The server side of each is verified.
- **SteamCMD with a real account:** Steam Guard code and mobile-confirm prompts, a full 36 GB
  download, and cached-token updates. Anonymous logins can't see Deadlock's build id, and Steam's
  `UpToDateCheck` API refuses the app, so update checks for SteamCMD installs use the saved user's
  cached token with `+@NoPromptForPassword 1`.
- **The Windows Firewall step.**
  - Each server's first start runs one elevated `netsh` (a UAC prompt) that replaces any rules for
    that server's `deadworks.exe` with an allow rule.
  - Without it, Windows prompts for every new server path, and dismissing the prompt creates block
    rules, which override allows.
  - Automated test runs set `DEADWORKS_SKIP_FIREWALL=1` (debug builds only). Not yet exercised with
    a real UAC click.
- **The port reachability check.** It needs the deadworks-api `hosting-reachability` branch (queued
  check handled by the probe) to be deployed. Until then the launcher shows "couldn't check".
- **A player kick.** `dw_kick` and `dw_host_status` exist in managed but not in any Deadworks
  release yet. Released builds fall back to `status` + `kickid`.

### Not done (small)

- The tray doesn't show a running-server count. Quit already asks before stopping servers.
- The launcher's own auto-update restarts it, which stops running servers without asking.

## Decisions (agreed)

| Topic | Decision |
|---|---|
| Game files | Default: copy from the detected Deadlock client install, driven by Steam's manifests, no login. **SteamCMD** with an in-app login is the advanced path (no client install, or a fully independent server). |
| Copy mode | Asked when the shared base install is created: full copy (~36.5 GB), or hardlinks when on the same drive as the client. The wizard explains the catch that Steam can't patch the client while a server runs. It can be changed later in Settings → Hosting. |
| Network modes | Steam relay (SDR), port forward (manual, guided, with a reachability check), LAN only. No UPnP. |
| Plugins | Local DLL / zip / folder drop only. No catalog, no GitHub URLs, no bundled presets in v1. |
| Platform | Windows only. |
| Lifetime | Servers survive closing the window (hide to tray). Tray *Quit* asks to stop running servers. No auto-start or crash auto-restart in v1. |
| Servers | Many profiles over one shared base install, several running at once. |
| Joining | "Copy connect command" (`connect ip:port` / `connect [A:1:…]`). No invite links or join button. |
| SDR Steam ID | It changes every restart (anonymous logon). Accept it and surface it: show the current ID prominently and flag "address changed, re-share" after each restart. |
| Browser listing | Toggle, default **unlisted** (`deadworks.jsonc` `serverbrowser.unlisted`). Only meaningful for port-forward servers. |
| Config UI | Basics, custom cvars/args, per-plugin JSONC editor (schema form later), content addons / extra maps. |
| Live tools | Embedded console, player list with kick, plugin hot-toggle, CPU/RAM/player meters. Bans deferred. |
| Updates | Automatic but never mid-game. Hold with a clear status when the Deadworks release doesn't support the new game build. |
| Admin rights | Out of scope for v1. |

## What exists to build on

- `docker/entrypoint.sh` is the working reference implementation. The launcher's hosting core is
  essentially its Windows port:
  - release caching (keep 3)
  - manifest-based deploy
  - settings in a generated cfg (argv breaks on spaces)
  - RCON on the game port
  - exit 78 / "Failed to find signature" → hold
- `gameinfo.rs`: its `locate_block` lexer plus the line helpers handle the NetworkSystem/ConVars edits.
- `addons.rs::download_and_decompress`: a streaming downloader that emits progress.
- `ping.rs` (A2S_INFO) and the untracked `addons/a2s.rs` (A2S_RULES).
- `connect.rs`: finds the Steam root, the library holding 1422450, and the game dir.
- Managed core:
  - `dw_plugin enable|disable`
  - `dw_reloadconfig`
  - plugins are loaded **from bytes** (`PluginLoader.cs:211`), so DLLs are never locked and hot-swap is safe
  - `configs/plugins.jsonc`, `configs/<Plugin>/<Plugin>.jsonc`, `configs/deadworks.jsonc`
- The release zip is a 12-file overlay rooted at the install. `DeadworksManaged.runtimeconfig.json`
  wants only `Microsoft.NETCore.App` 10.0 (rollForward LatestMinor).

Facts that constrain the design:

- Deadlock can't be downloaded anonymously, so SteamCMD needs a real login.
- `banid`, `writeid` and `removeid` are `developmentonly`. Release builds only have `kick`/`kickid`,
  so bans would need Deadworks core support (deferred).
- `sv_setsteamaccount` exists, but whether Valve issues GSLTs for 1422450 is unknown. Parked
  (churn is accepted).
- RCON listens on the host's vEthernet IP, not 127.0.0.1. That IP changes, so find it per-pid.
- A local server on 27015 crashed the same-PC client (observed three times). Default ports start
  at 27020.
- `sv_cheats` set from a cfg is reverted on map load; `+sv_cheats 1` in argv sticks.
- Measured cost: ~1.4 GB RAM idle plus ~9 MiB per player, about one CPU core at 30 players.

## Disk layout

The hosting root is chosen in the wizard. Default: `<drive with most free space>:\Deadworks Servers`,
refusing any drive with less than ~40 GB free.

```
<root>\
  hosting.json                   base source + copy mode, installed build id, Deadworks tag, runtime version
  base\game\...                  shipped game files only (from Steam manifests), marked read-only
  cache\deadworks\<tag>\         extracted releases, keep 3
  cache\dotnet\<ver>\            private .NET 10 runtime (zip, sha512-verified; no installer, no UAC)
  cache\steamcmd\                only if the SteamCMD path is used (login token cached by SteamCMD itself)
  plugins\<name>\<sha256>\       plugin library: imported once, usable by any profile (keeps sidecar dependency DLLs)
  servers\<slug>\
    server.json                  profile settings (source of truth for the UI)
    user\configs\                junction target for tree\...\configs (plugin configs survive rebuilds)
    user\vpks\, user\maps\       content addons / extra maps for this profile
    logs\                        console transcript per run (rotated)
    tree\game\...                runnable tree, rebuilt when base build / Deadworks tag / profile changes
```

The tree mirrors the Docker per-file symlink tree:

- Every shipped file is a **hardlink** into `base`. The tree and base share the root volume, so this
  always works and costs no disk.
- Real files laid over the top:
  - the Deadworks release files
  - the patched `gameinfo.gi`
  - `managed\plugins\*.dll` (copies of the enabled plugins)
  - `citadel\cfg\deadworks_launcher.cfg` (generated) and `deadworks_user.cfg`
- `bin\win64\configs` is a directory junction to `user\configs`. Junctions are fine on Windows; the
  reparse-point problem was Wine-only.
- Writes always replace the file (delete, then create) and never write through a link. The base is
  read-only, so any accidental write fails loudly instead of corrupting every profile.

## Architecture

### Rust: `src-tauri/src/hosting/`

| Module | Responsibility |
|---|---|
| `mod.rs` | `HostingManager` in Tauri state; commands; emits `hosting://…` events |
| `store.rs` | `hosting.json` + `server.json` load/save (atomic writes, schema version) |
| `disk.rs` | free space per drive, root picker default, size estimates |
| `manifest.rs` | read `appmanifest_1422450.acf` (buildid, StateFlags, InstalledDepots), parse `Steam\depotcache\<depot>_<manifest>.manifest` → file list + SHA1 |
| `base.rs` | build and sync `base` from the client (copy or hardlink, diff by manifest, delete removed files); detect a modified client (DMM, launcher's gameinfo block) and strip or warn |
| `steamcmd.rs` | bootstrap steamcmd.zip, login flow (password → Steam Guard code / mobile-confirm prompt), `app_update 1422450 validate`, progress parsing, anonymous `app_info_print` for build polling |
| `release.rs` | resolve Deadworks latest via the `/releases/latest` redirect (no API rate limit), download, extract, keep 3, compatibility hold |
| `dotnet.rs` | fetch the .NET 10 runtime zip from Microsoft's release metadata, verify sha512, extract; launch with `DOTNET_ROOT` |
| `tree.rs` | per-profile tree builder (hardlinks + overlays + junction), idempotent via a `.tree-state` stamp |
| `kv.rs` | extracted from `gameinfo.rs`: generic block locate / insert / replace, shared with the client patcher |
| `netcfg.rs` | per-mode gameinfo patch (SDR: `CreateListenSocketP2P 2`, `net_p2p_listen_dedicated 1`, rate block); port allocation; LAN IP listing; Windows Firewall rule (one UAC prompt via `netsh advfirewall`, only when needed) |
| `cfg.rs` | generate `deadworks_launcher.cfg` (hostname, sv_password, rcon_password, maxplayers-related cvars, user cvars, with quoting) and argv (space-free tokens only, validated) |
| `process.rs` | spawn `deadworks.exe` under a **ConPTY** (real console: no GetLine spam, stdin works, no window) inside a Job object (kill-on-close so a launcher crash never orphans servers); graceful stop = `quit` → 15 s → terminate; exit-code classification |
| `console.rs` | VT-stripped line stream, ring buffer + log file, pattern hooks (ready, SDR identity, signature failure, crash) |
| `rcon.rs` | Source RCON client; the listen address is found from the pid's TCP listen socket (`GetExtendedTcpTable`) |
| `status.rs` | player list / plugin state: poll `dw_host_status` (new, JSON) over RCON, falling back to parsing `status` |
| `metrics.rs` | per-pid CPU/RAM via `sysinfo` |
| `updater.rs` | watches the client build (appmanifest), Deadworks releases (30 min), SteamCMD builds; applies when every server on the base is stopped or empty; holds on unsupported builds |
| `plugins.rs` | import DLL/zip/folder into the library (checks for a PE + CLR header), per-profile enable, live copy-in + `dw_plugin` toggle |

New crates: `zip`, `portable-pty` (or `windows` ConPTY directly), `windows` (Job objects,
`GetExtendedTcpTable`), `sysinfo`, `sha1`, `sha2` (already present).

### Frontend

- **Top-level tabs** in the main window, **SERVERS | HOST**. `ServersPage.tsx:86` already has the tab
  styling; lift it into `App`.
- **HOST, first run:** a single "Host your own server" call to action opens the setup flow.
  1. **Install** (once): location with the free-space bar, and the game-files source:
     - "Copy from your Deadlock install at F:\… (36.5 GB)"
     - the hardlink option, shown only on the same drive, with the tradeoff in one sentence
     - "Download with SteamCMD instead" link
     Then one progress screen covering game files, Deadworks and .NET in parallel, with an ETA.
  2. **Create server:**
     - name, optional password, max players, map
     - network as three big cards:
       - *Friends anywhere, no setup* (SDR)
       - *Port forwarding* (best ping, needs router access)
       - *Same network* (LAN)
  3. **Plugins:** drop zone plus toggles (skippable).
  4. **Start:** the server page, with a big **Copy connect command** button.
- **HOST, with servers:** a left rail lists servers with status dots, plus *New* and *Duplicate*.
  The server page has a header (status, Start/Stop/Restart, Copy connect) and tabs:
  - **Overview**: CPU/RAM/players meters, player list with kick, network status (SDR ID with a
    "changed since last share" flag; public IP:port with the reachability result; LAN IPs)
  - **Console**: live log, filter, command box with history
  - **Plugins**: library toggles (live), import, per-plugin **Config** (CodeMirror 6 JSONC editor with
    lint and *Reset to defaults*; saving runs `dw_reloadconfig`). Before the first run: "Start the
    server once to generate this plugin's config."
  - **Settings**: basics and the browser-listing toggle
  - **Content**: addon VPKs and extra maps (drop in, toggle)
  - **Advanced**: custom cvars and launch args, open folder, delete server
- **Settings window → Hosting:** root location and usage, game-files source and copy mode, rebuild /
  verify base, update behaviour (read-only info), uninstall everything.
- **Tray:** a "N servers running" line; Quit confirms stopping them.
- **Status vocabulary:** Not installed · Installing · Stopped · Starting · Running · Stopping ·
  Updating · *Waiting for Deadworks to support game build N* · Crashed (shows the last 50 console lines
  plus *Restart* / *Open logs*).
- Every failure maps to a plain-language message with one action:
  - not enough space
  - client not found or mid-update
  - port in use (offer the next free port)
  - firewall blocked
  - .NET download failed
  - SDR logon failed
  - unsupported game build

New npm deps: `@codemirror/*` (json, lint), `jsonc-parser`.

## Required changes outside the launcher

**Deadworks core (nexus):**

1. **`dw_host_status`.** One JSON line containing:
   - players: slot, userid, steamid64, name, ping, connected secs, team, hero
   - plugins: name, enabled, loaded
   - map, uptime, game build, Deadworks version

   This keeps the launcher from depending on `status` text formatting.
2. **Ship a release that includes exit code 78** (it's on main, unreleased) so holds are detected
   precisely.

**api.deadworks.net:** `GET /api/hosting/reachability?port=N` sends one A2S_INFO to the *requester's
own* IP:port and returns `{publicIp, reachable}`. It is rate-limited and only ever targets the caller,
so it can't be used as a reflector.

## Spike results (2026-09-25, game build 6698, Deadworks v0.4.16)

1. **ConPTY: rejected.** Input works (it needs an answer to its `ESC[6n` cursor query first), but the
   engine's text console redraws a status bar at row 1 (`ESC[H 0/31 on map dl_midtown`). ConPTY
   therefore repaints the whole 50-row screen, every log line came through about 50 times, and lines
   can be lost between repaints.
   **Used instead:** `CreateProcessW` with `CREATE_NO_WINDOW`, `STARTF_USESTDHANDLES`, a **NULL**
   `hStdInput` and piped stdout/stderr.
   - Windows then gives the child its own hidden console's input, so there is no GetLine spam.
     `Stdio::null()` (the NUL device) spams 60 lines/s instead.
   - Output is clean and never repeated.
   - Commands are injected with `AttachConsole(pid)` → `CONIN$` → `WriteConsoleInputW` →
     `FreeConsole`, serialised by a process-wide lock. Each command is echoed into stdout.
   - `quit` exits in <1 s. **No RCON needed.**
2. **Hardlink tree + read-only base: works.**
   - 3,861 links in 0.5 s.
   - The server only *creates* files: `cfg/{boot,machine_convars,user_convars_0_slot0,user_keys_0_slot0}.vcfg`,
     `bin/win64/steam_appid.txt`, `bin/win64/configs/**`.
   - The base stayed read-only and untouched.
   - Note: a default `deadworks.jsonc` registers the server with the public browser. The launcher
     writes its own before first start.
3. **Steam vs hardlinked client files:** not testable without a real game update. The hardlink mode
   warns conservatively.
4. **depotcache manifests: work.**
   - Plain filenames, a SHA1 per file.
   - 3,862 files and 36,514,514,979 bytes, equal to `SizeOnDisk` in the appmanifest.
   - Full SHA1 verification of the copy took 7 s (warm cache); 3,861/3,862 matched.
   - The only mismatch was the client's `gameinfo.gi`, and stripping the launcher block restores
     Steam's exact hash.
   - The client install held 1,040 non-manifest files (plugins, addons, replays, maps, `bin_cs2`,
     DumpSource2…). None are copied.
5. **SDR, server side: works** with only `CreateListenSocketP2P 2` + `"net_p2p_listen_dedicated" "1"`
   (the stock gameinfo already has the rate block).
   - Log shows `SDR RelayNetworkStatus: avail=OK` and a P2P listen socket.
   - The identity line is `SV:  ServerSteamID=[A:1:570253314:51555] (…)`. It changed between runs,
     as expected.
   - `status` also reports the public IP (`udp/ip : … (public 146.70.168.146:27020)`).
   - **Client join over SDR still needs a human test.**
6. **Private .NET via `DOTNET_ROOT`: works.** `hostfxr` and `coreclr` were loaded from the private
   runtime even with .NET 10 installed system-wide.
7. SteamCMD login: needs real credentials. The flow is implemented from SteamCMD's documented
   prompts; a human test is still owed.

Consequences: `rcon.rs` is dropped. `status` (players: id/time/ping) plus `dw_host_status` (steamid,
hero, team, plugins) replace it, and kicks use `kickid <id>` from `status`.

## Spikes (do first, each answers a yes/no that changes the design)

1. **ConPTY + deadworks.exe.** Does the text console run cleanly under a pseudo console (no GetLine
   spam, output captured, `quit` via stdin, no visible window)? If not, fall back to a hidden real
   console, RCON for commands, and `-condebug` log tailing.
2. **Server from a hardlink tree with a read-only base.** Does the server boot, and does it ever open a
   shipped file for writing?
3. **Steam vs hardlinked client files.** When Steam patches, does it replace files (links break, base
   stays consistent) or patch in place? What does Steam do when a running server holds the file open?
   This decides how strongly the hardlink option warns.
4. **depotcache manifests on Windows.** Readable, with decrypted filenames and SHA1s? The Docker work
   says yes; confirm with the live client install. Fallback: mirror `game\` with an exclusion list.
5. **SDR on a separate install.** Do the gameinfo edits on the server copy alone suffice, with the
   client untouched? Which log line or `status` field carries the `[A:1:…]` identity?
6. **Private .NET via `DOTNET_ROOT`.** Does `DotNetHost` honour it on Windows when no runtime is
   installed system-wide?
7. **SteamCMD under pipes vs ConPTY.** Output buffering, Steam Guard prompt text, the mobile-confirm
   flow.

## Milestones

| # | Scope | Done when |
|---|---|---|
| M0 | Spikes 1-7; `bun install`; debug launcher builds from the worktree | answers written back into this file |
| M1 | Root picker, client-copy base (manifest-driven), release + .NET caches, tree builder, ConPTY process + console, Host tab with the minimal wizard, **LAN + port-forward** modes | a friend on the LAN joins a launcher-made server |
| M2 | SDR mode (per-profile gameinfo, identity capture, churn flag), copy connect command, reachability probe (backend), firewall rule | an off-network friend joins via SDR and via a forwarded port |
| M3 | Plugin library + import + per-profile toggles (live), JSONC config editor, content addons / extra maps, basics + cvars/args, browser-listing toggle | a plugin dropped into a running server loads; a config edit applies without a restart |
| M4 | Multiple concurrent profiles + port allocation, `dw_host_status` + player list, kick, meters, tray | two servers run side by side with correct players and meters |
| M5 | Updater (client build, Deadworks releases, apply-when-empty, hold), SteamCMD path, hardlink copy mode, Settings → Hosting (usage, verify, uninstall) | a simulated game update is picked up with no server running mid-match |
| M6 | Error catalog pass, empty/loading states, copy review, Rust unit tests (kv patch idempotence and DMM coexistence, manifest parsing, tree builder, cfg quoting, status parsing) | a fresh Windows user goes from zero to a running server without reading anything |

## Testing on this machine

- Test hosting root: `H:\deadworks-hosting-test` (C: has ~23 GB free; a base is 36.5 GB).
- Default ports 27020+ stay clear of the dev servers on 27067 / 27090 and the 27015 client crash.
- Launcher-made servers run from their own tree, so they never share `plugins.jsonc` or lock the dev
  `deadworks.exe` like the other sessions' servers do.

## Later (explicitly not v1)

- Curated plugin catalog, install from GitHub URL, bundled game-mode presets
- Invite links, SDR in the server browser, a join button for the host, GSLT for stable SDR IDs
- UPnP, auto-start with Windows, crash auto-restart, scheduled restarts
- Schema-driven plugin config forms, admin auto-grant, Linux (Wine) hosting
- Bans (core ban list + `dw_ban`/`dw_unban`; the engine's ban commands are dev-only)

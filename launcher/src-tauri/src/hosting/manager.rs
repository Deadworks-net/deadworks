//! The hosting core: install state, server profiles, running processes, the
//! background work (tasks, polling, updates), and the events the UI listens to.

use std::collections::BTreeMap;
use std::path::{Path, PathBuf};
use std::sync::{Arc, Mutex, MutexGuard, OnceLock};
use std::time::{Duration, Instant};

use tauri::{AppHandle, Emitter};

use super::console::{self, ConsoleBuffer, Event, HostPlayer, Monitor, StatusRow};
use super::manifest::{self, DepotFile};
use super::process::{self, Process, SpawnSpec, Stream};
use super::store::{self, now_secs, BaseRecord, HostingState, Layout, ServerFile};
use super::task::{Progress, TaskError};
use super::types::*;
use super::{base, cfg, content, disk, dotnet, firewall, fsutil, netcfg, plugins, release, steamcmd, tree};

const STORE_KEY: &str = "hosting_root";
const FIRST_PORT: u16 = 27020;
const STATUS_POLL: Duration = Duration::from_secs(5);
const METRICS_EVERY: Duration = Duration::from_secs(2);
const STOP_GRACE: Duration = Duration::from_secs(20);
/// Runs this long on a build before that build counts as known-good.
const GOOD_AFTER: u64 = 300;
/// Starting with no Steam logon line (e.g. Steam unreachable) still counts as up after this.
const READY_FALLBACK: u64 = 120;
const CLIENT_CHECK_EVERY: Duration = Duration::from_secs(5 * 60);
const RELEASE_CHECK_EVERY: Duration = Duration::from_secs(30 * 60);
const AUTO_UPDATE_RETRY: Duration = Duration::from_secs(30 * 60);

static MANAGER: OnceLock<Arc<Manager>> = OnceLock::new();

pub fn get() -> &'static Arc<Manager> {
    MANAGER.get().expect("hosting not initialised")
}

pub fn init(app: &AppHandle) {
    let root = read_root(app);
    let mut inner = Inner::default();
    if let Some(root) = root.filter(|r| r.join("hosting.json").is_file()) {
        inner.load(Layout::new(root));
    }
    let mgr = Arc::new(Manager { app: app.clone(), inner: Mutex::new(inner) });
    let _ = MANAGER.set(mgr.clone());
    std::thread::Builder::new().name("hosting-ticker".into()).spawn(move || ticker(mgr)).ok();
    std::thread::Builder::new().name("hosting-updater".into()).spawn(|| updater(get().clone())).ok();
}

fn read_root(app: &AppHandle) -> Option<PathBuf> {
    let store = tauri_plugin_store::StoreBuilder::new(app, "settings.json").build().ok()?;
    store.get(STORE_KEY).and_then(|v| v.as_str().map(PathBuf::from))
}

fn write_root(app: &AppHandle, root: Option<&Path>) {
    if let Ok(store) = tauri_plugin_store::StoreBuilder::new(app, "settings.json").build() {
        match root {
            Some(r) => store.set(STORE_KEY, serde_json::json!(r.to_string_lossy())),
            None => {
                store.delete(STORE_KEY);
            }
        }
        let _ = store.save();
    }
}

pub struct Manager {
    app: AppHandle,
    inner: Mutex<Inner>,
}

#[derive(Default)]
struct Inner {
    layout: Option<Layout>,
    state: HostingState,
    base_files: Arc<Vec<DepotFile>>,
    servers: BTreeMap<String, Server>,
    task: Option<Arc<Progress>>,
    last_task: Option<TaskProgress>,
    available_build: Option<String>,
    last_auto_update: Option<Instant>,
}

impl Inner {
    fn load(&mut self, layout: Layout) {
        self.state = store::load_state(&layout);
        self.base_files = Arc::new(store::load_base_files(&layout));
        self.servers = store::load_servers(&layout)
            .into_iter()
            .map(|f| (f.config.id.clone(), Server::new(f)))
            .collect();
        self.layout = Some(layout);
    }

    fn installed(&self) -> bool {
        self.layout.is_some()
            && self.state.base.is_some()
            && self.state.deadworks_tag.is_some()
            && self.state.dotnet_version.is_some()
    }

    fn layout(&self) -> Result<Layout, String> {
        self.layout.clone().ok_or_else(|| "Server hosting isn't set up yet.".to_string())
    }

    fn server(&mut self, id: &str) -> Result<&mut Server, String> {
        self.servers.get_mut(id).ok_or_else(|| "That server doesn't exist any more.".to_string())
    }

    fn save_state(&self) {
        if let Some(l) = &self.layout {
            if let Err(e) = store::save_state(l, &self.state) {
                println!("[hosting] couldn't save state: {e}");
            }
        }
    }
}

struct Server {
    file: ServerFile,
    rt: Runtime,
    console: ConsoleBuffer,
    monitor: Monitor,
    proc: Option<Arc<Process>>,
    /// Commands the user typed whose echo hasn't come back yet; the echo is
    /// shown as their input line instead of as output.
    echoes: std::collections::VecDeque<String>,
}

#[derive(Default)]
struct Runtime {
    state: Option<ServerState>,
    run: u64,
    started_at: Option<u64>,
    /// Network settings the running process was started with.
    network: Option<NetworkMode>,
    port: u16,
    status_rows: Vec<StatusRow>,
    host_players: Option<Vec<HostPlayer>>,
    host_partial: Vec<HostPlayer>,
    supports_host_status: Option<bool>,
    players: Vec<PlayerInfo>,
    cpu: f32,
    memory: u64,
    message: Option<String>,
    exit_code: Option<i32>,
    sdr_id: Option<String>,
    public_addr: Option<String>,
    reachability: Option<Reachability>,
    stop_requested: bool,
    restart_after_stop: bool,
    unsupported_build: bool,
    recorded_good: bool,
    last_poll: Option<Instant>,
}

impl Server {
    fn new(file: ServerFile) -> Self {
        Self { file, rt: Runtime::default(), console: ConsoleBuffer::default(), monitor: Monitor::default(), proc: None, echoes: Default::default() }
    }

    fn state(&self) -> ServerState {
        self.rt.state.unwrap_or(ServerState::Stopped)
    }

    fn live(&self) -> bool {
        matches!(self.state(), ServerState::Starting | ServerState::Running | ServerState::Stopping)
    }

    fn runtime(&self) -> ServerRuntime {
        let c = &self.file.config;
        let mode = self.rt.network.unwrap_or(c.network);
        let port = if self.live() { self.rt.port } else { c.port };
        let lan_ips: Vec<String> = netcfg::primary_lan_ip().map(|ip| ip.to_string()).into_iter().collect();
        let address = match mode {
            NetworkMode::Sdr => self.rt.sdr_id.clone(),
            NetworkMode::PortForward => self.rt.public_addr.clone(),
            NetworkMode::Lan => lan_ips.first().map(|ip| format!("{ip}:{port}")),
        };
        let connect_command = address.filter(|_| self.live()).map(|a| {
            if c.password.is_empty() {
                format!("connect {a}")
            } else {
                format!("password \"{}\"; connect {a}", c.password)
            }
        });
        let sdr_id_changed = mode == NetworkMode::Sdr
            && matches!((&self.rt.sdr_id, &self.file.last_shared_sdr_id), (Some(now), Some(shared)) if now != shared);
        ServerRuntime {
            id: c.id.clone(),
            state: self.state(),
            pid: self.proc.as_ref().map(|p| p.pid),
            started_at: self.rt.started_at,
            players: self.rt.players.clone(),
            cpu_percent: self.rt.cpu,
            memory_bytes: self.rt.memory,
            message: self.rt.message.clone(),
            exit_code: self.rt.exit_code,
            network: NetworkInfo {
                mode,
                port,
                lan_ips,
                public_ip: self.rt.public_addr.as_ref().map(|a| a.rsplit_once(':').map(|(ip, _)| ip).unwrap_or(a).to_string()),
                reachability: self.rt.reachability.unwrap_or(Reachability::Unknown),
                sdr_id: self.rt.sdr_id.clone(),
                sdr_id_changed,
                connect_command,
            },
        }
    }
}

impl Manager {
    fn lock(&self) -> MutexGuard<'_, Inner> {
        self.inner.lock().unwrap_or_else(|e| e.into_inner())
    }

    fn emit_changed(&self) {
        let _ = self.app.emit("hosting://changed", ());
    }

    fn emit_runtime_of(&self, s: &Server) {
        let _ = self.app.emit("hosting://runtime", s.runtime());
    }

    // ── Overview ──

    pub fn overview(&self) -> HostingOverview {
        let inner = self.lock();
        let base = inner.state.base.as_ref().map(|b| BaseInfo {
            source: b.source,
            copy_mode: b.copy_mode,
            build_id: b.build_id.clone(),
            size_bytes: b.size_bytes,
            modified_files: b.modified_files.clone(),
        });
        HostingOverview {
            installed: inner.installed(),
            root: inner.layout.as_ref().map(|l| l.root.to_string_lossy().into_owned()),
            base,
            dotnet_version: inner.state.dotnet_version.clone(),
            task: inner.task.as_ref().map(|t| t.snapshot()).or_else(|| inner.last_task.clone()),
            updates: Self::update_state(&inner),
            servers: inner
                .servers
                .values()
                .map(|s| ServerSummary { config: s.file.config.clone(), runtime: s.runtime() })
                .collect(),
        }
    }

    fn update_state(inner: &Inner) -> UpdateState {
        let installed_build = inner.state.base.as_ref().map(|b| b.build_id.clone());
        let pending = Self::pending(inner);
        UpdateState {
            available_build_id: inner.available_build.clone().or_else(|| installed_build.clone()),
            installed_build_id: installed_build,
            deadworks_installed: inner.state.deadworks_tag.clone(),
            deadworks_latest: inner.state.deadworks_latest.clone(),
            pending,
            hold_reason: inner.state.hold_reason.clone(),
            last_check: inner.state.last_check,
        }
    }

    fn pending(inner: &Inner) -> bool {
        let Some(base) = &inner.state.base else { return false };
        let game = inner.available_build.as_ref().is_some_and(|b| *b != base.build_id);
        let dw = match (&inner.state.deadworks_latest, &inner.state.deadworks_tag) {
            (Some(latest), Some(cur)) => latest != cur,
            _ => false,
        };
        game || dw
    }

    pub fn setup_check(&self) -> SetupCheck {
        let game_dir = crate::connect::resolve_game_dir().ok().filter(|d| d.join("citadel").is_dir());
        let app = game_dir
            .as_deref()
            .and_then(manifest::appmanifest_path_for_game_dir)
            .and_then(|p| manifest::read_appmanifest(&p).ok());
        let drives = disk::drives(game_dir.as_deref());
        SetupCheck {
            client_game_dir: game_dir.as_ref().map(|d| d.to_string_lossy().into_owned()),
            client_build_id: app.as_ref().map(|a| a.build_id.clone()),
            client_updating: app.as_ref().is_some_and(|a| a.updating()),
            required_bytes: app.as_ref().map(|a| a.size_on_disk).unwrap_or(0),
            suggested_root: disk::suggested_root(&drives),
            drives,
        }
    }

    // ── Tasks ──

    fn run_task<F>(self: &Arc<Self>, kind: TaskKind, work: F) -> Result<(), String>
    where
        F: FnOnce(&Arc<Manager>, &Progress) -> Result<(), TaskError> + Send + 'static,
    {
        let progress = {
            let mut inner = self.lock();
            if inner.task.is_some() {
                return Err("Another hosting task is still running.".into());
            }
            let p = Arc::new(Progress::new(kind));
            inner.task = Some(p.clone());
            inner.last_task = None;
            p
        };
        let mgr = self.clone();
        std::thread::spawn(move || {
            let done = Arc::new(std::sync::atomic::AtomicBool::new(false));
            let reporter = {
                let (mgr, p, done) = (mgr.clone(), progress.clone(), done.clone());
                std::thread::spawn(move || {
                    while !done.load(std::sync::atomic::Ordering::Relaxed) {
                        let _ = mgr.app.emit("hosting://task", p.snapshot());
                        std::thread::sleep(Duration::from_millis(250));
                    }
                })
            };
            let result = work(&mgr, &progress);
            done.store(true, std::sync::atomic::Ordering::Relaxed);
            let _ = reporter.join();
            let mut snap = progress.snapshot();
            snap.finished = true;
            snap.steam_prompt = None;
            match result {
                Ok(()) => {
                    snap.stage = "done".into();
                    snap.label = "Done".into();
                }
                Err(e) => {
                    snap.error = Some(e.message);
                    snap.modified_files = e.modified;
                }
            }
            {
                let mut inner = mgr.lock();
                inner.task = None;
                inner.last_task = Some(snap.clone());
            }
            let _ = mgr.app.emit("hosting://task", snap);
            mgr.emit_changed();
        });
        Ok(())
    }

    pub fn cancel_task(&self) {
        if let Some(t) = &self.lock().task {
            t.cancel.store(true, std::sync::atomic::Ordering::Relaxed);
        }
    }

    pub fn steamcmd_input(&self, value: String) -> Result<(), String> {
        match &self.lock().task {
            Some(t) => {
                t.submit_input(value);
                Ok(())
            }
            None => Err("SteamCMD isn't waiting for anything.".into()),
        }
    }

    // ── Install / update / verify / uninstall ──

    pub fn install(self: &Arc<Self>, opts: InstallOptions) -> Result<(), String> {
        if self.lock().servers.values().any(Server::live) {
            return Err("Stop your servers before reinstalling.".into());
        }
        let root = choose_root(&opts.root)?;
        self.run_task(TaskKind::Install, move |mgr, p| mgr.do_install(root, opts, p))
    }

    fn do_install(self: &Arc<Self>, root: PathBuf, opts: InstallOptions, p: &Progress) -> Result<(), TaskError> {
        std::fs::create_dir_all(&root).map_err(|e| format!("Couldn't create {}: {e}", root.display()))?;
        let layout = Layout::new(&root);
        {
            let mut inner = self.lock();
            if inner.layout.as_ref().map(|l| &l.root) != Some(&root) {
                inner.load(layout.clone());
            }
            // Mark the folder as ours right away, so a retry after a failed or
            // cancelled install reuses it instead of nesting a new one inside.
            inner.save_state();
        }
        write_root(&self.app, Some(&root));
        self.emit_changed();

        let previous = store::load_base_files(&layout);
        let generation = self.lock().state.base.as_ref().map(|b| b.generation).unwrap_or(0);
        let record = match opts.source {
            BaseSource::Client => {
                let game_dir = crate::connect::resolve_game_dir()
                    .ok()
                    .filter(|d| d.join("citadel").is_dir())
                    .ok_or("Deadlock isn't installed on this PC. Install it in Steam, or use SteamCMD instead.")?;
                if opts.copy_mode == CopyMode::Hardlink && fsutil::volume_of(&game_dir) != fsutil::volume_of(&root) {
                    return Err("Linking only works when the server folder is on the same drive as Deadlock.".into());
                }
                let (app, files) = client_manifest(&game_dir)?;
                let outcome = base::sync_from_client(
                    &layout.base_dir(),
                    &base::install_dir_of(&game_dir),
                    &files,
                    &previous,
                    opts.copy_mode,
                    opts.allow_modified,
                    p,
                )?;
                store::save_base_files(&layout, &files)?;
                BaseRecord {
                    source: BaseSource::Client,
                    copy_mode: opts.copy_mode,
                    build_id: app.build_id,
                    depots: app.depots,
                    size_bytes: outcome.size_bytes,
                    modified_files: outcome.modified_files,
                    generation: generation + 1,
                }
            }
            BaseSource::Steamcmd => {
                let user = opts.steam_username.clone().unwrap_or_default();
                steamcmd::install(&layout, &user, opts.steam_password.as_deref(), true, p)?;
                let (app, files) = steamcmd::installed(&layout)?;
                store::save_base_files(&layout, &files)?;
                let mut inner = self.lock();
                inner.state.steam_username = Some(user.trim().to_string());
                drop(inner);
                BaseRecord {
                    source: BaseSource::Steamcmd,
                    copy_mode: CopyMode::Copy,
                    build_id: app.build_id,
                    depots: app.depots,
                    size_bytes: files.iter().map(|f| f.size).sum(),
                    modified_files: Vec::new(),
                    generation: generation + 1,
                }
            }
        };
        {
            let mut inner = self.lock();
            inner.available_build = Some(record.build_id.clone());
            inner.state.base = Some(record);
            inner.base_files = Arc::new(store::load_base_files(&layout));
            inner.save_state();
        }
        self.install_runtime(&layout, p)?;
        Ok(())
    }

    /// Deadworks (latest release) and .NET.
    fn install_runtime(&self, layout: &Layout, p: &Progress) -> Result<(), TaskError> {
        p.check_cancel()?;
        let current = self.lock().state.deadworks_tag.clone();
        let tag = match release::latest_tag() {
            Ok(t) => t,
            Err(e) => current.clone().filter(|t| release::is_installed(layout, t)).ok_or(e)?,
        };
        release::ensure(layout, &tag, p)?;
        release::prune(layout, &tag);
        p.check_cancel()?;
        let dotnet_current = self.lock().state.dotnet_version.clone();
        let dn = dotnet::ensure(layout, dotnet_current.as_deref(), p)?;
        let mut inner = self.lock();
        if inner.state.hold_deadworks.as_deref().is_some_and(|h| h != tag) {
            inner.state.hold_reason = None;
            inner.state.hold_deadworks = None;
        }
        inner.state.deadworks_tag = Some(tag.clone());
        inner.state.deadworks_latest = Some(tag);
        inner.state.dotnet_version = Some(dn.clone());
        inner.state.last_check = Some(now_secs());
        inner.save_state();
        drop(inner);
        if !self.lock().servers.values().any(Server::live) {
            dotnet::prune(layout, &dn);
        }
        Ok(())
    }

    pub fn apply_updates(self: &Arc<Self>) -> Result<(), String> {
        self.lock().layout()?;
        self.run_task(TaskKind::Update, |mgr, p| mgr.do_update(p))
    }

    fn do_update(self: &Arc<Self>, p: &Progress) -> Result<(), TaskError> {
        let (layout, record, previous, username) = {
            let inner = self.lock();
            let record = inner.state.base.clone().ok_or("Server hosting isn't installed.")?;
            (inner.layout()?, record, inner.base_files.clone(), inner.state.steam_username.clone())
        };
        let was_running = self.stop_all_and_wait(p)?;
        let result = (|| -> Result<(), TaskError> {
            let record = match record.source {
                BaseSource::Client => {
                    let game_dir = crate::connect::resolve_game_dir().map_err(|_| "Deadlock isn't installed on this PC any more.")?;
                    let (app, files) = client_manifest(&game_dir)?;
                    if app.build_id == record.build_id && previous.len() == files.len() {
                        record
                    } else {
                        let outcome = base::sync_from_client(
                            &layout.base_dir(),
                            &base::install_dir_of(&game_dir),
                            &files,
                            &previous,
                            record.copy_mode,
                            !record.modified_files.is_empty(),
                            p,
                        )?;
                        store::save_base_files(&layout, &files)?;
                        BaseRecord {
                            build_id: app.build_id,
                            depots: app.depots,
                            size_bytes: outcome.size_bytes,
                            modified_files: outcome.modified_files,
                            generation: record.generation + 1,
                            ..record
                        }
                    }
                }
                BaseSource::Steamcmd => {
                    let user = username.ok_or("No Steam account is saved for updates; reinstall with SteamCMD.")?;
                    steamcmd::install(&layout, &user, None, false, p)?;
                    let (app, files) = steamcmd::installed(&layout)?;
                    store::save_base_files(&layout, &files)?;
                    BaseRecord {
                        build_id: app.build_id,
                        depots: app.depots,
                        size_bytes: files.iter().map(|f| f.size).sum(),
                        generation: record.generation + 1,
                        ..record
                    }
                }
            };
            {
                let mut inner = self.lock();
                inner.available_build = Some(record.build_id.clone());
                inner.state.base = Some(record);
                inner.base_files = Arc::new(store::load_base_files(&layout));
                inner.save_state();
            }
            self.install_runtime(&layout, p)
        })();
        let mut restart = was_running;
        if result.is_ok() {
            // Servers held for an unsupported game build come back once the hold lifts.
            let inner = self.lock();
            if inner.state.hold_reason.is_none() {
                restart.extend(
                    inner
                        .servers
                        .values()
                        .filter(|s| s.state() == ServerState::WaitingForDeadworks)
                        .map(|s| s.file.config.id.clone()),
                );
            }
        }
        for id in restart {
            if let Err(e) = self.start_inner(&id, true) {
                self.sys_line(&id, &format!("Couldn't restart after the update: {e}"));
            }
        }
        result
    }

    /// Stop every live server for an update; returns the ones to start again.
    fn stop_all_and_wait(self: &Arc<Self>, p: &Progress) -> Result<Vec<String>, String> {
        let live: Vec<String> = self.lock().servers.values().filter(|s| s.live()).map(|s| s.file.config.id.clone()).collect();
        if live.is_empty() {
            return Ok(live);
        }
        p.stage("stopping", "Stopping servers", 0, live.len() as u64);
        for id in &live {
            let _ = self.stop(id);
        }
        let deadline = Instant::now() + STOP_GRACE + Duration::from_secs(5);
        while Instant::now() < deadline {
            if !self.lock().servers.values().any(Server::live) {
                break;
            }
            std::thread::sleep(Duration::from_millis(250));
        }
        let mut inner = self.lock();
        for id in &live {
            if let Some(s) = inner.servers.get_mut(id) {
                if !s.live() {
                    s.rt.state = Some(ServerState::Updating);
                }
            }
        }
        drop(inner);
        self.emit_changed();
        Ok(live)
    }

    pub fn verify(self: &Arc<Self>) -> Result<(), String> {
        self.lock().layout()?;
        self.run_task(TaskKind::Verify, |mgr, p| {
            let (layout, record, files, user) = {
                let inner = mgr.lock();
                let record = inner.state.base.clone().ok_or("Server hosting isn't installed.")?;
                (inner.layout()?, record, inner.base_files.clone(), inner.state.steam_username.clone())
            };
            if record.source == BaseSource::Steamcmd {
                let user = user.ok_or("No Steam account is saved; reinstall with SteamCMD.")?;
                let was_running = mgr.stop_all_and_wait(p)?;
                let r = steamcmd::install(&layout, &user, None, true, p);
                for id in was_running {
                    let _ = mgr.start_inner(&id, true);
                }
                return r.map_err(Into::into);
            }
            let client = crate::connect::resolve_game_dir().ok().map(|g| base::install_dir_of(&g));
            let outcome = base::verify(&layout.base_dir(), client.as_deref(), &files, record.copy_mode, p)?;
            if outcome.repaired > 0 {
                let mut inner = mgr.lock();
                if let Some(b) = &mut inner.state.base {
                    b.generation += 1;
                }
                inner.save_state();
            }
            let broken = outcome.still_broken;
            if broken.is_empty() {
                Ok(())
            } else {
                Err(TaskError {
                    message: format!(
                        "{} game file{} couldn't be repaired from your Deadlock install. Repair Deadlock with Steam, then verify again.",
                        broken.len(),
                        if broken.len() == 1 { "" } else { "s" }
                    ),
                    modified: broken,
                })
            }
        })
    }

    pub fn uninstall(self: &Arc<Self>) -> Result<(), String> {
        let layout = self.lock().layout()?;
        self.run_task(TaskKind::Uninstall, move |mgr, p| {
            mgr.stop_all_and_wait(p)?;
            p.stage("uninstall", "Removing server files", 0, 0);
            // Only what hosting created: never the whole folder, which the user picked.
            for dir in [layout.base_dir(), layout.root.join("cache"), layout.plugins_dir(), layout.servers_dir()] {
                fsutil::remove_dir_all_force(&dir).map_err(|e| format!("Couldn't remove {}: {e}", dir.display()))?;
            }
            for f in [layout.state_file(), layout.base_files_file()] {
                let _ = fsutil::remove_file_force(&f);
            }
            let _ = std::fs::remove_dir(&layout.root);
            *mgr.lock() = Inner::default();
            write_root(&mgr.app, None);
            Ok(())
        })
    }

    // ── Servers ──

    pub fn maps(&self) -> Vec<String> {
        let inner = self.lock();
        let mut maps: Vec<String> = inner
            .layout
            .as_ref()
            .and_then(|l| std::fs::read_dir(l.base_game_dir().join("citadel").join("maps")).ok())
            .map(|rd| {
                rd.flatten()
                    .filter_map(|e| e.file_name().to_str()?.strip_suffix(".vpk").map(String::from))
                    .collect()
            })
            .unwrap_or_default();
        maps.sort_by_key(|m| (m != "dl_midtown", m.clone()));
        if maps.is_empty() {
            maps.push("dl_midtown".into());
        }
        maps
    }

    pub fn create_server(&self, name: String, network: NetworkMode) -> Result<ServerConfig, String> {
        let mut inner = self.lock();
        let layout = inner.layout()?;
        let config = ServerConfig {
            id: new_id(&name),
            name: name.trim().to_string(),
            password: String::new(),
            max_players: 12,
            map: "dl_midtown".into(),
            port: free_port(&inner, None),
            network,
            listed: false,
            cheats: false,
            hibernate_when_empty: true,
            plugins: Vec::new(),
            cvars: Vec::new(),
            launch_args: Vec::new(),
            content_addons: Vec::new(),
            extra_maps: Vec::new(),
        };
        cfg::validate(&config)?;
        let file = ServerFile { config: config.clone(), last_shared_sdr_id: None, created_at: now_secs() };
        store::save_server(&layout, &file)?;
        inner.servers.insert(config.id.clone(), Server::new(file));
        drop(inner);
        self.emit_changed();
        Ok(config)
    }

    pub fn update_server(&self, mut config: ServerConfig) -> Result<ServerConfig, String> {
        config.name = config.name.trim().to_string();
        if config.network != NetworkMode::PortForward {
            config.listed = false;
        }
        cfg::validate(&config)?;
        let mut inner = self.lock();
        let layout = inner.layout()?;
        let clash = inner
            .servers
            .values()
            .any(|s| s.file.config.id != config.id && s.file.config.port == config.port);
        if clash {
            return Err(format!("Another server already uses port {}. Pick a different one.", config.port));
        }
        let s = inner.server(&config.id)?;
        s.file.config = config.clone();
        store::save_server(&layout, &s.file)?;
        drop(inner);
        self.emit_changed();
        Ok(config)
    }

    pub fn duplicate_server(&self, id: &str) -> Result<ServerConfig, String> {
        let mut inner = self.lock();
        let layout = inner.layout()?;
        let src = inner.server(id)?.file.config.clone();
        let mut config = src.clone();
        config.name = format!("{} (copy)", src.name).chars().take(64).collect();
        config.id = new_id(&config.name);
        config.port = free_port(&inner, None);
        let file = ServerFile { config: config.clone(), last_shared_sdr_id: None, created_at: now_secs() };
        store::save_server(&layout, &file)?;
        // Plugin settings and content come along; the server-browser identity must not.
        copy_dir(&layout.server_configs(id), &layout.server_configs(&config.id), &["credentials.json"]);
        copy_dir(&layout.server_content(id), &layout.server_content(&config.id), &[]);
        inner.servers.insert(config.id.clone(), Server::new(file));
        drop(inner);
        self.emit_changed();
        Ok(config)
    }

    pub fn delete_server(&self, id: &str) -> Result<(), String> {
        let mut inner = self.lock();
        let layout = inner.layout()?;
        if inner.server(id)?.live() {
            return Err("Stop the server before deleting it.".into());
        }
        inner.servers.remove(id);
        drop(inner);
        fsutil::remove_dir_all_force(&layout.server_dir(id)).map_err(|e| format!("Couldn't delete the server's files: {e}"))?;
        self.emit_changed();
        Ok(())
    }

    fn sys_line(&self, id: &str, text: &str) {
        if let Some(s) = self.lock().servers.get_mut(id) {
            s.console.push(LineKind::Sys, text);
        }
    }

    pub fn start(self: &Arc<Self>, id: &str) -> Result<(), String> {
        self.start_inner(id, false)
    }

    /// `from_task`: called by the update task itself to bring servers back.
    fn start_inner(self: &Arc<Self>, id: &str, from_task: bool) -> Result<(), String> {
        let (layout, config, base_files, base_stamp, tag, dotnet_version, run) = {
            let mut inner = self.lock();
            if !inner.installed() {
                return Err("Server hosting isn't installed yet.".into());
            }
            if !from_task && inner.task.as_ref().is_some_and(|t| t.kind != TaskKind::Verify) {
                return Err("Wait for the current install or update to finish.".into());
            }
            let tag = inner.state.deadworks_tag.clone().unwrap_or_default();
            if let (Some(reason), Some(hold)) = (&inner.state.hold_reason, &inner.state.hold_deadworks) {
                if *hold == tag {
                    return Err(reason.clone());
                }
            }
            let layout = inner.layout()?;
            let base = inner.state.base.clone().unwrap();
            let dotnet_version = inner.state.dotnet_version.clone().unwrap_or_default();
            let base_files = inner.base_files.clone();
            let others: Vec<u16> = inner
                .servers
                .values()
                .filter(|s| s.file.config.id != id && s.live())
                .map(|s| s.rt.port)
                .collect();
            let s = inner.server(id)?;
            if s.live() {
                return Ok(());
            }
            let config = s.file.config.clone();
            if others.contains(&config.port) {
                return Err(format!("Another running server uses port {}. Change this server's port in Settings.", config.port));
            }
            if !netcfg::port_free(config.port) {
                return Err(format!(
                    "Port {} is already in use by another program. Pick a different port in Settings.",
                    config.port
                ));
            }
            s.rt = Runtime {
                state: Some(ServerState::Starting),
                run: s.rt.run + 1,
                network: Some(config.network),
                port: config.port,
                ..Runtime::default()
            };
            s.monitor = Monitor::default();
            s.console.open_log(&layout.server_logs(id));
            s.console.push(LineKind::Sys, format!("Starting {} (Deadworks {tag}, game build {})", config.name, base.build_id));
            self.emit_runtime_of(s);
            (layout, config, base_files, format!("{}:{}", base.build_id, base.generation), tag, dotnet_version, s.rt.run)
        };

        let mgr = self.clone();
        let id = id.to_string();
        std::thread::spawn(move || {
            let result = (|| -> Result<Process, String> {
                tree::prepare(&tree::TreeInputs {
                    layout: &layout,
                    config: &config,
                    base_files: &base_files,
                    base_stamp,
                    release_dir: &release::release_dir(&layout, &tag),
                })?;
                let bin = layout.server_bin(&id);
                let rule = firewall::rule_name(&id);
                // Automated test runs can't answer a UAC prompt.
                let skip = cfg!(debug_assertions) && std::env::var_os("DEADWORKS_SKIP_FIREWALL").is_some();
                if !skip && !firewall::rule_exists(&rule) {
                    mgr.sys_line(&id, "Asking Windows to let this server accept connections (one-time permission prompt)...");
                    if let Err(e) = firewall::allow_program(&rule, &bin.join("deadworks.exe")) {
                        let hint = if config.network == NetworkMode::Sdr {
                            "Players can still join through Steam's relay."
                        } else {
                            "Players on other PCs may not be able to connect until it's allowed."
                        };
                        mgr.sys_line(&id, &format!("{e} {hint}"));
                    }
                }
                let (m1, id1, m2, id2) = (mgr.clone(), id.clone(), mgr.clone(), id.clone());
                process::spawn(
                    SpawnSpec {
                        exe: bin.join("deadworks.exe"),
                        cwd: bin,
                        args: cfg::argv(&config),
                        env: vec![(
                            "DOTNET_ROOT".into(),
                            dotnet::runtime_dir(&layout, &dotnet_version).to_string_lossy().into_owned(),
                        )],
                    },
                    move |stream, line| m1.on_line(&id1, run, stream, line),
                    move |code| m2.on_exit(&id2, run, code),
                )
            })();
            let mut inner = mgr.lock();
            let Some(s) = inner.servers.get_mut(&id) else { return };
            if s.rt.run != run {
                return;
            }
            match result {
                Ok(p) => {
                    s.rt.started_at = Some(now_secs());
                    s.proc = Some(Arc::new(p));
                    // Stop was pressed while the tree was still being prepared.
                    if s.rt.stop_requested {
                        drop(inner);
                        let _ = mgr.stop(&id);
                        return;
                    }
                }
                Err(e) => {
                    s.console.push(LineKind::Sys, format!("Couldn't start: {e}"));
                    s.console.close_log();
                    s.rt.state = Some(ServerState::Crashed);
                    s.rt.message = Some(e);
                }
            }
            mgr.emit_runtime_of(s);
        });
        Ok(())
    }

    fn on_line(&self, id: &str, run: u64, stream: Stream, line: String) {
        let mut followup = None;
        let mut inner = self.lock();
        let Some(s) = inner.servers.get_mut(id) else { return };
        if s.rt.run != run {
            return;
        }
        let (show, events) = s.monitor.feed(&line);
        if show {
            let _ = stream;
            if s.echoes.front().is_some_and(|c| *c == line.trim()) {
                s.echoes.pop_front();
                s.console.push(LineKind::In, line);
            } else {
                s.console.push(LineKind::Out, line);
            }
        }
        let mut changed = false;
        for ev in events {
            changed = true;
            match ev {
                Event::Ready => {
                    if s.rt.state == Some(ServerState::Starting) {
                        s.rt.state = Some(ServerState::Running);
                        s.console.push(LineKind::Sys, "Server is up.");
                    }
                }
                Event::SdrId(sdr) => s.rt.sdr_id = Some(sdr),
                Event::RelayReady => {}
                Event::PublicAddress(a) => s.rt.public_addr = Some(a),
                Event::UnsupportedBuild => s.rt.unsupported_build = true,
                Event::StatusPlayers(rows) => {
                    s.rt.status_rows = rows;
                    s.rt.players = console::merge_players(s.rt.host_players.as_deref(), &s.rt.status_rows);
                }
                Event::HostStatus(hs) => {
                    s.rt.supports_host_status = Some(true);
                    s.rt.host_partial.extend(hs.players);
                    match hs.next {
                        Some(next) => followup = Some((s.proc.as_ref().map(|p| p.pid), format!("dw_host_status {next}"))),
                        None => {
                            s.rt.host_players = Some(std::mem::take(&mut s.rt.host_partial));
                            s.rt.players = console::merge_players(s.rt.host_players.as_deref(), &s.rt.status_rows);
                        }
                    }
                }
                Event::NoHostStatus => s.rt.supports_host_status = Some(false),
            }
        }
        if changed {
            self.emit_runtime_of(s);
        }
        drop(inner);
        if let Some((Some(pid), cmd)) = followup {
            let _ = process::send_line(pid, &cmd);
        }
    }

    fn on_exit(self: &Arc<Self>, id: &str, run: u64, code: i32) {
        let mut restart = false;
        let mut hold_changed = false;
        {
            let mut inner = self.lock();
            let tag = inner.state.deadworks_tag.clone().unwrap_or_default();
            let build = inner.state.base.as_ref().map(|b| b.build_id.clone()).unwrap_or_default();
            let last_good = inner.state.last_good_build.clone();
            let Some(s) = inner.servers.get_mut(id) else { return };
            if s.rt.run != run {
                return;
            }
            s.proc = None;
            let uptime = s.rt.started_at.map(|t| now_secs().saturating_sub(t)).unwrap_or(0);
            s.console.push(LineKind::Sys, format!("Server stopped (exit code {code})."));
            s.console.close_log();
            s.rt.players.clear();
            s.rt.cpu = 0.0;
            s.rt.memory = 0;
            s.rt.exit_code = Some(code);
            if s.rt.stop_requested {
                s.rt.state = Some(ServerState::Stopped);
                s.rt.message = None;
                restart = s.rt.restart_after_stop;
            } else if code == 78 || s.rt.unsupported_build {
                s.rt.state = Some(ServerState::WaitingForDeadworks);
                let reason = format!(
                    "Deadworks {tag} doesn't support Deadlock's latest update (build {build}) yet. \
                     Servers will start again automatically once a Deadworks update is out."
                );
                s.rt.message = Some(reason.clone());
                inner.state.hold_reason = Some(reason);
                inner.state.hold_deadworks = Some(tag);
                inner.save_state();
                hold_changed = true;
            } else {
                s.rt.state = Some(ServerState::Crashed);
                let mut msg = format!("The server stopped unexpectedly (exit code {code}).");
                if uptime < GOOD_AFTER && last_good.as_deref() != Some(build.as_str()) && code != 0 {
                    msg.push_str(" If this keeps happening right after a Deadlock update, Deadworks may need an update too.");
                }
                s.rt.message = Some(msg);
            }
            let s = inner.servers.get(id).unwrap();
            self.emit_runtime_of(s);
        }
        if hold_changed {
            self.emit_changed();
        }
        if restart {
            if let Err(e) = self.start(id) {
                self.sys_line(id, &format!("Couldn't restart: {e}"));
            }
        }
    }

    pub fn stop(self: &Arc<Self>, id: &str) -> Result<(), String> {
        let (proc, run) = {
            let mut inner = self.lock();
            let s = inner.server(id)?;
            let Some(proc) = s.proc.clone() else {
                // Starting but not spawned yet, or already down.
                if s.rt.state == Some(ServerState::Starting) {
                    s.rt.stop_requested = true;
                }
                if !s.live() {
                    s.rt.state = Some(ServerState::Stopped);
                    s.rt.message = None;
                }
                self.emit_runtime_of(s);
                return Ok(());
            };
            s.rt.stop_requested = true;
            s.rt.state = Some(ServerState::Stopping);
            s.console.push(LineKind::Sys, "Stopping...");
            self.emit_runtime_of(s);
            (proc, s.rt.run)
        };
        if process::send_line(proc.pid, "quit").is_err() {
            proc.terminate();
            return Ok(());
        }
        let mgr = self.clone();
        let id = id.to_string();
        std::thread::spawn(move || {
            let deadline = Instant::now() + STOP_GRACE;
            while Instant::now() < deadline {
                std::thread::sleep(Duration::from_millis(250));
                let inner = mgr.lock();
                match inner.servers.get(&id) {
                    Some(s) if s.rt.run == run && s.proc.is_some() => {}
                    _ => return,
                }
            }
            mgr.sys_line(&id, "The server didn't stop in time; forcing it.");
            proc.terminate();
        });
        Ok(())
    }

    pub fn restart(self: &Arc<Self>, id: &str) -> Result<(), String> {
        let live = {
            let mut inner = self.lock();
            let s = inner.server(id)?;
            if s.live() {
                s.rt.restart_after_stop = true;
            }
            s.live()
        };
        if live {
            self.stop(id)
        } else {
            self.start(id)
        }
    }

    /// Stop every server and wait (bounded) for them to exit. For quitting.
    pub fn stop_all_blocking(self: &Arc<Self>) {
        let ids: Vec<String> = self.lock().servers.values().filter(|s| s.live()).map(|s| s.file.config.id.clone()).collect();
        for id in &ids {
            let _ = self.stop(id);
        }
        let deadline = Instant::now() + Duration::from_secs(15);
        while Instant::now() < deadline && self.lock().servers.values().any(|s| s.proc.is_some()) {
            std::thread::sleep(Duration::from_millis(200));
        }
    }

    pub fn running_count(&self) -> usize {
        self.lock().servers.values().filter(|s| s.live()).count()
    }

    pub fn runtime(&self, id: &str) -> Result<ServerRuntime, String> {
        Ok(self.lock().server(id)?.runtime())
    }

    pub fn console_history(&self, id: &str) -> Result<Vec<ConsoleLine>, String> {
        Ok(self.lock().server(id)?.console.history())
    }

    fn live_pid(&self, id: &str) -> Result<u32, String> {
        let mut inner = self.lock();
        let s = inner.server(id)?;
        s.proc.as_ref().map(|p| p.pid).ok_or_else(|| "The server isn't running.".to_string())
    }

    pub fn send_command(&self, id: &str, command: &str) -> Result<(), String> {
        let command = command.trim();
        if command.is_empty() {
            return Ok(());
        }
        if command.contains(['\r', '\n']) {
            return Err("Send one command at a time.".into());
        }
        let pid = self.live_pid(id)?;
        {
            let mut inner = self.lock();
            let s = inner.server(id)?;
            s.echoes.push_back(command.to_string());
            // An echo that never comes (engine busy) mustn't hide later output.
            while s.echoes.len() > 8 {
                s.echoes.pop_front();
            }
        }
        process::send_line(pid, command)
    }

    pub fn kick(&self, id: &str, slot: i32) -> Result<(), String> {
        let pid = self.live_pid(id)?;
        let supports = self.lock().server(id)?.rt.supports_host_status == Some(true);
        // Without dw_host_status the "slot" is status's user id.
        let cmd = if supports { format!("dw_kick {slot}") } else { format!("kickid {slot}") };
        process::send_line(pid, &cmd)
    }

    pub fn mark_shared(&self, id: &str) -> Result<(), String> {
        let mut inner = self.lock();
        let layout = inner.layout()?;
        let s = inner.server(id)?;
        if let Some(sdr) = s.rt.sdr_id.clone() {
            s.file.last_shared_sdr_id = Some(sdr);
            store::save_server(&layout, &s.file)?;
        }
        self.emit_runtime_of(s);
        Ok(())
    }

    pub fn check_reachability(&self, id: &str) -> Result<NetworkInfo, String> {
        let port = {
            let mut inner = self.lock();
            let s = inner.server(id)?;
            if !s.live() {
                return Err("Start the server first, then check.".into());
            }
            s.rt.reachability = Some(Reachability::Checking);
            self.emit_runtime_of(s);
            s.rt.port
        };
        let api = crate::addons::resolve_api_url(&self.app);
        let (reach, ip) = reachability_check(&api, port);
        let mut inner = self.lock();
        let s = inner.server(id)?;
        s.rt.reachability = Some(reach);
        if s.rt.public_addr.is_none() {
            s.rt.public_addr = ip.map(|ip| format!("{ip}:{port}"));
        }
        self.emit_runtime_of(s);
        Ok(s.runtime().network)
    }

    pub fn open_folder(&self, id: Option<&str>) -> Result<(), String> {
        let inner = self.lock();
        let layout = inner.layout()?;
        let dir = match id {
            Some(id) => layout.server_dir(id),
            None => layout.root.clone(),
        };
        drop(inner);
        open::that(&dir).map_err(|e| format!("Couldn't open {}: {e}", dir.display()))
    }

    // ── Plugins ──

    pub fn plugin_library(&self) -> Vec<PluginEntry> {
        self.lock().layout.as_ref().map(plugins::library).unwrap_or_default()
    }

    pub fn import_plugins(&self, paths: Vec<String>) -> Result<Vec<PluginEntry>, String> {
        let layout = self.lock().layout()?;
        let out = plugins::import(&layout, &paths)?;
        // Servers running an updated plugin get the new files right away.
        let live: Vec<ServerConfig> = self
            .lock()
            .servers
            .values()
            .filter(|s| s.live() && out.iter().any(|e| s.file.config.plugins.contains(&e.id)))
            .map(|s| s.file.config.clone())
            .collect();
        for c in live {
            let _ = tree::apply_plugins(&layout, &c);
        }
        self.emit_changed();
        Ok(out)
    }

    pub fn remove_plugin(&self, plugin_id: &str) -> Result<(), String> {
        let layout = self.lock().layout()?;
        let affected: Vec<String> = self
            .lock()
            .servers
            .values()
            .filter(|s| s.file.config.plugins.iter().any(|p| p == plugin_id))
            .map(|s| s.file.config.id.clone())
            .collect();
        for id in &affected {
            self.set_plugin_enabled(id, plugin_id, false)?;
        }
        plugins::remove(&layout, plugin_id)?;
        self.emit_changed();
        Ok(())
    }

    pub fn set_plugin_enabled(&self, id: &str, plugin_id: &str, enabled: bool) -> Result<ServerConfig, String> {
        let (layout, config, pid) = {
            let mut inner = self.lock();
            let layout = inner.layout()?;
            if enabled && plugins::entry(&layout, plugin_id).is_none() {
                return Err("That plugin isn't in the library any more.".into());
            }
            let s = inner.server(id)?;
            let list = &mut s.file.config.plugins;
            list.retain(|p| p != plugin_id);
            if enabled {
                list.push(plugin_id.to_string());
            }
            store::save_server(&layout, &s.file)?;
            (layout, s.file.config.clone(), s.proc.as_ref().map(|p| p.pid))
        };
        if let Some(pid) = pid {
            // Deleting a DLL doesn't unload it, so unload first; enabling needs the
            // files in place before Deadworks is told to load them.
            if !enabled {
                let _ = process::send_line(pid, &format!("dw_plugin disable {plugin_id}"));
            }
            tree::apply_plugins(&layout, &config)?;
            if enabled {
                let _ = process::send_line(pid, &format!("dw_plugin enable {plugin_id}"));
            }
        }
        self.emit_changed();
        Ok(config)
    }

    pub fn plugin_configs(&self, id: &str) -> Result<Vec<PluginConfigFile>, String> {
        let mut inner = self.lock();
        let layout = inner.layout()?;
        let enabled = inner.server(id)?.file.config.plugins.clone();
        let dir = layout.server_configs(id);
        let mut out: Vec<PluginConfigFile> = enabled
            .iter()
            .map(|p| {
                let path = plugin_config_path(&dir, p);
                PluginConfigFile { plugin_id: p.clone(), exists: path.is_file(), path: path.to_string_lossy().into_owned() }
            })
            .collect();
        // Plugins whose class name differs from their DLL name keep their config under the class name.
        if let Ok(rd) = std::fs::read_dir(&dir) {
            for e in rd.flatten() {
                let name = e.file_name().to_string_lossy().into_owned();
                let path = plugin_config_path(&dir, &name);
                if name != "ServerBrowser" && path.is_file() && !out.iter().any(|c| c.plugin_id == name) {
                    out.push(PluginConfigFile { plugin_id: name, exists: true, path: path.to_string_lossy().into_owned() });
                }
            }
        }
        Ok(out)
    }

    fn config_file(&self, id: &str, plugin_id: &str) -> Result<(PathBuf, Option<u32>), String> {
        if plugin_id.is_empty() || !plugin_id.chars().all(|c| c.is_ascii_alphanumeric() || matches!(c, '_' | '-' | '.')) {
            return Err("Unknown plugin".into());
        }
        let mut inner = self.lock();
        let layout = inner.layout()?;
        let pid = inner.server(id)?.proc.as_ref().map(|p| p.pid);
        Ok((plugin_config_path(&layout.server_configs(id), plugin_id), pid))
    }

    pub fn read_plugin_config(&self, id: &str, plugin_id: &str) -> Result<String, String> {
        let (path, _) = self.config_file(id, plugin_id)?;
        std::fs::read_to_string(&path).map_err(|_| "This plugin has no settings file yet. Start the server once to create it.".into())
    }

    pub fn write_plugin_config(&self, id: &str, plugin_id: &str, text: &str) -> Result<(), String> {
        serde_json::from_str::<serde_json::Value>(&cfg::strip_jsonc(text)).map_err(|e| format!("That isn't valid JSON: {e}"))?;
        let (path, pid) = self.config_file(id, plugin_id)?;
        fsutil::write_real(&path, text.as_bytes()).map_err(|e| format!("Couldn't save: {e}"))?;
        if let Some(pid) = pid {
            let _ = process::send_line(pid, "dw_reloadconfig");
        }
        Ok(())
    }

    pub fn reset_plugin_config(&self, id: &str, plugin_id: &str) -> Result<(), String> {
        let (path, pid) = self.config_file(id, plugin_id)?;
        fsutil::remove_file_force(&path).map_err(|e| format!("Couldn't reset: {e}"))?;
        if let Some(pid) = pid {
            let _ = process::send_line(pid, "dw_reloadconfig");
        }
        Ok(())
    }

    // ── Content ──

    pub fn content(&self, id: &str) -> Result<Vec<ContentFile>, String> {
        let mut inner = self.lock();
        let layout = inner.layout()?;
        inner.server(id)?;
        Ok(content::list(&layout, id))
    }

    pub fn import_content(&self, id: &str, paths: Vec<String>, kind: ContentKind) -> Result<Vec<ContentFile>, String> {
        let layout = {
            let mut inner = self.lock();
            let layout = inner.layout()?;
            inner.server(id)?;
            layout
        };
        content::import(&layout, id, &paths, kind)?;
        self.emit_changed();
        Ok(content::list(&layout, id))
    }

    pub fn remove_content(&self, id: &str, file_name: &str) -> Result<(), String> {
        let mut inner = self.lock();
        let layout = inner.layout()?;
        if inner.server(id)?.live() {
            return Err("Stop the server before removing its content.".into());
        }
        content::remove(&layout, id, file_name)?;
        let s = inner.server(id)?;
        let stem = file_name.trim_end_matches(".vpk");
        s.file.config.content_addons.retain(|n| n.trim_end_matches(".vpk") != stem);
        s.file.config.extra_maps.retain(|n| n.trim_end_matches(".vpk") != stem);
        store::save_server(&layout, &s.file)?;
        drop(inner);
        self.emit_changed();
        Ok(())
    }

    // ── Updates ──

    /// Look for a newer game build / Deadworks release now.
    pub fn check_updates(&self, network: bool) -> UpdateState {
        let (source, layout, username) = {
            let inner = self.lock();
            (inner.state.base.as_ref().map(|b| b.source), inner.layout.clone(), inner.state.steam_username.clone())
        };
        let Some(layout) = layout else { return UpdateState::default() };
        let available = match source {
            Some(BaseSource::Client) => crate::connect::resolve_game_dir()
                .ok()
                .and_then(|g| manifest::appmanifest_path_for_game_dir(&g))
                .and_then(|p| manifest::read_appmanifest(&p).ok())
                .filter(|a| !a.updating())
                .map(|a| a.build_id),
            Some(BaseSource::Steamcmd) if network => username
                .and_then(|u| steamcmd::latest_build(&layout, &u, &Progress::new(TaskKind::Update)).ok()),
            _ => None,
        };
        let latest = if network { release::latest_tag().ok() } else { None };
        let mut inner = self.lock();
        if available.is_some() {
            inner.available_build = available;
        }
        if let Some(latest) = latest {
            inner.state.deadworks_latest = Some(latest);
            inner.state.last_check = Some(now_secs());
            inner.save_state();
        }
        Self::update_state(&inner)
    }

    fn maybe_auto_update(self: &Arc<Self>) {
        let ready = {
            let inner = self.lock();
            let idle = inner.servers.values().all(|s| match s.state() {
                ServerState::Running => s.rt.players.is_empty(),
                ServerState::Starting | ServerState::Stopping | ServerState::Updating => false,
                _ => true,
            });
            inner.installed()
                && inner.task.is_none()
                && Self::pending(&inner)
                && idle
                && inner.last_auto_update.is_none_or(|t| t.elapsed() > AUTO_UPDATE_RETRY)
        };
        if ready {
            self.lock().last_auto_update = Some(Instant::now());
            let _ = self.apply_updates();
        }
    }
}

// ── Background threads ──

fn ticker(mgr: Arc<Manager>) {
    let mut last_metrics = Instant::now();
    loop {
        std::thread::sleep(Duration::from_millis(100));
        let mut polls: Vec<(u32, bool)> = Vec::new();
        {
            let mut inner = mgr.lock();
            let mut good_build = None;
            let sample = last_metrics.elapsed() >= METRICS_EVERY;
            for s in inner.servers.values_mut() {
                let lines = s.console.take_pending();
                if !lines.is_empty() {
                    let _ = mgr.app.emit("hosting://console", ConsoleBatch { id: s.file.config.id.clone(), lines });
                }
                let Some(proc) = s.proc.clone() else { continue };
                let uptime = s.rt.started_at.map(|t| now_secs().saturating_sub(t)).unwrap_or(0);
                if s.rt.state == Some(ServerState::Starting) && uptime > READY_FALLBACK {
                    s.rt.state = Some(ServerState::Running);
                }
                if s.rt.state == Some(ServerState::Running) {
                    if uptime >= GOOD_AFTER && !s.rt.recorded_good {
                        s.rt.recorded_good = true;
                        good_build = Some(());
                    }
                    if s.rt.last_poll.is_none_or(|t| t.elapsed() >= STATUS_POLL) {
                        s.rt.last_poll = Some(Instant::now());
                        let host = s.rt.supports_host_status != Some(false);
                        s.monitor.poll_sent(host);
                        polls.push((proc.pid, host));
                    }
                }
                if sample {
                    let (cpu, mem) = proc.sample();
                    s.rt.cpu = cpu;
                    s.rt.memory = mem;
                    let _ = mgr.app.emit("hosting://runtime", s.runtime());
                }
            }
            if sample {
                last_metrics = Instant::now();
            }
            if good_build.is_some() {
                inner.state.last_good_build = inner.state.base.as_ref().map(|b| b.build_id.clone());
                inner.save_state();
            }
        }
        for (pid, host) in polls {
            let _ = process::send_line(pid, "status");
            if host {
                let _ = process::send_line(pid, "dw_host_status");
            }
        }
    }
}

fn updater(mgr: Arc<Manager>) {
    let mut last_client = Instant::now() - CLIENT_CHECK_EVERY;
    let mut last_release = Instant::now() - RELEASE_CHECK_EVERY + Duration::from_secs(60);
    loop {
        std::thread::sleep(Duration::from_secs(30));
        if !mgr.lock().installed() {
            continue;
        }
        let network = last_release.elapsed() >= RELEASE_CHECK_EVERY;
        if network || last_client.elapsed() >= CLIENT_CHECK_EVERY {
            let before = mgr.overview().updates;
            let after = mgr.check_updates(network);
            last_client = Instant::now();
            if network {
                last_release = Instant::now();
            }
            if before.pending != after.pending || before.deadworks_latest != after.deadworks_latest {
                mgr.emit_changed();
            }
        }
        mgr.maybe_auto_update();
    }
}

// ── Helpers ──

/// Ask api.deadworks.net to send one A2S_INFO to our own public IP at `port`.
/// A Worker can't send UDP, so the check is queued for the probe service and
/// polled here (see deadworks-api `/api/hosting/reachability`).
fn reachability_check(api: &str, port: u16) -> (Reachability, Option<String>) {
    let client = super::download::client();
    let created = client
        .post(format!("{api}/api/hosting/reachability"))
        .json(&serde_json::json!({ "port": port }))
        .timeout(Duration::from_secs(15))
        .send()
        .and_then(|r| r.error_for_status())
        .and_then(|r| r.json::<serde_json::Value>());
    let Ok(created) = created else { return (Reachability::Error, None) };
    let ip = created["ip"].as_str().map(String::from);
    let Some(check) = created["id"].as_str().map(String::from) else { return (Reachability::Error, ip) };
    let deadline = Instant::now() + Duration::from_secs(20);
    while Instant::now() < deadline {
        std::thread::sleep(Duration::from_millis(1000));
        let Ok(v) = client
            .get(format!("{api}/api/hosting/reachability/{check}"))
            .timeout(Duration::from_secs(10))
            .send()
            .and_then(|r| r.error_for_status())
            .and_then(|r| r.json::<serde_json::Value>())
        else {
            continue;
        };
        match v["status"].as_str() {
            Some("done") => {
                if v["reachable"].as_bool() == Some(true) {
                    return (Reachability::Open, ip);
                }
                // e.g. "ipv6_unsupported": the probe couldn't test, which isn't "closed".
                let untestable = v["reason"].as_str().is_some_and(|r| r.contains("unsupported") || r.contains("invalid"));
                return (if untestable { Reachability::Error } else { Reachability::Closed }, ip);
            }
            Some("expired") => return (Reachability::Error, ip),
            _ => {}
        }
    }
    (Reachability::Error, ip)
}

fn client_manifest(game_dir: &Path) -> Result<(manifest::AppManifest, Vec<DepotFile>), String> {
    let acf = manifest::appmanifest_path_for_game_dir(game_dir).ok_or("Couldn't find Steam's record of your Deadlock install.")?;
    let app = manifest::read_appmanifest(&acf)?;
    if app.updating() {
        return Err("Steam is still updating Deadlock. Try again once the update has finished.".into());
    }
    let steam_root = crate::connect::steam_root().ok();
    let files = manifest::load_depot_files(&app, &manifest::depotcache_dirs(steam_root.as_deref(), game_dir))?;
    Ok((app, files))
}

/// Where to install. A folder that already holds other things gets a
/// dedicated subfolder, so uninstalling can never touch the user's files.
fn choose_root(requested: &str) -> Result<PathBuf, String> {
    let p = PathBuf::from(requested.trim());
    if !p.is_absolute() {
        return Err("Choose a full folder path, like D:\\Deadworks Servers.".into());
    }
    if let Ok(game) = crate::connect::resolve_game_dir() {
        if let Some(install) = game.parent() {
            if p.starts_with(install) {
                return Err("Choose a folder outside your Deadlock install.".into());
            }
        }
    }
    let lower = p.to_string_lossy().to_ascii_lowercase();
    if lower.contains("\\windows\\") || lower.ends_with("\\windows") {
        return Err("Choose a folder outside the Windows directory.".into());
    }
    let ours = p.join("hosting.json").is_file();
    let empty = std::fs::read_dir(&p).map(|mut rd| rd.next().is_none()).unwrap_or(true);
    Ok(if ours || empty { p } else { p.join(store::FOLDER_NAME) })
}

fn new_id(name: &str) -> String {
    let slug: String = name
        .to_ascii_lowercase()
        .chars()
        .map(|c| if c.is_ascii_alphanumeric() { c } else { '-' })
        .collect::<String>()
        .split('-')
        .filter(|s| !s.is_empty())
        .collect::<Vec<_>>()
        .join("-");
    let slug: String = slug.chars().take(24).collect();
    let suffix = &uuid::Uuid::new_v4().simple().to_string()[..6];
    if slug.is_empty() {
        format!("server-{suffix}")
    } else {
        format!("{slug}-{suffix}")
    }
}

fn free_port(inner: &Inner, except: Option<&str>) -> u16 {
    let used: Vec<u16> = inner
        .servers
        .values()
        .filter(|s| Some(s.file.config.id.as_str()) != except)
        .map(|s| s.file.config.port)
        .collect();
    (FIRST_PORT..FIRST_PORT + 200)
        .step_by(2)
        .find(|p| !used.contains(p) && netcfg::port_free(*p))
        .unwrap_or(FIRST_PORT)
}

fn plugin_config_path(configs: &Path, name: &str) -> PathBuf {
    configs.join(name).join(format!("{name}.jsonc"))
}

fn copy_dir(src: &Path, dst: &Path, skip: &[&str]) {
    let Ok(rd) = std::fs::read_dir(src) else { return };
    let _ = std::fs::create_dir_all(dst);
    for e in rd.flatten() {
        let name = e.file_name();
        if skip.iter().any(|s| name.eq_ignore_ascii_case(s)) {
            continue;
        }
        let p = e.path();
        if p.is_dir() {
            copy_dir(&p, &dst.join(&name), skip);
        } else {
            let _ = std::fs::copy(&p, dst.join(&name));
        }
    }
}

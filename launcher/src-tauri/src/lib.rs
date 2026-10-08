mod addons;
mod bootstrap;
mod connect;
mod deep_link;
mod gameinfo;
mod hosting;
mod local_api;
mod ping;
mod telemetry;

/// Try to patch gameinfo.gi at startup. If the game is running the file is
/// locked; `reassert_quiet` stashes the error and the frontend puts it in a
/// modal once it mounts, since an unpatched file mounts nothing and says so
/// nowhere else.
fn patch_gameinfo_on_startup() {
    // Deadlock not installed (or not detected yet): nothing to patch.
    let Ok(game_dir) = connect::resolve_game_dir() else {
        return;
    };
    gameinfo::reassert_quiet(&game_dir);
}

/// Everything that must be true on disk before the game process starts.
///
/// Both steps are needed on every launch path: DMM rewrites the whole
/// SearchPaths block on its own launches, and the bootstrap addon may have a
/// staged update that only a game restart lets us apply.
///
/// This awaits the bootstrap work rather than kicking it off alongside the
/// launch — `steam://` is fire-and-forget, so anything still running when we
/// hand off is racing the game's own mount.
pub(crate) async fn prepare_for_launch(app: &tauri::AppHandle) {
    let Ok(game_dir) = connect::resolve_game_dir() else {
        return;
    };
    gameinfo::reassert_quiet(&game_dir);
    if let Err(e) = bootstrap::ensure(app, &game_dir, false).await {
        println!("[launch] bootstrap check failed: {}", e);
    }
}

/// Quit from the tray. Running servers are stopped cleanly first, after the
/// user confirms; the kill-on-close job would otherwise end them abruptly.
fn quit_from_tray(app: &tauri::AppHandle) {
    use tauri_plugin_dialog::{DialogExt, MessageDialogButtons, MessageDialogKind};
    let running = hosting::running_count();
    if running == 0 {
        app.exit(0);
        return;
    }
    let app = app.clone();
    let plural = if running == 1 { "server is" } else { "servers are" };
    app.dialog()
        .message(format!("{running} {plural} running. Quitting stops {}.", if running == 1 { "it" } else { "them" }))
        .title("Stop servers and quit?")
        .kind(MessageDialogKind::Warning)
        .buttons(MessageDialogButtons::OkCancelCustom("Stop and quit".into(), "Cancel".into()))
        .show(move |confirmed| {
            if confirmed {
                std::thread::spawn(move || {
                    hosting::stop_all_blocking();
                    app.exit(0);
                });
            }
        });
}

#[cfg_attr(mobile, tauri::mobile_entry_point)]
pub fn run() {
    tauri::Builder::default()
        .plugin(tauri_plugin_single_instance::init(|app, args, _cwd| {
            for arg in &args {
                if arg.starts_with("deadworks://") {
                    deep_link::dispatch(app, deep_link::parse_url(arg));
                }
            }
            deep_link::surface_main_window(app);
        }))
        .plugin(tauri_plugin_deep_link::init())
        .plugin(tauri_plugin_updater::Builder::new().build())
        .plugin(tauri_plugin_dialog::init())
        .plugin(tauri_plugin_process::init())
        .plugin(tauri_plugin_opener::init())
        .plugin(tauri_plugin_store::Builder::default().build())
        .plugin(tauri_plugin_autostart::init(
            tauri_plugin_autostart::MacosLauncher::LaunchAgent,
            None,
        ))
        .manage(deep_link::DeepLinkStateContainer::new())
        .invoke_handler(tauri::generate_handler![
            connect::launch_deadlock,
            connect::get_detected_game_dir,
            connect::get_game_dir,
            connect::set_game_dir,
            connect::reset_game_dir,
            addons::prepare_and_connect,
            bootstrap::bootstrap_status,
            bootstrap::retry_bootstrap_install,
            gameinfo::gameinfo_error,
            gameinfo::retry_gameinfo_patch,
            ping::ping_server,
            deep_link::deep_link_ready,
            hosting::hosting_overview,
            hosting::hosting_setup_check,
            hosting::hosting_install,
            hosting::hosting_cancel_task,
            hosting::hosting_steamcmd_input,
            hosting::hosting_check_updates,
            hosting::hosting_apply_updates,
            hosting::hosting_clear_hold,
            hosting::hosting_verify,
            hosting::hosting_repair_client,
            hosting::hosting_uninstall,
            hosting::hosting_maps,
            hosting::hosting_create_server,
            hosting::hosting_update_server,
            hosting::hosting_duplicate_server,
            hosting::hosting_delete_server,
            hosting::hosting_start,
            hosting::hosting_stop,
            hosting::hosting_restart,
            hosting::hosting_runtime,
            hosting::hosting_console_history,
            hosting::hosting_send_command,
            hosting::hosting_kick,
            hosting::hosting_ban,
            hosting::hosting_permissions,
            hosting::hosting_penalties,
            hosting::hosting_lift_penalty,
            hosting::hosting_write_permissions,
            hosting::hosting_mark_shared,
            hosting::hosting_check_reachability,
            hosting::hosting_open_folder,
            hosting::hosting_plugin_library,
            hosting::hosting_import_plugins,
            hosting::hosting_remove_plugin,
            hosting::hosting_set_plugin_enabled,
            hosting::hosting_plugin_configs,
            hosting::hosting_read_plugin_config,
            hosting::hosting_write_plugin_config,
            hosting::hosting_reset_plugin_config,
            hosting::hosting_content,
            hosting::hosting_import_content,
            hosting::hosting_remove_content,
        ])
        .setup(|app| {
            use tauri::menu::{Menu, MenuItem};
            use tauri::tray::{MouseButton, MouseButtonState, TrayIconBuilder, TrayIconEvent};
            use tauri::Manager;
            use tauri_plugin_deep_link::DeepLinkExt;

            // Register the deadworks:// scheme at runtime for dev / portable runs.
            // Bundled installers (MSI/NSIS) write the registry entry at install time
            // via tauri.conf.json, so this is a no-op for packaged builds.
            #[cfg(any(target_os = "linux", target_os = "windows"))]
            let _ = app.deep_link().register("deadworks");

            let handle = app.handle().clone();
            app.deep_link().on_open_url(move |event| {
                for url in event.urls() {
                    deep_link::dispatch(&handle, deep_link::parse_url(url.as_str()));
                }
                deep_link::surface_main_window(&handle);
            });

            // Restore game directory override from persisted settings BEFORE
            // attempting the gameinfo.gi patch — otherwise users with a legacy
            // Project8Staging install (or any custom location) would have the
            // patch silently skipped and hit "addonroot missing" on connect.
            if let Ok(store) = tauri_plugin_store::StoreBuilder::new(app.handle(), "settings.json").build() {
                if let Some(path) = store.get("game_dir_override").and_then(|v| v.as_str().map(String::from)) {
                    connect::set_game_dir_override(Some(std::path::PathBuf::from(path)));
                }
            }
            patch_gameinfo_on_startup();
            // The load-bearing update path: at launcher start the game is
            // usually not running, so a swap staged last session lands here.
            bootstrap::spawn_poller(app.handle().clone());

            // Loopback bridge for the in-game server browser. Panorama can only
            // load images, so this answers with PNGs whose dimensions encode the
            // reply. Runs on its own thread; a bind failure is non-fatal.
            local_api::start(app.handle().clone());

            // Local server hosting: loads saved servers and starts the
            // console/metrics ticker and the update checker.
            hosting::init(app.handle());

            let app_handle = app.handle().clone();
            tauri::async_runtime::spawn(async move {
                telemetry::maybe_send_install(&app_handle);
                telemetry::maybe_send_heartbeat(&app_handle);
            });

            let show = MenuItem::with_id(app, "show", "Show", true, None::<&str>)?;
            let launch = MenuItem::with_id(app, "launch", "Launch Deadlock", true, None::<&str>)?;
            let quit = MenuItem::with_id(app, "quit", "Quit", true, None::<&str>)?;
            let menu = Menu::with_items(app, &[&show, &launch, &quit])?;

            let _tray = TrayIconBuilder::new()
                .icon(app.default_window_icon().unwrap().clone())
                .menu(&menu)
                .tooltip("Deadworks")
                .on_tray_icon_event(|tray, event| {
                    if let TrayIconEvent::Click {
                        button: MouseButton::Left,
                        button_state: MouseButtonState::Up,
                        ..
                    } = event
                    {
                        let app = tray.app_handle();
                        if let Some(window) = app.get_webview_window("main") {
                            let _ = window.show();
                            let _ = window.unminimize();
                            let _ = window.set_focus();
                        }
                    }
                })
                .on_menu_event(|app, event| match event.id.as_ref() {
                    "show" => {
                        if let Some(window) = app.get_webview_window("main") {
                            let _ = window.show();
                            let _ = window.unminimize();
                            let _ = window.set_focus();
                        }
                    }
                    "launch" => {
                        let app = app.clone();
                        tauri::async_runtime::spawn(async move {
                            prepare_for_launch(&app).await;
                            let _ = open::that(format!("steam://run/{}", "1422450"));
                        });
                    }
                    "quit" => quit_from_tray(app),
                    _ => {}
                })
                .build(app)?;

            Ok(())
        })
        .on_window_event(|window, event| {
            if let tauri::WindowEvent::CloseRequested { api, .. } = event {
                // Only hide the main window to tray; let other windows close normally
                if window.label() == "main" {
                    api.prevent_close();
                    let _ = window.hide();
                }
            }
        })
        .run(tauri::generate_context!())
        .expect("error while running tauri application");
}

// Prevents additional console window on Windows in release, DO NOT REMOVE!!
#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

fn main() {
    // An MCP server on stdio for coding agents; it talks to the running launcher.
    if std::env::args().nth(1).as_deref() == Some("mcp") {
        return deadworks_launcher_lib::run_mcp();
    }
    deadworks_launcher_lib::run()
}

//! Progress shared between a long-running task (install, update, verify) and
//! the thread that reports it to the UI. Workers bump the atomics; the reporter
//! snapshots them a few times a second.

use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::sync::mpsc::{Receiver, Sender};
use std::sync::Mutex;

use super::types::{SteamPrompt, TaskKind, TaskProgress};

pub struct Progress {
    pub kind: TaskKind,
    pub cancel: AtomicBool,
    pub bytes_done: AtomicU64,
    pub bytes_total: AtomicU64,
    pub files_done: AtomicU64,
    pub files_total: AtomicU64,
    stage: Mutex<(String, String)>,
    prompt: Mutex<Option<SteamPrompt>>,
    input_tx: Sender<String>,
    input_rx: Mutex<Receiver<String>>,
}

impl Progress {
    pub fn new(kind: TaskKind) -> Self {
        let (input_tx, input_rx) = std::sync::mpsc::channel();
        Self {
            kind,
            cancel: AtomicBool::new(false),
            bytes_done: AtomicU64::new(0),
            bytes_total: AtomicU64::new(0),
            files_done: AtomicU64::new(0),
            files_total: AtomicU64::new(0),
            stage: Mutex::new(("starting".into(), "Starting".into())),
            prompt: Mutex::new(None),
            input_tx,
            input_rx: Mutex::new(input_rx),
        }
    }

    /// Enter a new stage; counters restart from zero.
    pub fn stage(&self, stage: &str, label: &str, bytes_total: u64, files_total: u64) {
        *self.stage.lock().unwrap() = (stage.into(), label.into());
        self.bytes_done.store(0, Ordering::Relaxed);
        self.files_done.store(0, Ordering::Relaxed);
        self.bytes_total.store(bytes_total, Ordering::Relaxed);
        self.files_total.store(files_total, Ordering::Relaxed);
    }

    pub fn label(&self, label: &str) {
        self.stage.lock().unwrap().1 = label.into();
    }

    pub fn cancelled(&self) -> bool {
        self.cancel.load(Ordering::Relaxed)
    }

    pub fn check_cancel(&self) -> Result<(), String> {
        if self.cancelled() {
            Err(CANCELLED.into())
        } else {
            Ok(())
        }
    }

    pub fn set_prompt(&self, prompt: Option<SteamPrompt>) {
        *self.prompt.lock().unwrap() = prompt;
    }

    /// Hand an answer (Steam Guard code, password) to a task waiting on a prompt.
    pub fn submit_input(&self, value: String) {
        let _ = self.input_tx.send(value);
    }

    /// Wait for the user's answer, giving up when the task is cancelled.
    pub fn wait_input(&self) -> Result<String, String> {
        let rx = self.input_rx.lock().unwrap();
        loop {
            self.check_cancel()?;
            match rx.recv_timeout(std::time::Duration::from_millis(250)) {
                Ok(v) => return Ok(v),
                Err(std::sync::mpsc::RecvTimeoutError::Timeout) => continue,
                Err(_) => return Err(CANCELLED.into()),
            }
        }
    }

    pub fn snapshot(&self) -> TaskProgress {
        let (stage, label) = self.stage.lock().unwrap().clone();
        TaskProgress {
            kind: self.kind,
            stage,
            label,
            bytes_done: self.bytes_done.load(Ordering::Relaxed),
            bytes_total: self.bytes_total.load(Ordering::Relaxed),
            files_done: self.files_done.load(Ordering::Relaxed),
            files_total: self.files_total.load(Ordering::Relaxed),
            steam_prompt: *self.prompt.lock().unwrap(),
            error: None,
            modified_files: Vec::new(),
            finished: false,
        }
    }
}

pub const CANCELLED: &str = "Cancelled.";

/// A task failure; `modified` carries the client files that blocked an install.
#[derive(Debug)]
pub struct TaskError {
    pub message: String,
    pub modified: Vec<String>,
}

impl From<String> for TaskError {
    fn from(message: String) -> Self {
        Self { message, modified: Vec::new() }
    }
}

impl From<&str> for TaskError {
    fn from(message: &str) -> Self {
        Self { message: message.into(), modified: Vec::new() }
    }
}

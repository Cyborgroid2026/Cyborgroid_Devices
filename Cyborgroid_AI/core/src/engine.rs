//! Supervises the local llama-server process and streams chat completions from it.

use crate::http::{self, Outcome};
use crate::json;
use std::fs::OpenOptions;
use std::io::{Read, Seek, SeekFrom};
use std::os::unix::process::CommandExt;
use std::path::Path;
use std::process::{Child, Command, Stdio};
use std::sync::atomic::AtomicBool;
use std::sync::Mutex;
use std::time::{Duration, Instant};

static CHILD: Mutex<Option<Child>> = Mutex::new(None);

pub struct EngineConfig<'a> {
    pub server: &'a str,
    pub model: &'a str,
    pub host: &'a str,
    pub port: u16,
    pub context: u32,
    pub threads: u32,
    pub gpu_layers: i32,
    pub log_path: &'a str,
    pub extra_args: Vec<&'a str>,
    pub startup_timeout: Duration,
}

pub fn health(host: &str, port: u16) -> Result<u16, String> {
    match http::request(host, port, "GET", "/health", None, Duration::from_secs(3), None, &mut |_, _| true)? {
        Outcome::Complete(code) => Ok(code),
        Outcome::Cancelled => Err("cancelled".into()),
    }
}

/// Starts the engine, or reuses one already serving on host:port.
/// Returns true if a new process was spawned.
pub fn start(cfg: &EngineConfig) -> Result<bool, String> {
    http::loopback(cfg.host)?;
    if health(cfg.host, cfg.port) == Ok(200) {
        return Ok(false);
    }
    for (what, p) in [("engine", cfg.server), ("model", cfg.model)] {
        if !Path::new(p).is_file() {
            return Err(format!("{} not found: {} (run `sudo cyborgroid update`)", what, p));
        }
    }

    if let Some(dir) = Path::new(cfg.log_path).parent() {
        std::fs::create_dir_all(dir).ok();
    }
    let log = OpenOptions::new()
        .create(true)
        .append(true)
        .open(cfg.log_path)
        .map_err(|e| format!("{}: {}", cfg.log_path, e))?;
    let log_start = log.metadata().map(|m| m.len()).unwrap_or(0);
    let log_err = log.try_clone().map_err(|e| e.to_string())?;

    let mut cmd = Command::new(cfg.server);
    cmd.arg("-m").arg(cfg.model)
        .arg("--host").arg(cfg.host)
        .arg("--port").arg(cfg.port.to_string())
        .arg("-c").arg(cfg.context.to_string())
        .arg("-ngl").arg(cfg.gpu_layers.to_string())
        .arg("--jinja");
    if cfg.threads > 0 {
        cmd.arg("-t").arg(cfg.threads.to_string());
    }
    cmd.args(&cfg.extra_args)
        .env("LLAMA_OFFLINE", "1")
        .env("HF_HUB_OFFLINE", "1")
        .stdin(Stdio::null())
        .stdout(log)
        .stderr(log_err);
    // Make the kernel stop the engine if the CLI dies without cleaning up.
    unsafe {
        cmd.pre_exec(|| {
            extern "C" {
                fn prctl(option: i32, ...) -> i32;
            }
            const PR_SET_PDEATHSIG: i32 = 1;
            const SIGTERM: i32 = 15;
            prctl(PR_SET_PDEATHSIG, SIGTERM);
            Ok(())
        });
    }

    let child = cmd.spawn().map_err(|e| format!("failed to launch {}: {}", cfg.server, e))?;
    *CHILD.lock().unwrap() = Some(child);

    let deadline = Instant::now() + cfg.startup_timeout;
    loop {
        if let Some(child) = CHILD.lock().unwrap().as_mut() {
            if let Ok(Some(status)) = child.try_wait() {
                return Err(format!("engine exited during startup ({})\n{}", status, log_tail(cfg.log_path, log_start)));
            }
        }
        if health(cfg.host, cfg.port) == Ok(200) {
            return Ok(true);
        }
        if Instant::now() > deadline {
            stop();
            return Err(format!("engine did not become ready in {:?}\n{}", cfg.startup_timeout, log_tail(cfg.log_path, log_start)));
        }
        std::thread::sleep(Duration::from_millis(250));
    }
}

fn log_tail(path: &str, from: u64) -> String {
    let mut s = String::new();
    if let Ok(mut f) = std::fs::File::open(path) {
        let len = f.metadata().map(|m| m.len()).unwrap_or(0);
        let start = from.max(len.saturating_sub(4096));
        f.seek(SeekFrom::Start(start)).ok();
        f.read_to_string(&mut s).ok();
    }
    s
}

/// Stops the engine if this process started it. Returns true if one was stopped.
pub fn stop() -> bool {
    let Some(mut child) = CHILD.lock().unwrap().take() else { return false };
    unsafe {
        extern "C" {
            fn kill(pid: i32, sig: i32) -> i32;
        }
        kill(child.id() as i32, 15);
    }
    let deadline = Instant::now() + Duration::from_secs(5);
    while Instant::now() < deadline {
        if let Ok(Some(_)) = child.try_wait() {
            return true;
        }
        std::thread::sleep(Duration::from_millis(50));
    }
    child.kill().ok();
    child.wait().ok();
    true
}

pub enum Piece<'a> {
    Content(&'a str),
    Reasoning(&'a str),
}

/// Posts an OpenAI-style chat request with `"stream": true` and forwards each delta.
pub fn chat(
    host: &str,
    port: u16,
    body: &str,
    cancel: &AtomicBool,
    on_piece: &mut dyn FnMut(Piece),
) -> Result<Outcome, String> {
    let mut line: Vec<u8> = Vec::new();
    let mut error_body: Vec<u8> = Vec::new();
    let mut failure: Option<String> = None;

    let outcome = http::request(
        host,
        port,
        "POST",
        "/v1/chat/completions",
        Some(body.as_bytes()),
        Duration::from_secs(600),
        Some(cancel),
        &mut |status, bytes| {
            if status != 200 {
                error_body.extend_from_slice(bytes);
                return true;
            }
            for &b in bytes {
                if b != b'\n' {
                    line.push(b);
                    continue;
                }
                let text = String::from_utf8_lossy(&line).trim_end_matches('\r').to_string();
                line.clear();
                let Some(payload) = text.strip_prefix("data:") else { continue };
                let payload = payload.trim();
                if payload == "[DONE]" {
                    return false;
                }
                let ev = match json::parse(payload) {
                    Ok(v) => v,
                    Err(e) => {
                        failure = Some(format!("bad stream event: {}", e));
                        return false;
                    }
                };
                if let Some(err) = ev.get("error") {
                    let msg = err.get("message").and_then(|m| m.as_str()).unwrap_or("engine error");
                    failure = Some(msg.to_string());
                    return false;
                }
                let Some(delta) = ev.get("choices").and_then(|c| c.at(0)).and_then(|c| c.get("delta")) else { continue };
                if let Some(r) = delta.get("reasoning_content").and_then(|v| v.as_str()) {
                    if !r.is_empty() {
                        on_piece(Piece::Reasoning(r));
                    }
                }
                if let Some(c) = delta.get("content").and_then(|v| v.as_str()) {
                    if !c.is_empty() {
                        on_piece(Piece::Content(c));
                    }
                }
            }
            true
        },
    )?;

    if let Some(f) = failure {
        return Err(f);
    }
    if let Outcome::Complete(code) = outcome {
        if code != 200 {
            let text = String::from_utf8_lossy(&error_body).to_string();
            let msg = json::parse(&text)
                .ok()
                .and_then(|v| v.get("error").and_then(|e| e.get("message")).and_then(|m| m.as_str()).map(String::from))
                .unwrap_or(text);
            return Err(format!("engine returned HTTP {}: {}", code, msg));
        }
    }
    Ok(outcome)
}

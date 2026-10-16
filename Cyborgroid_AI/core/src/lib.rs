//! Cyborgroid core — the Rust half of Cyborgroid, loaded by the C# CLI over a C ABI.
//!
//! Strings in and out are NUL-terminated UTF-8. Strings returned by `cy_*` functions
//! must be released with `cy_string_free`.

pub mod engine;
pub mod http;
pub mod json;
pub mod net;
pub mod sha256;

use std::cell::RefCell;
use std::ffi::{c_char, c_void, CStr, CString};
use std::sync::atomic::{AtomicBool, Ordering};
use std::time::Duration;

static CANCEL: AtomicBool = AtomicBool::new(false);

thread_local! {
    static LAST_ERROR: RefCell<String> = RefCell::new(String::new());
}

fn set_error(msg: impl Into<String>) {
    LAST_ERROR.with(|e| *e.borrow_mut() = msg.into());
}

fn out_string(s: &str) -> *mut c_char {
    CString::new(s.replace('\0', "")).unwrap().into_raw()
}

unsafe fn arg<'a>(p: *const c_char, name: &str) -> Result<&'a str, String> {
    if p.is_null() {
        return Err(format!("{} is null", name));
    }
    CStr::from_ptr(p).to_str().map_err(|_| format!("{} is not valid UTF-8", name))
}

#[no_mangle]
pub extern "C" fn cy_version() -> *mut c_char {
    out_string(env!("CARGO_PKG_VERSION"))
}

#[no_mangle]
pub extern "C" fn cy_last_error() -> *mut c_char {
    LAST_ERROR.with(|e| out_string(&e.borrow()))
}

#[no_mangle]
pub unsafe extern "C" fn cy_string_free(p: *mut c_char) {
    if !p.is_null() {
        drop(CString::from_raw(p));
    }
}

/// Returns 1 if a new engine was started, 0 if a running one was reused, -1 on error.
#[no_mangle]
pub unsafe extern "C" fn cy_engine_start(
    server: *const c_char,
    model: *const c_char,
    host: *const c_char,
    port: u16,
    context: u32,
    threads: u32,
    gpu_layers: i32,
    log_path: *const c_char,
    extra_args: *const c_char,
    startup_timeout_secs: u32,
) -> i32 {
    let run = || -> Result<bool, String> {
        let extra = if extra_args.is_null() { "" } else { arg(extra_args, "extra_args")? };
        let cfg = engine::EngineConfig {
            server: arg(server, "server")?,
            model: arg(model, "model")?,
            host: arg(host, "host")?,
            port,
            context,
            threads,
            gpu_layers,
            log_path: arg(log_path, "log_path")?,
            extra_args: extra.lines().map(str::trim).filter(|s| !s.is_empty()).collect(),
            startup_timeout: Duration::from_secs(startup_timeout_secs.max(5) as u64),
        };
        engine::start(&cfg)
    };
    match run() {
        Ok(started) => started as i32,
        Err(e) => {
            set_error(e);
            -1
        }
    }
}

#[no_mangle]
pub extern "C" fn cy_engine_stop() -> i32 {
    engine::stop() as i32
}

/// HTTP status of the engine's /health endpoint, or -1 if unreachable.
#[no_mangle]
pub unsafe extern "C" fn cy_engine_health(host: *const c_char, port: u16) -> i32 {
    match arg(host, "host").and_then(|h| engine::health(h, port)) {
        Ok(code) => code as i32,
        Err(e) => {
            set_error(e);
            -1
        }
    }
}

pub type PieceCallback = extern "C" fn(kind: i32, text: *const c_char, user: *mut c_void);

/// Streams a chat completion. `callback` gets kind 0 for answer text, 1 for reasoning, and
/// 2 for a finished tool call encoded as `id \x1f name \x1f arguments-json`.
/// Returns 0 when complete, 1 when cancelled via `cy_cancel`, -1 on error.
#[no_mangle]
pub unsafe extern "C" fn cy_chat_stream(
    host: *const c_char,
    port: u16,
    body_json: *const c_char,
    callback: PieceCallback,
    user: *mut c_void,
) -> i32 {
    CANCEL.store(false, Ordering::SeqCst);
    let run = || -> Result<http::Outcome, String> {
        let host = arg(host, "host")?;
        let body = arg(body_json, "body_json")?;
        engine::chat(host, port, body, &CANCEL, &mut |piece| {
            let (kind, text) = match piece {
                engine::Piece::Content(t) => (0, t.to_string()),
                engine::Piece::Reasoning(t) => (1, t.to_string()),
                engine::Piece::ToolCall { id, name, arguments } => (2, format!("{}\x1f{}\x1f{}", id, name, arguments)),
            };
            let c = CString::new(text.replace('\0', "")).unwrap();
            callback(kind, c.as_ptr(), user);
        })
    };
    match run() {
        Ok(http::Outcome::Complete(_)) => 0,
        Ok(http::Outcome::Cancelled) => 1,
        Err(e) => {
            set_error(e);
            -1
        }
    }
}

/// Asks an in-flight `cy_chat_stream` to stop. Safe to call from any thread.
#[no_mangle]
pub extern "C" fn cy_cancel() {
    CANCEL.store(true, Ordering::SeqCst);
}

/// Lowercase hex SHA-256 of a file, or null on error.
#[no_mangle]
pub unsafe extern "C" fn cy_sha256_file(path: *const c_char) -> *mut c_char {
    match arg(path, "path").and_then(sha256::file_hex) {
        Ok(h) => out_string(&h),
        Err(e) => {
            set_error(e);
            std::ptr::null_mut()
        }
    }
}

/// 1 if any physical network link is up, else 0.
#[no_mangle]
pub extern "C" fn cy_network_online() -> i32 {
    net::online() as i32
}

#[no_mangle]
pub extern "C" fn cy_network_describe() -> *mut c_char {
    out_string(&net::describe())
}

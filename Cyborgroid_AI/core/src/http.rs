//! Loopback-only HTTP/1.1 client. Cyborgroid's runtime talks to nothing but
//! the local engine, so any non-loopback address is refused outright.

use std::io::{ErrorKind, Read, Write};
use std::net::{IpAddr, Ipv4Addr, SocketAddr, TcpStream};
use std::sync::atomic::{AtomicBool, Ordering};
use std::time::{Duration, Instant};

pub fn loopback(host: &str) -> Result<IpAddr, String> {
    let ip: IpAddr = if host == "localhost" {
        IpAddr::V4(Ipv4Addr::LOCALHOST)
    } else {
        host.parse()
            .map_err(|_| format!("host '{}' must be a loopback IP literal", host))?
    };
    if !ip.is_loopback() {
        return Err(format!("refusing non-loopback address {} (runtime is offline-only)", ip));
    }
    Ok(ip)
}

#[derive(Clone, Copy)]
enum ChunkState {
    Size,
    Data(usize),
    DataEnd,
    Trailer,
    Done,
}

/// Incremental decoder for `Transfer-Encoding: chunked`.
struct Chunked {
    state: ChunkState,
    line: Vec<u8>,
}

impl Chunked {
    fn new() -> Self {
        Chunked { state: ChunkState::Size, line: Vec::new() }
    }

    /// Feeds raw bytes; decoded payload goes to `out`. Returns true once the final chunk is seen.
    fn feed(&mut self, mut d: &[u8], out: &mut dyn FnMut(&[u8])) -> Result<bool, String> {
        while !d.is_empty() {
            match self.state {
                ChunkState::Done => return Ok(true),
                ChunkState::Data(n) => {
                    let take = n.min(d.len());
                    out(&d[..take]);
                    d = &d[take..];
                    self.state = if take == n { ChunkState::DataEnd } else { ChunkState::Data(n - take) };
                }
                line_state => {
                    let Some(p) = d.iter().position(|&b| b == b'\n') else {
                        self.line.extend_from_slice(d);
                        if self.line.len() > 64 * 1024 {
                            return Err("chunk header too long".into());
                        }
                        break;
                    };
                    self.line.extend_from_slice(&d[..p]);
                    d = &d[p + 1..];
                    let line = String::from_utf8_lossy(&self.line).trim().to_string();
                    self.line.clear();
                    self.state = match line_state {
                        ChunkState::Size => {
                            let hex = line.split(';').next().unwrap_or("").trim();
                            let n = usize::from_str_radix(hex, 16)
                                .map_err(|_| format!("bad chunk size '{}'", hex))?;
                            if n == 0 { ChunkState::Trailer } else { ChunkState::Data(n) }
                        }
                        ChunkState::DataEnd if line.is_empty() => ChunkState::Size,
                        ChunkState::DataEnd => return Err("broken chunk framing".into()),
                        ChunkState::Trailer if line.is_empty() => ChunkState::Done,
                        other => other,
                    };
                }
            }
        }
        Ok(matches!(self.state, ChunkState::Done))
    }
}

pub enum Outcome {
    Complete(u16),
    Cancelled,
}

/// Sends one request and streams the decoded response body to `on_body`.
/// `on_body` returns false to stop reading early. `cancel` is polled while waiting.
pub fn request(
    host: &str,
    port: u16,
    method: &str,
    path: &str,
    body: Option<&[u8]>,
    timeout: Duration,
    cancel: Option<&AtomicBool>,
    on_body: &mut dyn FnMut(u16, &[u8]) -> bool,
) -> Result<Outcome, String> {
    let addr = SocketAddr::new(loopback(host)?, port);
    let mut stream = TcpStream::connect_timeout(&addr, Duration::from_secs(3))
        .map_err(|e| format!("engine not reachable at {}: {}", addr, e))?;
    stream.set_nodelay(true).ok();
    stream
        .set_read_timeout(Some(Duration::from_millis(200)))
        .map_err(|e| e.to_string())?;

    let mut head = format!(
        "{} {} HTTP/1.1\r\nHost: {}:{}\r\nConnection: close\r\nAccept: */*\r\n",
        method, path, host, port
    );
    if let Some(b) = body {
        head.push_str(&format!("Content-Type: application/json\r\nContent-Length: {}\r\n", b.len()));
    }
    head.push_str("\r\n");
    stream.write_all(head.as_bytes()).map_err(|e| e.to_string())?;
    if let Some(b) = body {
        stream.write_all(b).map_err(|e| e.to_string())?;
    }

    let mut buf = vec![0u8; 16 * 1024];
    let mut header: Vec<u8> = Vec::new();
    let mut status: Option<u16> = None;
    let mut chunked: Option<Chunked> = None;
    let mut remaining: Option<usize> = None;
    let mut last_data = Instant::now();

    loop {
        if cancel.map_or(false, |c| c.load(Ordering::SeqCst)) {
            return Ok(Outcome::Cancelled);
        }
        let n = match stream.read(&mut buf) {
            Ok(n) => n,
            Err(e) if matches!(e.kind(), ErrorKind::WouldBlock | ErrorKind::TimedOut | ErrorKind::Interrupted) => {
                if last_data.elapsed() > timeout {
                    return Err(format!("engine timed out after {:?}", timeout));
                }
                continue;
            }
            Err(e) => return Err(format!("read error: {}", e)),
        };
        if n == 0 {
            return match status {
                Some(s) => Ok(Outcome::Complete(s)),
                None => Err("engine closed the connection without a response".into()),
            };
        }
        last_data = Instant::now();
        let mut data = &buf[..n];

        let code = match status {
            Some(code) => code,
            None => {
                header.extend_from_slice(data);
                let Some(end) = header.windows(4).position(|w| w == b"\r\n\r\n") else {
                    if header.len() > 64 * 1024 {
                        return Err("response header too large".into());
                    }
                    continue;
                };
                let text = String::from_utf8_lossy(&header[..end]).to_string();
                let mut lines = text.split("\r\n");
                let code = lines
                    .next()
                    .and_then(|l| l.split_whitespace().nth(1))
                    .and_then(|c| c.parse::<u16>().ok())
                    .ok_or("malformed status line")?;
                for line in lines {
                    let Some((k, v)) = line.split_once(':') else { continue };
                    let (k, v) = (k.trim().to_ascii_lowercase(), v.trim());
                    if k == "transfer-encoding" && v.to_ascii_lowercase().contains("chunked") {
                        chunked = Some(Chunked::new());
                    } else if k == "content-length" {
                        remaining = v.parse().ok();
                    }
                }
                status = Some(code);
                let rest = header.split_off(end + 4);
                header = rest;
                data = &header;
                code
            }
        };

        let mut keep_going = true;
        let finished = if let Some(dec) = chunked.as_mut() {
            dec.feed(data, &mut |p| {
                if keep_going {
                    keep_going = on_body(code, p);
                }
            })?
        } else {
            let take = remaining.map_or(data.len(), |r| r.min(data.len()));
            if take > 0 {
                keep_going = on_body(code, &data[..take]);
            }
            remaining = remaining.map(|r| r - take);
            remaining == Some(0)
        };
        header.clear();
        if finished || !keep_going {
            return Ok(Outcome::Complete(code));
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn rejects_remote_hosts() {
        assert!(loopback("127.0.0.1").is_ok());
        assert!(loopback("::1").is_ok());
        assert!(loopback("localhost").is_ok());
        assert!(loopback("8.8.8.8").is_err());
        assert!(loopback("example.com").is_err());
    }

    #[test]
    fn decodes_chunks_split_anywhere() {
        let wire = b"5\r\nhello\r\n7;ext=1\r\n, world\r\n0\r\n\r\n";
        for split in 0..wire.len() {
            let mut dec = Chunked::new();
            let mut out = Vec::new();
            let a = dec.feed(&wire[..split], &mut |p| out.extend_from_slice(p)).unwrap();
            let b = dec.feed(&wire[split..], &mut |p| out.extend_from_slice(p)).unwrap();
            assert!(a || b);
            assert_eq!(out, b"hello, world");
        }
    }
}

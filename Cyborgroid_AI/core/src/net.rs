//! Reports whether any physical network link is up. Used only to tell the user
//! whether `update` can run; chatting never depends on it.

use std::fs;

pub struct Link {
    pub name: String,
    pub up: bool,
    pub wireless: bool,
}

pub fn physical_links() -> Vec<Link> {
    let Ok(dir) = fs::read_dir("/sys/class/net") else { return Vec::new() };
    let mut links: Vec<Link> = dir
        .flatten()
        .filter_map(|e| {
            let path = e.path();
            // Virtual interfaces (lo, docker, bridges, tun) have no backing device.
            if !path.join("device").exists() {
                return None;
            }
            let state = fs::read_to_string(path.join("operstate")).unwrap_or_default();
            Some(Link {
                name: e.file_name().to_string_lossy().into_owned(),
                up: state.trim() == "up",
                wireless: path.join("wireless").exists() || path.join("phy80211").exists(),
            })
        })
        .collect();
    links.sort_by(|a, b| a.name.cmp(&b.name));
    links
}

pub fn online() -> bool {
    physical_links().iter().any(|l| l.up)
}

pub fn describe() -> String {
    let links = physical_links();
    if links.is_empty() {
        return "no physical network interfaces".into();
    }
    links
        .iter()
        .map(|l| format!("{}{}={}", l.name, if l.wireless { "(wifi)" } else { "" }, if l.up { "up" } else { "down" }))
        .collect::<Vec<_>>()
        .join(", ")
}

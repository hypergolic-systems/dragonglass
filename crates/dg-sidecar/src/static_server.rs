//! HTTP server for the Dragonglass UI.
//!
//! Two URL spaces compose at request time:
//!
//! 1. **Synthesized shell** (`/` and `/index.html`) — inlines the
//!    import map and a single `<script type="module">` importing the
//!    configured entry specifier. No HTML file lives on disk.
//!
//! 2. **GameData mod root** (`/<ModName>/<file>`) — every directory
//!    under `GameData/` that has a `UI/` subdirectory is reachable
//!    here. That includes `Dragonglass_Hud` itself (where the runtime
//!    lives — svelte, instruments, telemetry, stock — addressable
//!    via canonical specifiers like `svelte`/`@dragonglass/stock` in
//!    the import map) and any third-party mod (`@<dir>` namespace).
//!
//! All file lookups go through `safe_join`, which lexically rejects
//! any path that escapes its declared root via `..` or absolute
//! components.

use std::path::{Path, PathBuf};
use std::sync::Arc;

use anyhow::Result;
use tiny_http::{Header, Request, Response};

use crate::importmap::{
    build_importmap, scan_gamedata, scan_runtime_css, synthesize_shell, ModDir,
};

/// Configuration handed to the static server. The spawned thread
/// owns this via `Arc`.
pub struct StaticServerConfig {
    pub gamedata: PathBuf,
    pub entry: String,
}

struct CachedShell {
    html: String,
    mods: Vec<ModDir>,
}

impl CachedShell {
    fn new(cfg: &StaticServerConfig) -> Self {
        let mods = scan_gamedata(&cfg.gamedata);
        let importmap_json = build_importmap(&mods);
        let css_links = scan_runtime_css(&cfg.gamedata);
        let html = synthesize_shell(&importmap_json, &cfg.entry, &css_links);
        eprintln!(
            "static_server: scanned {} mod UI dir(s); runtime CSS auto-link: {}",
            mods.len(),
            css_links.len()
        );
        for m in &mods {
            eprintln!(
                "static_server:   @{}{} → /{}/UI/",
                m.specifier,
                if m.has_index { " (+index)" } else { "" },
                m.dir_name
            );
        }
        Self { html, mods }
    }
}

/// Spawn a background thread serving the configured roots. Returns
/// the base URL (`http://127.0.0.1:<port>`).
pub fn start(cfg: StaticServerConfig) -> Result<String> {
    let server = tiny_http::Server::http("127.0.0.1:0")
        .map_err(|e| anyhow::anyhow!("failed to bind static file server: {e}"))?;
    let port = server.server_addr().to_ip().unwrap().port();
    let base_url = format!("http://127.0.0.1:{port}");

    let cached = Arc::new(CachedShell::new(&cfg));
    let cfg = Arc::new(cfg);

    std::thread::Builder::new()
        .name("static-http".into())
        .spawn(move || {
            for request in server.incoming_requests() {
                handle(request, &cfg, &cached);
            }
        })?;

    Ok(base_url)
}

fn handle(request: Request, cfg: &StaticServerConfig, cached: &CachedShell) {
    // request.url() carries the request-line target — strip query +
    // fragment before any filesystem lookup so `/?ws=...` isn't
    // mistaken for a literal file named `?ws=...`.
    let url = request.url().to_string();
    let path_only = url[..url.find(|c| c == '?' || c == '#').unwrap_or(url.len())].to_string();

    // Synthesized shell (no disk hit).
    if path_only == "/" || path_only == "/index.html" {
        respond_html(request, &cached.html);
        return;
    }

    let rel = path_only.trim_start_matches('/');

    // GameData mod root. The first segment must match a discovered
    // mod's directory name (case-sensitive, matching the on-disk
    // entry). Dragonglass_Hud is just one of those mods — its UI
    // directory holds the runtime ESM bundles addressed via the
    // canonical bare specifiers in `RUNTIME_INDEX`.
    if let Some(slash_idx) = rel.find('/') {
        let head = &rel[..slash_idx];
        let tail = rel[slash_idx + 1..].to_string();
        if let Some(m) = cached.mods.iter().find(|m| m.dir_name == head) {
            let mod_root = cfg.gamedata.join(&m.dir_name).join("UI");
            serve_under(request, &mod_root, &tail);
            return;
        }
    }

    respond_status(request, 404, "not found");
}

fn serve_under(request: Request, root: &Path, rel: &str) {
    let Some(joined) = safe_join(root, rel) else {
        respond_status(request, 403, "forbidden");
        return;
    };
    match std::fs::File::open(&joined) {
        Ok(file) => {
            let header =
                Header::from_bytes(b"Content-Type", content_type_for(&joined).as_bytes()).unwrap();
            let response = Response::from_file(file).with_header(header);
            let _ = request.respond(response);
        }
        Err(_) => {
            respond_status(request, 404, "not found");
        }
    }
}

fn respond_html(request: Request, html: &str) {
    let response = Response::from_string(html.to_string()).with_header(
        Header::from_bytes(b"Content-Type", b"text/html; charset=utf-8").unwrap(),
    );
    let _ = request.respond(response);
}

fn respond_status(request: Request, code: u16, body: &str) {
    let response = Response::from_string(body.to_string()).with_status_code(code);
    let _ = request.respond(response);
}

/// Lexically join `rel` onto `root`, rejecting any path component
/// that escapes the root or contains `..`. Empty `rel` resolves to
/// `root/index.html` so directory roots auto-serve their index.
fn safe_join(root: &Path, rel: &str) -> Option<PathBuf> {
    let trimmed = rel.trim_start_matches('/');
    if trimmed.is_empty() {
        let candidate = root.join("index.html");
        if candidate.starts_with(root) {
            return Some(candidate);
        }
        return None;
    }
    let mut out = PathBuf::from(root);
    for segment in trimmed.split('/') {
        if segment.is_empty() || segment == "." || segment == ".." {
            return None;
        }
        // Reject Windows-style backslashes and drive-letter prefixes.
        if segment.contains('\\') || segment.contains(':') {
            return None;
        }
        out.push(segment);
    }
    if out.starts_with(root) {
        Some(out)
    } else {
        None
    }
}

fn content_type_for(path: &Path) -> &'static str {
    match path.extension().and_then(|e| e.to_str()) {
        Some("html") => "text/html; charset=utf-8",
        Some("js") | Some("mjs") => "application/javascript",
        Some("css") => "text/css",
        Some("json") => "application/json",
        Some("png") => "image/png",
        Some("jpg") | Some("jpeg") => "image/jpeg",
        Some("svg") => "image/svg+xml",
        Some("woff2") => "font/woff2",
        Some("woff") => "font/woff",
        Some("map") => "application/json",
        _ => "application/octet-stream",
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::fs;

    #[test]
    fn safe_join_resolves_relative_path() {
        let root = std::env::temp_dir();
        let p = safe_join(&root, "a/b/c.js").unwrap();
        assert_eq!(p, root.join("a").join("b").join("c.js"));
    }

    #[test]
    fn safe_join_rejects_dotdot() {
        let root = std::env::temp_dir();
        assert!(safe_join(&root, "../outside.js").is_none());
        assert!(safe_join(&root, "a/../../outside.js").is_none());
    }

    #[test]
    fn safe_join_rejects_drive_and_backslash() {
        let root = std::env::temp_dir();
        assert!(safe_join(&root, "C:/Windows/system32").is_none());
        assert!(safe_join(&root, "a\\b").is_none());
    }

    #[test]
    fn safe_join_empty_returns_index() {
        let root = std::env::temp_dir();
        assert_eq!(safe_join(&root, "").unwrap(), root.join("index.html"));
        assert_eq!(safe_join(&root, "/").unwrap(), root.join("index.html"));
    }

    #[test]
    fn content_type_known_extensions() {
        assert_eq!(content_type_for(Path::new("a.js")), "application/javascript");
        assert_eq!(content_type_for(Path::new("a.css")), "text/css");
        assert_eq!(content_type_for(Path::new("a.html")), "text/html; charset=utf-8");
        assert_eq!(content_type_for(Path::new("a.unknown")), "application/octet-stream");
    }

    #[test]
    fn end_to_end_serves_shell_and_mod_files() {
        // Stage a fake install: gamedata with Dragonglass_Hud (the
        // core/runtime mod) plus a third-party mod, both addressable
        // through the same /<ModName>/<file> URL space.
        let base = std::env::temp_dir().join(format!(
            "dg-static-e2e-{}-{}",
            std::process::id(),
            std::time::SystemTime::now()
                .duration_since(std::time::UNIX_EPOCH)
                .map(|d| d.as_nanos())
                .unwrap_or(0)
        ));
        let gamedata = base.join("GameData");
        let core_ui = gamedata.join("Dragonglass_Hud").join("UI");
        let kerbalism_ui = gamedata.join("Kerbalism").join("UI");
        fs::create_dir_all(core_ui.join("chunks")).unwrap();
        fs::write(core_ui.join("svelte.js"), "// svelte").unwrap();
        fs::write(core_ui.join("stock.js"), "// stock").unwrap();
        fs::write(core_ui.join("runtime.css"), ":root { --x: 1; }").unwrap();
        fs::write(core_ui.join("chunks").join("deep-abc.js"), "// chunk").unwrap();
        fs::create_dir_all(&kerbalism_ui).unwrap();
        fs::write(kerbalism_ui.join("index.js"), "export const x = 1;").unwrap();

        let base_url = start(StaticServerConfig {
            gamedata: gamedata.clone(),
            entry: "@dragonglass/stock".into(),
        })
        .unwrap();

        // Synthesized shell.
        let shell = http_get(&format!("{base_url}/")).unwrap();
        assert!(shell.contains("<script type=\"importmap\">"));
        assert!(shell.contains("@dragonglass/stock"));
        assert!(shell.contains("@kerbalism"));
        assert!(shell.contains("/Dragonglass_Hud/runtime.css"));

        // Runtime ESM via /<mod>/ — UI/ is implicit on disk.
        let svelte_js = http_get(&format!("{base_url}/Dragonglass_Hud/svelte.js")).unwrap();
        assert!(svelte_js.contains("// svelte"));
        let chunk =
            http_get(&format!("{base_url}/Dragonglass_Hud/chunks/deep-abc.js")).unwrap();
        assert!(chunk.contains("// chunk"));

        // Third-party mod file.
        let mod_js = http_get(&format!("{base_url}/Kerbalism/index.js")).unwrap();
        assert!(mod_js.contains("export const x = 1;"));

        // Path traversal: rejected.
        let traversal =
            http_status(&format!("{base_url}/Dragonglass_Hud/../../etc/passwd"));
        assert_eq!(traversal, 403);

        // Unknown mod: 404.
        let unknown = http_status(&format!("{base_url}/UnknownMod/foo.js"));
        assert_eq!(unknown, 404);

        let _ = fs::remove_dir_all(&base);
    }

    fn http_get(url: &str) -> Option<String> {
        use std::io::Read;
        let parsed = url.strip_prefix("http://")?;
        let (host_port, path) = parsed.split_once('/').map(|(h, p)| (h, format!("/{p}")))
            .unwrap_or((parsed, "/".to_string()));
        let mut stream = std::net::TcpStream::connect(host_port).ok()?;
        use std::io::Write;
        write!(stream, "GET {path} HTTP/1.0\r\nHost: {host_port}\r\n\r\n").ok()?;
        let mut buf = String::new();
        stream.read_to_string(&mut buf).ok()?;
        let body_start = buf.find("\r\n\r\n")? + 4;
        Some(buf[body_start..].to_string())
    }

    fn http_status(url: &str) -> u16 {
        use std::io::Read;
        let parsed = url.strip_prefix("http://").unwrap();
        let (host_port, path) = parsed.split_once('/').map(|(h, p)| (h, format!("/{p}")))
            .unwrap_or((parsed, "/".to_string()));
        let mut stream = std::net::TcpStream::connect(host_port).unwrap();
        use std::io::Write;
        write!(stream, "GET {path} HTTP/1.0\r\nHost: {host_port}\r\n\r\n").unwrap();
        let mut buf = String::new();
        stream.read_to_string(&mut buf).unwrap();
        buf.split(' ').nth(1).and_then(|s| s.parse().ok()).unwrap_or(0)
    }

    #[test]
    fn cached_shell_synthesizes_html_with_mods() {
        let base = std::env::temp_dir().join(format!(
            "dg-static-test-{}-{}",
            std::process::id(),
            std::time::SystemTime::now()
                .duration_since(std::time::UNIX_EPOCH)
                .map(|d| d.as_nanos())
                .unwrap_or(0)
        ));
        let gamedata = base.join("GameData");
        fs::create_dir_all(gamedata.join("Kerbalism").join("UI")).unwrap();
        fs::write(gamedata.join("Kerbalism").join("UI").join("index.js"), "").unwrap();
        fs::create_dir_all(gamedata.join("Dragonglass_Hud").join("UI")).unwrap();
        fs::write(
            gamedata.join("Dragonglass_Hud").join("UI").join("runtime.css"),
            "/* tokens */",
        )
        .unwrap();

        let cfg = StaticServerConfig {
            gamedata,
            entry: "@dragonglass/stock".into(),
        };
        let cached = CachedShell::new(&cfg);
        assert!(cached.html.contains("@dragonglass/stock"));
        assert!(cached.html.contains(r#"href="/Dragonglass_Hud/runtime.css""#));
        assert!(cached.html.contains(r#""@kerbalism""#));
        // Kerbalism + Dragonglass_Hud both registered.
        assert_eq!(cached.mods.len(), 2);
        let _ = fs::remove_dir_all(&base);
    }
}

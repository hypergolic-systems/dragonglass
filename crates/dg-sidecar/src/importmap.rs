//! Import-map generation and shell HTML synthesis.
//!
//! Two pieces of state come together when CEF requests `/`:
//!
//! 1. The **runtime index** — a static list of (specifier, file
//!    path) pairs that mirrors what the `ui/runtime/` build emits
//!    into `Dragonglass_Hud/UI/`. These are bare specifiers like
//!    `svelte`, `@dragonglass/instruments`, `@dragonglass/stock`,
//!    that map to canonical npm-style names. They resolve to URLs
//!    under `/Dragonglass_Hud/UI/` — the same place stock and the
//!    other core packages live.
//!
//! 2. The **mod scan** — every immediate child of `GameData/` that
//!    has a `UI/` subdirectory becomes a Dragonglass UI mod with
//!    specifier `@<dir>` (lowercased). `Dragonglass_Hud` is included
//!    here too; addressing it as `@dragonglass_hud/...` works as a
//!    fallback to the canonical specifiers.
//!
//! `build_importmap` joins the two into a single JSON document.
//! `synthesize_shell` wraps it in the minimum `<html>` document the
//! synthesized shell needs (`<div id="app">` mount target plus a
//! single `<script type="module">import "<entry>";</script>`).

use std::collections::BTreeMap;
use std::path::{Path, PathBuf};

/// On-disk directory name of the Dragonglass core mod. The runtime
/// build ships its output here; the runtime index entries below
/// resolve to URLs under `/<this>/UI/`.
pub const CORE_MOD_DIR: &str = "Dragonglass_Hud";

/// One core specifier the sidecar publishes via the import map. The
/// path is relative to `<CORE_MOD_DIR>/UI/`; the static server URL
/// composes the two.
pub struct RuntimeEntry {
    pub specifier: &'static str,
    pub path: &'static str,
}

/// The set of bare specifiers Dragonglass exposes alongside its
/// runtime files in `Dragonglass_Hud/UI/`. Mirrors the entries in
/// `ui/runtime/vite.config.ts` — keep them in sync. Pure `*.css`
/// assets (theme tokens) aren't import-map entries; they're auto-
/// `<link>`ed by `synthesize_shell` separately.
pub const RUNTIME_INDEX: &[RuntimeEntry] = &[
    RuntimeEntry { specifier: "svelte",                              path: "svelte.js" },
    // Svelte 5 compiled-component output emits direct imports of
    // these internal sub-paths. Mapping them explicitly lets
    // externally-built UI mods (Nova et al.) share runtime chunks
    // with stock instead of bundling their own Svelte instance.
    RuntimeEntry { specifier: "svelte/internal/client",              path: "svelte/internal/client.js" },
    RuntimeEntry { specifier: "svelte/internal/disclose-version",    path: "svelte/internal/disclose-version.js" },
    RuntimeEntry { specifier: "svelte/internal/flags/legacy",        path: "svelte/internal/flags/legacy.js" },
    // Reactive collections (SvelteMap, SvelteSet, SvelteDate, etc.) —
    // mods reach for these for $state-y structures with dynamic keys.
    // Shared through the runtime so everyone signals against the same
    // instance.
    RuntimeEntry { specifier: "svelte/reactivity",                   path: "svelte/reactivity.js" },
    RuntimeEntry { specifier: "three",                               path: "three.js" },
    RuntimeEntry { specifier: "@threlte/core",                       path: "threlte.js" },
    RuntimeEntry { specifier: "@dragonglass/instruments",            path: "instruments/index.js" },
    RuntimeEntry { specifier: "@dragonglass/windows",                path: "windows/index.js" },
    RuntimeEntry { specifier: "@dragonglass/telemetry/core",         path: "telemetry/core.js" },
    RuntimeEntry { specifier: "@dragonglass/telemetry/svelte",       path: "telemetry/svelte.js" },
    RuntimeEntry { specifier: "@dragonglass/telemetry/simulated",    path: "telemetry/simulated.js" },
    RuntimeEntry { specifier: "@dragonglass/telemetry/smoothing",    path: "telemetry/smoothing.js" },
    RuntimeEntry { specifier: "@dragonglass/telemetry/dragonglass",  path: "telemetry/dragonglass.js" },
    RuntimeEntry { specifier: "@dragonglass/stock",                  path: "stock.js" },
];

/// One discovered mod under `GameData/`. The directory is
/// `<gamedata>/<dir_name>/UI/`; `dir_name` is the on-disk name (case
/// preserved); `specifier` is the lowercased form used as the import-
/// map key.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ModDir {
    pub dir_name: String,
    pub specifier: String,
    pub has_index: bool,
}

/// Walk the immediate children of `gamedata`, returning one `ModDir`
/// per directory that has a `UI/` subdirectory. Includes
/// `Dragonglass_Hud` — its UI files (svelte, stock, etc.) are
/// addressable via `@dragonglass_hud/...` as a fallback to the
/// canonical specifiers in `RUNTIME_INDEX`.
/// Failures to read a child directory are skipped silently (logging
/// is the caller's responsibility).
pub fn scan_gamedata(gamedata: &Path) -> Vec<ModDir> {
    let mut out: Vec<ModDir> = Vec::new();
    let entries = match std::fs::read_dir(gamedata) {
        Ok(e) => e,
        Err(_) => return out,
    };
    for entry in entries.flatten() {
        let dir_name = match entry.file_name().into_string() {
            Ok(s) => s,
            Err(_) => continue, // non-UTF-8 dir names are unlikely on KSP installs; skip
        };
        let ui_dir = entry.path().join("UI");
        if !ui_dir.is_dir() {
            continue;
        }
        let has_index = ui_dir.join("index.js").is_file();
        out.push(ModDir {
            specifier: dir_name.to_ascii_lowercase(),
            dir_name,
            has_index,
        });
    }
    out.sort_by(|a, b| a.specifier.cmp(&b.specifier));
    out
}

/// Build the import-map JSON. Core runtime specifiers resolve to URLs
/// under `/<CORE_MOD_DIR>/`; mod packages get `@<dir>` and `@<dir>/`
/// entries pointing at `/<dir>/`. The on-disk `UI/` segment doesn't
/// appear in URLs — the static server resolves `/<mod>/<rest>` to
/// `<gamedata>/<mod>/UI/<rest>`.
pub fn build_importmap(mods: &[ModDir]) -> String {
    let mut imports: BTreeMap<String, String> = BTreeMap::new();
    for entry in RUNTIME_INDEX {
        imports.insert(
            entry.specifier.to_string(),
            format!("/{CORE_MOD_DIR}/{}", entry.path),
        );
    }
    for m in mods {
        let url_prefix = format!("/{}/", m.dir_name);
        // Trailing-slash specifier always maps. Bare specifier maps
        // to <UI>/index.js if the mod ships one.
        imports.insert(format!("@{}/", m.specifier), url_prefix.clone());
        if m.has_index {
            imports.insert(format!("@{}", m.specifier), format!("{url_prefix}index.js"));
        }
    }
    let map = serde_json::json!({ "imports": imports });
    serde_json::to_string_pretty(&map).expect("import map serialization is total")
}

/// Generate the shell HTML the sidecar serves on `/`. Inlines the
/// import map and ships a single `<script type="module">` that
/// imports `entry` (a bare specifier the import map resolves). CSS
/// files in the runtime are auto-linked — they're internal Dragonglass
/// assets, not third-party — so theme tokens land in the document
/// regardless of which entry mounts.
///
/// All embedded content gets `</` → `<\/` rewritten so neither the
/// import map nor the entry specifier can break out of the
/// surrounding `<script>` element. JSON's grammar allows `\/`; HTML
/// stops looking for `</script>` once the slash isn't there.
pub fn synthesize_shell(importmap_json: &str, entry: &str, runtime_css: &[String]) -> String {
    let mut links = String::new();
    for href in runtime_css {
        links.push_str(&format!(
            "  <link rel=\"stylesheet\" href=\"{}\">\n",
            html_escape_attr(href)
        ));
    }
    let safe_importmap = importmap_json.replace("</", "<\\/");
    let safe_entry = json_string_lit(entry).replace("</", "<\\/");
    format!(
        "<!doctype html>\n\
         <html lang=\"en\">\n\
         <head>\n  \
           <meta charset=\"UTF-8\">\n  \
           <title>Dragonglass</title>\n\
           {links}  \
           <script type=\"importmap\">\n{safe_importmap}\n  </script>\n\
         </head>\n\
         <body>\n  \
           <div id=\"app\"></div>\n  \
           <script type=\"module\">import {safe_entry};</script>\n\
         </body>\n\
         </html>\n",
    )
}

/// Enumerate top-level `*.css` files in `<gamedata>/Dragonglass_Hud/UI/`
/// and return their URL paths. Used by `synthesize_shell` to auto-link
/// Dragonglass's internal stylesheets (theme tokens, stock's compiled
/// CSS). Mod CSS is _not_ auto-linked — only files in the core
/// directory. Missing/unreadable directory returns an empty vector.
pub fn scan_runtime_css(gamedata: &Path) -> Vec<String> {
    let mut out: Vec<String> = Vec::new();
    let core_ui = gamedata.join(CORE_MOD_DIR).join("UI");
    let entries = match std::fs::read_dir(&core_ui) {
        Ok(e) => e,
        Err(_) => return out,
    };
    for entry in entries.flatten() {
        if !entry.file_type().map(|ft| ft.is_file()).unwrap_or(false) {
            continue;
        }
        let name = match entry.file_name().into_string() {
            Ok(s) => s,
            Err(_) => continue,
        };
        if name.to_ascii_lowercase().ends_with(".css") {
            out.push(format!("/{CORE_MOD_DIR}/{name}"));
        }
    }
    out.sort();
    out
}

/// Convenience: the on-disk path the sidecar should serve under
/// `/<mod>/...`. Caller must validate the leading segment against
/// the scanned mod list before calling.
pub fn mod_root(gamedata: &Path, mods: &[ModDir], dir_name: &str) -> Option<PathBuf> {
    mods.iter()
        .find(|m| m.dir_name == dir_name)
        .map(|m| gamedata.join(&m.dir_name).join("UI"))
}

fn json_string_lit(s: &str) -> String {
    serde_json::to_string(s).unwrap_or_else(|_| "\"\"".into())
}

fn html_escape_attr(s: &str) -> String {
    s.replace('&', "&amp;")
        .replace('"', "&quot;")
        .replace('<', "&lt;")
        .replace('>', "&gt;")
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::fs;

    fn tmpdir() -> PathBuf {
        let base = std::env::temp_dir().join(format!(
            "dg-importmap-test-{}-{}",
            std::process::id(),
            // monotonic-ish suffix so multiple tests don't collide
            std::time::SystemTime::now()
                .duration_since(std::time::UNIX_EPOCH)
                .map(|d| d.as_nanos())
                .unwrap_or(0)
        ));
        fs::create_dir_all(&base).unwrap();
        base
    }

    #[test]
    fn scan_finds_ui_dirs_including_dragonglass_hud() {
        let root = tmpdir();
        for name in ["Kerbalism", "KER", "NoUI", "Dragonglass_Hud"] {
            fs::create_dir_all(root.join(name)).unwrap();
        }
        fs::create_dir_all(root.join("Kerbalism").join("UI")).unwrap();
        fs::write(root.join("Kerbalism").join("UI").join("index.js"), "").unwrap();
        fs::create_dir_all(root.join("KER").join("UI")).unwrap();
        // No UI/ dir in NoUI.
        fs::create_dir_all(root.join("Dragonglass_Hud").join("UI")).unwrap();

        let mods = scan_gamedata(&root);
        let names: Vec<&str> = mods.iter().map(|m| m.dir_name.as_str()).collect();
        assert_eq!(names, vec!["Dragonglass_Hud", "KER", "Kerbalism"]);
        let kerbalism = mods.iter().find(|m| m.dir_name == "Kerbalism").unwrap();
        assert!(kerbalism.has_index);
        let ker = mods.iter().find(|m| m.dir_name == "KER").unwrap();
        assert!(!ker.has_index);
        assert_eq!(ker.specifier, "ker");
        let _ = fs::remove_dir_all(&root);
    }

    #[test]
    fn build_importmap_includes_runtime_and_mods() {
        let mods = vec![
            ModDir {
                dir_name: "Dragonglass_Hud".into(),
                specifier: "dragonglass_hud".into(),
                has_index: false,
            },
            ModDir {
                dir_name: "Kerbalism".into(),
                specifier: "kerbalism".into(),
                has_index: true,
            },
            ModDir {
                dir_name: "KER".into(),
                specifier: "ker".into(),
                has_index: false,
            },
        ];
        let json = build_importmap(&mods);
        // Core runtime specifiers point under /Dragonglass_Hud/
        assert!(json.contains(r#""svelte": "/Dragonglass_Hud/svelte.js""#));
        assert!(json.contains(r#""@dragonglass/stock": "/Dragonglass_Hud/stock.js""#));
        assert!(
            json.contains(r#""@dragonglass/windows": "/Dragonglass_Hud/windows/index.js""#)
        );
        // Dragonglass_Hud is also addressable as a mod (fallback).
        assert!(json.contains(r#""@dragonglass_hud/": "/Dragonglass_Hud/""#));
        // Mod with index gets both forms.
        assert!(json.contains(r#""@kerbalism": "/Kerbalism/index.js""#));
        assert!(json.contains(r#""@kerbalism/": "/Kerbalism/""#));
        // Mod without index gets only trailing-slash form.
        assert!(json.contains(r#""@ker/": "/KER/""#));
        assert!(!json.contains(r#""@ker":"#));
    }

    #[test]
    fn synthesize_shell_inlines_importmap_and_entry() {
        let map = "{\"imports\":{}}";
        let html = synthesize_shell(map, "@dragonglass/stock", &[]);
        assert!(html.contains("<script type=\"importmap\">"));
        assert!(html.contains("\"imports\""));
        assert!(html.contains(r#"import "@dragonglass/stock";"#));
        assert!(html.contains(r#"<div id="app"></div>"#));
    }

    #[test]
    fn synthesize_shell_auto_links_runtime_css() {
        let html = synthesize_shell(
            "{}",
            "@dragonglass/stock",
            &[
                "/Dragonglass_Hud/runtime.css".into(),
                "/Dragonglass_Hud/stock.css".into(),
            ],
        );
        assert!(html.contains(r#"<link rel="stylesheet" href="/Dragonglass_Hud/runtime.css">"#));
        assert!(html.contains(r#"<link rel="stylesheet" href="/Dragonglass_Hud/stock.css">"#));
    }

    #[test]
    fn synthesize_shell_blocks_script_tag_breakout() {
        // The entry specifier sits inside a `<script type="module">`
        // element. A specifier containing `</script>` would close the
        // tag and let the rest execute as inline HTML. Defense: we
        // rewrite `</` to `<\/` before embedding (valid JSON escape
        // for `/`, and HTML stops looking for `</script>` without the
        // slash). The same defense applies to the importmap JSON.
        let html = synthesize_shell("{}", "</script><img onerror=alert(1)>", &[]);
        assert!(!html.contains("</script><img"));
        assert!(html.contains("<\\/script>"));

        let evil_map = r#"{"imports":{"x":"</script><img>"}}"#;
        let html2 = synthesize_shell(evil_map, "@dragonglass/stock", &[]);
        assert!(!html2.contains("</script><img"));
        assert!(html2.contains("<\\/script>"));
    }
}

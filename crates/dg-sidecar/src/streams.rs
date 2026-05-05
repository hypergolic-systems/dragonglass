//! Punch-through stream rect plumbing.
//!
//! The HUD UI declares `<PunchThrough id="…" chroma="…">` placeholders.
//! A `requestAnimationFrame` pump on the page collects each placeholder's
//! current bounds and ships the snapshot to the sidecar via a custom
//! V8 binding (`window.dgUpdatePunchRects(json)`) which we install in
//! the renderer's context. The binding sends a raw `CefProcessMessage`
//! named `dg_punch_rects` carrying the JSON string to the browser
//! process, where `Client::on_process_message_received` decodes it and
//! writes the SHM stream-rect table.
//!
//! We deliberately *do not* use `cef-rs`'s `MessageRouter` here:
//! it relies on `currently_on(ThreadId::*)` checks that don't hold
//! under our `external_message_pump=1` setup, causing the browser
//! handler to never be invoked and the renderer-side V8 binding to
//! silently return `undefined` (the router's `send_query` requires
//! `currently_on(RENDERER)` and bails out otherwise). A direct V8
//! handler + raw `CefProcessMessage` works around all of that with
//! ~80 lines of Rust.
//!
//! The snapshot format (string arg of the process message) is:
//!
//! ```json
//! {
//!   "rects": [
//!     { "id": "kerbal:Jeb", "x": 1620, "y": 880, "w": 240, "h": 240,
//!       "chroma": [255, 0, 255], "threshold": 24 }
//!   ]
//! }
//! ```

use std::sync::Mutex;

use cef::*;

use dg_shm::{ShmWriter, StreamRect};

/// Name of the `CefProcessMessage` carrying punch-through rect
/// snapshots from the renderer to the browser process.
pub const PUNCH_RECTS_MSG: &str = "dg_punch_rects";

/// Name of the JS function we bind on `window` for the page to call
/// with a JSON snapshot string.
pub const PUNCH_RECTS_JS_FN: &str = "dgUpdatePunchRects";

/// FNV-1a 32-bit hash. Matches the implementation used in the mod and
/// the JS pump; small change here means breaking the wire.
pub fn fnv1a_32(s: &str) -> u32 {
    let mut h: u32 = 0x811C_9DC5;
    for b in s.as_bytes() {
        h ^= *b as u32;
        h = h.wrapping_mul(0x0100_0193);
    }
    h
}

/// Parse the JSON rect snapshot into a vector of `StreamRect` slots.
/// Errors propagate as `serde_json::Error`; entries with empty `id`
/// are silently skipped (defensive — an unmounted PunchThrough should
/// just disappear, not error).
pub fn parse_rects(json: &str) -> Result<Vec<StreamRect>, serde_json::Error> {
    use serde_json::Value;
    let v: Value = serde_json::from_str(json)?;
    let arr = v.get("rects").and_then(Value::as_array);
    let Some(arr) = arr else {
        return Ok(Vec::new());
    };
    let mut out = Vec::with_capacity(arr.len());
    for item in arr {
        let id = item.get("id").and_then(Value::as_str).unwrap_or("");
        if id.is_empty() {
            continue;
        }
        let x = item.get("x").and_then(Value::as_i64).unwrap_or(0) as i16;
        let y = item.get("y").and_then(Value::as_i64).unwrap_or(0) as i16;
        let w = item.get("w").and_then(Value::as_u64).unwrap_or(0) as u16;
        let h = item.get("h").and_then(Value::as_u64).unwrap_or(0) as u16;
        let visible = item.get("visible").and_then(Value::as_bool).unwrap_or(true);
        let (cr, cg, cb) = item
            .get("chroma")
            .and_then(Value::as_array)
            .map(|a| {
                (
                    a.first().and_then(Value::as_u64).unwrap_or(255) as u8,
                    a.get(1).and_then(Value::as_u64).unwrap_or(0) as u8,
                    a.get(2).and_then(Value::as_u64).unwrap_or(255) as u8,
                )
            })
            .unwrap_or((255, 0, 255));
        let threshold = item
            .get("threshold")
            .and_then(Value::as_u64)
            .unwrap_or(24)
            .min(255) as u8;
        let mut flags = 0u32;
        if visible {
            flags |= dg_shm::layout::STREAM_FLAG_VISIBLE;
        }
        out.push(StreamRect {
            id_hash: fnv1a_32(id),
            x,
            y,
            w,
            h,
            chroma_r: cr,
            chroma_g: cg,
            chroma_b: cb,
            threshold,
            flags,
        });
    }
    Ok(out)
}

/// Browser-side handler: invoked from `Client::on_process_message_received`
/// for messages named `PUNCH_RECTS_MSG`. Decodes the single string
/// argument as JSON and writes the SHM stream-rect table.
pub fn handle_punch_rects_message(
    writer: &Mutex<ShmWriter>,
    message: &mut ProcessMessage,
) -> bool {
    let Some(args) = message.argument_list() else {
        eprintln!("dg_punch_rects: message has no argument list");
        return false;
    };
    if args.size() == 0 {
        eprintln!("dg_punch_rects: empty argument list");
        return false;
    }
    let json_userfree = args.string(0);
    let json: CefStringUtf16 = (&json_userfree).into();
    let json = json.to_string();
    match parse_rects(&json) {
        Ok(rects) => {
            if let Ok(mut w) = writer.lock() {
                w.write_stream_rects(&rects);
            }
            true
        }
        Err(e) => {
            eprintln!("dg_punch_rects: parse error: {e}");
            false
        }
    }
}

// ---------------------------------------------------------------------
// Renderer-side V8 handler
// ---------------------------------------------------------------------
//
// Bound to `window.dgUpdatePunchRects` in `on_context_created`. Takes a
// single string argument (the JSON snapshot). Builds a CefProcessMessage
// with that string as arg[0] and sends it to the browser process via
// the current frame.

wrap_v8_handler! {
    pub struct DgPunchRectsV8HandlerBuilder {}

    impl V8Handler {
        fn execute(
            &self,
            name: Option<&CefString>,
            _object: Option<&mut V8Value>,
            arguments: Option<&[Option<V8Value>]>,
            _retval: Option<&mut Option<V8Value>>,
            exception: Option<&mut CefString>,
        ) -> ::std::os::raw::c_int {
            let fn_name = name.map(|s| s.to_string()).unwrap_or_default();
            if fn_name != PUNCH_RECTS_JS_FN {
                return 0;
            }
            let Some(args) = arguments else {
                if let Some(ex) = exception {
                    *ex = CefString::from("dgUpdatePunchRects: no arguments");
                }
                return 1;
            };
            if args.len() != 1 {
                if let Some(ex) = exception {
                    *ex = CefString::from("dgUpdatePunchRects: expected 1 argument");
                }
                return 1;
            }
            let Some(arg) = args[0].as_ref() else {
                if let Some(ex) = exception {
                    *ex = CefString::from("dgUpdatePunchRects: argument is undefined");
                }
                return 1;
            };
            if arg.is_string() == 0 {
                if let Some(ex) = exception {
                    *ex = CefString::from("dgUpdatePunchRects: argument must be a string");
                }
                return 1;
            }
            let json_userfree = arg.string_value();
            let json: CefStringUtf16 = (&json_userfree).into();
            let json_string = json.to_string();

            // Find the frame to send the message from. Prefer the
            // entered context (the one V8 is currently dispatching
            // from); fall back to the current context.
            let context = v8_context_get_entered_context()
                .or_else(v8_context_get_current_context);
            let Some(context) = context else {
                if let Some(ex) = exception {
                    *ex = CefString::from("dgUpdatePunchRects: no V8 context");
                }
                return 1;
            };
            let Some(frame) = context.frame() else {
                if let Some(ex) = exception {
                    *ex = CefString::from("dgUpdatePunchRects: V8 context has no frame");
                }
                return 1;
            };

            let msg_name = CefString::from(PUNCH_RECTS_MSG);
            let Some(mut msg) = process_message_create(Some(&msg_name)) else {
                if let Some(ex) = exception {
                    *ex = CefString::from("dgUpdatePunchRects: process_message_create returned None");
                }
                return 1;
            };
            if let Some(args_list) = msg.argument_list() {
                let json_cef = CefString::from(json_string.as_str());
                args_list.set_string(0, Some(&json_cef));
            }
            frame.send_process_message(ProcessId::BROWSER, Some(&mut msg));
            1
        }
    }
}

impl DgPunchRectsV8HandlerBuilder {
    pub fn build() -> V8Handler {
        Self::new()
    }
}

/// Install the punch-rects JS binding on the freshly-created V8
/// context. Call this from `RenderProcessHandler::on_context_created`.
pub fn install_punch_rects_binding(context: &V8Context) {
    let Some(global) = context.global() else {
        eprintln!("install_punch_rects_binding: V8 context has no global object");
        return;
    };
    let mut handler = DgPunchRectsV8HandlerBuilder::build();
    let name = CefString::from(PUNCH_RECTS_JS_FN);
    let Some(mut func) = v8_value_create_function(Some(&name), Some(&mut handler)) else {
        eprintln!("install_punch_rects_binding: v8_value_create_function returned None");
        return;
    };
    let attrs = sys::cef_v8_propertyattribute_t([
        sys::cef_v8_propertyattribute_t::V8_PROPERTY_ATTRIBUTE_READONLY,
        sys::cef_v8_propertyattribute_t::V8_PROPERTY_ATTRIBUTE_DONTENUM,
        sys::cef_v8_propertyattribute_t::V8_PROPERTY_ATTRIBUTE_DONTDELETE,
    ]
    .into_iter()
    .fold(0, |acc, a| acc | a.0))
    .into();
    global.set_value_bykey(Some(&name), Some(&mut func), attrs);
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn fnv1a_matches_reference() {
        // Well-known reference values for the FNV-1a 32-bit hash.
        assert_eq!(fnv1a_32(""), 0x811C_9DC5);
        assert_eq!(fnv1a_32("a"), 0xE40C_292C);
        assert_eq!(fnv1a_32("foobar"), 0xBF9C_F968);
    }

    #[test]
    fn parses_basic_snapshot() {
        let json = r#"{
            "rects": [
                {"id":"kerbal:Jeb","x":10,"y":20,"w":128,"h":128,
                 "chroma":[255,0,255],"threshold":24,"visible":true}
            ]
        }"#;
        let rs = parse_rects(json).expect("parse");
        assert_eq!(rs.len(), 1);
        let r = &rs[0];
        assert_eq!(r.id_hash, fnv1a_32("kerbal:Jeb"));
        assert_eq!(r.x, 10);
        assert_eq!(r.w, 128);
        assert_eq!((r.chroma_r, r.chroma_g, r.chroma_b), (255, 0, 255));
        assert_eq!(r.threshold, 24);
        assert_eq!(
            r.flags & dg_shm::layout::STREAM_FLAG_VISIBLE,
            dg_shm::layout::STREAM_FLAG_VISIBLE
        );
    }

    #[test]
    fn skips_entries_with_no_id() {
        let json = r#"{"rects":[{"x":0,"y":0,"w":1,"h":1}]}"#;
        let rs = parse_rects(json).expect("parse");
        assert!(rs.is_empty());
    }
}

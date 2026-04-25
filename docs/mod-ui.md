# Dragonglass UI mods

Third-party KSP mods can publish browser UI packages alongside their KSP
plugins. Dragonglass discovers them at startup and exposes each as a
browser import-map specifier (`@<modname>`) that any other UI package —
including the stock HUD — can import from. Stock and third-party UIs
are symmetric: stock is just the mod whose `UI/` happens to ship the
runtime.

## Discovery

Dragonglass walks the immediate children of `GameData/` at sidecar
startup. A directory is treated as a Dragonglass UI mod if it contains a
`UI/` subdirectory.

```
GameData/
├── Dragonglass_Hud/
│   └── UI/                 ← runtime: svelte.js, three.js, stock.js,
│       ├── stock.js        │  instruments/, telemetry/, chunks/, …
│       ├── svelte.js
│       └── …
├── Kerbalism/
│   └── UI/                 ← @kerbalism — discovered, mapped automatically
│       ├── index.js
│       └── status.js
└── SomeOtherMod/           ← no UI/, not registered
```

## URL space

The static server exposes one URL space:

| URL pattern              | resolves to                              |
| ------------------------ | ---------------------------------------- |
| `/`, `/index.html`       | synthesized shell HTML (importmap + boot script) |
| `/<ModName>/<file>`      | `<gamedata>/<ModName>/UI/<file>`         |

The on-disk `UI/` segment doesn't appear in URLs — it's implicit. So
`@dragonglass/stock` resolves to `/Dragonglass_Hud/stock.js`, served
from `<KSP>/GameData/Dragonglass_Hud/UI/stock.js`.

## Import-map specifiers

Every discovered mod gets two import-map entries:

| specifier      | resolves to                       |
| -------------- | --------------------------------- |
| `@<mod>`       | `/<ModName>/index.js` (only if that file exists) |
| `@<mod>/<…>`   | `/<ModName>/<…>`                  |

`<mod>` is the directory name lowercased. The URL preserves the
filesystem case. So a mod installed at `GameData/Kerbalism/UI/` is
addressable as:

```js
import '@kerbalism';                      // → /Kerbalism/index.js
import { foo } from '@kerbalism/status';  // → /Kerbalism/status.js
```

## Core runtime

Dragonglass also publishes its own packages under canonical npm-style
specifiers:

| specifier                            | resolves to                                |
| ------------------------------------ | ------------------------------------------ |
| `svelte`                             | `/Dragonglass_Hud/svelte.js`               |
| `three`                              | `/Dragonglass_Hud/three.js`                |
| `@threlte/core`                      | `/Dragonglass_Hud/threlte.js`              |
| `@dragonglass/instruments`           | `/Dragonglass_Hud/instruments/index.js`    |
| `@dragonglass/telemetry/core`        | `/Dragonglass_Hud/telemetry/core.js`       |
| `@dragonglass/telemetry/svelte`      | `/Dragonglass_Hud/telemetry/svelte.js`     |
| `@dragonglass/telemetry/simulated`   | `/Dragonglass_Hud/telemetry/simulated.js`  |
| `@dragonglass/telemetry/smoothing`   | `/Dragonglass_Hud/telemetry/smoothing.js`  |
| `@dragonglass/telemetry/dragonglass` | `/Dragonglass_Hud/telemetry/dragonglass.js`|
| `@dragonglass/stock`                 | `/Dragonglass_Hud/stock.js`                |

These are also reachable as `@dragonglass_hud/<file>` via the standard
mod-namespace mapping (`Dragonglass_Hud` is just another mod) — but the
canonical specifiers above are preferred.

## Shared runtime

All core specifiers — including `@dragonglass/stock` — are built in a
single Rollup pass. Shared deps (Svelte runtime, three.js, render
helpers, snippet helpers) extract into `chunks/<name>-<hash>.js`.
Every entry imports from the same chunk URLs, so the browser
instantiates each shared module exactly once.

That gives you **a single Svelte runtime instance** spanning stock,
the runtime entries, and any third-party mod that imports from the
runtime via the import map. `setContext` / `getContext` cross
boundaries, stores subscribe across packages, effects propagate. Mods
don't need to bundle their own copy of Svelte — and shouldn't, if they
want to compose with stock's tree.

## CSS

Two paths, depending on the scope:

**Component-coupled CSS** — import as a CSS module:

```js
// inside @kerbalism/status.js
import sheet from './status.css' with { type: 'css' };
document.adoptedStyleSheets = [...document.adoptedStyleSheets, sheet];
```

This composes cleanly with mod-to-mod imports: when a consumer does
`import '@kerbalism/status'`, the side-effect adopts kerbalism's
stylesheet. No coordination at the call site.

**Dragonglass-internal theme** — top-level `.css` files in
`Dragonglass_Hud/UI/` are auto-`<link>`ed into the synthesized shell.
The runtime build emits one consolidated `runtime.css` here covering
theme tokens and stock's compiled component styles. **Mod CSS is not
auto-linked**, by design — keeps mod stylesheets scoped via
`adoptedStyleSheets`, avoids `:root`/`body` cascade collisions, gives
mods deterministic ordering.

## Synthesized shell

There is no static `index.html` on disk. The sidecar synthesizes the
shell at request time:

```html
<!doctype html>
<html lang="en">
<head>
  <meta charset="UTF-8">
  <title>Dragonglass</title>
  <link rel="stylesheet" href="/Dragonglass_Hud/runtime.css">
  <script type="importmap">{ /* runtime + discovered mods */ }</script>
</head>
<body>
  <div id="app"></div>
  <script type="module">import "@dragonglass/stock";</script>
</body>
</html>
```

The shell provides a `<div id="app">` mount target; entries that need a
different target create their own DOM. The entry specifier comes from
the sidecar's `--entry=<spec>` CLI flag (set by the C# plugin).

## Minimal mod skeleton

```
GameData/SampleMod/UI/
├── index.js          // entry — gets @samplemod
├── styles.css
└── components/
    └── widget.svelte
```

```js
// index.js
import { mount } from 'svelte';                                    // shared with stock
import { Navball } from '@dragonglass/instruments';                // shared component
import { kspStore } from '@dragonglass/telemetry/svelte';          // shared store
import sheet from './styles.css' with { type: 'css' };
import Widget from './components/widget.svelte';

document.adoptedStyleSheets = [...document.adoptedStyleSheets, sheet];

// Mount somewhere — in stock's tree (via setContext bridge), in your
// own portal, or as a standalone page reachable via kspcli dg_open.
```

To exercise the mod once installed, attach the CEF devtools at
`http://localhost:9229` and run `await import('@samplemod')` in the
console.

## Limitations (v1)

- **No hot reload.** New mods added during a KSP session don't appear
  until the sidecar restarts (which happens on the next KSP launch).
- **Discovery is convention-only.** Any `<dir>/UI/` registers, no
  manifest required. If a non-Dragonglass mod ships an unrelated `UI/`
  directory, it will still get an import-map entry — harmless but
  visible in devtools.
- **Mods that bundle their own Svelte run a separate runtime.** The
  shared runtime only covers code paths that import via the import
  map. Mods that pre-bundle Svelte get their own Svelte universe and
  can't compose Svelte components with stock's tree.

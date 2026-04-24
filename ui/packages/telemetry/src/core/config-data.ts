/**
 * Raw contents of the plugin's config.json. The plugin dumps the
 * file verbatim into the `config` topic's payload without parsing,
 * so the shape is entirely up to the UI — each app declares its own
 * interface over the top-level keys it cares about. Stock reads
 * `editor` and `paw` booleans; other UIs can use any JSON-valued
 * shape.
 *
 * An empty `{}` is guaranteed if the file is missing or unreadable.
 */
export type ConfigData = Record<string, unknown>;

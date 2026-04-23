// Pretty-printing helpers for PartModule class names.
//
// KSP's moduleName comes straight off the C# type (`ModuleEngines`,
// `ModuleScienceExperiment`, ...). For the PAW header we want a terse
// all-caps label. This strips the `Module` prefix and inserts spaces
// at CamelCase boundaries.

/**
 * `"ModuleEnginesFX"` → `"ENGINES FX"`.
 * `"ModuleScienceExperiment"` → `"SCIENCE EXPERIMENT"`.
 * `"ModuleCommand"` → `"COMMAND"`.
 * Unknown / already short → uppercased as-is.
 */
export function prettyModuleName(moduleName: string): string {
  if (!moduleName) return '';
  let s = moduleName;
  if (s.startsWith('Module')) s = s.slice(6);
  // Insert a space before each capital that follows a lowercase or
  // precedes a lowercase (handles both "Sci|Exp" and "FX|Next" edges).
  s = s
    .replace(/([a-z])([A-Z])/g, '$1 $2')
    .replace(/([A-Z]+)([A-Z][a-z])/g, '$1 $2');
  return s.toUpperCase();
}

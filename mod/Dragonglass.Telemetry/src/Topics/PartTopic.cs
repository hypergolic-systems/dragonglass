// Per-part telemetry. One PartTopic instance exists while the UI has a
// PAW open for that part — see PartSubscriptionManager for the
// add / remove wiring.
//
// The component lives on the Part's own GameObject. Unity handles
// the lifetime for free: when KSP destroys the Part (stage-off,
// explosion, unload) the PartTopic goes with it — OnDisable fires,
// the topic unregisters, and no stale pointer is ever seen. Sibling
// access to the Part is a plain GetComponent<Part>() on the same
// GameObject; no dictionaries, no lookups by persistentId.
//
// The topic name carries the KSP `Part.persistentId`:
//   Name = "part/<persistentId>"
// which is how client code subscribes to a specific part. The name is
// derived in OnEnable from the sibling Part's persistentId, so there
// is no separate Setup step — AddComponent-and-go.
//
// Wire format (positional):
//   data: [persistentId, name, [screenX, screenY, visible],
//          [[resourceName, abbr, available, capacity], ...],
//          [module, ...]]
//
// Each `module` row is itself tagged by a one-character kind in its
// first element, then `moduleName`, then a per-kind typed tail.
// Typed kinds carry ONLY the typed payload — no generic events/fields
// arrays — so the wire doesn't waste bytes restating field names +
// labels the client already knows from the kind.
//
//   ['G', moduleName, events, fields]                — generic fallback
//   ['E', moduleName,
//         status, thrustLimit, currentThrust, maxThrust,
//         realIsp, [propellants]]                    — ModuleEngines(FX)
//       status      : "burning" | "idle" | "flameout" | "shutdown"
//       thrustLimit : float [0, 100] (ModuleEngines.thrustPercentage)
//       currentThrust : float kN (finalThrust)
//       maxThrust   : float kN (GetMaxThrust())
//       realIsp     : float seconds
//       propellants : [[name, displayName, ratio, currentAmount, totalAvailable], ...]
//   ['S', moduleName, sensorType, active, value, unit, statusText]
//       — ModuleEnviroSensor (Thermometer / Barometer / Gravimeter /
//         Accelerometer). We compute the reading ourselves from the
//         live vessel / part state; stock's `readoutInfo` only
//         refreshes when the stock PAW is shown, which we suppress.
//       sensorType  : "temperature" | "pressure" | "gravity" | "acceleration"
//       active      : bool (ModuleEnviroSensor.sensorActive)
//       value       : float (kelvin / kPa / g / g — see unit)
//       unit        : string ("K", "kPa", "g", or "" when unavailable)
//       statusText  : string — "Active" when value is valid, or a
//                     reason like "Off" / "Out of Range" / "No Atm"
//                     / "Trace Atm" — matches stock's readoutInfo
//                     edge-case vocabulary.
//
//   ['X', moduleName, experimentTitle, state, rerunnable,
//         transmitValue, dataAmount]                   — ModuleScienceExperiment
//       state         : "stowed" | "ready" | "inoperable"
//       transmitValue : science points if transmitted now
//       dataAmount    : raw data in Mits
//       Client invokes 'DeployExperiment' / 'ReviewDataEvent' /
//       'ResetExperiment' via invokeEvent.
//
//   ['V', moduleName, state, flowRate, chargeRate, sunAOA,
//         retractable, isTracking]                     — ModuleDeployableSolarPanel
//       state   : "retracted" | "extending" | "extended" | "retracting" | "broken"
//       flowRate/chargeRate : EC/s current/max
//       sunAOA  : 0..1 (cos of sun angle through panel normal)
//       Client invokes 'Extend' / 'Retract' via invokeEvent.
//
//   ['R', moduleName, active, alwaysOn, efficiency, status,
//         [inputs], [outputs]]                         — ModuleGenerator
//       inputs/outputs : [[resourceName, rate], ...] (rate in units/s)
//       Fuel-cell-style generators respond to 'Activate' / 'Shutdown';
//       RTGs ignore (alwaysOn=true).
//
//   ['L', moduleName, on, r, g, b]                    — ModuleLight
//       r/g/b : Unity Color channels, 0..1.
//       Client invokes 'LightsOn' / 'LightsOff' / 'ToggleLights'.
//
//   ['C', moduleName, state, safeState, deployAltitude, minPressure]
//                                                      — ModuleParachute
//       state       : "stowed" | "active" | "semi" | "deployed" | "cut"
//       safeState   : "safe" | "risky" | "unsafe" | "none"
//       Client invokes 'Deploy' / 'CutParachute' / 'Repack' via invokeEvent.
//
// Typed modules drive their interactions via the generic
// `invokeEvent` / `setField` ops with hard-coded KSP member names
// (e.g. 'Activate' / 'Shutdown' / 'thrustPercentage'). The server
// re-verifies guiActive/active on every write, so stale clicks drop
// silently.
//
// events : [[eventName, guiName], ...]   — guiActive && active events
// fields : tagged-union rows, first element = kind:
//     ['L', id, label, value]                             — label
//     ['T', id, label, value, enabledText, disabledText]  — UI_Toggle
//     ['R', id, label, value, min, max, step]             — UI_FloatRange
//     ['N', id, label, value, min, max, incL, incS, incSl, unit] — UI_FloatEdit
//     ['O', id, label, selectedIndex, displays[]]         — UI_ChooseOption
//     ['P', id, label, value, min, max]                   — UI_ProgressBar
//
// Ops (client → server):
//   {op:"invokeEvent", args:[moduleIdx, eventName]}
//     Invokes the named event on part.Modules[moduleIdx].
//   {op:"setField", args:[moduleIdx, fieldName, value]}
//     Writes a new value to the field. `value` type depends on the
//     field's kind:
//       toggle       → bool
//       slider/numeric → number (cast to the field's declared type)
//       option       → number (index into the option display list)
//
// Dead-zoning: emit when screen position moves > 0.5 px, any resource
// amount shifts by > 0.5% of capacity, or any module's events/fields
// list changes (structural diff).

using System.Collections.Generic;
using System.Text;
using Dragonglass.Telemetry.Util;
using UnityEngine;

namespace Dragonglass.Telemetry.Topics
{
    public sealed class PartTopic : Topic
    {
        private const string LogPrefix = "[Dragonglass/Telemetry] ";

        private const float ScreenEpsilonPx = 0.5f;
        private const double ResourceFractionEpsilon = 0.005;  // 0.5% of capacity

        private struct ResourceFrame
        {
            public string ResourceName;
            public string Abbr;
            public double Available;
            public double Capacity;
        }

        private struct EventFrame
        {
            public string EventName;
            public string GuiName;
        }

        // A KSPField row — polymorphic by Kind. Unused fields for a
        // given kind are left at their default (null / 0) and ignored
        // by WriteData. Using one struct rather than class hierarchy
        // keeps the sample loop allocation-free: the List<FieldFrame>
        // reuses its backing array across frames.
        private struct FieldFrame
        {
            public char Kind;            // L/T/R/N/O/P
            public string Id;
            public string GuiName;

            public string ValueStr;      // label
            public bool ValueBool;       // toggle
            public double ValueNum;      // slider / numeric / progress
            public int ValueIdx;         // option (-1 if value isn't in options)

            public string EnabledText;   // toggle
            public string DisabledText;  // toggle
            public double Min;           // slider / numeric / progress
            public double Max;
            public double Step;          // slider
            public double IncLarge;      // numeric
            public double IncSmall;
            public double IncSlide;
            public string Unit;          // numeric
            public List<string> Display; // option
        }

        private struct EnginePropellantFrame
        {
            public string Name;
            public string DisplayName;
            public float Ratio;
            public double CurrentAmount;
            public double TotalAvailable;
        }

        private struct ModuleFrame
        {
            public char Kind;            // 'G' generic, 'E' engines, 'S' sensor
            public string ModuleName;
            public List<EventFrame> Events;
            public List<FieldFrame> Fields;

            // Engine-specific — valid only when Kind == 'E'.
            public string EngineStatus;
            public float EngineThrustLimit;
            public float EngineCurrentThrust;
            public float EngineMaxThrust;
            public float EngineRealIsp;
            public List<EnginePropellantFrame> EnginePropellants;

            // Sensor-specific — valid only when Kind == 'S'.
            public string SensorType;     // "temperature" / "pressure" / "gravity" / "acceleration"
            public bool SensorActive;
            public double SensorValue;
            public string SensorUnit;
            public string SensorStatusText;

            // Science experiment — valid only when Kind == 'X'.
            public string ScienceExperimentTitle;
            public string ScienceState;   // "stowed" / "ready" / "inoperable"
            public bool ScienceRerunnable;
            public float ScienceTransmitValue;  // science points if transmitted
            public float ScienceDataAmount;     // raw data in Mits

            // Solar panel — valid only when Kind == 'V'.
            public string SolarState;     // "retracted" / "extending" / "extended" / "retracting" / "broken"
            public float SolarFlowRate;   // EC/s currently flowing
            public float SolarChargeRate; // max EC/s configured
            public float SolarSunAOA;     // 0..1, cosine of sun angle
            public bool SolarRetractable;
            public bool SolarIsTracking;

            // Generator — valid only when Kind == 'R'.
            public bool GeneratorActive;
            public bool GeneratorAlwaysOn;
            public float GeneratorEfficiency;
            public string GeneratorStatus;       // short stock string ("Nominal", etc.)
            public List<GeneratorResourceFrame> GeneratorInputs;
            public List<GeneratorResourceFrame> GeneratorOutputs;

            // Light — valid only when Kind == 'L'.
            public bool LightOn;
            public float LightR;
            public float LightG;
            public float LightB;

            // Parachute — valid only when Kind == 'C'.
            public string ChuteState;         // "stowed" / "active" / "semi" / "deployed" / "cut"
            public string ChuteSafeState;     // "safe" / "risky" / "unsafe" / "none"
            public float ChuteDeployAltitude;
            public float ChuteMinPressure;
        }

        private struct GeneratorResourceFrame
        {
            public string Name;
            public double Rate;
        }

        private Part _part;
        private string _name = "part/unknown";
        private string _persistentIdStr = "";

        private string _partTitle = "";
        private float _screenX;
        private float _screenY;
        private bool _visible;
        private readonly List<ResourceFrame> _resources = new List<ResourceFrame>();
        private readonly List<ResourceFrame> _scratch = new List<ResourceFrame>();
        private readonly List<ModuleFrame> _modules = new List<ModuleFrame>();
        private readonly List<ModuleFrame> _modulesScratch = new List<ModuleFrame>();

        public override string Name { get { return _name; } }

        protected override void OnEnable()
        {
            // Sibling Part on the same GameObject — AddComponent<PartTopic>
            // was called against a Part's GameObject, so GetComponent<Part>
            // is the Part we sample.
            _part = GetComponent<Part>();
            if (_part != null)
            {
                _persistentIdStr = _part.persistentId.ToString();
                _name = "part/" + _persistentIdStr;
            }
            else
            {
                Debug.LogWarning(LogPrefix + "PartTopic added to a GameObject without a Part component; nothing to sample");
            }
            base.OnEnable();
            // Initial sample so the first broadcaster flush carries
            // real data. Without this the first frame could ship
            // while _partTitle is still "" and _resources still empty
            // — Unity's first Update call on a newly-added component
            // doesn't happen until the next frame, so OnEnable running
            // + a broadcaster tick on the same frame would otherwise
            // emit a placeholder.
            SampleFrame();
        }

        private void Update()
        {
            SampleFrame();
        }

        private void SampleFrame()
        {
            if (_part == null) return;

            bool changed = false;

            // Part title — localized. Stable over the part's lifetime
            // but cheap to diff each frame.
            string title = _part.partInfo != null ? _part.partInfo.title : _part.name;
            if (title == null) title = "";
            if (title != _partTitle) { _partTitle = title; changed = true; }

            // Screen position. CEF-viewport pixels, top-left origin —
            // matches the HUD's mouse-forwarding frame.
            Camera cam = FlightCamera.fetch != null ? FlightCamera.fetch.mainCamera : null;
            if (cam != null && _part.transform != null)
            {
                Vector3 screen = cam.WorldToScreenPoint(_part.transform.position);
                float nx = screen.x;
                float ny = Screen.height - screen.y;
                bool nv = screen.z > 0f;
                if (Mathf.Abs(nx - _screenX) > ScreenEpsilonPx) { _screenX = nx; changed = true; }
                if (Mathf.Abs(ny - _screenY) > ScreenEpsilonPx) { _screenY = ny; changed = true; }
                if (nv != _visible) { _visible = nv; changed = true; }
            }

            // Resources. Sample into scratch, compare positionally
            // against the last published list.
            _scratch.Clear();
            PartResourceList rl = _part.Resources;
            if (rl != null)
            {
                for (int i = 0; i < rl.Count; i++)
                {
                    PartResource r = rl[i];
                    if (r == null || r.info == null) continue;
                    _scratch.Add(new ResourceFrame
                    {
                        ResourceName = r.info.name,
                        Abbr = r.info.abbreviation,
                        Available = r.amount,
                        Capacity = r.maxAmount,
                    });
                }
            }
            if (ResourcesChanged(_resources, _scratch))
            {
                _resources.Clear();
                for (int i = 0; i < _scratch.Count; i++) _resources.Add(_scratch[i]);
                changed = true;
            }

            // PartModules: events (guiActive KSPEvents) + labels (guiActive
            // KSPFields with no interactive UI_Control). Interactive
            // fields — sliders, toggles, dropdowns — are intentionally
            // skipped here; they belong to a later tier that ships
            // typed widgets instead of text rows.
            SampleModules(_modulesScratch);
            if (ModulesChanged(_modules, _modulesScratch))
            {
                CopyModules(_modulesScratch, _modules);
                changed = true;
            }

            if (changed) MarkDirty();
        }

        private void SampleModules(List<ModuleFrame> dest)
        {
            dest.Clear();
            PartModuleList mods = _part.Modules;
            if (mods == null) return;
            for (int i = 0; i < mods.Count; i++)
            {
                PartModule mod = mods[i];
                if (mod == null) continue;
                if (!mod.isEnabled) continue;

                // Typed modules short-circuit generic collection — no
                // events/fields arrays to scan, no wire bytes spent
                // restating info the client already knows from the
                // kind. Bespoke renderer drives interaction by hard-
                // coded KSP member names.
                if (mod is ModuleEngines eng)
                {
                    dest.Add(BuildEngineModuleFrame(mod.ClassName, eng));
                    continue;
                }
                if (mod is ModuleEnviroSensor sensor)
                {
                    dest.Add(BuildSensorModuleFrame(mod.ClassName, sensor));
                    continue;
                }
                if (mod is ModuleScienceExperiment exp)
                {
                    dest.Add(BuildScienceModuleFrame(mod.ClassName, exp));
                    continue;
                }
                if (mod is ModuleDeployableSolarPanel panel)
                {
                    dest.Add(BuildSolarPanelModuleFrame(mod.ClassName, panel));
                    continue;
                }
                if (mod is ModuleGenerator gen)
                {
                    dest.Add(BuildGeneratorModuleFrame(mod.ClassName, gen));
                    continue;
                }
                if (mod is ModuleLight light)
                {
                    dest.Add(BuildLightModuleFrame(mod.ClassName, light));
                    continue;
                }
                if (mod is ModuleParachute chute)
                {
                    dest.Add(BuildParachuteModuleFrame(mod.ClassName, chute));
                    continue;
                }

                // Generic fallback path — scan events + fields.
                List<EventFrame> events = null;
                BaseEventList evs = mod.Events;
                if (evs != null)
                {
                    for (int j = 0; j < evs.Count; j++)
                    {
                        BaseEvent ev = evs.GetByIndex(j);
                        if (ev == null) continue;
                        if (!ev.active || !ev.guiActive) continue;
                        if (ev.advancedTweakable && !GameSettings.ADVANCED_TWEAKABLES) continue;
                        if (events == null) events = new List<EventFrame>();
                        events.Add(new EventFrame
                        {
                            EventName = ev.name,
                            GuiName = ev.guiName,
                        });
                    }
                }

                List<FieldFrame> fieldRows = null;
                BaseFieldList<BaseField, KSPField> fields = mod.Fields;
                if (fields != null)
                {
                    for (int j = 0; j < fields.Count; j++)
                    {
                        BaseField f = fields[j];
                        if (f == null) continue;
                        if (!f.guiActive) continue;
                        if (f.advancedTweakable && !GameSettings.ADVANCED_TWEAKABLES) continue;
                        if (fieldRows == null) fieldRows = new List<FieldFrame>();
                        fieldRows.Add(BuildFieldFrame(f, mod));
                    }
                }

                // Elide modules with nothing to show — most stock
                // parts have structural modules with no UI surface at
                // all, and emitting them wastes bytes + clutters the
                // PAW.
                if ((events == null || events.Count == 0)
                    && (fieldRows == null || fieldRows.Count == 0))
                    continue;

                dest.Add(new ModuleFrame
                {
                    Kind = 'G',
                    ModuleName = mod.ClassName,
                    Events = events ?? EmptyEvents,
                    Fields = fieldRows ?? EmptyFields,
                });
            }
        }

        private static readonly List<EnginePropellantFrame> EmptyPropellants =
            new List<EnginePropellantFrame>(0);

        // Serialise a generator's in/out resource list as a flat
        // [[name, rate], ...] array. Used for both inputs and outputs
        // in the 'R' wire tail.
        private static void WriteGeneratorResources(
            StringBuilder sb, List<GeneratorResourceFrame> rows)
        {
            sb.Append('[');
            if (rows != null)
            {
                for (int i = 0; i < rows.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    GeneratorResourceFrame r = rows[i];
                    sb.Append('[');
                    Json.WriteString(sb, r.Name ?? "");
                    sb.Append(',');
                    Json.WriteDouble(sb, r.Rate);
                    sb.Append(']');
                }
            }
            sb.Append(']');
        }

        // Compute the live environmental-sensor reading directly from
        // vessel/part state. We can't reuse stock's `readoutInfo`:
        // ModuleEnviroSensor.FixedUpdate only populates it while the
        // stock PAW is showing the part (see FixedUpdate → the
        // UIPartActionController.Instance.ItemListContains guard),
        // and we've suppressed that PAW entirely. So the bespoke
        // payload owns the math.
        private ModuleFrame BuildSensorModuleFrame(
            string moduleName, ModuleEnviroSensor sensor)
        {
            string sensorTypeStr;
            double value = 0;
            string unit = "";
            string status;

            if (!sensor.sensorActive)
            {
                status = "Off";
                sensorTypeStr = SensorTypeToWire(sensor.sensorType);
            }
            else
            {
                sensorTypeStr = SensorTypeToWire(sensor.sensorType);
                status = "Active";
                switch (sensor.sensorType)
                {
                    case ModuleEnviroSensor.SensorType.TEMP:
                        value = _part != null ? _part.temperature : 0.0;
                        unit = "K";
                        break;
                    case ModuleEnviroSensor.SensorType.ACC:
                        // vessel.geeForce is unitless multiples of g.
                        value = sensor.vessel != null ? sensor.vessel.geeForce : 0.0;
                        unit = "g";
                        break;
                    case ModuleEnviroSensor.SensorType.GRAV:
                        // Stock gates gravimeter on altitude ≤ 3×R so
                        // far-away readings flatten to the asymptote.
                        // Mirror that gate so "Out of Range" shows up
                        // at the same moments the stock PAW reports it.
                        if (sensor.vessel != null
                            && sensor.vessel.orbit != null
                            && sensor.vessel.orbit.referenceBody != null
                            && sensor.vessel.orbit.altitude
                                <= sensor.vessel.orbit.referenceBody.Radius * 3.0)
                        {
                            value = FlightGlobals
                                .getGeeForceAtPosition(sensor.transform.position)
                                .magnitude;
                            unit = "m/s²";
                        }
                        else
                        {
                            status = "Out of Range";
                        }
                        break;
                    case ModuleEnviroSensor.SensorType.PRES:
                        if (sensor.vessel != null)
                        {
                            double p = sensor.vessel.staticPressurekPa;
                            if (p > 0.0001)
                            {
                                value = p;
                                unit = "kPa";
                            }
                            else if (p > 0.0)
                            {
                                status = "Trace Atm";
                            }
                            else
                            {
                                status = "No Atm";
                            }
                        }
                        break;
                }
            }

            return new ModuleFrame
            {
                Kind = 'S',
                ModuleName = moduleName,
                Events = EmptyEvents,
                Fields = EmptyFields,
                SensorType = sensorTypeStr,
                SensorActive = sensor.sensorActive,
                SensorValue = value,
                SensorUnit = unit,
                SensorStatusText = status,
            };
        }

        private static string SensorTypeToWire(ModuleEnviroSensor.SensorType t)
        {
            switch (t)
            {
                case ModuleEnviroSensor.SensorType.TEMP: return "temperature";
                case ModuleEnviroSensor.SensorType.PRES: return "pressure";
                case ModuleEnviroSensor.SensorType.GRAV: return "gravity";
                case ModuleEnviroSensor.SensorType.ACC:  return "acceleration";
                default: return "unknown";
            }
        }

        // Science experiment: state machine stowed → ready → inoperable.
        // Stock stores the collected data as a private `experimentData`
        // ScienceData; we read it via the IScienceDataContainer.GetData()
        // return path so we don't need reflection. Transmit value is
        // baseTransmitValue × transmitBonus — the science points the
        // player would bank if they hit Transmit right now.
        private static ModuleFrame BuildScienceModuleFrame(
            string moduleName, ModuleScienceExperiment exp)
        {
            string title = exp.experiment != null
                ? exp.experiment.experimentTitle
                : (exp.experimentID ?? moduleName);

            string state = exp.Inoperable ? "inoperable"
                : (exp.Deployed ? "ready" : "stowed");

            float transmitValue = 0f;
            float dataAmount = 0f;
            ScienceData[] data = exp.GetData();
            if (data != null && data.Length > 0 && data[0] != null)
            {
                ScienceData d = data[0];
                transmitValue = d.baseTransmitValue * d.transmitBonus;
                dataAmount = d.dataAmount;
            }

            return new ModuleFrame
            {
                Kind = 'X',
                ModuleName = moduleName,
                Events = EmptyEvents,
                Fields = EmptyFields,
                ScienceExperimentTitle = title ?? "",
                ScienceState = state,
                ScienceRerunnable = exp.rerunnable,
                ScienceTransmitValue = transmitValue,
                ScienceDataAmount = dataAmount,
            };
        }

        // Solar panel: wraps `ModuleDeployableSolarPanel`. Deploy state
        // maps the DeployState enum to lowercase wire strings; flow
        // rate is the current EC/s stock is producing.
        private static ModuleFrame BuildSolarPanelModuleFrame(
            string moduleName, ModuleDeployableSolarPanel panel)
        {
            return new ModuleFrame
            {
                Kind = 'V',
                ModuleName = moduleName,
                Events = EmptyEvents,
                Fields = EmptyFields,
                SolarState = DeployStateToWire(panel.deployState),
                SolarFlowRate = panel.flowRate,
                SolarChargeRate = panel.chargeRate,
                SolarSunAOA = panel.sunAOA,
                SolarRetractable = panel.retractable,
                SolarIsTracking = panel.isTracking,
            };
        }

        private static string DeployStateToWire(ModuleDeployablePart.DeployState s)
        {
            switch (s)
            {
                case ModuleDeployablePart.DeployState.RETRACTED:  return "retracted";
                case ModuleDeployablePart.DeployState.EXTENDING:  return "extending";
                case ModuleDeployablePart.DeployState.EXTENDED:   return "extended";
                case ModuleDeployablePart.DeployState.RETRACTING: return "retracting";
                case ModuleDeployablePart.DeployState.BROKEN:     return "broken";
                default: return "retracted";
            }
        }

        // Generator: RTGs / fuel cells / resource converters at the
        // `ModuleGenerator` layer. RTGs are `isAlwaysActive`; fuel
        // cells honour generatorIsActive and expose Activate/Shutdown
        // events.
        private static ModuleFrame BuildGeneratorModuleFrame(
            string moduleName, ModuleGenerator gen)
        {
            List<GeneratorResourceFrame> ins = null;
            List<GeneratorResourceFrame> outs = null;
            if (gen.resHandler != null)
            {
                if (gen.resHandler.inputResources != null)
                {
                    ins = new List<GeneratorResourceFrame>(gen.resHandler.inputResources.Count);
                    for (int i = 0; i < gen.resHandler.inputResources.Count; i++)
                    {
                        ModuleResource r = gen.resHandler.inputResources[i];
                        if (r == null) continue;
                        ins.Add(new GeneratorResourceFrame { Name = r.name, Rate = r.rate });
                    }
                }
                if (gen.resHandler.outputResources != null)
                {
                    outs = new List<GeneratorResourceFrame>(gen.resHandler.outputResources.Count);
                    for (int i = 0; i < gen.resHandler.outputResources.Count; i++)
                    {
                        ModuleResource r = gen.resHandler.outputResources[i];
                        if (r == null) continue;
                        outs.Add(new GeneratorResourceFrame { Name = r.name, Rate = r.rate });
                    }
                }
            }
            return new ModuleFrame
            {
                Kind = 'R',
                ModuleName = moduleName,
                Events = EmptyEvents,
                Fields = EmptyFields,
                GeneratorActive = gen.generatorIsActive || gen.isAlwaysActive,
                GeneratorAlwaysOn = gen.isAlwaysActive,
                GeneratorEfficiency = gen.efficiency,
                GeneratorStatus = gen.status ?? "",
                GeneratorInputs = ins ?? EmptyGenResources,
                GeneratorOutputs = outs ?? EmptyGenResources,
            };
        }

        private static readonly List<GeneratorResourceFrame> EmptyGenResources =
            new List<GeneratorResourceFrame>(0);

        // Light: on/off + RGB tint. Unity's Color r/g/b are 0..1
        // floats; we ship them that way so the UI can paint a
        // matching swatch without needing to know KSP's config
        // conventions.
        private static ModuleFrame BuildLightModuleFrame(
            string moduleName, ModuleLight light)
        {
            return new ModuleFrame
            {
                Kind = 'L',
                ModuleName = moduleName,
                Events = EmptyEvents,
                Fields = EmptyFields,
                LightOn = light.isOn,
                LightR = light.lightR,
                LightG = light.lightG,
                LightB = light.lightB,
            };
        }

        // Parachute: the 5-state deployment ladder plus the
        // safe/risky/unsafe status stock shows in the PAW. Deploy
        // altitude is a user-tunable field (UI_FloatRange in stock);
        // for MVP we ship it as a read-only value and let the pilot
        // edit via stock ops in a future pass.
        private static ModuleFrame BuildParachuteModuleFrame(
            string moduleName, ModuleParachute chute)
        {
            return new ModuleFrame
            {
                Kind = 'C',
                ModuleName = moduleName,
                Events = EmptyEvents,
                Fields = EmptyFields,
                ChuteState = ChuteStateToWire(chute.deploymentState),
                ChuteSafeState = ChuteSafeStateToWire(chute.deploymentSafeState),
                ChuteDeployAltitude = chute.deployAltitude,
                ChuteMinPressure = chute.minAirPressureToOpen,
            };
        }

        private static string ChuteStateToWire(ModuleParachute.deploymentStates s)
        {
            switch (s)
            {
                case ModuleParachute.deploymentStates.STOWED:       return "stowed";
                case ModuleParachute.deploymentStates.ACTIVE:       return "active";
                case ModuleParachute.deploymentStates.SEMIDEPLOYED: return "semi";
                case ModuleParachute.deploymentStates.DEPLOYED:     return "deployed";
                case ModuleParachute.deploymentStates.CUT:          return "cut";
                default: return "stowed";
            }
        }

        private static string ChuteSafeStateToWire(ModuleParachute.deploymentSafeStates s)
        {
            switch (s)
            {
                case ModuleParachute.deploymentSafeStates.SAFE:   return "safe";
                case ModuleParachute.deploymentSafeStates.RISKY:  return "risky";
                case ModuleParachute.deploymentSafeStates.UNSAFE: return "unsafe";
                default: return "none";
            }
        }

        // Extract the typed engine readouts from a live ModuleEngines.
        // Status resolves the familiar 4-state pattern the UI already
        // uses in EngineTopic (shared vocabulary: burning / idle /
        // flameout / shutdown).
        private static ModuleFrame BuildEngineModuleFrame(
            string moduleName, ModuleEngines eng)
        {
            string status;
            if (!eng.EngineIgnited) status = "shutdown";
            else if (eng.flameout) status = "flameout";
            else if (eng.finalThrust > 0f) status = "burning";
            else status = "idle";

            List<EnginePropellantFrame> props = null;
            if (eng.propellants != null && eng.propellants.Count > 0)
            {
                props = new List<EnginePropellantFrame>(eng.propellants.Count);
                for (int i = 0; i < eng.propellants.Count; i++)
                {
                    Propellant p = eng.propellants[i];
                    if (p == null) continue;
                    props.Add(new EnginePropellantFrame
                    {
                        Name = p.name,
                        DisplayName = string.IsNullOrEmpty(p.displayName) ? p.name : p.displayName,
                        Ratio = p.ratio,
                        CurrentAmount = p.currentAmount,
                        TotalAvailable = p.actualTotalAvailable,
                    });
                }
            }

            return new ModuleFrame
            {
                Kind = 'E',
                ModuleName = moduleName,
                // Typed modules don't carry generic events/fields.
                Events = EmptyEvents,
                Fields = EmptyFields,
                EngineStatus = status,
                EngineThrustLimit = eng.thrustPercentage,
                EngineCurrentThrust = eng.finalThrust,
                EngineMaxThrust = eng.GetMaxThrust(),
                EngineRealIsp = eng.realIsp,
                EnginePropellants = props ?? EmptyPropellants,
            };
        }

        private static readonly List<EventFrame> EmptyEvents = new List<EventFrame>(0);
        private static readonly List<FieldFrame> EmptyFields = new List<FieldFrame>(0);

        // Dispatch a single field into a typed row. `host` is the
        // PartModule that owns the field — BaseField.GetValue wants
        // it to resolve through `FieldInfo.GetValue`.
        private static FieldFrame BuildFieldFrame(BaseField f, PartModule host)
        {
            UI_Control ctrl = f.uiControlFlight;

            if (ctrl is UI_Toggle tog)
            {
                bool v = false;
                try { v = (bool)f.GetValue(host); } catch { }
                return new FieldFrame
                {
                    Kind = 'T', Id = f.name, GuiName = f.guiName,
                    ValueBool = v,
                    EnabledText = tog.displayEnabledText ?? "",
                    DisabledText = tog.displayDisabledText ?? "",
                };
            }
            if (ctrl is UI_FloatRange rng)
            {
                double v = 0;
                try { v = System.Convert.ToDouble(f.GetValue(host)); } catch { }
                return new FieldFrame
                {
                    Kind = 'R', Id = f.name, GuiName = f.guiName,
                    ValueNum = v,
                    Min = rng.minValue, Max = rng.maxValue, Step = rng.stepIncrement,
                };
            }
            if (ctrl is UI_FloatEdit fe)
            {
                double v = 0;
                try { v = System.Convert.ToDouble(f.GetValue(host)); } catch { }
                return new FieldFrame
                {
                    Kind = 'N', Id = f.name, GuiName = f.guiName,
                    ValueNum = v,
                    Min = fe.minValue, Max = fe.maxValue,
                    IncLarge = fe.incrementLarge,
                    IncSmall = fe.incrementSmall,
                    IncSlide = fe.incrementSlide,
                    Unit = fe.unit ?? "",
                };
            }
            if (ctrl is UI_ChooseOption opt)
            {
                string[] display = opt.display != null && opt.display.Length > 0
                    ? opt.display : opt.options;
                string[] values = opt.options;
                // Resolve the field's current value to an index into
                // `values`; fall back to -1 if it's not present.
                int selectedIndex = -1;
                string currentStr = null;
                try
                {
                    object raw = f.GetValue(host);
                    currentStr = raw != null ? raw.ToString() : null;
                }
                catch { }
                if (currentStr != null && values != null)
                {
                    for (int i = 0; i < values.Length; i++)
                    {
                        if (values[i] == currentStr) { selectedIndex = i; break; }
                    }
                }
                List<string> dlist = new List<string>(display != null ? display.Length : 0);
                if (display != null)
                {
                    for (int i = 0; i < display.Length; i++)
                        dlist.Add(display[i] ?? "");
                }
                return new FieldFrame
                {
                    Kind = 'O', Id = f.name, GuiName = f.guiName,
                    ValueIdx = selectedIndex,
                    Display = dlist,
                };
            }
            if (ctrl is UI_ProgressBar pb)
            {
                double v = 0;
                try { v = System.Convert.ToDouble(f.GetValue(host)); } catch { }
                return new FieldFrame
                {
                    Kind = 'P', Id = f.name, GuiName = f.guiName,
                    ValueNum = v,
                    Min = pb.minValue, Max = pb.maxValue,
                };
            }

            // UI_Label, null, UI_ColorPicker, UI_Vector2, UI_MinMaxRange,
            // UI_VariantSelector, etc. — render as a formatted label.
            string value = "";
            try { value = f.GetStringValue(host, gui: true) + (f.guiUnits ?? ""); }
            catch { value = ""; }
            return new FieldFrame
            {
                Kind = 'L', Id = f.name, GuiName = f.guiName,
                ValueStr = value ?? "",
            };
        }

        private static void CopyModules(List<ModuleFrame> src, List<ModuleFrame> dest)
        {
            dest.Clear();
            for (int i = 0; i < src.Count; i++) dest.Add(src[i]);
        }

        private static bool ModulesChanged(List<ModuleFrame> a, List<ModuleFrame> b)
        {
            if (a.Count != b.Count) return true;
            for (int i = 0; i < a.Count; i++)
            {
                ModuleFrame m1 = a[i];
                ModuleFrame m2 = b[i];
                if (m1.Kind != m2.Kind) return true;
                if (m1.ModuleName != m2.ModuleName) return true;
                if (EventsChanged(m1.Events, m2.Events)) return true;
                if (FieldsChanged(m1.Fields, m2.Fields)) return true;
                if (m1.Kind == 'E' && EngineExtrasChanged(m1, m2)) return true;
                if (m1.Kind == 'S' && SensorExtrasChanged(m1, m2)) return true;
                if (m1.Kind == 'X' && ScienceExtrasChanged(m1, m2)) return true;
                if (m1.Kind == 'V' && SolarExtrasChanged(m1, m2)) return true;
                if (m1.Kind == 'R' && GeneratorExtrasChanged(m1, m2)) return true;
                if (m1.Kind == 'L' && LightExtrasChanged(m1, m2)) return true;
                if (m1.Kind == 'C' && ChuteExtrasChanged(m1, m2)) return true;
            }
            return false;
        }

        // Dead-zoning tolerances for the new typed payloads. Tuned so
        // a resting panel / idle generator / stowed chute never emits
        // on jitter alone.
        private const float SolarFlowEpsilon = 0.005f;    // EC/s
        private const float SolarAngleEpsilon = 0.005f;   // cos(θ)
        private const float GenEfficiencyEpsilon = 0.005f;
        private const double GenRateEpsilon = 0.0005;
        private const float ScienceValueEpsilon = 0.01f;  // science points
        private const float ScienceDataEpsilon = 0.01f;   // mits
        private const float ChuteAltEpsilon = 1f;         // meters

        private static bool ScienceExtrasChanged(ModuleFrame a, ModuleFrame b)
        {
            if (a.ScienceExperimentTitle != b.ScienceExperimentTitle) return true;
            if (a.ScienceState != b.ScienceState) return true;
            if (a.ScienceRerunnable != b.ScienceRerunnable) return true;
            if (Mathf.Abs(a.ScienceTransmitValue - b.ScienceTransmitValue) > ScienceValueEpsilon) return true;
            if (Mathf.Abs(a.ScienceDataAmount - b.ScienceDataAmount) > ScienceDataEpsilon) return true;
            return false;
        }

        private static bool SolarExtrasChanged(ModuleFrame a, ModuleFrame b)
        {
            if (a.SolarState != b.SolarState) return true;
            if (a.SolarRetractable != b.SolarRetractable) return true;
            if (a.SolarIsTracking != b.SolarIsTracking) return true;
            if (Mathf.Abs(a.SolarFlowRate - b.SolarFlowRate) > SolarFlowEpsilon) return true;
            if (Mathf.Abs(a.SolarChargeRate - b.SolarChargeRate) > SolarFlowEpsilon) return true;
            if (Mathf.Abs(a.SolarSunAOA - b.SolarSunAOA) > SolarAngleEpsilon) return true;
            return false;
        }

        private static bool GeneratorExtrasChanged(ModuleFrame a, ModuleFrame b)
        {
            if (a.GeneratorActive != b.GeneratorActive) return true;
            if (a.GeneratorAlwaysOn != b.GeneratorAlwaysOn) return true;
            if (a.GeneratorStatus != b.GeneratorStatus) return true;
            if (Mathf.Abs(a.GeneratorEfficiency - b.GeneratorEfficiency) > GenEfficiencyEpsilon) return true;
            if (GenResourcesChanged(a.GeneratorInputs, b.GeneratorInputs)) return true;
            if (GenResourcesChanged(a.GeneratorOutputs, b.GeneratorOutputs)) return true;
            return false;
        }

        private static bool GenResourcesChanged(List<GeneratorResourceFrame> a, List<GeneratorResourceFrame> b)
        {
            int na = a != null ? a.Count : 0;
            int nb = b != null ? b.Count : 0;
            if (na != nb) return true;
            for (int i = 0; i < na; i++)
            {
                if (a[i].Name != b[i].Name) return true;
                if (System.Math.Abs(a[i].Rate - b[i].Rate) > GenRateEpsilon) return true;
            }
            return false;
        }

        private static bool LightExtrasChanged(ModuleFrame a, ModuleFrame b)
        {
            if (a.LightOn != b.LightOn) return true;
            if (a.LightR != b.LightR || a.LightG != b.LightG || a.LightB != b.LightB) return true;
            return false;
        }

        private static bool ChuteExtrasChanged(ModuleFrame a, ModuleFrame b)
        {
            if (a.ChuteState != b.ChuteState) return true;
            if (a.ChuteSafeState != b.ChuteSafeState) return true;
            if (Mathf.Abs(a.ChuteDeployAltitude - b.ChuteDeployAltitude) > ChuteAltEpsilon) return true;
            if (a.ChuteMinPressure != b.ChuteMinPressure) return true;
            return false;
        }

        private const double SensorValueEpsilon = 0.01;

        private static bool SensorExtrasChanged(ModuleFrame a, ModuleFrame b)
        {
            if (a.SensorType != b.SensorType) return true;
            if (a.SensorActive != b.SensorActive) return true;
            if (a.SensorUnit != b.SensorUnit) return true;
            if (a.SensorStatusText != b.SensorStatusText) return true;
            if (System.Math.Abs(a.SensorValue - b.SensorValue) > SensorValueEpsilon) return true;
            return false;
        }

        private const float EngineThrustEpsilon = 0.1f;   // kN
        private const float EngineIspEpsilon = 0.2f;      // s
        private const float EngineThrustLimitEpsilon = 0.5f;
        private const double EnginePropellantEpsilon = 0.5;

        private static bool EngineExtrasChanged(ModuleFrame a, ModuleFrame b)
        {
            if (a.EngineStatus != b.EngineStatus) return true;
            if (System.Math.Abs(a.EngineThrustLimit - b.EngineThrustLimit) > EngineThrustLimitEpsilon) return true;
            if (System.Math.Abs(a.EngineCurrentThrust - b.EngineCurrentThrust) > EngineThrustEpsilon) return true;
            if (System.Math.Abs(a.EngineMaxThrust - b.EngineMaxThrust) > EngineThrustEpsilon) return true;
            if (System.Math.Abs(a.EngineRealIsp - b.EngineRealIsp) > EngineIspEpsilon) return true;
            List<EnginePropellantFrame> pa = a.EnginePropellants;
            List<EnginePropellantFrame> pb = b.EnginePropellants;
            int na = pa != null ? pa.Count : 0;
            int nb = pb != null ? pb.Count : 0;
            if (na != nb) return true;
            for (int i = 0; i < na; i++)
            {
                EnginePropellantFrame x = pa[i];
                EnginePropellantFrame y = pb[i];
                if (x.Name != y.Name) return true;
                if (x.Ratio != y.Ratio) return true;
                if (System.Math.Abs(x.CurrentAmount - y.CurrentAmount) > EnginePropellantEpsilon) return true;
                if (System.Math.Abs(x.TotalAvailable - y.TotalAvailable) > EnginePropellantEpsilon) return true;
            }
            return false;
        }

        private static bool EventsChanged(List<EventFrame> a, List<EventFrame> b)
        {
            if (a.Count != b.Count) return true;
            for (int i = 0; i < a.Count; i++)
            {
                if (a[i].EventName != b[i].EventName) return true;
                if (a[i].GuiName != b[i].GuiName) return true;
            }
            return false;
        }

        private static bool FieldsChanged(List<FieldFrame> a, List<FieldFrame> b)
        {
            if (a.Count != b.Count) return true;
            for (int i = 0; i < a.Count; i++)
            {
                FieldFrame x = a[i];
                FieldFrame y = b[i];
                if (x.Kind != y.Kind) return true;
                if (x.Id != y.Id) return true;
                if (x.GuiName != y.GuiName) return true;
                switch (x.Kind)
                {
                    case 'L':
                        if (x.ValueStr != y.ValueStr) return true;
                        break;
                    case 'T':
                        if (x.ValueBool != y.ValueBool) return true;
                        if (x.EnabledText != y.EnabledText) return true;
                        if (x.DisabledText != y.DisabledText) return true;
                        break;
                    case 'R':
                        if (x.ValueNum != y.ValueNum) return true;
                        if (x.Min != y.Min || x.Max != y.Max || x.Step != y.Step) return true;
                        break;
                    case 'N':
                        if (x.ValueNum != y.ValueNum) return true;
                        if (x.Min != y.Min || x.Max != y.Max) return true;
                        if (x.IncLarge != y.IncLarge || x.IncSmall != y.IncSmall || x.IncSlide != y.IncSlide) return true;
                        if (x.Unit != y.Unit) return true;
                        break;
                    case 'O':
                        if (x.ValueIdx != y.ValueIdx) return true;
                        if (StringListChanged(x.Display, y.Display)) return true;
                        break;
                    case 'P':
                        if (x.ValueNum != y.ValueNum) return true;
                        if (x.Min != y.Min || x.Max != y.Max) return true;
                        break;
                }
            }
            return false;
        }

        private static bool StringListChanged(List<string> a, List<string> b)
        {
            if (a == null && b == null) return false;
            if (a == null || b == null) return true;
            if (a.Count != b.Count) return true;
            for (int i = 0; i < a.Count; i++) if (a[i] != b[i]) return true;
            return false;
        }

        private static bool ResourcesChanged(List<ResourceFrame> prev, List<ResourceFrame> next)
        {
            if (prev.Count != next.Count) return true;
            for (int i = 0; i < next.Count; i++)
            {
                ResourceFrame a = prev[i];
                ResourceFrame b = next[i];
                if (a.ResourceName != b.ResourceName) return true;
                if (a.Abbr != b.Abbr) return true;
                if (a.Capacity != b.Capacity) return true;
                double cap = b.Capacity > 0 ? b.Capacity : 1;
                if (System.Math.Abs(a.Available - b.Available) / cap > ResourceFractionEpsilon)
                    return true;
            }
            return false;
        }

        public override void WriteData(StringBuilder sb)
        {
            sb.Append('[');
            Json.WriteString(sb, _persistentIdStr);
            sb.Append(',');
            Json.WriteString(sb, _partTitle);
            sb.Append(',');
            sb.Append('[');
            Json.WriteFloat(sb, _screenX);
            sb.Append(',');
            Json.WriteFloat(sb, _screenY);
            sb.Append(',');
            Json.WriteBool(sb, _visible);
            sb.Append(']');
            sb.Append(',');
            sb.Append('[');
            for (int i = 0; i < _resources.Count; i++)
            {
                if (i > 0) sb.Append(',');
                ResourceFrame r = _resources[i];
                sb.Append('[');
                Json.WriteString(sb, r.ResourceName);
                sb.Append(',');
                Json.WriteString(sb, r.Abbr);
                sb.Append(',');
                Json.WriteDouble(sb, r.Available);
                sb.Append(',');
                Json.WriteDouble(sb, r.Capacity);
                sb.Append(']');
            }
            sb.Append(']');
            sb.Append(',');
            sb.Append('[');
            for (int i = 0; i < _modules.Count; i++)
            {
                if (i > 0) sb.Append(',');
                WriteModuleFrame(sb, _modules[i]);
            }
            sb.Append(']');
            sb.Append(']');
        }

        private static void WriteModuleFrame(StringBuilder sb, ModuleFrame m)
        {
            sb.Append('[');
            // Common prefix: [kind, moduleName, ...]
            Json.WriteString(sb, m.Kind.ToString());
            sb.Append(',');
            Json.WriteString(sb, m.ModuleName);

            switch (m.Kind)
            {
                case 'E':
                    sb.Append(',');
                    Json.WriteString(sb, m.EngineStatus ?? "shutdown");
                    sb.Append(',');
                    Json.WriteFloat(sb, m.EngineThrustLimit);
                    sb.Append(',');
                    Json.WriteFloat(sb, m.EngineCurrentThrust);
                    sb.Append(',');
                    Json.WriteFloat(sb, m.EngineMaxThrust);
                    sb.Append(',');
                    Json.WriteFloat(sb, m.EngineRealIsp);
                    sb.Append(',');
                    sb.Append('[');
                    List<EnginePropellantFrame> props = m.EnginePropellants;
                    if (props != null)
                    {
                        for (int i = 0; i < props.Count; i++)
                        {
                            if (i > 0) sb.Append(',');
                            EnginePropellantFrame p = props[i];
                            sb.Append('[');
                            Json.WriteString(sb, p.Name ?? "");
                            sb.Append(',');
                            Json.WriteString(sb, p.DisplayName ?? "");
                            sb.Append(',');
                            Json.WriteFloat(sb, p.Ratio);
                            sb.Append(',');
                            Json.WriteDouble(sb, p.CurrentAmount);
                            sb.Append(',');
                            Json.WriteDouble(sb, p.TotalAvailable);
                            sb.Append(']');
                        }
                    }
                    sb.Append(']');
                    break;

                case 'S':
                    sb.Append(',');
                    Json.WriteString(sb, m.SensorType ?? "unknown");
                    sb.Append(',');
                    Json.WriteBool(sb, m.SensorActive);
                    sb.Append(',');
                    Json.WriteDouble(sb, m.SensorValue);
                    sb.Append(',');
                    Json.WriteString(sb, m.SensorUnit ?? "");
                    sb.Append(',');
                    Json.WriteString(sb, m.SensorStatusText ?? "");
                    break;

                case 'X':
                    sb.Append(',');
                    Json.WriteString(sb, m.ScienceExperimentTitle ?? "");
                    sb.Append(',');
                    Json.WriteString(sb, m.ScienceState ?? "stowed");
                    sb.Append(',');
                    Json.WriteBool(sb, m.ScienceRerunnable);
                    sb.Append(',');
                    Json.WriteFloat(sb, m.ScienceTransmitValue);
                    sb.Append(',');
                    Json.WriteFloat(sb, m.ScienceDataAmount);
                    break;

                case 'V':
                    sb.Append(',');
                    Json.WriteString(sb, m.SolarState ?? "retracted");
                    sb.Append(',');
                    Json.WriteFloat(sb, m.SolarFlowRate);
                    sb.Append(',');
                    Json.WriteFloat(sb, m.SolarChargeRate);
                    sb.Append(',');
                    Json.WriteFloat(sb, m.SolarSunAOA);
                    sb.Append(',');
                    Json.WriteBool(sb, m.SolarRetractable);
                    sb.Append(',');
                    Json.WriteBool(sb, m.SolarIsTracking);
                    break;

                case 'R':
                    sb.Append(',');
                    Json.WriteBool(sb, m.GeneratorActive);
                    sb.Append(',');
                    Json.WriteBool(sb, m.GeneratorAlwaysOn);
                    sb.Append(',');
                    Json.WriteFloat(sb, m.GeneratorEfficiency);
                    sb.Append(',');
                    Json.WriteString(sb, m.GeneratorStatus ?? "");
                    sb.Append(',');
                    WriteGeneratorResources(sb, m.GeneratorInputs);
                    sb.Append(',');
                    WriteGeneratorResources(sb, m.GeneratorOutputs);
                    break;

                case 'L':
                    sb.Append(',');
                    Json.WriteBool(sb, m.LightOn);
                    sb.Append(',');
                    Json.WriteFloat(sb, m.LightR);
                    sb.Append(',');
                    Json.WriteFloat(sb, m.LightG);
                    sb.Append(',');
                    Json.WriteFloat(sb, m.LightB);
                    break;

                case 'C':
                    sb.Append(',');
                    Json.WriteString(sb, m.ChuteState ?? "stowed");
                    sb.Append(',');
                    Json.WriteString(sb, m.ChuteSafeState ?? "none");
                    sb.Append(',');
                    Json.WriteFloat(sb, m.ChuteDeployAltitude);
                    sb.Append(',');
                    Json.WriteFloat(sb, m.ChuteMinPressure);
                    break;

                case 'G':
                default:
                    // Generic modules carry events + fields arrays.
                    sb.Append(',');
                    sb.Append('[');
                    for (int j = 0; j < m.Events.Count; j++)
                    {
                        if (j > 0) sb.Append(',');
                        EventFrame ev = m.Events[j];
                        sb.Append('[');
                        Json.WriteString(sb, ev.EventName);
                        sb.Append(',');
                        Json.WriteString(sb, ev.GuiName);
                        sb.Append(']');
                    }
                    sb.Append(']');
                    sb.Append(',');
                    sb.Append('[');
                    for (int j = 0; j < m.Fields.Count; j++)
                    {
                        if (j > 0) sb.Append(',');
                        WriteFieldFrame(sb, m.Fields[j]);
                    }
                    sb.Append(']');
                    break;
            }

            sb.Append(']');
        }

        private static void WriteFieldFrame(StringBuilder sb, FieldFrame f)
        {
            sb.Append('[');
            // Kind + id + label are the common prefix.
            Json.WriteString(sb, f.Kind.ToString());
            sb.Append(',');
            Json.WriteString(sb, f.Id);
            sb.Append(',');
            Json.WriteString(sb, f.GuiName);
            sb.Append(',');
            switch (f.Kind)
            {
                case 'L':
                    Json.WriteString(sb, f.ValueStr ?? "");
                    break;
                case 'T':
                    Json.WriteBool(sb, f.ValueBool);
                    sb.Append(',');
                    Json.WriteString(sb, f.EnabledText ?? "");
                    sb.Append(',');
                    Json.WriteString(sb, f.DisabledText ?? "");
                    break;
                case 'R':
                    Json.WriteDouble(sb, f.ValueNum);
                    sb.Append(',');
                    Json.WriteDouble(sb, f.Min);
                    sb.Append(',');
                    Json.WriteDouble(sb, f.Max);
                    sb.Append(',');
                    Json.WriteDouble(sb, f.Step);
                    break;
                case 'N':
                    Json.WriteDouble(sb, f.ValueNum);
                    sb.Append(',');
                    Json.WriteDouble(sb, f.Min);
                    sb.Append(',');
                    Json.WriteDouble(sb, f.Max);
                    sb.Append(',');
                    Json.WriteDouble(sb, f.IncLarge);
                    sb.Append(',');
                    Json.WriteDouble(sb, f.IncSmall);
                    sb.Append(',');
                    Json.WriteDouble(sb, f.IncSlide);
                    sb.Append(',');
                    Json.WriteString(sb, f.Unit ?? "");
                    break;
                case 'O':
                    Json.WriteLong(sb, f.ValueIdx);
                    sb.Append(',');
                    sb.Append('[');
                    if (f.Display != null)
                    {
                        for (int i = 0; i < f.Display.Count; i++)
                        {
                            if (i > 0) sb.Append(',');
                            Json.WriteString(sb, f.Display[i] ?? "");
                        }
                    }
                    sb.Append(']');
                    break;
                case 'P':
                    Json.WriteDouble(sb, f.ValueNum);
                    sb.Append(',');
                    Json.WriteDouble(sb, f.Min);
                    sb.Append(',');
                    Json.WriteDouble(sb, f.Max);
                    break;
            }
            sb.Append(']');
        }

        public override void HandleOp(string op, List<object> args)
        {
            if (_part == null) return;
            switch (op)
            {
                case "invokeEvent":
                    // args: [moduleIndex:double, eventName:string]
                    if (args != null && args.Count >= 2
                        && args[0] is double d && args[1] is string eventName)
                    {
                        int moduleIdx = (int)d;
                        PartModuleList mods = _part.Modules;
                        if (mods == null) return;
                        if (moduleIdx < 0 || moduleIdx >= mods.Count) return;
                        PartModule mod = mods[moduleIdx];
                        if (mod == null || !mod.isEnabled) return;
                        BaseEvent ev = mod.Events != null ? mod.Events[eventName] : null;
                        if (ev == null) return;
                        if (!ev.active || !ev.guiActive) return;
                        // Invoke on the main thread — OpDispatcher.Drain
                        // runs there, so this is already safe.
                        ev.Invoke();
                        // Force a fresh frame soon so the UI sees any
                        // state flip (e.g. "Deploy" → "Retract" rename)
                        // without waiting for dead-zone exit.
                        MarkDirty();
                    }
                    break;
                case "setField":
                    // args: [moduleIndex:double, fieldName:string, value:any]
                    if (args != null && args.Count >= 3
                        && args[0] is double mi && args[1] is string fieldName)
                    {
                        int moduleIdx = (int)mi;
                        PartModuleList mods = _part.Modules;
                        if (mods == null) return;
                        if (moduleIdx < 0 || moduleIdx >= mods.Count) return;
                        PartModule mod = mods[moduleIdx];
                        if (mod == null || !mod.isEnabled) return;
                        BaseField f = mod.Fields != null ? mod.Fields[fieldName] : null;
                        if (f == null || !f.guiActive) return;
                        SetFieldFromWire(f, mod, args[2]);
                        MarkDirty();
                    }
                    break;
                default:
                    Debug.LogWarning(LogPrefix + "PartTopic: unknown op '" + op + "'");
                    break;
            }
        }

        // Write a wire-parsed value onto a BaseField. Value typing on
        // the wire is minimal (JSON bool / number / string); we cast
        // to the field's declared type here. After a successful write
        // we fire the field's `onFieldChanged` callback so any stock
        // side-effects (engine gimbal recompute, antenna rebuild,
        // etc.) run — matches UIPartActionFieldItem.SetFieldValue's
        // sequence.
        private static void SetFieldFromWire(BaseField f, PartModule host, object raw)
        {
            System.Type t = f.FieldInfo != null ? f.FieldInfo.FieldType : null;
            if (t == null) return;
            object oldValue = null;
            try { oldValue = f.GetValue(host); } catch { }
            object newValue = null;
            try
            {
                if (t == typeof(bool))
                {
                    newValue = raw is bool b ? b : System.Convert.ToBoolean(raw);
                }
                else if (t == typeof(float))
                {
                    newValue = System.Convert.ToSingle(raw);
                }
                else if (t == typeof(double))
                {
                    newValue = System.Convert.ToDouble(raw);
                }
                else if (t == typeof(int))
                {
                    // For UI_ChooseOption string-typed fields, raw is
                    // an index we dereference below; plain int fields
                    // take the value directly.
                    if (f.uiControlFlight is UI_ChooseOption _)
                    {
                        newValue = System.Convert.ToInt32(raw);
                    }
                    else newValue = System.Convert.ToInt32(raw);
                }
                else if (t == typeof(string))
                {
                    // Strings arrive either as raw strings or as a
                    // ChooseOption index that we map to the option
                    // value by looking it up on the UI_Control.
                    if (raw is string rs) newValue = rs;
                    else if (f.uiControlFlight is UI_ChooseOption opt
                        && raw is double dx)
                    {
                        int idx = (int)dx;
                        if (opt.options != null
                            && idx >= 0 && idx < opt.options.Length)
                        {
                            newValue = opt.options[idx];
                        }
                    }
                }
                else
                {
                    // Unknown type — best-effort toString.
                    newValue = raw != null ? raw.ToString() : null;
                }
            }
            catch (System.Exception e)
            {
                Debug.LogWarning(LogPrefix + "setField cast failed on " +
                                 f.name + ": " + e.Message);
                return;
            }
            if (newValue == null) return;
            if (object.Equals(oldValue, newValue)) return;

            f.SetValue(newValue, host);
            UI_Control ctrl = f.uiControlFlight;
            if (ctrl != null && ctrl.onFieldChanged != null)
            {
                try { ctrl.onFieldChanged(f, oldValue); }
                catch (System.Exception e)
                {
                    Debug.LogWarning(LogPrefix + "onFieldChanged threw on " +
                                     f.name + ": " + e.Message);
                }
            }
        }
    }
}

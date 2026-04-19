// Shared `FlightData` store — the first component to call
// `useFlightData()` lazily initialises the store and subscribes
// once to `FlightTopic`. Every subsequent caller gets the same
// reactive reference back, so N consumers share 1 subscription
// and 1 `$state` value. No per-caller allocation, no per-caller
// subscription churn on mount/unmount.
//
// `FlightData` is `readonly` on every field, so consumers can
// only read or fold into `$derived`. The store uses an internal
// mutable mirror (`MutableFlightData`) for its own writes and
// casts to the readonly public interface at the boundary.

import { Quaternion, Vector3 } from 'three';
import { getKsp } from './context';
import { FlightTopic } from '../core/topics';
import type { FlightData } from '../core/flight-data';

// Internal writable shape — assignments are the store's concern,
// not the consumer's. The readonly cast at the return type keeps
// consumers honest.
interface MutableFlightData {
  vesselId: string;
  altitudeAsl: number;
  altitudeRadar: number;
  surfaceVelocity: Vector3;
  orbitalVelocity: Vector3;
  throttle: number;
  sas: boolean;
  rcs: boolean;
  orientation: Quaternion;
  angularVelocity: Vector3;
  hasTarget: boolean;
  targetVelocity: Vector3;
  deltaVMission: number;
  currentThrust: number;
}

function defaults(): MutableFlightData {
  return {
    vesselId: '',
    altitudeAsl: 0,
    altitudeRadar: 0,
    surfaceVelocity: new Vector3(),
    orbitalVelocity: new Vector3(),
    throttle: 0,
    sas: false,
    rcs: false,
    orientation: new Quaternion(),
    angularVelocity: new Vector3(),
    hasTarget: false,
    targetVelocity: new Vector3(),
    deltaVMission: 0,
    currentThrust: 0,
  };
}

const store = $state<MutableFlightData>(defaults());
let subscribed = false;

/**
 * Return the shared reactive `FlightData` store. Reading fields on
 * it tracks Svelte dependencies; changes propagate whenever the
 * underlying topic emits a new frame. Must be called during
 * component initialization (needs Svelte context for `getKsp()`).
 */
export function useFlightData(): FlightData {
  if (!subscribed) {
    subscribed = true;
    const telemetry = getKsp();
    telemetry.subscribe(FlightTopic, (frame) => {
      // Plain scalars — Svelte's `$state` proxy intercepts each
      // assignment and fires fine-grained reactivity for the key.
      store.vesselId = frame.vesselId;
      store.altitudeAsl = frame.altitudeAsl;
      store.altitudeRadar = frame.altitudeRadar;
      store.throttle = frame.throttle;
      store.sas = frame.sas;
      store.rcs = frame.rcs;
      store.hasTarget = frame.hasTarget;
      store.deltaVMission = frame.deltaVMission;
      store.currentThrust = frame.currentThrust;
      // Nested class instances (Vector3 / Quaternion) — replace
      // the reference. Svelte doesn't deep-proxy class instances,
      // so copying into the existing instance wouldn't notify
      // dependents. Frames publish fresh instances per tick, so
      // this is a simple pointer swap.
      store.surfaceVelocity = frame.surfaceVelocity;
      store.orbitalVelocity = frame.orbitalVelocity;
      store.orientation = frame.orientation;
      store.angularVelocity = frame.angularVelocity;
      store.targetVelocity = frame.targetVelocity;
    });
  }
  return store as FlightData;
}

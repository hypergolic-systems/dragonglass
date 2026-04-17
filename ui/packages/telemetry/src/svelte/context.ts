import { getContext, setContext } from 'svelte';
import type { Ksp } from '../core/ksp';

const KEY = Symbol('telemetry');

export function setKsp(t: Ksp): void {
  setContext(KEY, t);
}

export function getKsp(): Ksp {
  return getContext<Ksp>(KEY);
}

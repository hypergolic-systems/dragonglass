<script lang="ts">
  // Velocity-driven orbital markers. The six classical navball markers
  // form an orthonormal frame derived from the orbital state:
  //
  //   prograde     = v̂                   direction of orbital velocity
  //   retrograde   = -v̂
  //   normal       = v̂ × r̂                perpendicular to orbital
  //                                       plane (angular momentum)
  //   anti-normal  = -normal
  //   radial-out   = normal × prograde    perpendicular to velocity,
  //                                       in orbital plane, pointing
  //                                       away from central body
  //   radial-in    = -radial-out
  //
  // (r̂ is planet-radial-up, which in our surface frame is always
  // sphere +Y.)
  //
  // For a circular orbit the radial-out direction coincides with r̂,
  // but for any orbit with a radial velocity component (elliptical,
  // ascent, descent, maneuvering) it does not — the radial markers
  // tilt with the orbit geometry, which is the intended navball
  // behaviour.
  //
  // Handedness note: the server-side surface frame is labelled
  // (+X=east, +Y=up, +Z=north), which is effectively *left-handed* —
  // cross(+X, +Y) in this frame gives -Z, not +Z. That's why
  // `normal` is computed as `cross(v̂, r̂)` using three.js's
  // right-handed cross; in our frame, that formula gives the physical
  // angular-momentum direction. Verified against equatorial-prograde
  // (normal = +north) and polar-prograde (normal = west).
  //
  // Degenerate case: if velocity is purely radial (ω × r-plane is
  // undefined), `normal` collapses and the radial markers can't be
  // resolved either — we hide those four and keep only
  // prograde/retrograde. Velocity near zero hides everything.
  //
  // Prograde/retrograde track whatever velocity reference KSP's
  // speed-display mode has selected — orbital, surface, or target-
  // relative. That keeps the marker consistent with the speed tape
  // and with the pilot's mental frame of reference. Normal and
  // radial markers are still orbital-derived; they only have a
  // meaningful interpretation in orbital mechanics and stock KSP
  // hides them outside orbit mode too.

  import { T, useTask } from '@threlte/core';
  import * as THREE from 'three';
  import { useFlightData } from '@dragonglass/telemetry/svelte';
  import { MARKER_KINDS, drawMarkerTexture } from './navball-textures';
  import type { MarkerKind } from './navball-textures';

  const flight = useFlightData();

  const RADIAL_UP = Object.freeze(new THREE.Vector3(0, 1, 0));
  const WORLD_UP = Object.freeze(new THREE.Vector3(0, 1, 0));
  const MIN_VEL = 1e-3;
  const MIN_NORMAL = 1e-3;

  type Marker = {
    kind: MarkerKind;
    texture: THREE.CanvasTexture;
    ref: THREE.Mesh | undefined;
    direction: THREE.Vector3;
  };

  const markers: Marker[] = MARKER_KINDS.map((kind) => {
    const texture = new THREE.CanvasTexture(drawMarkerTexture(kind));
    texture.colorSpace = THREE.SRGBColorSpace;
    texture.anisotropy = 8;
    return {
      kind,
      texture,
      ref: undefined,
      direction: new THREE.Vector3(),
    };
  });
  const byKind = new Map(markers.map((m) => [m.kind, m]));

  // Per-frame scratch — avoids allocating new Vector3 / Matrix4 each tick.
  const prograde = new THREE.Vector3();
  const retrograde = new THREE.Vector3();
  const normal = new THREE.Vector3();
  const antiNormal = new THREE.Vector3();
  const radialOut = new THREE.Vector3();
  const radialIn = new THREE.Vector3();
  const targetPrograde = new THREE.Vector3();
  const targetRetrograde = new THREE.Vector3();
  const basisUp = new THREE.Vector3();
  const basisRight = new THREE.Vector3();
  const basisProj = new THREE.Vector3();
  const basisMatrix = new THREE.Matrix4();

  function setVisible(kind: MarkerKind, dir: THREE.Vector3 | null) {
    const m = byKind.get(kind)!;
    if (!m.ref) return;
    if (dir === null) {
      m.ref.visible = false;
      return;
    }
    m.ref.visible = true;
    m.direction.copy(dir);

    // Position just above the sphere surface along the marker's radial direction.
    m.ref.position.copy(dir).multiplyScalar(1.005);

    // Orient the plane so its face is tangent to the sphere (local +Z
    // = outward direction). Tangent-up is world-up projected onto the
    // tangent plane, except at the poles where that projection is
    // zero and we pick a fallback.
    const alignedWithPole = Math.abs(dir.dot(WORLD_UP)) > 0.999;
    if (alignedWithPole) {
      basisUp.set(0, 0, -1);
    } else {
      basisProj.copy(dir).multiplyScalar(WORLD_UP.dot(dir));
      basisUp.copy(WORLD_UP).sub(basisProj).normalize();
    }
    basisRight.crossVectors(basisUp, dir).normalize();
    basisMatrix.makeBasis(basisRight, basisUp, dir);
    m.ref.quaternion.setFromRotationMatrix(basisMatrix);
  }

  useTask(() => {
    // Prograde/retrograde follow the active speed-display mode.
    // Target mode explicitly does not produce the orbital marker
    // pair here — target-prograde/retrograde below handles that
    // case on its own subframe.
    const progradeSource =
      flight.speedDisplayMode === 'orbit' ? flight.orbitalVelocity
      : flight.speedDisplayMode === 'surface' ? flight.surfaceVelocity
      : null;

    // Normal / radial markers always derive from orbital velocity —
    // they're orbital-mechanics concepts and stock KSP hides them
    // outside orbit mode too.
    const showNormalRadial = flight.speedDisplayMode === 'orbit';

    if (progradeSource === null) {
      setVisible('prograde', null);
      setVisible('retrograde', null);
    } else {
      prograde.copy(progradeSource);
      const speed = prograde.length();
      if (speed < MIN_VEL) {
        setVisible('prograde', null);
        setVisible('retrograde', null);
      } else {
        prograde.divideScalar(speed);
        retrograde.copy(prograde).negate();
        setVisible('prograde', prograde);
        setVisible('retrograde', retrograde);
      }
    }

    if (!showNormalRadial) {
      setVisible('normal', null);
      setVisible('anti-normal', null);
      setVisible('radial-out', null);
      setVisible('radial-in', null);
    } else {
      // Orbital-velocity basis for normal / radial. Independent of
      // the prograde-marker visibility decision above — in orbit
      // mode prograde uses the same source, but we recompute here
      // so surface/target modes would still work cleanly if this
      // branch were ever reused.
      normal.copy(flight.orbitalVelocity);
      const obtSpeed = normal.length();
      if (obtSpeed < MIN_VEL) {
        setVisible('normal', null);
        setVisible('anti-normal', null);
        setVisible('radial-out', null);
        setVisible('radial-in', null);
      } else {
        const vHat = normal.clone().divideScalar(obtSpeed);
        // normal = v̂ × r̂. Collapses if velocity is parallel to
        // radial (vertical ascent / descent); hide the normal-
        // radial subframe in that case.
        normal.crossVectors(vHat, RADIAL_UP);
        const normalMag = normal.length();
        if (normalMag < MIN_NORMAL) {
          setVisible('normal', null);
          setVisible('anti-normal', null);
          setVisible('radial-out', null);
          setVisible('radial-in', null);
        } else {
          normal.divideScalar(normalMag);
          antiNormal.copy(normal).negate();
          setVisible('normal', normal);
          setVisible('anti-normal', antiNormal);

          // radial-out = normal × v̂, already unit since both
          // arguments are unit and orthogonal.
          radialOut.crossVectors(normal, vHat);
          radialIn.copy(radialOut).negate();
          setVisible('radial-out', radialOut);
          setVisible('radial-in', radialIn);
        }
      }
    }

    // Target markers: prograde / retrograde relative to the targeted
    // vessel or body. Independent of the orbital-velocity subframe —
    // a landed vessel with a nearby orbiting target still gets
    // meaningful target markers.
    if (!flight.hasTarget) {
      setVisible('target-prograde', null);
      setVisible('target-retrograde', null);
    } else {
      targetPrograde.copy(flight.targetVelocity);
      const tSpeed = targetPrograde.length();
      if (tSpeed < MIN_VEL) {
        setVisible('target-prograde', null);
        setVisible('target-retrograde', null);
      } else {
        targetPrograde.divideScalar(tSpeed);
        targetRetrograde.copy(targetPrograde).negate();
        setVisible('target-prograde', targetPrograde);
        setVisible('target-retrograde', targetRetrograde);
      }
    }
  });
</script>

{#each markers as m (m.kind)}
  <T.Mesh bind:ref={m.ref}>
    <T.PlaneGeometry args={[0.22, 0.22]} />
    <T.MeshBasicMaterial map={m.texture} transparent depthWrite={false} />
  </T.Mesh>
{/each}

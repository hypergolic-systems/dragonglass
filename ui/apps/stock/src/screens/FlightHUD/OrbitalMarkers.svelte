<script lang="ts">
  import { T } from '@threlte/core';
  import * as THREE from 'three';
  import { MARKER_SPECS, drawMarkerTexture } from './navball-textures';

  const WORLD_UP = new THREE.Vector3(0, 1, 0);

  const markers = MARKER_SPECS.map((spec) => {
    const dir = new THREE.Vector3(spec.dir[0], spec.dir[1], spec.dir[2]).normalize();
    const position = dir.clone().multiplyScalar(1.005);

    const alignedWithPole = Math.abs(dir.dot(WORLD_UP)) > 0.999;
    const tangentUp = alignedWithPole
      ? new THREE.Vector3(0, 0, -1)
      : WORLD_UP.clone()
          .sub(dir.clone().multiplyScalar(WORLD_UP.dot(dir)))
          .normalize();
    const tangentRight = new THREE.Vector3()
      .crossVectors(tangentUp, dir)
      .normalize();
    const basis = new THREE.Matrix4().makeBasis(tangentRight, tangentUp, dir);
    const quaternion = new THREE.Quaternion().setFromRotationMatrix(basis);

    const texture = new THREE.CanvasTexture(drawMarkerTexture(spec.kind));
    texture.colorSpace = THREE.SRGBColorSpace;
    texture.anisotropy = 8;

    return {
      kind: spec.kind,
      position: [position.x, position.y, position.z] as [number, number, number],
      quaternion: [quaternion.x, quaternion.y, quaternion.z, quaternion.w] as [number, number, number, number],
      texture,
    };
  });
</script>

{#each markers as m (m.kind)}
  <T.Mesh position={m.position} quaternion={m.quaternion}>
    <T.PlaneGeometry args={[0.22, 0.22]} />
    <T.MeshBasicMaterial map={m.texture} transparent depthWrite={false} />
  </T.Mesh>
{/each}

<script lang="ts">
  import { T, useTask } from '@threlte/core';
  import * as THREE from 'three';
  import { drawNavballTexture } from './navball-textures';
  import OrbitalMarkers from './OrbitalMarkers.svelte';

  let { orientation }: { orientation: THREE.Quaternion } = $props();

  let groupRef: THREE.Group | undefined = $state();

  const texture = new THREE.CanvasTexture(drawNavballTexture());
  texture.colorSpace = THREE.SRGBColorSpace;
  texture.anisotropy = 8;
  texture.wrapS = THREE.RepeatWrapping;
  texture.wrapT = THREE.ClampToEdgeWrapping;

  const emissive = new THREE.Color('#0a1020');

  useTask(() => {
    if (!groupRef) return;
    groupRef.quaternion.copy(orientation).invert();
  });
</script>

<T.Group bind:ref={groupRef}>
  <T.Mesh>
    <T.SphereGeometry args={[1, 96, 72]} />
    <T.MeshStandardMaterial
      map={texture}
      roughness={0.82}
      metalness={0.04}
      emissive={emissive}
      emissiveIntensity={0.25}
    />
  </T.Mesh>
  <OrbitalMarkers />
</T.Group>

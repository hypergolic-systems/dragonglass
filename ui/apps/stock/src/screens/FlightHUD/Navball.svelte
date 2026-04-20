<script lang="ts">
  import { Canvas, T } from '@threlte/core';
  import NavballSphere from './NavballSphere.svelte';
</script>

<div class="navball">
  <div class="navball__stage">
    <Canvas>
      <T.OrthographicCamera
        makeDefault
        position={[0, 0, 5]}
        zoom={87}
        near={0.1}
        far={100}
      />
      <T.AmbientLight intensity={1.15} />
      <T.DirectionalLight position={[3, 2.5, 4]} intensity={0.8} color="#ffffff" />
      <T.DirectionalLight position={[-3, -1, 2]} intensity={0.3} color="#6ea8d0" />
      <T.DirectionalLight position={[0, -3, 1]} intensity={0.2} color="#d28560" />
      <NavballSphere />
    </Canvas>

    <!-- Center boresight reticle -->
    <svg
      class="navball__reticle"
      viewBox="-60 -60 120 120"
      aria-hidden="true"
    >
      <g stroke="#7ef5b8" stroke-width="0.9" fill="none" stroke-linecap="round">
        <line x1="-11" y1="0" x2="-4" y2="0" />
        <line x1="4" y1="0" x2="11" y2="0" />
        <line x1="0" y1="-11" x2="0" y2="-4" />
        <path d="M-3.5 0 L0 -2.5 L3.5 0" />
        <circle r="1" fill="#7ef5b8" />
      </g>
    </svg>
  </div>
</div>

import { defineConfig } from 'vite';
import { svelte } from '@sveltejs/vite-plugin-svelte';

export default defineConfig({
  plugins: [svelte()],
  // Relative paths so the build works when loaded as file:// from
  // GameData/Dragonglass/UI/ or opened directly from dist/.
  base: './',
});

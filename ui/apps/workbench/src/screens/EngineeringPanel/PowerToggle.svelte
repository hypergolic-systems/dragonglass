<script lang="ts">
  let { state, onToggle, title }: {
    state: 'on' | 'off' | 'mixed';
    onToggle: () => void;
    title?: string;
  } = $props();
</script>

<button
  type="button"
  class="ptog ptog--{state}"
  onclick={(e: MouseEvent) => { e.stopPropagation(); onToggle(); }}
  {title}
>
  {state === 'on' ? 'ON' : state === 'off' ? 'OFF' : 'MIX'}
</button>

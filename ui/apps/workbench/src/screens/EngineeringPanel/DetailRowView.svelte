<script lang="ts">
  import type { DetailRow } from './helpers';

  let { row }: { row: DetailRow } = $props();
</script>

<div class="pdet__row">
  <dt class="pdet__k">{row.k}</dt>
  <dd class="pdet__v" class:pdet__v--good={row.tone === 'good'} class:pdet__v--warn={row.tone === 'warn'} class:pdet__v--dim={row.tone === 'dim'} class:pdet__v--info={row.tone === 'info'} class:pdet__v--mute={row.tone === 'mute'}>
    {row.v}
  </dd>
</div>

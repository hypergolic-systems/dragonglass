<script lang="ts">
  import type { VesselEntry } from './power';
  import { fmtFlow, fmtStorage, pctLabel } from './helpers';
  import DetailRowView from './DetailRowView.svelte';

  let { entry }: { entry: VesselEntry } = $props();

  let open = $state(false);
  let isPartner = $derived(entry.kind === 'partner');
  let inflow = $derived(entry.flow > 0);
  let flowTone = $derived(entry.flow >= 0 ? 'good' : 'warn');
</script>

<article class="pcard pcard--vessels" class:pcard--open={open}>
  <button type="button" class="pcard__head" onclick={() => open = !open}>
    <span class="pcard__chev">{open ? '▾' : '▸'}</span>
    <span class="pcard__name">{entry.vessel.name}</span>
    {#if isPartner}
      <span
        class="catg__dir"
        class:catg__dir--in={inflow}
        class:catg__dir--out={!inflow}
      >
        {inflow ? 'INFLOW' : 'OUTFLOW'}
      </span>
    {/if}
    <span class="pcard__flex"></span>
    <span class="pcard__stat pcard__stat--{flowTone}">
      <span class="pcard__num">{fmtFlow(entry.flow)}</span>
      <span class="pcard__unit">EC/s</span>
    </span>
  </button>
  {#if open}
    <div class="pcard__body">
      <dl class="pdet">
        {#if isPartner}
          <DetailRowView row={{ k: 'VIA PORT', v: entry.portName ?? '—' }} />
          <DetailRowView
            row={{
              k: inflow ? 'FROM' : 'TO',
              v: `${entry.vessel.name} · ${entry.partnerPortName ?? ''}`,
            }}
          />
          <DetailRowView
            row={{
              k: 'FLOW',
              v: `${fmtFlow(entry.flow)} EC/s (${inflow ? 'INFLOW' : 'OUTFLOW'})`,
              tone: inflow ? 'good' : 'warn',
            }}
          />
          <DetailRowView row={{ k: 'CROSSFEED', v: 'ENABLED', tone: 'good' }} />
        {:else}
          <DetailRowView
            row={{
              k: 'ROLE',
              v: entry.vessel.role.toUpperCase() +
                (entry.vessel.callsign ? ` · ${entry.vessel.callsign}` : ''),
            }}
          />
          <DetailRowView
            row={{
              k: 'OWN GEN',
              v: entry.summary.gen > 0 ? `${fmtFlow(entry.summary.gen)} EC/s` : '—',
              tone: entry.summary.gen > 0 ? 'good' : 'mute',
            }}
          />
          <DetailRowView
            row={{
              k: 'OWN DRAW',
              v: `${fmtFlow(-entry.summary.draw)} EC/s`,
              tone: 'dim',
            }}
          />
          <DetailRowView
            row={{
              k: 'OWN NET',
              v: `${fmtFlow(entry.summary.net)} EC/s`,
              tone: entry.summary.net >= 0 ? 'good' : 'warn',
            }}
          />
          <DetailRowView
            row={{
              k: 'STORAGE',
              v: `${fmtStorage(entry.summary.storage)} EC · ${pctLabel(entry.summary.storage)}`,
              tone: 'info',
            }}
          />
        {/if}
        <DetailRowView
          row={{
            k: 'NOTE',
            v: isPartner
              ? 'Synthesized attribution; physical EC pool is shared across the dock.'
              : 'Member of the current assembly; flows pool with other vessels.',
            tone: 'mute',
          }}
        />
      </dl>
    </div>
  {/if}
</article>

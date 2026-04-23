<script lang="ts">
  // Static read-only label — matches the previous DefaultModule label
  // row exactly so nothing visually regresses when a field falls
  // back to label (UI_Label, unknown control types, ...).

  import type { PartFieldData } from '@dragonglass/telemetry/core';

  interface Props {
    field: Extract<PartFieldData, { kind: 'label' }>;
  }

  const { field }: Props = $props();
</script>

<div class="widget widget--label">
  <span class="widget__name">{field.label}</span>
  <span class="widget__value">{field.value}</span>
</div>

<style>
  .widget {
    display: flex;
    align-items: baseline;
    gap: 6px;
    min-height: 14px;
    font-size: 9px;
  }

  .widget__name {
    color: var(--fg-dim);
    letter-spacing: 0.06em;
    text-transform: uppercase;
    flex: 0 0 auto;
    white-space: nowrap;
    overflow: hidden;
    text-overflow: ellipsis;
    max-width: 55%;
  }

  .widget__value {
    color: var(--fg);
    font-variant-numeric: tabular-nums;
    flex: 1 1 auto;
    text-align: right;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }
</style>

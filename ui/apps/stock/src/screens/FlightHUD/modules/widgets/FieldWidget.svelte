<script lang="ts">
  // Dispatcher: picks the right widget for the field's kind. Keeps
  // DefaultModule (and any future bespoke renderer) from having to
  // repeat the same switch — drop a `<FieldWidget {field} ... />`
  // and the correct component appears.

  import type { PartFieldData } from '@dragonglass/telemetry/core';
  import LabelField from './LabelField.svelte';
  import ToggleField from './ToggleField.svelte';
  import SliderField from './SliderField.svelte';
  import NumericField from './NumericField.svelte';
  import OptionField from './OptionField.svelte';
  import ProgressField from './ProgressField.svelte';

  interface Props {
    field: PartFieldData;
    onSetField: (fieldId: string, value: boolean | number) => void;
  }

  const { field, onSetField }: Props = $props();
</script>

{#if field.kind === 'toggle'}
  <ToggleField {field} {onSetField} />
{:else if field.kind === 'slider'}
  <SliderField {field} {onSetField} />
{:else if field.kind === 'numeric'}
  <NumericField {field} {onSetField} />
{:else if field.kind === 'option'}
  <OptionField {field} {onSetField} />
{:else if field.kind === 'progress'}
  <ProgressField {field} />
{:else}
  <LabelField {field} />
{/if}

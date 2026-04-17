<script lang="ts">
  let { label, short, statusLabel, statusDetail }: {
    label: string;
    short: string;
    statusLabel: string;
    statusDetail: string;
  } = $props();
</script>

<section class="acc acc--stub">
  <div class="acc__head" aria-disabled="true">
    <span class="acc__chev acc__chev--dim">►</span>
    <span class="acc__title acc__title--dim">{label}</span>
    <span class="acc__stub-short">{short}</span>
    <span class="acc__flex"></span>
    <span class="acc__stub-status">
      <span class="acc__stub-dot"></span>
      <span class="acc__stub-label">{statusLabel}</span>
      <span class="acc__stub-detail">{statusDetail}</span>
    </span>
  </div>
</section>

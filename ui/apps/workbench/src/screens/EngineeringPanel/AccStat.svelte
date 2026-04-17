<script lang="ts">
  let { label, value, tone = 'good' }: {
    label: string;
    value: string;
    tone?: 'good' | 'dim';
  } = $props();
</script>

<span class="accstat accstat--{tone}">
  <span class="accstat__k">{label}</span>
  <span class="accstat__v">{value}</span>
</span>

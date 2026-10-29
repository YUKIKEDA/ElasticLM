<script lang="ts">
  import Detail from './Detail.svelte';
  import List from './List.svelte';
  import { m } from './messages';

  let hash = $state(location.hash);
  $effect(() => {
    const changed = () => (hash = location.hash);
    addEventListener('hashchange', changed);
    return () => removeEventListener('hashchange', changed);
  });
  const id = $derived(/^#\/licenses\/(.+)$/.exec(hash)?.[1]);
</script>

<main>
  <h1>{m.title}</h1>
  {#if id}
    {#key id}<Detail id={decodeURIComponent(id)} />{/key}
  {:else}
    <List />
  {/if}
</main>

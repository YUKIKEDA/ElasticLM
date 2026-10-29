<script lang="ts">
  import { api, type Page } from './api';
  import { m } from './messages';

  let text = $state('');
  let query = $state({ q: '', after: null as string | null });
  let page = $state<Page | null>(null);
  let error = $state<Error | null>(null);
  $effect(() => {
    const { q, after } = query;
    let live = true;
    page = null;
    error = null;
    api.list(q, after).then((p) => { if (live) page = p; }, (e) => { if (live) error = e; });
    return () => { live = false; };
  });
  function search(e: SubmitEvent) {
    e.preventDefault();
    query = { q: text, after: null };
  }
</script>

<section>
  <form onsubmit={search}>
    <input aria-label={m.search} bind:value={text} />
    <button>{m.search}</button>
  </form>
  {#if error}
    <p class="error" role="alert">{error.message}</p>
  {:else if !page}
    <p>{m.loading}</p>
  {:else if page.items.length === 0}
    <p>{m.none}</p>
  {:else}
    <table>
      <thead><tr><th>{m.name}</th><th>{m.state}</th><th>{m.expires}</th></tr></thead>
      <tbody>
        {#each page.items as l (l.id)}
          <tr>
            <td><a href={`#/licenses/${encodeURIComponent(l.id)}`}>{l.name}</a></td>
            <td>{l.state}</td>
            <td>{l.expiresAt ?? m.never}</td>
          </tr>
        {/each}
      </tbody>
    </table>
    {#if page.next}
      <button onclick={() => (query = { q: query.q, after: page!.next })}>{m.next}</button>
    {/if}
  {/if}
</section>

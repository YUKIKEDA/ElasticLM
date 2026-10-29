<script lang="ts">
  import { api, type License } from './api';
  import { m } from './messages';

  let { id }: { id: string } = $props();
  let license = $state<License | null>(null);
  let error = $state<Error | null>(null);
  let asking = $state(false);
  let version = $state(0);
  $effect(() => {
    version;
    let live = true;
    api.get(id).then((l) => { if (live) license = l; }, (e) => { if (live) error = e; });
    return () => { live = false; };
  });
  async function revoke() {
    asking = false;
    try {
      await api.revoke(id);
      version++;
    } catch (e) {
      error = e as Error;
    }
  }
</script>

<section>
  <a href="#/">{m.back}</a>
  {#if error}<p class="error" role="alert">{error.message}</p>{/if}
  {#if !license && !error}<p>{m.loading}</p>{/if}
  {#if license}
    <dl>
      <dt>{m.name}</dt><dd>{license.name}</dd>
      <dt>{m.state}</dt><dd>{license.state}</dd>
      <dt>{m.expires}</dt><dd>{license.expiresAt ?? m.never}</dd>
    </dl>
    {#if license.state !== 'revoked'}
      {#if asking}
        <p>
          {m.confirmRevoke(license.name)} <button onclick={revoke}>{m.revoke}</button>
          <button onclick={() => (asking = false)}>{m.cancel}</button>
        </p>
      {:else}
        <button onclick={() => (asking = true)}>{m.revoke}</button>
      {/if}
    {/if}
  {/if}
</section>

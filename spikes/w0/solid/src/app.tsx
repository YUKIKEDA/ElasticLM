import { createResource, createSignal, For, Match, onCleanup, Show, Switch } from 'solid-js';
import { api } from './api';
import { m } from './messages';

function useHash() {
  const [hash, setHash] = createSignal(location.hash);
  const changed = () => setHash(location.hash);
  addEventListener('hashchange', changed);
  onCleanup(() => removeEventListener('hashchange', changed));
  return hash;
}

export function App() {
  const hash = useHash();
  const id = () => /^#\/licenses\/(.+)$/.exec(hash())?.[1];
  return (
    <main>
      <h1>{m.title}</h1>
      <Show when={id()} keyed fallback={<List />}>{(i) => <Detail id={decodeURIComponent(i)} />}</Show>
    </main>
  );
}

function List() {
  const [text, setText] = createSignal('');
  const [query, setQuery] = createSignal({ q: '', after: null as string | null });
  const [page] = createResource(query, (p) => api.list(p.q, p.after));
  return (
    <section>
      <form onSubmit={(e) => { e.preventDefault(); setQuery({ q: text(), after: null }); }}>
        <input aria-label={m.search} value={text()} onInput={(e) => setText(e.currentTarget.value)} />
        <button>{m.search}</button>
      </form>
      <Switch>
        <Match when={page.error}><p class="error" role="alert">{(page.error as Error).message}</p></Match>
        <Match when={page.loading}><p>{m.loading}</p></Match>
        <Match when={page()}>
          {(p) => (
            <>
              <Show when={p().items.length} fallback={<p>{m.none}</p>}>
                <table>
                  <thead><tr><th>{m.name}</th><th>{m.state}</th><th>{m.expires}</th></tr></thead>
                  <tbody>
                    <For each={p().items}>
                      {(l) => (
                        <tr>
                          <td><a href={`#/licenses/${encodeURIComponent(l.id)}`}>{l.name}</a></td>
                          <td>{l.state}</td>
                          <td>{l.expiresAt ?? m.never}</td>
                        </tr>
                      )}
                    </For>
                  </tbody>
                </table>
              </Show>
              <Show when={p().next}>
                {(next) => <button onClick={() => setQuery({ q: query().q, after: next() })}>{m.next}</button>}
              </Show>
            </>
          )}
        </Match>
      </Switch>
    </section>
  );
}

function Detail(props: { id: string }) {
  const [license, { refetch }] = createResource(() => props.id, api.get);
  const [asking, setAsking] = createSignal(false);
  const [error, setError] = createSignal<Error | null>(null);
  const revoke = async () => {
    setAsking(false);
    try {
      await api.revoke(props.id);
      await refetch();
    } catch (e) {
      setError(e as Error);
    }
  };
  return (
    <section>
      <a href="#/">{m.back}</a>
      <Show when={error() ?? (license.error as Error | undefined)}>{(e) => <p class="error" role="alert">{e().message}</p>}</Show>
      <Show when={!license.error && license()} fallback={<Show when={!license.error}><p>{m.loading}</p></Show>}>
        {(l) => (
          <>
            <dl>
              <dt>{m.name}</dt><dd>{l().name}</dd>
              <dt>{m.state}</dt><dd>{l().state}</dd>
              <dt>{m.expires}</dt><dd>{l().expiresAt ?? m.never}</dd>
            </dl>
            <Show when={l().state !== 'revoked'}>
              <Show when={asking()} fallback={<button onClick={() => setAsking(true)}>{m.revoke}</button>}>
                <p>
                  {m.confirmRevoke(l().name)} <button onClick={revoke}>{m.revoke}</button>
                  <button onClick={() => setAsking(false)}>{m.cancel}</button>
                </p>
              </Show>
            </Show>
          </>
        )}
      </Show>
    </section>
  );
}

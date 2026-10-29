import { useEffect, useState } from 'preact/hooks';
import { api, Problem, type License, type Page } from './api';
import { m } from './messages';

function useHash() {
  const [hash, setHash] = useState(location.hash);
  useEffect(() => {
    const changed = () => setHash(location.hash);
    addEventListener('hashchange', changed);
    return () => removeEventListener('hashchange', changed);
  }, []);
  return hash;
}

export function App() {
  const match = /^#\/licenses\/(.+)$/.exec(useHash());
  return (
    <main>
      <h1>{m.title}</h1>
      {match ? <Detail key={match[1]} id={decodeURIComponent(match[1])} /> : <List />}
    </main>
  );
}

function ErrorBanner({ error }: { error: Error | null }) {
  return error ? <p class="error" role="alert">{error.message}</p> : null;
}

function List() {
  const [text, setText] = useState('');
  const [query, setQuery] = useState({ q: '', after: null as string | null });
  const [page, setPage] = useState<Page | null>(null);
  const [error, setError] = useState<Error | null>(null);
  useEffect(() => {
    let live = true;
    setPage(null);
    setError(null);
    api.list(query.q, query.after).then((p) => live && setPage(p), (e) => live && setError(e));
    return () => { live = false; };
  }, [query]);
  return (
    <section>
      <form onSubmit={(e) => { e.preventDefault(); setQuery({ q: text, after: null }); }}>
        <input aria-label={m.search} value={text} onInput={(e) => setText(e.currentTarget.value)} />
        <button>{m.search}</button>
      </form>
      <ErrorBanner error={error} />
      {!page && !error && <p>{m.loading}</p>}
      {page && (page.items.length === 0 ? <p>{m.none}</p> : (
        <table>
          <thead><tr><th>{m.name}</th><th>{m.state}</th><th>{m.expires}</th></tr></thead>
          <tbody>
            {page.items.map((l) => (
              <tr key={l.id}>
                <td><a href={`#/licenses/${encodeURIComponent(l.id)}`}>{l.name}</a></td>
                <td>{l.state}</td>
                <td>{l.expiresAt ?? m.never}</td>
              </tr>
            ))}
          </tbody>
        </table>
      ))}
      {page?.next && <button onClick={() => setQuery({ q: query.q, after: page.next })}>{m.next}</button>}
    </section>
  );
}

function Detail({ id }: { id: string }) {
  const [license, setLicense] = useState<License | null>(null);
  const [error, setError] = useState<Error | null>(null);
  const [asking, setAsking] = useState(false);
  const [version, setVersion] = useState(0);
  useEffect(() => {
    let live = true;
    api.get(id).then((l) => live && setLicense(l), (e) => live && setError(e));
    return () => { live = false; };
  }, [id, version]);
  const revoke = async () => {
    setAsking(false);
    try {
      await api.revoke(id);
      setVersion((v) => v + 1);
    } catch (e) {
      setError(e as Problem);
    }
  };
  return (
    <section>
      <a href="#/">{m.back}</a>
      <ErrorBanner error={error} />
      {!license && !error && <p>{m.loading}</p>}
      {license && (
        <>
          <dl>
            <dt>{m.name}</dt><dd>{license.name}</dd>
            <dt>{m.state}</dt><dd>{license.state}</dd>
            <dt>{m.expires}</dt><dd>{license.expiresAt ?? m.never}</dd>
          </dl>
          {license.state !== 'revoked' && (asking ? (
            <p>
              {m.confirmRevoke(license.name)} <button onClick={revoke}>{m.revoke}</button>
              <button onClick={() => setAsking(false)}>{m.cancel}</button>
            </p>
          ) : <button onClick={() => setAsking(true)}>{m.revoke}</button>)}
        </>
      )}
    </section>
  );
}

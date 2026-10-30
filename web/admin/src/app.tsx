import { useEffect, useState } from 'preact/hooks';
import { api, Problem, signIn, SignInNeeded, type Filter, type License, type Me, type Page, type Product } from './api';
import { keyHash } from './keys';
import { m } from './messages';

// design §15.2 (0.100): every value from the API is drawn as text only; nothing builds markup, href, or src from it.

/** The routes of the screen, after "#" (design §15.2): the list, a license, or nothing known. */
export type Route = { kind: 'list' } | { kind: 'license'; id: string } | { kind: 'unknown' };

/** Reads a location hash as a route; an empty hash or "#/" is the list. */
export function route(hash: string): Route {
  if (hash === '' || hash === '#' || hash === '#/' || hash === '#/licenses') {
    return { kind: 'list' };
  }

  const license = /^#\/licenses\/([^/]+)$/.exec(hash);
  return license ? { kind: 'license', id: decodeURIComponent(license[1]) } : { kind: 'unknown' };
}

function useHash(): string {
  const [hash, setHash] = useState(location.hash);
  useEffect(() => {
    const changed = () => setHash(location.hash);
    addEventListener('hashchange', changed);
    return () => removeEventListener('hashchange', changed);
  }, []);
  return hash;
}

export function App() {
  const [me, setMe] = useState<Me | null>(null);
  const [error, setError] = useState<Error | null>(null);
  const current = route(useHash());
  useEffect(() => {
    api.me().then(setMe, setError);
  }, []);

  if (error instanceof SignInNeeded) {
    return (
      <main>
        <p role="alert">{m.signInAgain}</p>
        <button type="button" onClick={signIn}>
          {m.signIn}
        </button>
      </main>
    );
  }

  return (
    <>
      <header>
        <strong>{m.product}</strong>
        {me && (
          <span class="who">
            {me.subject} ({me.role}){' '}
            <button type="button" onClick={() => api.logout().catch(setError)}>
              {m.signOut}
            </button>
          </span>
        )}
      </header>
      <main>
        <ErrorBanner error={error} />
        {me &&
          (current.kind === 'list' ? (
            <List />
          ) : current.kind === 'license' ? (
            <Detail key={current.id} id={current.id} me={me} />
          ) : (
            <p>{m.notFound}</p>
          ))}
      </main>
    </>
  );
}

function ErrorBanner({ error }: { error: Error | null }) {
  if (!error || error instanceof SignInNeeded) {
    return null;
  }

  const text = error instanceof Problem && error.status === 403 ? m.forbidden : error.message;
  return (
    <p class="error" role="alert">
      {text}
    </p>
  );
}

function List() {
  const [filter, setFilter] = useState<Filter>({ state: '', q: '', keyHash: '' });
  const [after, setAfter] = useState<string | null>(null);
  const [page, setPage] = useState<Page<License> | null>(null);
  const [products, setProducts] = useState<Map<string, Product> | null>(null);
  const [error, setError] = useState<Error | null>(null);

  useEffect(() => {
    // design §15.2: a caller without product.read sees the product IDs.
    api.products().then(setProducts, () => setProducts(new Map()));
  }, []);

  useEffect(() => {
    let live = true;
    setPage(null);
    setError(null);
    api.licenses(filter, after).then(
      (p) => live && setPage(p),
      (e: Error) => live && setError(e),
    );
    return () => {
      live = false;
    };
  }, [filter, after]);

  // The values are read from the form's elements when it is sent (design §15.2, a lesson of W0).
  const submit = async (event: SubmitEvent) => {
    event.preventDefault();
    const form = new FormData(event.currentTarget as HTMLFormElement);
    const typedKey = String(form.get('key') ?? '').trim();
    let hash = '';
    if (typedKey) {
      const made = await keyHash(typedKey);
      if (made === null) {
        setError(new Error(m.badKey));
        return;
      }

      hash = made;
    }

    setAfter(null);
    setFilter({ state: String(form.get('state') ?? ''), q: String(form.get('q') ?? '').trim(), keyHash: hash });
  };

  return (
    <section>
      <h1>{m.licenses}</h1>
      <form onSubmit={submit}>
        <label>
          {m.nameSearch} <input name="q" maxLength={100} />
        </label>
        <label>
          {m.key} <input name="key" autoComplete="off" />
        </label>
        <label>
          {m.state}{' '}
          <select name="state">
            <option value="">{m.anyState}</option>
            {Object.keys(m.states).map((s) => (
              <option value={s}>{m.states[s]}</option>
            ))}
          </select>
        </label>
        <button type="submit">{m.search}</button>
      </form>
      <ErrorBanner error={error} />
      {!page && !error && <p>{m.loading}</p>}
      {page &&
        (page.items.length === 0 ? (
          <p>{m.none}</p>
        ) : (
          <table>
            <thead>
              <tr>
                <th>{m.name}</th>
                <th>{m.productColumn}</th>
                <th>{m.state}</th>
                <th>{m.expires}</th>
                <th>{m.created}</th>
              </tr>
            </thead>
            <tbody>
              {page.items.map((l) => (
                <tr key={l.id}>
                  <td>
                    <a href={`#/licenses/${encodeURIComponent(l.id)}`}>{l.name}</a>
                  </td>
                  <td>{products?.get(l.productId)?.name ?? l.productId}</td>
                  <td>{m.states[l.state] ?? l.state}</td>
                  <td>{l.expiresAt ?? m.never}</td>
                  <td>{l.createdAt}</td>
                </tr>
              ))}
            </tbody>
          </table>
        ))}
      {page?.next && (
        <button type="button" onClick={() => setAfter(page.next)}>
          {m.next}
        </button>
      )}
    </section>
  );
}

function Detail({ id, me }: { id: string; me: Me }) {
  const [license, setLicense] = useState<License | null>(null);
  const [error, setError] = useState<Error | null>(null);
  const [asking, setAsking] = useState(false);
  const [done, setDone] = useState(false);

  useEffect(() => {
    let live = true;
    api.license(id).then(
      (l) => live && setLicense(l),
      (e: Error) => live && setError(e),
    );
    return () => {
      live = false;
    };
  }, [id]);

  const revoke = async () => {
    setAsking(false);
    try {
      setLicense(await api.revoke(id));
      setDone(true);
    } catch (e) {
      setError(e as Error);
    }
  };

  // design §15.2: no button for an operation the role does not allow; the API refuses it as well (design §15.1).
  const mayRevoke = me.permissions.includes('license.revoke');
  return (
    <section>
      <a href="#/licenses">{m.back}</a>
      <ErrorBanner error={error} />
      {!license && !error && <p>{m.loading}</p>}
      {license && (
        <>
          <h1>{license.name}</h1>
          {done && <p role="status">{m.revoked}</p>}
          <dl>
            <dt>{m.fields.id}</dt>
            <dd>{license.id}</dd>
            <dt>{m.state}</dt>
            <dd>{m.states[license.state] ?? license.state}</dd>
            <dt>{m.fields.product}</dt>
            <dd>{license.productId}</dd>
            <dt>{m.fields.policy}</dt>
            <dd>{license.policyId}</dd>
            <dt>{m.fields.maxActivations}</dt>
            <dd>{license.maxActivations ?? m.fields.policyDefault}</dd>
            <dt>{m.created}</dt>
            <dd>{license.createdAt}</dd>
            <dt>{m.expires}</dt>
            <dd>{license.expiresAt ?? m.never}</dd>
            {license.suspendedAt && (
              <>
                <dt>{m.fields.suspended}</dt>
                <dd>{license.suspendedAt}</dd>
              </>
            )}
            {license.revokedAt && (
              <>
                <dt>{m.fields.revokedAt}</dt>
                <dd>{license.revokedAt}</dd>
              </>
            )}
            <dt>{m.fields.deactivations}</dt>
            <dd>{license.deactivations}</dd>
          </dl>
          <h2>{m.fields.features}</h2>
          <table>
            <thead>
              <tr>
                <th>{m.feature.code}</th>
                <th>{m.feature.version}</th>
                <th>{m.feature.seats}</th>
                <th>{m.feature.expires}</th>
              </tr>
            </thead>
            <tbody>
              {license.features.map((f) => (
                <tr key={f.code}>
                  <td>{f.code}</td>
                  <td>{f.ver ?? ''}</td>
                  <td>{f.seats ?? ''}</td>
                  <td>{f.expiresAt ?? ''}</td>
                </tr>
              ))}
            </tbody>
          </table>
          <h2>{m.fields.meta}</h2>
          <pre>{JSON.stringify(license.meta, null, 2)}</pre>
          {mayRevoke &&
            license.state !== 'revoked' &&
            (asking ? (
              <p>
                {m.confirmRevoke(license.name)}{' '}
                <button type="button" onClick={revoke}>
                  {m.revoke}
                </button>{' '}
                <button type="button" onClick={() => setAsking(false)}>
                  {m.cancel}
                </button>
              </p>
            ) : (
              <button type="button" onClick={() => setAsking(true)}>
                {m.revoke}
              </button>
            ))}
        </>
      )}
    </section>
  );
}

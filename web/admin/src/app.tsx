import { useEffect, useState } from 'preact/hooks';
import { api, signIn, SignInNeeded, type Filter, type License, type Me, type Page, type Product } from './api';
import { Admins } from './admins';
import { ApiKeys } from './apikeys';
import { Audit } from './audit';
import { ErrorBanner } from './common';
import { Detail } from './detail';
import { Issue } from './issue';
import { keyHash } from './keys';
import { m } from './messages';
import { Offline } from './offline';
import { Trials } from './trials';

// design §15.2 (0.100): every value from the API is drawn as text only; nothing builds markup, href, or src from it.

/** The routes of the screen, after "#" (design §15.2 "M5c の形", "M5d1 の形", and "M5d2 の形"). */
export type Route =
  | { kind: 'list' }
  | { kind: 'license'; id: string }
  | { kind: 'issue' }
  | { kind: 'offline' }
  | { kind: 'audit' }
  | { kind: 'trials' }
  | { kind: 'admins' }
  | { kind: 'apiKeys' }
  | { kind: 'unknown' };

/** Reads a location hash as a route; an empty hash or "#/" is the list. */
export function route(hash: string): Route {
  if (hash === '' || hash === '#' || hash === '#/' || hash === '#/licenses') {
    return { kind: 'list' };
  }

  const screens: Record<string, Route> = {
    '#/issue': { kind: 'issue' },
    '#/offline': { kind: 'offline' },
    '#/audit': { kind: 'audit' },
    '#/trials': { kind: 'trials' },
    '#/admins': { kind: 'admins' },
    '#/api-keys': { kind: 'apiKeys' },
  };
  if (Object.hasOwn(screens, hash)) {
    return screens[hash];
  }

  const license = /^#\/licenses\/([^/]+)$/.exec(hash);
  if (!license) {
    return { kind: 'unknown' };
  }

  // A hash typed by hand may hold a broken percent-encoding; it is a route that does not exist, not a failed screen.
  try {
    return { kind: 'license', id: decodeURIComponent(license[1]) };
  } catch {
    return { kind: 'unknown' };
  }
}

/**
 * The permissions each screen needs (design §15.2, M5d1 and M5d2). The header shows a screen only when the role has all of them, and a
 * route opened without them says so before any call; the API refuses as well (design §15.1).
 */
export const needs: Record<Exclude<Route['kind'], 'unknown'>, string[]> = {
  list: ['license.read'],
  license: ['license.read'],
  issue: ['license.create', 'product.read', 'policy.read'],
  offline: ['activation.offline'],
  audit: ['audit.read'],
  trials: ['trial.read'],
  admins: ['admin.read'],
  apiKeys: ['apikey.read'],
};

/** Returns whether a role's permissions open a route; an unknown route is open, to say it does not exist. */
export function allowed(me: Me, to: Route): boolean {
  return to.kind === 'unknown' || needs[to.kind].every((p) => me.permissions.includes(p));
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
        {me && <Nav me={me} />}
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
        {me && <Screen me={me} to={current} />}
      </main>
    </>
  );
}

function Nav({ me }: { me: Me }) {
  const links: [Exclude<Route['kind'], 'unknown' | 'license'>, string, string][] = [
    ['list', '#/licenses', m.nav.licenses],
    ['issue', '#/issue', m.nav.issue],
    ['offline', '#/offline', m.nav.offline],
    ['trials', '#/trials', m.nav.trials],
    ['apiKeys', '#/api-keys', m.nav.apiKeys],
    ['admins', '#/admins', m.nav.admins],
    ['audit', '#/audit', m.nav.audit],
  ];
  return (
    <nav>
      {links
        .filter(([kind]) => allowed(me, { kind }))
        .map(([, href, text]) => (
          <a href={href}>{text}</a>
        ))}
    </nav>
  );
}

function Screen({ me, to }: { me: Me; to: Route }) {
  if (!allowed(me, to)) {
    return (
      <p class="error" role="alert">
        {m.forbidden}
      </p>
    );
  }

  switch (to.kind) {
    case 'list':
      return <List />;
    case 'license':
      return <Detail key={to.id} id={to.id} me={me} />;
    case 'issue':
      return <Issue />;
    case 'offline':
      return <Offline />;
    case 'audit':
      return <Audit />;
    case 'trials':
      return <Trials mayDelete={me.permissions.includes('trial.delete')} />;
    case 'apiKeys':
      return <ApiKeys me={me} />;
    case 'admins':
      return <Admins me={me} />;
    default:
      return <p>{m.notFound}</p>;
  }
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

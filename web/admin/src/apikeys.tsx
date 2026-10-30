import { useEffect, useRef, useState } from 'preact/hooks';
import { api, Problem, SignInNeeded, type Admin, type ApiKey, type ApiKeyRequest, type Me, type NewApiKey, type Page } from './api';
import { Confirm, ErrorBanner } from './common';
import { scalars } from './files';
import { m } from './messages';

// The API keys screen (design §15.2 "M5d2 の形"). owner, support, and viewer read the list (§14.6); only owner creates and
// revokes. A new key stays in this component's memory only: not in the URL and not in the browser's storage.

const whole = /^\d+$/;

/**
 * Reads the create form into the API's request, with the checks the screen makes before sending, or returns the first
 * refusal. An empty number of days is not sent, so the API's default (90) applies.
 */
export function apiKeyRequest(data: FormData): ApiKeyRequest | string {
  const name = String(data.get('name') ?? '');
  if (scalars(name) < 1 || scalars(name) > 64) return m.apiKeys.badName;
  const permissions = data.getAll('permission').map(String);
  if (permissions.length < 1) return m.apiKeys.badPermissions;
  const days = String(data.get('days') ?? '').trim();
  if (days === '') return { name, permissions };
  if (!(whole.test(days) && Number(days) >= 1 && Number(days) <= 365)) return m.apiKeys.badDays;
  return { name, permissions, expiresInDays: Number(days) };
}

/** Returns what keeps a key from working that the screen can see, or an empty text (the 90-day rule is not visible). */
export function keyStatus(key: ApiKey, admins: Map<string, Admin> | null, now: number): string {
  if (key.revokedAt) return m.apiKeys.revoked;
  if (Date.parse(key.expiresAt) <= now) return m.apiKeys.expired;
  if (admins?.get(key.createdBy)?.enabled === false) return m.apiKeys.creatorDisabled;
  return '';
}

export function ApiKeys({ me }: { me: Me }) {
  const may = (permission: string) => me.permissions.includes(permission);
  const [admins, setAdmins] = useState<Map<string, Admin> | null>(null);
  const [after, setAfter] = useState<string | null>(null);
  const [round, setRound] = useState(0);
  const [page, setPage] = useState<Page<ApiKey> | null>(null);
  const [error, setError] = useState<Error | null>(null);
  const [asking, setAsking] = useState<ApiKey | null>(null);
  const [status, setStatus] = useState<string | null>(null);
  const [made, setMade] = useState<NewApiKey | null>(null);
  const [sending, setSending] = useState(false);
  // Two clicks in one task both see the old state, so the request in flight is also kept outside the state.
  const inFlight = useRef(false);

  useEffect(() => {
    // design §15.2 (M5d2): support and viewer have no admin.read and see the creator's ID.
    if (may('admin.read')) {
      api.admins().then((list) => setAdmins(new Map(list.map((a) => [a.id, a]))), setError);
    }
  }, []);

  useEffect(() => {
    let live = true;
    setPage(null);
    api.apiKeys(after).then(
      (p) => live && setPage(p),
      (e: Error) => live && setError(e),
    );
    return () => {
      live = false;
    };
  }, [after, round]);

  const reload = () => {
    setAfter(null);
    setRound((r) => r + 1);
  };

  // POST /v1/admin/api-keys is not idempotent: the button is off while it is out, and a lost answer is not sent again.
  const create = async (event: SubmitEvent) => {
    event.preventDefault();
    if (inFlight.current) return;
    setError(null);
    setStatus(null);
    const read = apiKeyRequest(new FormData(event.currentTarget as HTMLFormElement));
    if (typeof read === 'string') {
      setError(new Error(read));
      return;
    }

    inFlight.current = true;
    setSending(true);
    try {
      setMade(await api.createApiKey(read));
    } catch (e) {
      setError(e instanceof Problem || e instanceof SignInNeeded ? (e as Error) : new Error(m.apiKeys.noAnswer));
    } finally {
      inFlight.current = false;
      setSending(false);
    }
  };

  const revoke = async (key: ApiKey) => {
    setAsking(null);
    setError(null);
    setStatus(null);
    try {
      await api.revokeApiKey(key.id);
      setStatus(m.apiKeys.done);
    } catch (e) {
      setError(e as Error);
    }
    reload();
  };

  if (made) {
    return (
      <Made
        apiKey={made}
        onBack={() => {
          setMade(null);
          reload();
        }}
      />
    );
  }

  const creator = (id: string) => admins?.get(id)?.subject ?? id;
  const now = Date.now();
  return (
    <section>
      <h1>{m.apiKeys.title}</h1>
      <ErrorBanner error={error} />
      {status && <p role="status">{status}</p>}
      {asking && (
        <Confirm text={m.apiKeys.confirm(asking.name, asking.prefix)} yes={m.apiKeys.revoke} onYes={() => revoke(asking)} onNo={() => setAsking(null)} />
      )}
      {!page && !error && <p>{m.loading}</p>}
      {page &&
        (page.items.length === 0 ? (
          <p>{m.apiKeys.none}</p>
        ) : (
          <table class="api-keys">
            <thead>
              <tr>
                <th>{m.apiKeys.name}</th>
                <th>{m.apiKeys.prefix}</th>
                <th>{m.apiKeys.permissions}</th>
                <th>{m.apiKeys.createdBy}</th>
                <th>{m.created}</th>
                <th>{m.expires}</th>
                <th>{m.apiKeys.revokedAt}</th>
                <th>{m.apiKeys.status}</th>
                {may('apikey.revoke') && <th />}
              </tr>
            </thead>
            <tbody>
              {page.items.map((k) => (
                <tr key={k.id}>
                  <td>{k.name}</td>
                  <td>{k.prefix}</td>
                  <td>{k.permissions.join(' ')}</td>
                  <td>{creator(k.createdBy)}</td>
                  <td>{k.createdAt}</td>
                  <td>{k.expiresAt}</td>
                  <td>{k.revokedAt ?? ''}</td>
                  <td>{keyStatus(k, admins, now)}</td>
                  {may('apikey.revoke') && (
                    <td>
                      {!k.revokedAt && (
                        <button type="button" onClick={() => setAsking(k)}>
                          {m.apiKeys.revoke}
                        </button>
                      )}
                    </td>
                  )}
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
      {may('apikey.create') && (
        <form class="api-key" onSubmit={create}>
          <h2>{m.apiKeys.create}</h2>
          <label>
            {m.apiKeys.name} <input name="name" autoComplete="off" />
          </label>
          <fieldset class="permissions">
            <legend>{m.apiKeys.permissions}</legend>
            {/* design §15.2 (M5d2): only the creator's own permissions; the API refuses any other with 403. */}
            {me.permissions.map((p) => (
              <label key={p}>
                <input type="checkbox" name="permission" value={p} /> {p}
              </label>
            ))}
          </fieldset>
          <label>
            {m.apiKeys.days} <input name="days" inputMode="numeric" />
          </label>
          <button type="submit" disabled={sending}>
            {m.apiKeys.send}
          </button>
        </form>
      )}
    </section>
  );
}

function Made({ apiKey, onBack }: { apiKey: NewApiKey; onBack: () => void }) {
  const [copy, setCopy] = useState<string | null>(null);
  const copyKey = () =>
    Promise.resolve()
      .then(() => navigator.clipboard.writeText(apiKey.key))
      .then(
        () => setCopy(m.apiKeys.copied),
        () => setCopy(m.apiKeys.copyFailed),
      );
  return (
    <section>
      <h1>{apiKey.name}</h1>
      <p role="status">{m.apiKeys.made}</p>
      <input class="key" readOnly value={apiKey.key} aria-label={m.apiKeys.title} onFocus={(e) => (e.currentTarget as HTMLInputElement).select()} />{' '}
      <button type="button" onClick={copyKey}>
        {m.apiKeys.copy}
      </button>
      {copy && <p role="status">{copy}</p>}
      <p>
        <button type="button" onClick={onBack}>
          {m.apiKeys.back}
        </button>
      </p>
    </section>
  );
}

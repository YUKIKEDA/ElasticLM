import { useEffect, useState } from 'preact/hooks';
import { api, Problem, type Activation, type License, type Me, type Page, type Policy } from './api';
import { Confirm, ErrorBanner, UtcField } from './common';
import { save, utcOf } from './files';
import { m } from './messages';

// The license's screen (design §15.2 "M5c の形" and "M5d1 の形"): its fields, the operations the role and the license's
// state allow, and its activations. The API refuses what the screen hides as well (design §15.1).

export function Detail({ id, me }: { id: string; me: Me }) {
  const [license, setLicense] = useState<License | null>(null);
  const [policy, setPolicy] = useState<Policy | null>(null);
  const [error, setError] = useState<Error | null>(null);
  const [asking, setAsking] = useState<'revoke' | 'suspend' | null>(null);
  const [status, setStatus] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const may = (permission: string) => me.permissions.includes(permission);

  useEffect(() => {
    let live = true;
    api.license(id).then(
      (l) => {
        if (!live) return;
        setLicense(l);
        // design §15.2 (M5d1): the license file button needs the policy's binding; a role with license.file has policy.read.
        if (may('license.file')) {
          api.policy(l.policyId).then(
            (p) => live && setPolicy(p),
            (e: Error) => live && setError(e),
          );
        }
      },
      (e: Error) => live && setError(e),
    );
    return () => {
      live = false;
    };
  }, [id]);

  // A refused change (409) means the license changed under the screen, for example by another administrator: it is read again.
  const act = async (run: () => Promise<License | void>, done: string) => {
    setAsking(null);
    setError(null);
    setStatus(null);
    setBusy(true);
    try {
      const changed = await run();
      if (changed) setLicense(changed);
      setStatus(done);
    } catch (e) {
      setError(e as Error);
      if (e instanceof Problem && e.status === 409) {
        api.license(id).then(setLicense, setError);
      }
    } finally {
      setBusy(false);
    }
  };

  const extend = (event: SubmitEvent) => {
    event.preventDefault();
    const typed = String(new FormData(event.currentTarget as HTMLFormElement).get('expiresAt') ?? '');
    const at = utcOf(typed);
    if (at === null) {
      setError(new Error(m.badTime));
      return;
    }

    if (!license?.expiresAt || Date.parse(at) <= Date.parse(license.expiresAt) || Date.parse(at) <= Date.now()) {
      setError(new Error(m.notLater));
      return;
    }

    void act(() => api.extend(id, at), m.extended);
  };

  const saveFile = () =>
    act(async () => {
      save(await api.licenseFile(id), `${id}.elmlic`);
    }, m.fileSaved);

  const update = may('license.update');
  return (
    <section>
      <a href="#/licenses">{m.back}</a>
      <ErrorBanner error={error} />
      {!license && !error && <p>{m.loading}</p>}
      {license && (
        <>
          <h1>{license.name}</h1>
          {status && <p role="status">{status}</p>}
          <dl>
            <dt>{m.fields.id}</dt>
            <dd>{license.id}</dd>
            <dt>{m.state}</dt>
            <dd>{m.states[license.state] ?? license.state}</dd>
            <dt>{m.fields.product}</dt>
            <dd>{license.productId}</dd>
            <dt>{m.fields.policy}</dt>
            <dd>{policy ? policy.name : license.policyId}</dd>
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
          {asking === 'suspend' && (
            <Confirm text={m.confirmSuspend(license.name)} yes={m.suspend} onYes={() => act(() => api.suspend(id), m.suspended)} onNo={() => setAsking(null)} />
          )}
          {asking === 'revoke' && (
            <Confirm text={m.confirmRevoke(license.name)} yes={m.revoke} onYes={() => act(() => api.revoke(id), m.revoked)} onNo={() => setAsking(null)} />
          )}
          <div class="actions">
            {update && (license.state === 'active' || license.state === 'expired') && (
              <button type="button" disabled={busy} onClick={() => setAsking('suspend')}>
                {m.suspend}
              </button>
            )}
            {/* design §15.2 (M5d1): resuming stops no device, so it asks nothing. */}
            {update && license.state === 'suspended' && (
              <button type="button" disabled={busy} onClick={() => act(() => api.resume(id), m.resumed)}>
                {m.resume}
              </button>
            )}
            {may('license.file') && policy?.binding === 'unbound' && license.state === 'active' && (
              <button type="button" disabled={busy} onClick={saveFile}>
                {m.licenseFile}
              </button>
            )}
            {may('license.revoke') && license.state !== 'revoked' && (
              <button type="button" disabled={busy} onClick={() => setAsking('revoke')}>
                {m.revoke}
              </button>
            )}
          </div>
          {update && license.expiresAt && !license.suspendedAt && !license.revokedAt && (
            <form class="extend" onSubmit={extend}>
              <UtcField name="expiresAt" label={m.newExpiry} />
              <button type="submit" disabled={busy}>
                {m.extend}
              </button>
            </form>
          )}
          {may('activation.read') && <Activations licenseId={id} mayDeactivate={may('activation.deactivate')} />}
        </>
      )}
    </section>
  );
}

function Activations({ licenseId, mayDeactivate }: { licenseId: string; mayDeactivate: boolean }) {
  const [after, setAfter] = useState<string | null>(null);
  const [round, setRound] = useState(0);
  const [page, setPage] = useState<Page<Activation> | null>(null);
  const [error, setError] = useState<Error | null>(null);
  const [asking, setAsking] = useState<Activation | null>(null);
  const [status, setStatus] = useState<string | null>(null);

  useEffect(() => {
    let live = true;
    setPage(null);
    api.activations(licenseId, after).then(
      (p) => live && setPage(p),
      (e: Error) => live && setError(e),
    );
    return () => {
      live = false;
    };
  }, [licenseId, after, round]);

  const deactivate = async (activation: Activation) => {
    setAsking(null);
    setError(null);
    setStatus(null);
    try {
      await api.deactivate(activation.id);
      setStatus(m.activations.done);
      setAfter(null);
      setRound((r) => r + 1);
    } catch (e) {
      setError(e as Error);
    }
  };

  // The host and the user are what the device sent (design §12.3): drawn as text like every other value.
  const who = (a: Activation) => a.client?.host || a.keyId;
  return (
    <section>
      <h2>{m.activations.title}</h2>
      <ErrorBanner error={error} />
      {status && <p role="status">{status}</p>}
      {asking && (
        <Confirm text={m.activations.confirm(who(asking))} yes={m.activations.deactivate} onYes={() => deactivate(asking)} onNo={() => setAsking(null)} />
      )}
      {!page && !error && <p>{m.loading}</p>}
      {page &&
        (page.items.length === 0 ? (
          <p>{m.activations.none}</p>
        ) : (
          <table class="activations">
            <thead>
              <tr>
                <th>{m.activations.keyId}</th>
                <th>{m.activations.host}</th>
                <th>{m.activations.app}</th>
                <th>{m.activations.seq}</th>
                <th>{m.activations.blocked}</th>
                <th>{m.created}</th>
                <th>{m.updated}</th>
                {mayDeactivate && <th />}
              </tr>
            </thead>
            <tbody>
              {page.items.map((a) => (
                <tr key={a.id}>
                  <td>{a.keyId}</td>
                  <td>{a.client ? `${a.client.host} / ${a.client.user}` : ''}</td>
                  <td>{a.app ?? ''}</td>
                  <td>{a.seq}</td>
                  <td>{a.blockedAt ?? ''}</td>
                  <td>{a.createdAt}</td>
                  <td>{a.updatedAt}</td>
                  {mayDeactivate && (
                    <td>
                      <button type="button" onClick={() => setAsking(a)}>
                        {m.activations.deactivate}
                      </button>
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
    </section>
  );
}

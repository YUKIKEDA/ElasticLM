import { useEffect, useState } from 'preact/hooks';
import { api, type AuditPage, type Verified } from './api';
import { ErrorBanner } from './common';
import { m } from './messages';

// The audit log screen (design §14.5, §15.2 "M5d1 の形"): newest first, older pages on demand, and the chain check. Every
// read writes its own audit.list row (§14.5), which is shown at the top as it is.

export function Audit() {
  const [after, setAfter] = useState<number | null>(null);
  const [page, setPage] = useState<AuditPage | null>(null);
  const [error, setError] = useState<Error | null>(null);
  const [verified, setVerified] = useState<Verified | null>(null);
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    let live = true;
    setPage(null);
    api.audit(after).then(
      (p) => live && setPage(p),
      (e: Error) => live && setError(e),
    );
    return () => {
      live = false;
    };
  }, [after]);

  const verify = async () => {
    setBusy(true);
    setError(null);
    setVerified(null);
    try {
      setVerified(await api.verifyAudit());
    } catch (e) {
      setError(e as Error);
    } finally {
      setBusy(false);
    }
  };

  return (
    <section>
      <h1>{m.audit.title}</h1>
      <ErrorBanner error={error} />
      <button type="button" disabled={busy} onClick={verify}>
        {m.audit.verify}
      </button>
      {verified && (
        <p role="status" class={verified.intact ? '' : 'error'}>
          {verified.intact ? m.audit.intact(verified.checked) : m.audit.broken(verified.checked, verified.firstBroken)}
        </p>
      )}
      {!page && !error && <p>{m.loading}</p>}
      {page &&
        (page.items.length === 0 ? (
          <p>{m.audit.none}</p>
        ) : (
          <table class="audit">
            <thead>
              <tr>
                <th>{m.audit.n}</th>
                <th>{m.audit.ts}</th>
                <th>{m.audit.actor}</th>
                <th>{m.audit.action}</th>
                <th>{m.audit.target}</th>
                <th>{m.audit.result}</th>
                <th>{m.audit.ip}</th>
                <th>{m.audit.detail}</th>
              </tr>
            </thead>
            <tbody>
              {page.items.map((r) => (
                <tr key={r.n}>
                  <td>{r.n}</td>
                  <td>{r.ts}</td>
                  <td>{r.actorId === null ? r.actorType : `${r.actorType} ${r.actorId}`}</td>
                  <td>{r.action}</td>
                  <td>{r.target ?? ''}</td>
                  <td>{r.result}</td>
                  <td>{r.ip ?? ''}</td>
                  <td>{r.detail !== null && <pre class="wrap">{r.detail}</pre>}</td>
                </tr>
              ))}
            </tbody>
          </table>
        ))}
      {page?.next !== null && page?.next !== undefined && (
        <button type="button" onClick={() => setAfter(page.next)}>
          {m.audit.older}
        </button>
      )}
    </section>
  );
}

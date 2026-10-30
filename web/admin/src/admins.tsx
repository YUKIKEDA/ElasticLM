import { useEffect, useState } from 'preact/hooks';
import { api, type Admin, type Me, type Page, type Session } from './api';
import { Confirm, ErrorBanner } from './common';
import { m } from './messages';

// The administrators screen (design §15.2 "M5d2 の形"): every administrator, disabling and enabling them, and the admin
// UI's sessions. Under §14.6 only owner has admin.* and session.*.

type Asking = { kind: 'disable' | 'enable'; admin: Admin } | null;

export function Admins({ me }: { me: Me }) {
  const may = (permission: string) => me.permissions.includes(permission);
  const [admins, setAdmins] = useState<Admin[] | null>(null);
  const [round, setRound] = useState(0);
  const [sessionRound, setSessionRound] = useState(0);
  const [error, setError] = useState<Error | null>(null);
  const [asking, setAsking] = useState<Asking>(null);
  const [status, setStatus] = useState<string | null>(null);

  useEffect(() => {
    let live = true;
    // The old list stays until the new one comes, so the sessions below are not made again on each reload.
    // design §15.2 (M5d2): every page, so the session filter and the subjects name every administrator.
    api.admins().then(
      (list) => live && setAdmins(list),
      (e: Error) => live && setError(e),
    );
    return () => {
      live = false;
    };
  }, [round]);

  const change = async (kind: 'disable' | 'enable', admin: Admin) => {
    setAsking(null);
    setError(null);
    setStatus(null);
    try {
      if (kind === 'disable') {
        await api.disableAdmin(admin.id);
        setStatus(m.admins.disabled);
      } else {
        await api.enableAdmin(admin.id);
        setStatus(m.admins.enabledDone);
      }
    } catch (e) {
      setError(e as Error);
    }

    setRound((r) => r + 1);
    // A disable ends the administrator's sessions in the same transaction (§14.5), so the sessions are read again too.
    if (kind === 'disable') setSessionRound((r) => r + 1);
  };

  const update = may('admin.update');
  return (
    <section>
      <h1>{m.admins.title}</h1>
      <ErrorBanner error={error} />
      {status && <p role="status">{status}</p>}
      {asking && (
        <Confirm
          text={asking.kind === 'disable' ? m.admins.confirmDisable(asking.admin.subject) : m.admins.confirmEnable(asking.admin.subject)}
          yes={asking.kind === 'disable' ? m.admins.disable : m.admins.enable}
          onYes={() => change(asking.kind, asking.admin)}
          onNo={() => setAsking(null)}
        />
      )}
      {!admins && !error && <p>{m.loading}</p>}
      {admins && (
        <table class="admins">
          <thead>
            <tr>
              <th>{m.admins.subject}</th>
              <th>{m.admins.issuer}</th>
              <th>{m.admins.role}</th>
              <th>{m.admins.enabled}</th>
              <th>{m.created}</th>
              <th>{m.admins.lastLogin}</th>
              {update && <th />}
            </tr>
          </thead>
          <tbody>
            {admins.map((a) => (
              <tr key={a.id}>
                <td>
                  {a.subject}
                  {a.id === me.id && ` ${m.you}`}
                </td>
                <td>{a.issuer}</td>
                <td>{a.role}</td>
                <td>{a.enabled ? m.yes : m.no}</td>
                <td>{a.createdAt}</td>
                <td>{a.lastLoginAt ?? ''}</td>
                {update && (
                  <td>
                    {/* The API refuses to disable oneself with 409 as well; the button is not offered. */}
                    {a.enabled && a.id !== me.id && (
                      <button type="button" onClick={() => setAsking({ kind: 'disable', admin: a })}>
                        {m.admins.disable}
                      </button>
                    )}
                    {!a.enabled && (
                      <button type="button" onClick={() => setAsking({ kind: 'enable', admin: a })}>
                        {m.admins.enable}
                      </button>
                    )}
                  </td>
                )}
              </tr>
            ))}
          </tbody>
        </table>
      )}
      {may('session.read') && admins && <Sessions me={me} admins={admins} mayRevoke={may('session.revoke')} round={sessionRound} />}
    </section>
  );
}

function Sessions({ me, admins, mayRevoke, round }: { me: Me; admins: Admin[]; mayRevoke: boolean; round: number }) {
  const [adminId, setAdminId] = useState('');
  const [after, setAfter] = useState<string | null>(null);
  const [own, setOwn] = useState(0);
  const [page, setPage] = useState<Page<Session> | null>(null);
  const [error, setError] = useState<Error | null>(null);
  const [asking, setAsking] = useState<Session | null>(null);
  const [status, setStatus] = useState<string | null>(null);

  useEffect(() => {
    let live = true;
    setPage(null);
    api.sessions(adminId, after).then(
      (p) => live && setPage(p),
      (e: Error) => live && setError(e),
    );
    return () => {
      live = false;
    };
  }, [adminId, after, own, round]);

  const show = (event: SubmitEvent) => {
    event.preventDefault();
    setError(null);
    setAfter(null);
    setAdminId(String(new FormData(event.currentTarget as HTMLFormElement).get('admin') ?? ''));
    setOwn((r) => r + 1);
  };

  // A 404 means the session ended already (elsewhere, by its expiry, or by idling): the list is read again either way.
  // Ending the session in use makes the next request answer 401, which moves to the sign-in once (design §15.2, M5c).
  const revoke = async (session: Session) => {
    setAsking(null);
    setError(null);
    setStatus(null);
    try {
      await api.revokeSession(session.id);
      setStatus(m.sessions.done);
    } catch (e) {
      setError(e as Error);
    }
    setAfter(null);
    setOwn((r) => r + 1);
  };

  const subject = (id: string) => admins.find((a) => a.id === id)?.subject ?? id;
  const confirmText = (s: Session) => (s.adminId === me.id ? m.sessions.confirmOwn(s.createdAt) : m.sessions.confirm(subject(s.adminId), s.createdAt));
  return (
    <section>
      <h2>{m.sessions.title}</h2>
      <form onSubmit={show}>
        <label>
          {m.sessions.admin}{' '}
          <select name="admin">
            <option value="">{m.sessions.anyAdmin}</option>
            {admins.map((a) => (
              <option value={a.id}>{a.subject}</option>
            ))}
          </select>
        </label>
        <button type="submit">{m.sessions.filter}</button>
      </form>
      <ErrorBanner error={error} />
      {status && <p role="status">{status}</p>}
      {asking && <Confirm text={confirmText(asking)} yes={m.sessions.revoke} onYes={() => revoke(asking)} onNo={() => setAsking(null)} />}
      {!page && !error && <p>{m.loading}</p>}
      {page &&
        (page.items.length === 0 ? (
          <p>{m.sessions.none}</p>
        ) : (
          <table class="sessions">
            <thead>
              <tr>
                <th>{m.sessions.admin}</th>
                <th>{m.created}</th>
                <th>{m.sessions.lastSeen}</th>
                <th>{m.expires}</th>
                <th>{m.audit.ip}</th>
                {mayRevoke && <th />}
              </tr>
            </thead>
            <tbody>
              {page.items.map((s) => (
                <tr key={s.id}>
                  <td>
                    {subject(s.adminId)}
                    {s.adminId === me.id && ` ${m.you}`}
                  </td>
                  <td>{s.createdAt}</td>
                  <td>{s.lastSeenAt}</td>
                  <td>{s.expiresAt}</td>
                  <td>{s.ip ?? ''}</td>
                  {mayRevoke && (
                    <td>
                      <button type="button" onClick={() => setAsking(s)}>
                        {m.sessions.revoke}
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

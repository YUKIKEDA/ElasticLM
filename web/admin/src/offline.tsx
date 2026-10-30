import { useState } from 'preact/hooks';
import { api } from './api';
import { ErrorBanner } from './common';
import { answerName, offlineLimit, save } from './files';
import { m } from './messages';

// The offline screen (design §12.8.4, §15.2 "M5d1 の形"): a request file goes to the service unchanged, and the answer is
// saved as the service sent it. Neither the screen nor the service keeps the files.

export function Offline() {
  const [error, setError] = useState<Error | null>(null);
  const [status, setStatus] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const send = async (event: SubmitEvent) => {
    event.preventDefault();
    setError(null);
    setStatus(null);
    const input = (event.currentTarget as HTMLFormElement).elements.namedItem('file') as HTMLInputElement;
    const file = input.files?.[0];
    if (!file) return;
    if (file.size > offlineLimit) {
      setError(new Error(m.offline.tooBig));
      return;
    }

    setBusy(true);
    try {
      const answer = await api.offline(file);
      if (answer === null) {
        setStatus(m.offline.taken);
      } else {
        const name = answerName(file.name);
        save(answer, name);
        setStatus(m.offline.answered(name));
      }
    } catch (e) {
      setError(e as Error);
    } finally {
      setBusy(false);
    }
  };

  return (
    <section>
      <h1>{m.offline.title}</h1>
      <ErrorBanner error={error} />
      {status && <p role="status">{status}</p>}
      <form onSubmit={send}>
        <label>
          {m.offline.file} <input type="file" name="file" accept=".elmreq,.elmdeact" />
        </label>
        <button type="submit" disabled={busy}>
          {m.offline.send}
        </button>
      </form>
    </section>
  );
}

import { useEffect, useState } from 'preact/hooks';
import { api, type Page, type Product, type Trial } from './api';
import { Confirm, ErrorBanner } from './common';
import { m } from './messages';

// The trials screen (design §15.2 "M5d2 の形"): the trials, by product, and their deletion. Every role with trial.read
// also has product.read, so the product names are always read.

export function Trials({ mayDelete }: { mayDelete: boolean }) {
  const [products, setProducts] = useState<Map<string, Product> | null>(null);
  const [productId, setProductId] = useState('');
  const [after, setAfter] = useState<string | null>(null);
  const [round, setRound] = useState(0);
  const [page, setPage] = useState<Page<Trial> | null>(null);
  const [error, setError] = useState<Error | null>(null);
  const [asking, setAsking] = useState<Trial | null>(null);
  const [status, setStatus] = useState<string | null>(null);

  useEffect(() => {
    api.products().then(setProducts, setError);
  }, []);

  useEffect(() => {
    let live = true;
    setPage(null);
    api.trials(productId, after).then(
      (p) => live && setPage(p),
      (e: Error) => live && setError(e),
    );
    return () => {
      live = false;
    };
  }, [productId, after, round]);

  const show = (event: SubmitEvent) => {
    event.preventDefault();
    const chosen = String(new FormData(event.currentTarget as HTMLFormElement).get('product') ?? '');
    setError(null);
    setAfter(null);
    setProductId(chosen);
    setRound((r) => r + 1);
  };

  // A 404 means the trial was deleted elsewhere: the list is read again either way.
  const remove = async (trial: Trial) => {
    setAsking(null);
    setError(null);
    setStatus(null);
    try {
      await api.deleteTrial(trial.id);
      setStatus(m.trials.done);
    } catch (e) {
      setError(e as Error);
    }
    setAfter(null);
    setRound((r) => r + 1);
  };

  const productName = (id: string) => products?.get(id)?.name ?? id;
  return (
    <section>
      <h1>{m.trials.title}</h1>
      <form onSubmit={show}>
        <label>
          {m.trials.product}{' '}
          <select name="product">
            <option value="">{m.trials.anyProduct}</option>
            {[...(products?.values() ?? [])].map((p) => (
              <option value={p.id}>{p.name}</option>
            ))}
          </select>
        </label>
        <button type="submit">{m.trials.filter}</button>
      </form>
      <ErrorBanner error={error} />
      {status && <p role="status">{status}</p>}
      {asking && (
        <Confirm
          text={m.trials.confirm(productName(asking.productId), asking.createdAt)}
          yes={m.trials.remove}
          onYes={() => remove(asking)}
          onNo={() => setAsking(null)}
        />
      )}
      {!page && !error && <p>{m.loading}</p>}
      {page &&
        (page.items.length === 0 ? (
          <p>{m.trials.none}</p>
        ) : (
          <table class="trials">
            <thead>
              <tr>
                <th>{m.trials.product}</th>
                <th>{m.trials.fingerprints}</th>
                <th>{m.created}</th>
                {mayDelete && <th />}
              </tr>
            </thead>
            <tbody>
              {page.items.map((t) => (
                <tr key={t.id}>
                  <td>{productName(t.productId)}</td>
                  <td>{t.fingerprints.join(', ')}</td>
                  <td>{t.createdAt}</td>
                  {mayDelete && (
                    <td>
                      <button type="button" onClick={() => setAsking(t)}>
                        {m.trials.remove}
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

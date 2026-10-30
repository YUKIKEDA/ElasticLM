import { useEffect, useRef, useState } from 'preact/hooks';
import { api, Problem, SignInNeeded, type Feature, type LicenseRequest, type NewLicense, type Policy, type Product, type ProductFeature } from './api';
import { ErrorBanner, UtcField } from './common';
import { apiTime, scalars, utcOf } from './files';
import { m } from './messages';

// The issue screen (design §15.2 "M5d1 の形"): a license for one policy of one product, checked before it is sent, and
// its key shown once. The key stays in this component's memory only: not in the URL and not in the browser's storage.

/** Returns whether a policy's licenses carry seats per feature: a server license or a cloud-floating one (design §12.7.6). */
export function needsSeats(policy: Policy): boolean {
  return policy.licenseKind === 'server' || policy.floating === 'cloud';
}

const whole = /^\d+$/;

/**
 * Reads the issue form into the API's request, with the checks the screen makes before sending, or returns the first
 * refusal. The byte limits of meta are left to the API's detail.
 */
export function licenseRequest(data: FormData, policy: Policy, features: ProductFeature[]): LicenseRequest | string {
  const text = (name: string) => String(data.get(name) ?? '');
  const name = text('name');
  if (scalars(name) < 1 || scalars(name) > 256) return m.issue.badName;

  const seats = needsSeats(policy);
  const timed = policy.expirationBasis !== 'fromFirstActivation';
  const time = (field: string): string | null | undefined => (text(field) === '' ? null : (utcOf(text(field)) ?? undefined));
  const granted: Feature[] = [];
  for (const f of features) {
    if (!data.get(`use.${f.code}`)) continue;
    const count = text(`seats.${f.code}`).trim();
    if (seats && !(whole.test(count) && Number(count) >= 1 && Number(count) <= 1_000_000)) return m.issue.badSeats(f.code);
    const expiresAt = timed ? time(`expires.${f.code}`) : null;
    if (expiresAt === undefined) return m.badTime;
    granted.push({ code: f.code, ver: text(`ver.${f.code}`).trim() || null, seats: seats ? Number(count) : null, expiresAt });
  }

  if (granted.length < 1 || granted.length > 256) return m.issue.badFeatures;

  const max = text('maxActivations').trim();
  if (max !== '' && !(whole.test(max) && Number(max) >= 1 && Number(max) <= 100_000)) return m.issue.badMax;
  const expiresAt = timed ? time('expiresAt') : null;
  if (expiresAt === undefined) return m.badTime;

  // A key such as "__proto__" must stay a plain key, so the pairs go into an object with no prototype.
  const meta = Object.create(null) as Record<string, string>;
  const keys = data.getAll('metaKey').map(String);
  const values = data.getAll('metaValue').map(String);
  const seen = new Set<string>();
  for (let i = 0; i < keys.length; i++) {
    if (keys[i] === '' && values[i] === '') continue;
    if (keys[i] === '') return m.issue.emptyKey;
    if (seen.has(keys[i])) return m.issue.twiceKey(keys[i]);
    seen.add(keys[i]);
    meta[keys[i]] = values[i];
  }

  return { policyId: policy.id, name, features: granted, maxActivations: max === '' ? null : Number(max), expiresAt, meta };
}

export function Issue() {
  const [products, setProducts] = useState<Product[] | null>(null);
  const [productId, setProductId] = useState('');
  const [policies, setPolicies] = useState<Policy[] | null>(null);
  const [features, setFeatures] = useState<ProductFeature[] | null>(null);
  const [policyId, setPolicyId] = useState('');
  const [metaRows, setMetaRows] = useState<number[]>([]);
  const [review, setReview] = useState<LicenseRequest | null>(null);
  const [sending, setSending] = useState(false);
  // Two clicks in one task both see the old state, so the request in flight is also kept outside the state.
  const inFlight = useRef(false);
  const [made, setMade] = useState<NewLicense | null>(null);
  const [error, setError] = useState<Error | null>(null);

  useEffect(() => {
    api.products().then((p) => setProducts([...p.values()]), setError);
  }, []);

  useEffect(() => {
    if (!productId) return;
    let live = true;
    setPolicies(null);
    setFeatures(null);
    setPolicyId('');
    setReview(null);
    // design §15.2 (M5d1): trial licenses come from the trial flow (§12.9), so a trial policy is not offered here.
    api.policies(productId).then(
      (p) => live && setPolicies(p.filter((y) => y.licenseKind !== 'trial')),
      (e: Error) => live && setError(e),
    );
    api.features(productId).then(
      (f) => live && setFeatures(f),
      (e: Error) => live && setError(e),
    );
    return () => {
      live = false;
    };
  }, [productId]);

  const policy = policies?.find((y) => y.id === policyId) ?? null;

  // The values are read from the form's elements when it is sent (design §15.2, a lesson of W0).
  const check = (event: SubmitEvent) => {
    event.preventDefault();
    setError(null);
    if (!policy || !features) return;
    const read = licenseRequest(new FormData(event.currentTarget as HTMLFormElement), policy, features);
    if (typeof read === 'string') {
      setError(new Error(read));
      return;
    }

    setReview(read);
  };

  // POST /v1/admin/licenses is not idempotent: the button is off while it is out, and a lost answer is not sent again.
  const create = async () => {
    if (!review || inFlight.current) return;
    inFlight.current = true;
    setSending(true);
    setError(null);
    try {
      setMade(await api.createLicense(review));
    } catch (e) {
      setError(e instanceof Problem || e instanceof SignInNeeded ? (e as Error) : new Error(m.issue.noAnswer));
    } finally {
      inFlight.current = false;
      setSending(false);
    }
  };

  if (made) {
    return <Made license={made} />;
  }

  const empty = policies !== null && features !== null && (policies.length === 0 || features.length === 0);
  return (
    <section>
      <h1>{m.issue.title}</h1>
      <ErrorBanner error={error} />
      <form class="issue" onSubmit={check}>
        <fieldset disabled={review !== null}>
          <label>
            {m.issue.product}{' '}
            <select name="product" value={productId} onChange={(e) => setProductId((e.currentTarget as HTMLSelectElement).value)}>
              <option value="">{m.issue.choose}</option>
              {products?.map((p) => (
                <option value={p.id}>{p.name}</option>
              ))}
            </select>
          </label>
          {policies?.length === 0 && <p class="error">{m.issue.noPolicy}</p>}
          {features?.length === 0 && <p class="error">{m.issue.noFeature}</p>}
          {productId && !empty && policies && features && (
            <div key={productId} class="fields">
              <label>
                {m.issue.policy}{' '}
                <select name="policy" value={policyId} onChange={(e) => setPolicyId((e.currentTarget as HTMLSelectElement).value)}>
                  <option value="">{m.issue.choose}</option>
                  {policies.map((y) => (
                    <option value={y.id}>{y.name}</option>
                  ))}
                </select>
              </label>
              {policy && <PolicyFields policy={policy} features={features} metaRows={metaRows} setMetaRows={setMetaRows} />}
            </div>
          )}
          <button type="submit" disabled={!policy || empty}>
            {m.issue.review}
          </button>
        </fieldset>
      </form>
      {review && policy && (
        <section class="review">
          <h2>{m.issue.confirm}</h2>
          <dl>
            <dt>{m.issue.policy}</dt>
            <dd>{policy.name}</dd>
            <dt>{m.name}</dt>
            <dd>{review.name}</dd>
            <dt>{m.fields.maxActivations}</dt>
            <dd>{review.maxActivations ?? m.fields.policyDefault}</dd>
            <dt>{m.expires}</dt>
            <dd>{review.expiresAt ?? m.fields.policyDefault}</dd>
          </dl>
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
              {review.features.map((f) => (
                <tr key={f.code}>
                  <td>{f.code}</td>
                  <td>{f.ver ?? ''}</td>
                  <td>{f.seats ?? ''}</td>
                  <td>{f.expiresAt ?? ''}</td>
                </tr>
              ))}
            </tbody>
          </table>
          <pre>{JSON.stringify(review.meta, null, 2)}</pre>
          <button type="button" disabled={sending} onClick={create}>
            {m.issue.create}
          </button>{' '}
          <button type="button" disabled={sending} onClick={() => setReview(null)}>
            {m.issue.edit}
          </button>
        </section>
      )}
    </section>
  );
}

function PolicyFields({
  policy,
  features,
  metaRows,
  setMetaRows,
}: {
  policy: Policy;
  features: ProductFeature[];
  metaRows: number[];
  setMetaRows: (rows: number[]) => void;
}) {
  const seats = needsSeats(policy);
  const timed = policy.expirationBasis !== 'fromFirstActivation';
  const guess = policy.expirationBasis === 'fromIssue' && policy.duration !== null ? apiTime(Date.now() + policy.duration * 1000) : null;
  return (
    <>
      <label>
        {m.name} <input name="name" autoComplete="off" />
      </label>
      <table class="features">
        <thead>
          <tr>
            <th>{m.issue.use}</th>
            <th>{m.issue.version}</th>
            {seats && <th>{m.issue.seats}</th>}
            {timed && (
              <th>
                {m.issue.featureExpiry} ({m.utc})
              </th>
            )}
          </tr>
        </thead>
        <tbody>
          {features.map((f) => (
            <tr key={f.code}>
              <td>
                <label>
                  <input type="checkbox" name={`use.${f.code}`} value="1" /> {f.name} ({f.code})
                </label>
              </td>
              <td>
                <input name={`ver.${f.code}`} aria-label={`${m.issue.version} ${f.code}`} />
              </td>
              {seats && (
                <td>
                  <input name={`seats.${f.code}`} inputMode="numeric" aria-label={`${m.issue.seats} ${f.code}`} />
                </td>
              )}
              {timed && (
                <td>
                  <input type="datetime-local" name={`expires.${f.code}`} aria-label={`${m.issue.featureExpiry} ${f.code}`} />
                </td>
              )}
            </tr>
          ))}
        </tbody>
      </table>
      <label>
        {m.issue.maxActivations} <input name="maxActivations" inputMode="numeric" />
      </label>
      {timed ? (
        <UtcField name="expiresAt" label={m.issue.expiry}>
          {guess && <small> {m.issue.defaultExpiry(guess)}</small>}
        </UtcField>
      ) : (
        <p>{m.issue.noExpiry}</p>
      )}
      <fieldset class="meta">
        <legend>{m.issue.meta}</legend>
        {metaRows.map((row) => (
          <div key={row}>
            <input name="metaKey" aria-label={m.issue.metaKey} placeholder={m.issue.metaKey} />{' '}
            <input name="metaValue" aria-label={m.issue.metaValue} placeholder={m.issue.metaValue} />{' '}
            <button type="button" onClick={() => setMetaRows(metaRows.filter((r) => r !== row))}>
              {m.issue.removeMeta}
            </button>
          </div>
        ))}
        <button type="button" onClick={() => setMetaRows([...metaRows, (metaRows.at(-1) ?? 0) + 1])}>
          {m.issue.addMeta}
        </button>
      </fieldset>
    </>
  );
}

function Made({ license }: { license: NewLicense }) {
  const [copy, setCopy] = useState<string | null>(null);
  const copyKey = () =>
    Promise.resolve()
      .then(() => navigator.clipboard.writeText(license.key))
      .then(
        () => setCopy(m.issue.copied),
        () => setCopy(m.issue.copyFailed),
      );
  return (
    <section>
      <h1>{license.name}</h1>
      <p role="status">{m.issue.made}</p>
      <input class="key" readOnly value={license.key} aria-label={m.key} onFocus={(e) => (e.currentTarget as HTMLInputElement).select()} />{' '}
      <button type="button" onClick={copyKey}>
        {m.issue.copy}
      </button>
      {copy && <p role="status">{copy}</p>}
      <p>
        <button type="button" onClick={() => (location.hash = `#/licenses/${encodeURIComponent(license.id)}`)}>
          {m.issue.open}
        </button>
      </p>
    </section>
  );
}

// The calls of the admin UI to the administration API (design §14.5, §15.2 "M5c の形" and "M5d1 の形", §15.3).
export type Feature = { code: string; ver: string | null; seats: number | null; expiresAt: string | null };
export type License = {
  id: string;
  productId: string;
  policyId: string;
  name: string;
  features: Feature[];
  maxActivations: number | null;
  state: string;
  createdAt: string;
  expiresAt: string | null;
  suspendedAt: string | null;
  revokedAt: string | null;
  meta: Record<string, string>;
  deactivations: number;
};
export type NewLicense = License & { key: string };
export type Page<T> = { items: T[]; next: string | null };
export type Product = { id: string; code: string; name: string };
export type ProductFeature = { id: string; productId: string; code: string; name: string };
export type Policy = {
  id: string;
  productId: string;
  name: string;
  licenseKind: string;
  binding: string;
  expirationBasis: string;
  duration: number | null;
  floating: string;
};
export type Activation = {
  id: string;
  licenseId: string;
  keyId: string;
  client: { host: string; user: string } | null;
  app: string | null;
  seq: number;
  blockedAt: string | null;
  createdAt: string;
  updatedAt: string;
};
export type AuditRow = {
  n: number;
  ts: string;
  actorType: string;
  actorId: string | null;
  action: string;
  target: string | null;
  result: string;
  ip: string | null;
  detail: string | null;
};
export type AuditPage = { items: AuditRow[]; next: number | null };
export type Verified = { intact: boolean; checked: number; firstBroken: number | null };
export type Me = { id: string; issuer: string; subject: string; role: string; permissions: string[] };
export type Filter = { state: string; q: string; keyHash: string };
export type LicenseRequest = {
  policyId: string;
  name: string;
  features: Feature[];
  maxActivations: number | null;
  expiresAt: string | null;
  meta: Record<string, string>;
};

/** A refusal or failure of the service, with its ELM_ code and its one-sentence detail. */
export class Problem extends Error {
  readonly code: string;
  readonly status: number;
  constructor(status: number, code: string, detail: string) {
    super(detail);
    this.code = code;
    this.status = status;
  }
}

// design §15.2: one automatic move to the sign-in; a second 401 stops with a button, so a browser that does not keep the
// session cookie does not go back and forth with the identity provider.
const signInMark = 'elm-sign-in-tried';

function tried(): boolean {
  try {
    return sessionStorage.getItem(signInMark) !== null;
  } catch {
    return true;
  }
}

/** Starts the sign-in; the service sends the browser to the identity provider and back to "/". */
export function signIn(): void {
  try {
    sessionStorage.setItem(signInMark, '1');
  } catch {
    // Without session storage the next 401 asks for the button instead.
  }
  location.assign('/auth/login');
}

/** Forgets the sign-in mark once a request with the session passes. */
export function signedIn(): void {
  try {
    sessionStorage.removeItem(signInMark);
  } catch {
    // Nothing to forget.
  }
}

/** The error a 401 gives when the automatic sign-in was already tried. */
export class SignInNeeded extends Error {}

type Body = { json: unknown } | { blob: Blob };

// fetch keeps its default mode (cors): with same-origin or no-cors, Referrer-Policy no-referrer sends Origin: null,
// which the service refuses with 403 (design §15.3).
async function send(path: string, method: string, body?: Body): Promise<Response> {
  const headers: Record<string, string> = { Accept: 'application/json' };
  if (method !== 'GET') {
    headers['X-ELM-CSRF'] = '1';
  }

  let payload: BodyInit | undefined;
  if (body && 'json' in body) {
    headers['Content-Type'] = 'application/json';
    payload = JSON.stringify(body.json);
  } else if (body) {
    // design §15.2 (M5d1): the chosen file goes out as it is; the service trims it and does not read the type (§12.8.4).
    headers['Content-Type'] = 'text/plain';
    payload = body.blob;
  }

  const response = await fetch(path, { method, headers, credentials: 'same-origin', body: payload });
  if (response.status === 401) {
    if (tried()) {
      throw new SignInNeeded();
    }

    signIn();
    throw new Problem(401, 'ELM_UNAUTHORIZED', 'Signing in…');
  }

  if (!response.ok) {
    const problem = (await response.json().catch(() => null)) as { code?: string; detail?: string } | null;
    throw new Problem(response.status, problem?.code ?? 'ELM_INTERNAL_ERROR', problem?.detail ?? `The service answered ${response.status}.`);
  }

  return response;
}

async function call<T>(path: string, method = 'GET', body?: Body): Promise<T> {
  const response = await send(path, method, body);
  return (response.status === 204 ? undefined : await response.json()) as T;
}

const e = encodeURIComponent;

/** Returns the query of a license list: the filters that are set, then the cursor. */
export function listQuery(filter: Filter, after: string | null): string {
  const parts = ['limit=50'];
  if (filter.state) parts.push(`state=${e(filter.state)}`);
  if (filter.q) parts.push(`q=${e(filter.q)}`);
  if (filter.keyHash) parts.push(`keyHash=${e(filter.keyHash)}`);
  if (after) parts.push(`after=${e(after)}`);
  return parts.join('&');
}

// design §15.2: lists the screen needs whole are read page by page with the largest page (200).
async function all<T>(path: string): Promise<T[]> {
  const items: T[] = [];
  const glue = path.includes('?') ? '&' : '?';
  let after: string | null = null;
  do {
    const page: Page<T> = await call<Page<T>>(`${path}${glue}limit=200${after ? `&after=${e(after)}` : ''}`);
    items.push(...page.items);
    after = page.next;
  } while (after);
  return items;
}

export const api = {
  me: async () => {
    const me = await call<Me>('/v1/admin/me');
    signedIn();
    return me;
  },
  licenses: (filter: Filter, after: string | null) => call<Page<License>>(`/v1/admin/licenses?${listQuery(filter, after)}`),
  license: (id: string) => call<License>(`/v1/admin/licenses/${e(id)}`),
  createLicense: (request: LicenseRequest) => call<NewLicense>('/v1/admin/licenses', 'POST', { json: request }),
  revoke: (id: string) => call<License>(`/v1/admin/licenses/${e(id)}/revoke`, 'POST'),
  suspend: (id: string) => call<License>(`/v1/admin/licenses/${e(id)}/suspend`, 'POST'),
  resume: (id: string) => call<License>(`/v1/admin/licenses/${e(id)}/resume`, 'POST'),
  extend: (id: string, expiresAt: string) => call<License>(`/v1/admin/licenses/${e(id)}/extend`, 'POST', { json: { expiresAt } }),
  // design §15.2 (M5d1): the signed document (§8.5) is saved as the service sent it, never parsed and written again.
  licenseFile: async (id: string) => (await send(`/v1/admin/licenses/${e(id)}/file`, 'POST')).blob(),
  products: async (): Promise<Map<string, Product>> => new Map((await all<Product>('/v1/admin/products')).map((p) => [p.id, p])),
  policies: (productId: string) => all<Policy>(`/v1/admin/policies?productId=${e(productId)}`),
  policy: (id: string) => call<Policy>(`/v1/admin/policies/${e(id)}`),
  features: (productId: string) => all<ProductFeature>(`/v1/admin/products/${e(productId)}/features`),
  activations: (licenseId: string, after: string | null) =>
    call<Page<Activation>>(`/v1/admin/licenses/${e(licenseId)}/activations?limit=50${after ? `&after=${e(after)}` : ''}`),
  deactivate: (id: string) => call<void>(`/v1/admin/activations/${e(id)}/deactivate`, 'POST'),
  /** Sends an offline request file; returns the answer's bytes (200) or null when the service only took it (204). */
  offline: async (file: Blob): Promise<Blob | null> => {
    const response = await send('/v1/admin/offline', 'POST', { blob: file });
    return response.status === 204 ? null : response.blob();
  },
  audit: (after: number | null) => call<AuditPage>(`/v1/admin/audit?order=desc&limit=50${after === null ? '' : `&after=${after}`}`),
  verifyAudit: () => call<Verified>('/v1/admin/audit/verify', 'POST'),
  logout: async () => {
    const answer = await call<{ endSession: string | null }>('/auth/logout', 'POST');
    location.assign(answer.endSession ?? '/');
  },
};

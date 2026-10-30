// The calls of the admin UI to the administration API (design §14.5, §15.2 "M5c の形", §15.3).
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
export type Page<T> = { items: T[]; next: string | null };
export type Product = { id: string; code: string; name: string };
export type Me = { id: string; issuer: string; subject: string; role: string; permissions: string[] };
export type Filter = { state: string; q: string; keyHash: string };

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

// fetch keeps its default mode (cors): with same-origin or no-cors, Referrer-Policy no-referrer sends Origin: null,
// which the service refuses with 403 (design §15.3).
async function call<T>(path: string, method = 'GET'): Promise<T> {
  const headers: Record<string, string> = { Accept: 'application/json' };
  if (method !== 'GET') {
    headers['X-ELM-CSRF'] = '1';
  }

  const response = await fetch(path, { method, headers, credentials: 'same-origin' });
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

export const api = {
  me: async () => {
    const me = await call<Me>('/v1/admin/me');
    signedIn();
    return me;
  },
  licenses: (filter: Filter, after: string | null) => call<Page<License>>(`/v1/admin/licenses?${listQuery(filter, after)}`),
  license: (id: string) => call<License>(`/v1/admin/licenses/${e(id)}`),
  revoke: (id: string) => call<License>(`/v1/admin/licenses/${e(id)}/revoke`, 'POST'),
  products: async (): Promise<Map<string, Product>> => {
    // design §15.2: every page, kept for the list's view.
    const products = new Map<string, Product>();
    let after: string | null = null;
    do {
      const page: Page<Product> = await call<Page<Product>>(`/v1/admin/products?limit=200${after ? `&after=${e(after)}` : ''}`);
      page.items.forEach((p) => products.set(p.id, p));
      after = page.next;
    } while (after);
    return products;
  },
  logout: async () => {
    const answer = await call<{ endSession: string | null }>('/auth/logout', 'POST');
    location.assign(answer.endSession ?? '/');
  },
};

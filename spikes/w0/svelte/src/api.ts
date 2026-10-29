// The calls of the prototype to the administration API (design §14.5); the same file in every candidate.
export type License = { id: string; name: string; state: string; expiresAt: string | null };
export type Page = { items: License[]; next: string | null };

export class Problem extends Error {
  readonly code: string;
  constructor(code: string, detail: string) {
    super(detail);
    this.code = code;
  }
}

async function call<T>(path: string, method = 'GET'): Promise<T> {
  const headers: Record<string, string> = { Accept: 'application/json' };
  if (method !== 'GET') headers['X-ELM-CSRF'] = '1';
  const response = await fetch(path, { method, headers, credentials: 'same-origin' });
  if (response.status === 401) {
    location.assign('/auth/login');
    throw new Problem('ELM_UNAUTHORIZED', 'Signing in.');
  }
  if (!response.ok) {
    const problem = await response.json().catch(() => null);
    throw new Problem(problem?.code ?? 'ELM_INTERNAL_ERROR', problem?.detail ?? `The service answered ${response.status}.`);
  }
  return (response.status === 204 ? undefined : await response.json()) as T;
}

const q = encodeURIComponent;
export const api = {
  list: (query: string, after: string | null) =>
    call<Page>(`/v1/admin/licenses?limit=20${query ? `&q=${q(query)}` : ''}${after ? `&after=${q(after)}` : ''}`),
  get: (id: string) => call<License>(`/v1/admin/licenses/${q(id)}`),
  revoke: (id: string) => call<void>(`/v1/admin/licenses/${q(id)}/revoke`, 'POST'),
};

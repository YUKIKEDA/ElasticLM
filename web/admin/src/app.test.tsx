import { createHash } from 'node:crypto';
import { render } from 'preact';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { api, listQuery, SignInNeeded } from './api';
import { App, route } from './app';
import { keyHash, normalizeKey } from './keys';

// The admin UI's screens and calls (design §15.2 "M5c の形"), against a fetch that the test answers.

type Answer = { status: number; body?: unknown };
type Sent = { url: string; method: string; headers: Record<string, string>; mode: RequestMode | undefined; credentials: RequestCredentials | undefined };

let answers: Map<string, Answer>;
let sent: Sent[];
let assigned: string[];

function answer(method: string, path: string, status: number, body?: unknown) {
  answers.set(`${method} ${path}`, { status, body });
}

beforeEach(() => {
  answers = new Map();
  sent = [];
  assigned = [];
  sessionStorage.clear();
  location.hash = '';
  vi.stubGlobal('fetch', async (url: string, init: RequestInit = {}) => {
    const method = init.method ?? 'GET';
    sent.push({ url, method, headers: (init.headers ?? {}) as Record<string, string>, mode: init.mode, credentials: init.credentials });
    const found = answers.get(`${method} ${url}`) ?? { status: 404, body: { code: 'ELM_NOT_FOUND', detail: 'Not answered by the test.' } };
    return new Response(found.body === undefined ? null : JSON.stringify(found.body), {
      status: found.status,
      headers: { 'Content-Type': 'application/json' },
    });
  });
  vi.spyOn(location, 'assign').mockImplementation((url: string | URL) => {
    assigned.push(String(url));
  });
});

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
  document.body.innerHTML = '';
});

async function settle() {
  for (let i = 0; i < 10; i++) {
    await new Promise((r) => setTimeout(r, 0));
  }
}

const owner = { id: 'a1', issuer: 'https://idp.example', subject: 'alice', role: 'owner', permissions: ['license.read', 'license.revoke', 'product.read'] };
const viewer = { ...owner, subject: 'vic', role: 'viewer', permissions: ['license.read', 'product.read'] };
const license = {
  id: 'l1',
  productId: 'p1',
  policyId: 'y1',
  name: 'Contoso <script>alert(1)</script><img src=x onerror=alert(2)>',
  features: [{ code: 'pro', ver: null, seats: null, expiresAt: null }],
  maxActivations: null,
  state: 'active',
  createdAt: '2026-10-01T00:00:00Z',
  expiresAt: null,
  suspendedAt: null,
  revokedAt: null,
  meta: { customer: '<b>bold</b>' },
  deactivations: 0,
};
const firstPage = '/v1/admin/licenses?limit=50';

function mount() {
  const host = document.createElement('div');
  document.body.append(host);
  render(<App />, host);
  return host;
}

describe('route', () => {
  /*
   * Preconditions: none.
   * Steps: read "", "#/", "#/licenses", "#/licenses/l%201", "#/other".
   * Expected: the list three times, the license "l 1", unknown.
   */
  it('reads the hash as the list, a license, or unknown', () => {
    expect([route(''), route('#/'), route('#/licenses')]).toEqual([{ kind: 'list' }, { kind: 'list' }, { kind: 'list' }]);
    expect(route('#/licenses/l%201')).toEqual({ kind: 'license', id: 'l 1' });
    expect(route('#/other')).toEqual({ kind: 'unknown' });
  });
});

describe('keys', () => {
  /*
   * Preconditions: a key written with dashes and lower case.
   * Steps: normalize it and a text that is not a key; hash the key.
   * Expected: the upper-case form without dashes; null; the base64url SHA-256 of the normal form (as node:crypto makes it).
   */
  it('hashes the normal form of a key and refuses other text', async () => {
    const typed = 'elm-7q2kx-m9d4r-hv0t8-3npwz';
    expect(normalizeKey(typed)).toBe('ELM7Q2KXM9D4RHV0T83NPWZ');
    expect(normalizeKey('ELM-7Q2KX-M9D4R-HV0T8-3NPWU')).toBeNull();
    expect(await keyHash('not a key')).toBeNull();
    const expected = createHash('sha256').update('ELM7Q2KXM9D4RHV0T83NPWZ').digest('base64url');
    expect(await keyHash(typed)).toBe(expected);
    expect(expected).toHaveLength(43);
  });
});

describe('api', () => {
  /*
   * Preconditions: none.
   * Steps: make the list query with every filter and a cursor, and with none.
   * Expected: limit, state, q, keyHash, after, each encoded; only limit without filters.
   */
  it('makes the list query from the filters that are set', () => {
    expect(listQuery({ state: 'active', q: 'a&b', keyHash: 'h_-' }, 'c1')).toBe('limit=50&state=active&q=a%26b&keyHash=h_-&after=c1');
    expect(listQuery({ state: '', q: '', keyHash: '' }, null)).toBe('limit=50');
  });

  /*
   * Preconditions: the service answers revoke with the license.
   * Steps: revoke; read a license.
   * Expected: the POST carries X-ELM-CSRF: 1 and the GET does not; neither sets fetch's mode (the default, cors, sends
   * Origin); both send credentials same-origin.
   */
  it('sends the CSRF header on POST only and keeps fetch default mode', async () => {
    answer('POST', '/v1/admin/licenses/l1/revoke', 200, license);
    answer('GET', '/v1/admin/licenses/l1', 200, license);
    await api.revoke('l1');
    await api.license('l1');
    expect(sent.map((s) => [s.method, s.headers['X-ELM-CSRF'], s.mode, s.credentials])).toEqual([
      ['POST', '1', undefined, 'same-origin'],
      ['GET', undefined, undefined, 'same-origin'],
    ]);
  });

  /*
   * Preconditions: the service answers 401.
   * Steps: read a license twice.
   * Expected: the first moves to /auth/login once; the second throws SignInNeeded without moving again.
   */
  it('moves to the sign-in once and then stops', async () => {
    answer('GET', '/v1/admin/licenses/l1', 401, { code: 'ELM_UNAUTHORIZED', detail: 'No session.' });
    await expect(api.license('l1')).rejects.toThrow('Signing in');
    await expect(api.license('l1')).rejects.toBeInstanceOf(SignInNeeded);
    expect(assigned).toEqual(['/auth/login']);
  });
});

describe('App', () => {
  /*
   * Preconditions: alice is an owner; one license whose name holds a script element and an img with onerror.
   * Steps: open the list.
   * Expected: the row shows the name as text and the product's name; no script or img element is made.
   */
  it('lists licenses and draws their names as text', async () => {
    answer('GET', '/v1/admin/me', 200, owner);
    answer('GET', '/v1/admin/products?limit=200', 200, { items: [{ id: 'p1', code: 'app', name: 'The App' }], next: null });
    answer('GET', firstPage, 200, { items: [license], next: null });
    const host = mount();
    await settle();
    expect(host.querySelector('tbody td a')?.textContent).toBe(license.name);
    expect(host.querySelector('tbody td:nth-child(2)')?.textContent).toBe('The App');
    expect(host.querySelectorAll('script, img')).toHaveLength(0);
    expect(host.querySelector('header')?.textContent).toContain('alice (owner)');
  });

  /*
   * Preconditions: the caller has no product.read (products answers 403).
   * Steps: open the list.
   * Expected: the product column shows the product's ID.
   */
  it('shows the product ID when products cannot be read', async () => {
    answer('GET', '/v1/admin/me', 200, viewer);
    answer('GET', '/v1/admin/products?limit=200', 403, { code: 'ELM_FORBIDDEN', detail: 'No.' });
    answer('GET', firstPage, 200, { items: [license], next: null });
    const host = mount();
    await settle();
    expect(host.querySelector('tbody td:nth-child(2)')?.textContent).toBe('p1');
  });

  /*
   * Preconditions: an owner; the list.
   * Steps: type a key, a name, and a state, and send the form.
   * Expected: the request carries keyHash (the key's SHA-256), q, and state, and never the key.
   */
  it('searches by the key hash, never the key', async () => {
    answer('GET', '/v1/admin/me', 200, owner);
    answer('GET', '/v1/admin/products?limit=200', 200, { items: [], next: null });
    answer('GET', firstPage, 200, { items: [], next: null });
    const host = mount();
    await settle();
    const form = host.querySelector('form')!;
    (form.querySelector('input[name=q]') as HTMLInputElement).value = 'Contoso';
    (form.querySelector('input[name=key]') as HTMLInputElement).value = 'ELM-7Q2KX-M9D4R-HV0T8-3NPWZ';
    (form.querySelector('select[name=state]') as HTMLSelectElement).value = 'active';
    form.dispatchEvent(new Event('submit', { cancelable: true }));
    await settle();
    const search = sent.map((s) => s.url).find((u) => u.includes('keyHash='))!;
    const hash = createHash('sha256').update('ELM7Q2KXM9D4RHV0T83NPWZ').digest('base64url');
    expect(search).toBe(`/v1/admin/licenses?limit=50&state=active&q=Contoso&keyHash=${hash}`);
    expect(sent.some((s) => s.url.includes('7Q2KX'))).toBe(false);
  });

  /*
   * Preconditions: an owner; a license.
   * Steps: open the license; press Revoke; confirm.
   * Expected: the confirmation names the license; one POST revoke; the revoked state and message are shown; meta is in a
   * pre as JSON text.
   */
  it('revokes after a confirmation that names the license', async () => {
    answer('GET', '/v1/admin/me', 200, owner);
    answer('GET', '/v1/admin/licenses/l1', 200, license);
    answer('POST', '/v1/admin/licenses/l1/revoke', 200, { ...license, state: 'revoked', revokedAt: '2026-10-02T00:00:00Z' });
    location.hash = '#/licenses/l1';
    const host = mount();
    await settle();
    expect(host.querySelector('pre')?.textContent).toContain('"customer": "<b>bold</b>"');
    expect(host.querySelectorAll('b')).toHaveLength(0);
    const revoke = [...host.querySelectorAll('button')].find((b) => b.textContent === 'Revoke')!;
    revoke.click();
    await settle();
    expect(host.textContent).toContain(`Revoke the license "${license.name}"?`);
    [...host.querySelectorAll('button')].find((b) => b.textContent === 'Revoke')!.click();
    await settle();
    expect(sent.filter((s) => s.method === 'POST').map((s) => s.url)).toEqual(['/v1/admin/licenses/l1/revoke']);
    expect(host.textContent).toContain('The license is revoked.');
  });

  /*
   * Preconditions: a viewer, without license.revoke; a license.
   * Steps: open the license.
   * Expected: no Revoke button.
   */
  it('shows no revoke button without the permission', async () => {
    answer('GET', '/v1/admin/me', 200, viewer);
    answer('GET', '/v1/admin/licenses/l1', 200, license);
    location.hash = '#/licenses/l1';
    const host = mount();
    await settle();
    expect(host.querySelector('h1')?.textContent).toBe(license.name);
    expect([...host.querySelectorAll('button')].some((b) => b.textContent === 'Revoke')).toBe(false);
  });

  /*
   * Preconditions: the sign-in was already tried in this tab; the service answers 401.
   * Steps: open the screen.
   * Expected: no move; the reason and a Sign in button are shown.
   */
  it('stops with a sign-in button after one try', async () => {
    sessionStorage.setItem('elm-sign-in-tried', '1');
    answer('GET', '/v1/admin/me', 401, { code: 'ELM_UNAUTHORIZED', detail: 'No session.' });
    const host = mount();
    await settle();
    expect(assigned).toEqual([]);
    expect([...host.querySelectorAll('button')].map((b) => b.textContent)).toEqual(['Sign in']);
  });

  /*
   * Preconditions: an owner; an unknown route.
   * Steps: open "#/other".
   * Expected: the not-found text.
   */
  it('says when a route does not exist', async () => {
    answer('GET', '/v1/admin/me', 200, owner);
    location.hash = '#/other';
    const host = mount();
    await settle();
    expect(host.textContent).toContain('This page does not exist.');
  });
});

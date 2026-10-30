import { createHash } from 'node:crypto';
import { render } from 'preact';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { api, listQuery, SignInNeeded, type License, type Policy } from './api';
import { apiKeyRequest, keyStatus } from './apikeys';
import { App, route } from './app';
import { answerName, save, scalars, utcOf } from './files';
import { licenseRequest } from './issue';
import { keyHash, normalizeKey } from './keys';

// The admin UI's screens and calls (design §15.2 "M5c の形", "M5d1 の形", and "M5d2 の形"), against a fetch that the test
// answers.

// raw: an answer body sent as these bytes, not as JSON; lost: no answer at all (fetch rejects, as on a dropped connection).
type Answer = { status: number; body?: unknown; raw?: string; lost?: boolean };
type Sent = {
  url: string;
  method: string;
  headers: Record<string, string>;
  mode: RequestMode | undefined;
  credentials: RequestCredentials | undefined;
  body: BodyInit | null | undefined;
};

let answers: Map<string, Answer>;
let sent: Sent[];
let assigned: string[];
let mounted: HTMLElement[] = [];

function answer(method: string, path: string, status: number, body?: unknown, more: Partial<Answer> = {}) {
  answers.set(`${method} ${path}`, { status, body, ...more });
}

beforeEach(() => {
  answers = new Map();
  sent = [];
  assigned = [];
  sessionStorage.clear();
  location.hash = '';
  vi.stubGlobal('fetch', async (url: string, init: RequestInit = {}) => {
    const method = init.method ?? 'GET';
    sent.push({ url, method, headers: (init.headers ?? {}) as Record<string, string>, mode: init.mode, credentials: init.credentials, body: init.body });
    const found = answers.get(`${method} ${url}`) ?? { status: 404, body: { code: 'ELM_NOT_FOUND', detail: 'Not answered by the test.' } };
    if (found.lost) {
      throw new TypeError('Failed to fetch');
    }

    return new Response(found.raw ?? (found.body === undefined ? null : JSON.stringify(found.body)), {
      status: found.status,
      headers: { 'Content-Type': 'application/json' },
    });
  });
  vi.spyOn(location, 'assign').mockImplementation((url: string | URL) => {
    assigned.push(String(url));
  });
});

afterEach(() => {
  vi.useRealTimers();
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
  // An App left mounted would still follow the hash and call fetch in the next test.
  mounted.forEach((host) => render(null, host));
  mounted = [];
  document.body.innerHTML = '';
});

async function settle() {
  for (let i = 0; i < 10; i++) {
    await new Promise((r) => setTimeout(r, 0));
  }
}

const owner = { id: 'a1', issuer: 'https://idp.example', subject: 'alice', role: 'owner', permissions: ['license.read', 'license.revoke', 'product.read'] };
const viewer = { ...owner, subject: 'vic', role: 'viewer', permissions: ['license.read', 'product.read'] };
const license: License = {
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
  mounted.push(host);
  return host;
}

describe('route', () => {
  /*
   * Preconditions: none.
   * Steps: read "", "#/", "#/licenses", "#/licenses/l%201", "#/other", "#/licenses/%", "#/licenses/%E0".
   * Expected: the list three times, the license "l 1", unknown three times (a broken encoding is unknown, not an error).
   */
  it('reads the hash as the list, a license, or unknown', () => {
    expect([route(''), route('#/'), route('#/licenses')]).toEqual([{ kind: 'list' }, { kind: 'list' }, { kind: 'list' }]);
    expect(route('#/licenses/l%201')).toEqual({ kind: 'license', id: 'l 1' });
    expect(route('#/other')).toEqual({ kind: 'unknown' });
    expect(route('#/licenses/%')).toEqual({ kind: 'unknown' });
    expect(route('#/licenses/%E0')).toEqual({ kind: 'unknown' });
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

// The daily screens of M5d1 (design §15.2 "M5d1 の形").

const admin = {
  ...owner,
  subject: 'ada',
  role: 'admin',
  permissions: [
    'license.read',
    'license.create',
    'license.update',
    'license.revoke',
    'license.file',
    'product.read',
    'policy.read',
    'activation.read',
    'activation.deactivate',
    'activation.offline',
    'audit.read',
  ],
};
const standard: Policy = {
  id: 'y1',
  productId: 'p1',
  name: 'Standard',
  licenseKind: 'standard',
  binding: 'unbound',
  expirationBasis: 'fromIssue',
  duration: 365 * 86400,
  floating: 'none',
};
const server: Policy = { ...standard, id: 'y2', name: 'Server', licenseKind: 'server', binding: 'nodeLocked' };
const firstUse: Policy = { ...standard, id: 'y3', name: 'First use', expirationBasis: 'fromFirstActivation', duration: null };
const trial: Policy = { ...standard, id: 'y4', name: 'Trial', licenseKind: 'trial' };
const features = [
  { id: 'f1', productId: 'p1', code: 'pro', name: 'Pro' },
  { id: 'f2', productId: 'p1', code: 'max', name: 'Max' },
];
const productsPath = '/v1/admin/products?limit=200';
const policiesPath = '/v1/admin/policies?productId=p1&limit=200';
const featuresPath = '/v1/admin/products/p1/features?limit=200';
const activationsPath = '/v1/admin/licenses/l1/activations?limit=50';

const button = (host: HTMLElement, text: string) => [...host.querySelectorAll('button')].find((b) => b.textContent === text);
const buttonTexts = (host: HTMLElement) => [...host.querySelectorAll('button')].map((b) => b.textContent);
const field = (host: HTMLElement, name: string) => host.querySelector(`[name="${name}"]`) as HTMLInputElement;
const posts = () => sent.filter((s) => s.method === 'POST');

function choose(select: HTMLSelectElement, value: string) {
  select.value = value;
  select.dispatchEvent(new Event('change'));
}

function submit(form: HTMLFormElement) {
  form.dispatchEvent(new Event('submit', { cancelable: true }));
}

function form(entries: [string, string][]): FormData {
  const data = new FormData();
  entries.forEach(([k, v]) => data.append(k, v));
  return data;
}

// Every saved file: its name, its bytes, and how many object URLs were released when its link was clicked.
let saved: { name: string; blob: Blob; releasedAtClick: number }[];
let released: string[];

function catchSaves() {
  saved = [];
  released = [];
  const blobs = new Map<string, Blob>();
  vi.spyOn(URL, 'createObjectURL').mockImplementation((blob: Blob | MediaSource) => {
    const url = `blob:test/${blobs.size}`;
    blobs.set(url, blob as Blob);
    return url;
  });
  vi.spyOn(URL, 'revokeObjectURL').mockImplementation((url: string) => {
    released.push(url);
  });
  vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(function (this: HTMLAnchorElement) {
    saved.push({ name: this.download, blob: blobs.get(this.getAttribute('href')!)!, releasedAtClick: released.length });
  });
}

describe('routes and permissions', () => {
  /*
   * Preconditions: none.
   * Steps: read "#/issue", "#/offline", "#/audit", "#/issue/x".
   * Expected: the issue, offline, and audit routes; unknown for the last.
   */
  it('reads the daily routes', () => {
    expect([route('#/issue'), route('#/offline'), route('#/audit')]).toEqual([{ kind: 'issue' }, { kind: 'offline' }, { kind: 'audit' }]);
    expect(route('#/issue/x')).toEqual({ kind: 'unknown' });
  });

  /*
   * Preconditions: a viewer with license.read, product.read, and policy.read, but no license.create, activation.offline,
   * or audit.read.
   * Steps: open "#/issue", then "#/offline", then "#/audit".
   * Expected: the header links only the list; each route says the role does not allow it, and nothing but /v1/admin/me
   * is called.
   */
  it('refuses a route without its permissions before calling the API', async () => {
    answer('GET', '/v1/admin/me', 200, { ...viewer, permissions: ['license.read', 'product.read', 'policy.read'] });
    location.hash = '#/issue';
    const host = mount();
    await settle();
    expect([...host.querySelectorAll('nav a')].map((a) => a.textContent)).toEqual(['Licenses']);
    expect(host.textContent).toContain('Your role does not allow this.');
    for (const hash of ['#/offline', '#/audit']) {
      location.hash = hash;
      await settle();
      expect(host.textContent).toContain('Your role does not allow this.');
    }

    expect(sent.map((s) => s.url)).toEqual(['/v1/admin/me']);
  });

  /*
   * Preconditions: an admin with every daily permission.
   * Steps: open the list.
   * Expected: the header links the list, issue, offline, and the audit log.
   */
  it('links every screen the role allows', async () => {
    answer('GET', '/v1/admin/me', 200, admin);
    answer('GET', productsPath, 200, { items: [], next: null });
    answer('GET', firstPage, 200, { items: [], next: null });
    const host = mount();
    await settle();
    expect([...host.querySelectorAll('nav a')].map((a) => a.getAttribute('href'))).toEqual(['#/licenses', '#/issue', '#/offline', '#/audit']);
  });
});

describe('files', () => {
  /*
   * Preconditions: none.
   * Steps: read datetime-local values with and without seconds and other text; name answers; count scalars.
   * Expected: the API's UTC form with ":00" added when seconds are missing, null otherwise; acme.elmreq gives
   * acme.elmresp and other names get .elmresp added; 256 emoji count as 256.
   */
  it('reads times as UTC, names answers, and counts scalars', () => {
    expect(utcOf('2026-10-27T09:30')).toBe('2026-10-27T09:30:00Z');
    expect(utcOf('2026-10-27T09:30:15')).toBe('2026-10-27T09:30:15Z');
    expect([utcOf(''), utcOf('2026-10-27'), utcOf('2026-10-27T09:30Z')]).toEqual([null, null, null]);
    expect(answerName('acme.elmreq')).toBe('acme.elmresp');
    expect(answerName('acme.txt')).toBe('acme.txt.elmresp');
    expect(scalars('😀'.repeat(256))).toBe(256);
  });

  /*
   * Preconditions: object URLs and link clicks are caught.
   * Steps: save bytes as a.elmlic; look at the released URLs when save returns, then after the next task.
   * Expected: one click on a link named a.elmlic; no URL released when save returns; one released after the next task.
   */
  it('releases the object URL only after the click was handled', async () => {
    catchSaves();
    save(new Blob(['bytes']), 'a.elmlic');
    expect([saved.map((f) => f.name), released.length]).toEqual([['a.elmlic'], 0]);
    await settle();
    expect(released).toHaveLength(1);
  });
});

describe('licenseRequest', () => {
  /*
   * Preconditions: the standard policy; the features pro and max.
   * Steps: read a form with a name of 256 emoji, pro with a version and an expiry, most activations 5, an expiry, two
   * meta pairs (one "__proto__") and an empty pair; then the same with 257 emoji, with no feature, and with 257 features.
   * Expected: the request with the UTC times, null seats, and both meta keys as own keys (the empty pair left out); then
   * the name, feature-count refusals.
   */
  it('reads the form into the request and checks the name and the features', () => {
    const name = '😀'.repeat(256);
    const read = licenseRequest(
      form([
        ['name', name],
        ['use.pro', '1'],
        ['ver.pro', ' >=2.0 <3.0 '],
        ['expires.pro', '2027-01-01T00:00'],
        ['maxActivations', '5'],
        ['expiresAt', '2027-06-01T12:00'],
        ['metaKey', 'customer'],
        ['metaValue', 'Contoso'],
        ['metaKey', '__proto__'],
        ['metaValue', 'kept'],
        ['metaKey', ''],
        ['metaValue', ''],
      ]),
      standard,
      features,
    );
    expect(read).toEqual({
      policyId: 'y1',
      name,
      features: [{ code: 'pro', ver: '>=2.0 <3.0', seats: null, expiresAt: '2027-01-01T00:00:00Z' }],
      maxActivations: 5,
      expiresAt: '2027-06-01T12:00:00Z',
      meta: expect.anything(),
    });
    expect(JSON.stringify((read as { meta: unknown }).meta)).toBe('{"customer":"Contoso","__proto__":"kept"}');
    expect(licenseRequest(form([['name', '😀'.repeat(257)], ['use.pro', '1']]), standard, features)).toBe('The name must be 1 to 256 characters.');
    expect(licenseRequest(form([['name', 'A']]), standard, features)).toBe('Grant 1 to 256 features.');
    const many = Array.from({ length: 257 }, (_, i) => ({ id: `f${i}`, productId: 'p1', code: `c${i}`, name: `C${i}` }));
    expect(licenseRequest(form([['name', 'A'], ...many.map((f): [string, string] => [`use.${f.code}`, '1'])]), standard, many)).toBe(
      'Grant 1 to 256 features.',
    );
  });

  /*
   * Preconditions: the server policy (seats per feature); the standard policy.
   * Steps: read pro with no seats, 0, 1000001, and 1000000 seats; read meta with an empty key, then with a key twice;
   * read most activations 0; read an expiry that is not a date and time.
   * Expected: the seats refusal three times, then seats 1000000; the empty-key and twice-key refusals; the most-activations
   * refusal; the time refusal.
   */
  it('refuses seats, meta keys, most activations, and times the API would refuse', () => {
    const seats = (count: string) => licenseRequest(form([['name', 'A'], ['use.pro', '1'], ['seats.pro', count]]), server, features);
    for (const count of ['', '0', '1000001']) expect(seats(count)).toBe('Seats of "pro" must be a whole number from 1 to 1,000,000.');
    expect((seats('1000000') as { features: { seats: number }[] }).features[0].seats).toBe(1_000_000);
    const base: [string, string][] = [['name', 'A'], ['use.pro', '1']];
    expect(licenseRequest(form([...base, ['metaKey', ''], ['metaValue', 'v']]), standard, features)).toBe('A meta pair has an empty key.');
    expect(licenseRequest(form([...base, ['metaKey', 'k'], ['metaValue', '1'], ['metaKey', 'k'], ['metaValue', '2']]), standard, features)).toBe(
      'The meta key "k" is given twice.',
    );
    expect(licenseRequest(form([...base, ['maxActivations', '0']]), standard, features)).toBe('Most activations must be a whole number from 1 to 100,000.');
    expect(licenseRequest(form([...base, ['expiresAt', 'tomorrow']]), standard, features)).toBe('Enter a date and a time.');
  });
});

describe('Issue', () => {
  async function openIssue(policies: Policy[]) {
    answer('GET', '/v1/admin/me', 200, admin);
    answer('GET', productsPath, 200, { items: [{ id: 'p1', code: 'app', name: 'The App' }], next: null });
    answer('GET', policiesPath, 200, { items: policies, next: null });
    answer('GET', featuresPath, 200, { items: features, next: null });
    location.hash = '#/issue';
    const host = mount();
    await settle();
    choose(field(host, 'product') as unknown as HTMLSelectElement, 'p1');
    await settle();
    return host;
  }

  /*
   * Preconditions: the product has the standard policy and a trial policy.
   * Steps: open the issue screen and choose the product.
   * Expected: the policy list offers Standard only.
   */
  it('leaves trial policies out', async () => {
    const host = await openIssue([standard, trial]);
    expect([...host.querySelectorAll('select[name=policy] option')].map((o) => o.textContent)).toEqual(['Choose…', 'Standard']);
  });

  /*
   * Preconditions: the product has only a trial policy.
   * Steps: open the issue screen and choose the product.
   * Expected: the reason is shown and the Review button is off.
   */
  it('says why a product without a policy cannot be issued', async () => {
    const host = await openIssue([trial]);
    expect(host.textContent).toContain('This product has no policy for a license that is not a trial.');
    expect(button(host, 'Review')!.disabled).toBe(true);
  });

  /*
   * Preconditions: the time is 2026-10-30T00:00:00Z; the product has the server, first-use, and standard policies.
   * Steps: choose each policy in turn.
   * Expected: the server policy shows a seats field per feature; the first-use policy shows no time field and says why;
   * the standard policy shows no seats field and the expected default expiry 2027-10-30T00:00:00Z.
   */
  it('shapes the fields by the policy', async () => {
    vi.useFakeTimers({ toFake: ['Date'] });
    vi.setSystemTime(new Date('2026-10-30T00:00:00Z'));
    const host = await openIssue([server, firstUse, standard]);
    const policy = field(host, 'policy') as unknown as HTMLSelectElement;
    choose(policy, 'y2');
    await settle();
    expect(host.querySelectorAll('input[name^="seats."]')).toHaveLength(2);
    choose(policy, 'y3');
    await settle();
    expect(host.querySelectorAll('input[type=datetime-local]')).toHaveLength(0);
    expect(host.textContent).toContain('This policy starts the expiry at the first activation.');
    choose(policy, 'y1');
    await settle();
    expect(host.querySelectorAll('input[name^="seats."]')).toHaveLength(0);
    expect(host.textContent).toContain('about 2027-10-30T00:00:00Z');
  });

  /*
   * Preconditions: the standard policy; the service answers the creation with the license and its key; the clipboard
   * refuses to write.
   * Steps: fill the form (name, pro, most activations 7, one meta pair); Review; press Create twice in the same task;
   * press Copy.
   * Expected: the confirmation shows most activations 7; one POST with the request as JSON; the key in a read-only field;
   * the key is in neither storage nor the URL; the copy failure says to select and copy.
   */
  it('confirms, sends once, and shows the key in memory only', async () => {
    const key = 'ELM-7Q2KX-M9D4R-HV0T8-3NPWZ';
    answer('POST', '/v1/admin/licenses', 201, { ...license, id: 'l9', name: 'Contoso', key });
    const host = await openIssue([standard]);
    choose(field(host, 'policy') as unknown as HTMLSelectElement, 'y1');
    await settle();
    button(host, 'Add a pair')!.click();
    await settle();
    field(host, 'name').value = 'Contoso';
    field(host, 'use.pro').checked = true;
    field(host, 'maxActivations').value = '7';
    field(host, 'metaKey').value = 'customer';
    field(host, 'metaValue').value = 'C-1';
    submit(host.querySelector('form')!);
    await settle();
    expect(host.querySelector('.review')?.textContent).toContain('Most activations7');
    vi.stubGlobal('navigator', { clipboard: { writeText: () => Promise.reject(new Error('denied')) } });
    const create = button(host, 'Create the license')!;
    create.click();
    create.click();
    await settle();
    expect(posts().map((s) => [s.url, s.headers['Content-Type'], s.body])).toEqual([
      [
        '/v1/admin/licenses',
        'application/json',
        JSON.stringify({
          policyId: 'y1',
          name: 'Contoso',
          features: [{ code: 'pro', ver: null, seats: null, expiresAt: null }],
          maxActivations: 7,
          expiresAt: null,
          meta: { customer: 'C-1' },
        }),
      ],
    ]);
    const shown = host.querySelector('input.key') as HTMLInputElement;
    expect([shown.value, shown.readOnly]).toEqual([key, true]);
    expect(JSON.stringify({ ...sessionStorage }) + JSON.stringify({ ...localStorage }) + location.href).not.toContain('7Q2KX');
    button(host, 'Copy the key')!.click();
    await settle();
    expect(host.textContent).toContain('The browser did not copy the key. Select it and copy it.');
  });

  /*
   * Preconditions: the standard policy; the creation gets no answer (the connection drops).
   * Steps: fill the form, Review, Create.
   * Expected: one POST; the screen says to search the list before sending again; the confirmation stays.
   */
  it('does not send again when no answer came', async () => {
    answer('POST', '/v1/admin/licenses', 0, undefined, { lost: true });
    const host = await openIssue([standard]);
    choose(field(host, 'policy') as unknown as HTMLSelectElement, 'y1');
    await settle();
    field(host, 'name').value = 'Contoso';
    field(host, 'use.pro').checked = true;
    submit(host.querySelector('form')!);
    await settle();
    button(host, 'Create the license')!.click();
    await settle();
    expect(posts()).toHaveLength(1);
    expect(host.textContent).toContain('Search the list for the name before you send again');
    expect(button(host, 'Create the license')).toBeDefined();
  });
});

describe('Detail operations', () => {
  const dated = { ...license, name: 'Contoso', expiresAt: '2027-01-01T00:00:00Z' };

  async function openLicense(me: typeof owner, shown: License, policy: Policy = standard) {
    answer('GET', '/v1/admin/me', 200, me);
    answer('GET', '/v1/admin/licenses/l1', 200, shown);
    answer('GET', '/v1/admin/policies/y1', 200, policy);
    answer('GET', activationsPath, 200, { items: [], next: null });
    location.hash = '#/licenses/l1';
    const host = mount();
    await settle();
    return host;
  }

  /*
   * Preconditions: an admin; an active license with an expiry, of an unbound policy.
   * Steps: open the license.
   * Expected: Suspend, Save the license file, Revoke, and the extend form; no Resume.
   */
  it('offers what an active license allows', async () => {
    const host = await openLicense(admin, dated);
    expect(buttonTexts(host)).toEqual(['Sign out', 'Suspend', 'Save the license file', 'Revoke', 'Extend']);
  });

  /*
   * Preconditions: an admin; a suspended license with an expiry; the service answers resume with the active license.
   * Steps: open the license; press Resume.
   * Expected: Resume and Revoke only (no Suspend, file, or extend); one POST resume with no confirmation; the screen
   * shows the answer.
   */
  it('resumes a suspended license without asking', async () => {
    const suspended = { ...dated, state: 'suspended', suspendedAt: '2026-10-29T00:00:00Z' };
    answer('POST', '/v1/admin/licenses/l1/resume', 200, dated);
    const host = await openLicense(admin, suspended);
    expect(buttonTexts(host)).toEqual(['Sign out', 'Resume', 'Revoke']);
    button(host, 'Resume')!.click();
    await settle();
    expect(posts().map((s) => s.url)).toEqual(['/v1/admin/licenses/l1/resume']);
    expect(host.textContent).toContain('The license is active again.');
  });

  /*
   * Preconditions: an admin; an active license of a node-locked policy; then a viewer with license.read only.
   * Steps: open the license as each.
   * Expected: no license file button for the node-locked policy; the viewer sees no operation, and neither the policy
   * nor the activations are read.
   */
  it('hides the license file for a bound policy and every operation without permissions', async () => {
    let host = await openLicense(admin, dated, { ...standard, binding: 'nodeLocked' });
    expect(button(host, 'Save the license file')).toBeUndefined();
    render(null, host);
    document.body.innerHTML = '';
    sent = [];
    host = await openLicense({ ...viewer, permissions: ['license.read'] }, dated);
    expect(buttonTexts(host)).toEqual(['Sign out']);
    expect(sent.map((s) => s.url)).toEqual(['/v1/admin/me', '/v1/admin/licenses/l1']);
  });

  /*
   * Preconditions: an admin; an active license; the service refuses suspend with 409 because the license was revoked
   * meanwhile, and then reads it as revoked.
   * Steps: press Suspend, confirm.
   * Expected: the confirmation names the license; the detail of the 409 is shown; the license is read again and shows
   * revoked.
   */
  it('reads the license again after a refused change', async () => {
    const host = await openLicense(admin, dated);
    answer('POST', '/v1/admin/licenses/l1/suspend', 409, { code: 'ELM_CONFLICT', detail: 'A revoked license cannot be suspended.' });
    answer('GET', '/v1/admin/licenses/l1', 200, { ...dated, state: 'revoked', revokedAt: '2026-10-30T00:00:00Z' });
    button(host, 'Suspend')!.click();
    await settle();
    expect(host.textContent).toContain('Suspend the license "Contoso"?');
    button(host, 'Suspend')!.click();
    await settle();
    expect(host.textContent).toContain('A revoked license cannot be suspended.');
    expect(sent.filter((s) => s.url === '/v1/admin/licenses/l1')).toHaveLength(2);
    expect(host.querySelector('dl')?.textContent).toContain('revoked');
  });

  /*
   * Preconditions: the time is 2026-10-30T00:00:00Z; an admin; a license that expires 2027-01-01T00:00:00Z.
   * Steps: extend to 2026-12-01T00:00 (earlier than the expiry); then to 2027-06-01T09:30.
   * Expected: the first is refused on the screen with no POST; the second sends {"expiresAt":"2027-06-01T09:30:00Z"}.
   */
  it('extends to a later UTC time only', async () => {
    vi.useFakeTimers({ toFake: ['Date'] });
    vi.setSystemTime(new Date('2026-10-30T00:00:00Z'));
    answer('POST', '/v1/admin/licenses/l1/extend', 200, { ...dated, expiresAt: '2027-06-01T09:30:00Z' });
    const host = await openLicense(admin, dated);
    const extend = host.querySelector('form.extend') as HTMLFormElement;
    field(host, 'expiresAt').value = '2026-12-01T00:00';
    submit(extend);
    await settle();
    expect(host.textContent).toContain('The new expiry must be later than the current expiry and than now.');
    expect(posts()).toHaveLength(0);
    field(host, 'expiresAt').value = '2027-06-01T09:30';
    submit(extend);
    await settle();
    expect(posts().map((s) => s.body)).toEqual(['{"expiresAt":"2027-06-01T09:30:00Z"}']);
    expect(host.textContent).toContain('The expiry is changed.');
  });

  /*
   * Preconditions: an admin; an active license of an unbound policy; the service answers the license file with bytes
   * that a JSON parser would rewrite (spaces and a trailing newline).
   * Steps: press Save the license file.
   * Expected: one POST file with X-ELM-CSRF; the saved file is l1.elmlic with the same bytes; its object URL is released
   * after the click, not before.
   */
  it('saves the license file as the service sent it', async () => {
    catchSaves();
    const bytes = '{ "payload" :  "x",\n  "sig": "y" }\n';
    answer('POST', '/v1/admin/licenses/l1/file', 200, undefined, { raw: bytes });
    const host = await openLicense(admin, dated);
    button(host, 'Save the license file')!.click();
    await settle();
    expect(posts().map((s) => [s.url, s.headers['X-ELM-CSRF']])).toEqual([['/v1/admin/licenses/l1/file', '1']]);
    expect(saved.map((f) => [f.name, f.releasedAtClick])).toEqual([['l1.elmlic', 0]]);
    expect(await saved[0].blob.text()).toBe(bytes);
    expect(released).toHaveLength(1);
  });
});

describe('Activations', () => {
  /*
   * Preconditions: an admin; a license with one activation whose host, user, and app hold an img with onerror and a
   * script element; the service answers the deactivation with 204 and then lists no activation.
   * Steps: open the license; press Deactivate; confirm.
   * Expected: the values are text and no img or script element is made; the confirmation names the host as text; one
   * POST deactivate; the list is read again and is empty.
   */
  it('draws device values as text and deactivates after a confirmation', async () => {
    const host = '<img src=x onerror=alert(1)>';
    const activation = {
      id: 'a1',
      licenseId: 'l1',
      keyId: 'k1',
      client: { host, user: '<script>alert(2)</script>' },
      app: '<img src=y onerror=alert(3)>',
      seq: 3,
      blockedAt: null,
      createdAt: '2026-10-01T00:00:00Z',
      updatedAt: '2026-10-02T00:00:00Z',
    };
    answer('GET', '/v1/admin/me', 200, admin);
    answer('GET', '/v1/admin/licenses/l1', 200, { ...license, name: 'Contoso' });
    answer('GET', '/v1/admin/policies/y1', 200, standard);
    answer('GET', activationsPath, 200, { items: [activation], next: null });
    answer('POST', '/v1/admin/activations/a1/deactivate', 204);
    location.hash = '#/licenses/l1';
    const view = mount();
    await settle();
    expect(view.querySelector('table.activations')?.textContent).toContain(`${host} / <script>alert(2)</script>`);
    expect(view.querySelector('table.activations')?.textContent).toContain('<img src=y onerror=alert(3)>');
    expect(view.querySelectorAll('img, script')).toHaveLength(0);
    answer('GET', activationsPath, 200, { items: [], next: null });
    button(view, 'Deactivate')!.click();
    await settle();
    expect(view.querySelector('.confirm')?.textContent).toContain(`Deactivate the device "${host}"?`);
    expect(view.querySelectorAll('img, script')).toHaveLength(0);
    view.querySelector('.confirm button')!.dispatchEvent(new Event('click'));
    await settle();
    expect(posts().map((s) => s.url)).toEqual(['/v1/admin/activations/a1/deactivate']);
    expect(sent.filter((s) => s.url === activationsPath)).toHaveLength(2);
    expect(view.textContent).toContain('No device is activated.');
  });
});

describe('Offline', () => {
  async function openOffline() {
    answer('GET', '/v1/admin/me', 200, admin);
    location.hash = '#/offline';
    const host = mount();
    await settle();
    return host;
  }

  function pick(host: HTMLElement, file: File) {
    Object.defineProperty(field(host, 'file'), 'files', { value: [file], configurable: true });
    submit(host.querySelector('form')!);
  }

  /*
   * Preconditions: an admin.
   * Steps: pick a 65,537-byte request file and send it.
   * Expected: the size refusal; no POST.
   */
  it('refuses a file larger than 64 KiB', async () => {
    const host = await openOffline();
    pick(host, new File(['x'.repeat(65_537)], 'big.elmreq'));
    await settle();
    expect(host.textContent).toContain('The file is larger than 64 KiB (65,536 bytes).');
    expect(posts()).toHaveLength(0);
  });

  /*
   * Preconditions: an admin; the service answers the request file with answer bytes (200), then a deactivation proof
   * with 204.
   * Steps: send acme.elmreq (with spaces around its text); then send pc.elmdeact.
   * Expected: the first POST body is the picked file itself, as text/plain with X-ELM-CSRF; the answer is saved as
   * acme.elmresp with the same bytes; the second says the deactivation is recorded and saves nothing.
   */
  it('sends the file unchanged and saves the answer under the request name', async () => {
    catchSaves();
    const host = await openOffline();
    const request = new File(['  ELMREQ.body \n'], 'acme.elmreq');
    answer('POST', '/v1/admin/offline', 200, undefined, { raw: 'ELMRESP.bytes' });
    pick(host, request);
    await settle();
    expect(posts().map((s) => [s.body, s.headers['Content-Type'], s.headers['X-ELM-CSRF']])).toEqual([[request, 'text/plain', '1']]);
    expect(saved.map((f) => f.name)).toEqual(['acme.elmresp']);
    expect(await saved[0].blob.text()).toBe('ELMRESP.bytes');
    expect(host.textContent).toContain('The answer is saved as acme.elmresp.');
    answer('POST', '/v1/admin/offline', 204);
    pick(host, new File(['proof'], 'pc.elmdeact'));
    await settle();
    expect(host.textContent).toContain('The deactivation is recorded.');
    expect(saved).toHaveLength(1);
  });
});

describe('Audit', () => {
  /*
   * Preconditions: an admin; the newest page holds a row whose target and detail hold an img with onerror, and points to
   * row 40 as the next; the chain check answers broken at row 7 of 120.
   * Steps: open the audit log; press Older rows; press Verify the chain.
   * Expected: the first read asks for order=desc and limit=50 and the next for after=40; the values are text in the
   * table and the detail is in a pre; no img element is made; one POST verify with X-ELM-CSRF; the broken row is named.
   */
  it('reads newest first, pages older, draws values as text, and verifies', async () => {
    const row = {
      n: 41,
      ts: '2026-10-30T00:00:00Z',
      actorType: 'admin',
      actorId: 'a1',
      action: 'license.create',
      target: '<img src=x onerror=alert(1)>',
      result: 'ok',
      ip: '127.0.0.1',
      detail: '{"name":"<img src=y onerror=alert(2)>"}',
      prev: 'p',
      hash: 'h',
    };
    answer('GET', '/v1/admin/me', 200, admin);
    answer('GET', '/v1/admin/audit?order=desc&limit=50', 200, { items: [row], next: 40 });
    answer('GET', '/v1/admin/audit?order=desc&limit=50&after=40', 200, { items: [{ ...row, n: 40, target: null, detail: null }], next: null });
    answer('POST', '/v1/admin/audit/verify', 200, { intact: false, checked: 120, firstBroken: 7 });
    location.hash = '#/audit';
    const host = mount();
    await settle();
    expect(host.querySelector('table.audit tbody')?.textContent).toContain(row.target);
    expect(host.querySelector('pre.wrap')?.textContent).toBe(row.detail);
    expect(host.querySelectorAll('img')).toHaveLength(0);
    button(host, 'Older rows')!.click();
    await settle();
    expect(host.querySelector('table.audit tbody td')?.textContent).toBe('40');
    button(host, 'Verify the chain')!.click();
    await settle();
    expect(sent.filter((s) => s.url.startsWith('/v1/admin/audit')).map((s) => [s.method, s.url, s.headers['X-ELM-CSRF']])).toEqual([
      ['GET', '/v1/admin/audit?order=desc&limit=50', undefined],
      ['GET', '/v1/admin/audit?order=desc&limit=50&after=40', undefined],
      ['POST', '/v1/admin/audit/verify', '1'],
    ]);
    expect(host.textContent).toContain('The chain is broken at row 7 (120 rows checked).');
  });
});

// The support and administration screens of M5d2 (design §15.2 "M5d2 の形").

const allPermissions = [
  'apikey.read',
  'apikey.create',
  'apikey.revoke',
  'audit.read',
  'product.read',
  'product.write',
  'policy.read',
  'policy.write',
  'license.read',
  'license.create',
  'license.update',
  'license.revoke',
  'license.file',
  'signingkey.read',
  'activation.read',
  'activation.deactivate',
  'activation.offline',
  'trial.read',
  'trial.delete',
  'lease.read',
  'lease.release',
  'usagelog.read',
  'usagelog.seal',
  'admin.read',
  'admin.update',
  'session.read',
  'session.revoke',
];
const root = { id: 'a1', issuer: 'https://idp.example', subject: 'olive', role: 'owner', permissions: allPermissions };
// support and viewer hold every *.read but admin.* and session.* (design §14.6), so apikey.read too.
const reader = { ...root, id: 'a3', subject: 'vic', role: 'viewer', permissions: allPermissions.filter((p) => p.endsWith('.read') && !p.startsWith('admin.') && !p.startsWith('session.')) };
const adminRole = { ...root, id: 'a4', subject: 'adam', role: 'admin', permissions: allPermissions.filter((p) => !/^(apikey|admin|session)\./.test(p)) };
const xss = '<img src=x onerror=alert(1)>';
const deletes = () => sent.filter((s) => s.method === 'DELETE');
const adminsPath = '/v1/admin/admins?limit=200';
const people = [
  { id: 'a1', issuer: 'https://idp.example', subject: 'olive', role: 'owner', enabled: true, createdAt: '2026-10-01T00:00:00Z', lastLoginAt: null },
  { id: 'a2', issuer: 'https://idp.example', subject: xss, role: 'support', enabled: false, createdAt: '2026-10-02T00:00:00Z', lastLoginAt: '2026-10-03T00:00:00Z' },
];

describe('M5d2 routes', () => {
  /*
   * Preconditions: none.
   * Steps: read "#/trials", "#/admins", "#/api-keys", "#/apikeys".
   * Expected: the trials, admins, and API keys routes; unknown for the last.
   */
  it('reads the support and administration routes', () => {
    expect([route('#/trials'), route('#/admins'), route('#/api-keys')]).toEqual([{ kind: 'trials' }, { kind: 'admins' }, { kind: 'apiKeys' }]);
    expect(route('#/apikeys')).toEqual({ kind: 'unknown' });
  });

  /*
   * Preconditions: an owner, a viewer, and an admin (the roles of design §14.6).
   * Steps: open the list as each and read the header's links.
   * Expected: the owner links trials, API keys, and administrators; the viewer links trials and API keys but not
   * administrators; the admin links trials but neither API keys nor administrators.
   */
  it('links each screen to the roles that may read it', async () => {
    const links = async (me: typeof root) => {
      answer('GET', '/v1/admin/me', 200, me);
      answer('GET', '/v1/admin/products?limit=200', 200, { items: [], next: null });
      answer('GET', firstPage, 200, { items: [], next: null });
      const host = mount();
      await settle();
      const found = [...host.querySelectorAll('nav a')].map((a) => a.getAttribute('href')).filter((h) => ['#/trials', '#/api-keys', '#/admins'].includes(h!));
      render(null, host);
      return found;
    };
    expect(await links(root)).toEqual(['#/trials', '#/api-keys', '#/admins']);
    expect(await links(reader)).toEqual(['#/trials', '#/api-keys']);
    expect(await links(adminRole)).toEqual(['#/trials']);
  });
});

describe('Leases', () => {
  const cloud: Policy = { ...standard, floating: 'cloud' };
  const lease = {
    id: 'le1',
    feature: 'core',
    seats: 2,
    keyId: 'k1',
    seq: 1,
    expiresAt: '2026-10-30T01:00:00Z',
    borrow: true,
    stopped: false,
    client: { host: xss, user: '<script>alert(2)</script>' },
    app: '<img src=y onerror=alert(3)>',
    createdAt: '2026-10-30T00:00:00Z',
  };

  async function openLicense(policy: Policy, me: typeof root = root) {
    answer('GET', '/v1/admin/me', 200, me);
    answer('GET', '/v1/admin/licenses/l1', 200, { ...license, name: 'Contoso' });
    answer('GET', '/v1/admin/policies/y1', 200, policy);
    answer('GET', '/v1/admin/licenses/l1/activations?limit=50', 200, { items: [], next: null });
    answer('GET', '/v1/admin/licenses/l1/leases?limit=50', 200, { items: [lease], next: null });
    location.hash = '#/licenses/l1';
    const host = mount();
    await settle();
    return host;
  }

  /*
   * Preconditions: an owner; a license of a policy without floating, then of a cloud-floating one; a viewer (lease.read
   * without license.file).
   * Steps: open the license each way.
   * Expected: no lease list and no lease read for the first; the list for the cloud policy; the viewer reads the policy
   * once and sees the list without a Release button.
   */
  it('shows the leases of a cloud-floating policy only', async () => {
    let host = await openLicense(standard);
    expect(host.querySelector('table.leases')).toBeNull();
    expect(sent.some((s) => s.url.includes('/leases'))).toBe(false);
    render(null, host);
    sent = [];
    host = await openLicense(cloud);
    expect(host.querySelectorAll('table.leases tbody tr')).toHaveLength(1);
    render(null, host);
    sent = [];
    host = await openLicense(cloud, reader);
    expect(sent.filter((s) => s.url === '/v1/admin/policies/y1')).toHaveLength(1);
    expect(host.querySelectorAll('table.leases tbody tr')).toHaveLength(1);
    expect(button(host, 'Release')).toBeUndefined();
  });

  /*
   * Preconditions: an owner; a cloud-floating license with one borrowed lease whose host, user, and app hold markup; the
   * service answers the release with 404 (it expired meanwhile) and then lists no lease.
   * Steps: press Release; confirm.
   * Expected: the values are text and no img or script element is made; the confirmation names the host and the feature
   * and says a borrowed lease is returned too; one DELETE; the 404's detail is shown and the list is read again.
   */
  it('releases after a confirmation and reads the list again on 404', async () => {
    const host = await openLicense(cloud);
    expect(host.querySelector('table.leases')?.textContent).toContain(`${xss} / <script>alert(2)</script>`);
    expect(host.querySelectorAll('img, script')).toHaveLength(0);
    answer('DELETE', '/v1/admin/leases/le1', 404, { code: 'ELM_UNKNOWN_LEASE', detail: 'No lease has this ID.' });
    answer('GET', '/v1/admin/licenses/l1/leases?limit=50', 200, { items: [], next: null });
    button(host, 'Release')!.click();
    await settle();
    expect(host.querySelector('.confirm')?.textContent).toContain(`Release the lease of "core" held by "${xss}"? A borrowed lease is returned too`);
    host.querySelector('.confirm button')!.dispatchEvent(new Event('click'));
    await settle();
    expect(deletes().map((s) => s.url)).toEqual(['/v1/admin/leases/le1']);
    expect(host.textContent).toContain('No lease has this ID.');
    expect(sent.filter((s) => s.url === '/v1/admin/licenses/l1/leases?limit=50')).toHaveLength(2);
    expect(host.textContent).toContain('No lease is held.');
  });
});

describe('Trials', () => {
  const trial = { id: 't1', productId: 'p2', fingerprints: ['fp-a', xss], createdAt: '2026-10-29T00:00:00Z' };

  async function openTrials(me: typeof root = root) {
    answer('GET', '/v1/admin/me', 200, me);
    answer('GET', '/v1/admin/products?limit=200', 200, { items: [{ id: 'p1', code: 'a', name: 'First' }], next: 'p1' });
    answer('GET', '/v1/admin/products?limit=200&after=p1', 200, { items: [{ id: 'p2', code: 'b', name: 'Second' }], next: null });
    answer('GET', '/v1/admin/trials?limit=50', 200, { items: [trial], next: null });
    location.hash = '#/trials';
    const host = mount();
    await settle();
    return host;
  }

  /*
   * Preconditions: an owner; products on two pages; one trial of the product on the second page, a fingerprint holding
   * markup.
   * Steps: open the trials; choose Second and show.
   * Expected: the row names Second and draws the fingerprints as text; the filter sends productId=p2.
   */
  it('names products from every page and filters by product', async () => {
    const host = await openTrials();
    expect(host.querySelector('table.trials tbody td')?.textContent).toBe('Second');
    expect(host.querySelector('table.trials')?.textContent).toContain(`fp-a, ${xss}`);
    expect(host.querySelectorAll('img')).toHaveLength(0);
    answer('GET', '/v1/admin/trials?limit=50&productId=p2', 200, { items: [trial], next: null });
    (host.querySelector('select[name=product]') as HTMLSelectElement).value = 'p2';
    submit(host.querySelector('form')!);
    await settle();
    expect(sent.some((s) => s.url === '/v1/admin/trials?limit=50&productId=p2')).toBe(true);
  });

  /*
   * Preconditions: an owner; one trial; the service answers its deletion with 404 (deleted elsewhere); then a viewer.
   * Steps: press Delete and confirm; open the trials as the viewer.
   * Expected: the confirmation names the product and the time and says the device can try again and the license stays;
   * one DELETE; the detail is shown and the list read again; the viewer has no Delete button.
   */
  it('deletes after a confirmation and offers no delete without trial.delete', async () => {
    let host = await openTrials();
    answer('DELETE', '/v1/admin/trials/t1', 404, { code: 'ELM_NOT_FOUND', detail: 'No trial has this ID.' });
    button(host, 'Delete')!.click();
    await settle();
    expect(host.querySelector('.confirm')?.textContent).toContain(
      'Delete the trial of "Second" made at 2026-10-29T00:00:00Z? Its device can start a trial again. The trial license and its activations stay.',
    );
    host.querySelector('.confirm button')!.dispatchEvent(new Event('click'));
    await settle();
    expect(deletes().map((s) => s.url)).toEqual(['/v1/admin/trials/t1']);
    expect(host.textContent).toContain('No trial has this ID.');
    expect(sent.filter((s) => s.url === '/v1/admin/trials?limit=50')).toHaveLength(2);
    render(null, host);
    host = await openTrials(reader);
    expect(button(host, 'Delete')).toBeUndefined();
  });
});

describe('apiKeyRequest and keyStatus', () => {
  /*
   * Preconditions: none.
   * Steps: read forms with a name of 64 emoji and one permission and no days; 65 emoji; no permission; days 1.5, 0, 366,
   * and 365.
   * Expected: the request without expiresInDays; the name, permission, and days refusals; then expiresInDays 365.
   */
  it('reads the create form and checks the name, the permissions, and the days', () => {
    const name = '😀'.repeat(64);
    expect(apiKeyRequest(form([['name', name], ['permission', 'license.read'], ['days', '']]))).toEqual({ name, permissions: ['license.read'] });
    expect(apiKeyRequest(form([['name', '😀'.repeat(65)], ['permission', 'license.read']]))).toBe('The name must be 1 to 64 characters.');
    expect(apiKeyRequest(form([['name', 'ci']]))).toBe('Choose at least one permission.');
    for (const days of ['1.5', '0', '366']) {
      expect(apiKeyRequest(form([['name', 'ci'], ['permission', 'license.read'], ['days', days]]))).toBe('The days must be a whole number from 1 to 365.');
    }
    expect(apiKeyRequest(form([['name', 'ci'], ['permission', 'a'], ['permission', 'b'], ['days', '365']]))).toEqual({
      name: 'ci',
      permissions: ['a', 'b'],
      expiresInDays: 365,
    });
  });

  /*
   * Preconditions: the time is 2026-10-30T00:00:00Z; a key made by a disabled administrator.
   * Steps: read the status of the key revoked, past its expiry, within it, and with no administrators known.
   * Expected: revoked; expired (an expiry equal to now has passed); creator disabled; and empty when the creator is unknown.
   */
  it('marks keys that no longer work', () => {
    const now = Date.parse('2026-10-30T00:00:00Z');
    const key = { id: 'k', name: 'n', prefix: 'elm_abcd', permissions: [], createdBy: 'a2', createdAt: '2026-10-01T00:00:00Z', expiresAt: '2027-01-01T00:00:00Z', revokedAt: null };
    const admins = new Map(people.map((a) => [a.id, a]));
    expect(keyStatus({ ...key, revokedAt: '2026-10-20T00:00:00Z' }, admins, now)).toBe('revoked');
    expect(keyStatus({ ...key, expiresAt: '2026-10-30T00:00:00Z' }, admins, now)).toBe('expired');
    expect(keyStatus(key, admins, now)).toBe('creator disabled');
    expect(keyStatus(key, null, now)).toBe('');
  });
});

describe('ApiKeys', () => {
  const live = { id: 'k1', name: xss, prefix: 'elm_abcd', permissions: ['license.read'], createdBy: 'a1', createdAt: '2026-10-01T00:00:00Z', expiresAt: '2099-01-01T00:00:00Z', revokedAt: null };
  const keysPath = '/v1/admin/api-keys?limit=50';

  /*
   * Preconditions: a viewer (apikey.read, no admin.read); one key whose name holds markup.
   * Steps: open the API keys.
   * Expected: the name is text and no img is made; the creator is shown as its ID; no create form and no Revoke button;
   * the administrators are not read.
   */
  it('shows the list read-only to a viewer', async () => {
    answer('GET', '/v1/admin/me', 200, reader);
    answer('GET', keysPath, 200, { items: [live], next: null });
    location.hash = '#/api-keys';
    const host = mount();
    await settle();
    expect(host.querySelector('table.api-keys tbody td')?.textContent).toBe(xss);
    expect(host.querySelectorAll('img')).toHaveLength(0);
    expect(host.querySelector('table.api-keys tbody td:nth-child(4)')?.textContent).toBe('a1');
    expect([host.querySelector('form.api-key'), button(host, 'Revoke')]).toEqual([null, undefined]);
    expect(sent.some((s) => s.url.startsWith('/v1/admin/admins'))).toBe(false);
  });

  /*
   * Preconditions: an owner; one live key and one revoked key; the creator a1 is named by the administrators' list; the
   * service answers the creation with the key; the clipboard refuses.
   * Steps: open the API keys; fill the name, two permissions, and no days; press Create twice in one task; press Copy;
   * press Back to the list.
   * Expected: the creator is shown as olive; the revoked key has no Revoke button and says revoked; the permissions offered
   * are the owner's 27; one POST without expiresInDays; the key in a read-only field and in neither storage nor the URL;
   * the copy failure; the list read again and the key gone.
   */
  it('creates a key once, shows it once, and keeps it in memory only', async () => {
    const key = 'elm_' + 'Q'.repeat(43);
    answer('GET', '/v1/admin/me', 200, root);
    answer('GET', adminsPath, 200, { items: people, next: null });
    answer('GET', keysPath, 200, { items: [live, { ...live, id: 'k2', name: 'old', revokedAt: '2026-10-02T00:00:00Z' }], next: null });
    answer('POST', '/v1/admin/api-keys', 201, { ...live, id: 'k3', name: 'ci', key });
    location.hash = '#/api-keys';
    const host = mount();
    await settle();
    const rows = [...host.querySelectorAll('table.api-keys tbody tr')];
    expect(rows[0].querySelector('td:nth-child(4)')?.textContent).toBe('olive');
    expect([rows[1].querySelector('button'), rows[1].querySelector('td:nth-child(8)')?.textContent]).toEqual([null, 'revoked']);
    expect(host.querySelectorAll('input[name=permission]')).toHaveLength(27);
    field(host, 'name').value = 'ci';
    (host.querySelector('input[value="license.read"]') as HTMLInputElement).checked = true;
    (host.querySelector('input[value="license.create"]') as HTMLInputElement).checked = true;
    const create = host.querySelector('form.api-key') as HTMLFormElement;
    submit(create);
    submit(create);
    await settle();
    expect(posts().map((s) => s.body)).toEqual([JSON.stringify({ name: 'ci', permissions: ['license.read', 'license.create'] })]);
    const shown = host.querySelector('input.key') as HTMLInputElement;
    expect([shown.value, shown.readOnly]).toEqual([key, true]);
    expect(JSON.stringify({ ...sessionStorage }) + JSON.stringify({ ...localStorage }) + location.href).not.toContain('QQQQ');
    vi.stubGlobal('navigator', { clipboard: { writeText: () => Promise.reject(new Error('denied')) } });
    button(host, 'Copy the key')!.click();
    await settle();
    expect(host.textContent).toContain('The browser did not copy the key. Select it and copy it.');
    button(host, 'Back to the list')!.click();
    await settle();
    expect(sent.filter((s) => s.url === keysPath)).toHaveLength(2);
    expect(host.querySelector('input.key')).toBeNull();
  });

  /*
   * Preconditions: an owner; one live key.
   * Steps: press Revoke; confirm.
   * Expected: the confirmation names the key and its prefix; one DELETE; the list is read again.
   */
  it('revokes a key after a confirmation', async () => {
    answer('GET', '/v1/admin/me', 200, root);
    answer('GET', adminsPath, 200, { items: people, next: null });
    answer('GET', keysPath, 200, { items: [{ ...live, name: 'ci' }], next: null });
    answer('DELETE', '/v1/admin/api-keys/k1', 204);
    location.hash = '#/api-keys';
    const host = mount();
    await settle();
    button(host, 'Revoke')!.click();
    await settle();
    expect(host.querySelector('.confirm')?.textContent).toContain('Revoke the API key "ci" (elm_abcd…)?');
    host.querySelector('.confirm button')!.dispatchEvent(new Event('click'));
    await settle();
    expect(deletes().map((s) => s.url)).toEqual(['/v1/admin/api-keys/k1']);
    expect(sent.filter((s) => s.url === keysPath)).toHaveLength(2);
    expect(host.textContent).toContain('The API key is revoked.');
  });
});

describe('Admins and sessions', () => {
  const third = { ...people[0], id: 'a9', subject: 'zed', role: 'admin' };
  const sessionsPath = '/v1/admin/sessions?limit=50';
  const sessions = [
    { id: 's1', adminId: 'a1', createdAt: '2026-10-30T00:00:00Z', lastSeenAt: '2026-10-30T00:05:00Z', expiresAt: '2026-10-30T12:00:00Z', ip: '127.0.0.1' },
    { id: 's2', adminId: 'a9', createdAt: '2026-10-30T01:00:00Z', lastSeenAt: '2026-10-30T01:05:00Z', expiresAt: '2026-10-30T13:00:00Z', ip: null },
  ];

  async function openAdmins() {
    answer('GET', '/v1/admin/me', 200, root);
    answer('GET', adminsPath, 200, { items: people, next: 'a2' });
    answer('GET', `${adminsPath}&after=a2`, 200, { items: [third], next: null });
    answer('GET', sessionsPath, 200, { items: sessions, next: null });
    location.hash = '#/admins';
    const host = mount();
    await settle();
    return host;
  }

  /*
   * Preconditions: an owner (a1); administrators on two pages, a2 (disabled, subject holding markup) and a9 on the second;
   * sessions of a1 and a9.
   * Steps: open the administrators.
   * Expected: three rows, the own row marked and without Disable, a2 offering Enable and a9 Disable; the subject is text;
   * the sessions name zed from the second page and mark the own session.
   */
  it('lists every administrator and names the sessions', async () => {
    const host = await openAdmins();
    const rows = [...host.querySelectorAll('table.admins tbody tr')];
    expect(rows.map((r) => r.querySelector('td')?.textContent)).toEqual(['olive (you)', xss, 'zed']);
    expect(rows.map((r) => r.querySelector('button')?.textContent ?? null)).toEqual([null, 'Enable', 'Disable']);
    expect(host.querySelectorAll('img')).toHaveLength(0);
    expect([...host.querySelectorAll('table.sessions tbody tr td:first-child')].map((t) => t.textContent)).toEqual(['olive (you)', 'zed']);
  });

  /*
   * Preconditions: as above; the service refuses the first disable with 409 and takes the second; it takes the enable.
   * Steps: disable zed and confirm (409); disable zed and confirm again; enable a2 and confirm.
   * Expected: the disable confirmation says the sessions end and the API keys stop; the 409's detail is shown; after the
   * taken disable the sessions are read again; the enable confirmation says the API keys work again, and the enable is
   * one POST.
   */
  it('confirms disabling and enabling and reads the sessions again after a disable', async () => {
    const host = await openAdmins();
    answer('POST', '/v1/admin/admins/a9/disable', 409, { code: 'ELM_CONFLICT', detail: 'The last enabled owner cannot be disabled.' });
    button(host, 'Disable')!.click();
    await settle();
    expect(host.querySelector('.confirm')?.textContent).toContain('Disable the administrator "zed"? Their sessions in this screen end, and their API keys stop working.');
    host.querySelector('.confirm button')!.dispatchEvent(new Event('click'));
    await settle();
    expect(host.textContent).toContain('The last enabled owner cannot be disabled.');
    const sessionReads = sent.filter((s) => s.url === sessionsPath).length;
    answer('POST', '/v1/admin/admins/a9/disable', 204);
    button(host, 'Disable')!.click();
    await settle();
    host.querySelector('.confirm button')!.dispatchEvent(new Event('click'));
    await settle();
    expect(sent.filter((s) => s.url === sessionsPath).length).toBe(sessionReads + 1);
    answer('POST', '/v1/admin/admins/a2/enable', 204);
    button(host, 'Enable')!.click();
    await settle();
    expect(posts().filter((s) => s.url.endsWith('/enable'))).toHaveLength(0);
    expect(host.querySelector('.confirm')?.textContent).toContain(`Enable the administrator "${xss}"? Their API keys that are not revoked or expired work again at once.`);
    host.querySelector('.confirm button')!.dispatchEvent(new Event('click'));
    await settle();
    expect(posts().filter((s) => s.url.endsWith('/enable')).map((s) => s.url)).toEqual(['/v1/admin/admins/a2/enable']);
  });

  /*
   * Preconditions: as above; the service answers ending zed's session with 404 (it idled out).
   * Steps: filter by zed; press End on the own session and cancel; press End on zed's session and confirm.
   * Expected: the filter sends adminId=a9; the own confirmation warns of signing in again; one DELETE; the 404's detail is
   * shown and the sessions read again.
   */
  it('filters, warns on the own session, and reads again on 404', async () => {
    const host = await openAdmins();
    answer('GET', `${sessionsPath}&adminId=a9`, 200, { items: [sessions[1]], next: null });
    const filter = host.querySelector('select[name=admin]')!.closest('form') as HTMLFormElement;
    (filter.querySelector('select') as HTMLSelectElement).value = '';
    button(host, 'End')!.click();
    await settle();
    expect(host.querySelector('.confirm')?.textContent).toContain('End your session made at 2026-10-30T00:00:00Z? If it is the one you are using');
    button(host, 'Cancel')!.click();
    await settle();
    (filter.querySelector('select') as HTMLSelectElement).value = 'a9';
    submit(filter);
    await settle();
    expect(sent.some((s) => s.url === `${sessionsPath}&adminId=a9`)).toBe(true);
    answer('DELETE', '/v1/admin/sessions/s2', 404, { code: 'ELM_NOT_FOUND', detail: 'No session has this ID.' });
    button(host, 'End')!.click();
    await settle();
    expect(host.querySelector('.confirm')?.textContent).toContain('End the session of "zed" made at 2026-10-30T01:00:00Z?');
    host.querySelector('.confirm button')!.dispatchEvent(new Event('click'));
    await settle();
    expect(deletes().map((s) => s.url)).toEqual(['/v1/admin/sessions/s2']);
    expect(host.textContent).toContain('No session has this ID.');
    expect(sent.filter((s) => s.url === `${sessionsPath}&adminId=a9`)).toHaveLength(2);
  });
});

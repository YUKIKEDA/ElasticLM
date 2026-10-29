// A stand-in for the administration API (design §14.5) and the static files of one candidate's dist.
// Usage: node mock-server.mjs <candidate> <port>
import { createServer } from 'node:http';
import { readFile } from 'node:fs/promises';
import { extname, join, normalize, resolve, sep } from 'node:path';

const [candidate, port = '5180'] = process.argv.slice(2);
const root = resolve(import.meta.dirname, candidate, 'dist');
const types = { '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css' };
const licenses = Array.from({ length: 45 }, (_, i) => ({
  id: `lic_${String(i + 1).padStart(3, '0')}`,
  name: i === 0 ? 'Acme' : `Customer ${i + 1}`,
  state: 'active',
  expiresAt: i % 3 === 0 ? null : '2027-12-31T00:00:00Z',
}));

function json(res, status, body) {
  res.writeHead(status, { 'content-type': 'application/json' });
  res.end(JSON.stringify(body));
}

createServer(async (req, res) => {
  const url = new URL(req.url, 'http://localhost');
  if (url.pathname.startsWith('/v1/') && /(^|; )signedOut=1/.test(req.headers.cookie ?? '')) {
    return json(res, 401, { code: 'ELM_UNAUTHORIZED', detail: 'Sign in again.' });
  }
  const detail = /^\/v1\/admin\/licenses\/([^/]+)(\/revoke)?$/.exec(url.pathname);
  if (url.pathname === '/v1/admin/licenses' && req.method === 'GET') {
    const q = (url.searchParams.get('q') ?? '').toLowerCase();
    const limit = Number(url.searchParams.get('limit') ?? 20);
    const after = url.searchParams.get('after');
    const all = licenses.filter((l) => l.name.toLowerCase().includes(q));
    const start = after ? all.findIndex((l) => l.id === after) + 1 : 0;
    const items = all.slice(start, start + limit);
    return json(res, 200, { items, next: start + limit < all.length ? items.at(-1).id : null });
  }
  if (detail) {
    const license = licenses.find((l) => l.id === decodeURIComponent(detail[1]));
    if (!license) return json(res, 404, { code: 'ELM_NOT_FOUND', detail: 'The license does not exist.' });
    if (!detail[2] && req.method === 'GET') return json(res, 200, license);
    if (detail[2] && req.method === 'POST') {
      if (req.headers['x-elm-csrf'] !== '1') return json(res, 403, { code: 'ELM_FORBIDDEN', detail: 'X-ELM-CSRF is missing.' });
      license.state = 'revoked';
      res.writeHead(204);
      return res.end();
    }
    return json(res, 405, { code: 'ELM_METHOD_NOT_ALLOWED', detail: 'Method not allowed.' });
  }
  if (url.pathname === '/auth/login') {
    res.writeHead(200, { 'content-type': 'text/plain' });
    return res.end('login');
  }
  const file = normalize(join(root, url.pathname === '/' ? 'index.html' : decodeURIComponent(url.pathname)));
  if (!file.startsWith(root + sep)) return json(res, 404, { code: 'ELM_NOT_FOUND', detail: 'Not found.' });
  try {
    const body = await readFile(file);
    res.writeHead(200, {
      'content-type': types[extname(file)] ?? 'application/octet-stream',
      'content-security-policy': "default-src 'self'",
    });
    res.end(body);
  } catch {
    console.error(`404 ${url.pathname}`);
    json(res, 404, { code: 'ELM_NOT_FOUND', detail: 'Not found.' });
  }
}).listen(Number(port), '127.0.0.1');

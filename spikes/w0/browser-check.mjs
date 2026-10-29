// Drives one candidate's dist in headless Chrome under `Content-Security-Policy: default-src 'self'` and reports each step and every CSP violation.
// Usage: node browser-check.mjs <candidate> <path to chrome> <scratch directory>
import { spawn } from 'node:child_process';
import { mkdtempSync, rmSync } from 'node:fs';
import { join } from 'node:path';

const [candidate, chrome, scratch] = process.argv.slice(2);
const appPort = 5180;
const debugPort = 9333;
const server = spawn(process.execPath, [join(import.meta.dirname, 'mock-server.mjs'), candidate, String(appPort)], { stdio: 'inherit' });
const profile = mkdtempSync(join(scratch, 'chrome-'));
const browser = spawn(chrome, ['--headless=new', `--remote-debugging-port=${debugPort}`, `--user-data-dir=${profile}`, '--no-first-run', 'about:blank'], { stdio: 'ignore' });

const pause = (ms) => new Promise((r) => setTimeout(r, ms));
let target;
for (let i = 0; i < 50 && !target; i++) {
  await pause(200);
  target = await fetch(`http://127.0.0.1:${debugPort}/json/list`).then((r) => r.json()).then((l) => l.find((t) => t.type === 'page')).catch(() => undefined);
}
const ws = new WebSocket(target.webSocketDebuggerUrl);
await new Promise((r) => ws.addEventListener('open', r));
let id = 0;
const pending = new Map();
const logs = [];
ws.addEventListener('message', (e) => {
  const msg = JSON.parse(e.data);
  if (msg.id) pending.get(msg.id)?.(msg);
  if (msg.method === 'Log.entryAdded') logs.push(`${msg.params.entry.level}: ${msg.params.entry.text}`);
  if (msg.method === 'Runtime.exceptionThrown') logs.push(`exception: ${msg.params.exceptionDetails.exception?.description ?? msg.params.exceptionDetails.text}`);
});
const send = (method, params = {}) =>
  new Promise((r) => {
    pending.set(++id, r);
    ws.send(JSON.stringify({ id, method, params }));
  });

await send('Page.enable');
await send('Runtime.enable');
await send('Log.enable');
await send('Page.addScriptToEvaluateOnNewDocument', {
  source: "window.__csp = []; addEventListener('securitypolicyviolation', (e) => __csp.push(e.violatedDirective + ' ' + e.blockedURI));",
});
await send('Page.navigate', { url: `http://127.0.0.1:${appPort}/` });

const steps = `(async () => {
  const done = [];
  const text = () => document.body.textContent;
  const wait = async (what, f) => {
    for (let i = 0; i < 100; i++) { if (f()) { done.push(what); return; } await new Promise((r) => setTimeout(r, 50)); }
    throw new Error('Timed out: ' + what + ' after ' + done.join(', '));
  };
  const button = (label, scope = document) => [...scope.querySelectorAll('button')].find((b) => b.textContent.trim() === label);
  await wait('list', () => text().includes('Acme') && document.querySelectorAll('tbody tr').length === 20);
  button('Next page').click();
  await wait('next page', () => text().includes('Customer 21') && !text().includes('Acme'));
  const input = document.querySelector('input');
  input.value = '45';
  input.dispatchEvent(new Event('input', { bubbles: true }));
  await new Promise((r) => setTimeout(r, 100));
  button('Search').click();
  await wait('search', () => document.querySelectorAll('tbody tr').length === 1 && text().includes('Customer 45'));
  location.hash = '#/licenses/lic_001';
  await wait('detail', () => [...document.querySelectorAll('dd')].some((d) => d.textContent === 'Acme'));
  button('Revoke').click();
  await wait('confirm', () => text().includes('Revoke the license "Acme"?'));
  button('Revoke', document.querySelector('p:has(button)')).click();
  await wait('revoked', () => [...document.querySelectorAll('dd')].some((d) => d.textContent === 'revoked'));
  location.hash = '#/';
  await wait('back to the list', () => text().includes('Customer 2'));
  location.hash = '#/licenses/missing';
  await wait('error', () => document.querySelector('[role=alert]')?.textContent === 'The license does not exist.');
  location.hash = '#/';
  await wait('list again', () => text().includes('Customer 2'));
  document.cookie = 'signedOut=1';
  button('Next page').click();
  return { done, csp: window.__csp };
})()`;

await pause(500);
const result = await send('Runtime.evaluate', { expression: steps, awaitPromise: true, returnByValue: true });
await pause(1000);
const after = await send('Runtime.evaluate', { expression: 'location.pathname + " " + (window.__csp ?? []).length', returnByValue: true });
console.log(JSON.stringify({ candidate, result: result.result?.exceptionDetails ? `failed: ${result.result.exceptionDetails.exception?.description ?? result.result.exceptionDetails.text}` : result.result?.result?.value, afterSignOut: after.result?.result?.value, logs }));
ws.close();
browser.kill();
server.kill();
await pause(500);
rmSync(profile, { recursive: true, force: true, maxRetries: 10, retryDelay: 200 });

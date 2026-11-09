// design §20.7 (V1c5, Issue #192): every Mermaid diagram of the built site is drawn, in the light and the dark color
// scheme. A diagram once rendered empty only in the dark scheme (a second render with the same id), so each page is
// opened in a new browser context whose prefers-color-scheme is set before the page loads. The expected number of
// diagrams comes from the source pages, because the built HTML holds no trace of them until the browser draws them.
//
// Usage: node check-mermaid.mjs [--build <folder>] [--browser <executable>] [--jobs <n>]
// The browser defaults to google-chrome on Linux and to Microsoft Edge on Windows; --browser or the environment
// variable MERMAID_CHECK_BROWSER names another Chromium-based browser. Only Node.js built-ins are used.
import { spawn } from 'node:child_process';
import { existsSync, mkdtempSync, readdirSync, readFileSync, rmSync, statSync } from 'node:fs';
import { createServer } from 'node:http';
import { tmpdir } from 'node:os';
import { dirname, extname, join, normalize, relative, resolve, sep } from 'node:path';
import { fileURLToPath } from 'node:url';
import { parseArgs } from 'node:util';

const site = dirname(fileURLToPath(import.meta.url));
const baseUrl = '/ElasticLM/';
const publicOrigin = 'https://yukikeda.github.io';
const schemes = ['light', 'dark'];
const settleMs = 2000;
const pageLimitMs = 30000;
const sources = {
  en: join(site, '..', 'docs', 'guide'),
  ja: join(site, '..', 'docs', 'guide.ja'),
};

const { values: options } = parseArgs({
  options: {
    build: { type: 'string', default: join(site, 'build') },
    browser: { type: 'string', default: process.env.MERMAID_CHECK_BROWSER ?? defaultBrowser() },
    jobs: { type: 'string', default: '4' },
  },
});
const build = resolve(options.build);
const jobs = Number.parseInt(options.jobs, 10);
if (!(jobs >= 1)) {
  throw new Error(`--jobs must be a positive number: ${options.jobs}`);
}

function defaultBrowser() {
  if (process.platform === 'win32') {
    return 'C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe';
  }
  if (process.platform === 'darwin') {
    return '/Applications/Google Chrome.app/Contents/MacOS/Google Chrome';
  }
  return 'google-chrome';
}

// The expected diagrams: the fenced mermaid blocks of each source page, by doc id (its path without .md).
function readSources(root) {
  const counts = new Map();
  const walk = (folder) => {
    for (const entry of readdirSync(folder, { withFileTypes: true })) {
      const path = join(folder, entry.name);
      if (entry.isDirectory()) {
        walk(path);
      } else if (entry.name.endsWith('.md')) {
        const id = relative(root, path).slice(0, -'.md'.length).split(sep).join('/');
        const blocks = readFileSync(path, 'utf8').match(/^[ \t]*```mermaid[ \t]*$/gm);
        counts.set(id, blocks?.length ?? 0);
      }
    }
  };
  walk(root);
  return counts;
}

const contentTypes = {
  '.html': 'text/html; charset=utf-8',
  '.js': 'text/javascript; charset=utf-8',
  '.css': 'text/css; charset=utf-8',
  '.json': 'application/json',
  '.xml': 'application/xml',
  '.svg': 'image/svg+xml',
  '.png': 'image/png',
  '.ico': 'image/x-icon',
  '.txt': 'text/plain; charset=utf-8',
  '.woff2': 'font/woff2',
};

// The build folder's file for a URL path under /ElasticLM/, or null. A folder serves its index.html.
function fileFor(urlPath) {
  if (!urlPath.startsWith(baseUrl)) {
    return null;
  }
  const path = normalize(join(build, decodeURIComponent(urlPath.slice(baseUrl.length))));
  if (path !== build && !path.startsWith(build + sep)) {
    return null;
  }
  if (existsSync(path) && statSync(path).isDirectory()) {
    const index = join(path, 'index.html');
    return existsSync(index) ? index : null;
  }
  return existsSync(path) ? path : null;
}

function serve() {
  const server = createServer((request, response) => {
    const file = fileFor(new URL(request.url, 'http://localhost').pathname);
    if (file === null) {
      response.writeHead(404).end();
      return;
    }
    response.writeHead(200, { 'content-type': contentTypes[extname(file)] ?? 'application/octet-stream' });
    response.end(readFileSync(file));
  });
  return new Promise((done) => server.listen(0, '127.0.0.1', () => done(server)));
}

// The DevTools protocol over --remote-debugging-pipe: Chrome reads commands on fd 3 and writes on fd 4, each message
// ending with a NUL byte. The sandbox stays on: --no-sandbox is added only if the runner cannot start Chrome without it
// (design §20.7, V1c5 R8), and then with the reason here.
function launch(executable, profile) {
  const args = [
    '--headless',
    '--remote-debugging-pipe',
    `--user-data-dir=${profile}`,
    '--no-first-run',
    '--no-default-browser-check',
    '--disable-extensions',
    '--disable-sync',
    'about:blank',
  ];
  const child = spawn(executable, args, { stdio: ['ignore', 'ignore', 'pipe', 'pipe', 'pipe'] });
  let stderr = '';
  child.stderr.on('data', (chunk) => {
    stderr = (stderr + chunk).slice(-4000);
  });
  const pending = new Map();
  const listeners = new Set();
  let nextId = 1;
  let buffer = Buffer.alloc(0);
  let closed = null;
  const fail = (error) => {
    closed ??= error;
    for (const { reject } of pending.values()) {
      reject(closed);
    }
    pending.clear();
  };
  child.on('error', fail);
  child.on('exit', (code) => fail(new Error(`The browser exited (${code}). ${stderr.trim()}`)));
  child.stdio[4].on('data', (chunk) => {
    buffer = Buffer.concat([buffer, chunk]);
    let end;
    while ((end = buffer.indexOf(0)) !== -1) {
      const message = JSON.parse(buffer.subarray(0, end).toString('utf8'));
      buffer = buffer.subarray(end + 1);
      if (message.id !== undefined) {
        const call = pending.get(message.id);
        pending.delete(message.id);
        if (message.error) {
          call?.reject(new Error(`${call.method}: ${message.error.message}`));
        } else {
          call?.resolve(message.result);
        }
      } else {
        for (const listener of listeners) {
          listener(message);
        }
      }
    }
  });
  const send = (method, params = {}, sessionId = undefined) => {
    if (closed) {
      return Promise.reject(closed);
    }
    const id = nextId++;
    return new Promise((resolveCall, reject) => {
      pending.set(id, { resolve: resolveCall, reject, method });
      child.stdio[3].write(`${JSON.stringify({ id, method, params, sessionId })}\0`);
    });
  };
  const close = async () => {
    if (child.exitCode === null) {
      const exited = new Promise((done) => child.once('exit', done));
      await send('Browser.close').catch(() => {});
      const timer = setTimeout(() => child.kill('SIGKILL'), 5000);
      await exited;
      clearTimeout(timer);
    }
  };
  return { send, listeners, close };
}

// Installed in each page before its scripts run: the time of the last change inside or of a diagram container.
const observer = `(() => {
  window.__mermaidChangedAt = performance.now();
  const touches = (node) =>
    node.nodeType === Node.ELEMENT_NODE &&
    (node.matches('.docusaurus-mermaid-container') || node.querySelector('.docusaurus-mermaid-container') !== null);
  new MutationObserver((records) => {
    for (const record of records) {
      const target = record.target.nodeType === Node.ELEMENT_NODE ? record.target : record.target.parentElement;
      if (target?.closest('.docusaurus-mermaid-container') || [...record.addedNodes, ...record.removedNodes].some(touches)) {
        window.__mermaidChangedAt = performance.now();
        return;
      }
    }
  }).observe(document, { subtree: true, childList: true, attributes: true, characterData: true });
})()`;

const probe = `(() => {
  const containers = [...document.querySelectorAll('.docusaurus-mermaid-container')];
  return {
    hydrated: document.documentElement.dataset.hasHydrated === 'true',
    theme: document.documentElement.dataset.theme ?? null,
    drawn: containers.map((container) => container.querySelector('svg') !== null),
    sinceChange: performance.now() - (window.__mermaidChangedAt ?? 0),
  };
})()`;

const sleep = (ms) => new Promise((done) => setTimeout(done, ms));

async function checkPage(browser, url, scheme, expected) {
  const { browserContextId } = await browser.send('Target.createBrowserContext');
  try {
    const { targetId } = await browser.send('Target.createTarget', { url: 'about:blank', browserContextId });
    const { sessionId } = await browser.send('Target.attachToTarget', { targetId, flatten: true });
    const send = (method, params) => browser.send(method, params, sessionId);
    await send('Emulation.setEmulatedMedia', { features: [{ name: 'prefers-color-scheme', value: scheme }] });
    await send('Page.enable');
    await send('Page.addScriptToEvaluateOnNewDocument', { source: observer });
    const started = performance.now();
    await send('Page.navigate', { url });
    let state = null;
    let matchedAt = null;
    while (performance.now() - started < pageLimitMs) {
      await sleep(100);
      const { result } = await send('Runtime.evaluate', { expression: probe, returnByValue: true }).catch(() => ({}));
      state = result?.value ?? null;
      if (state === null || !state.hydrated || state.drawn.length !== expected) {
        matchedAt = null;
        continue;
      }
      matchedAt ??= performance.now();
      if (performance.now() - matchedAt >= settleMs && state.sinceChange >= settleMs) {
        break;
      }
    }
    const elapsed = performance.now() - started;
    const problems = [];
    if (state === null || !state.hydrated) {
      problems.push('the page did not hydrate');
    } else {
      if (state.theme !== scheme) {
        problems.push(`data-theme is ${state.theme}`);
      }
      if (state.drawn.length !== expected) {
        problems.push(`${state.drawn.length} containers, ${expected} in the source`);
      }
      const empty = state.drawn.flatMap((drawn, index) => (drawn ? [] : [index + 1]));
      if (empty.length > 0) {
        problems.push(`no svg in container ${empty.join(', ')}`);
      }
    }
    if (elapsed >= pageLimitMs) {
      problems.push(`not settled within ${pageLimitMs / 1000} s`);
    }
    return { containers: state?.drawn.length ?? 0, elapsed, problems };
  } finally {
    await browser.send('Target.disposeBrowserContext', { browserContextId }).catch(() => {});
  }
}

function sitemapPages(file) {
  if (!existsSync(file)) {
    throw new Error(`${file} does not exist; build the site first.`);
  }
  const urls = [...readFileSync(file, 'utf8').matchAll(/<loc>([^<]+)<\/loc>/g)].map((match) => match[1]);
  if (urls.length === 0) {
    throw new Error(`${file} lists no page.`);
  }
  return urls.map((url) => {
    const path = new URL(url).pathname;
    if (!url.startsWith(publicOrigin + baseUrl)) {
      throw new Error(`${file} lists ${url}, outside ${publicOrigin + baseUrl}.`);
    }
    return path;
  });
}

async function main() {
  const failures = [];
  const expected = { en: readSources(sources.en), ja: readSources(sources.ja) };
  const sourceTotal = [...expected.en.values(), ...expected.ja.values()].reduce((sum, count) => sum + count, 0);

  // The pages: both sitemaps, each page's doc id read from the class docs-doc-id-<id> of its <html>.
  const pages = [];
  const seen = { en: new Set(), ja: new Set() };
  for (const sitemap of [join(build, 'sitemap.xml'), join(build, 'ja', 'sitemap.xml')]) {
    for (const path of sitemapPages(sitemap)) {
      const locale = path.startsWith(`${baseUrl}ja/`) ? 'ja' : 'en';
      const file = fileFor(path);
      if (file === null) {
        failures.push(`${path}: in the sitemap, not in the build`);
        continue;
      }
      const html = readFileSync(file, 'utf8').match(/<html[^>]*>/)?.[0] ?? '';
      const id = html.match(/class="[^"]*\bdocs-doc-id-([^\s"]+)/)?.[1] ?? null;
      if (id !== null) {
        if (!expected[locale].has(id)) {
          failures.push(`${path}: doc id ${id} has no source page in ${relative(site, sources[locale])}`);
        }
        seen[locale].add(id);
      }
      pages.push({ path, expected: id === null ? 0 : (expected[locale].get(id) ?? 0) });
    }
  }
  for (const locale of ['en', 'ja']) {
    for (const id of expected[locale].keys()) {
      if (!seen[locale].has(id)) {
        failures.push(`${relative(site, join(sources[locale], id))}.md: no page in the sitemap`);
      }
    }
  }

  const server = await serve();
  const origin = `http://127.0.0.1:${server.address().port}`;
  const profile = mkdtempSync(join(tmpdir(), 'mermaid-check-'));
  const browser = launch(options.browser, profile);
  const started = performance.now();
  const totals = { light: 0, dark: 0 };
  try {
    await browser.send('Browser.getVersion').then((version) => console.log(`${version.product}, ${pages.length} pages`));
    const work = pages.flatMap((page) => schemes.map((scheme) => ({ page, scheme })));
    let next = 0;
    const worker = async () => {
      while (next < work.length) {
        const { page, scheme } = work[next++];
        const result = await checkPage(browser, origin + page.path, scheme, page.expected);
        totals[scheme] += result.containers;
        const status = result.problems.length === 0 ? 'ok  ' : 'FAIL';
        const detail = result.problems.length === 0 ? '' : `: ${result.problems.join('; ')}`;
        console.log(
          `${status} ${scheme.padEnd(5)} ${page.path} ${result.containers}/${page.expected} diagrams, ` +
            `${(result.elapsed / 1000).toFixed(1)} s${detail}`,
        );
        if (result.problems.length > 0) {
          failures.push(`${scheme} ${page.path}${detail}`);
        }
      }
    };
    await Promise.all(Array.from({ length: Math.min(jobs, work.length) }, worker));
  } finally {
    await browser.close();
    server.close();
    rmSync(profile, { recursive: true, force: true, maxRetries: 5, retryDelay: 200 });
  }

  for (const scheme of schemes) {
    if (sourceTotal === 0 || totals[scheme] !== sourceTotal) {
      failures.push(`${scheme}: ${totals[scheme]} containers on the site, ${sourceTotal} mermaid blocks in the source`);
    }
  }
  const seconds = ((performance.now() - started) / 1000).toFixed(1);
  console.log(
    `${pages.length} pages x ${schemes.length} schemes in ${seconds} s; containers light ${totals.light}, ` +
      `dark ${totals.dark}, source ${sourceTotal}; ${failures.length} failures`,
  );
  if (failures.length > 0) {
    console.error(failures.map((failure) => `- ${failure}`).join('\n'));
    process.exitCode = 1;
  }
}

await main();

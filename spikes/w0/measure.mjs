// Measures the five candidates of W0 (Issue #94): size, dependencies, install and build time, a byte-identical rebuild, and lines of code.
// Usage: node measure.mjs <path to npm-cli.js> [rounds] [candidates...]
import { execFileSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { existsSync, readdirSync, readFileSync, rmSync, statSync } from 'node:fs';
import { join, relative } from 'node:path';
import { brotliCompressSync, constants, gzipSync } from 'node:zlib';

const [npmCli, rounds = '3', ...only] = process.argv.slice(2);
const names = only.length ? only : ['preact', 'preact-lean', 'solid', 'svelte', 'vue', 'vanilla'];
const shared = new Set(['api.ts', 'messages.ts', 'styles.css', 'app.test.ts']);

const files = (dir) =>
  readdirSync(dir, { withFileTypes: true }).flatMap((e) =>
    e.isDirectory() ? files(join(dir, e.name)) : [join(dir, e.name)],
  );

function timed(dir, ...args) {
  const start = process.hrtime.bigint();
  execFileSync(process.execPath, [npmCli, ...args], { cwd: dir, stdio: 'ignore' });
  return Number(process.hrtime.bigint() - start) / 1e9;
}

function distHash(dir) {
  const hash = createHash('sha256');
  for (const f of files(join(dir, 'dist')).sort()) hash.update(relative(dir, f)).update(readFileSync(f));
  return hash.digest('hex').slice(0, 16);
}

const results = Object.fromEntries(names.map((n) => [n, { ci: [], build: [], hashes: new Set() }]));
for (let r = 0; r < Number(rounds); r++) {
  for (const n of r % 2 ? [...names].reverse() : names) {
    const dir = join(import.meta.dirname, n);
    rmSync(join(dir, 'node_modules'), { recursive: true, force: true });
    rmSync(join(dir, 'dist'), { recursive: true, force: true });
    results[n].ci.push(timed(dir, 'ci', '--no-audit', '--no-fund'));
    results[n].build.push(timed(dir, 'run', 'build'));
    results[n].hashes.add(distHash(dir));
  }
}

for (const n of names) {
  const dir = join(import.meta.dirname, n);
  const lock = JSON.parse(readFileSync(join(dir, 'package-lock.json'), 'utf8')).packages;
  const pkg = JSON.parse(readFileSync(join(dir, 'package.json'), 'utf8'));
  const entries = Object.entries(lock).filter(([k]) => k !== '');
  let raw = 0, gz = 0, br = 0;
  for (const f of files(join(dir, 'dist'))) {
    const b = readFileSync(f);
    raw += b.length;
    gz += gzipSync(b, { level: 9 }).length;
    br += brotliCompressSync(b, { params: { [constants.BROTLI_PARAM_QUALITY]: 11 } }).length;
  }
  const loc = files(join(dir, 'src'))
    .filter((f) => !shared.has(f.split(/[\\/]/).at(-1)))
    .reduce((sum, f) => sum + readFileSync(f, 'utf8').split('\n').filter((l) => l.trim()).length, 0);
  const nodeModules = files(join(dir, 'node_modules')).reduce((s, f) => s + statSync(f).size, 0);
  const span = (a) => `${Math.min(...a).toFixed(1)}–${Math.max(...a).toFixed(1)}`;
  console.log(JSON.stringify({
    name: n,
    runtimeDirect: Object.keys(pkg.dependencies ?? {}).length,
    devDirect: Object.keys(pkg.devDependencies ?? {}).length,
    runtimeAll: entries.filter(([, v]) => !v.dev).length,
    all: entries.length,
    installed: entries.filter(([k]) => existsSync(join(dir, k))).length,
    distRaw: raw, distGzip: gz, distBrotli: br,
    ciSeconds: span(results[n].ci), buildSeconds: span(results[n].build),
    identicalRebuilds: results[n].hashes.size === 1, hashes: [...results[n].hashes],
    linesOfCode: loc, nodeModulesMB: (nodeModules / 1e6).toFixed(1),
  }));
}

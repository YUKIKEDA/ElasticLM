// Compares npm and pnpm on preact-lean (Issue #94): install time, installed packages, and the dist hash of three clean builds each.
// Usage: node pm-compare.mjs <path to npm-cli.js> <path to pnpm executable> <scratch directory>
import { execFileSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { cpSync, existsSync, readdirSync, readFileSync, rmSync } from 'node:fs';
import { join, relative } from 'node:path';

const [npmCli, pnpm, scratch] = process.argv.slice(2);
const npmDir = join(import.meta.dirname, 'preact-lean');
// A short name: pnpm's nested store paths reach the 260-character limit of Windows without long paths under a deep scratch directory.
const pnpmDir = join(scratch, 'pn');
rmSync(pnpmDir, { recursive: true, force: true });
cpSync(npmDir, pnpmDir, { recursive: true, filter: (p) => !/[\\/](node_modules|dist|package-lock\.json)$/.test(p) });

const files = (dir) =>
  readdirSync(dir, { withFileTypes: true }).flatMap((e) => (e.isDirectory() ? files(join(dir, e.name)) : [join(dir, e.name)]));
const distHash = (dir) => {
  const hash = createHash('sha256');
  for (const f of files(join(dir, 'dist')).sort()) hash.update(relative(dir, f)).update(readFileSync(f));
  return hash.digest('hex').slice(0, 16);
};
const managers = {
  npm: { dir: npmDir, run: (args) => [process.execPath, [npmCli, ...args]], install: ['ci', '--no-audit', '--no-fund'] },
  pnpm: { dir: pnpmDir, run: (args) => [pnpm, args], install: ['install', '--frozen-lockfile'] },
};
const results = { npm: { install: [], hashes: new Set() }, pnpm: { install: [], hashes: new Set() } };
for (let r = 0; r < 3; r++) {
  for (const name of r % 2 ? ['pnpm', 'npm'] : ['npm', 'pnpm']) {
    const m = managers[name];
    rmSync(join(m.dir, 'node_modules'), { recursive: true, force: true });
    rmSync(join(m.dir, 'dist'), { recursive: true, force: true });
    const start = process.hrtime.bigint();
    execFileSync(...m.run(m.install), { cwd: m.dir, stdio: 'ignore' });
    results[name].install.push(Number(process.hrtime.bigint() - start) / 1e9);
    execFileSync(...m.run(['run', 'build']), { cwd: m.dir, stdio: 'ignore' });
    results[name].hashes.add(distHash(m.dir));
  }
}
for (const [name, m] of Object.entries(managers)) {
  execFileSync(...m.run(['test']), { cwd: m.dir, stdio: 'ignore' });
  const installed = name === 'npm'
    ? Object.keys(JSON.parse(readFileSync(join(m.dir, 'package-lock.json'), 'utf8')).packages).filter((k) => k && existsSync(join(m.dir, k))).length
    : readdirSync(join(m.dir, 'node_modules', '.pnpm')).filter((e) => e.includes('@')).length;
  console.log(JSON.stringify({
    name,
    version: execFileSync(...m.run(['--version']), { cwd: m.dir }).toString().trim(),
    installSeconds: results[name].install.map((x) => x.toFixed(1)),
    hashes: [...results[name].hashes],
    installed,
    testPassed: true,
  }));
}
rmSync(pnpmDir, { recursive: true, force: true });

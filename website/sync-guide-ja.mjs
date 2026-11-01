// design §20.7 (V1c1, decision 0.117): the Japanese pages live in docs/guide.ja/, beside the English ones, so they read
// well on GitHub. Docusaurus reads a locale's docs only from i18n/<locale>/docusaurus-plugin-content-docs/current/
// (getPluginI18nPath in @docusaurus/utils), so this copies them there before every build. The copy is not kept in git.
import { cpSync, existsSync, rmSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const site = dirname(fileURLToPath(import.meta.url));
const source = join(site, '..', 'docs', 'guide.ja');
const target = join(site, 'i18n', 'ja', 'docusaurus-plugin-content-docs', 'current');

if (!existsSync(source)) {
  throw new Error(`${source} does not exist.`);
}

rmSync(target, { recursive: true, force: true });
cpSync(source, target, { recursive: true });

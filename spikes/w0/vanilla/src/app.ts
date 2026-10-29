import { api, type License, type Page } from './api';
import { m } from './messages';

type Child = Node | string | null | false | undefined;

function el<K extends keyof HTMLElementTagNameMap>(tag: K, attrs: Record<string, string> = {}, ...children: Child[]) {
  const node = document.createElement(tag);
  for (const [name, value] of Object.entries(attrs)) node.setAttribute(name, value);
  for (const child of children) if (child) node.append(child);
  return node;
}

function on<T extends HTMLElement>(node: T, event: string, handler: (e: Event) => void) {
  node.addEventListener(event, handler);
  return node;
}

const errorBanner = (e: Error) => el('p', { class: 'error', role: 'alert' }, e.message);

export function start(target: HTMLElement) {
  const view = el('div');
  target.replaceChildren(el('main', {}, el('h1', {}, m.title), view));
  let generation = 0;
  const route = () => {
    const current = ++generation;
    const live = () => current === generation;
    const match = /^#\/licenses\/(.+)$/.exec(location.hash);
    if (match) showDetail(view, decodeURIComponent(match[1]), live);
    else showList(view, live);
  };
  addEventListener('hashchange', route);
  route();
}

function showList(view: HTMLElement, live: () => boolean) {
  const input = el('input', { 'aria-label': m.search });
  const body = el('div');
  const load = (q: string, after: string | null) => {
    body.replaceChildren(el('p', {}, m.loading));
    api.list(q, after).then(
      (page) => live() && body.replaceChildren(...renderPage(page, (next) => load(q, next))),
      (e) => live() && body.replaceChildren(errorBanner(e)),
    );
  };
  const form = on(el('form', {}, input, el('button', {}, m.search)), 'submit', (e) => {
    e.preventDefault();
    load(input.value, null);
  });
  view.replaceChildren(el('section', {}, form, body));
  load('', null);
}

function renderPage(page: Page, next: (after: string) => void): Node[] {
  if (page.items.length === 0) return [el('p', {}, m.none)];
  const rows = page.items.map((l) =>
    el('tr', {},
      el('td', {}, el('a', { href: `#/licenses/${encodeURIComponent(l.id)}` }, l.name)),
      el('td', {}, l.state),
      el('td', {}, l.expiresAt ?? m.never)),
  );
  const table = el('table', {},
    el('thead', {}, el('tr', {}, el('th', {}, m.name), el('th', {}, m.state), el('th', {}, m.expires))),
    el('tbody', {}, ...rows));
  return page.next ? [table, on(el('button', {}, m.next), 'click', () => next(page.next!))] : [table];
}

function showDetail(view: HTMLElement, id: string, live: () => boolean) {
  const render = (license: License | null, error: Error | null, asking: boolean) => {
    const actions: Child[] = [];
    if (license && license.state !== 'revoked') {
      actions.push(asking
        ? el('p', {}, m.confirmRevoke(license.name) + ' ',
            on(el('button', {}, m.revoke), 'click', () => revoke(license)),
            on(el('button', {}, m.cancel), 'click', () => render(license, null, false)))
        : on(el('button', {}, m.revoke), 'click', () => render(license, null, true)));
    }
    view.replaceChildren(el('section', {},
      el('a', { href: '#/' }, m.back),
      error && errorBanner(error),
      !license && !error && el('p', {}, m.loading),
      license && el('dl', {},
        el('dt', {}, m.name), el('dd', {}, license.name),
        el('dt', {}, m.state), el('dd', {}, license.state),
        el('dt', {}, m.expires), el('dd', {}, license.expiresAt ?? m.never)),
      ...actions,
    ));
  };
  const load = () => api.get(id).then((l) => live() && render(l, null, false), (e) => live() && render(null, e, false));
  const revoke = (license: License) =>
    api.revoke(id).then(load, (e) => live() && render(license, e, false));
  render(null, null, false);
  load();
}

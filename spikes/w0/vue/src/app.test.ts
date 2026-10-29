import { expect, it, vi } from 'vitest';

it('lists the licenses the API returns', async () => {
  globalThis.fetch = vi.fn(async () =>
    new Response(JSON.stringify({ items: [{ id: 'l1', name: 'Acme', state: 'active', expiresAt: null }], next: null }), { status: 200, headers: { 'content-type': 'application/json' } }),
  ) as typeof fetch;
  document.body.innerHTML = '<div id="app"></div>';
  const { createApp } = await import('vue');
  const { default: App } = await import('./App.vue');
  createApp(App).mount('#app');

  await vi.waitFor(() => expect(document.body.textContent).toContain('Acme'));
});

<script setup lang="ts">
import { ref, watchEffect } from 'vue';
import { api, type Page } from './api';
import { m } from './messages';

const text = ref('');
const query = ref<{ q: string; after: string | null }>({ q: '', after: null });
const page = ref<Page | null>(null);
const error = ref<Error | null>(null);
watchEffect((onCleanup) => {
  const { q, after } = query.value;
  let live = true;
  onCleanup(() => (live = false));
  page.value = null;
  error.value = null;
  api.list(q, after).then((p) => { if (live) page.value = p; }, (e) => { if (live) error.value = e; });
});
</script>

<template>
  <section>
    <form @submit.prevent="query = { q: text, after: null }">
      <input :aria-label="m.search" v-model="text" />
      <button>{{ m.search }}</button>
    </form>
    <p v-if="error" class="error" role="alert">{{ error.message }}</p>
    <p v-else-if="!page">{{ m.loading }}</p>
    <p v-else-if="page.items.length === 0">{{ m.none }}</p>
    <template v-else>
      <table>
        <thead><tr><th>{{ m.name }}</th><th>{{ m.state }}</th><th>{{ m.expires }}</th></tr></thead>
        <tbody>
          <tr v-for="l in page.items" :key="l.id">
            <td><a :href="`#/licenses/${encodeURIComponent(l.id)}`">{{ l.name }}</a></td>
            <td>{{ l.state }}</td>
            <td>{{ l.expiresAt ?? m.never }}</td>
          </tr>
        </tbody>
      </table>
      <button v-if="page.next" @click="query = { q: query.q, after: page.next }">{{ m.next }}</button>
    </template>
  </section>
</template>

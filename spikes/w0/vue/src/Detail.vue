<script setup lang="ts">
import { ref, watchEffect } from 'vue';
import { api, type License } from './api';
import { m } from './messages';

const props = defineProps<{ id: string }>();
const license = ref<License | null>(null);
const error = ref<Error | null>(null);
const asking = ref(false);
const version = ref(0);
watchEffect((onCleanup) => {
  version.value;
  let live = true;
  onCleanup(() => (live = false));
  api.get(props.id).then((l) => { if (live) license.value = l; }, (e) => { if (live) error.value = e; });
});
async function revoke() {
  asking.value = false;
  try {
    await api.revoke(props.id);
    version.value++;
  } catch (e) {
    error.value = e as Error;
  }
}
</script>

<template>
  <section>
    <a href="#/">{{ m.back }}</a>
    <p v-if="error" class="error" role="alert">{{ error.message }}</p>
    <p v-if="!license && !error">{{ m.loading }}</p>
    <template v-if="license">
      <dl>
        <dt>{{ m.name }}</dt><dd>{{ license.name }}</dd>
        <dt>{{ m.state }}</dt><dd>{{ license.state }}</dd>
        <dt>{{ m.expires }}</dt><dd>{{ license.expiresAt ?? m.never }}</dd>
      </dl>
      <template v-if="license.state !== 'revoked'">
        <p v-if="asking">
          {{ m.confirmRevoke(license.name) }} <button @click="revoke">{{ m.revoke }}</button>
          <button @click="asking = false">{{ m.cancel }}</button>
        </p>
        <button v-else @click="asking = true">{{ m.revoke }}</button>
      </template>
    </template>
  </section>
</template>
